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

## API Reference

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

## Project Structure

```
ProcessStreamEngine/
├── ProcessStreamEngine.cs          # Main library (single public class)
├── ProcessStreamEngine.csproj  # .NET 9 class library
└── README.md
```

## Related Tests

See `ProcessStreamEngine.Tests` in the parent solution for integration tests covering ping data capture, nslookup interaction, prompt handling, conditional flows, and error cases.
