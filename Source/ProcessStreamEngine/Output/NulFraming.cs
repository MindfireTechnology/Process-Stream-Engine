using System.Text;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Splits on NUL (0x00), for example <c>git -z</c> output.
/// </summary>
public sealed class NulFraming : IOutputFraming
{
	private readonly StringBuilder Tail = new();

	public bool HasPendingTail => Tail.Length > 0;

	public IReadOnlyList<FramedSegment> Consume(ReadOnlySpan<char> chars, bool endOfStream)
	{
		var results = new List<FramedSegment>();
		var index = 0;

		while (index < chars.Length)
		{
			var start = index;
			while (index < chars.Length && chars[index] != '\0')
				index++;

			if (index > start)
				Tail.Append(chars.Slice(start, index - start));

			if (index < chars.Length)
			{
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
		}

		if (endOfStream && Tail.Length > 0)
		{
			results.Add(new FramedSegment(Tail.ToString(), IsComplete: true));
			Tail.Clear();
		}

		return results;
	}

	public FramedSegment? FlushPartialTail()
	{
		if (Tail.Length == 0)
			return null;

		var text = Tail.ToString();
		Tail.Clear();
		return new FramedSegment(text, IsComplete: false);
	}
}
