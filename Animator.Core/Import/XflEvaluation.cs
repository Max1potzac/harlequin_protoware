using System.Numerics;

namespace Animator.Core.Import;

public enum XflDrawingKind { Symbol, Bitmap, Shapes }

// Something that can be drawn once and reused: a symbol whose timeline never changes (drawn
// whole), a library picture, or a run of shapes (and groups) on one keyframe of a timeline
// (Timeline: a symbol's name, or a scene's as SceneKey makes it; Layer, Keyframe: indexes in it;
// Run: which run of shapes among the keyframe's elements).
public readonly record struct XflDrawingKey(XflDrawingKind Kind, string Name, int Layer = -1, int Keyframe = -1, int Run = -1);

// Where a track's drawing is at one frame: Matrix takes the drawing's own space into the
// imported timeline's (pixels, y down); Pivot is its transformation point in its own space;
// Blend, how it mixes with what's behind it
public readonly record struct XflPlacement(XflDrawingKey Drawing, Matrix3x2 Matrix, XflColor Color, Vector2 Pivot,
	BlendMode Blend = BlendMode.Normal);

// One thing that moves on its own through the animation: a symbol instance (or picture, or run
// of shapes) on a layer, at some depth inside other symbols, or (in projects whose layers don't
// keep to one thing, see XflThreading) on several layers in turn. It can change drawings over time.
public sealed class XflTrack
{
	public string Id { get; init; } = "";                 // layer indexes and instance numbers down the symbols (" + " between layers)
	public string LayerName { get; init; } = "";          // the layer it's on most
	public bool AutoNamed { get; init; }
	public bool Hidden { get; init; }                     // on layers hidden in Animate
	public XflPlacement?[] Frames { get; init; } = Array.Empty<XflPlacement?>();   // null = not on stage
	public string Name { get; set; } = "";
	public IReadOnlyList<XflTrackPiece> Pieces { get; init; } = Array.Empty<XflTrackPiece>();   // where its frames come from
	// Shown for under half of each animation it's in (frame labels start them): a crumb flying
	// off, a splash, a spark
	public bool ShortLived { get; init; }

	public IEnumerable<int> Shown => Enumerable.Range(0, Frames.Length).Where(f => Frames[f] != null);

	// The drawing it shows most often
	public XflDrawingKey MostShown() =>
		Frames.Where(f => f != null).GroupBy(f => f!.Value.Drawing).OrderByDescending(g => g.Count()).First().Key;
}

// Frames Start..End (both shown) of a track came from the layer named Layer
public readonly record struct XflTrackPiece(string Layer, int Start, int End);

public static class XflMath
{
	// Animate's way of building a transform: x axis at angleX scaled by sx, y axis at angleY scaled by sy
	public static Matrix3x2 Compose(float sx, float sy, float angleX, float angleY) =>
		new(sx * MathF.Cos(angleX), sx * MathF.Sin(angleX), -sy * MathF.Sin(angleY), sy * MathF.Cos(angleY), 0, 0);

