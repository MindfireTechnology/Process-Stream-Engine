using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Session;

public class SessionBuilderTests
{
	[Fact]
	public void Create_RequiresStartInfo()
	{
		Should.Throw<ArgumentNullException>(() => ProcessSessionBuilder.Create(null!));
	}

	[Fact]
	public async Task Start_RunsProcessAndReturnsExitCode()
	{
		await using var session = ProcessTestHarness.StartSession(ShellCommand.Echo("ok"));
		var result = await session.WaitForExitAsync();
		result.ExitCode.ShouldBe(0);
		result.StandardOutput!.Trim().ShouldBe("ok");
	}
}
