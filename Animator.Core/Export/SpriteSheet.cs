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
