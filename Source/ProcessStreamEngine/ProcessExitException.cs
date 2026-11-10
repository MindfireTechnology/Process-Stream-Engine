namespace ProcessStreamEngine;

/// <summary>
/// Thrown when a process exits with a non-zero code and stderr is available.
/// </summary>
public sealed class ProcessExitException : ProcessStreamEngineException
{
	public int ExitCode { get; }
	public string StandardErrorTail { get; }

	public ProcessExitException(int exitCode, string standardErrorTail)
		: base($"Process exited with code {exitCode}.")
	{
		ExitCode = exitCode;
		StandardErrorTail = standardErrorTail;
	}
}
