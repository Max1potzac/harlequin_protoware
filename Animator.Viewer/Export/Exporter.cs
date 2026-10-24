using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using SkiaSharp;

namespace Animator.Viewer.Export;

public enum ExportFormat { PngSequence, Gif, SpriteSheet, Video }

// What to export and how (filled in by the Export dialog)
public sealed class ExportSettings
{
	public bool Scene { get; set; } = true;               // the scene timeline; false = one object's animations
	public SceneObject? Object { get; set; }
	public List<Animation> Animations { get; set; } = new();
	public ExportFormat Format { get; set; } = ExportFormat.PngSequence;
	public string Path { get; set; } = "";                // the file chosen; sequences and sheets name theirs after it

	public float Scale { get; set; } = 1f;                // output pixels per scene unit
	public bool PixelArt { get; set; }                    // no smoothing (crisp pixels)
	public SKColor? Background { get; set; }              // null = see-through
	public int FrameStep { get; set; } = 1;               // every frame, every 2nd…
	public int FirstFrame { get; set; }                   // scene: the range to export
	public int LastFrame { get; set; } = -1;              // -1 = to the end
	public bool UseStage { get; set; } = true;            // scene: just the stage (else everything that shows)

	public int Columns { get; set; }                      // sprite sheet: 0 = automatic
	public bool RowPerAnimation { get; set; } = true;
	public int Padding { get; set; } = 2;

	public bool Dither { get; set; }                      // GIF
	public bool Loop { get; set; } = true;                // GIF
	public string? FfmpegPath { get; set; }               // video
}

public readonly record struct ExportProgress(int Done, int Total, string Step);

// Renders frames of the scene, or of one object's animations, and writes them as PNGs, GIFs, a
// sprite sheet or a video. Run() works one frame at a time and reports after each, so the
// window can stay responsive and offer Cancel (stopping early removes what was written).
public sealed class Exporter
{
	// A run of frames: its name (for file names and the sheet), the times, how to pose for a
	// time, which objects to draw and through which transform
	private sealed record Sequence(string Name, List<float> Times, Action<float> Pose,
		IReadOnlyList<SceneObject> Objects, Func<SceneObject, Matrix3x2> Transform, bool Loop);

	private readonly Scene _scene;
	private readonly ExportSettings _settings;
	private readonly SkeletonRenderer _renderer;
	private readonly List<Sequence> _sequences;
	private readonly int _fps;
	private readonly float _scale;
	private readonly List<string> _written = new();

	// The part of the scene (or object) that's exported, y up, and the picture's size
	private float _minX, _maxY;
	private int _width, _height;

	public Exporter(Scene scene, AssetStore assets, int fps, ExportSettings settings)
	{
		_scene = scene;
		_settings = settings;
		_fps = Math.Max(1, fps);
		_scale = Math.Clamp(settings.Scale, 0.01f, 100f);
		_renderer = new SkeletonRenderer(assets) { PixelArt = settings.PixelArt };
		_sequences = BuildSequences();
	}

	public int FrameCount => _sequences.Sum(s => s.Times.Count);
	public IReadOnlyList<string> Written => _written;
	public (int Width, int Height) Size => (_width, _height);

	// ======================= what to draw =======================

	private List<Sequence> BuildSequences()
	{
		var list = new List<Sequence>();
		int step = Math.Max(1, _settings.FrameStep);

		if (_settings.Scene)
		{
			int end = (int)MathF.Round(_scene.Duration * _fps) - (_scene.Loop ? 1 : 0);   // a loop's last frame is its first
			int last = _settings.LastFrame >= 0 ? Math.Min(_settings.LastFrame, end) : end;
			var times = new List<float>();
			for (int f = Math.Max(0, _settings.FirstFrame); f <= last; f += step) times.Add(f / (float)_fps);
			var shown = _scene.Objects.Where(o => o.Visible && o.Export).ToList();
			list.Add(new Sequence("scene", times, t => ScenePlayer.Pose(_scene, t), shown, _scene.WorldTransform, _scene.Loop));
			return list;
		}

		if (_settings.Object is not { } obj) return list;
		foreach (var animation in _settings.Animations)
		{
			int end = (int)MathF.Round(animation.Duration * _fps) - (animation.Loop ? 1 : 0);
			var times = new List<float>();
			for (int f = 0; f <= Math.Max(0, end); f += step) times.Add(f / (float)_fps);
			// In the object's own space: where it stands in the scene doesn't matter to a game
			list.Add(new Sequence(animation.Name, times, t => ScenePlayer.Pose(obj, animation, t),
				new[] { obj }, _ => Matrix3x2.Identity, animation.Loop));
		}
		return list;
	}

