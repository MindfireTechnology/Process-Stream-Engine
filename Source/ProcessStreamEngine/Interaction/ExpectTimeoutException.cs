namespace ProcessStreamEngine.Interaction;

/// <summary>
/// Thrown when <see cref="ProcessSession.ExpectAsync"/> does not observe a match within the timeout.
/// </summary>
public sealed class ExpectTimeoutException : ProcessStreamEngineException
{
	public string PatternName { get; }
	public TimeSpan Elapsed { get; }

	public ExpectTimeoutException(string patternName, string patternDescription, TimeSpan elapsed)
		: base($"Expected pattern '{patternName}' did not match within {elapsed.TotalMilliseconds}ms.")
	{
		PatternName = patternName;
		Elapsed = elapsed;
	}
}
