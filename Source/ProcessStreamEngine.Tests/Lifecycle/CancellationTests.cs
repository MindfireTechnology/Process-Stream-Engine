using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Lifecycle;

public class CancellationTests
{
	[Fact]
	public async Task WaitForExitAsync_CancellationKillsChildProcess()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.SleepForever());
		using var cts = new CancellationTokenSource();
		await using var session = ProcessSessionBuilder.Create(startInfo).KillOnCancel().Start();

		var waitTask = session.WaitForExitAsync(cts.Token, timeoutMs: 60000);
		await Task.Delay(200);
		await cts.CancelAsync();

		await Should.ThrowAsync<OperationCanceledException>(() => waitTask);
		await session.DisposeAsync();
	}
}
