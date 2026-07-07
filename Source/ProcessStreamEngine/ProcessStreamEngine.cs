using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Instances;

namespace ProcessStreamEngine;

/// <summary>
/// Thrown when a process exits with a non-zero exit code and stderr is available.
/// </summary>
public sealed class ProcessStreamEngineException : Exception
{
	public int ExitCode { get; }
	public string StandardError { get; }

	public ProcessStreamEngineException(int exitCode, string standardError)
		: base($"Process exited with code {exitCode}. Stderr: {standardError}")
	{
		ExitCode = exitCode;
		StandardError = standardError;
	}
}

/// <summary>
/// Buffered text output from a completed process run.
/// </summary>
public sealed record ProcessTextResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Stream passthrough options for <see cref="ProcessStreamEngine.RunWithStreamsAsync"/>.
/// </summary>
public sealed record ProcessStreamOptions
{
	public Stream? StandardInput { get; init; }
	public Stream? StandardOutput { get; init; }
	public Stream? StandardError { get; init; }
	public bool MonitorStderrAsLines { get; init; }
	public bool LeaveStreamsOpen { get; init; }
}

/// <summary>
/// Takes a process that was started and watches the output for "events" and "data" that can be captured and processed.
/// Events would be delegates that are invoked when certain output is detected via Regex or string matching.
/// Data could also be captured via Regex and stored for later retrieval.
/// </summary>
public class ProcessStreamEngine : IDisposable
{
	/// <summary>
	/// The process being monitored.
	/// </summary>
	protected Process Process { get; private set; }

	/// <summary>
	/// When true, the process is started by <see cref="StartMonitoringAsync"/> rather than at construction.
	/// </summary>
	private readonly bool DeferredStart;

	/// <summary>
	/// Whether <see cref="EnsureProcessStarted"/> has been called.
	/// </summary>
	private bool ProcessStarted;

	/// <summary>
	/// Command line for deferred Instance.Start when using <see cref="CreateDeferredFromInstance"/>.
	/// </summary>
	private readonly (string FileName, string Arguments)? DeferredInstanceStart;

	/// <summary>
	/// The IProcessInstance if created from Instances library, null otherwise.
	/// </summary>
	protected IProcessInstance? ProcessInstance { get; private set; }

	/// <summary>
	/// Stream monitors for stdout and stderr.
	/// </summary>
	protected StreamMonitor StdoutMonitor { get; } = new(isErrorStream: false);
	protected StreamMonitor StderrMonitor { get; } = new(isErrorStream: true);

	/// <summary>
	/// List of monitoring tasks.
	/// </summary>
	protected List<Task> MonitoringTasks { get; } = new();

	/// <summary>
	/// Cancellation token source for stopping monitoring.
	/// </summary>
	protected CancellationTokenSource CancellationTokenSource { get; } = new();

	/// <summary>
	/// Backing field for StreamToConsole property.
	/// </summary>
	protected bool StreamToConsoleBacking = false;

	/// <summary>
	/// Track whether the instance has been disposed.
	/// </summary>
	private bool Disposed = false;


	/// <summary>
	/// Delegate for streaming output to console. Receives the line and a flag indicating if it's from stderr.
	/// </summary>
	public Action<string, bool>? ConsoleStreamHandler { get; set; }

	/// <summary>
	/// Gets or sets whether output should be streamed to console automatically.
	/// </summary>
	public bool StreamToConsole
	{
		get => StreamToConsoleBacking;
		set => StreamToConsoleBacking = value;
	}

	/// <summary>
	/// Gets stdout lines accumulated during line monitoring, in order.
	/// </summary>
	public IReadOnlyList<string> StdoutLines => StdoutMonitor.OrderedLines;

	/// <summary>
	/// Gets stderr lines accumulated during line monitoring, in order.
	/// </summary>
	public IReadOnlyList<string> StderrLines => StderrMonitor.OrderedLines;

	/// <summary>
	/// Gets stdout text joined with <see cref="Environment.NewLine"/>.
	/// </summary>
	public string StdoutText => StdoutMonitor.GetJoinedText();

	/// <summary>
	/// Gets stderr text joined with <see cref="Environment.NewLine"/>.
	/// </summary>
	public string StderrText => StderrMonitor.GetJoinedText();

	/// <summary>
	/// Gets all captured data entries for all patterns.
	/// </summary>
	public IEnumerable<string> GetAllCapturedData()
	{
		return StdoutMonitor.CapturedData.Values.SelectMany(bag => bag)
			.Concat(StderrMonitor.CapturedData.Values.SelectMany(bag => bag));
	}

	/// <summary>
	/// Gets the appropriate stream monitor based on error stream flag.
	/// </summary>
	protected StreamMonitor GetMonitor(bool isErrorStream) => 
		isErrorStream ? StderrMonitor : StdoutMonitor;

	/// <summary>
	/// Streams a line to the console if console streaming is enabled.
	/// </summary>
	protected void StreamLineToConsole(string line, bool isErrorStream)
	{
		if (!StreamToConsoleBacking && ConsoleStreamHandler == null) 
			return;

		if (ConsoleStreamHandler != null)
		{
			ConsoleStreamHandler(line, isErrorStream);
		}
		else if (StreamToConsoleBacking)
		{
			if (isErrorStream)
				Console.Error.WriteLine(line);
			else
				Console.WriteLine(line);
		}
	}

	/// <summary>
	/// Initializes a new instance of ProcessStreamEngine with the specified process.
	/// </summary>
	/// <param name="process">The process to monitor. Must have RedirectStandardOutput and RedirectStandardError set to true.</param>
	/// <exception cref="ArgumentNullException">Thrown when process is null.</exception>
	/// <exception cref="ArgumentException">Thrown when process output redirection is not enabled.</exception>
	public ProcessStreamEngine(Process process)
	{
		Process = process ?? throw new ArgumentNullException(nameof(process));
		
		if (!Process.StartInfo.RedirectStandardOutput || !Process.StartInfo.RedirectStandardError)
		{
			throw new ArgumentException("Process must have RedirectStandardOutput and RedirectStandardError set to true.", nameof(process));
		}

		ProcessStarted = true;
	}

