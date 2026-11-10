using System.Diagnostics;

namespace ProcessStreamEngine.Interaction;

/// <summary>
/// Writes to a process stdin.
/// </summary>
public sealed class ProcessInput
{
	private readonly Process Process;
	private readonly CancellationToken CancellationToken;

	public ProcessInput(Process process, CancellationToken cancellationToken)
	{
		Process = process;
		CancellationToken = cancellationToken;
	}

	public async Task WriteLineAsync(string text, CancellationToken cancellationToken = default)
	{
		EnsureWritable();
		var token = cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken;
		await Process.StandardInput.WriteLineAsync(text.AsMemory(), token);
		await Process.StandardInput.FlushAsync(token);
	}

	public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
	{
		EnsureWritable();
		var token = cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken;
		await Process.StandardInput.BaseStream.WriteAsync(data, token);
		await Process.StandardInput.FlushAsync(token);
	}

	public async Task CopyFromAsync(Stream source, bool leaveOpen, CancellationToken cancellationToken = default)
	{
		EnsureWritable();
		var token = cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken;
		try
		{
			await source.CopyToAsync(Process.StandardInput.BaseStream, token);
		}
		finally
		{
			if (!leaveOpen)
				await source.DisposeAsync();
		}
	}

	public Task CompleteAsync()
	{
		EnsureWritable();
		Process.StandardInput.Close();
		return Task.CompletedTask;
	}

	private void EnsureWritable()
	{
		if (Process.HasExited)
			throw new InvalidOperationException("Process has already exited.");

		if (!Process.StartInfo.RedirectStandardInput)
			throw new InvalidOperationException("Process input is not redirected.");
	}
}
