using ProcessStreamEngine.Output;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class BackpressureTests
{
	[Fact]
	public async Task SlowSubscriber_DoesNotDropLinesForFastSubscriber()
	{
		const int count = 30;
		var startInfo = ShellCommand.PowerShell(
			$"1..{count} | ForEach-Object {{ Write-Output \"out-$_\"; Start-Sleep -Milliseconds 25 }}");

		var fast = new List<string>();
		await using var session = ProcessSessionBuilder.Create(startInfo).Start();

		var slow = session.Output.SubscribeChannel(capacity: 2, backpressure: BackpressurePolicy.Wait);
		var fastSub = session.Output.SubscribeChannel(capacity: 256, backpressure: BackpressurePolicy.Wait);

		var slowTask = Task.Run(async () =>
		{
			await foreach (var line in slow.Channel.Reader.ReadAllAsync())
			{
				if (line.Stream == OutputStream.StandardOutput)
					await Task.Delay(30);
			}
		});

		var fastTask = Task.Run(async () =>
		{
			await foreach (var line in fastSub.Channel.Reader.ReadAllAsync())
			{
				if (line.Stream == OutputStream.StandardOutput)
					fast.Add(line.Text.Trim());
			}
		});

		await session.WaitForExitAsync(timeoutMs: 120000);
		await Task.WhenAll(slowTask, fastTask);

		fast.Count.ShouldBe(count);
		fast.ShouldContain("out-1");
		fast.ShouldContain($"out-{count}");
	}
}
