namespace ProcessStreamEngine.Output;

/// <summary>
/// How a subscriber's bounded queue behaves when full.
/// </summary>
public enum BackpressurePolicy
{
	/// <summary>
	/// Block the publisher until space is available (default). Slows the child when it fills pipes.
	/// </summary>
	Wait = 0,

	/// <summary>
	/// Drop the oldest queued item to make room for the newest line.
	/// </summary>
	DropOldest = 1
}
