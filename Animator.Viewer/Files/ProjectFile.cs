using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Animator.Core.Serialization;
using SkiaSharp;

namespace Animator.Viewer.Files;

public static class ProjectFile
{
	public const string Extension = ".hqnap";
	public const string Filter = "Harlequin Animation Project (*.hqnap)|*.hqnap";
	public const int FormatVersion = 8;   // 2: depth rotation, look constraints. 3: detach/attach keys, events.
	                                      // 4: placement keys. 5: blend modes, part colour and opacity.
	                                      // 6: a scene of objects, each with its own rig and animations
	                                      // 7: the scene timeline (clips), project kind, stage size
	                                      // 8: draw order keys

	private sealed class Manifest
	{
		public string Format { get; set; } = "hqnap";
		public int Version { get; set; } = FormatVersion;
		public string App { get; set; } = "Harlequin";
		public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
	}

	public sealed record LoadedProject(Scene Scene, AssetStore Store);

	// ======================= save =======================

	public static void Save(string path, Scene scene, AssetStore store, SKBitmap? thumbnail)
	{
		using var snapshot = Take(scene, store, thumbnail, copyPixels: false);
		Write(path, snapshot);
	}

	// Everything a save writes, taken from the project now. With copied pixels it can be written
	// later, on another thread, while the project goes on changing (the autosave does that).
	public sealed class Snapshot : IDisposable
	{
		internal ProjectDto Dto { get; init; } = new();
		internal List<(string Name, SKBitmap Pixels, bool Own)> Pictures { get; } = new();

		public void Dispose()
		{
			foreach (var (_, pixels, own) in Pictures)
				if (own) pixels.Dispose();
			Pictures.Clear();
		}
	}

	public static Snapshot Take(Scene scene, AssetStore store, SKBitmap? thumbnail, bool copyPixels)
	{
		var snapshot = new Snapshot { Dto = ProjectMapper.SceneToDto(scene) };
		foreach (var asset in scene.Objects.SelectMany(o => o.Skeleton.Assets.Values))
			foreach (var layer in asset.Layers)
				if (store.GetLayer(layer) is { } bmp)
					snapshot.Pictures.Add(Picture($"layers/{layer.Id}.png", bmp, copyPixels));
		if (thumbnail != null) snapshot.Pictures.Add(Picture("thumbnail.png", thumbnail, copyPixels));
		return snapshot;
	}

	private static (string, SKBitmap, bool) Picture(string name, SKBitmap bitmap, bool copy) =>
		copy && bitmap.Copy() is { } own ? (name, own, true) : (name, bitmap, false);

	public static void Write(string path, Snapshot snapshot)
	{
		// Write a temporary file first: a crash mid-save can't damage the existing project
		string temp = path + ".saving";
		using (var file = File.Create(temp))
		using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
		{
			WriteJson(zip, "manifest.json", new Manifest());
			WriteJson(zip, "project.json", snapshot.Dto);
			foreach (var (name, pixels, _) in snapshot.Pictures)
				WritePng(zip, name, pixels);
		}

		File.Move(temp, path, overwrite: true);   // only now replace the real file
	}

	// Saving over a project keeps the version that was there: in a Backups folder beside it, named
	// after it and when that version was saved, the newest `keep` of them (older ones go)
	public static void KeepBackup(string path, int keep)
	{
		if (keep <= 0 || !File.Exists(path)) return;
		string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Backups");
		string name = Path.GetFileNameWithoutExtension(path);
		string ext = Path.GetExtension(path);
		Directory.CreateDirectory(folder);
		string stamp = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
		File.Copy(path, Path.Combine(folder, $"{name} {stamp}{ext}"), overwrite: true);

		foreach (var file in Backups(path).Skip(keep)) file.Delete();
	}

	// A project's backups (see KeepBackup), the newest first
	public static List<FileInfo> Backups(string path)
	{
		string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Backups");
		if (!Directory.Exists(folder)) return new List<FileInfo>();
		string name = Path.GetFileNameWithoutExtension(path);
		string ext = Path.GetExtension(path);
		var stamped = new Regex("^" + Regex.Escape(name) + @" \d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2}" + Regex.Escape(ext) + "$",
								RegexOptions.IgnoreCase);
		return new DirectoryInfo(folder).GetFiles(name + " *" + ext)
			.Where(f => stamped.IsMatch(f.Name))   // not another project whose name starts the same way
			.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)   // the date in the name sorts them
			.ToList();
	}

	private static void WriteJson<T>(ZipArchive zip, string name, T value)
	{
		var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		using var stream = entry.Open();
		JsonSerializer.Serialize(stream, value, ProjectMapper.Json);
	}

	private static void WritePng(ZipArchive zip, string name, SKBitmap bitmap)
	{
		// PNGs are already compressed; zipping them again only costs time
		var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
		using var stream = entry.Open();
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 100);
		data.SaveTo(stream);
	}

	// ======================= load =======================

	// Reads everything into NEW objects. Throws on any problem, without touching the open project.
	public static LoadedProject Load(string path)
	{
		using var zip = ZipFile.OpenRead(path);

		var manifest = ReadJson<Manifest>(zip, "manifest.json")
			?? throw new InvalidDataException("This file isn't a Harlequin project (no manifest).");
		if (manifest.Format != "hqnap")
			throw new InvalidDataException($"Unknown project format '{manifest.Format}'.");
		if (manifest.Version > FormatVersion)
			throw new InvalidDataException(
				$"This project was saved by a newer version of Harlequin (format {manifest.Version}). Please update.");

		var dto = ReadJson<ProjectDto>(zip, "project.json")
			?? throw new InvalidDataException("The project data (project.json) is missing.");

		var scene = ProjectMapper.SceneFromDto(dto);

		var store = new AssetStore();
		foreach (var asset in scene.Objects.SelectMany(o => o.Skeleton.Assets.Values))
			foreach (var layer in asset.Layers)
			{
				using var pixels = ReadPng(zip, $"layers/{layer.Id}.png");   // missing = empty layer
				store.AttachLayer(asset, layer, pixels);
			}

		return new LoadedProject(scene, store);
	}

	private static T? ReadJson<T>(ZipArchive zip, string name)
	{
		var entry = zip.GetEntry(name);
		if (entry == null) return default;
		using var stream = entry.Open();
		return JsonSerializer.Deserialize<T>(stream, ProjectMapper.Json);
	}

	private static SKBitmap? ReadPng(ZipArchive zip, string name)
	{
		var entry = zip.GetEntry(name);
		if (entry == null) return null;

		// Zip streams can't jump around, which some decoders need, so copy into memory first
		using var stream = entry.Open();
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		memory.Position = 0;
		return SKBitmap.Decode(memory);
	}
}