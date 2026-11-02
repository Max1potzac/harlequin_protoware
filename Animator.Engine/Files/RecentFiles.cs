using System.IO;
namespace Animator.Viewer.Files;

// File → Open recent: the last projects opened or saved, the latest first, each once (the same
// file however its path was written), at most Max of them
public static class RecentFiles
{
	public const int Max = 10;

	public static void Add(List<string> list, string path)
	{
		string full = Full(path);
		list.RemoveAll(p => Same(p, full));
		list.Insert(0, full);
		if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
	}

	public static bool Remove(List<string> list, string path) => list.RemoveAll(p => Same(p, path)) > 0;

	private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

	private static string Full(string path)
	{
		try { return Path.GetFullPath(path); }
		catch (Exception) { return path; }   // (a path that can't be made full: kept as it is)
	}
}