	// ...and taking one apart: a mirror goes into the y scale, so angles stay continuous
	public static (float Sx, float Sy, float AngleX, float AngleY) Decompose(Matrix3x2 m)
	{
		float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
		float det = m.M11 * m.M22 - m.M12 * m.M21;
		float sy = MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22) * (det < 0 ? -1 : 1);
		float ax = MathF.Atan2(m.M12, m.M11);
		float ay = sy != 0 ? MathF.Atan2(-m.M21 / sy, m.M22 / sy) : ax;
		return (sx, sy, ax, ay);
	}

	public static float Mod(float x, float m) => x - m * MathF.Floor(x / m);

	// A classic tween's progress at a moment t (0..1): its ease, or its own ease curve
	public static float ClassicProgress(XflFrame frame, float t)
	{
		if (frame.CustomEase is { } points)
		{
			for (int i = 0; i + 3 < points.Count; i += 3)
			{
				var (p0, c1, c2, p1) = (points[i], points[i + 1], points[i + 2], points[i + 3]);
				if (t > p1.X && i + 6 < points.Count) continue;
				float lo = 0, hi = 1;
				for (int step = 0; step < 40; step++)
				{
					float mid = (lo + hi) / 2;
					if (XflMotion.Bezier(p0, c1, c2, p1, mid).X < t) lo = mid;
					else hi = mid;
				}
				return XflMotion.Bezier(p0, c1, c2, p1, (lo + hi) / 2).Y;
			}
			return t;
		}
		return t - frame.Acceleration / 100f * t * (1 - t);   // checked against Animate's published frames
	}

	// A classic tween between two keyframes' instances, `t` of the way: scale and axis angles
	// blend (turning the short way, or as many times as asked), around the transformation
	// point, which moves in a straight line. Checked against Animate's published frames.
	public static (Matrix3x2 Matrix, XflColor Color) Classic(XflFrame frame, XflInstance from, XflInstance to, float t)
	{
		float p = ClassicProgress(frame, t);
		var (sx0, sy0, ax0, ay0) = Decompose(from.Matrix);
		var (sx1, sy1, ax1, ay1) = Decompose(to.Matrix);
		if (!frame.TweenScale) (sx1, sy1) = (sx0, sy0);
		ax1 = Turn(ax0, ax1, frame);
		ay1 = Turn(ay0, ay1, frame);
		var m = Compose(sx0 + (sx1 - sx0) * p, sy0 + (sy1 - sy0) * p, ax0 + (ax1 - ax0) * p, ay0 + (ay1 - ay0) * p);
		var pivot = Vector2.Lerp(Vector2.Transform(from.Pivot, from.Matrix), Vector2.Transform(to.Pivot, to.Matrix), p);
		m.Translation = pivot - Vector2.TransformNormal(from.Pivot, m);
		return (m, XflColor.Lerp(from.Color, to.Color, p));
	}

	// Where an angle tweens to: the short way round, or clockwise / anticlockwise (y points
	// down, so clockwise is a growing angle) with extra whole turns
	private static float Turn(float a0, float a1, XflFrame frame)
	{
		const float full = 2 * MathF.PI;
		float d = a1 - a0;
		switch (frame.Rotate)
		{
			case XflRotate.Clockwise:
				d = Mod(d, full) + full * frame.RotateTimes;
				break;
			case XflRotate.CounterClockwise:
				d = Mod(d, full);
				if (d > 0) d -= full;
				d -= full * frame.RotateTimes;
				break;
			default:
				d = Mod(d + MathF.PI, full) - MathF.PI;
				break;
		}
		return a0 + d;
	}
}

// Plays a timeline: what's drawn at each frame, with nested symbols opened up
public sealed class XflEvaluator
{
	public static string SceneKey(string scene) => "\n" + scene;   // can't clash with a symbol name

	private readonly XflDocument _doc;
	private readonly Dictionary<string, bool> _static = new(StringComparer.Ordinal);
	public HashSet<string> Notes { get; } = new();

	public XflEvaluator(XflDocument doc) => _doc = doc;

	public XflTimeline? Timeline(string key) =>
		key.StartsWith('\n') ? _doc.Scenes.Find(s => s.Name == key[1..]) : _doc.Symbol(key)?.Timeline;

	// A symbol that always looks the same (one frame, and everything in it the same too) is drawn
	// whole; anything else is opened up, so its moving pieces become tracks of their own
	public bool IsStatic(string symbol)
	{
		if (_static.TryGetValue(symbol, out bool known)) return known;
		_static[symbol] = true;   // a symbol inside itself: stop here
		bool still = _doc.Symbol(symbol) is { } s && s.Timeline.Length == 1 &&
					 s.Timeline.Layers.All(l => l.Frames.All(f => f.Elements.All(StaysStill)));
		_static[symbol] = still;
		return still;
	}

	private bool StaysStill(XflElement e) => e switch
	{
		XflInstance i => i.Kind == XflSymbolKind.Button || IsStatic(i.Library),
		XflGroup g => g.Members.All(StaysStill),
		_ => true
	};

	// What `timeline` shows at `frame`, back to front
	public List<(string Track, XflPlacement Placement, string LayerName, bool AutoNamed, bool Hidden)> Frame(XflTimeline timeline, string key, int frame)
	{
		var output = new List<(string, XflPlacement, string, bool, bool)>();
		Visit(timeline, key, frame, Matrix3x2.Identity, XflColor.Identity, BlendMode.Normal, "", false, output, 0);
		return output;
	}

