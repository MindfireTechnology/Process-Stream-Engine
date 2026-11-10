using System.Diagnostics;
using System.Text.RegularExpressions;
using ProcessStreamEngine.Output;
using ProcessStreamEngine.Output.Matching;

namespace ProcessStreamEngine;

/// <summary>
/// Configures a <see cref="ProcessSession"/> before the process starts.
/// </summary>
public sealed class ProcessSessionBuilder
{
	private readonly ProcessStartInfo? StartInfo;
	private readonly Process? AttachedProcess;
	private IOutputFraming Framing = new LineFraming();
	private bool KeepTranscriptBacking;
	private bool KillOnCancelBacking = true;
	private TimeSpan? PartialTailQuietPeriodBacking;
	private readonly List<PatternMatchRegistration> Patterns = new();
	private readonly List<DataCaptureRegistration> Captures = new();

	private ProcessSessionBuilder(ProcessStartInfo startInfo)
	{
		StartInfo = startInfo;
	}

	private ProcessSessionBuilder(Process process)
	{
		AttachedProcess = process;
	}

	public static ProcessSessionBuilder Create(ProcessStartInfo startInfo)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		return new(startInfo);
	}

	public static ProcessSessionBuilder Attach(Process process) =>
		new(process);

	public ProcessSessionBuilder WithFraming(IOutputFraming framing)
	{
		Framing = framing;
		return this;
	}

	public ProcessSessionBuilder UseLineFraming() =>
		WithFraming(new LineFraming());

	public ProcessSessionBuilder UseNulFraming() =>
		WithFraming(new NulFraming());

	public ProcessSessionBuilder KeepTranscript()
	{
		KeepTranscriptBacking = true;
		return this;
	}

	public ProcessSessionBuilder KillOnCancel(bool enabled = true)
	{
		KillOnCancelBacking = enabled;
		return this;
	}

	public ProcessSessionBuilder PartialTailQuietPeriod(TimeSpan period)
	{
		PartialTailQuietPeriodBacking = period;
		return this;
	}

	public ProcessSessionBuilder OnMatch(
		string name,
		Regex pattern,
		Func<Match, CancellationToken, Task> handler,
		OutputStream? stream = null)
	{
		Patterns.Add(new PatternMatchRegistration(name, pattern, stream, handler));
		return this;
	}

	public ProcessSessionBuilder OnMatch(
		string name,
		Regex pattern,
		Action<Match> handler,
		OutputStream? stream = null) =>
		OnMatch(name, pattern, (match, _) =>
		{
			handler(match);
			return Task.CompletedTask;
		}, stream);

	public ProcessSessionBuilder OnMatch(
		string name,
		string pattern,
		Action<Match> handler,
		OutputStream? stream = null) =>
		OnMatch(name, new Regex(pattern), handler, stream);

	public ProcessSessionBuilder Capture(
		string name,
		Regex pattern,
		string? captureGroup = null,
		OutputStream? stream = null)
	{
		Captures.Add(new DataCaptureRegistration(name, pattern, stream, captureGroup));
		return this;
	}

	public ProcessSessionBuilder Capture(
		string name,
		string pattern,
		string? captureGroup = null,
		OutputStream? stream = null) =>
		Capture(name, new Regex(pattern), captureGroup, stream);

	public ProcessSession Start()
	{
		Process process;
		var ownsProcess = false;

		if (AttachedProcess is not null)
		{
			process = AttachedProcess;
			EnsureRedirects(process.StartInfo);
		}
		else
		{
			ArgumentNullException.ThrowIfNull(StartInfo);
			EnsureRedirects(StartInfo);
			StartInfo.UseShellExecute = false;
			process = new Process { StartInfo = StartInfo };
			process.Start();
			ownsProcess = true;
		}

		var hub = new OutputHub();
		foreach (var pattern in Patterns)
			hub.MatcherEngine.RegisterPattern(pattern);
		foreach (var capture in Captures)
			hub.MatcherEngine.RegisterCapture(capture);

		var transcript = KeepTranscriptBacking ? new OutputTranscript() : null;

		return new ProcessSession(
			process,
			ownsProcess,
			hub,
			transcript,
			Framing,
			KillOnCancelBacking,
			PartialTailQuietPeriodBacking);
	}

	private static void EnsureRedirects(ProcessStartInfo startInfo)
	{
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
	}
}
