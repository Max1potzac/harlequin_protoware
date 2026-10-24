using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Animator.Core.Import;

// Adobe Animate (Flash) projects, in their XFL form: a folder of XML files (DOMDocument.xml and a
// LIBRARY of symbols), with the pictures in bin/ or LIBRARY/, or the same zipped up as a .fla.
// Coordinates are Animate's own: pixels, y pointing down. A matrix maps x' = a·x + c·y + tx,
// y' = b·x + d·y + ty, which is a Matrix3x2 used with row vectors (M11 = a, M12 = b, M21 = c,
// M22 = d, M31 = tx, M32 = ty): an element's matrix takes its own space into its parent's.

public enum XflSymbolKind { Graphic, MovieClip, Button }

// How a graphic symbol's own timeline plays inside its parent's
public enum XflLoop { Loop, PlayOnce, SingleFrame, LoopReverse, PlayOnceReverse }

public enum XflTween { None, Classic, Shape, Motion }

public enum XflRotate { Auto, None, Clockwise, CounterClockwise }

public enum XflLayerKind { Normal, Guide, Mask, Folder }

// A colour effect: each channel (red, green, blue, alpha) multiplied, then offset by 0..255
public readonly record struct XflColor(Vector4 Multiply, Vector4 Offset)
{
	public static readonly XflColor Identity = new(Vector4.One, Vector4.Zero);

	public bool IsIdentity => Multiply == Vector4.One && Offset == Vector4.Zero;

	// This effect, seen through `outer` (an instance inside another instance)
	public XflColor Then(XflColor outer) => new(Multiply * outer.Multiply, Offset * outer.Multiply + outer.Offset);

	public static XflColor Lerp(XflColor a, XflColor b, float t) =>
		new(Vector4.Lerp(a.Multiply, b.Multiply, t), Vector4.Lerp(a.Offset, b.Offset, t));
}

// ======================= what's on a frame =======================

public abstract class XflElement
{
	public Matrix3x2 Matrix { get; init; } = Matrix3x2.Identity;
}

// A symbol placed on a frame
public sealed class XflInstance : XflElement
{
	public string Library { get; init; } = "";
	public XflSymbolKind Kind { get; init; }
	public XflLoop Loop { get; init; }
	public int FirstFrame { get; init; }
	public int? LastFrame { get; init; }
	public Vector2 Pivot { get; init; }          // the transformation point, in the symbol's own space
	public XflColor Color { get; init; } = XflColor.Identity;
}

// A library picture placed on a frame
public sealed class XflBitmapInstance : XflElement
{
	public string Library { get; init; } = "";
}

// A vector drawing: its fills and strokes, and the edges between them
public sealed class XflShape : XflElement
{
	public Dictionary<int, XflPaint> Fills { get; } = new();
	public Dictionary<int, XflStroke> Strokes { get; } = new();
	public List<XflEdge> Edges { get; } = new();
}

// A piece of a shape's outline (Animate's edge format) with the fill on its left and on its
// right (0 = none) and the stroke along it
public readonly record struct XflEdge(int LeftFill, int RightFill, int Stroke, string Path);

public sealed class XflGroup : XflElement
{
	public List<XflElement> Members { get; } = new();
}

// Text: not brought in (only noted)
public sealed class XflText : XflElement { }

public abstract class XflPaint { }

public sealed class XflSolid : XflPaint
{
	public Vector4 Color { get; init; } = new(0, 0, 0, 1);   // red, green, blue, alpha: 0..1
}

public enum XflSpread { Pad, Reflect, Repeat }

// A gradient over the square ±819.2 (Animate's gradient box), taken into the shape by Matrix
public sealed class XflGradient : XflPaint
{
	public const float Half = 819.2f;
	public bool Radial { get; init; }
	public Matrix3x2 Matrix { get; init; } = Matrix3x2.Identity;
	public float FocalPoint { get; init; }                     // radial: -1..1 along the gradient's x axis
	public XflSpread Spread { get; init; }
	public List<(float Ratio, Vector4 Color)> Stops { get; } = new();
}

