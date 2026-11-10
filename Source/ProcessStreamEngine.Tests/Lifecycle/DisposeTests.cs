using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Lifecycle;

public class DisposeTests
{
	[Fact]
	public async Task DisposeAsync_CompletesWithoutThrowing()
	{
		var session = ProcessTestHarness.StartSession(ShellCommand.SleepForever());
		await session.DisposeAsync();
	}
}