	/// <summary>
	/// Initializes a new instance of ProcessStreamEngine with the specified IProcessInstance from the Instances library.
	/// The Instances library manages the streams, so we'll use its events instead of reading streams directly.
	/// </summary>
	/// <param name="processInstance">The process instance to monitor.</param>
	/// <exception cref="ArgumentNullException">Thrown when processInstance is null.</exception>
	/// <remarks>
	/// Register event and data capture handlers before calling <see cref="StartMonitoringAsync"/>
	/// so output produced immediately after start is not missed. For fast-exiting processes,
	/// prefer <see cref="CreateDeferredFromInstance"/> so handlers can be registered before the process starts.
	/// </remarks>
	public ProcessStreamEngine(IProcessInstance processInstance)
	{
		if (processInstance == null)
			throw new ArgumentNullException(nameof(processInstance));

		ProcessInstance = processInstance;
		Process = ExtractProcessFromInstance(processInstance);
		ProcessStarted = true;
	}

	/// <summary>
	/// Creates an engine that starts the process when <see cref="StartMonitoringAsync"/> is called,
	/// allowing handlers to be registered first and avoiding missed early output.
	/// </summary>
	/// <param name="startInfo">Process start configuration. RedirectStandardOutput and RedirectStandardError must be true.</param>
	public static ProcessStreamEngine CreateDeferred(ProcessStartInfo startInfo) =>
		new(startInfo);

	/// <summary>
	/// Creates an engine that starts a process via <see cref="Instances.Instance.Start(string, string)"/>
	/// when <see cref="StartMonitoringAsync"/> is called, allowing handlers to be registered first.
	/// </summary>
	/// <param name="fileName">The executable or command to run.</param>
	/// <param name="arguments">Optional command-line arguments.</param>
	public static ProcessStreamEngine CreateDeferredFromInstance(string fileName, string arguments = "") =>
		new(fileName, arguments);

