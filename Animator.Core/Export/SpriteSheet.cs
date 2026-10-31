using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animator.Core.Export;

// Where each frame goes on a sprite sheet: cells of one size in a grid, `padding` pixels apart
// (and from the edges)
public static class SpriteSheetLayout
{
	public const int MaxSize = 16384;   // pixels each way; bigger sheets are hard to load anywhere

	// counts: how many frames each animation has. columns 0 = automatic (one row per animation:
	// as many as the longest one; otherwise a roughly square sheet). rowPerAnimation: each
	// animation starts on a new row.
	public static (List<List<(int X, int Y)>> Cells, int Width, int Height) Arrange(
		IReadOnlyList<int> counts, int cellWidth, int cellHeight, int columns, bool rowPerAnimation, int padding)
	{
		int total = counts.Sum();
		if (columns <= 0)
			columns = rowPerAnimation
				? Math.Max(1, counts.DefaultIfEmpty(1).Max())
				: Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(total)));

		var cells = new List<List<(int X, int Y)>>();
		int col = 0, row = 0, usedColumns = 0, lastRow = -1;
		foreach (int count in counts)
		{
			var list = new List<(int X, int Y)>();
			if (rowPerAnimation && col > 0 && count > 0)
			{
				row++;
				col = 0;
			}
			for (int i = 0; i < count; i++)
			{
				if (col == columns)
				{
					row++;
					col = 0;
				}
				list.Add((padding + col * (cellWidth + padding), padding + row * (cellHeight + padding)));
				col++;
				usedColumns = Math.Max(usedColumns, col);
				lastRow = row;
			}
			cells.Add(list);
		}

		int width = padding + usedColumns * (cellWidth + padding);
		int height = padding + (lastRow + 1) * (cellHeight + padding);
		return (cells, width, height);
	}
}

// The data file next to a sprite sheet, in the layout Aseprite and TexturePacker use (many game
// engines and tools read it): every frame's rectangle and duration, and named runs of frames
// ("frameTags", one per animation). Harlequin adds "origin": where the object's origin falls in
// each frame, so a game can line the frames up.
public sealed class SpriteSheetJson
{
	public List<SheetFrame> Frames { get; set; } = new();
	public SheetMeta Meta { get; set; } = new();

	public static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public string ToJson() => JsonSerializer.Serialize(this, Options);

	// Reads either kind: "frames" as a list (Aseprite "Array"), or as an object keyed by the
	// frame's name (Aseprite / TexturePacker "Hash")
	public static SpriteSheetJson Read(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		var sheet = new SpriteSheetJson();

		if (root.TryGetProperty("frames", out var frames))
		{
			if (frames.ValueKind == JsonValueKind.Array)
			{
				foreach (var f in frames.EnumerateArray())
					if (f.Deserialize<SheetFrame>(Options) is { } frame) sheet.Frames.Add(frame);
			}
			else if (frames.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in frames.EnumerateObject())
					if (p.Value.Deserialize<SheetFrame>(Options) is { } frame)
					{
						if (string.IsNullOrEmpty(frame.Filename)) frame.Filename = p.Name;
						sheet.Frames.Add(frame);
					}
			}
		}
		if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
			sheet.Meta = meta.Deserialize<SheetMeta>(Options) ?? new SheetMeta();
		return sheet;
	}
}

public sealed class SheetFrame
{
	public string Filename { get; set; } = "";
	public SheetRect Frame { get; set; } = new();
	public bool Rotated { get; set; }
	public bool Trimmed { get; set; }
	public SheetRect SpriteSourceSize { get; set; } = new();
	public SheetSize SourceSize { get; set; } = new();
	public int Duration { get; set; } = 100;   // milliseconds
	public List<SheetHitbox>? Hitboxes { get; set; }   // Harlequin: the hitboxes on in this frame, in its pixels
	public List<SheetMarker>? Points { get; set; }     // Harlequin: the points, in its pixels
}

