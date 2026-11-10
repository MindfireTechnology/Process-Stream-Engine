namespace ProcessStreamEngine;

/// <summary>
/// Thrown when a process or its output streams do not complete within a timeout.
/// </summary>
public sealed class ProcessTimeoutException : ProcessStreamEngineException
{
	public ProcessTimeoutPhase Phase { get; }
	public int TimeoutMs { get; }

	public ProcessTimeoutException(ProcessTimeoutPhase phase, int timeoutMs)
		: base(phase == ProcessTimeoutPhase.Exit
			? $"Process did not exit within {timeoutMs}ms."
			: $"Process exited but output streams did not finish within {timeoutMs}ms.")
	{
		Phase = phase;
		TimeoutMs = timeoutMs;
	}
}

/// <summary>
/// Which phase timed out while waiting for a process session to finish.
/// </summary>
public enum ProcessTimeoutPhase
{
	Exit = 0,
	Drain = 1
}
