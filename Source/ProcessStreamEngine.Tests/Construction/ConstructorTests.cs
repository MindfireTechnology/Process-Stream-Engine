using System.Diagnostics;
using ProcessStreamEngine.Tests.Helpers;
using Shouldly;

namespace ProcessStreamEngine.Tests.Construction;

public class ConstructorTests
{
	[Fact]
	public void Constructor_ThrowsWhenProcessIsNull()
	{
		Should.Throw<ArgumentNullException>(() => new ProcessStreamEngine((Process)null!));
	}

	[Fact]
	public void Constructor_ThrowsWhenOutputNotRedirected()
	{
		var process = new System.Diagnostics.Process
		{
			StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
			{
				RedirectStandardError = true,
				RedirectStandardOutput = false,
				UseShellExecute = false
			}
		};

		Should.Throw<ArgumentException>(() => new ProcessStreamEngine(process));
	}

	[Fact]
	public void CreateDeferred_ThrowsWhenOutputNotRedirected()
	{
		var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
		{
			RedirectStandardOutput = false,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		Should.Throw<ArgumentException>(() => ProcessStreamEngine.CreateDeferred(startInfo));
	}

	[Fact]
	public void CreateDeferredFromInstance_ThrowsWhenFileNameEmpty()
	{
		Should.Throw<ArgumentException>(() =>
			ProcessStreamEngine.CreateDeferredFromInstance(""));
	}
}
