using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ProcessStreamEngine.Output;

namespace ProcessStreamEngine;

/// <summary>
/// One-shot and streaming helpers built on <see cref="ProcessSession"/>.
/// </summary>
public static class ProcessRunner
{
	public static ProcessSessionBuilder Create(ProcessStartInfo startInfo) =>
		ProcessSessionBuilder.Create(startInfo);

	public static ProcessSessionBuilder Attach(Process process) =>
		ProcessSessionBuilder.Attach(process);

	public static async Task<ProcessTextResult> RunTextAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken = default,
		int? timeoutMs = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);

		await using var session = ProcessSessionBuilder.Create(startInfo)
			.KeepTranscript()
			.Start();

		var result = await session.WaitForExitAsync(cancellationToken, timeoutMs ?? 30000);
		return new ProcessTextResult(
			result.ExitCode,
			result.StandardOutput ?? string.Empty,
			result.StandardError ?? string.Empty);
	}

	public static async Task<ProcessTextResult> RunTextOrThrowAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken = default,
		int? timeoutMs = null)
	{
		var result = await RunTextAsync(startInfo, cancellationToken, timeoutMs);
		if (result.ExitCode != 0)
		{
			throw new ProcessExitException(
				result.ExitCode,
				TruncateForMessage(result.StandardError));
		}

		return result;
	}

	public static async IAsyncEnumerable<OutputLine> ReadLinesAsync(
		ProcessStartInfo startInfo,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await using var session = ProcessSessionBuilder.Create(startInfo).Start();
		await foreach (var line in session.Output.ReadAllAsync(cancellationToken: cancellationToken))
			yield return line;

		await session.WaitForExitAsync(cancellationToken);
	}

	public static async Task<int> RunWithStreamsAsync(
		ProcessStartInfo startInfo,
		ProcessStreamOptions options,
		CancellationToken cancellationToken = default,
		int? timeoutMs = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentNullException.ThrowIfNull(options);

		if (options.StandardError != null && options.MonitorStderrAsLines)
		{
			throw new ArgumentException(
				"StandardError stream and MonitorStderrAsLines are mutually exclusive.",
				nameof(options));
		}

		startInfo.UseShellExecute = false;

		if (options.StandardInput != null)
			startInfo.RedirectStandardInput = true;
		if (options.StandardOutput != null)
			startInfo.RedirectStandardOutput = true;
		if (options.StandardError != null || options.MonitorStderrAsLines)
			startInfo.RedirectStandardError = true;

		using var process = new Process { StartInfo = startInfo };
		process.Start();

		using var timeoutCts = timeoutMs.HasValue ? new CancellationTokenSource(timeoutMs.Value) : null;
		using var linkedCts = timeoutCts != null
			? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
			: null;
		var token = linkedCts?.Token ?? cancellationToken;

		var stderrLines = new List<string>();
		var copyTasks = new List<Task>();

		if (options.StandardInput != null)
		{
			copyTasks.Add(CopyToProcessStdinAsync(
				options.StandardInput,
				process,
				options.LeaveStreamsOpen,
				token));
		}

		if (options.StandardOutput != null)
		{
			copyTasks.Add(CopyFromProcessStreamAsync(
				process.StandardOutput.BaseStream,
				options.StandardOutput,
				options.LeaveStreamsOpen,
				token));
		}

		if (options.StandardError != null)
		{
			copyTasks.Add(CopyFromProcessStreamAsync(
				process.StandardError.BaseStream,
				options.StandardError,
				options.LeaveStreamsOpen,
				token));
		}
		else if (options.MonitorStderrAsLines)
		{
			copyTasks.Add(MonitorStderrAsLinesAsync(process.StandardError, stderrLines, token));
		}

		try
		{
			await process.WaitForExitAsync(token);
			await Task.WhenAll(copyTasks);

			var exitCode = process.ExitCode;

			if (options.MonitorStderrAsLines && exitCode != 0)
			{
				throw new ProcessExitException(
					exitCode,
					TruncateForMessage(string.Join(Environment.NewLine, stderrLines)));
			}

			return exitCode;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			KillProcessIfRunning(process);
			throw;
		}
	}

	public static ProcessStartInfo CreateStartInfo(
		string fileName,
		IEnumerable<string> arguments,
		string? workingDirectory = null)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));
		ArgumentNullException.ThrowIfNull(arguments);

		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		if (workingDirectory != null)
			startInfo.WorkingDirectory = workingDirectory;

		return startInfo;
	}

	public static void ApplyEnvironment(
		ProcessStartInfo startInfo,
		IReadOnlyDictionary<string, string?> variables)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentNullException.ThrowIfNull(variables);

		foreach (var (key, value) in variables)
		{
			if (value is null)
				startInfo.Environment.Remove(key);
			else
				startInfo.Environment[key] = value;
		}
	}

	private static string TruncateForMessage(string text, int maxLength = 2048)
	{
		if (text.Length <= maxLength)
			return text;

		return text[..maxLength] + "…";
	}

	private static async Task CopyToProcessStdinAsync(
		Stream source,
		Process process,
		bool leaveStreamsOpen,
		CancellationToken cancellationToken)
	{
		try
		{
			await source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
		}
		finally
		{
			process.StandardInput.Close();

			if (!leaveStreamsOpen)
				await source.DisposeAsync();
		}
	}

	private static async Task CopyFromProcessStreamAsync(
		Stream source,
		Stream destination,
		bool leaveStreamsOpen,
		CancellationToken cancellationToken)
	{
		await source.CopyToAsync(destination, cancellationToken);

		if (!leaveStreamsOpen)
			await destination.DisposeAsync();
	}

	private static async Task MonitorStderrAsLinesAsync(
		StreamReader stderr,
		List<string> stderrLines,
		CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var line = await stderr.ReadLineAsync(cancellationToken);
				if (line is null)
					break;

				lock (stderrLines)
					stderrLines.Add(line);
			}
		}
		catch (OperationCanceledException)
		{
			// Expected when cancelled
		}
	}

	private static void KillProcessIfRunning(Process process)
	{
		if (process.HasExited)
			return;

		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Process may have exited
		}
	}
}
