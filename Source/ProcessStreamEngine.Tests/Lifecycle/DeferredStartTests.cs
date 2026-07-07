using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Lifecycle;

public class DeferredStartTests
{
	[Fact]
	public async Task CreateDeferred_StartsProcessOnStartMonitoring()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("deferred"));

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		engine.StdoutText.Trim().ShouldBe("deferred");
	}

	[Fact]
	public async Task WaitForExitAsync_ThrowsWhenMonitoringNotStarted()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("x"));

		await Should.ThrowAsync<InvalidOperationException>(() =>
			engine.WaitForExitAsync());
	}
}

public class DisposeTests
{
	[Fact]
	public void Dispose_CanBeCalledMultipleTimes()
	{
		var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("x"));

		engine.Dispose();
		Should.NotThrow(() => engine.Dispose());
	}

	[Fact]
	public async Task Cleanup_StopsMonitoring()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("cleanup"));
		await engine.StartMonitoringAsync();
		engine.Cleanup();

		Should.NotThrow(() => engine.Cleanup());
	}
}
