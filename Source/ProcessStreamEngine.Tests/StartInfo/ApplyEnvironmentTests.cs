using Shouldly;

namespace ProcessStreamEngine.Tests.StartInfo;

public class ApplyEnvironmentTests
{
	[Fact]
	public void ApplyEnvironment_SetsVariables()
	{
		var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe");

		ProcessStreamEngine.ApplyEnvironment(startInfo, new Dictionary<string, string?>
		{
			["GIT_DIR"] = ".git",
			["GIT_WORK_TREE"] = @"C:\repo"
		});

		startInfo.Environment["GIT_DIR"].ShouldBe(".git");
		startInfo.Environment["GIT_WORK_TREE"].ShouldBe(@"C:\repo");
	}

	[Fact]
	public void ApplyEnvironment_RemovesVariableWhenValueIsNull()
	{
		var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe");
		startInfo.Environment["REMOVE_ME"] = "value";

		ProcessStreamEngine.ApplyEnvironment(startInfo, new Dictionary<string, string?>
		{
			["REMOVE_ME"] = null
		});

		startInfo.Environment.ContainsKey("REMOVE_ME").ShouldBeFalse();
	}

	[Fact]
	public void ApplyEnvironment_ThrowsWhenStartInfoNull()
	{
		Should.Throw<ArgumentNullException>(() =>
			ProcessStreamEngine.ApplyEnvironment(null!, new Dictionary<string, string?>()));
	}

	[Fact]
	public void ApplyEnvironment_ThrowsWhenVariablesNull()
	{
		var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe");

		Should.Throw<ArgumentNullException>(() =>
			ProcessStreamEngine.ApplyEnvironment(startInfo, null!));
	}
}