	private void Visit(XflTimeline timeline, string key, int frame, Matrix3x2 parent, XflColor parentColor, BlendMode parentBlend,
		string path, bool hidden, List<(string, XflPlacement, string, bool, bool)> output, int depth)
	{
		if (depth > 32) return;
		for (int li = timeline.Layers.Count - 1; li >= 0; li--)   // the bottom layer first
		{
			var layer = timeline.Layers[li];
			if (layer.Kind == XflLayerKind.Mask) Notes.Add("Masks aren't brought in: masked layers show whole.");
			if (layer.Kind != XflLayerKind.Normal) continue;   // guides, masks and folders draw nothing
			int k = layer.KeyframeAt(frame);
			if (k < 0) continue;
			var keyframe = layer.Frames[k];
			int offset = frame - keyframe.Index;
			if (keyframe.Tween == XflTween.Shape) Notes.Add("Shape tweens aren't brought in: each keyframe's shape holds until the next.");
			var next = k + 1 < layer.Frames.Count && layer.Frames[k + 1].Index == keyframe.End ? layer.Frames[k + 1] : null;
			bool layerHidden = hidden || !layer.Visible;
			string layerPath = $"{path}/{li}";

			int instances = 0, runs = 0;
			bool inRun = false;
			foreach (var element in keyframe.Elements)
			{
				if (element is XflBitmapInstance bitmap)
				{
					inRun = false;
					if (_doc.Bitmaps.ContainsKey(bitmap.Library))
						output.Add(($"{layerPath}#{instances}",
							new XflPlacement(new XflDrawingKey(XflDrawingKind.Bitmap, bitmap.Library), bitmap.Matrix * parent, parentColor, Vector2.Zero, parentBlend),
							layer.Name, layer.AutoNamed, layerHidden));
					instances++;
				}
				else if (element is XflInstance instance)
				{
					inRun = false;
					var (matrix, color) = (instance.Matrix, instance.Color);
					if (instances == 0 && keyframe.Tween == XflTween.Motion && keyframe.Motion is { } motion)
					{
						matrix = motion.Placement(instance, offset, _doc.FrameRate);
						if (motion.HasColor) color = motion.Color(offset, _doc.FrameRate);
					}
					else if (instances == 0 && keyframe.Tween == XflTween.Classic && next?.Elements.OfType<XflInstance>().FirstOrDefault() is { } target)
					{
						(matrix, color) = XflMath.Classic(keyframe, instance, target, offset / (float)keyframe.Duration);
					}
					AddInstance(instance, matrix * parent, color.Then(parentColor), BlendOf(instance.Blend, parentBlend), $"{layerPath}#{instances}",
						PlayedFrame(instance, layer, k, offset), layer, layerHidden, output, depth);
					instances++;
				}
				else if (element is not XflText)
				{
					if (!inRun)
					{
						output.Add(($"{layerPath}~{runs}",
							new XflPlacement(new XflDrawingKey(XflDrawingKind.Shapes, key, li, k, runs), parent, parentColor, Vector2.Zero, parentBlend),
							layer.Name, layer.AutoNamed, layerHidden));
						runs++;
					}
					inRun = true;
				}
			}
		}
	}

	private void AddInstance(XflInstance instance, Matrix3x2 world, XflColor color, BlendMode blend, string track, int symbolFrame,
		XflLayer layer, bool hidden, List<(string, XflPlacement, string, bool, bool)> output, int depth)
	{
		var symbol = _doc.Symbol(instance.Library);
		if (symbol == null)
		{
			Notes.Add($"Some symbols are missing from the library (\"{instance.Library}\"): they're left out.");
			return;
		}
		if (instance.Kind == XflSymbolKind.Button || IsStatic(symbol.Name))
		{
			output.Add((track, new XflPlacement(new XflDrawingKey(XflDrawingKind.Symbol, symbol.Name), world, color, instance.Pivot, blend),
				layer.Name, layer.AutoNamed, hidden));
			return;
		}
		Visit(symbol.Timeline, symbol.Name, symbolFrame, world, color, blend, track, hidden, output, depth + 1);
	}

	// An instance's blend mode, as parts can have it; "normal" (or "layer") keeps the one it's in
	private BlendMode BlendOf(string blend, BlendMode around)
	{
		switch (blend)
		{
			case "add": return BlendMode.Add;
			case "multiply": return BlendMode.Multiply;
			case "screen": return BlendMode.Screen;
			case "normal" or "layer" or "": return around;
			default:
				Notes.Add($"Blend modes other than add, multiply and screen aren't brought in (\"{blend}\" shows as normal).");
				return around;
		}
	}

