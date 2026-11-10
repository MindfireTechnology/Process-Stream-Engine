using System.Diagnostics;
using System.Text.RegularExpressions;
using ProcessStreamEngine.Interaction;
using ProcessStreamEngine.Output;

namespace ProcessStreamEngine;

/// <summary>
/// A running process with coordinated stdout/stderr readers and subscriber fan-out.
/// </summary>
public sealed class ProcessSession : IAsyncDisposable
{
	private readonly Process Process;
	private readonly bool OwnsProcess;
	private readonly OutputHub Hub;
	private readonly OutputTranscript? Transcript;
	private readonly CancellationTokenSource SessionCts = new();
	private readonly List<Task> ReaderTasks = new();
	private readonly bool KillOnCancelBacking;
	private bool Disposed;
	private Task<int>? CompletionTask;

	public ProcessOutput Output { get; }
	public ProcessInput Input { get; }

	internal ProcessSession(
		Process process,
		bool ownsProcess,
		OutputHub hub,
		OutputTranscript? transcript,
		IOutputFraming framing,
		bool killOnCancel,
		TimeSpan? partialTailQuietPeriod)
	{
		Process = process;
		OwnsProcess = ownsProcess;
		Hub = hub;
		Transcript = transcript;
		KillOnCancelBacking = killOnCancel;

		Output = new ProcessOutput(hub);
		Input = new ProcessInput(process, SessionCts.Token);

		var stdoutReader = new OutputReader(
			process.StandardOutput.BaseStream,
			OutputStream.StandardOutput,
			CloneFraming(framing),
			hub,
			transcript,
			partialTailQuietPeriod);

		var stderrReader = new OutputReader(
			process.StandardError.BaseStream,
			OutputStream.StandardError,
			CloneFraming(framing),
			hub,
			transcript,
			partialTailQuietPeriod);

		ReaderTasks.Add(Task.Run(() => stdoutReader.RunAsync(SessionCts.Token)));
		ReaderTasks.Add(Task.Run(() => stderrReader.RunAsync(SessionCts.Token)));

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.WhenAll(ReaderTasks).ConfigureAwait(false);
				await Hub.CompleteAllAsync().ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				await Hub.CompleteAllAsync().ConfigureAwait(false);
			}
		});
	}

	public Task<int> Completion => CompletionTask ??= RunCompletionAsync();

	public Task<Match> ExpectAsync(
		Regex pattern,
		OutputStream? stream = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default) =>
		Output.ExpectAsync(pattern, stream, timeout, cancellationToken);

	public async Task<ProcessResult> WaitForExitAsync(
		CancellationToken cancellationToken = default,
		int timeoutMs = 30000)
	{
		ObjectDisposedException.ThrowIf(Disposed, this);

		using var timeoutCts = new CancellationTokenSource(timeoutMs);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			SessionCts.Token,
			timeoutCts.Token);

		var exitObserved = false;
		try
		{
			if (!Process.HasExited)
				await Process.WaitForExitAsync(linked.Token);

			exitObserved = true;
			await Task.WhenAll(ReaderTasks).WaitAsync(linked.Token);
			await Hub.CompleteAllAsync();
		}
		catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
		{
			await ForceStopAsync();
			throw new ProcessTimeoutException(
				exitObserved ? ProcessTimeoutPhase.Drain : ProcessTimeoutPhase.Exit,
				timeoutMs);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && KillOnCancelBacking)
		{
			await ForceStopAsync();
			throw;
		}

		return BuildResult();
	}

	public async ValueTask DisposeAsync()
	{
		if (Disposed)
			return;

		Disposed = true;
		await ForceStopAsync();
		SessionCts.Dispose();
		GC.SuppressFinalize(this);
	}

	private async Task<int> RunCompletionAsync()
	{
		var result = await WaitForExitAsync();
		return result.ExitCode;
	}

	private ProcessResult BuildResult()
	{
		var result = new ProcessResult(Process.ExitCode);
		if (Transcript is null)
			return result;

		return result with
		{
			StandardOutput = Transcript.StandardOutput,
			StandardError = Transcript.StandardError
		};
	}

	private static IOutputFraming CloneFraming(IOutputFraming framing) =>
		framing switch
		{
			NulFraming => new NulFraming(),
			LineFraming => new LineFraming(),
			_ => framing
		};

	private async Task ForceStopAsync()
	{
		if (!SessionCts.IsCancellationRequested)
			await SessionCts.CancelAsync();

		try
		{
			await Task.WhenAll(ReaderTasks).WaitAsync(TimeSpan.FromSeconds(2));
		}
		catch
		{
			// Best effort
		}

		await Hub.CompleteAllAsync();

		if (OwnsProcess && !Process.HasExited)
		{
			try
			{
				Process.Kill(entireProcessTree: true);
				await Process.WaitForExitAsync(CancellationToken.None);
			}
			catch
			{
				// Process may already be gone
			}
		}
	}
}