	/// <summary>
	/// Runs a process and returns buffered stdout/stderr text. Does not throw on non-zero exit.
	/// </summary>
	public static async Task<ProcessTextResult> RunTextAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken = default,
		int? timeoutMs = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);

		startInfo.UseShellExecute = false;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;

		using var engine = CreateDeferred(startInfo);
		await engine.StartMonitoringAsync(cancellationToken);
		var exitCode = await engine.WaitForExitAsync(cancellationToken, timeoutMs ?? 30000);
		return new ProcessTextResult(exitCode, engine.StdoutText, engine.StderrText);
	}

	/// <summary>
	/// Runs a process and returns buffered stdout/stderr text. Throws <see cref="ProcessStreamEngineException"/> on non-zero exit.
	/// </summary>
	public static async Task<ProcessTextResult> RunTextOrThrowAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken = default,
		int? timeoutMs = null)
	{
		var result = await RunTextAsync(startInfo, cancellationToken, timeoutMs);
		if (result.ExitCode != 0)
			throw new ProcessStreamEngineException(result.ExitCode, result.StandardError);

		return result;
	}

	/// <summary>
	/// Runs a process with raw byte-stream passthrough for stdin/stdout/stderr.
	/// </summary>
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
				throw new ProcessStreamEngineException(
					exitCode,
					string.Join(Environment.NewLine, stderrLines));
			}

			return exitCode;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			KillProcessIfRunning(process);
			throw;
		}
	}

	/// <summary>
	/// Creates a <see cref="ProcessStartInfo"/> with shell execution disabled and arguments populated via <see cref="ProcessStartInfo.ArgumentList"/>.
	/// </summary>
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
			UseShellExecute = false
		};

		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		if (workingDirectory != null)
			startInfo.WorkingDirectory = workingDirectory;

		return startInfo;
	}

	/// <summary>
	/// Applies environment variables to a <see cref="ProcessStartInfo"/>. Null values remove the variable.
	/// </summary>
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
				source.Dispose();
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
				var line = await ReadLineAsync(stderr, cancellationToken);
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
			// Process may have exited or already killed
		}
	}

	private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
	{
#if NET7_0_OR_GREATER
		return await reader.ReadLineAsync(cancellationToken);
#else
		cancellationToken.ThrowIfCancellationRequested();
		return await reader.ReadLineAsync();
#endif
	}

	private ProcessStreamEngine(ProcessStartInfo startInfo)
	{
		ArgumentNullException.ThrowIfNull(startInfo);

		if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
		{
			throw new ArgumentException(
				"Process must have RedirectStandardOutput and RedirectStandardError set to true.",
				nameof(startInfo));
		}

		Process = new Process { StartInfo = startInfo };
		DeferredStart = true;
	}

	private ProcessStreamEngine(string fileName, string arguments)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));

		DeferredInstanceStart = (fileName, arguments ?? string.Empty);
		DeferredStart = true;
		Process = null!;
	}

	private void EnsureProcessStarted()
	{
		if (ProcessStarted)
			return;

		if (DeferredInstanceStart is { } cmd)
		{
			var instance = Instance.Start(cmd.FileName, cmd.Arguments);
			ProcessInstance = instance;
			Process = ExtractProcessFromInstance(instance);
		}
		else
		{
			Process.Start();
		}

		ProcessStarted = true;
	}

	/// <summary>
	/// Extracts the underlying Process from an IProcessInstance using reflection.
	/// </summary>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IProcessInstance implementations are known and preserved")]
	private static Process ExtractProcessFromInstance(IProcessInstance instance)
	{
		// Try properties first
		var propertyNames = new[] { "Process", "ProcessInstance" };
		foreach (var name in propertyNames)
		{
			var property = instance.GetType().GetProperty(name, 
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			
			if (property != null)
			{
				var process = property.GetValue(instance) as Process;
				if (process != null)
					return process;
			}
		}

		// Try common field patterns
		var fieldNames = new[] { "_process", "process", "Process" };
		foreach (var name in fieldNames)
		{
			var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null)
			{
				var process = field.GetValue(instance) as Process;
				if (process != null)
					return process;
			}
		}

		throw new ArgumentException(
			"Unable to access underlying Process from IProcessInstance. " +
			"The Instances library may have changed its internal structure.",
			nameof(instance));
	}

	/// <summary>
	/// Registers an event handler that will be invoked when the specified pattern matches output.
	/// </summary>
	/// <param name="patternName">A unique name for this event pattern.</param>
	/// <param name="pattern">The regex pattern to match against output lines.</param>
	/// <param name="handler">The delegate to invoke when a match is found. Receives the Match object.</param>
	/// <param name="matchOnErrorStream">If true, matches against stderr; otherwise, matches against stdout.</param>
	public void RegisterEvent(string patternName, string pattern, Action<Match> handler, bool matchOnErrorStream = false)
	{
		if (string.IsNullOrWhiteSpace(patternName))
			throw new ArgumentException("Pattern name cannot be null or empty.", nameof(patternName));
		if (string.IsNullOrWhiteSpace(pattern))
			throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));
		if (handler == null)
			throw new ArgumentNullException(nameof(handler));

		var monitor = GetMonitor(matchOnErrorStream);
		
		monitor.EventPatterns.AddOrUpdate(patternName,
			new EventPattern
			{
				PatternName = patternName,
				Pattern = pattern,
				Regex = new Regex(pattern, RegexOptions.Compiled),
				Handlers = new List<Action<Match>> { handler },
				MatchOnErrorStream = matchOnErrorStream
			},
			(_, existing) =>
			{
				existing.Handlers.Add(handler);
				return existing;
			});
	}

	/// <summary>
	/// Registers a simple string-based event handler (case-sensitive).
	/// </summary>
	/// <param name="patternName">A unique name for this event pattern.</param>
	/// <param name="searchString">The string to search for in output lines.</param>
	/// <param name="handler">The delegate to invoke when the string is found.</param>
	/// <param name="matchOnErrorStream">If true, matches against stderr; otherwise, matches against stdout.</param>
	public void RegisterStringEvent(string patternName, string searchString, Action handler, bool matchOnErrorStream = false)
	{
		if (string.IsNullOrWhiteSpace(patternName))
			throw new ArgumentException("Pattern name cannot be null or empty.", nameof(patternName));
		if (string.IsNullOrWhiteSpace(searchString))
			throw new ArgumentException("Search string cannot be null or empty.", nameof(searchString));
		if (handler == null)
			throw new ArgumentNullException(nameof(handler));

		RegisterEvent(patternName, Regex.Escape(searchString), _ => handler(), matchOnErrorStream);
	}

	/// <summary>
	/// Registers a data capture pattern that will store matching output for later retrieval.
	/// </summary>
	/// <param name="patternName">A unique name for this data capture pattern.</param>
	/// <param name="pattern">The regex pattern to match against output lines.</param>
	/// <param name="captureGroup">Optional named or numbered capture group to extract. If null, captures the entire match.</param>
	/// <param name="matchOnErrorStream">If true, matches against stderr; otherwise, matches against stdout.</param>
	public void RegisterDataCapture(string patternName, string pattern, string? captureGroup = null, bool matchOnErrorStream = false)
	{
		if (string.IsNullOrWhiteSpace(patternName))
			throw new ArgumentException("Pattern name cannot be null or empty.", nameof(patternName));
		if (string.IsNullOrWhiteSpace(pattern))
			throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));

		var monitor = GetMonitor(matchOnErrorStream);
		
		monitor.DataPatterns.TryAdd(patternName, new DataCapturePattern
		{
			Pattern = pattern,
			CaptureGroup = captureGroup,
			Regex = new Regex(pattern, RegexOptions.Compiled)
		});
	}

	/// <summary>
	/// Gets all captured data entries that match the specified pattern name.
	/// </summary>
	/// <param name="patternName">The name of the data capture pattern.</param>
	/// <param name="matchOnErrorStream">If true, retrieves from error stream captures; otherwise, from output stream.</param>
	/// <returns>A collection of captured data strings.</returns>
	public IEnumerable<string> GetCapturedData(string patternName, bool matchOnErrorStream = false)
	{
		var monitor = GetMonitor(matchOnErrorStream);
		if (monitor.CapturedData.TryGetValue(patternName, out var data))
		{
			return data.ToList(); // Return a copy to avoid modification issues
		}
		return Enumerable.Empty<string>();
	}

	/// <summary>
	/// Clears all captured data.
	/// </summary>
	public void ClearCapturedData()
	{
		StdoutMonitor.CapturedData.Clear();
		StderrMonitor.CapturedData.Clear();
	}

	/// <summary>
	/// Clears captured data for a specific pattern.
	/// </summary>
	/// <param name="patternName">The name of the data capture pattern.</param>
	/// <param name="matchOnErrorStream">If true, clears error stream captures; otherwise, output stream captures.</param>
	public void ClearCapturedData(string patternName, bool matchOnErrorStream = false)
	{
		var monitor = GetMonitor(matchOnErrorStream);
		monitor.CapturedData.TryRemove(patternName, out _);
	}

	/// <summary>
	/// Starts monitoring the process output and error streams.
	/// If the process has already exited, remaining stdout/stderr is drained and processed
	/// through the same handlers as live monitoring — no exception is thrown.
	/// </summary>
	/// <param name="cancellationToken">Optional cancellation token to stop monitoring.</param>
	/// <returns>A task that completes when monitoring is set up (or drained for an already-exited process).</returns>
	public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
	{
		StdoutMonitor.ClearOrderedLines();
		StderrMonitor.ClearOrderedLines();

		EnsureProcessStarted();

		var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenSource.Token, cancellationToken);
		var token = linkedCts.Token;

		if (Process.HasExited)
		{
			await DrainRemainingOutputAsync(token);
			return;
		}

		try
		{
			if (ProcessInstance != null)
				await StartInstanceMonitoringAsync(token);
			else
				await StartStreamMonitoringAsync(token);
		}
		catch (InvalidOperationException) when (Process.HasExited)
		{
			// Process exited between the HasExited check and stream/event setup (same pattern as Process.WaitForExitAsync).
			await DrainRemainingOutputAsync(token);
		}
	}

	/// <summary>
	/// Drains any remaining stdout/stderr and feeds lines through console handlers, events, and data capture.
	/// </summary>
	private async Task DrainRemainingOutputAsync(CancellationToken token)
	{
		if (ProcessInstance != null)
		{
			await DrainRemainingInstanceOutputAsync(token);
			return;
		}

		var outputTask = Task.Run(async () =>
		{
			try
			{
				await DrainStreamAsync(Process.StandardOutput, isErrorStream: false, token);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
			catch (InvalidOperationException) when (Process.HasExited)
			{
				// Stream unavailable after exit — best effort
			}
		}, token);

		var errorTask = Task.Run(async () =>
		{
			try
			{
				await DrainStreamAsync(Process.StandardError, isErrorStream: true, token);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
			catch (InvalidOperationException) when (Process.HasExited)
			{
				// Stream unavailable after exit — best effort
			}
		}, token);

		MonitoringTasks.Add(outputTask);
		MonitoringTasks.Add(errorTask);
		await Task.WhenAll(outputTask, errorTask);
	}

	/// <summary>
	/// Drains remaining output from an IProcessInstance's OutputData/ErrorData collections.
	/// </summary>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IProcessInstance implementations are known and preserved")]
	private async Task DrainRemainingInstanceOutputAsync(CancellationToken token)
	{
		var outputDataProperty = ProcessInstance!.GetType().GetProperty("OutputData");
		var errorDataProperty = ProcessInstance.GetType().GetProperty("ErrorData");

		await ProcessExistingInstanceDataAsync(outputDataProperty, errorDataProperty);

		var lastOutputCount = GetDataListCount(outputDataProperty, ProcessInstance);
		var lastErrorCount = GetDataListCount(errorDataProperty, ProcessInstance);

		lastOutputCount = await ProcessNewInstanceDataAsync(
			outputDataProperty, ProcessInstance, lastOutputCount, isErrorStream: false);
		lastErrorCount = await ProcessNewInstanceDataAsync(
			errorDataProperty, ProcessInstance, lastErrorCount, isErrorStream: true);

		var completedTask = Task.CompletedTask;
		MonitoringTasks.Add(completedTask);
		await completedTask;
	}

	/// <summary>
	/// Reads all remaining lines from a stream to end-of-stream and processes them.
	/// </summary>
	private async Task DrainStreamAsync(StreamReader reader, bool isErrorStream, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var line = await ReadLineAsync(reader, cancellationToken);
				if (line == null)
					break;

				await ProcessOutputLineAsync(line, isErrorStream);
			}
		}
		catch (OperationCanceledException)
		{
			// Expected when cancelled
		}
		catch (ObjectDisposedException)
		{
			// Expected when process is disposed
		}
	}

	/// <summary>
	/// Processes a single output line through console streaming, events, and data capture.
	/// </summary>
	private async Task ProcessOutputLineAsync(string line, bool isErrorStream)
	{
		var monitor = GetMonitor(isErrorStream);
		monitor.RecentLines.Add(line);
		monitor.AddOrderedLine(line);
		StreamLineToConsole(line, isErrorStream);
		await ProcessLineAsync(line, isErrorStream);
	}

	/// <summary>
	/// Starts monitoring using IProcessInstance's OutputData/ErrorData collections.
	/// </summary>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IProcessInstance implementations are known and preserved")]
	private async Task StartInstanceMonitoringAsync(CancellationToken token)
	{
		var outputDataProperty = ProcessInstance!.GetType().GetProperty("OutputData");
		var errorDataProperty = ProcessInstance.GetType().GetProperty("ErrorData");

		// Process any existing data immediately
		await ProcessExistingInstanceDataAsync(outputDataProperty, errorDataProperty);

		if (Process.HasExited)
		{
			var lastOutputCount = GetDataListCount(outputDataProperty, ProcessInstance);
			var lastErrorCount = GetDataListCount(errorDataProperty, ProcessInstance);
			await ProcessNewInstanceDataAsync(
				outputDataProperty, ProcessInstance, lastOutputCount, isErrorStream: false);
			await ProcessNewInstanceDataAsync(
				errorDataProperty, ProcessInstance, lastErrorCount, isErrorStream: true);
			MonitoringTasks.Add(Task.CompletedTask);
			return;
		}

		// Start polling task for new data
		var monitorTask = Task.Run(async () =>
		{
			var lastOutputCount = GetDataListCount(outputDataProperty, ProcessInstance);
			var lastErrorCount = GetDataListCount(errorDataProperty, ProcessInstance);

			try
			{
				while (!token.IsCancellationRequested && !Process.HasExited)
				{
					// Check for new output data
					lastOutputCount = await ProcessNewInstanceDataAsync(
						outputDataProperty, ProcessInstance, lastOutputCount, isErrorStream: false);

					// Check for new error data
					lastErrorCount = await ProcessNewInstanceDataAsync(
						errorDataProperty, ProcessInstance, lastErrorCount, isErrorStream: true);

					// Small delay to avoid busy-waiting
					await Task.Delay(10, token);
				}

				// Process any remaining data after process exits
				await ProcessNewInstanceDataAsync(outputDataProperty, ProcessInstance, lastOutputCount, isErrorStream: false);
				await ProcessNewInstanceDataAsync(errorDataProperty, ProcessInstance, lastErrorCount, isErrorStream: true);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
		}, token);

		MonitoringTasks.Add(monitorTask);
	}

	/// <summary>
	/// Starts monitoring by reading directly from Process streams.
	/// </summary>
	private Task StartStreamMonitoringAsync(CancellationToken token)
	{
		var outputTask = Task.Run(async () =>
		{
			try
			{
				await MonitorStreamAsync(Process.StandardOutput, false, token);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
			catch (InvalidOperationException) when (Process.HasExited)
			{
				await DrainStreamAsync(Process.StandardOutput, isErrorStream: false, token);
			}
		}, token);

		var errorTask = Task.Run(async () =>
		{
			try
			{
				await MonitorStreamAsync(Process.StandardError, true, token);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
			catch (InvalidOperationException) when (Process.HasExited)
			{
				await DrainStreamAsync(Process.StandardError, isErrorStream: true, token);
			}
		}, token);

		MonitoringTasks.Add(outputTask);
		MonitoringTasks.Add(errorTask);

		return Task.CompletedTask;
	}

	/// <summary>
	/// Processes existing data from IProcessInstance's OutputData/ErrorData collections.
	/// </summary>
	private async Task ProcessExistingInstanceDataAsync(PropertyInfo? outputDataProperty, PropertyInfo? errorDataProperty)
	{
		if (outputDataProperty != null && outputDataProperty.GetValue(ProcessInstance) is IList<string> outputList)
		{
			await ProcessLinesFromListAsync(outputList, 0, outputList.Count, isErrorStream: false);
		}

		if (errorDataProperty != null && errorDataProperty.GetValue(ProcessInstance) is IList<string> errorList)
		{
			await ProcessLinesFromListAsync(errorList, 0, errorList.Count, isErrorStream: true);
		}
	}

	/// <summary>
	/// Processes new data from an IProcessInstance property and returns the new count.
	/// </summary>
	private async Task<int> ProcessNewInstanceDataAsync(PropertyInfo? dataProperty, IProcessInstance instance, int lastCount, bool isErrorStream)
	{
		if (dataProperty == null)
			return lastCount;

		var data = dataProperty.GetValue(instance);
		if (data is IList<string> dataList)
		{
			var currentCount = dataList.Count;
			if (currentCount > lastCount)
			{
				await ProcessLinesFromListAsync(dataList, lastCount, currentCount, isErrorStream);
				return currentCount;
			}
		}

		return lastCount;
	}

	/// <summary>
	/// Processes lines from a list between the specified indices.
	/// </summary>
	private async Task ProcessLinesFromListAsync(IList<string> lines, int startIndex, int endIndex, bool isErrorStream)
	{
		var monitor = GetMonitor(isErrorStream);

		for (int i = startIndex; i < endIndex; i++)
		{
			var line = lines[i];
			if (!string.IsNullOrWhiteSpace(line))
			{
				monitor.RecentLines.Add(line);
				StreamLineToConsole(line, isErrorStream);
				await ProcessLineAsync(line, isErrorStream);
			}
		}
	}

	/// <summary>
	/// Gets the count of items in a data list property, or 0 if unavailable.
	/// </summary>
	private static int GetDataListCount(PropertyInfo? dataProperty, IProcessInstance instance)
	{
		if (dataProperty != null && dataProperty.GetValue(instance) is IList<string> list)
		{
			return list.Count;
		}
		return 0;
	}


	/// <summary>
	/// Stops monitoring the process streams and cleans up resources.
	/// </summary>
	public void StopMonitoring()
	{
		if (CancellationTokenSource.IsCancellationRequested)
			return;

		CancellationTokenSource.Cancel();
	}

	/// <summary>
	/// Waits for a specific event pattern to be triggered.
	/// </summary>
	/// <param name="patternName">The name of the event pattern to wait for.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="timeoutMs">Optional timeout in milliseconds. Default is 30000 (30 seconds).</param>
	/// <param name="matchOnErrorStream">If true, waits for pattern on stderr; otherwise, on stdout.</param>
	/// <returns>The Match object from the pattern match.</returns>
	/// <exception cref="ArgumentException">Thrown when patternName is not registered.</exception>
	/// <exception cref="TimeoutException">Thrown when the event doesn't occur within the timeout.</exception>
	public async Task<Match> WaitForEventAsync(string patternName, CancellationToken cancellationToken = default, int timeoutMs = 30000, bool matchOnErrorStream = false)
	{
		var monitor = GetMonitor(matchOnErrorStream);
		
		if (!monitor.EventPatterns.ContainsKey(patternName))
		{
			throw new ArgumentException($"Event pattern '{patternName}' is not registered.", nameof(patternName));
		}

		var waiterQueue = monitor.EventWaiters.GetOrAdd(patternName, _ => new ConcurrentQueue<TaskCompletionSource<Match>>());

		var tcs = new TaskCompletionSource<Match>();
		waiterQueue.Enqueue(tcs);

		using var timeoutCts = new CancellationTokenSource(timeoutMs);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
		
		linkedCts.Token.Register(() =>
		{
			if (!tcs.Task.IsCompleted)
			{
				tcs.TrySetException(new TimeoutException($"Event pattern '{patternName}' did not occur within {timeoutMs}ms timeout."));
			}
		});

		return await tcs.Task;
	}

	/// <summary>
	/// Waits for a prompt pattern (common for interactive commands). This is a convenience method.
	/// Registers the pattern and waits for it to appear. If the prompt already appeared in existing output,
	/// it will wait for the next occurrence.
	/// </summary>
	/// <param name="promptPattern">The regex pattern for the prompt. Default is ">\s*$" for nslookup-style prompts (matches ">" at end of line with optional whitespace).</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="timeoutMs">Optional timeout in milliseconds. Default is 30000 (30 seconds).</param>
	/// <returns>The Match object from the prompt match.</returns>
	public async Task<Match> WaitForPromptAsync(string promptPattern = @">\s*$", CancellationToken cancellationToken = default, int timeoutMs = 30000)
	{
		const string promptPatternName = "__wait_for_prompt__";
		
		// Register the prompt pattern if not already registered
		if (!StdoutMonitor.EventPatterns.ContainsKey(promptPatternName))
		{
			RegisterEvent(promptPatternName, promptPattern, _ => { }, matchOnErrorStream: false);
		}

		// Simply delegate to WaitForEventAsync - it handles everything
		return await WaitForEventAsync(promptPatternName, cancellationToken, timeoutMs, matchOnErrorStream: false);
	}

	/// <summary>
	/// Waits for a prompt pattern, then invokes a callback where you can evaluate captured data and determine the next command.
	/// This is useful for interactive command flows where you need to analyze results before sending the next command.
	/// </summary>
	/// <param name="evaluator">A callback that receives the ProcessStreamEngine and should evaluate captured data to determine the next command.
	/// Return the next command to send, or null/empty string to stop waiting for prompts.</param>
	/// <param name="promptPattern">The regex pattern for the prompt. Default is "^>\s*$|>\s*$" for nslookup-style prompts.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="timeoutMs">Optional timeout in milliseconds. Default is 30000 (30 seconds).</param>
	/// <returns>A task that completes when the evaluator returns null/empty or when cancellation/timeout occurs.</returns>
	public async Task WaitForPromptThenEvaluateAsync(
		Func<ProcessStreamEngine, Task<string?>> evaluator,
		string promptPattern = @">\s*$",
		CancellationToken cancellationToken = default,
		int timeoutMs = 30000)
	{
		if (evaluator == null)
			throw new ArgumentNullException(nameof(evaluator));

		while (!cancellationToken.IsCancellationRequested)
		{
			// Wait for the prompt with a shorter timeout per iteration
			// If prompt detection fails, we'll timeout and retry
			try
			{
				await WaitForPromptAsync(promptPattern, cancellationToken, Math.Min(timeoutMs, 5000));
			}
			catch (TimeoutException)
			{
				// If we timeout waiting for prompt, assume we're at prompt and continue
				// This is a workaround for prompt pattern detection issues
			}

			// Once at prompt, invoke the evaluator callback
			var nextCommand = await evaluator(this);

			// If evaluator returns null or empty, we're done
			if (string.IsNullOrWhiteSpace(nextCommand))
			{
				break;
			}

			// Send the command determined by the evaluator
			await SendInputAsync(nextCommand);
		}
	}

	/// <summary>
	/// Waits for a prompt pattern, then invokes a synchronous callback where you can evaluate captured data and determine the next command.
	/// This is useful for interactive command flows where you need to analyze results before sending the next command.
	/// </summary>
	/// <param name="evaluator">A callback that receives the ProcessStreamEngine and should evaluate captured data to determine the next command.
	/// Return the next command to send, or null/empty string to stop waiting for prompts.</param>
	/// <param name="promptPattern">The regex pattern for the prompt. Default is "^>\s*$|>\s*$" for nslookup-style prompts.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="timeoutMs">Optional timeout in milliseconds. Default is 30000 (30 seconds).</param>
	/// <returns>A task that completes when the evaluator returns null/empty or when cancellation/timeout occurs.</returns>
	public async Task WaitForPromptThenEvaluateAsync(
		Func<ProcessStreamEngine, string?> evaluator,
		string promptPattern = @">\s*$",
		CancellationToken cancellationToken = default,
		int timeoutMs = 30000)
	{
		if (evaluator == null)
			throw new ArgumentNullException(nameof(evaluator));

		await WaitForPromptThenEvaluateAsync(
			async (engine) => await Task.FromResult(evaluator(engine)),
			promptPattern,
			cancellationToken,
			timeoutMs);
	}

	/// <summary>
	/// Represents a conditional action that can be taken when a pattern matches.
	/// </summary>
	public class ConditionalAction
	{
		/// <summary>
		/// The pattern name to wait for.
		/// </summary>
		public string PatternName { get; set; } = string.Empty;

		/// <summary>
		/// The regex pattern to match.
		/// </summary>
		public string Pattern { get; set; } = string.Empty;

		/// <summary>
		/// Action to execute when this pattern matches. Receives the Match object and the ProcessStreamEngine.
		/// </summary>
		public Func<Match, ProcessStreamEngine, Task>? Action { get; set; }

		/// <summary>
		/// Whether to match on error stream (stderr) instead of stdout.
		/// </summary>
		public bool MatchOnErrorStream { get; set; }

		/// <summary>
		/// Optional timeout in milliseconds. If null, uses default timeout.
		/// </summary>
		public int? TimeoutMs { get; set; }
	}

	/// <summary>
	/// Waits for one of multiple patterns and executes the corresponding action declaratively.
	/// This allows for conditional command flow based on output patterns.
	/// </summary>
	/// <param name="conditions">Array of conditional actions. The first pattern that matches will trigger its action.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="defaultTimeoutMs">Default timeout in milliseconds if not specified in conditions. Default is 30000 (30 seconds).</param>
	/// <returns>The Match object from the pattern that matched, and the pattern name that matched.</returns>
	/// <exception cref="ArgumentException">Thrown when no conditions are provided or patterns are not registered.</exception>
	/// <exception cref="TimeoutException">Thrown when no pattern matches within the timeout.</exception>
	public async Task<(Match Match, string PatternName)> WaitForAnyConditionAsync(
		ConditionalAction[] conditions,
		CancellationToken cancellationToken = default,
		int defaultTimeoutMs = 30000)
	{
		if (conditions == null || conditions.Length == 0)
		{
			throw new ArgumentException("At least one condition must be provided.", nameof(conditions));
		}

		// Register all patterns if not already registered
		foreach (var condition in conditions)
		{
			var monitor = GetMonitor(condition.MatchOnErrorStream);
			
			if (!monitor.EventPatterns.ContainsKey(condition.PatternName))
			{
				RegisterEvent(condition.PatternName, condition.Pattern, _ => { }, condition.MatchOnErrorStream);
			}
		}

		// Create a task completion source that will be completed when any pattern matches
		var tcs = new TaskCompletionSource<(Match Match, string PatternName)>();

		// Create tasks for each condition
		var tasks = new List<Task<Match>>();
		var patternNames = new List<string>();

		foreach (var condition in conditions)
		{
			var timeout = condition.TimeoutMs ?? defaultTimeoutMs;
			var patternName = condition.PatternName;
			
			var task = WaitForEventAsync(
				patternName,
				cancellationToken,
				timeout,
				condition.MatchOnErrorStream).ContinueWith(t =>
			{
				if (t.IsCompletedSuccessfully && !tcs.Task.IsCompleted)
				{
					tcs.TrySetResult((t.Result, patternName));
					
					// Execute the action if provided
					if (condition.Action != null)
					{
						_ = Task.Run(async () =>
						{
							try
							{
								await condition.Action(t.Result, this);
							}
							catch (Exception ex)
							{
								System.Diagnostics.Debug.WriteLine($"Error executing conditional action for pattern '{patternName}': {ex.Message}");
							}
						});
					}
				}
				return t.Result;
			}, TaskContinuationOptions.ExecuteSynchronously);

			tasks.Add(task);
			patternNames.Add(patternName);
		}

		// Wait for any task to complete
		var completedTask = await Task.WhenAny(tasks);

		if (tcs.Task.IsCompleted)
		{
			return await tcs.Task;
		}

		// If we get here, one task completed but didn't set the result (might be a timeout)
		// Check if any succeeded
		foreach (var task in tasks)
		{
			if (task.IsCompletedSuccessfully && !tcs.Task.IsCompleted)
			{
				var index = tasks.IndexOf(task);
				return (await task, patternNames[index]);
			}
		}

		// If all timed out or failed
		throw new TimeoutException($"None of the conditional patterns matched within the timeout period.");
	}

	/// <summary>
	/// Sends input to the process standard input stream.
	/// </summary>
	/// <param name="input">The input string to send.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <returns>A task representing the async operation.</returns>
	/// <exception cref="InvalidOperationException">Thrown when process input is not redirected.</exception>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IProcessInstance implementations are known and preserved")]
	public async Task SendInputAsync(string input, CancellationToken cancellationToken = default)
	{
		if (DeferredStart && !ProcessStarted)
			throw new InvalidOperationException("Process has not been started. Call StartMonitoringAsync() first.");

		if (Process.HasExited)
			throw new InvalidOperationException("Process has already exited.");

		// If using IProcessInstance, use its SendInput method
		if (ProcessInstance != null)
		{
			var sendInputMethod = ProcessInstance.GetType().GetMethod("SendInput", new[] { typeof(string) });
			if (sendInputMethod != null)
			{
				// Ensure input ends with newline for interactive commands
				var inputToSend = input.EndsWith("\n") || input.EndsWith("\r\n") ? input : input + "\n";
				sendInputMethod.Invoke(ProcessInstance, new object[] { inputToSend });
				await Task.CompletedTask;
				return;
			}
		}

		// Fallback to direct Process access
		if (!Process.StartInfo.RedirectStandardInput)
			throw new InvalidOperationException("Process input is not redirected. Set RedirectStandardInput to true.");

		await Process.StandardInput.WriteLineAsync(input);
		await Process.StandardInput.FlushAsync();
	}

	/// <summary>
	/// Sends raw bytes to the process standard input stream without appending a newline.
	/// </summary>
	/// <param name="data">The raw bytes to send.</param>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <returns>A task representing the async operation.</returns>
	/// <exception cref="InvalidOperationException">Thrown when process input is not redirected.</exception>
	public async Task SendRawInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
	{
		if (DeferredStart && !ProcessStarted)
			throw new InvalidOperationException("Process has not been started. Call StartMonitoringAsync() first.");

		if (Process.HasExited)
			throw new InvalidOperationException("Process has already exited.");

		if (!Process.StartInfo.RedirectStandardInput)
			throw new InvalidOperationException("Process input is not redirected. Set RedirectStandardInput to true.");

		await Process.StandardInput.BaseStream.WriteAsync(data, cancellationToken);
		await Process.StandardInput.FlushAsync();
	}

	/// <summary>
	/// Waits for the process to exit and completes monitoring.
	/// </summary>
	/// <param name="cancellationToken">Optional cancellation token.</param>
	/// <param name="timeoutMs">Optional timeout in milliseconds. Default is 30000 (30 seconds).</param>
	/// <returns>The exit code of the process.</returns>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IProcessInstance implementations are known and preserved")]
	public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default, int timeoutMs = 30000)
	{
		if (DeferredStart && !ProcessStarted)
			throw new InvalidOperationException("Process has not been started. Call StartMonitoringAsync() first.");

		using var timeoutCts = new CancellationTokenSource(timeoutMs);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
		var combinedToken = linkedCts.Token;

		try
		{
			// Check if already exited
			if (Process.HasExited)
			{
				StopMonitoring();
				await Task.WhenAll(MonitoringTasks);
				return Process.ExitCode;
			}

			// If using IProcessInstance, try its WaitForExitAsync method, but fallback to polling if needed
			if (ProcessInstance != null)
			{
				var waitForExitMethod = ProcessInstance.GetType().GetMethod("WaitForExitAsync", new[] { typeof(CancellationToken) });
				if (waitForExitMethod != null)
				{
					try
					{
						var resultTask = waitForExitMethod.Invoke(ProcessInstance, new object[] { combinedToken });
						if (resultTask is Task task)
						{
							// Use Task.WhenAny to add timeout protection
							var timeoutTask = Task.Delay(timeoutMs, combinedToken);
							var completedTask = await Task.WhenAny(task, timeoutTask);
							
							if (completedTask == timeoutTask)
							{
								throw new TimeoutException($"Process did not exit within {timeoutMs}ms timeout.");
							}
							
							await task;
							
							// Wait a bit for monitoring task to process final data
							await Task.Delay(200);
							StopMonitoring();
							await Task.WhenAll(MonitoringTasks);
							
							// Try to get exit code from result
							if (task.GetType().IsGenericType)
							{
								var resultProperty = task.GetType().GetProperty("Result");
								if (resultProperty != null)
								{
									var result = resultProperty.GetValue(task);
									if (result != null)
									{
										var exitCodeProperty = result.GetType().GetProperty("ExitCode");
										if (exitCodeProperty != null)
										{
											return (int)exitCodeProperty.GetValue(result)!;
										}
									}
								}
							}
						}
						else
						{
							// Method returned void, wait for process with timeout
							await Process.WaitForExitAsync(combinedToken);
						}
					}
					catch (Exception)
					{
						// If reflection-based approach fails, fall back to Process.WaitForExitAsync
						await Process.WaitForExitAsync(combinedToken);
					}
				}
				else
				{
					// No WaitForExitAsync method, use Process directly with timeout
					await Process.WaitForExitAsync(combinedToken);
				}
			}
			else
			{
				// Fallback to direct Process access with timeout
				await Process.WaitForExitAsync(combinedToken);
			}
		}
		catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
		{
			// Timeout occurred - force stop monitoring and kill process if needed
			StopMonitoring();
			await Task.WhenAll(MonitoringTasks);
			
			if (!Process.HasExited)
			{
				try
				{
					Process.Kill();
					await Process.WaitForExitAsync(CancellationToken.None);
				}
				catch
				{
					// Process may have exited or already killed
				}
			}
			
			throw new TimeoutException($"Process did not exit within {timeoutMs}ms timeout.");
		}
		
		StopMonitoring();
		await Task.WhenAll(MonitoringTasks);
		return Process.ExitCode;
	}

	protected async Task MonitorStreamAsync(StreamReader reader, bool isErrorStream, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && !Process.HasExited)
			{
				var line = await ReadLineAsync(reader, cancellationToken);
				
				if (line == null)
				{
					// End of stream or process exited
					if (Process.HasExited)
						break;
					
					await Task.Delay(10, cancellationToken);
					continue;
				}

				await ProcessOutputLineAsync(line, isErrorStream);
			}

			if (Process.HasExited)
				await DrainStreamAsync(reader, isErrorStream, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			// Expected when cancelled
		}
		catch (ObjectDisposedException)
		{
			// Expected when process is disposed
		}
		catch (InvalidOperationException) when (Process.HasExited)
		{
			await DrainStreamAsync(reader, isErrorStream, cancellationToken);
		}
	}

	protected async Task ProcessLineAsync(string line, bool isErrorStream)
	{
		var monitor = GetMonitor(isErrorStream);
		var eventPatterns = monitor.EventPatterns;
		var eventWaiters = monitor.EventWaiters;
		var dataPatterns = monitor.DataPatterns;
		var capturedData = monitor.CapturedData;

		// Process event handlers - create a snapshot to avoid modification during iteration
		var patternsSnapshot = eventPatterns.ToList();
		
		foreach (var kvp in patternsSnapshot)
		{
			var match = kvp.Value.Regex.Match(line);
			if (match.Success)
			{
				// Check if there are any waiters for this event pattern
				// Complete waiters BEFORE invoking handlers so handlers can see the match result
				if (eventWaiters.TryGetValue(kvp.Key, out var waiters))
				{
					// Try to dequeue a waiter - complete the first one waiting
					if (waiters.TryDequeue(out var waiter))
					{
						// Complete the first waiting task
						waiter.TrySetResult(match);
					}
				}

				// Invoke registered handlers
				// Create a snapshot of handlers to avoid modification during iteration
				var handlersSnapshot = kvp.Value.Handlers.ToList();
				foreach (var handler in handlersSnapshot)
				{
					try
					{
						await Task.Run(() => handler(match), CancellationToken.None);
					}
					catch (Exception ex)
					{
						// Log or handle exceptions in event handlers
						System.Diagnostics.Debug.WriteLine($"Error in event handler for pattern '{kvp.Value.PatternName}': {ex.Message}");
					}
				}
			}
		}

		// Process data capture patterns - create a snapshot to avoid modification during iteration
		var dataPatternsSnapshot = dataPatterns.ToList();
		foreach (var kvp in dataPatternsSnapshot)
		{
			var match = kvp.Value.Regex.Match(line);
			if (match.Success)
			{
				string capturedValue;
				
				if (!string.IsNullOrEmpty(kvp.Value.CaptureGroup))
				{
					// Try named group first
					if (match.Groups[kvp.Value.CaptureGroup].Success)
					{
						capturedValue = match.Groups[kvp.Value.CaptureGroup].Value;
					}
					// Try numbered group
					else if (int.TryParse(kvp.Value.CaptureGroup, out var groupNumber) && match.Groups.Count > groupNumber)
					{
						capturedValue = match.Groups[groupNumber].Value;
					}
					else
					{
						capturedValue = match.Value;
					}
				}
				else
				{
					capturedValue = match.Value;
				}

				if (!string.IsNullOrWhiteSpace(capturedValue))
				{
					var dataBag = capturedData.GetOrAdd(kvp.Key, _ => new ConcurrentBag<string>());
					dataBag.Add(capturedValue);
				}
			}
		}
	}

	protected class EventPattern
	{
		public string PatternName { get; set; } = string.Empty;
		public string Pattern { get; set; } = string.Empty;
		public Regex Regex { get; set; } = null!;
		public List<Action<Match>> Handlers { get; set; } = new();
		public bool MatchOnErrorStream { get; set; }
	}

	protected class DataCapturePattern
	{
		public string Pattern { get; set; } = string.Empty;
		public string? CaptureGroup { get; set; }
		public Regex Regex { get; set; } = null!;
	}

	/// <summary>
	/// Helper class to manage monitoring for a single stream (stdout or stderr).
	/// Reduces duplication by consolidating all stream-specific data structures.
	/// </summary>
	protected class StreamMonitor
	{
		private readonly object LinesLock = new();

		public bool IsErrorStream { get; }
		public ConcurrentDictionary<string, EventPattern> EventPatterns { get; } = new();
		public ConcurrentDictionary<string, DataCapturePattern> DataPatterns { get; } = new();
		public ConcurrentDictionary<string, ConcurrentBag<string>> CapturedData { get; } = new();
		public ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<Match>>> EventWaiters { get; } = new();
		public ConcurrentBag<string> RecentLines { get; } = new();
		public List<string> OrderedLines { get; } = new();

		public StreamMonitor(bool isErrorStream)
		{
			IsErrorStream = isErrorStream;
		}

		public void AddOrderedLine(string line)
		{
			lock (LinesLock)
				OrderedLines.Add(line);
		}

		public void ClearOrderedLines()
		{
			lock (LinesLock)
				OrderedLines.Clear();
		}

		public string GetJoinedText()
		{
			lock (LinesLock)
				return string.Join(Environment.NewLine, OrderedLines);
		}
	}

	/// <summary>
	/// Cleans up resources. Call this when you're done with the ProcessStreamEngine.
	/// The process itself is not disposed since it's not owned by this class.
	/// </summary>
	public void Cleanup()
	{
		if (Disposed)
			return;

		StopMonitoring();
		
		// Wait a bit for tasks to complete, but don't block indefinitely
		try
		{
			Task.WaitAll(MonitoringTasks.ToArray(), TimeSpan.FromSeconds(1));
		}
		catch
		{
			// Ignore timeout exceptions
		}
		
		CancellationTokenSource.Dispose();
	}

	/// <summary>
	/// Disposes the ProcessStreamEngine and releases resources.
	/// </summary>
	public void Dispose()
	{
		if (Disposed)
			return;

		Cleanup();
		Disposed = true;
		GC.SuppressFinalize(this);
	}
}


