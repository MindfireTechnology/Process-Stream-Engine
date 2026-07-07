using FakeItEasy;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Monitoring;

public class LineAccumulationTests
{
	[Fact]
	public async Task StartMonitoringAsync_AccumulatesStdoutLinesInOrder()
	{
		using var engine = await ProcessTestHarness.RunToCompletionAsync(
			ShellCommand.Echo("line-one"));

		engine.StdoutLines.Count.ShouldBe(1);
		engine.StdoutLines[0].Trim().ShouldBe("line-one");
		engine.StdoutText.Trim().ShouldBe("line-one");
		engine.StderrLines.ShouldBeEmpty();
		engine.StderrText.ShouldBeEmpty();
	}

	[Fact]
	public async Task StartMonitoringAsync_AccumulatesStderrLines()
	{
		using var engine = await ProcessTestHarness.RunToCompletionAsync(
			ShellCommand.EchoStderr("error-one"));

		engine.StderrLines.Count.ShouldBe(1);
		engine.StderrLines[0].Trim().ShouldBe("error-one");
		engine.StderrText.Trim().ShouldBe("error-one");
	}

	[Fact]
	public async Task StartMonitoringAsync_ClearsLinesBetweenRuns()
	{
		using var firstEngine = await ProcessTestHarness.RunToCompletionAsync(ShellCommand.Echo("first"));
		firstEngine.StdoutLines.Count.ShouldBe(1);

		using var secondEngine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("second"));
		secondEngine.StdoutLines.ShouldBeEmpty();
		await secondEngine.StartMonitoringAsync();
		await secondEngine.WaitForExitAsync();
		secondEngine.StdoutLines[0].Trim().ShouldBe("second");
	}

	[Fact]
	public void StdoutLines_IsEmptyBeforeMonitoring()
	{
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("x"));
		engine.StdoutLines.ShouldBeEmpty();
		engine.StdoutText.ShouldBeEmpty();
	}
}

public class ConsoleStreamHandlerTests
{
	[Fact]
	public async Task ConsoleStreamHandler_ReceivesStdoutLines()
	{
		var handler = ProcessTestHarness.CreateFakeConsoleHandler(out var captured);
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("streamed"));
		engine.ConsoleStreamHandler = handler;

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		captured.Count.ShouldBe(1);
		captured[0].Line.Trim().ShouldBe("streamed");
		captured[0].IsError.ShouldBeFalse();
	}

	[Fact]
	public async Task ConsoleStreamHandler_ReceivesStderrLines()
	{
		var handler = ProcessTestHarness.CreateFakeConsoleHandler(out var captured);
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.EchoStderr("err-stream"));
		engine.ConsoleStreamHandler = handler;

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		captured.Count.ShouldBe(1);
		captured[0].Line.Trim().ShouldBe("err-stream");
		captured[0].IsError.ShouldBeTrue();
	}

	[Fact]
	public async Task ConsoleStreamHandler_TakesPrecedenceOverStreamToConsole()
	{
		var handler = A.Fake<Action<string, bool>>();
		using var engine = ProcessTestHarness.CreateDeferredEngine(ShellCommand.Echo("x"));
		engine.StreamToConsole = true;
		engine.ConsoleStreamHandler = handler;

		await engine.StartMonitoringAsync();
		await engine.WaitForExitAsync();

		A.CallTo(() => handler(A<string>._, A<bool>._)).MustHaveHappened();
	}
}
