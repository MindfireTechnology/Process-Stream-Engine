using System.Diagnostics;
using ProcessStreamEngine.Output;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class ExitDrainTests
{
	private const int LineCount = 500;

	[Fact]
	public async Task WaitForExitAsync_DeliversEveryLine()
	{
		for (var run = 0; run < 5; run++)
		{
			var lines = new List<OutputLine>();
			await using var session = ProcessTestHarness.StartSession(ShellCommand.EchoNumberedLines(LineCount));
			await foreach (var line in session.Output.ReadAllAsync())
				lines.Add(line);

			await session.WaitForExitAsync();
			ShouldHaveEveryLine(lines.Where(l => l.Stream == OutputStream.StandardOutput).Select(l => l.Text), "out", $"run {run}");
			ShouldHaveEveryLine(lines.Where(l => l.Stream == OutputStream.StandardError).Select(l => l.Text), "err", $"run {run}");
		}
	}

	[Fact]
	public async Task RunTextAsync_ReturnsEveryLine()
	{
		var startInfo = ProcessRunner.CreateStartInfo(ShellCommand.Executable, ShellCommand.EchoNumberedLines(LineCount));
		var result = await ProcessRunner.RunTextAsync(startInfo, timeoutMs: 60000);
		result.ExitCode.ShouldBe(0);
		ShouldHaveEveryLine(SplitLines(result.StandardOutput), "out");
		ShouldHaveEveryLine(SplitLines(result.StandardError), "err");
	}

	[Fact]
	public async Task WaitForExitAsync_ThrowsTimeoutWhenProcessDoesNotExit()
	{
		await using var session = ProcessTestHarness.StartSession(ShellCommand.SleepForever());
		var stopwatch = Stopwatch.StartNew();

		var exception = await Should.ThrowAsync<ProcessTimeoutException>(() =>
			session.WaitForExitAsync(timeoutMs: 500));

		exception.Phase.ShouldBe(ProcessTimeoutPhase.Exit);
		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
	}

	private static void ShouldHaveEveryLine(IEnumerable<string> lines, string prefix, string? context = null)
	{
		var list = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
		list.Count.ShouldBe(LineCount, context);
		for (var i = 1; i <= LineCount; i++)
			list.ShouldContain($"{prefix}-{i}", context);
	}

	private static List<string> SplitLines(string text) =>
		text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
}
