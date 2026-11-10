using Shouldly;

namespace ProcessStreamEngine.Tests.Exception;

public class ExceptionTests
{
	[Fact]
	public void ProcessExitException_ExposesExitCode()
	{
		var ex = new ProcessExitException(5, "err");
		ex.ExitCode.ShouldBe(5);
		ex.StandardErrorTail.ShouldBe("err");
	}
}