// A picture repeated (or not) across a fill; Matrix takes its pixels into the shape
public sealed class XflBitmapFill : XflPaint
{
	public string Bitmap { get; init; } = "";
	public Matrix3x2 Matrix { get; init; } = Matrix3x2.Identity;
	public bool Clipped { get; init; }
}

public enum XflCaps { Round, None, Square }

public enum XflJoints { Round, Bevel, Miter }

public sealed class XflStroke
{
	public XflPaint Paint { get; init; } = new XflSolid();
	public float Weight { get; init; } = 1f;
	public bool Hairline { get; init; }
	public XflCaps Caps { get; init; }
	public XflJoints Joints { get; init; }
	public float MiterLimit { get; init; } = 3f;
}

// ======================= timelines =======================

public sealed class XflFrame
{
	public int Index { get; init; }
	public int Duration { get; init; } = 1;
	public int End => Index + Duration;
	public XflTween Tween { get; init; }
	public float Acceleration { get; init; }                // a classic tween's ease as the file has it: -100 (fast start) .. 100 (slow start)
	public List<Vector2>? CustomEase { get; init; }         // or its own curve: Bézier points, x = time, y = progress
	public XflRotate Rotate { get; init; }
	public int RotateTimes { get; init; }
	public bool TweenScale { get; init; } = true;
	public string? Label { get; init; }
	public XflMotion? Motion { get; init; }                 // a motion tween (tweenType "motion object")
	public List<XflElement> Elements { get; } = new();
}

public sealed class XflLayer
{
	public string Name { get; init; } = "";
	public XflLayerKind Kind { get; init; }
	public bool Visible { get; init; } = true;
	public bool AutoNamed { get; init; }
	public List<XflFrame> Frames { get; } = new();   // keyframes, in order
	public int Length => Frames.Count == 0 ? 0 : Frames[^1].End;

	// The keyframe showing at a frame (its index in Frames), or -1
	public int KeyframeAt(int frame)
	{
		int lo = 0, hi = Frames.Count - 1;
		while (lo <= hi)
		{
			int mid = (lo + hi) / 2;
			var f = Frames[mid];
			if (frame < f.Index) hi = mid - 1;
			else if (frame >= f.End) lo = mid + 1;
			else return mid;
		}
		return -1;
	}
}

public sealed class XflTimeline
{
	public string Name { get; init; } = "";
	public List<XflLayer> Layers { get; } = new();   // top to bottom, as Animate lists them
	public int Length => Layers.Count == 0 ? 1 : Math.Max(1, Layers.Max(l => l.Length));
}

public sealed class XflSymbol
{
	public string Name { get; init; } = "";
	public XflSymbolKind Kind { get; init; }
	public XflTimeline Timeline { get; init; } = new();
}

public sealed class XflBitmapItem
{
	public string Name { get; init; } = "";
	public string? Href { get; init; }            // a file in LIBRARY
	public string? DataHref { get; init; }        // Animate's own copy in bin/
	public string? SourcePath { get; init; }      // where it was imported from (relative to the project)
	public int Width { get; init; }               // pixels, 0 = not said
	public int Height { get; init; }
}

// ======================= the project =======================

