using System.Numerics;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer.Panels;

// The graph editor (the timeline's Curves view): each number the picked bones' and parts' keys
// hold, as a curve over time (angles in degrees). Click a key to select it (Shift: as well), drag
// it to move it in time and value (Shift: one way only). The selected key shows its Bézier handles:
// drag them to ease the stretch from it (and the one into it). Wheel: zoom the values; middle-drag:
// move them; double-click: fit them again. It shares its selection with the dopesheet.
public sealed class GraphPanel
{
	private readonly EditorState _state;

	private const float LabelWidth = 150f;
	private const float RulerHeight = 24f;
	private const float LeftPad = 10f;
	private const float Margin = 16f;   // above and below the curves
	private const float PickPx = 7f;
	private const float RowHeight = 18f;

	private float _frameWidth = 10f;
	private Vector2 _size = new(800, 200);

	// One curve: a number of a row's keys (x of a move, say), as shown
	private sealed class Channel
	{
		public required Timeline Tl { get; init; }
		public required string Name { get; init; }
		public required SKColor Color { get; init; }
		public required Func<List<(float Time, float Value)>> Keys { get; init; }
		public required Func<float, float?> Sample { get; init; }
		public required Action<int, float> SetValue { get; init; }   // key i's number, as shown
		public string Label => $"{Tl.Target} {Name}";
	}

	private readonly HashSet<string> _hidden = new();          // channels switched off (their labels)
	private (float Min, float Max)? _manual;                     // the values zoomed or moved by hand (null: fitted)
	private (float Min, float Max) _range = (-1, 1);

	private enum Drag { None, Scrub, Key, Handle, Pan }
	private Drag _drag;
	private Vector2 _press;
	private AnimationSnapshot? _before;
	private bool _changed;
	private (Timeline Tl, string Name, int Key)? _focus;   // the key whose handles show (its curve, and which key)
	private int _handle;                               // 1: the focus key's way out; 2: its way in
	private List<(Timeline Tl, int Frame)> _dragKeys = new();
	private (float Min, float Max) _dragRange;

	public GraphPanel(EditorState state) => _state = state;

	private HashSet<(Timeline Tl, int Frame)> Sel => _state.SelectedKeys;
	private int Fps => _state.Fps;
	private int FrameOf(float time) => (int)MathF.Round(time * Fps);

	// ======================= what's shown =======================

	// The rows of the picked bones and parts, and rows with selected keys (nothing picked: all of them)
	private List<Channel> Channels()
	{
		var bones = _state.PickedBones.Select(b => b.Name).ToHashSet();
		var parts = _state.PickedSlots.Select(s => s.Name).ToHashSet();
		var selected = Sel.Select(k => k.Tl).ToHashSet();
		var rows = _state.Animation.Timelines.Where(tl =>
			selected.Contains(tl) ||
			(RigOps.TargetsBone(tl) && bones.Contains(tl.Target)) ||
			(PartOps.TargetsPart(tl) && parts.Contains(tl.Target))).ToList();
		if (rows.Count == 0 && bones.Count == 0 && parts.Count == 0) rows = _state.Animation.Timelines.ToList();
		var all = new List<Channel>();
		foreach (var tl in rows) all.AddRange(ChannelsOf(tl, all.Count));
		return all;
	}

	private static readonly SKColor[] Palette =
	{
		new(240, 110, 100), new(110, 210, 120), new(110, 160, 255), new(250, 200, 80),
		new(200, 130, 250), new(90, 220, 220), new(250, 150, 60), new(230, 120, 200)
	};

	private const float Deg = 180f / MathF.PI;

