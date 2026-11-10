using System.Text.RegularExpressions;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class EventAndCaptureTests
{
	[Fact]
	public async Task OnMatch_InvokesHandler()
	{
		var seen = new List<string>();
		await using var session = ProcessTestHarness.StartSession(
			ShellCommand.Echo("token-42"),
			b => b.OnMatch("id", new Regex(@"token-(\d+)"), m => seen.Add(m.Groups[1].Value)));

		await session.WaitForExitAsync();
		seen.ShouldBe(["42"]);
	}

	[Fact]
	public async Task Capture_StoresValues()
	{
		await using var session = ProcessTestHarness.StartSession(
			ShellCommand.Echo("TTL=128"),
			b => b.Capture("ttl", new Regex(@"TTL=(\d+)"), captureGroup: "1"));

		await session.WaitForExitAsync();
		session.Output.GetCapturedData("ttl").ShouldBe(["128"]);
	}

	[Fact]
	public async Task ExpectAsync_ReturnsMatch()
	{
		await using var session = ProcessTestHarness.StartSession(ShellCommand.Echo("ready"));
		var readTask = session.ExpectAsync(new Regex("ready"), timeout: TimeSpan.FromSeconds(5));
		var match = await readTask;
		match.Success.ShouldBeTrue();
		await session.WaitForExitAsync();
	}
}
