using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ProcessStreamEngine.Interaction;
using ProcessStreamEngine.Output.Matching;

namespace ProcessStreamEngine.Output;

/// <summary>
/// Runs pattern handlers and data capture against each published line.
/// </summary>
public sealed class OutputMatcherEngine
{
	private readonly List<PatternMatchRegistration> PatternHandlers = new();
	private readonly List<DataCaptureRegistration> CaptureHandlers = new();
	private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<Match>>> ExpectWaiters = new();
	private readonly ConcurrentDictionary<string, ConcurrentBag<string>> CapturedData = new();

	public void RegisterPattern(PatternMatchRegistration registration) =>
		PatternHandlers.Add(registration);

	public void RegisterCapture(DataCaptureRegistration registration) =>
		CaptureHandlers.Add(registration);

	public IReadOnlyList<string> GetCapturedData(string name)
	{
		if (CapturedData.TryGetValue(name, out var bag))
			return bag.ToArray();

		return Array.Empty<string>();
	}

	public void ClearCapturedData() => CapturedData.Clear();

	public Task<Match> ExpectAsync(
		Regex pattern,
		OutputStream? stream,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var name = $"__expect_{Guid.NewGuid():N}";
		PatternHandlers.Add(new PatternMatchRegistration(
			name,
			pattern,
			stream,
			(_, _) => Task.CompletedTask));

		return WaitForNamedExpectAsync(name, pattern, timeout, cancellationToken);
	}

	private async Task<Match> WaitForNamedExpectAsync(
		string name,
		Regex pattern,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var queue = ExpectWaiters.GetOrAdd(name, _ => new ConcurrentQueue<TaskCompletionSource<Match>>());
		var tcs = new TaskCompletionSource<Match>(TaskCreationOptions.RunContinuationsAsynchronously);
		queue.Enqueue(tcs);

		using var timeoutCts = new CancellationTokenSource(timeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
		using var registration = linked.Token.Register(() =>
		{
			if (tcs.Task.IsCompleted)
				return;

			if (timeoutCts.IsCancellationRequested)
				tcs.TrySetException(new ExpectTimeoutException(name, name, timeout));
			else
				tcs.TrySetCanceled(linked.Token);
		});

		return await tcs.Task;
	}

	internal void ProcessLine(OutputLine line)
	{
		var text = line.Text;
		foreach (var handler in PatternHandlers)
		{
			if (handler.Stream is { } stream && stream != line.Stream)
				continue;

			var match = handler.Pattern.Match(text);
			if (!match.Success)
				continue;

			if (ExpectWaiters.TryGetValue(handler.Name, out var waiters)
				&& waiters.TryDequeue(out var waiter))
			{
				waiter.TrySetResult(match);
			}

			_ = InvokeHandlerAsync(handler, match);
		}

		foreach (var capture in CaptureHandlers)
		{
			if (capture.Stream is { } stream && stream != line.Stream)
				continue;

			var match = capture.Pattern.Match(text);
			if (!match.Success)
				continue;

			var value = ExtractCapture(match, capture.CaptureGroup);
			if (!string.IsNullOrWhiteSpace(value))
			{
				var bag = CapturedData.GetOrAdd(capture.Name, _ => new ConcurrentBag<string>());
				bag.Add(value);
			}
		}
	}

	private static string ExtractCapture(Match match, string? captureGroup)
	{
		if (string.IsNullOrEmpty(captureGroup))
			return match.Value;

		if (match.Groups[captureGroup].Success)
			return match.Groups[captureGroup].Value;

		if (int.TryParse(captureGroup, out var index) && match.Groups.Count > index)
			return match.Groups[index].Value;

		return match.Value;
	}

	private static async Task InvokeHandlerAsync(PatternMatchRegistration registration, Match match)
	{
		try
		{
			await registration.Handler(match, CancellationToken.None);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(
				$"Pattern handler '{registration.Name}' failed: {ex.Message}");
		}
	}
}
