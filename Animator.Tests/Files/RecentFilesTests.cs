using System.IO;
using Animator.Viewer.Files;

namespace Animator.Tests;

public class RecentFilesTests
{
	private static string P(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));

	[Fact]
	public void The_latest_comes_first_and_each_file_is_there_once()
	{
		var list = new List<string>();
		RecentFiles.Add(list, P("a.hqnap"));
		RecentFiles.Add(list, P("b.hqnap"));
		RecentFiles.Add(list, P("A.HQNAP"));   // the same file again (Windows paths ignore case)
		Assert.Equal(2, list.Count);
		Assert.Equal(P("A.HQNAP"), list[0]);
		Assert.Equal(P("b.hqnap"), list[1]);
	}

	[Fact]
	public void It_keeps_ten()
	{
		var list = new List<string>();
		for (int i = 0; i < 15; i++) RecentFiles.Add(list, P($"{i}.hqnap"));
		Assert.Equal(RecentFiles.Max, list.Count);
		Assert.Equal(P("14.hqnap"), list[0]);
		Assert.Equal(P("5.hqnap"), list[^1]);
	}

	[Fact]
	public void A_file_can_be_taken_off()
	{
		var list = new List<string>();
		RecentFiles.Add(list, P("a.hqnap"));
		RecentFiles.Add(list, P("b.hqnap"));
		Assert.True(RecentFiles.Remove(list, P("a.hqnap")));
		Assert.False(RecentFiles.Remove(list, P("missing.hqnap")));
		Assert.Equal(new[] { P("b.hqnap") }, list);
	}
}
