using System.Buffers;
using System.Text;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Owns a single process output stream and publishes framed <see cref="OutputLine"/> values.
/// </summary>
public sealed class OutputReader
{
	private const int DefaultBufferSize = 4096;
	private static readonly TimeSpan DefaultPartialTailQuietPeriod = TimeSpan.FromMilliseconds(100);

	private readonly Stream Source;
	private readonly OutputStream StreamKind;
	private readonly IOutputFraming Framing;
	private readonly OutputHub Hub;
	private readonly OutputTranscript? Transcript;
	private readonly TimeSpan PartialTailQuietPeriod;
	private readonly Encoding Encoding;

	public OutputReader(
		Stream source,
		OutputStream streamKind,
		IOutputFraming framing,
		OutputHub hub,
		OutputTranscript? transcript,
		TimeSpan? partialTailQuietPeriod = null,
		Encoding? encoding = null)
	{
		Source = source;
		StreamKind = streamKind;
		Framing = framing;
		Hub = hub;
		Transcript = transcript;
		PartialTailQuietPeriod = partialTailQuietPeriod ?? DefaultPartialTailQuietPeriod;
		Encoding = encoding ?? Encoding.UTF8;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		var decoder = Encoding.GetDecoder();
		var byteBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
		var charBuffer = ArrayPool<char>.Shared.Rent(Encoding.GetMaxCharCount(DefaultBufferSize));

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var bytesRead = await Source.ReadAsync(byteBuffer.AsMemory(0, DefaultBufferSize), cancellationToken);
				if (bytesRead == 0)
					break;

				var charCount = decoder.GetChars(byteBuffer, 0, bytesRead, charBuffer, 0);
				var chars = charBuffer.AsSpan(0, charCount);
				Transcript?.Append(StreamKind, chars);

				foreach (var segment in Framing.Consume(chars, endOfStream: false))
					await PublishSegmentAsync(segment, cancellationToken);

				SchedulePartialTailFlush(cancellationToken);
			}

			var finalCharCount = decoder.GetChars(ReadOnlySpan<byte>.Empty, charBuffer, false);
			if (finalCharCount > 0)
			{
				var tailChars = charBuffer.AsSpan(0, finalCharCount);
				Transcript?.Append(StreamKind, tailChars);
				foreach (var segment in Framing.Consume(tailChars, endOfStream: true))
					await PublishSegmentAsync(segment, cancellationToken);
			}
			else
			{
				foreach (var segment in Framing.Consume(ReadOnlySpan<char>.Empty, endOfStream: true))
					await PublishSegmentAsync(segment, cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(byteBuffer);
			ArrayPool<char>.Shared.Return(charBuffer);
		}
	}

	private int PartialFlushScheduled;

	private void SchedulePartialTailFlush(CancellationToken cancellationToken)
	{
		if (Framing is not LineFraming and not NulFraming)
			return;

		var hasTail = Framing switch
		{
			LineFraming line => line.HasPendingTail,
			NulFraming nul => nul.HasPendingTail,
			_ => false
		};

		if (!hasTail)
			return;

		if (Interlocked.CompareExchange(ref PartialFlushScheduled, 1, 0) != 0)
			return;

		_ = TryEmitQuietPartialTailAsync(cancellationToken);
	}

	private async Task TryEmitQuietPartialTailAsync(CancellationToken cancellationToken)
	{
		if (Framing is not LineFraming and not NulFraming)
			return;

		var hasTail = Framing switch
		{
			LineFraming line => line.HasPendingTail,
			NulFraming nul => nul.HasPendingTail,
			_ => false
		};

		if (!hasTail)
			return;

		try
		{
			await Task.Delay(PartialTailQuietPeriod, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		var partial = Framing switch
		{
			LineFraming line => line.FlushPartialTail(),
			NulFraming nul => nul.FlushPartialTail(),
			_ => null
		};

		if (partial is { } segment && segment.Text.Length > 0)
			await PublishSegmentAsync(segment, cancellationToken);

		Interlocked.Exchange(ref PartialFlushScheduled, 0);
	}

	private Task PublishSegmentAsync(FramedSegment segment, CancellationToken cancellationToken)
	{
		if (!segment.IsComplete && segment.Text.Length == 0)
			return Task.CompletedTask;

		if (segment.IsComplete && segment.Text.Length == 0)
			return Hub.PublishAsync(new OutputLine(string.Empty, StreamKind, 0, true), cancellationToken);

		if (segment.Text.Length == 0)
			return Task.CompletedTask;

		return Hub.PublishAsync(
			new OutputLine(segment.Text, StreamKind, 0, segment.IsComplete),
			cancellationToken);
	}
}
