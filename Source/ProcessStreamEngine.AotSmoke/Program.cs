using ProcessStreamEngine;

var startInfo = ProcessRunner.CreateStartInfo(
	OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
	OperatingSystem.IsWindows() ? ["/c", "echo", "aot-smoke"] : ["-c", "echo aot-smoke"]);

var result = await ProcessRunner.RunTextAsync(startInfo, timeoutMs: 15000);
Console.WriteLine(result.ExitCode);
Console.WriteLine(result.StandardOutput.Trim());