public sealed class XflDocument
{
	public float Width { get; init; } = 550;
	public float Height { get; init; } = 400;
	public float FrameRate { get; init; } = 24;
	public List<XflTimeline> Scenes { get; } = new();
	public Dictionary<string, XflSymbol> Symbols { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, XflBitmapItem> Bitmaps { get; } = new(StringComparer.Ordinal);
	public List<string> Notes { get; } = new();          // what didn't come across
	public XflFiles Files { get; init; } = XflFiles.Empty;

	public XflSymbol? Symbol(string name) => Symbols.TryGetValue(name, out var s) ? s : null;

	// A library picture's file: Animate's copy in bin/, else the file in LIBRARY, else where it came from
	public byte[]? BitmapBytes(XflBitmapItem item)
	{
		foreach (var path in new[] { item.DataHref is { } d ? "bin/" + d : null, item.Href is { } h ? "LIBRARY/" + h : null,
					 item.SourcePath, item.Href, item.Name.Contains('.') ? "LIBRARY/" + item.Name : null })
			if (path != null && Files.Read(path) is { } bytes)
				return bytes;
		return null;
	}
}

// Where a project's files are: a folder, or the entries of a zip (a .fla, or a zipped folder)
public sealed class XflFiles
{
	public static readonly XflFiles Empty = new(new Dictionary<string, byte[]>());

	private readonly string? _folder;
	private readonly Dictionary<string, byte[]>? _entries;   // by normalised path

	public XflFiles(string folder) => _folder = folder;

	public XflFiles(Dictionary<string, byte[]> entries) => _entries = entries;

	private static string Normalise(string path) =>
		path.Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

