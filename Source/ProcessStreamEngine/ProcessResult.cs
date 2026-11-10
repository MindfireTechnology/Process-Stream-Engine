namespace ProcessStreamEngine;

/// <summary>
/// Outcome of a completed process session.
/// </summary>
public sealed record ProcessResult(int ExitCode)
{
	public string? StandardOutput { get; init; }
	public string? StandardError { get; init; }
}

/// <summary>
/// Buffered text output from a one-shot process run.
/// </summary>
public sealed record ProcessTextResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Stream passthrough options for <see cref="ProcessRunner.RunWithStreamsAsync"/>.
/// </summary>
public sealed record ProcessStreamOptions
{
	public Stream? StandardInput { get; init; }
	public Stream? StandardOutput { get; init; }
	public Stream? StandardError { get; init; }
	public bool MonitorStderrAsLines { get; init; }
	public bool LeaveStreamsOpen { get; init; }
}
