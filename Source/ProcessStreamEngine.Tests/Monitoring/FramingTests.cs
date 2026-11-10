using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class FramingTests
{
	[Fact]
	public async Task NulFraming_SplitsOnNullTerminator()
	{
		var startInfo = ShellCommand.PowerShell(
			"[Console]::Write('alpha' + [char]0 + 'beta' + [char]0)");

		var lines = new List<string>();
		await using var session = ProcessSessionBuilder.Create(startInfo).UseNulFraming().Start();
		await foreach (var line in session.Output.ReadAllAsync())
		{
			if (line.Text.Length > 0)
				lines.Add(line.Text);
		}

		await session.WaitForExitAsync();
		lines.ShouldBe(["alpha", "beta"]);
	}
}
