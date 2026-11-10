using System.Text;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Runner;

public class RunWithStreamsTests
{
	[Fact]
	public async Task RunWithStreamsAsync_CopiesStdout()
	{
		var startInfo = ProcessRunner.CreateStartInfo(ShellCommand.Executable, ShellCommand.Echo("streamed"));
		await using var output = new MemoryStream();

		var exitCode = await ProcessRunner.RunWithStreamsAsync(
			startInfo,
			new ProcessStreamOptions { StandardOutput = output },
			timeoutMs: 10000);

		exitCode.ShouldBe(0);
		Encoding.UTF8.GetString(output.ToArray()).Trim().ShouldBe("streamed");
	}

	[Fact]
	public async Task MonitorStderrAsLines_ThrowsOnFailure()
	{
		var startInfo = ProcessRunner.CreateStartInfo(
			ShellCommand.Executable,
			ShellCommand.FailWithStderr("nope", 3));

		var ex = await Should.ThrowAsync<ProcessExitException>(() =>
			ProcessRunner.RunWithStreamsAsync(
				startInfo,
				new ProcessStreamOptions { MonitorStderrAsLines = true },
				timeoutMs: 10000));

		ex.ExitCode.ShouldBe(3);
	}
}