	public byte[]? Read(string path)
	{
		path = path.Replace('\\', '/');
		if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
		if (_entries != null)
			return _entries.TryGetValue(Normalise(path), out var bytes) ? bytes : null;
		if (_folder == null) return null;
		try
		{
			string full = Path.GetFullPath(Path.Combine(_folder, path));
			if (!full.StartsWith(Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase)) return null;   // stays inside the project
			return File.Exists(full) ? File.ReadAllBytes(full) : null;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	// A zip's entries under `root` (the folder holding DOMDocument.xml inside it), by normalised path
	public static XflFiles FromZip(ZipArchive zip, string root)
	{
		var entries = new Dictionary<string, byte[]>();
		string prefix = Normalise(root);
		if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
		foreach (var entry in zip.Entries)
		{
			string name = Normalise(entry.FullName);
			if (name.Length == 0 || name.EndsWith('/') || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
			using var stream = entry.Open();
			using var memory = new MemoryStream();
			stream.CopyTo(memory);
			entries[name[prefix.Length..]] = memory.ToArray();
		}
		return new XflFiles(entries);
	}
}

// ======================= reading =======================

public static class XflReader
{
	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	// path: a .fla (or a zip of an XFL folder), a .xfl file or DOMDocument.xml (their folder), or the folder
	public static XflDocument Read(string path)
	{
		if (Directory.Exists(path)) return Read(new XflFiles(path));
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext is ".xfl" or ".xml") return Read(new XflFiles(Path.GetDirectoryName(Path.GetFullPath(path))!));
		return ReadZip(File.ReadAllBytes(path));
	}

	public static XflDocument ReadZip(byte[] data)
	{
		using var zip = OpenZip(data);
		var document = zip.Entries
			.Where(e => e.Name.Equals("DOMDocument.xml", StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.FullName.Count(c => c == '/'))
			.FirstOrDefault()
			?? throw new InvalidDataException("This file has no DOMDocument.xml: it isn't an Animate (XFL) project.");
		string root = document.FullName[..^document.Name.Length];
		return Read(XflFiles.FromZip(zip, root));
	}

	// Animate writes .fla zips whose directory record says it's 54 bytes longer than it is; some
	// readers refuse them. If this one does, the record is put right and it's read again.
	private static ZipArchive OpenZip(byte[] data)
	{
		try
		{
			return new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
		}
		catch (InvalidDataException)
		{
			var copy = (byte[])data.Clone();
			for (int i = copy.Length - 22; i >= Math.Max(0, copy.Length - 22 - 65535); i--)
			{
				if (copy[i] != 0x50 || copy[i + 1] != 0x4B || copy[i + 2] != 0x05 || copy[i + 3] != 0x06) continue;
				uint size = BitConverter.ToUInt32(copy, i + 12), offset = BitConverter.ToUInt32(copy, i + 16);
				uint actual = (uint)(i - offset);
				if (size != actual) BitConverter.GetBytes(actual).CopyTo(copy, i + 12);
				break;
			}
			return new ZipArchive(new MemoryStream(copy, writable: false), ZipArchiveMode.Read);
		}
	}

	public static XflDocument Read(XflFiles files)
	{
		var root = Load(files.Read("DOMDocument.xml")
			?? throw new InvalidDataException("DOMDocument.xml is missing: this isn't an Animate (XFL) project."));
		if (root.Name.LocalName != "DOMDocument")
			throw new InvalidDataException("DOMDocument.xml isn't an Animate document.");

		var doc = new XflDocument
		{
			Width = Float(root, "width", 550),
			Height = Float(root, "height", 400),
			FrameRate = Float(root, "frameRate", 24),
			Files = files
		};

		foreach (var item in Children(Child(root, "media"), "DOMBitmapItem"))
		{
			string name = Name(item, "name");
			if (name.Length == 0 || doc.Bitmaps.ContainsKey(name)) continue;
			doc.Bitmaps[name] = new XflBitmapItem
			{
				Name = name,
				Href = Text(item, "href"),
				DataHref = Text(item, "bitmapDataHRef"),
				SourcePath = Text(item, "sourceExternalFilepath"),
				Width = (int)MathF.Round(Float(item, "frameRight", 0) / 20f),
				Height = (int)MathF.Round(Float(item, "frameBottom", 0) / 20f)
			};
		}

		int unreadable = 0;
		foreach (var include in Children(Child(root, "symbols"), "Include"))
		{
			string href = Text(include, "href") ?? "";
			if (href.Length == 0) continue;
			var xml = files.Read("LIBRARY/" + href) ?? files.Read("LIBRARY/" + href.Replace('&', '_'));
			XElement? item = null;
			if (xml != null)
			{
				try { item = Load(xml); }
				catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { item = null; }
			}
			if (item == null || item.Name.LocalName != "DOMSymbolItem")
			{
				unreadable++;
				continue;
			}
			string name = Name(item, "name");
			if (name.Length == 0) name = Unescape(href[..^Path.GetExtension(href).Length]);
			doc.Symbols[name] = new XflSymbol
			{
				Name = name,
				Kind = SymbolKind(Text(item, "symbolType")),
				Timeline = ReadTimeline(Child(Child(item, "timeline"), "DOMTimeline"), name, doc)
			};
		}
		if (unreadable > 0) doc.Notes.Add($"{unreadable} symbol file(s) in the library couldn't be read: they're left out.");

		foreach (var timeline in Children(Child(root, "timelines"), "DOMTimeline"))
			doc.Scenes.Add(ReadTimeline(timeline, Name(timeline, "name"), doc));
		return doc;
	}

	private static XElement Load(byte[] xml)
	{
		using var stream = new MemoryStream(xml, writable: false);
		return XDocument.Load(stream).Root ?? throw new InvalidDataException("An empty XML file.");
	}

	// ----- timelines -----

	private static XflTimeline ReadTimeline(XElement? element, string name, XflDocument doc)
	{
		var timeline = new XflTimeline { Name = name };
		if (element == null) return timeline;
		foreach (var layerElement in Children(Child(element, "layers"), "DOMLayer"))
		{
			var layer = new XflLayer
			{
				Name = Name(layerElement, "name"),
				Kind = Text(layerElement, "layerType") switch
				{
					"guide" => XflLayerKind.Guide,
					"mask" => XflLayerKind.Mask,
					"folder" => XflLayerKind.Folder,
					_ => XflLayerKind.Normal
				},
				Visible = Text(layerElement, "visible") != "false",
				AutoNamed = Text(layerElement, "autoNamed") != "false" && IsAutoName(Name(layerElement, "name"))
			};
			foreach (var frameElement in Children(Child(layerElement, "frames"), "DOMFrame"))
			{
				var frame = ReadFrame(frameElement, doc);
				if (layer.Frames.Count > 0 && frame.Index < layer.Frames[^1].End) continue;   // overlapping: not valid, skip it
				layer.Frames.Add(frame);
			}
			timeline.Layers.Add(layer);
		}
		return timeline;
	}

	// "Layer 3", "3": names Animate (or a converter) made up, not the animator
	private static bool IsAutoName(string name) =>
		name.Length == 0 || Regex.IsMatch(name, @"^(Layer\s*)?\d+$", RegexOptions.IgnoreCase);

	private static XflFrame ReadFrame(XElement element, XflDocument doc)
	{
		string? labelType = Text(element, "labelType");
		string? label = Text(element, "name");
		var tweens = Child(element, "tweens");
		List<Vector2>? ease = null;
		if (Child(tweens, "CustomEase") is { } custom)
			ease = Children(custom, "Point").Select(p => new Vector2(Float(p, "x", 0), Float(p, "y", 0))).ToList();

		XflMotion? motion = null;
		var core = Child(Child(element, "motionObjectXML"), "AnimationCore");
		if (core != null) motion = XflMotion.Read(core);

		var frame = new XflFrame
		{
			Index = Int(element, "index", 0),
			Duration = Math.Max(1, Int(element, "duration", 1)),
			Tween = Text(element, "tweenType") switch
			{
				"motion" => XflTween.Classic,
				"shape" => XflTween.Shape,
				"motion object" => XflTween.Motion,
				_ => XflTween.None
			},
			Acceleration = Float(element, "acceleration", 0),
			CustomEase = ease is { Count: >= 4 } ? ease : null,
			Rotate = Text(element, "motionTweenRotate") switch
			{
				"none" => XflRotate.None,
				"clockwise" => XflRotate.Clockwise,
				"counter-clockwise" => XflRotate.CounterClockwise,
				_ => XflRotate.Auto
			},
			RotateTimes = Int(element, "motionTweenRotateTimes", 0),
			TweenScale = Text(element, "motionTweenScale") != "false",
			Label = !string.IsNullOrWhiteSpace(label) && labelType is null or "name" or "anchor" ? Unescape(label!) : null,
			Motion = motion
		};
		foreach (var e in Child(element, "elements")?.Elements() ?? Enumerable.Empty<XElement>())
			if (ReadElement(e, doc) is { } read)
				frame.Elements.Add(read);
		return frame;
	}

	private static XflElement? ReadElement(XElement e, XflDocument doc)
	{
		switch (e.Name.LocalName)
		{
			case "DOMSymbolInstance":
				var kind = SymbolKind(Text(e, "symbolType"));
				return new XflInstance
				{
					Matrix = ReadMatrix(e),
					Library = Name(e, "libraryItemName"),
					Kind = kind,
					Loop = Text(e, "loop") switch
					{
						"loop" => XflLoop.Loop,
						"play once" => XflLoop.PlayOnce,
						"single frame" => XflLoop.SingleFrame,
						"loop reverse" => XflLoop.LoopReverse,
						"play once reverse" => XflLoop.PlayOnceReverse,
						_ => XflLoop.Loop
					},
					FirstFrame = Int(e, "firstFrame", 0),
					LastFrame = Text(e, "lastFrame") is { } last && int.TryParse(last, NumberStyles.Integer, Invariant, out int l) ? l : null,
					Pivot = ReadPoint(Child(e, "transformationPoint")),
					Color = ReadColor(Child(Child(e, "color"), "Color"))
				};
			case "DOMBitmapInstance":
				return new XflBitmapInstance { Matrix = ReadMatrix(e), Library = Name(e, "libraryItemName") };
			case "DOMShape":
				return ReadShape(e);
			case "DOMGroup":
				var group = new XflGroup();   // a group's own matrix repeats its members' placement: it's not used
				foreach (var member in Child(e, "members")?.Elements() ?? Enumerable.Empty<XElement>())
					if (ReadElement(member, doc) is { } read)
						group.Members.Add(read);
				return group;
			case "DOMStaticText" or "DOMDynamicText" or "DOMInputText" or "DOMTLFText":
				if (!doc.Notes.Contains(TextNote)) doc.Notes.Add(TextNote);
				return new XflText { Matrix = ReadMatrix(e) };
			default:
				return null;   // video, sound and the like: nothing to draw
		}
	}

	private const string TextNote = "Text isn't brought in (convert it to shapes in Animate first, if you need it).";

	private static XflShape ReadShape(XElement e)
	{
		var shape = new XflShape { Matrix = ReadMatrix(e) };
		foreach (var fill in Children(Child(e, "fills"), "FillStyle"))
			if (fill.Elements().FirstOrDefault() is { } paint && ReadPaint(paint) is { } p)
				shape.Fills[Int(fill, "index", 0)] = p;
		foreach (var strokeStyle in Children(Child(e, "strokes"), "StrokeStyle"))
		{
			var stroke = strokeStyle.Elements().FirstOrDefault();
			if (stroke == null) continue;
			var paint = Child(stroke, "fill")?.Elements().FirstOrDefault() is { } f ? ReadPaint(f) : null;
			shape.Strokes[Int(strokeStyle, "index", 0)] = new XflStroke
			{
				Paint = paint ?? new XflSolid(),
				Weight = Float(stroke, "weight", 1),
				Hairline = Text(stroke, "solidStyle") == "hairline",
				Caps = Text(stroke, "caps") switch { "none" => XflCaps.None, "square" => XflCaps.Square, _ => XflCaps.Round },
				Joints = Text(stroke, "joints") switch { "bevel" => XflJoints.Bevel, "miter" => XflJoints.Miter, _ => XflJoints.Round },
				MiterLimit = Float(stroke, "miterLimit", 3)
			};
		}
		foreach (var edge in Children(Child(e, "edges"), "Edge"))
		{
			string? path = Text(edge, "edges");
			if (string.IsNullOrEmpty(path)) continue;   // edges with only "cubics" are hints for Animate
			shape.Edges.Add(new XflEdge(Int(edge, "fillStyle0", 0), Int(edge, "fillStyle1", 0), Int(edge, "strokeStyle", 0), path));
		}
		return shape;
	}

	private static XflPaint? ReadPaint(XElement p)
	{
		switch (p.Name.LocalName)
		{
			case "SolidColor":
				return new XflSolid { Color = ReadRgba(p, "color", "alpha") };
			case "LinearGradient" or "RadialGradient":
				var gradient = new XflGradient
				{
					Radial = p.Name.LocalName == "RadialGradient",
					Matrix = ReadMatrix(p),
					FocalPoint = Float(p, "focalPointRatio", 0),
					Spread = Text(p, "spreadMethod") switch { "reflect" => XflSpread.Reflect, "repeat" => XflSpread.Repeat, _ => XflSpread.Pad }
				};
				foreach (var entry in Children(p, "GradientEntry"))
					gradient.Stops.Add((Math.Clamp(Float(entry, "ratio", 0), 0, 1), ReadRgba(entry, "color", "alpha")));
				if (gradient.Stops.Count == 0) gradient.Stops.Add((0, new Vector4(0, 0, 0, 1)));
				return gradient;
			case "BitmapFill":
				var m = ReadMatrix(p);   // its scale is in twips per pixel
				return new XflBitmapFill
				{
					Bitmap = Name(p, "bitmapPath"),
					Matrix = new Matrix3x2(m.M11 / 20, m.M12 / 20, m.M21 / 20, m.M22 / 20, m.M31, m.M32),
					Clipped = Text(p, "bitmapIsClipped") == "true"
				};
			default:
				return null;
		}
	}

	// ----- small parts -----

	public static Matrix3x2 ReadMatrix(XElement e)
	{
		var m = Child(Child(e, "matrix"), "Matrix");
		if (m == null) return Matrix3x2.Identity;
		return new Matrix3x2(Float(m, "a", 1), Float(m, "b", 0), Float(m, "c", 0), Float(m, "d", 1), Float(m, "tx", 0), Float(m, "ty", 0));
	}

	private static Vector2 ReadPoint(XElement? container)
	{
		var p = Child(container, "Point");
		return p == null ? Vector2.Zero : new Vector2(Float(p, "x", 0), Float(p, "y", 0));
	}

	// Alpha, brightness, tint or advanced (any mix of multipliers and offsets)
	public static XflColor ReadColor(XElement? c)
	{
		if (c == null) return XflColor.Identity;
		var multiply = new Vector4(Float(c, "redMultiplier", 1), Float(c, "greenMultiplier", 1), Float(c, "blueMultiplier", 1), Float(c, "alphaMultiplier", 1));
		var offset = new Vector4(Float(c, "redOffset", 0), Float(c, "greenOffset", 0), Float(c, "blueOffset", 0), Float(c, "alphaOffset", 0));
		if (Text(c, "brightness") != null)
		{
			float b = Float(c, "brightness", 0);
			multiply = new Vector4(b < 0 ? 1 + b : 1 - b, b < 0 ? 1 + b : 1 - b, b < 0 ? 1 + b : 1 - b, multiply.W);
			offset = new Vector4(b > 0 ? b * 255 : 0, b > 0 ? b * 255 : 0, b > 0 ? b * 255 : 0, offset.W);
		}
		else if (Text(c, "tintMultiplier") != null || Text(c, "tintColor") != null)
		{
			float t = Float(c, "tintMultiplier", 0);
			var tint = ReadRgba(c, "tintColor", null);
			multiply = new Vector4(1 - t, 1 - t, 1 - t, multiply.W);
			offset = new Vector4(tint.X * 255 * t, tint.Y * 255 * t, tint.Z * 255 * t, offset.W);
		}
		return new XflColor(multiply, offset);
	}

	private static Vector4 ReadRgba(XElement e, string colorName, string? alphaName)
	{
		string color = Text(e, colorName) ?? "#000000";
		float alpha = alphaName == null ? 1 : Float(e, alphaName, 1);
		if (color.StartsWith('#') && color.Length >= 7 &&
			int.TryParse(color.AsSpan(1, 6), NumberStyles.HexNumber, Invariant, out int rgb))
			return new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);
		return new Vector4(0, 0, 0, alpha);
	}

	private static XflSymbolKind SymbolKind(string? type) => type switch
	{
		"graphic" => XflSymbolKind.Graphic,
		"button" => XflSymbolKind.Button,
		_ => XflSymbolKind.MovieClip
	};

	private static XElement? Child(XElement? e, string name) =>
		e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);

	private static IEnumerable<XElement> Children(XElement? e, string name) =>
		e?.Elements().Where(c => c.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

	private static string? Text(XElement? e, string name) => e?.Attribute(name)?.Value;

	private static string Name(XElement e, string attribute) => Unescape(Text(e, attribute) ?? "");

	// Names can carry character references twice escaped ("&#47;"): those become characters
	private static string Unescape(string s) =>
		s.Contains("&#") ? Regex.Replace(s, @"&#(\d+);?", m => ((char)int.Parse(m.Groups[1].Value, Invariant)).ToString()) : s;

	private static float Float(XElement? e, string name, float fallback) =>
		e?.Attribute(name)?.Value is { Length: > 0 } v && float.TryParse(v, NumberStyles.Float, Invariant, out float f) && float.IsFinite(f)
			? f
			: fallback;

	private static int Int(XElement? e, string name, int fallback) =>
		e?.Attribute(name)?.Value is { Length: > 0 } v && double.TryParse(v, NumberStyles.Float, Invariant, out double d) && double.IsFinite(d)
			? (int)Math.Round(d)
			: fallback;
}
