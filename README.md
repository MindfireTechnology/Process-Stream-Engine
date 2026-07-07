# ProcessStreamEngine

A lightweight .NET library for monitoring external process output and driving interactive command-line workflows. `ProcessStreamEngine` watches stdout and stderr, fires handlers when patterns match, captures structured data from output, and supports sending input to running processes.

Built for scenarios where you need more than `Process.StandardOutput.ReadToEnd()` — parsing ping results, automating `nslookup`, waiting for CLI prompts, or reacting to specific log lines as they appear.

## Features

- **Stream monitoring** — Reads stdout and stderr line-by-line on background tasks.
- **Regex events** — Register handlers that run when output matches a pattern.
- **String events** — Simple case-sensitive substring matching without writing regex.
- **Data capture** — Store matched values (full match, numbered group, or named group) for later retrieval.
- **Async waiting** — Block until a specific pattern appears, with configurable timeout.
- **Interactive CLI support** — Send input, wait for prompts, and evaluate captured data before sending the next command.
- **Conditional flows** — Wait for the first of several patterns and run the corresponding action.
- **Console streaming** — Mirror output to the console or a custom handler.
- **Buffered output** — Access full stdout/stderr as ordered line lists or joined text after monitoring.
- **One-shot text runs** — `RunTextAsync` / `RunTextOrThrowAsync` run a process and return captured output without manual lifecycle management.
- **Raw stream passthrough** — `RunWithStreamsAsync` pipes byte streams to/from stdin, stdout, and stderr for piping and proxy scenarios.
- **Raw stdin** — `SendRawInputAsync` writes bytes to stdin without appending a newline.
- **Process helpers** — `CreateStartInfo` and `ApplyEnvironment` simplify `ProcessStartInfo` setup with argument lists and env vars.
- **Two integration modes** — Works with `System.Diagnostics.Process` directly, or with the [Instances](https://www.nuget.org/packages/Instances) library for a simpler process-start API.

## Requirements

- .NET 9.0 or later
- [Instances](https://www.nuget.org/packages/Instances) 3.0.2 (included as a package reference; optional if you construct `ProcessStreamEngine` from a `Process` you manage yourself)

## Installation

Add the project to your solution:

```xml
<ItemGroup>
  <ProjectReference Include="path\to\ProcessStreamEngine\ProcessStreamEngine.csproj" />
</ItemGroup>
```

Or copy `ProcessStreamEngine.cs` into your project and add the Instances package:

```xml
<PackageReference Include="Instances" Version="3.0.2" />
```

## Quick Start

### Using the Instances library (recommended)

```csharp
using Instances;
using ProcessStreamEngine;

using var instance = Instance.Start("ping", "-n 4 8.8.8.8");
using var engine = new ProcessStreamEngine(instance);

engine.RegisterDataCapture("ttl", @"TTL=(\d+)", captureGroup: "1");

await engine.StartMonitoringAsync();
await engine.WaitForExitAsync();

var ttls = engine.GetCapturedData("ttl").ToList();
```

### Using System.Diagnostics.Process directly

When constructing from a `Process`, stdout and stderr redirection must be enabled before starting the process:

```csharp
using System.Diagnostics;
using ProcessStreamEngine;

var process = new Process
{
	StartInfo = new ProcessStartInfo
	{
		FileName = "my-tool",
		Arguments = "--verbose",
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		RedirectStandardInput = true,   // Required for SendInputAsync
		UseShellExecute = false,
		CreateNoWindow = true
	}
};

process.Start();

using var engine = new ProcessStreamEngine(process);
await engine.StartMonitoringAsync();
// ... work with output ...
await engine.WaitForExitAsync();
```

### One-shot text capture (no engine lifecycle)

For simple "run command, get output" scenarios, use the static helpers:

```csharp
var startInfo = ProcessStreamEngine.CreateStartInfo("git", ["status", "--short"]);

var result = await ProcessStreamEngine.RunTextAsync(startInfo);
Console.WriteLine($"Exit: {result.ExitCode}");
Console.WriteLine(result.StandardOutput);

// Throws ProcessStreamEngineException (includes ExitCode and StandardError) on non-zero exit:
var ok = await ProcessStreamEngine.RunTextOrThrowAsync(startInfo);
```

`RunTextAsync` does not throw on non-zero exit codes. `RunTextOrThrowAsync` throws `ProcessStreamEngineException` with the exit code and stderr text when the process fails.

### Raw stream passthrough

`RunWithStreamsAsync` copies bytes between your streams and the process — useful for piping, proxies, or forwarding output to a file or network stream:

```csharp
await using var input = File.OpenRead("commands.txt");
await using var output = File.Create("result.txt");

var startInfo = new ProcessStartInfo("my-tool", "--batch")
{
	WorkingDirectory = @"C:\tools"
};

ProcessStreamEngine.ApplyEnvironment(startInfo, new Dictionary<string, string?>
{
	["MY_VAR"] = "value",
	["OLD_VAR"] = null  // removes the variable
});

var exitCode = await ProcessStreamEngine.RunWithStreamsAsync(
	startInfo,
	new ProcessStreamOptions
	{
		StandardInput = input,
		StandardOutput = output,
		MonitorStderrAsLines = true  // collect stderr; throws on non-zero exit
	});
```

| `ProcessStreamOptions` | Description |
|---|---|
| `StandardInput` | Bytes copied to the process stdin (enables `RedirectStandardInput`). |
| `StandardOutput` | Process stdout copied here (enables `RedirectStandardOutput`). |
| `StandardError` | Process stderr copied here (enables `RedirectStandardError`). |
| `MonitorStderrAsLines` | Line-buffer stderr instead of copying to a stream. On non-zero exit, throws `ProcessStreamEngineException` with joined stderr. Mutually exclusive with `StandardError`. |
| `LeaveStreamsOpen` | When false (default), provided streams are disposed after the run. |

Only streams you supply are redirected. Omitted streams are not read or written.

## Core Concepts

### Lifecycle

A typical session follows this order:

1. Create a `ProcessStreamEngine` (or use `CreateDeferred` / `CreateDeferredFromInstance` to start the process later).
2. Register event patterns, data capture patterns, and optional console handlers.
3. Call `StartMonitoringAsync()` to begin reading output (and start the process, if deferred).
4. Optionally send input, wait for events, or read captured data while the process runs.
5. Call `WaitForExitAsync()` when the process should finish.
6. Dispose the engine (or use `using`) to stop monitoring and release resources.

`ProcessStreamEngine` does **not** own the underlying process. Disposing the engine stops monitoring but does not kill or dispose the process.

**Fast-exiting processes.** If a process exits before or during `StartMonitoringAsync()`, the engine drains any remaining stdout/stderr and runs it through the same handlers — no exception is thrown. `WaitForExitAsync()` returns the exit code immediately.

**Deferred start (recommended for new code).** Use `CreateDeferred` or `CreateDeferredFromInstance` so handlers are registered before the process starts, avoiding missed early output:

```csharp
using var engine = ProcessStreamEngine.CreateDeferredFromInstance("ping", "-n 1 127.0.0.1");
engine.RegisterDataCapture("ttl", @"TTL=(\d+)", captureGroup: "1");
await engine.StartMonitoringAsync();  // starts process and begins monitoring
await engine.WaitForExitAsync();
```

For `System.Diagnostics.Process`:

```csharp
var startInfo = new ProcessStartInfo("cmd.exe", "/c echo hello")
{
	RedirectStandardOutput = true,
	RedirectStandardError = true,
	UseShellExecute = false,
	CreateNoWindow = true
};

using var engine = ProcessStreamEngine.CreateDeferred(startInfo);
engine.RegisterStringEvent("done", "hello", () => { });
await engine.StartMonitoringAsync();
await engine.WaitForExitAsync();
```

### Events vs. Data Capture

| Capability | Purpose | API |
|---|---|---|
| **Events** | Run code immediately when a line matches | `RegisterEvent`, `RegisterStringEvent`, `WaitForEventAsync` |
| **Data capture** | Collect matched values into a named store | `RegisterDataCapture`, `GetCapturedData` |

Events are for side effects (logging, state changes, triggering logic). Data capture is for collecting values you will read later.

Both support matching on stdout (default) or stderr via `matchOnErrorStream: true`.

### Buffered Output

While monitoring, every line read from stdout and stderr is stored in order. After `StartMonitoringAsync()` (and once output has been processed), you can read the full transcript without re-parsing captured data:

```csharp
await engine.StartMonitoringAsync();
await engine.WaitForExitAsync();

var lines = engine.StdoutLines;           // IReadOnlyList<string>, in order
var text = engine.StdoutText;             // lines joined with Environment.NewLine
var errors = engine.StderrLines;
var errorText = engine.StderrText;
```

Buffered output is cleared when `StartMonitoringAsync()` is called again on the same engine instance.

### Waiting for Output

```csharp
engine.RegisterEvent("ready", @"Ready>", match => { });

await engine.StartMonitoringAsync();

// Blocks until "Ready>" appears or timeout (default 30 seconds)
var match = await engine.WaitForEventAsync("ready", timeoutMs: 10000);
```

For simple substring matching:

```csharp
engine.RegisterStringEvent("started", "Service started", () =>
{
	Console.WriteLine("Process reported it is ready.");
});
```

## Examples

### Capture multiple values from ping output

```csharp
using var instance = Instance.Start("ping", "-n 4 8.8.8.8");
using var engine = new ProcessStreamEngine(instance);

engine.RegisterDataCapture("ttl", @"TTL=(\d+)", captureGroup: "1");
engine.RegisterDataCapture("time", @"time=(\d+)ms", captureGroup: "1");

await engine.StartMonitoringAsync();
await engine.WaitForExitAsync();

foreach (var ttl in engine.GetCapturedData("ttl"))
	Console.WriteLine($"TTL: {ttl}");
```

### React to output with an event handler

```csharp
var replyCount = 0;

engine.RegisterEvent("ping_reply", @"Reply from", match =>
{
	replyCount++;
});

await engine.StartMonitoringAsync();
await engine.WaitForExitAsync();
```

### Stream output to the console or a custom handler

```csharp
engine.StreamToConsole = true;

// Or provide your own handler (stdout vs stderr):
engine.ConsoleStreamHandler = (line, isErrorStream) =>
{
	var prefix = isErrorStream ? "ERR" : "OUT";
	Console.WriteLine($"[{prefix}] {line}");
};
```

### Interactive commands (nslookup-style prompts)

For CLI tools that show a `>` prompt and accept stdin:

```csharp
using var instance = Instance.Start("nslookup");
using var engine = new ProcessStreamEngine(instance);

engine.RegisterDataCapture("nameserver", @"nameserver\s*=\s*(\S+)", captureGroup: "1");

await engine.StartMonitoringAsync();

await engine.WaitForPromptAsync(@">\s*$");
await engine.SendInputAsync("server 8.8.8.8");

await engine.WaitForPromptAsync();
await engine.SendInputAsync("set type=NS");

await engine.WaitForPromptAsync();
await engine.SendInputAsync("google.com");

await Task.Delay(1000); // Allow output to arrive

var nameServers = engine.GetCapturedData("nameserver").ToList();

await engine.SendInputAsync("exit");
await engine.WaitForExitAsync();
```

### Prompt-driven evaluation loop

`WaitForPromptThenEvaluateAsync` waits for a prompt, runs your callback to decide the next command, sends it, and repeats until the callback returns `null` or an empty string:

```csharp
await engine.WaitForPromptThenEvaluateAsync(engine =>
{
	var servers = engine.GetCapturedData("nameserver").ToList();

	if (servers.Count == 0)
		return "google.com";

	return null; // stop the loop
});
```

### Conditional branching on output

Wait for whichever pattern appears first and optionally run an action:

```csharp
var (match, patternName) = await engine.WaitForAnyConditionAsync(
[
	new ProcessStreamEngine.ConditionalAction
	{
		PatternName = "success",
		Pattern = @"name server = (\S+)",
		Action = async (m, eng) => await eng.SendInputAsync($"server {m.Groups[1].Value}")
	},
	new ProcessStreamEngine.ConditionalAction
	{
		PatternName = "failure",
		Pattern = @"can't find",
		Action = async (m, eng) => await eng.SendInputAsync("exit")
	}
]);
```

### Read full output after monitoring

```csharp
using var engine = ProcessStreamEngine.CreateDeferredFromInstance("ping", "-n 2 127.0.0.1");

await engine.StartMonitoringAsync();
var exitCode = await engine.WaitForExitAsync();

if (exitCode != 0)
	Console.Error.WriteLine(engine.StderrText);

foreach (var line in engine.StdoutLines)
	Console.WriteLine(line);
```

### Send raw bytes to stdin

When a tool expects binary or partial input (no trailing newline), use `SendRawInputAsync` instead of `SendInputAsync`:

```csharp
await engine.SendRawInputAsync(Encoding.UTF8.GetBytes("partial"));
await engine.SendRawInputAsync("\n"u8.ToArray());  // newline only when you want it
```

## API Reference

### Types

| Type | Description |
|---|---|
| `ProcessTextResult` | Record: `ExitCode`, `StandardOutput`, `StandardError` — returned by `RunTextAsync` / `RunTextOrThrowAsync`. |
| `ProcessStreamOptions` | Options for `RunWithStreamsAsync`: optional stdin/stdout/stderr streams, `MonitorStderrAsLines`, `LeaveStreamsOpen`. |
| `ProcessStreamEngineException` | Thrown on non-zero exit when stderr is available (`RunTextOrThrowAsync`, `RunWithStreamsAsync` with `MonitorStderrAsLines`). Exposes `ExitCode` and `StandardError`. |

### Static Helpers

| Method | Description |
|---|---|
| `RunTextAsync(startInfo, cancellationToken?, timeoutMs?)` | Run process, return buffered stdout/stderr. Does not throw on non-zero exit. |
| `RunTextOrThrowAsync(startInfo, cancellationToken?, timeoutMs?)` | Same as `RunTextAsync`; throws `ProcessStreamEngineException` on non-zero exit. |
| `RunWithStreamsAsync(startInfo, options, cancellationToken?, timeoutMs?)` | Byte-stream passthrough; returns exit code. |
| `CreateStartInfo(fileName, arguments, workingDirectory?)` | `ProcessStartInfo` with `UseShellExecute = false` and `ArgumentList` populated. |
| `ApplyEnvironment(startInfo, variables)` | Set or remove environment variables (`null` value removes the key). |

### Construction

| Member | Description |
|---|---|
| `ProcessStreamEngine(Process process)` | Monitor a process with redirected stdout/stderr. |
| `ProcessStreamEngine(IProcessInstance processInstance)` | Monitor via the Instances library (uses reflection for stream access). Register handlers before `StartMonitoringAsync()`. |
| `CreateDeferred(ProcessStartInfo startInfo)` | Create an engine that starts the process when monitoring begins. |
| `CreateDeferredFromInstance(string fileName, string arguments?)` | Create an engine that calls `Instance.Start` when monitoring begins. |

### Registration

| Method | Description |
|---|---|
| `RegisterEvent(name, pattern, handler, matchOnErrorStream?)` | Invoke `handler(Match)` when regex matches a line. |
| `RegisterStringEvent(name, searchString, handler, matchOnErrorStream?)` | Invoke handler when substring is found. |
| `RegisterDataCapture(name, pattern, captureGroup?, matchOnErrorStream?)` | Store matched values under `name`. |

Multiple handlers can be registered under the same event name; all are invoked on match.

### Monitoring and Control

| Method | Description |
|---|---|
| `StartMonitoringAsync(cancellationToken?)` | Begin reading process output. Drains remaining output if the process has already exited. |
| `StopMonitoring()` | Cancel monitoring tasks. |
| `SendInputAsync(input, cancellationToken?)` | Write a line to stdin (appends newline if missing). |
| `SendRawInputAsync(data, cancellationToken?)` | Write raw bytes to stdin without appending a newline. |
| `WaitForExitAsync(cancellationToken?, timeoutMs?)` | Wait for process exit, stop monitoring, return exit code. |

### Waiting

| Method | Description |
|---|---|
| `WaitForEventAsync(patternName, cancellationToken?, timeoutMs?, matchOnErrorStream?)` | Await the next match for a registered event. |
| `WaitForPromptAsync(promptPattern?, cancellationToken?, timeoutMs?)` | Convenience wrapper for common CLI prompts (default: `>\s*$`). |
| `WaitForPromptThenEvaluateAsync(evaluator, ...)` | Prompt loop with sync or async command selection. |
| `WaitForAnyConditionAsync(conditions, cancellationToken?, defaultTimeoutMs?)` | Race multiple patterns; run the first matching action. |

### Data Retrieval

| Method | Description |
|---|---|
| `GetCapturedData(patternName, matchOnErrorStream?)` | Values captured for a named pattern. |
| `GetAllCapturedData()` | All captured values across all patterns and streams. |
| `ClearCapturedData()` | Clear all captured data. |
| `ClearCapturedData(patternName, matchOnErrorStream?)` | Clear one pattern's captured data. |

### Properties

| Member | Description |
|---|---|
| `StdoutLines` | Ordered stdout lines accumulated during line monitoring. |
| `StderrLines` | Ordered stderr lines accumulated during line monitoring. |
| `StdoutText` | `StdoutLines` joined with `Environment.NewLine`. |
| `StderrText` | `StderrLines` joined with `Environment.NewLine`. |
| `StreamToConsole` | When true, writes output lines to `Console` / `Console.Error`. |
| `ConsoleStreamHandler` | Custom `(line, isErrorStream) => void` handler; takes precedence over `StreamToConsole`. |

### Cleanup

| Method | Description |
|---|---|
| `Cleanup()` | Stop monitoring and dispose internal cancellation token. |
| `Dispose()` | Calls `Cleanup()`. Always dispose when finished. |

## Tips and Best Practices

**Register patterns before monitoring.** Event and data patterns should be registered before calling `StartMonitoringAsync()` so early output is not missed. Use `CreateDeferred` or `CreateDeferredFromInstance` to eliminate the start/monitor race entirely.

**Use `using` or `Dispose()`.** Monitoring runs on background tasks tied to a `CancellationTokenSource` that is released on dispose.

**Interactive processes need stdin redirected.** `SendInputAsync` requires `RedirectStandardInput = true` on the `ProcessStartInfo`, or an Instances instance that exposes `SendInput`.

**Prompt patterns vary by tool.** The default `>\s*$` suits tools like `nslookup`. Adjust the regex for your CLI's prompt format.

**Timeouts default to 30 seconds.** Pass `timeoutMs` to `WaitForEventAsync`, `WaitForPromptAsync`, and `WaitForExitAsync` for long-running operations.

**Capture groups.** Use `captureGroup: "1"` for the first group, a named group like `"ttl"`, or omit it to capture the full match.

**Thread safety.** Event handlers run on thread-pool threads via `Task.Run`. Keep handlers short or marshal back to your synchronization context if updating UI state.

**Choose the right API for your scenario.** Use `RunTextAsync` for fire-and-forget command output. Use `RunWithStreamsAsync` when you need byte-level piping. Use a `ProcessStreamEngine` instance when you need events, data capture, prompts, or interactive flows.

**`MonitorStderrAsLines` vs `StandardError`.** You cannot set both. Use line monitoring when you only need stderr for error reporting on failure; use a stream when you need to forward stderr elsewhere.

**Stream disposal in `RunWithStreamsAsync`.** By default, streams passed in `ProcessStreamOptions` are disposed when the run completes. Set `LeaveStreamsOpen = true` if the caller owns the stream lifetime.

## Project Structure

```
Process-Stream-Engine/
├── Source/
│   ├── ProcessStreamEngine.slnx
│   ├── ProcessStreamEngine/
│   │   ├── ProcessStreamEngine.cs      # Main library
│   │   └── ProcessStreamEngine.csproj
│   └── ProcessStreamEngine.Tests/      # Unit tests
└── README.md
```

## Related Tests

See `Source/ProcessStreamEngine.Tests` for unit tests covering buffered text execution, stream passthrough, event/capture handling, interactive input, and error cases.
