namespace Animator.Cli;

// A project opened (or started) by a command, and saved back where it came from
public sealed class Project
{
	public string Path { get; }
	public Scene Scene { get; }
	public AssetStore Assets { get; }

	private Project(string path, Scene scene, AssetStore assets)
	{
		Path = path;
		Scene = scene;
		Assets = assets;
	}

	public static Project Open(string path)
	{
		if (!File.Exists(path)) throw new FileNotFoundException($"There's no project at {path}.");
		var loaded = ProjectFile.Load(path);
		return new Project(path, loaded.Scene, loaded.Store);
	}

	public static Project OpenOrNew(string path)
	{
		if (File.Exists(path)) return Open(path);
		if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
			throw new UsageException($"A new project's name ends in {ProjectFile.Extension}.");
		Console.WriteLine($"New project: {path}");
		return new Project(path, new Scene(), new AssetStore());
	}

	public SceneObject Object(string name) =>
		Scene.Find(name) ?? throw new UsageException($"There's no object \"{name}\" (there's {string.Join(", ", Scene.Objects.Select(o => o.Name))}).");

	public Skin Skin(SceneObject obj, string name) =>
		obj.Skeleton.FindSkin(name) ?? throw new UsageException($"{obj.Name} has no skin \"{name}\".");

	// Written to a file beside it first, then put in its place, so a failed save leaves the old one
	public void Save()
	{
		string full = System.IO.Path.GetFullPath(Path);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
		string temporary = full + ".saving";
		using (var thumbnail = Snapshots.Thumbnail(Scene, new SkeletonRenderer(Assets), 256))
			ProjectFile.Save(temporary, Scene, Assets, thumbnail);
		File.Move(temporary, full, overwrite: true);
		Console.WriteLine($"Saved {Path}.");
	}
}
