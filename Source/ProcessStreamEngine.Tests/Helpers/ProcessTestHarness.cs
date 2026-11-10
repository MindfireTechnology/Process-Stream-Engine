using System.Diagnostics;

namespace ProcessStreamEngine.Tests.Helpers;

internal static class ProcessTestHarness
{
	public static ProcessStartInfo CreateRedirectedStartInfo(IEnumerable<string> arguments, bool redirectInput = false)
	{
		var startInfo = ProcessRunner.CreateStartInfo(ShellCommand.Executable, arguments);
		if (redirectInput)
			startInfo.RedirectStandardInput = true;
		return startInfo;
	}

	public static ProcessSession StartSession(
		IEnumerable<string> arguments,
		Action<ProcessSessionBuilder>? configure = null,
		bool redirectInput = false,
		bool keepTranscript = true)
	{
		var startInfo = CreateRedirectedStartInfo(arguments, redirectInput);
		var builder = ProcessSessionBuilder.Create(startInfo);
		if (keepTranscript)
			builder.KeepTranscript();
		configure?.Invoke(builder);
		return builder.Start();
	}

}