	private static IEnumerable<Channel> ChannelsOf(Timeline tl, int first)
	{
		SKColor C(int i) => Palette[(first + i) % Palette.Length];
		static float X(Vector2 v) => v.X;
		static float Y(Vector2 v) => v.Y;
		switch (tl)
		{
			case RotateTimeline r: yield return Make(r, "angle", C(0), v => v * Deg, (_, x) => x / Deg); break;
			case PartRotateTimeline r: yield return Make(r, "angle", C(0), v => v * Deg, (_, x) => x / Deg); break;
			case IkMixTimeline m: yield return Make(m, "IK mix", C(0), v => v, (_, x) => x); break;
			case PartOpacityTimeline o: yield return Make(o, "opacity", C(0), v => v, (_, x) => x); break;
			case TranslateTimeline or PartTranslateTimeline or ScaleTimeline or PartScaleTimeline:
				{
					var t = (TrackTimeline<Vector2>)tl;
					bool scale = tl is ScaleTimeline or PartScaleTimeline;
					yield return Make(t, scale ? "scale x" : "x", C(0), X, (v, x) => v with { X = x });
					yield return Make(t, scale ? "scale y" : "y", C(1), Y, (v, y) => v with { Y = y });
					break;
				}
			case DepthTimeline d:
				yield return Make(d, "tilt", C(0), v => v.X * Deg, (v, x) => v with { X = x / Deg });
				yield return Make(d, "turn", C(1), v => v.Y * Deg, (v, y) => v with { Y = y / Deg });
				break;
			case PartColorTimeline c:
				yield return Make(c, "red", new SKColor(240, 90, 90), v => v.X, (v, x) => v with { X = x });
				yield return Make(c, "green", new SKColor(90, 220, 110), v => v.Y, (v, y) => v with { Y = y });
				yield return Make(c, "blue", new SKColor(100, 150, 255), v => v.Z, (v, z) => v with { Z = z });
				break;
		}
	}

	private static Channel Make<T>(TrackTimeline<T> tl, string name, SKColor color, Func<T, float> get, Func<T, float, T> set) => new()
	{
		Tl = tl,
		Name = name,
		Color = color,
		Keys = () => tl.Track.Keys.Select(k => (k.Time, get(k.Value))).ToList(),
		Sample = time => tl.Track.TrySample(time, out var v) ? get(v) : null,
		SetValue = (i, x) => tl.Track.Keys[i] = tl.Track.Keys[i] with { Value = set(tl.Track.Keys[i].Value, x) }
	};

	private List<Channel> Visible() => Channels().Where(c => !_hidden.Contains(c.Label)).ToList();

	// ======================= where things are =======================

	private float FrameToX(float frame) => LabelWidth + LeftPad + frame * _frameWidth;
	private float XToFrame(float x) => (x - LabelWidth - LeftPad) / _frameWidth;
	private float Top => RulerHeight + Margin;
	private float Bottom => MathF.Max(Top + 10, _size.Y - Margin);
	private float ValueToY(float v) => Top + (_range.Max - v) / MathF.Max(1e-6f, _range.Max - _range.Min) * (Bottom - Top);
	private float YToValue(float y) => _range.Max - (y - Top) / MathF.Max(1e-6f, Bottom - Top) * (_range.Max - _range.Min);
	private Vector2 At(float time, float value) => new(FrameToX(time * Fps), ValueToY(value));

	// Every number shown, and enough room round them
	private (float Min, float Max) Fit(List<Channel> channels)
	{
		float min = float.MaxValue, max = float.MinValue;
		int frames = Math.Max(1, _state.FrameCount);
		foreach (var c in channels)
		{
			foreach (var (_, v) in c.Keys()) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
			for (int f = 0; f <= frames; f++)
				if (c.Sample(f / (float)Fps) is { } v) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
		}
		if (min > max) return (-1, 1);
		if (max - min < 1e-3f) { min -= 1; max += 1; }
		float pad = (max - min) * 0.08f;
		return (min - pad, max + pad);
	}

