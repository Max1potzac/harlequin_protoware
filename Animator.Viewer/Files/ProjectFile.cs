using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Animator.Core.Serialization;
using SkiaSharp;

namespace Animator.Viewer.Files;

public static class ProjectFile
{
	public const string Extension = ".hqnap";
	public const string Filter = "Harlequin Animation Project (*.hqnap)|*.hqnap";
	public const int FormatVersion = 6;   // 2: depth rotation, look constraints. 3: detach/attach keys, events.
	                                      // 4: placement keys. 5: blend modes, part colour and opacity.
	                                      // 6: a scene of objects, each with its own rig and animations

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
		var dto = ProjectMapper.SceneToDto(scene);

		// Write a temporary file first: a crash mid-save can't damage the existing project
		string temp = path + ".saving";
		using (var file = File.Create(temp))
		using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
		{
			WriteJson(zip, "manifest.json", new Manifest());
			WriteJson(zip, "project.json", dto);

			foreach (var asset in scene.Objects.SelectMany(o => o.Skeleton.Assets.Values))
				foreach (var layer in asset.Layers)
					if (store.GetLayer(layer) is { } bmp)
						WritePng(zip, $"layers/{layer.Id}.png", bmp);

			if (thumbnail != null) WritePng(zip, "thumbnail.png", thumbnail);
		}

		File.Move(temp, path, overwrite: true);   // only now replace the real file
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