// A hitbox in a frame: its middle and size in the frame's pixels (y down), turned clockwise by
// Angle degrees; a circle has a Radius instead of W and H
public sealed class SheetHitbox
{
	public string Name { get; set; } = "";
	public string Kind { get; set; } = "";
	public string Shape { get; set; } = "box";
	public float X { get; set; }
	public float Y { get; set; }
	public float? W { get; set; }
	public float? H { get; set; }
	public float? Angle { get; set; }
	public float? Radius { get; set; }
}

// A point in a frame: where it is in the frame's pixels (y down), and which way it faces
// (degrees, clockwise from pointing right)
public sealed class SheetMarker
{
	public string Name { get; set; } = "";
	public float X { get; set; }
	public float Y { get; set; }
	public float Angle { get; set; }
}

// An object's hitboxes and points as a frame shows them, posed as it is now.
// toPixels: the object's space → the frame's pixels.
public static class SheetMarkers
{
	private static float Round(float v) => MathF.Round(v, 2);
	private static float Degrees(Vector2 along) => Round(MathF.Atan2(along.Y, along.X) * 180f / MathF.PI);

	public static List<SheetHitbox> Hitboxes(Skeleton skeleton, Matrix3x2 toPixels, string prefix = "")
	{
		var list = new List<SheetHitbox>();
		foreach (var hitbox in skeleton.Hitboxes.Where(h => h.Active))
		{
			var m = hitbox.World * toPixels;
			var centre = m.Translation;
			var box = new SheetHitbox { Name = prefix + hitbox.Name, Kind = hitbox.Kind, X = Round(centre.X), Y = Round(centre.Y) };
			if (hitbox.Shape == HitboxShape.Box)
			{
				var across = Vector2.TransformNormal(new Vector2(hitbox.Size.X / 2f, 0f), m);
				var up = Vector2.TransformNormal(new Vector2(0f, hitbox.Size.Y / 2f), m);
				box.W = Round(2f * across.Length());
				box.H = Round(2f * up.Length());
				box.Angle = Degrees(across);
			}
			else
			{
				box.Shape = "circle";
				box.Radius = Round((Vector2.TransformNormal(new Vector2(hitbox.Radius, 0f), m).Length()
					+ Vector2.TransformNormal(new Vector2(0f, hitbox.Radius), m).Length()) / 2f);
			}
			list.Add(box);
		}
		return list;
	}

	public static List<SheetMarker> Points(Skeleton skeleton, Matrix3x2 toPixels, string prefix = "") =>
		skeleton.Points.Select(p =>
		{
			var m = p.World * toPixels;
			return new SheetMarker { Name = prefix + p.Name, X = Round(m.Translation.X), Y = Round(m.Translation.Y), Angle = Degrees(Vector2.TransformNormal(Vector2.UnitX, m)) };
		}).ToList();
}

public sealed class SheetRect
{
	public int X { get; set; }
	public int Y { get; set; }
	public int W { get; set; }
	public int H { get; set; }
}

public sealed class SheetSize
{
	public int W { get; set; }
	public int H { get; set; }
}

public sealed class SheetPoint
{
	public float X { get; set; }
	public float Y { get; set; }
}

public sealed class SheetMeta
{
	public string App { get; set; } = "Harlequin";
	public string Version { get; set; } = "1.0";
	public string Image { get; set; } = "";
	public string Format { get; set; } = "RGBA8888";
	public SheetSize Size { get; set; } = new();
	public string Scale { get; set; } = "1";
	public List<SheetTag> FrameTags { get; set; } = new();
	public SheetPoint? Origin { get; set; }   // Harlequin: the object's origin, in pixels from each frame's top-left
}

public sealed class SheetTag
{
	public string Name { get; set; } = "";
	public int From { get; set; }
	public int To { get; set; }
	public string Direction { get; set; } = "forward";
	public bool? Loop { get; set; }   // Harlequin: whether the animation loops
}
