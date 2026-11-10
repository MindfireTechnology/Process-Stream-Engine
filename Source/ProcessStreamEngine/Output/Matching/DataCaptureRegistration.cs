using System.Text.RegularExpressions;

namespace ProcessStreamEngine.Output.Matching;

/// <summary>
/// Registers regex-based data capture for later retrieval.
/// </summary>
public sealed record DataCaptureRegistration(
	string Name,
	Regex Pattern,
	OutputStream? Stream,
	string? CaptureGroup);
