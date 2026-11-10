using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Runner;

public class RunTextTests
{
	[Fact]
	public async Task RunTextAsync_ReturnsBufferedOutput()
	{
		var startInfo = ProcessRunner.CreateStartInfo(ShellCommand.Executable, ShellCommand.Echo("hello"));
		var result = await ProcessRunner.RunTextAsync(startInfo);
		result.ExitCode.ShouldBe(0);
		result.StandardOutput.Trim().ShouldBe("hello");
	}

	[Fact]
	public async Task RunTextOrThrowAsync_ThrowsOnNonZeroExit()
	{
		var startInfo = ProcessRunner.CreateStartInfo(
			ShellCommand.Executable,
			ShellCommand.FailWithStderr("boom", 2));

		var ex = await Should.ThrowAsync<ProcessExitException>(() =>
			ProcessRunner.RunTextOrThrowAsync(startInfo, timeoutMs: 10000));

		ex.ExitCode.ShouldBe(2);
		ex.StandardErrorTail.ShouldContain("boom");
	}
}
