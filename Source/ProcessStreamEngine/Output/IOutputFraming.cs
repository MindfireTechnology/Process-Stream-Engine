namespace ProcessStreamEngine.Output;

/// <summary>
/// Splits a decoded character stream into framed output segments.
/// </summary>
public interface IOutputFraming
{
	/// <summary>
	/// Consumes newly decoded characters and returns complete frames. The framing implementation may retain a tail buffer.
	/// </summary>
	IReadOnlyList<FramedSegment> Consume(ReadOnlySpan<char> chars, bool endOfStream);
}

/// <summary>
/// A segment emitted by framing logic.
/// </summary>
/// <param name="Text">Segment text without delimiters.</param>
/// <param name="IsComplete">True when closed by a delimiter; false for flushed tails.</param>
public readonly record struct FramedSegment(string Text, bool IsComplete);
