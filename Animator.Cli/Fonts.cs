namespace Animator.Cli;

// A typeface for labels, read straight from a font file (Skia here is built without fontconfig,
// so it can't ask the system): the first of the usual ones found, else any .ttf about. Null = none.
public static class Fonts
{
	private static readonly string[] Preferred = { "DejaVuSans.ttf", "LiberationSans-Regular.ttf", "NotoSans-Regular.ttf", "Arial.ttf", "arial.ttf", "segoeui.ttf" };

	public static SKTypeface? Find()
	{
		var folders = new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts",
			System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
			System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts"),
			Environment.GetFolderPath(Environment.SpecialFolder.Fonts) }
			.Where(f => f.Length > 0 && Directory.Exists(f)).ToList();
		var files = new List<string>();
		foreach (string folder in folders)
		{
			try
			{
				files.AddRange(Directory.EnumerateFiles(folder, "*.ttf", SearchOption.AllDirectories));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		foreach (string file in files.OrderBy(f => Rank(System.IO.Path.GetFileName(f))))
			if (SKTypeface.FromFile(file) is { } face) return face;
		return null;
	}

	private static int Rank(string name)
	{
		int i = Array.IndexOf(Preferred, name);
		return i >= 0 ? i : Preferred.Length + (name.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
	}
}
