namespace ProcessStreamEngine.Output;

/// <summary>
/// A framed unit of process output. <see cref="IsComplete"/> is false for delimiter-free tails (e.g. prompts).
/// </summary>
/// <param name="Text">Line or segment text without delimiters.</param>
/// <param name="Stream">Stdout or stderr.</param>
/// <param name="Sequence">Approximate arrival order across both pipes.</param>
/// <param name="IsComplete">True when a framing delimiter was seen; false for a quiet-period partial tail.</param>
public readonly record struct OutputLine(
	string Text,
	OutputStream Stream,
	long Sequence,
	bool IsComplete);
