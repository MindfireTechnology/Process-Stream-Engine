using System.Diagnostics;
using System.Text;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.RunWithStreams;

public class RunWithStreamsAsyncTests
{
	[Fact]
	public async Task RunWithStreamsAsync_CopiesStdoutAsRawBytes()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("binary-test"));
		var output = new MemoryStream();

		var exitCode = await ProcessStreamEngine.RunWithStreamsAsync(
			startInfo,
			new ProcessStreamOptions { StandardOutput = output });

		exitCode.ShouldBe(0);
		Encoding.UTF8.GetString(output.ToArray()).Trim().ShouldBe("binary-test");
	}

	[Fact]
	public async Task RunWithStreamsAsync_CopiesStderrAsRawBytes()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.EchoStderr("stderr-binary"));
		var error = new MemoryStream();

		var exitCode = await ProcessStreamEngine.RunWithStreamsAsync(
			startInfo,
			new ProcessStreamOptions { StandardError = error });

		exitCode.ShouldBe(0);
		Encoding.UTF8.GetString(error.ToArray()).Trim().ShouldBe("stderr-binary");
	}

	[Fact]
	public async Task RunWithStreamsAsync_CopiesStdinToProcess()
	{
		var startInfo = ShellCommand.PowerShell(
			"$bytes = New-Object byte[] 3; [void][Console]::OpenStandardInput().Read($bytes,0,3); [Console]::OpenStandardOutput().Write($bytes,0,3)");
		var input = new MemoryStream([0x01, 0x02, 0x03]);
		var output = new MemoryStream();

		var exitCode = await ProcessStreamEngine.RunWithStreamsAsync(
			startInfo,
			new ProcessStreamOptions
			{
				StandardInput = input,
				StandardOutput = output
			},
			timeoutMs: 15000);

		exitCode.ShouldBe(0);
		output.ToArray().ShouldBe([0x01, 0x02, 0x03]);
	}

	[Fact]
	public async Task RunWithStreamsAsync_MonitorStderrAsLines_ThrowsOnNonZeroExit()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(
			ShellCommand.FailWithStderr("git-fatal", 42));

		var exception = await Should.ThrowAsync<ProcessStreamEngineException>(() =>
			ProcessStreamEngine.RunWithStreamsAsync(
				startInfo,
				new ProcessStreamOptions { MonitorStderrAsLines = true },
				timeoutMs: 10000));

		exception.ExitCode.ShouldBe(42);
		exception.StandardError.Trim().ShouldBe("git-fatal");
	}

	[Fact]
	public async Task RunWithStreamsAsync_ThrowsWhenStderrStreamAndMonitorBothSet()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("x"));

		await Should.ThrowAsync<ArgumentException>(() =>
			ProcessStreamEngine.RunWithStreamsAsync(
				startInfo,
				new ProcessStreamOptions
				{
					StandardError = new MemoryStream(),
					MonitorStderrAsLines = true
				}));
	}

	[Fact]
	public async Task RunWithStreamsAsync_LeaveStreamsOpen_DoesNotDisposeDestination()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("open"));
		var output = new MemoryStream();

		await ProcessStreamEngine.RunWithStreamsAsync(
			startInfo,
			new ProcessStreamOptions
			{
				StandardOutput = output,
				LeaveStreamsOpen = true
			});

		output.CanWrite.ShouldBeTrue();
	}

	[Fact]
	public async Task RunWithStreamsAsync_CancellationKillsProcess()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.SleepForever());

		using var cts = new CancellationTokenSource();
		cts.CancelAfter(500);

		await Should.ThrowAsync<OperationCanceledException>(() =>
			ProcessStreamEngine.RunWithStreamsAsync(
				startInfo,
				new ProcessStreamOptions(),
				cancellationToken: cts.Token,
				timeoutMs: 60000));
	}
}
