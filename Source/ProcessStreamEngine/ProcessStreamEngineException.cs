namespace ProcessStreamEngine;

/// <summary>
/// Base exception for ProcessStreamEngine failures.
/// </summary>
public class ProcessStreamEngineException : Exception
{
	public ProcessStreamEngineException(string message) : base(message) { }

	public ProcessStreamEngineException(string message, Exception innerException)
		: base(message, innerException) { }
}
