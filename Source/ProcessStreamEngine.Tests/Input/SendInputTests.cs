using System.Text;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Input;

public class SendInputAsyncTests
{
	[Fact]
	public async Task SendInputAsync_WritesLineToProcess()
	{
		var startInfo = ShellCommand.PowerShell(
			"$line = [Console]::In.ReadLine(); Write-Output \"got:$line\"");
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);
		await engine.StartMonitoringAsync();
		await engine.SendInputAsync("hello");
		await engine.WaitForExitAsync(timeoutMs: 15000);

		engine.StdoutText.Trim().ShouldBe("got:hello");
	}

	[Fact]
	public async Task SendInputAsync_ThrowsWhenProcessNotStarted()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("x"), redirectInput: true);
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);

		await Should.ThrowAsync<InvalidOperationException>(() =>
			engine.SendInputAsync("input"));
	}

	[Fact]
	public async Task SendInputAsync_ThrowsWhenStdinNotRedirected()
	{
		var process = ProcessTestHarness.StartProcess(ShellCommand.Echo("x"));
		using var engine = new ProcessStreamEngine(process);

		await Should.ThrowAsync<InvalidOperationException>(() =>
			engine.SendInputAsync("input"));
	}
}

public class SendRawInputAsyncTests
{
	[Fact]
	public async Task SendRawInputAsync_WritesBytesWithoutNewline()
	{
		var startInfo = ShellCommand.PowerShell(
			"$b = New-Object byte[] 1; [void][Console]::OpenStandardInput().Read($b,0,1); Write-Output $b[0]");
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);
		await engine.StartMonitoringAsync();
		await engine.SendRawInputAsync(new byte[] { 0 });
		await engine.WaitForExitAsync(timeoutMs: 15000);

		engine.StdoutText.Trim().ShouldBe("0");
	}

	[Fact]
	public async Task SendRawInputAsync_ThrowsWhenProcessNotStarted()
	{
		var startInfo = ProcessTestHarness.CreateRedirectedStartInfo(ShellCommand.Echo("x"), redirectInput: true);
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);

		await Should.ThrowAsync<InvalidOperationException>(() =>
			engine.SendRawInputAsync(new byte[] { 0 }));
	}

	[Fact]
	public async Task SendRawInputAsync_SendsMultipleBytesWithoutNewline()
	{
		var startInfo = ShellCommand.PowerShell(
			"$bytes = New-Object byte[] 3; [void][Console]::OpenStandardInput().Read($bytes,0,3); [Console]::OpenStandardOutput().Write($bytes,0,3)");
		using var engine = ProcessStreamEngine.CreateDeferred(startInfo);

		await engine.StartMonitoringAsync();
		await engine.SendRawInputAsync(new byte[] { 0x41, 0x42, 0x43 });
		await engine.WaitForExitAsync(timeoutMs: 15000);

		engine.StdoutLines.ShouldNotBeEmpty();
	}
}
