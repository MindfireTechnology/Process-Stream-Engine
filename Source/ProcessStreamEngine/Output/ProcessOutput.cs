using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Public surface for subscribing to process output from a <see cref="ProcessSession"/>.
/// </summary>
public sealed class ProcessOutput
{
	private readonly OutputHub Hub;

	internal ProcessOutput(OutputHub hub)
	{
		Hub = hub;
	}

	public IAsyncEnumerable<OutputLine> ReadAllAsync(
		int capacity = OutputHub.DefaultSubscriberCapacity,
		BackpressurePolicy backpressure = BackpressurePolicy.Wait,
		CancellationToken cancellationToken = default) =>
		Hub.ReadAllAsync(capacity, backpressure, cancellationToken);

	public IDisposable Subscribe(IObserver<OutputLine> observer) =>
		Hub.Subscribe(observer);

	public OutputSubscription SubscribeChannel(
		int capacity = OutputHub.DefaultSubscriberCapacity,
		BackpressurePolicy backpressure = BackpressurePolicy.Wait) =>
		Hub.Subscribe(capacity, backpressure);

	public IReadOnlyList<string> GetCapturedData(string name) =>
		Hub.MatcherEngine.GetCapturedData(name);

	public Task<Match> ExpectAsync(
		Regex pattern,
		OutputStream? stream = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default) =>
		Hub.MatcherEngine.ExpectAsync(
			pattern,
			stream,
			timeout ?? TimeSpan.FromSeconds(30),
			cancellationToken);
}
