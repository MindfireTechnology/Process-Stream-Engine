# ProcessStreamEngine 2.0

AOT-friendly .NET library for monitoring external process output and driving interactive CLI workflows. Version 2.0 centers on a **`ProcessSession`**: one reader per pipe, fan-out to subscribers via bounded channels, and adapters for `IAsyncEnumerable`, `IObservable`, regex events, and buffered text.

## Features

- **ProcessSession** — owns the process lifecycle (`IAsyncDisposable`), kills on cancel by default, and drains pipes before returning.
- **Output hub** — each subscriber gets an isolated bounded `Channel<OutputLine>` with `Wait` or `DropOldest` backpressure.
- **`IAsyncEnumerable`** — `session.Output.ReadAllAsync()` and `ProcessRunner.ReadLinesAsync()`.
- **`IObservable`** — `session.Output.Subscribe(observer)` without System.Reactive.
- **Pattern matching** — register `OnMatch` / `Capture` on the builder before start; use `[GeneratedRegex]` for AOT.
- **Expect** — `await session.ExpectAsync(regex)` (partial prompts without trailing newline via quiet-period tail emission).
- **Framing** — line mode (CR/LF/CRLF) or NUL mode (`git -z`).
- **Transcript** — opt-in exact decoded stdout/stderr via `.KeepTranscript()`.
- **One-shot runs** — `ProcessRunner.RunTextAsync`, `RunTextOrThrowAsync`, `RunWithStreamsAsync`.
- **No reflection, no Instances** — `ProcessStartInfo` or an already-started `Process` only.

## Requirements

- .NET 8.0, 9.0, or 10.0
- Marked `<IsAotCompatible>true</IsAotCompatible>` for trim/AOT analyzers

## Quick start

### Session with streaming

```csharp
using ProcessStreamEngine;
using ProcessStreamEngine.Output;

var startInfo = ProcessRunner.CreateStartInfo("git", ["status", "--short"]);

await using var session = ProcessSessionBuilder.Create(startInfo)
	.Capture("branch", new Regex(@"On branch (.+)"), captureGroup: "1")
	.KeepTranscript()
	.Start();

await foreach (var line in session.Output.ReadAllAsync())
	Console.WriteLine($"[{line.Stream}] {line.Text}");

var result = await session.WaitForExitAsync();
```

### Interactive input

```csharp
await using var session = ProcessSessionBuilder.Create(startInfo)
	.Start(); // RedirectStandardInput = true on startInfo when needed

await session.ExpectAsync(new Regex(@">\s*$"));
await session.Input.WriteLineAsync("server 8.8.8.8");
```

### One-shot text

```csharp
var result = await ProcessRunner.RunTextAsync(startInfo);
// result.ExitCode, result.StandardOutput, result.StandardError

var ok = await ProcessRunner.RunTextOrThrowAsync(startInfo); // throws ProcessExitException when non-zero
```

### Raw byte passthrough

```csharp
await ProcessRunner.RunWithStreamsAsync(
	startInfo,
	new ProcessStreamOptions
	{
		StandardInput = inputStream,
		StandardOutput = outputStream,
		MonitorStderrAsLines = true
	});
```

## Core types

| Type | Role |
|------|------|
| `ProcessSessionBuilder` | Configure framing, handlers, transcript; call `Start()`. |
| `ProcessSession` | Running process + `Output` / `Input` + `WaitForExitAsync`. |
| `ProcessOutput` | `ReadAllAsync`, `Subscribe`, `GetCapturedData`, `ExpectAsync`. |
| `OutputLine` | `Text`, `OutputStream`, `Sequence`, `IsComplete`. |
| `ProcessRunner` | Static one-shots, `CreateStartInfo`, `ApplyEnvironment`. |

## Migration from 1.x

| 1.x | 2.0 |
|-----|-----|
| `ProcessStreamEngine` class | `ProcessSession` via `ProcessSessionBuilder` |
| `CreateDeferred` / `StartMonitoringAsync` | `ProcessSessionBuilder.Create(...).Start()` |
| `RegisterEvent` / `RegisterDataCapture` | Builder `.OnMatch` / `.Capture` (pass `Regex` or string) |
| `WaitForEventAsync` | `session.ExpectAsync(regex)` or `.OnMatch` |
| `RunTextAsync` (static on class) | `ProcessRunner.RunTextAsync` |
| `StdoutText` / `StdoutLines` | `.KeepTranscript()` then `WaitForExitAsync` → `ProcessResult.StandardOutput` or `await foreach` |
| `Instances` / `IProcessInstance` | Removed — use `ProcessStartInfo` |
| `ProcessStreamEngineException` (non-zero exit) | `ProcessExitException` |
| `SendInputAsync` | `session.Input.WriteLineAsync` / `WriteAsync` |
| net6.0 / net7.0 | net8.0+ only |

Handlers should use `Regex` instances (including source-generated regex) instead of `RegexOptions.Compiled`.

## Testing

```bash
dotnet test Source/ProcessStreamEngine.Tests/ProcessStreamEngine.Tests.csproj
```

`Source/ProcessStreamEngine.AotSmoke` validates native publish:

```bash
dotnet publish Source/ProcessStreamEngine.AotSmoke/ProcessStreamEngine.AotSmoke.csproj -r win-x64 -c Release
```

## License

MIT — see [LICENSE](LICENSE).
