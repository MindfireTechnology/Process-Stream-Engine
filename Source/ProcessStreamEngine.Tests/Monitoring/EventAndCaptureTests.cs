using System.Text.RegularExpressions;
using FakeItEasy;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class EventAndCaptureTests
{
	[Fact]
	public async Task RegisterEvent_InvokesHandlerOnMatch()
	{
		var matches = new List<Match>();
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("commit abc1234"));
		engine.RegisterEvent("commit", @"abc(\d+)", matches.Add);

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		matches.Count.ShouldBe(1);
		matches[0].Groups[1].Value.ShouldBe("1234");
	}

	[Fact]
	public async Task RegisterStringEvent_InvokesHandlerOnSubstringMatch()
	{
		var invoked = false;
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("hello world"));
		engine.RegisterStringEvent("greet", "hello", () => invoked = true);

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		invoked.ShouldBeTrue();
	}

	[Fact]
	public async Task RegisterDataCapture_StoresMatchedValue()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("sha1 deadbeef"));
		engine.RegisterDataCapture("hash", @"sha1 ([a-f0-9]+)", captureGroup: "1");

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		engine.GetCapturedData("hash").ShouldBe(["deadbeef"]);
		engine.GetAllCapturedData().ShouldContain("deadbeef");
	}

	[Fact]
	public async Task ClearCapturedData_RemovesPatternData()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("id 42"));
		engine.RegisterDataCapture("id", @"id (\d+)", captureGroup: "1");

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();
		engine.GetCapturedData("id").Count().ShouldBe(1);

		engine.ClearCapturedData("id");
		engine.GetCapturedData("id").ShouldBeEmpty();
	}

	[Fact]
	public void RegisterEvent_ThrowsWhenPatternNameEmpty()
	{
		using var process = ProcessTestHarness.StartProcess(ShellCommand.Echo("x"));
		using var engine = new ProcessStreamEngine(process);

		Should.Throw<ArgumentException>(() =>
			engine.RegisterEvent("", ".*", _ => { }));
	}
}

public class WaitForEventTests
{
	[Fact]
	public async Task WaitForEventAsync_ReturnsMatchWhenPatternOccurs()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("ready: done"));
		engine.RegisterEvent("ready", @"ready: (\w+)", _ => { });

		await engine.StartMonitoringAsync();
		var match = await engine.WaitForEventAsync("ready", timeoutMs: 10000);
		await engine.WaitForExitAsync();

		match.Groups[1].Value.ShouldBe("done");
	}

	[Fact]
	public async Task WaitForEventAsync_ThrowsWhenPatternNotRegistered()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("x"));

		await Should.ThrowAsync<ArgumentException>(() =>
			engine.WaitForEventAsync("missing"));
	}
}

public class WaitForPromptTests
{
	[Fact]
	public async Task WaitForPromptAsync_MatchesPromptPattern()
	{
		var startInfo = ShellCommand.PowerShell("Write-Output 'Question>'; $null = [Console]::In.ReadLine()");
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);

		await engine.StartMonitoringAsync();
		var matchTask = engine.WaitForPromptAsync(@">\s*$", timeoutMs: 10000);
		await engine.SendInputAsync("answer");
		var match = await matchTask;
		await engine.WaitForExitAsync(timeoutMs: 10000);

		match.Success.ShouldBeTrue();
	}
}

public class WaitForAnyConditionTests
{
	[Fact]
	public async Task WaitForAnyConditionAsync_RunsFirstMatchingAction()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("branch: main"));
		var actionRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var waitTask = engine.WaitForAnyConditionAsync(
		[
			new ProcessStreamEngine.ConditionalAction
			{
				PatternName = "branch",
				Pattern = @"branch: (\w+)",
				Action = (_, _) =>
				{
					actionRan.TrySetResult();
					return Task.CompletedTask;
				}
			}
		],
		defaultTimeoutMs: 10000);

		await engine.StartMonitoringAsync();
		await waitTask;
		await engine.WaitForExitAsync();

		// The action is started fire-and-forget once the pattern matches, so wait for it instead of assuming it already ran.
		await actionRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}
}
