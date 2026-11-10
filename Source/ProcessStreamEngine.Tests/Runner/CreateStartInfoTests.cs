using System.Diagnostics;
using Shouldly;

namespace ProcessStreamEngine.Tests.Runner;

public class CreateStartInfoTests
{
	[Fact]
	public void CreateStartInfo_BuildsArgumentList()
	{
		var startInfo = ProcessRunner.CreateStartInfo("tool.exe", ["a", "b"]);
		startInfo.FileName.ShouldBe("tool.exe");
		startInfo.ArgumentList.ShouldBe(["a", "b"]);
		startInfo.RedirectStandardOutput.ShouldBeTrue();
		startInfo.RedirectStandardError.ShouldBeTrue();
	}

	[Fact]
	public void ApplyEnvironment_SetsAndRemovesVariables()
	{
		var startInfo = new ProcessStartInfo("tool.exe");
		startInfo.Environment["KEEP"] = "old";

		ProcessRunner.ApplyEnvironment(startInfo, new Dictionary<string, string?>
		{
			["NEW"] = "value",
			["KEEP"] = null
		});

		startInfo.Environment.ContainsKey("KEEP").ShouldBeFalse();
		startInfo.Environment["NEW"].ShouldBe("value");
	}
}
