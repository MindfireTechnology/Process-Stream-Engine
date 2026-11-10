using ProcessStreamEngine.Interaction;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class PartialPromptTests
{
	[Fact]
	public async Task ExpectAsync_MatchesPromptWithoutTrailingNewline()
	{
		var startInfo = ShellCommand.PowerShell(
			"[Console]::Write('> '); Start-Sleep -Seconds 3");

		await using var session = ProcessSessionBuilder.Create(startInfo)
			.PartialTailQuietPeriod(TimeSpan.FromMilliseconds(80))
			.Start();

		var match = await session.ExpectAsync(
			new System.Text.RegularExpressions.Regex(@">\s*$"),
			timeout: TimeSpan.FromSeconds(5));

		match.Success.ShouldBeTrue();
		await session.DisposeAsync();
	}
}
