using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class TranscriptTests
{
	[Fact]
	public async Task KeepTranscript_PreservesStdoutText()
	{
		var startInfo = ShellCommand.PowerShell("Write-Output 'line-one'; Write-Output 'line-two'");
		await using var session = ProcessSessionBuilder.Create(startInfo).KeepTranscript().Start();
		var result = await session.WaitForExitAsync();
		result.StandardOutput!.ShouldContain("line-one");
		result.StandardOutput!.ShouldContain("line-two");
	}
}