	// The stage, or everything the frames show (with the object's origin where it falls)
	private void FindArea()
	{
		float minX, minY, maxX, maxY;
		if (_settings.Scene && _settings.UseStage && _scene.HasStage)
		{
			minX = -_scene.StageWidth / 2f;
			maxX = _scene.StageWidth / 2f;
			minY = -_scene.StageHeight / 2f;
			maxY = _scene.StageHeight / 2f;
		}
		else
		{
			minX = minY = float.MaxValue;
			maxX = maxY = float.MinValue;
			foreach (var seq in _sequences)
				foreach (float t in seq.Times)
				{
					seq.Pose(t);
					foreach (var obj in seq.Objects)
					{
						if (_renderer.ImageBounds(obj.Skeleton, margin: 0) is not { } box) continue;
						var m = seq.Transform(obj);
						foreach (var corner in new[] { new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top),
													   new Vector2(box.Right, box.Bottom), new Vector2(box.Left, box.Bottom) })
						{
							var p = Vector2.Transform(corner, m);
							minX = MathF.Min(minX, p.X);
							minY = MathF.Min(minY, p.Y);
							maxX = MathF.Max(maxX, p.X);
							maxY = MathF.Max(maxY, p.Y);
						}
					}
				}
			if (minX > maxX) throw new InvalidOperationException("Nothing to export: these frames show no pictures.");
			// Whole units, so pixel art lands on whole pixels
			minX = MathF.Floor(minX);
			minY = MathF.Floor(minY);
			maxX = MathF.Ceiling(maxX);
			maxY = MathF.Ceiling(maxY);
		}

