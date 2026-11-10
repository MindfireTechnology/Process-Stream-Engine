using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Fan-out hub: each subscriber has an isolated bounded channel.
/// </summary>
public sealed class OutputHub
{
	public const int DefaultSubscriberCapacity = 256;

	private long Sequence;
	private readonly List<OutputSubscription> Subscriptions = new();
	private readonly object SubscriptionGate = new();
	private int Publishing;

	public OutputMatcherEngine MatcherEngine { get; } = new();

	public Task PublishAsync(OutputLine line, CancellationToken cancellationToken)
	{
		var sequenced = line with { Sequence = Interlocked.Increment(ref Sequence) };
		MatcherEngine.ProcessLine(sequenced);

		OutputSubscription[] snapshot;
		lock (SubscriptionGate)
			snapshot = Subscriptions.ToArray();

		Interlocked.Increment(ref Publishing);

		try
		{
			var waitTasks = new List<Task>(snapshot.Length);
			foreach (var subscription in snapshot)
			{
				waitTasks.Add(WriteToSubscriptionAsync(subscription, sequenced, cancellationToken));
			}

			return Task.WhenAll(waitTasks);
		}
		finally
		{
			Interlocked.Decrement(ref Publishing);
		}
	}

	public OutputSubscription Subscribe(
		int capacity = DefaultSubscriberCapacity,
		BackpressurePolicy backpressure = BackpressurePolicy.Wait)
	{
		var options = new BoundedChannelOptions(capacity)
		{
			FullMode = backpressure == BackpressurePolicy.DropOldest
				? BoundedChannelFullMode.DropOldest
				: BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false
		};

		var channel = Channel.CreateBounded<OutputLine>(options);
		var subscription = new OutputSubscription(this, channel);
		lock (SubscriptionGate)
			Subscriptions.Add(subscription);

		return subscription;
	}

	public async IAsyncEnumerable<OutputLine> ReadAllAsync(
		int capacity = DefaultSubscriberCapacity,
		BackpressurePolicy backpressure = BackpressurePolicy.Wait,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await using var subscription = Subscribe(capacity, backpressure);
		await foreach (var line in subscription.Channel.Reader.ReadAllAsync(cancellationToken))
			yield return line;
	}

	public IDisposable Subscribe(IObserver<OutputLine> observer)
	{
		ArgumentNullException.ThrowIfNull(observer);
		var subscription = Subscribe();
		var registration = Task.Run(async () =>
		{
			try
			{
				await foreach (var line in subscription.Channel.Reader.ReadAllAsync())
					observer.OnNext(line);
				observer.OnCompleted();
			}
			catch (Exception ex)
			{
				observer.OnError(ex);
			}
		});

		return new ObserverSubscription(subscription, registration);
	}

	public async Task CompleteAllAsync()
	{
		OutputSubscription[] snapshot;
		lock (SubscriptionGate)
			snapshot = Subscriptions.ToArray();

		foreach (var subscription in snapshot)
			subscription.Channel.Writer.TryComplete();

		await WaitForDrainedAsync(CancellationToken.None);
	}

	public Task WaitForDrainedAsync(CancellationToken cancellationToken)
	{
		return Task.Run(async () =>
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				if (Volatile.Read(ref Publishing) == 0)
				{
					OutputSubscription[] snapshot;
					lock (SubscriptionGate)
						snapshot = Subscriptions.ToArray();

					var allEmpty = true;
					foreach (var sub in snapshot)
					{
						if (sub.Channel.Reader.Count > 0)
						{
							allEmpty = false;
							break;
						}
					}

					if (allEmpty)
						return;
				}

				await Task.Delay(5, cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();
		}, cancellationToken);
	}

	internal void RemoveSubscription(OutputSubscription subscription)
	{
		lock (SubscriptionGate)
			Subscriptions.Remove(subscription);

		subscription.Channel.Writer.TryComplete();
	}

	private static async Task WriteToSubscriptionAsync(
		OutputSubscription subscription,
		OutputLine line,
		CancellationToken cancellationToken)
	{
		await subscription.Channel.Writer.WriteAsync(line, cancellationToken);
	}

	private sealed class ObserverSubscription : IDisposable
	{
		private readonly OutputSubscription Subscription;
		private readonly Task PumpTask;

		public ObserverSubscription(OutputSubscription subscription, Task pumpTask)
		{
			Subscription = subscription;
			PumpTask = pumpTask;
		}

		public void Dispose()
		{
			Subscription.Dispose();
			_ = PumpTask;
		}
	}
}
