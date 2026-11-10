using System.Text;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Splits on CR, LF, or CRLF.
/// </summary>
public sealed class LineFraming : IOutputFraming
{
	private readonly StringBuilder Tail = new();

	public bool HasPendingTail => Tail.Length > 0;

	public IReadOnlyList<FramedSegment> Consume(ReadOnlySpan<char> chars, bool endOfStream)
	{
		var results = new List<FramedSegment>();
		var index = 0;

		while (index < chars.Length)
		{
			var lineStart = index;
			while (index < chars.Length && chars[index] is not '\r' and not '\n')
				index++;

			if (index > lineStart)
				Tail.Append(chars.Slice(lineStart, index - lineStart));

			if (index >= chars.Length)
				break;

			var hadCr = chars[index] == '\r';
			index++;
			if (hadCr && index < chars.Length && chars[index] == '\n')
				index++;

			if (Tail.Length > 0)
			{
				results.Add(new FramedSegment(Tail.ToString(), IsComplete: true));
				Tail.Clear();
			}
			else
			{
				results.Add(new FramedSegment(string.Empty, IsComplete: true));
			}
		}

		if (endOfStream && Tail.Length > 0)
		{
			results.Add(new FramedSegment(Tail.ToString(), IsComplete: true));
			Tail.Clear();
		}

		return results;
	}

	/// <summary>
	/// Flushes a non-empty tail as an incomplete segment (e.g. prompt without trailing newline).
	/// </summary>
	public FramedSegment? FlushPartialTail()
	{
		if (Tail.Length == 0)
			return null;

		var text = Tail.ToString();
		Tail.Clear();
		return new FramedSegment(text, IsComplete: false);
	}
}
