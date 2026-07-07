using Shouldly;

namespace ProcessStreamEngine.Tests.Exception;

public class ProcessStreamEngineExceptionTests
{
	[Fact]
	public void Constructor_SetsExitCodeAndStandardError()
	{
		var exception = new ProcessStreamEngineException(128, "fatal: not a git repository");

		exception.ExitCode.ShouldBe(128);
		exception.StandardError.ShouldBe("fatal: not a git repository");
		exception.Message.ShouldContain("128");
		exception.Message.ShouldContain("fatal: not a git repository");
	}
}