		_minX = minX;
		_maxY = maxY;
		_width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) * _scale));
		_height = Math.Max(1, (int)MathF.Ceiling((maxY - minY) * _scale));
		if (_settings.Format == ExportFormat.Video)
		{
			_width += _width % 2;   // videos need even sizes
			_height += _height % 2;
		}
		if (_width > SpriteSheetLayout.MaxSize || _height > SpriteSheetLayout.MaxSize)
			throw new InvalidOperationException($"The picture would be {_width} × {_height} pixels: too big. Use a smaller scale.");
	}

	// Where the object's origin is in each picture, in pixels from the top-left
	private Vector2 OriginPixel => new(-_minX * _scale, _maxY * _scale);

	// Poses and draws one frame onto a canvas whose (0, 0) is the picture's top-left corner
	private void DrawFrame(SKCanvas canvas, Sequence seq, float time)
	{
		seq.Pose(time);
		canvas.Save();
		canvas.Scale(_scale, -_scale);   // the scene's y points up, pictures' down
		canvas.Translate(-_minX, -_maxY);
		foreach (var obj in seq.Objects)
		{
			var m = SkeletonRenderer.ToSk(seq.Transform(obj));
			canvas.Save();
			canvas.Concat(in m);
			_renderer.DrawImages(canvas, obj.Skeleton);
			canvas.Restore();
		}
		canvas.Restore();
	}

	private SKBitmap NewPicture(int width, int height) =>
		new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

	private void Render(SKBitmap picture, Sequence seq, float time)
	{
		using var canvas = new SKCanvas(picture);
		canvas.Clear(_settings.Background ?? SKColors.Transparent);
		DrawFrame(canvas, seq, time);
	}

	// ======================= running =======================

	public IEnumerable<ExportProgress> Run()
	{
		if (FrameCount == 0) throw new InvalidOperationException("Nothing to export: no frames (pick at least one animation).");
		bool finished = false;
		try
		{
			yield return new ExportProgress(0, FrameCount, "Measuring");
			FindArea();
			var steps = _settings.Format switch
			{
				ExportFormat.Gif => WriteGifs(),
				ExportFormat.SpriteSheet => WriteSheet(),
				ExportFormat.Video => WriteVideo(),
				_ => WritePngs()
			};
			foreach (var p in steps) yield return p;
			finished = true;
		}
		finally
		{
			if (!finished)
				foreach (var file in _written)
					TryDelete(file);
		}
	}

	private static void TryDelete(string file)
	{
		try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	// The chosen file's folder and name, with the animation's name added for an object's animations
	private string BaseName(Sequence seq)
	{
		string folder = System.IO.Path.GetDirectoryName(_settings.Path) ?? "";
		string name = System.IO.Path.GetFileNameWithoutExtension(_settings.Path);
		if (!_settings.Scene) name += "_" + Safe(seq.Name);
		return System.IO.Path.Combine(folder, name);
	}

	private static string Safe(string name)
	{
		var invalid = System.IO.Path.GetInvalidFileNameChars();
		var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
		return s.Length == 0 ? "animation" : s;
	}

	private void Track(string file) => _written.Add(file);

	private static void SavePng(SKBitmap bitmap, string file)
	{
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 100);
		using var stream = File.Create(file);
		data.SaveTo(stream);
	}

	// ----- PNG sequence: name_0000.png, name_0001.png …

	private IEnumerable<ExportProgress> WritePngs()
	{
		using var picture = NewPicture(_width, _height);
		int done = 0;
		foreach (var seq in _sequences)
			for (int i = 0; i < seq.Times.Count; i++)
			{
				Render(picture, seq, seq.Times[i]);
				string file = $"{BaseName(seq)}_{i:D4}.png";
				Track(file);
				SavePng(picture, file);
				yield return new ExportProgress(++done, FrameCount, $"Frame {done} of {FrameCount}");
			}
	}

	// ----- Sprite sheet: one PNG with every frame in a grid, and a JSON describing it

	private IEnumerable<ExportProgress> WriteSheet()
	{
		int padding = Math.Max(0, _settings.Padding);
		var (cells, width, height) = SpriteSheetLayout.Arrange(
			_sequences.Select(s => s.Times.Count).ToList(), _width, _height,
			_settings.Columns, _settings.RowPerAnimation, padding);
		if (width > SpriteSheetLayout.MaxSize || height > SpriteSheetLayout.MaxSize)
			throw new InvalidOperationException(
				$"The sheet would be {width} × {height} pixels: too big. Use a smaller scale, fewer frames, or more columns.");

		using var sheet = NewPicture(width, height);
		using var canvas = new SKCanvas(sheet);
		canvas.Clear(SKColors.Transparent);

		var json = new SpriteSheetJson();
		int done = 0;
		for (int s = 0; s < _sequences.Count; s++)
		{
			var seq = _sequences[s];
			int first = json.Frames.Count;
			for (int i = 0; i < seq.Times.Count; i++)
			{
				var (x, y) = cells[s][i];
				canvas.Save();
				canvas.ClipRect(new SKRect(x, y, x + _width, y + _height));
				canvas.Translate(x, y);
				if (_settings.Background is { } background) canvas.DrawColor(background, SKBlendMode.Src);   // just this cell
				DrawFrame(canvas, seq, seq.Times[i]);
				canvas.Restore();

				json.Frames.Add(new SheetFrame
				{
					Filename = $"{seq.Name} {i}",
					Frame = new SheetRect { X = x, Y = y, W = _width, H = _height },
					SpriteSourceSize = new SheetRect { X = 0, Y = 0, W = _width, H = _height },
					SourceSize = new SheetSize { W = _width, H = _height },
					Duration = FrameMilliseconds(i)
				});
				yield return new ExportProgress(++done, FrameCount, $"Frame {done} of {FrameCount}");
			}
			if (seq.Times.Count > 0)
				json.Meta.FrameTags.Add(new SheetTag { Name = seq.Name, From = first, To = json.Frames.Count - 1, Loop = seq.Loop });
		}

		string png = BaseNameForSheet() + ".png", data = BaseNameForSheet() + ".json";
		json.Meta.Image = System.IO.Path.GetFileName(png);
		json.Meta.Size = new SheetSize { W = width, H = height };
		json.Meta.Scale = _scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
		json.Meta.Origin = new SheetPoint { X = OriginPixel.X, Y = OriginPixel.Y };

		Track(png);
		SavePng(sheet, png);
		Track(data);
		File.WriteAllText(data, json.ToJson());
	}

	private string BaseNameForSheet() => System.IO.Path.Combine(
		System.IO.Path.GetDirectoryName(_settings.Path) ?? "", System.IO.Path.GetFileNameWithoutExtension(_settings.Path));

	// Frame i's share of the time, rounded so the durations add up right
	private int FrameMilliseconds(int i)
	{
		float each = 1000f * Math.Max(1, _settings.FrameStep) / _fps;
		return (int)MathF.Round((i + 1) * each) - (int)MathF.Round(i * each);
	}

	// ----- GIF: one per animation (or one for the scene)

	private IEnumerable<ExportProgress> WriteGifs()
	{
		int total = FrameCount * 2, done = 0;   // every frame is drawn twice: for the palette, then for the file
		using var picture = NewPicture(_width, _height);
		var pixels = new byte[_width * _height * 4];
		var previous = new byte[pixels.Length];
		float framesPerSecond = _fps / (float)Math.Max(1, _settings.FrameStep);
		int sampleStep = Math.Max(1, _width * _height / 250_000);   // big pictures: sample the colours

		foreach (var seq in _sequences)
		{
			// Pass 1: the colours, and runs of identical frames (each run becomes one GIF frame
			// that lasts as long as the run)
			var palette = new GifPalette();
			var runs = new List<(int Frame, int Delay)>();
			for (int i = 0; i < seq.Times.Count; i++)
			{
				Render(picture, seq, seq.Times[i]);
				ReadStraight(picture, pixels);
				int delay = GifWriter.Delay(i, framesPerSecond);
				if (runs.Count > 0 && pixels.AsSpan().SequenceEqual(previous))
					runs[^1] = (runs[^1].Frame, runs[^1].Delay + delay);
				else
				{
					palette.Add(pixels, sampleStep);
					runs.Add((i, delay));
					(pixels, previous) = (previous, pixels);
				}
				yield return new ExportProgress(++done, total, $"Colours: frame {i + 1} of {seq.Times.Count}");
			}
			palette.Build();

			// Pass 2: the file
			string file = BaseName(seq) + ".gif";
			Track(file);
			using (var stream = File.Create(file))
			using (var gif = new GifWriter(stream, _width, _height, palette, _settings.Loop, _settings.Dither))
			{
				int run = 0;
				for (int i = 0; i < seq.Times.Count; i++)
				{
					if (run < runs.Count && runs[run].Frame == i)
					{
						Render(picture, seq, seq.Times[i]);
						ReadStraight(picture, pixels);
						gif.AddFrame(pixels, runs[run].Delay);
						run++;
					}
					yield return new ExportProgress(++done, total, $"Writing: frame {i + 1} of {seq.Times.Count}");
				}
			}
		}
	}

	// The picture's pixels as straight (not premultiplied) RGBA
	private static void ReadStraight(SKBitmap picture, byte[] into)
	{
		picture.GetPixelSpan().CopyTo(into);
		for (int i = 0; i < into.Length; i += 4)
		{
			int a = into[i + 3];
			if (a == 0 || a == 255) continue;
			into[i] = (byte)Math.Min(255, into[i] * 255 / a);
			into[i + 1] = (byte)Math.Min(255, into[i + 1] * 255 / a);
			into[i + 2] = (byte)Math.Min(255, into[i + 2] * 255 / a);
		}
	}

	// ----- Video: MP4 (H.264) through FFmpeg, fed one frame at a time

	private static string Text(StringBuilder errors)
	{
		lock (errors) return errors.ToString().Trim();
	}

	private IEnumerable<ExportProgress> WriteVideo()
	{
		string ffmpeg = _settings.FfmpegPath is { Length: > 0 } path ? path : "ffmpeg";
		string file = System.IO.Path.ChangeExtension(_settings.Path, ".mp4");
		float rate = _fps / (float)Math.Max(1, _settings.FrameStep);
		var info = new ProcessStartInfo(ffmpeg)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (string arg in new[]
		{
			"-y", "-hide_banner", "-loglevel", "error",
			"-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{_width}x{_height}",
			"-r", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", "-",
			"-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", "-movflags", "+faststart", file
		})
			info.ArgumentList.Add(arg);

		Process process;
		try
		{
			process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg didn't start.");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			throw new InvalidOperationException(
				"Videos are made with FFmpeg, a free program, and it wasn't found. Download it (ffmpeg.org), " +
				"then pick ffmpeg.exe in the Export dialog.");
		}

		var errors = new StringBuilder();
		process.ErrorDataReceived += (_, e) =>
		{
			lock (errors)
				if (e.Data != null && errors.Length < 4000) errors.AppendLine(e.Data);
		};
		process.BeginErrorReadLine();
		Track(file);

		bool closed = false;
		try
		{
			using var picture = NewPicture(_width, _height);
			var pixels = new byte[_width * _height * 4];
			var input = process.StandardInput.BaseStream;
			int done = 0;
			foreach (var seq in _sequences)
				foreach (float t in seq.Times)
				{
					using (var canvas = new SKCanvas(picture))
					{
						canvas.Clear(_settings.Background is { Alpha: 255 } c ? c : SKColors.Black);   // videos aren't see-through
						DrawFrame(canvas, seq, t);
					}
					picture.GetPixelSpan().CopyTo(pixels);
					try
					{
						input.Write(pixels, 0, pixels.Length);
					}
					catch (IOException)
					{
						throw new InvalidOperationException("FFmpeg stopped: " + Text(errors));
					}
					yield return new ExportProgress(++done, FrameCount, $"Frame {done} of {FrameCount}");
				}

			input.Close();
			closed = true;
			process.WaitForExit();
			if (process.ExitCode != 0)
				throw new InvalidOperationException("FFmpeg couldn't make the video: " + Text(errors));
		}
		finally
		{
			if (!closed)
			{
				// Stopped half way: end FFmpeg so the half-written file can be removed
				try { process.StandardInput.Close(); } catch (IOException) { }
				try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
				process.WaitForExit(2000);
			}
			process.Dispose();
		}
	}
}
