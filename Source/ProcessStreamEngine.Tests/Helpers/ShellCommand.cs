namespace ProcessStreamEngine.Tests.Helpers;

using System.Diagnostics;

internal static class ShellCommand
{
	public static string Executable => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

	public static IReadOnlyList<string> Echo(string text) =>
		OperatingSystem.IsWindows()
			? ["/c", "echo", text]
			: ["-c", $"echo {text}"];

	public static IReadOnlyList<string> EchoStderr(string text) =>
		OperatingSystem.IsWindows()
			? ["/c", $"echo {text} 1>&2"]
			: ["-c", $"echo {text} >&2"];

	public static IReadOnlyList<string> ExitWith(int code) =>
		OperatingSystem.IsWindows()
			? ["/c", $"exit /b {code}"]
			: ["-c", $"exit {code}"];

	public static IReadOnlyList<string> EchoStdoutAndStderr(string stdout, string stderr) =>
		OperatingSystem.IsWindows()
			? ["/c", $"echo {stdout}&echo {stderr} 1>&2"]
			: ["-c", $"echo {stdout}; echo {stderr} >&2"];

	public static IReadOnlyList<string> FailWithStderr(string message, int code) =>
		OperatingSystem.IsWindows()
			? ["/c", $"echo {message} 1>&2 & exit /b {code}"]
			: ["-c", $"echo {message} >&2; exit {code}"];

	/// <summary>
	/// Writes out-1..out-N to stdout and err-1..err-N to stderr, interleaved, then exits.
	/// </summary>
	public static IReadOnlyList<string> EchoNumberedLines(int count) =>
		OperatingSystem.IsWindows()
			? ["/c", $"for /L %i in (1,1,{count}) do @(echo out-%i&>&2 echo err-%i)"]
			: ["-c", $"i=1; while [ $i -le {count} ]; do echo out-$i; echo err-$i >&2; i=$((i+1)); done"];

	public static IReadOnlyList<string> SleepForever() =>
		OperatingSystem.IsWindows()
			? ["/c", "ping", "127.0.0.1", "-n", "61", ">", "nul"]
			: ["-c", "sleep 60"];

	public static ProcessStartInfo PowerShell(string command) =>
		new()
		{
			FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
			Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
}
