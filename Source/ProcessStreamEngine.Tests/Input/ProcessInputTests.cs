using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Input;

public class ProcessInputTests
{
	[Fact]
	public async Task WriteLineAsync_WritesToProcess()
	{
		var startInfo = ShellCommand.PowerShell(
			"$line = [Console]::In.ReadLine(); Write-Output \"got:$line\"");

		await using var psSession = ProcessSessionBuilder.Create(startInfo).KeepTranscript().Start();
		await psSession.Input.WriteLineAsync("hello");
		var result = await psSession.WaitForExitAsync(timeoutMs: 15000);
		result.StandardOutput!.Trim().ShouldBe("got:hello");
	}
}
