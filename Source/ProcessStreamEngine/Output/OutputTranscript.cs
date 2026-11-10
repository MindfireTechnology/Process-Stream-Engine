using System.Text;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Stores exact decoded text per stream, including original line endings and delimiters.
/// </summary>
public sealed class OutputTranscript
{
	private readonly StringBuilder StdoutBuilder = new();
	private readonly StringBuilder StderrBuilder = new();
	private readonly object Gate = new();

	public void Append(OutputStream stream, ReadOnlySpan<char> chars)
	{
		lock (Gate)
		{
			var builder = stream == OutputStream.StandardOutput ? StdoutBuilder : StderrBuilder;
			builder.Append(chars);
		}
	}

	public string GetText(OutputStream stream)
	{
		lock (Gate)
		{
			return stream == OutputStream.StandardOutput
				? StdoutBuilder.ToString()
				: StderrBuilder.ToString();
		}
	}

	public string StandardOutput => GetText(OutputStream.StandardOutput);

	public string StandardError => GetText(OutputStream.StandardError);
}