	// Which frame of its own timeline a symbol shows, `offset` frames after its keyframe. Graphic
	// symbols follow their loop setting; movie clips play on their own, from where they appeared
	// (a run of keyframes with the same clip continues it), looping.
	private int PlayedFrame(XflInstance instance, XflLayer layer, int keyframe, int offset)
	{
		var symbol = _doc.Symbol(instance.Library);
		int length = symbol?.Timeline.Length ?? 1;
		if (instance.Kind == XflSymbolKind.Button) return 0;
		if (instance.Kind == XflSymbolKind.MovieClip)
		{
			int start = keyframe;
			while (start > 0 && layer.Frames[start - 1].End == layer.Frames[start].Index &&
				   layer.Frames[start - 1].Elements.OfType<XflInstance>().FirstOrDefault()?.Library == instance.Library)
				start--;
			return (layer.Frames[keyframe].Index - layer.Frames[start].Index + offset) % length;
		}
		int first = instance.FirstFrame;
		int last = Math.Clamp(instance.LastFrame ?? length - 1, 0, length - 1);
		return instance.Loop switch
		{
			XflLoop.SingleFrame => Math.Clamp(first, 0, length - 1),
			XflLoop.PlayOnce => Math.Min(first + offset, last),
			XflLoop.PlayOnceReverse => Math.Max(first - offset, 0),
			XflLoop.LoopReverse => (int)XflMath.Mod(first - offset, length),
			_ => instance.LastFrame is { } && last >= first ? first + offset % (last - first + 1) : (first + offset) % length
		};
	}

	// The elements a Shapes drawing stands for
	public IEnumerable<XflElement> RunElements(XflDrawingKey key)
	{
		if (Timeline(key.Name) is not { } timeline || key.Layer < 0 || key.Layer >= timeline.Layers.Count) yield break;
		var frames = timeline.Layers[key.Layer].Frames;
		if (key.Keyframe < 0 || key.Keyframe >= frames.Count) yield break;
		int run = -1;
		bool inRun = false;
		foreach (var element in frames[key.Keyframe].Elements)
		{
			if (element is XflInstance or XflBitmapInstance)
			{
				inRun = false;
				continue;
			}
			if (element is XflText) continue;
			if (!inRun) run++;
			inRun = true;
			if (run == key.Run) yield return element;
			else if (run > key.Run) yield break;
		}
	}

	// A name for a drawing: a picture's file name; a symbol that's just one picture gets the
	// picture's name, any other symbol its own (without its library folders)
	public string DrawingName(XflDrawingKey key)
	{
		switch (key.Kind)
		{
			case XflDrawingKind.Bitmap:
				return Plain(key.Name);
			case XflDrawingKind.Symbol:
				string? name = key.Name;
				for (int depth = 0; depth < 8 && name != null; depth++)
				{
					var only = _doc.Symbol(name)?.Timeline.Layers.SelectMany(l => l.Frames).SelectMany(f => f.Elements).ToList();
					if (only is not { Count: 1 }) break;
					if (only[0] is XflBitmapInstance b) return Plain(b.Library);
					name = (only[0] as XflInstance)?.Library;
				}
				return Plain(key.Name);
			default:
				var timeline = Timeline(key.Name);
				string layer = timeline != null && key.Layer >= 0 && key.Layer < timeline.Layers.Count ? timeline.Layers[key.Layer].Name : "";
				return layer.Length > 0 ? layer : "shape";
		}
	}

	private static string Plain(string libraryName)
	{
		string name = libraryName.Replace('\\', '/');
		name = name[(name.LastIndexOf('/') + 1)..];
		string ext = Path.GetExtension(name).ToLowerInvariant();
		if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp") name = name[..^ext.Length];
		return name.Length > 0 ? name : libraryName;
	}
}

// A whole timeline played out: every track's placement at every frame, the tracks in drawing
// order (back to front), and the frame labels. The tracks are one per layer (and instance on it),
// or, for projects whose layers don't keep to one thing, parts followed by their drawings from
// layer to layer (see XflThreading); Follow picks which.
public sealed class XflAnimationData
{
	public float FrameRate { get; init; }
	public int FrameCount { get; init; }
	public List<XflTrack> Tracks { get; } = new();
	public List<(string Name, int Frame)> Labels { get; } = new();
	public XflEvaluator Evaluator { get; init; } = null!;

