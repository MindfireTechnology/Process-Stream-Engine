using System.Threading.Channels;

namespace ProcessStreamEngine.Output;

/// <summary>
/// A subscriber queue over process output. Dispose to release the queue.
/// </summary>
public sealed class OutputSubscription : IAsyncDisposable, IDisposable
{
	public Channel<OutputLine> Channel { get; }
	private readonly OutputHub Hub;

	internal OutputSubscription(OutputHub hub, Channel<OutputLine> channel)
	{
		Hub = hub;
		Channel = channel;
	}

	public void Dispose()
	{
		Hub.RemoveSubscription(this);
		GC.SuppressFinalize(this);
	}

	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}
}
