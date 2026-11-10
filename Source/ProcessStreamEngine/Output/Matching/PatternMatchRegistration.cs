using System.Text.RegularExpressions;

namespace ProcessStreamEngine.Output.Matching;

/// <summary>
/// Registers a regex handler for a named pattern on a specific stream.
/// </summary>
public sealed record PatternMatchRegistration(
	string Name,
	Regex Pattern,
	OutputStream? Stream,
	Func<Match, CancellationToken, Task> Handler);