	// Parts move from layer to layer here (as converters write them): following their drawings is
	// the way to see them
	public bool LooksConverted { get; private set; }
	public bool FollowsDrawings { get; private set; }

	private List<XflTrack> _byLayer = new(), _byDrawing = new();
	private bool _layersReordered, _drawingsReordered;
	private readonly List<string> _notes = new();

	public IEnumerable<string> Notes
	{
		get
		{
			if (FollowsDrawings ? _drawingsReordered : _layersReordered)
				yield return "The parts change their stacking order during the animation: each keeps the place it has on most frames.";
			foreach (string note in _notes) yield return note;
		}
	}

	// The tracks: parts followed by their drawings from layer to layer, or one per layer
	public void Follow(bool drawings)
	{
		FollowsDrawings = drawings;
		Tracks.Clear();
		Tracks.AddRange(drawings ? _byDrawing : _byLayer);
	}

	public static XflAnimationData Evaluate(XflDocument doc, XflTimeline timeline, string key)
	{
		var evaluator = new XflEvaluator(doc);
		int length = timeline.Length;
		var data = new XflAnimationData { FrameRate = doc.FrameRate, FrameCount = length, Evaluator = evaluator };

		foreach (var layer in timeline.Layers)
			foreach (var frame in layer.Frames)
				if (frame.Label is { } label && !data.Labels.Any(l => l.Name == label) && frame.Index < length)
					data.Labels.Add((label, frame.Index));
		data.Labels.Sort((a, b) => a.Frame.CompareTo(b.Frame));

		// Every layer (and instance on it) as a track, in the order they first show, and what each
		// frame draws, back to front
		var index = new Dictionary<string, int>(StringComparer.Ordinal);
		var layers = new List<LayerTrack>();
		var drawn = new List<int>[length];
		for (int f = 0; f < length; f++)
		{
			drawn[f] = new List<int>();
			foreach (var (track, placement, layer, auto, hidden) in evaluator.Frame(timeline, key, f))
			{
				if (!index.TryGetValue(track, out int t))
				{
					index[track] = t = layers.Count;
					layers.Add(new LayerTrack(track, layer, auto, hidden, new XflPlacement?[length]));
				}
				if (layers[t].Frames[f] == null) layers[t].Frames[f] = placement;   // the same track twice in a frame: the first counts
				if (drawn[f].Count == 0 || drawn[f][^1] != t) drawn[f].Add(t);
			}
		}

		var frames = layers.Select(l => l.Frames).ToList();
		var spans = Spans(data.Labels, length);
		var threads = XflThreading.ByDrawing(frames, data.Labels.Select(l => l.Frame).ToHashSet(), length);
		data._byLayer = Build(XflThreading.ByLayer(frames, length), layers, drawn, spans, length, out data._layersReordered);
		data._byDrawing = Build(threads, layers, drawn, spans, length, out data._drawingsReordered);

		// Converted: many parts (not counting short-lived ones) go from layer to layer
		var lasting = data._byDrawing.Where(t => !t.ShortLived).ToList();
		int moving = lasting.Count(t => t.Id.Contains(" + ", StringComparison.Ordinal));
		data.LooksConverted = moving >= Math.Max(2, lasting.Count / 4);
		data.Follow(data.LooksConverted);

		data._notes.AddRange(evaluator.Notes.OrderBy(n => n));
		return data;
	}

	private sealed record LayerTrack(string Id, string Layer, bool Auto, bool Hidden, XflPlacement?[] Frames);

	// The animations' spans of frames: label to label (and the frames before the first label)
	private static List<(int Start, int End)> Spans(List<(string Name, int Frame)> labels, int length)
	{
		var starts = labels.Select(l => l.Frame).Append(0).Where(f => f < length).Distinct().OrderBy(f => f).ToList();
		return starts.Select((s, i) => (s, i + 1 < starts.Count ? starts[i + 1] : length)).ToList();
	}

