using System.Diagnostics;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class ExitDrainTests
{
	private const int LineCount = 500;

	[Fact]
	public async Task WaitForExitAsync_WithSlowHandler_DeliversEveryLineBeforeReturning()
	{
		// A slow handler keeps the readers behind the process, so it exits with most of its output still unread.
		var (stdout, stderr) = await RunAndCollectAsync(TimeSpan.FromMilliseconds(0.5));

		ShouldHaveEveryLine(stdout, "out");
		ShouldHaveEveryLine(stderr, "err");
	}

	[Fact]
	public async Task WaitForExitAsync_WithFastHandler_DeliversEveryLineBeforeReturning()
	{
		for (var run = 0; run < 20; run++)
		{
			var (stdout, stderr) = await RunAndCollectAsync(TimeSpan.Zero);

			ShouldHaveEveryLine(stdout, "out", $"run {run}");
			ShouldHaveEveryLine(stderr, "err", $"run {run}");
		}
	}

	[Fact]
	public async Task RunTextAsync_ReturnsEveryLine()
	{
		for (var run = 0; run < 10; run++)
		{
			var startInfo = ProcessStreamEngine.CreateStartInfo(ShellCommand.Executable, ShellCommand.EchoNumberedLines(LineCount));

			var result = await ProcessStreamEngine.RunTextAsync(startInfo);

			result.ExitCode.ShouldBe(0);
			ShouldHaveEveryLine(SplitLines(result.StandardOutput), "out", $"run {run}");
			ShouldHaveEveryLine(SplitLines(result.StandardError), "err", $"run {run}");
		}
	}

	[Fact]
	public async Task WaitForExitAsync_ThrowsTimeoutWhenProcessDoesNotExit()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.SleepForever());
		await engine.StartMonitoringAsync();
		var stopwatch = Stopwatch.StartNew();

		var exception = await Should.ThrowAsync<TimeoutException>(() => engine.WaitForExitAsync(timeoutMs: 500));

		exception.Message.ShouldContain("did not exit");
		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
	}

	private static async Task<(List<string> Stdout, List<string> Stderr)> RunAndCollectAsync(TimeSpan handlerDelay)
	{
		var stdout = new List<string>();
		var stderr = new List<string>();
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.EchoNumberedLines(LineCount));
		engine.ConsoleStreamHandler = (line, isError) =>
		{
			Spin(handlerDelay);
			var lines = isError ? stderr : stdout;
			lock (lines)
				lines.Add(line);
		};

		await engine.StartMonitoringAsync();
		var exitCode = await engine.WaitForExitAsync();

		// Snapshot immediately: anything that arrives after WaitForExitAsync returns counts as missing.
		List<string> stdoutSnapshot, stderrSnapshot;
		lock (stdout)
			stdoutSnapshot = stdout.ToList();
		lock (stderr)
			stderrSnapshot = stderr.ToList();
		var engineStdout = engine.StdoutLines.ToList();
		var engineStderr = engine.StderrLines.ToList();

		exitCode.ShouldBe(0);
		engineStdout.ShouldBe(stdoutSnapshot);
		engineStderr.ShouldBe(stderrSnapshot);
		return (stdoutSnapshot, stderrSnapshot);
	}

	private static void ShouldHaveEveryLine(List<string> actual, string prefix, string? context = null)
	{
		// Count first so a dropped tail fails with a short message rather than a 500-item diff.
		actual.Count.ShouldBe(LineCount, context);
		actual.ShouldBe(Enumerable.Range(1, LineCount).Select(i => $"{prefix}-{i}").ToList(), context);
	}

	private static List<string> SplitLines(string text) =>
		text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();

	private static void Spin(TimeSpan duration)
	{
		if (duration <= TimeSpan.Zero)
			return;

		var stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < duration)
			Thread.SpinWait(10);
	}
}
