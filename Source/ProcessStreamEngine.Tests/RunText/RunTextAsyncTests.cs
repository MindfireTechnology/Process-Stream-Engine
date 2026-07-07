using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.RunText;

public class RunTextAsyncTests
{
	[Fact]
	public async Task RunTextAsync_CapturesStdout()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("hello-world"));

		var result = await ProcessStreamEngine.RunTextAsync(startInfo);

		result.ExitCode.ShouldBe(0);
		result.StandardOutput.Trim().ShouldBe("hello-world");
		result.StandardError.ShouldBeEmpty();
	}

	[Fact]
	public async Task RunTextAsync_DoesNotThrowOnNonZeroExit()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.ExitWith(5));

		var result = await ProcessStreamEngine.RunTextAsync(startInfo);

		result.ExitCode.ShouldBe(5);
	}

	[Fact]
	public async Task RunTextAsync_CapturesStderr()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.EchoStderr("error-line"));

		var result = await ProcessStreamEngine.RunTextAsync(startInfo);

		result.ExitCode.ShouldBe(0);
		result.StandardError.Trim().ShouldBe("error-line");
	}

	[Fact]
	public async Task RunTextAsync_ThrowsWhenStartInfoNull()
	{
		await Should.ThrowAsync<ArgumentNullException>(() =>
			ProcessStreamEngine.RunTextAsync(null!));
	}
}

public class RunTextOrThrowAsyncTests
{
	[Fact]
	public async Task RunTextOrThrowAsync_ReturnsResultOnSuccess()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("ok"));

		var result = await ProcessStreamEngine.RunTextOrThrowAsync(startInfo);

		result.ExitCode.ShouldBe(0);
		result.StandardOutput.Trim().ShouldBe("ok");
	}

	[Fact]
	public async Task RunTextOrThrowAsync_ThrowsProcessStreamEngineExceptionOnFailure()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(
			ShellCommand.FailWithStderr("err-msg", 3));

		var exception = await Should.ThrowAsync<ProcessStreamEngineException>(() =>
			ProcessStreamEngine.RunTextOrThrowAsync(startInfo));

		exception.ExitCode.ShouldBe(3);
		exception.StandardError.ShouldContain("err-msg");
	}
}
