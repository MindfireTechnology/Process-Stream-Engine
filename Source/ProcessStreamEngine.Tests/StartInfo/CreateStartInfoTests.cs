using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.StartInfo;

public class CreateStartInfoTests
{
	[Fact]
	public void CreateStartInfo_SetsFileNameAndArguments()
	{
		var startInfo = ProcessStreamEngine.CreateStartInfo("git.exe", ["log", "--oneline"]);

		startInfo.FileName.ShouldBe("git.exe");
		startInfo.ArgumentList.ShouldBe(["log", "--oneline"]);
		startInfo.UseShellExecute.ShouldBeFalse();
	}

	[Fact]
	public void CreateStartInfo_SetsWorkingDirectoryWhenProvided()
	{
		var startInfo = ProcessStreamEngine.CreateStartInfo("cmd.exe", ["/c", "echo", "hi"], @"C:\Temp");

		startInfo.WorkingDirectory.ShouldBe(@"C:\Temp");
	}

	[Fact]
	public void CreateStartInfo_ThrowsWhenFileNameIsEmpty()
	{
		Should.Throw<ArgumentException>(() =>
			ProcessStreamEngine.CreateStartInfo("", ["arg"]));
	}

	[Fact]
	public void CreateStartInfo_ThrowsWhenArgumentsNull()
	{
		Should.Throw<ArgumentNullException>(() =>
			ProcessStreamEngine.CreateStartInfo("cmd.exe", null!));
	}
}
