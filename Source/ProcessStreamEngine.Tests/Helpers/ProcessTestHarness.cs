using System.Diagnostics;
using FakeItEasy;

namespace ProcessStreamEngine.Tests.Helpers;

internal static class ProcessTestHarness
{
	public static ProcessStartInfo CreateRedirectedStartInfo(IEnumerable<string> arguments, bool redirectInput = false)
	{
		var startInfo = ProcessStreamEngine.CreateStartInfo(ShellCommand.Executable, arguments);
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.RedirectStandardInput = redirectInput;
		return startInfo;
	}

	public static Process StartProcess(IEnumerable<string> arguments, bool redirectInput = false)
	{
		var startInfo = CreateRedirectedStartInfo(arguments, redirectInput);
		var process = new Process { StartInfo = startInfo };
		process.Start();
		return process;
	}

	public static ProcessStreamEngine CreateEngineForRunningProcess(IEnumerable<string> arguments, bool redirectInput = false)
	{
		var process = StartProcess(arguments, redirectInput);
		return new ProcessStreamEngine(process);
	}

	public static ProcessStreamEngine CreateDeferredEngine(IEnumerable<string> arguments, bool redirectInput = false)
	{
		var startInfo = CreateRedirectedStartInfo(arguments, redirectInput);
		return ProcessStreamEngine.CreateDeferred(startInfo);
	}

	public static async Task<ProcessStreamEngine> RunToCompletionAsync(
		IEnumerable<string> arguments,
		Action<ProcessStreamEngine>? configure = null,
		int timeoutMs = 10000)
	{
		var engine = CreateDeferredEngine(arguments);
		configure?.Invoke(engine);
		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync(timeoutMs: timeoutMs);
		return engine;
	}

	public static Action<string, bool> CreateFakeConsoleHandler(out List<(string Line, bool IsError)> CapturedLines)
	{
		var captured = new List<(string Line, bool IsError)>();
		CapturedLines = captured;
		var handler = A.Fake<Action<string, bool>>();
		A.CallTo(() => handler(A<string>._, A<bool>._))
			.Invokes((string line, bool isError) => captured.Add((line, isError)));
		return handler;
	}
}