	// Tracks for parts made of pieces of layer tracks, in drawing order
	private static List<XflTrack> Build(List<List<XflThreading.Piece>> parts, List<LayerTrack> layers, List<int>[] drawn,
		List<(int Start, int End)> spans, int length, out bool reordered)
	{
		var partOf = layers.Select(_ => Enumerable.Repeat(-1, length).ToArray()).ToArray();
		for (int p = 0; p < parts.Count; p++)
			foreach (var piece in parts[p])
				for (int f = piece.Start; f <= piece.End; f++)
					partOf[piece.Track][f] = p;

		// Which part is drawn just behind which, on how many frames
		var firstSeen = new Dictionary<int, int>();
		var behind = new Dictionary<(int A, int B), int>();
		for (int f = 0; f < length; f++)
		{
			int previous = -1;
			foreach (int t in drawn[f])
			{
				int p = partOf[t][f];
				if (p < 0) continue;
				firstSeen.TryAdd(p, firstSeen.Count);
				if (previous >= 0 && previous != p) behind[(previous, p)] = behind.GetValueOrDefault((previous, p)) + 1;
				previous = p;
			}
		}
		// A pair drawn both ways round keeps the way it's drawn on most frames
		var before = behind.Where(b => !behind.TryGetValue((b.Key.B, b.Key.A), out int other) || b.Value >= other)
			.Select(b => b.Key).ToHashSet();
		reordered = behind.Keys.Any(k => behind.ContainsKey((k.B, k.A)));
		var order = DrawingOrder(firstSeen, before, out bool cycled);
		reordered |= cycled;

		var tracks = new List<XflTrack>();
		foreach (int p in order)
		{
			var pieces = parts[p].OrderBy(x => x.Start).ToList();
			var on = pieces.Select(x => x.Track).Distinct().ToList();
			int main = on.OrderByDescending(t => pieces.Where(x => x.Track == t).Sum(x => x.End - x.Start + 1)).First();
			var placements = new XflPlacement?[length];
			foreach (var piece in pieces)
				for (int f = piece.Start; f <= piece.End; f++)
					placements[f] = layers[piece.Track].Frames[f];
			tracks.Add(new XflTrack
			{
				Id = string.Join(" + ", on.Select(t => layers[t].Id)),
				LayerName = layers[main].Layer,
				AutoNamed = layers[main].Auto,
				Hidden = on.All(t => layers[t].Hidden),
				Frames = placements,
				Pieces = pieces.Select(x => new XflTrackPiece(layers[x.Track].Layer, x.Start, x.End)).ToList(),
				ShortLived = ShortLived(placements, spans)
			});
		}
		return tracks;
	}

	// Shown for under half of each animation it's shown in at all
	private static bool ShortLived(XflPlacement?[] frames, List<(int Start, int End)> spans)
	{
		bool shownAtAll = false;
		foreach (var (start, end) in spans)
		{
			int shown = 0;
			for (int f = start; f < end; f++)
				if (frames[f] != null) shown++;
			if (shown == 0) continue;
			if (2 * shown >= end - start) return false;
			shownAtAll = true;
		}
		return shownAtAll;
	}

	// Back to front: whatever is drawn before something else (on most of the frames they're both
	// drawn) comes first, in the order they appeared otherwise; where that goes round in a circle,
	// it's broken where it started
	private static List<int> DrawingOrder(Dictionary<int, int> firstSeen, HashSet<(int A, int B)> before, out bool reordered)
	{
		reordered = false;
		var after = firstSeen.Keys.ToDictionary(k => k, _ => new List<int>());
		var waiting = firstSeen.Keys.ToDictionary(k => k, _ => 0);
		foreach (var (a, b) in before)
		{
			after[a].Add(b);
			waiting[b]++;
		}
		var ready = new SortedSet<(int Seen, int Part)>(firstSeen.Where(p => waiting[p.Key] == 0).Select(p => (p.Value, p.Key)));
		var left = new SortedSet<(int Seen, int Part)>(firstSeen.Select(p => (p.Value, p.Key)));
		var order = new List<int>();
		while (left.Count > 0)
		{
			(int Seen, int Part) next;
			if (ready.Count > 0) next = ready.Min;
			else
			{
				next = left.Min;   // a loop of "in front of": break it where it started
				reordered = true;
			}
			ready.Remove(next);
			left.Remove(next);
			order.Add(next.Part);
			foreach (int b in after[next.Part])
				if (--waiting[b] == 0 && left.Contains((firstSeen[b], b)))
					ready.Add((firstSeen[b], b));
		}
		return order;
	}
}