	// A key's stretch to the next one: where its handles are (null: stepped, or the last key)
	private (Vector2 From, Vector2 To, Vector2 H1, Vector2 H2)? Handles(Channel c, int i, List<(float Time, float Value)> keys)
	{
		if (i < 0 || i + 1 >= keys.Count || c.Tl.HandlesAt(keys[i].Time) is not { } h) return null;
		var (t0, v0) = keys[i];
		var (t1, v1) = keys[i + 1];
		float dv = Span(v0, v1);
		return (At(t0, v0), At(t1, v1),
			At(t0 + h.X1 * (t1 - t0), v0 + h.Y1 * dv),
			At(t0 + h.X2 * (t1 - t0), v0 + h.Y2 * dv));
	}

	// The value change across a stretch: none (flat) still gets some room, so its handles can be used
	private float Span(float v0, float v1) =>
		MathF.Abs(v1 - v0) > 1e-4f ? v1 - v0 : (_range.Max - _range.Min) * 0.25f;

	// ======================= drawing =======================

	public void Draw(SKCanvas canvas, Vector2 size)
	{
		_size = size;
		int frames = Math.Max(1, _state.FrameCount);
		_frameWidth = Math.Max(4f, (size.X - LabelWidth - LeftPad * 2) / frames);
		var channels = Visible();
		var listed = Channels();
		if (_drag is Drag.Key or Drag.Handle) _range = _dragRange;
		else _range = _manual ?? Fit(channels);
		if (_focus is { } f && Focused() == null) _focus = null;

		canvas.Clear(new SKColor(30, 33, 39));
		using var font = new SKFont { Size = 12 };
		using var small = new SKFont { Size = 10 };
		using var text = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true };
		using var faint = new SKPaint { Color = new SKColor(150, 156, 168), IsAntialias = true };
		using var tick = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1 };
		using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 18), StrokeWidth = 1 };
		using var zero = new SKPaint { Color = new SKColor(255, 255, 255, 50), StrokeWidth = 1 };
		using var divider = new SKPaint { Color = new SKColor(255, 255, 255, 30), StrokeWidth = 1 };
		using var playhead = new SKPaint { Color = new SKColor(255, 90, 90), StrokeWidth = 2 };

		// The ruler (frames), as in the dopesheet
		for (int fr = 0; fr <= frames; fr++)
		{
			float x = FrameToX(fr);
			canvas.DrawLine(x, RulerHeight - (fr % 5 == 0 ? 10 : 4), x, RulerHeight, tick);
			if (fr % 10 == 0) canvas.DrawText(fr.ToString(), x + 2, 14, SKTextAlign.Left, font, text);
		}
		canvas.DrawLine(0, RulerHeight, size.X, RulerHeight, divider);

		canvas.Save();
		canvas.ClipRect(new SKRect(LabelWidth, RulerHeight, size.X, size.Y));

		// Value lines, numbered
		float step = ViewAids.NiceStep((_range.Max - _range.Min) / MathF.Max(1, (Bottom - Top) / 40));
		for (float v = MathF.Ceiling(_range.Min / step) * step; v <= _range.Max; v += step)
		{
			float y = ValueToY(v);
			canvas.DrawLine(LabelWidth, y, size.X, y, MathF.Abs(v) < step / 2 ? zero : grid);
			canvas.DrawText(v.ToString(step >= 1 ? "0" : step >= 0.1f ? "0.0" : "0.00"), LabelWidth + 3, y - 2, SKTextAlign.Left, small, faint);
		}

		using var line = new SKPaint { StrokeWidth = 1.6f, IsAntialias = true, Style = SKPaintStyle.Stroke };
		using var dot = new SKPaint { IsAntialias = true };
		using var picked = new SKPaint { Color = SKColors.White, IsAntialias = true };
		foreach (var c in channels)
		{
			// The curve, a point a pixel
			line.Color = c.Color;
			using (var path = new SKPath())
			{
				bool started = false;
				for (float x = FrameToX(0); x <= FrameToX(frames); x += 1)
				{
					if (c.Sample(XToFrame(x) / Fps) is not { } v) continue;
					var p = new Vector2(x, ValueToY(v));
					if (!started) { path.MoveTo(p.X, p.Y); started = true; }
					else path.LineTo(p.X, p.Y);
				}
				canvas.DrawPath(path, line);
			}

			var keys = c.Keys();
			dot.Color = c.Color;
			for (int i = 0; i < keys.Count; i++)
			{
				var p = At(keys[i].Time, keys[i].Value);
				bool sel = Sel.Contains((c.Tl, FrameOf(keys[i].Time)));
				canvas.DrawCircle(p.X, p.Y, sel ? 5f : 4f, sel ? picked : dot);
				if (sel) canvas.DrawCircle(p.X, p.Y, 2.5f, dot);
			}
		}

		// The focus key's handles: out of it, and into it
		if (Focused() is { } focus)
		{
			var keys = focus.Channel.Keys();
			using var arm = new SKPaint { Color = new SKColor(255, 255, 255, 150), StrokeWidth = 1, IsAntialias = true };
			using var knob = new SKPaint { Color = SKColors.White, IsAntialias = true };
			if (Handles(focus.Channel, focus.Key, keys) is { } outOf)
			{
				canvas.DrawLine(outOf.From.X, outOf.From.Y, outOf.H1.X, outOf.H1.Y, arm);
				canvas.DrawRect(outOf.H1.X - 3.5f, outOf.H1.Y - 3.5f, 7, 7, knob);
			}
			if (Handles(focus.Channel, focus.Key - 1, keys) is { } into)
			{
				canvas.DrawLine(into.To.X, into.To.Y, into.H2.X, into.H2.Y, arm);
				canvas.DrawRect(into.H2.X - 3.5f, into.H2.Y - 3.5f, 7, 7, knob);
			}
		}
		canvas.Restore();

		// The channels, down the left: click one to hide or show its curve
		for (int i = 0; i < listed.Count; i++)
		{
			float y = RulerHeight + 4 + i * RowHeight;
			if (y + RowHeight > size.Y) break;
			var c = listed[i];
			bool off = _hidden.Contains(c.Label);
			using var swatch = new SKPaint { Color = off ? c.Color.WithAlpha(60) : c.Color };
			canvas.DrawRect(8, y + 4, 10, 10, swatch);
			canvas.Save();
			canvas.ClipRect(new SKRect(0, y, LabelWidth - 4, y + RowHeight));
			canvas.DrawText(c.Label, 24, y + 13, SKTextAlign.Left, font, off ? faint : text);
			canvas.Restore();
		}
		canvas.DrawLine(LabelWidth, 0, LabelWidth, size.Y, divider);
		if (listed.Count == 0)
			canvas.DrawText("Nothing keyed to draw: pick bones or parts with keys, or select keys in the dopesheet.",
				LabelWidth + 12, RulerHeight + 30, SKTextAlign.Left, font, faint);

		float px = FrameToX(_state.Frame);
		canvas.DrawLine(px, 0, px, size.Y, playhead);
		canvas.DrawText($"frame {_state.Frame} / {frames}", 8, 14, SKTextAlign.Left, font, text);
	}

	// ======================= hitting things =======================

	private (Channel Channel, int Key)? KeyAt(Vector2 p)
	{
		(Channel, int)? best = null;
		float bestDistance = PickPx;
		foreach (var c in Visible())
		{
			var keys = c.Keys();
			for (int i = 0; i < keys.Count; i++)
			{
				float d = Vector2.Distance(p, At(keys[i].Time, keys[i].Value));
				if (d <= bestDistance) { best = (c, i); bestDistance = d; }
			}
		}
		return best;
	}

	// The focus key's curve (shown), and which key
	private (Channel Channel, int Key)? Focused()
	{
		if (_focus is not { } f || _hidden.Contains($"{f.Tl.Target} {f.Name}")) return null;
		if (!_state.Animation.Timelines.Contains(f.Tl)) return null;
		var channel = ChannelsOf(f.Tl, 0).FirstOrDefault(c => c.Name == f.Name);
		return channel != null && f.Key < channel.Keys().Count ? (channel, f.Key) : null;
	}

	private static (Timeline, string, int) FocusOf((Channel Channel, int Key) hit) => (hit.Channel.Tl, hit.Channel.Name, hit.Key);

	// 1: the focus key's way-out handle, 2: its way-in one, 0: neither
	private int HandleAt(Vector2 p)
	{
		if (Focused() is not { } focus) return 0;
		var keys = focus.Channel.Keys();
		if (Handles(focus.Channel, focus.Key, keys) is { } outOf && Vector2.Distance(p, outOf.H1) <= PickPx) return 1;
		if (Handles(focus.Channel, focus.Key - 1, keys) is { } into && Vector2.Distance(p, into.H2) <= PickPx) return 2;
		return 0;
	}

	// Right-click: the key there becomes (part of) the selection, for its menu. false: no key there
	public bool SelectForMenu(Vector2 p)
	{
		if (KeyAt(p) is not { } hit) return false;
		var id = (hit.Channel.Tl, FrameOf(hit.Channel.Keys()[hit.Key].Time));
		if (!Sel.Contains(id))
		{
			Sel.Clear();
			Sel.Add(id);
		}
		_focus = FocusOf(hit);
		return true;
	}

	// ======================= the mouse =======================

	public void MouseDown(Vector2 p, ModifierKeys modifiers, MouseButton button, int clicks)
	{
		_press = p;
		_changed = false;
		bool shift = modifiers.HasFlag(ModifierKeys.Shift);

		if (button == MouseButton.Middle)
		{
			_manual ??= _range;
			_drag = Drag.Pan;
			return;
		}
		if (button != MouseButton.Left) return;

		if (p.X < LabelWidth)
		{
			// A channel's name: its curve hidden or shown
			int row = (int)MathF.Floor((p.Y - RulerHeight - 4) / RowHeight);
			var listed = Channels();
			if (row >= 0 && row < listed.Count && !_hidden.Remove(listed[row].Label)) _hidden.Add(listed[row].Label);
			return;
		}
		if (clicks == 2 && KeyAt(p) == null)
		{
			_manual = null;   // fit the values again
			return;
		}

		_handle = HandleAt(p);
		if (_handle != 0)
		{
			Begin(Drag.Handle);
			return;
		}

		if (KeyAt(p) is { } hit)
		{
			var id = (hit.Channel.Tl, FrameOf(hit.Channel.Keys()[hit.Key].Time));
			if (shift)
			{
				if (!Sel.Remove(id)) Sel.Add(id);
				if (!Sel.Contains(id)) return;
			}
			else if (!Sel.Contains(id))
			{
				Sel.Clear();
				Sel.Add(id);
			}
			_focus = FocusOf(hit);
			_dragKeys = Sel.ToList();
			_state.Playing = false;
			_state.SetFrame(id.Item2);
			Begin(Drag.Key);
			return;
		}

		if (!shift) Sel.Clear();
		_focus = null;
		_drag = Drag.Scrub;
		Scrub(p.X);
	}

	private void Begin(Drag drag)
	{
		_drag = drag;
		_before = AnimationSnapshot.Capture(_state.Animation);
		_dragRange = _range;   // the values stay put while something is dragged
	}

	public void MouseMove(Vector2 p, ModifierKeys modifiers)
	{
		switch (_drag)
		{
			case Drag.Scrub:
				Scrub(p.X);
				break;
			case Drag.Pan:
				if (_manual is { } m)
				{
					float by = (p.Y - _press.Y) / MathF.Max(1, Bottom - Top) * (m.Max - m.Min);
					_manual = (m.Min + by, m.Max + by);
					_press = p;
				}
				break;
			case Drag.Key when _before != null && Focused() is { } focus:
				DragKeys(p, focus, modifiers.HasFlag(ModifierKeys.Shift));
				break;
			case Drag.Handle when _before != null && Focused() is { } focus:
				DragHandle(p, focus);
				break;
		}
	}

	// The selected keys moved alike: in time (whole frames) and in the dragged curve's number
	private void DragKeys(Vector2 p, (Channel Channel, int Key) focus, bool oneWay)
	{
		_range = _dragRange;
		var delta = p - _press;
		if (oneWay)
		{
			if (MathF.Abs(delta.X) > MathF.Abs(delta.Y)) delta.Y = 0;
			else delta.X = 0;
		}
		int frames = (int)MathF.Round(delta.X / _frameWidth);
		float by = YToValue(_press.Y + delta.Y) - YToValue(_press.Y);

		_before!.Restore();
		int min = _dragKeys.Min(k => k.Frame), max = _dragKeys.Max(k => k.Frame);
		frames = Math.Clamp(frames, -min, _state.FrameCount - max);

		// The number first, where the keys are now: every selected key with a curve of the same name
		string name = focus.Channel.Name;
		foreach (var tl in _dragKeys.Select(k => k.Tl).Distinct())
		{
			var channel = ChannelsOf(tl, 0).FirstOrDefault(c => c.Name == name);
			if (channel == null) continue;
			var keys = channel.Keys();
			var mine = _dragKeys.Where(k => k.Tl == tl).Select(k => k.Frame).ToHashSet();
			for (int i = 0; i < keys.Count; i++)
				if (mine.Contains(FrameOf(keys[i].Time))) channel.SetValue(i, keys[i].Value + by);
		}
		var moved = KeyTimeOps.Move(_dragKeys, f => f + frames, Fps);
		Sel.Clear();
		Sel.UnionWith(moved);
		_state.SetFrame(FrameOf(focus.Channel.Keys().ElementAtOrDefault(focus.Key).Time));
		_changed = frames != 0 || MathF.Abs(by) > 0;
	}

	// The focus key's handle: the Bézier of the stretch out of it (or of the one into it)
	private void DragHandle(Vector2 p, (Channel Channel, int Key) focus)
	{
		_range = _dragRange;
		var keys = focus.Channel.Keys();
		int i = _handle == 1 ? focus.Key : focus.Key - 1;
		if (i < 0 || i + 1 >= keys.Count) return;
		var (t0, v0) = keys[i];
		var (t1, v1) = keys[i + 1];
		var tl = focus.Channel.Tl;
		if (tl.HandlesAt(t0) is not { } h) return;
		float time = XToFrame(p.X) / Fps, value = YToValue(p.Y);
		float x = Math.Clamp((time - t0) / MathF.Max(1e-6f, t1 - t0), 0f, 1f);
		float y = (value - v0) / Span(v0, v1);
		tl.SetBezier(t0, _handle == 1 ? h with { X1 = x, Y1 = y } : h with { X2 = x, Y2 = y });
		_changed = true;
	}

	public void MouseUp()
	{
		if (_changed && _before != null)
		{
			string name = _drag == Drag.Handle ? "Ease a stretch (Bézier handle)" : $"Move {_dragKeys.Count} key(s) in the graph";
			_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(name, _before, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
		}
		_drag = Drag.None;
		_before = null;
		_changed = false;
	}

	public void Wheel(Vector2 p, int delta)
	{
		var range = _manual ?? _range;
		float at = YToValue(p.Y);
		float k = delta > 0 ? 1 / 1.25f : 1.25f;
		_manual = (at + (range.Min - at) * k, at + (range.Max - at) * k);
	}

	private void Scrub(float x)
	{
		_state.Playing = false;
		_state.SetFrame((int)MathF.Round(XToFrame(x)));
	}
}
