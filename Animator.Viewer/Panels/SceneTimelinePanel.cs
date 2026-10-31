using System.Numerics;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer.Panels;

// The scene timeline, shown at scene level: a row per object (front first, like the Objects
// list) with its clips as blocks.
//   Click the ruler or an empty spot: move the playhead. Click a clip: select it.
//   Drag a clip: move it. Drag its right edge: longer (it loops or holds) or shorter.
//   Drag its left edge: trim the start. Double-click a clip: edit that animation in place.
//   Right-click: add a clip, and more. Wheel: scroll rows, Shift+wheel: scroll in time,
//   Ctrl+wheel: zoom.
public sealed class SceneTimelinePanel
{
	private readonly Func<Scene> _scene;
	private readonly SceneState _sel;
	private readonly EditorState _state;   // frame rate, playing, undo

	private const float LabelWidth = 150f;
	private const float RulerHeight = 24f;
	private const float RowHeight = 24f;
	private const float LeftPad = 10f;
	private const float EdgePx = 6f;   // this close to a clip's edge grabs the edge

	private float _fit = 10f;          // pixels per frame when the whole scene fits
	private float _zoom = 1f;          // 1 = fit
	private float _frameWidth = 10f;
	private float _scrollX, _scrollY;
	private Vector2 _view;

	private enum Drag { None, Scrub, Move, End, Start }
	private Drag _drag;
	private SceneObject? _dragObject;
	private SceneClip? _dragClip;
	private float _pressX;
	private (float Start, float Length, float Offset) _clipAtPress;
	private SceneSnapshot? _before;

	// Double-click on a clip: the main window opens its animation in place
	public event Action<SceneObject, SceneClip>? ClipOpened;

	// Right-click on a row: the object, the clip under the mouse (or null), and the frame there
	public event Action<SceneObject, SceneClip?, int>? MenuRequested;

	public SceneTimelinePanel(Func<Scene> scene, SceneState sel, EditorState state)
	{
		_scene = scene;
		_sel = sel;
		_state = state;
	}

	private int Fps => Math.Max(1, _state.Fps);
	private int SceneFrames => Math.Max(1, (int)MathF.Round(_scene().Duration * Fps));
	public int Frame => (int)MathF.Round(_sel.Time * Fps);

	// Front first, like the Objects list
	private List<SceneObject> Rows => _scene().Objects.AsEnumerable().Reverse().ToList();

	private float FrameToX(float frame) => LabelWidth + LeftPad + frame * _frameWidth - _scrollX;
	private float TimeToX(float time) => FrameToX(time * Fps);
	private float XToFrameF(float x) => (x - LabelWidth - LeftPad + _scrollX) / _frameWidth;
	private int XToFrame(float x) => (int)MathF.Round(XToFrameF(x));
	private float RowTop(int row) => RulerHeight + row * RowHeight - _scrollY;
	private int RowAt(float y) => (int)MathF.Floor((y - RulerHeight + _scrollY) / RowHeight);
	private float Snap(float time) => MathF.Round(time * Fps) / Fps;

	// ======================= drawing =======================

	public void Draw(SKCanvas canvas, Vector2 size)
	{
		_view = size;
		var scene = _scene();
		var rows = Rows;
		int frames = SceneFrames;
		_fit = MathF.Max(2f, (size.X - LabelWidth - LeftPad * 2) / frames);
		_frameWidth = _fit * _zoom;
		FollowPlayhead();
		ClampScroll();

		canvas.Clear(Themes.Sk("Canvas"));

		using var font = new SKFont { Size = 12 };
		using var smallFont = new SKFont { Size = 11 };
		using var text = new SKPaint { Color = Themes.Sk("CanvasText"), IsAntialias = true };
		using var dimText = new SKPaint { Color = Themes.SkAlpha("CanvasText", 110), IsAntialias = true };
		using var hint = new SKPaint { Color = Themes.SkAlpha("CanvasText", 90), IsAntialias = true };
		using var tick = new SKPaint { Color = Themes.SkAlpha("CanvasText", 90), StrokeWidth = 1 };
		using var divider = new SKPaint { Color = Themes.SkAlpha("CanvasText", 30), StrokeWidth = 1 };
		using var rowSel = new SKPaint { Color = new SKColor(110, 220, 120, 30) };
		using var pastEnd = new SKPaint { Color = new SKColor(0, 0, 0, 90) };
		using var endLine = new SKPaint { Color = Themes.SkAlpha("CanvasText", 120), StrokeWidth = 1.5f };
		using var playhead = new SKPaint { Color = Themes.Sk("Playhead"), StrokeWidth = 2 };
		using var scrollbar = new SKPaint { Color = Themes.SkAlpha("CanvasText", 50), IsAntialias = true };

		// Ruler, with labels far enough apart to read
		int labelStep = LabelStep(_frameWidth);
		int firstFrame = Math.Max(0, (int)MathF.Floor(XToFrameF(LabelWidth)));
		int lastFrame = (int)MathF.Ceiling(XToFrameF(size.X));
		canvas.Save();
		canvas.ClipRect(new SKRect(LabelWidth, 0, size.X, RulerHeight));
		for (int f = firstFrame; f <= lastFrame; f++)
		{
			bool major = f % labelStep == 0;
			if (!major && _frameWidth < 3) continue;   // too crowded for every frame's tick
			float x = FrameToX(f);
			float h = major ? 10 : f % 5 == 0 ? 6 : 3;
			canvas.DrawLine(x, RulerHeight - h, x, RulerHeight, tick);
			if (major) canvas.DrawText(f.ToString(), x + 2, 14, SKTextAlign.Left, font, text);
		}
		canvas.Restore();
		canvas.DrawLine(0, RulerHeight, size.X, RulerHeight, divider);

		// Rows, scrolled, never over the ruler
		canvas.Save();
		canvas.ClipRect(new SKRect(0, RulerHeight, size.X, size.Y));
		for (int i = 0; i < rows.Count; i++)
		{
			float y = RowTop(i);
			if (y + RowHeight < RulerHeight || y > size.Y) continue;
			var obj = rows[i];

			if (obj == _sel.Selected) canvas.DrawRect(0, y, size.X, RowHeight, rowSel);
			canvas.DrawLine(0, y + RowHeight, size.X, y + RowHeight, divider);

			canvas.Save();
			canvas.ClipRect(new SKRect(0, y, LabelWidth - 4, y + RowHeight));
			canvas.DrawText(obj.Name, 8, y + 16, SKTextAlign.Left, font, obj.Visible ? text : dimText);
			canvas.Restore();

			canvas.Save();
			canvas.ClipRect(new SKRect(LabelWidth, y, size.X, y + RowHeight));
			if (obj.Clips.Count == 0)
			{
				string preview = (obj.FindAnimation(obj.PreviewAnimation) ?? obj.Animations.FirstOrDefault())?.Name ?? "-";
				canvas.DrawText($"no clips: loops \"{preview}\"   (right-click to add a clip)",
								LabelWidth + LeftPad + 4, y + 16, SKTextAlign.Left, smallFont, hint);
			}
			foreach (var clip in obj.Clips)
				DrawClip(canvas, obj, clip, y, smallFont);
			canvas.Restore();
		}

		// A thin scrollbar when there are more rows than fit
		float content = rows.Count * RowHeight;
		float visible = size.Y - RulerHeight;
		if (content > visible && visible > 0)
		{
			float barHeight = MathF.Max(16, visible * visible / content);
			float barTop = RulerHeight + (visible - barHeight) * (_scrollY / (content - visible));
			canvas.DrawRoundRect(size.X - 6, barTop, 4, barHeight, 2, 2, scrollbar);
		}
		canvas.Restore();

		// After the scene's end: dimmed
		float endX = FrameToX(frames);
		if (endX < size.X)
		{
			float from = MathF.Max(LabelWidth, endX);
			canvas.DrawRect(from, RulerHeight, size.X - from, size.Y - RulerHeight, pastEnd);
		}
		if (endX >= LabelWidth) canvas.DrawLine(endX, 0, endX, size.Y, endLine);

		canvas.DrawLine(LabelWidth, 0, LabelWidth, size.Y, divider);

		float px = TimeToX(_sel.Time);
		if (px >= LabelWidth) canvas.DrawLine(px, 0, px, size.Y, playhead);
		canvas.DrawText($"scene {Frame} / {frames}", 8, 16, SKTextAlign.Left, font, text);
	}

	// One clip: a block in its animation's colour, ticks where the animation starts over (or a
	// paler tail where it holds its last frame), its name, and grips on its edges when selected
	private void DrawClip(SKCanvas canvas, SceneObject obj, SceneClip clip, float y, SKFont font)
	{
		float x0 = TimeToX(clip.Start), x1 = MathF.Max(x0 + 2, TimeToX(clip.End));
		if (x1 < LabelWidth || x0 > _view.X) return;
		var rect = new SKRect(x0, y + 3, x1, y + RowHeight - 3);
		bool missing = !obj.Animations.Contains(clip.Animation);
		bool selected = clip == _sel.SelectedClip;

		using var fill = new SKPaint
		{
			IsAntialias = true,
			Color = missing ? new SKColor(160, 60, 60, 200) : SKColor.FromHsl(HueOf(clip.Animation.Name), 45, 38).WithAlpha(210)
		};
		using var tail = new SKPaint { Color = new SKColor(0, 0, 0, 70) };
		using var loopTick = new SKPaint { Color = Themes.SkAlpha("CanvasText", 110), StrokeWidth = 1 };
		using var outline = new SKPaint
		{
			IsAntialias = true,
			Style = SKPaintStyle.Stroke,
			StrokeWidth = selected ? 2 : 1,
			Color = selected ? SKColors.White : Themes.SkAlpha("CanvasText", 60)
		};
		using var label = new SKPaint { Color = new SKColor(245, 245, 245), IsAntialias = true };

		canvas.DrawRoundRect(rect, 4, 4, fill);

		// Where the animation reaches its end inside the clip: it starts over, or stops moving
		float d = clip.Animation.Duration;
		if (!missing && d > 0)
		{
			int guard = 0;
			for (float t = clip.Start + d - clip.Offset; t < clip.End - 1e-4f && guard++ < 1000; t += d)
			{
				float x = TimeToX(t);
				if (clip.Loop)
				{
					canvas.DrawLine(x, rect.Top + 3, x, rect.Bottom - 3, loopTick);
					continue;
				}
				canvas.DrawRect(new SKRect(x, rect.Top, x1, rect.Bottom), tail);   // holds the last frame
				break;
			}
		}

		canvas.Save();
		canvas.ClipRect(rect);
		string name = missing ? $"{clip.Animation.Name} (deleted)" : clip.Animation.Name + (clip.Loop ? "  ↻" : "");
		canvas.DrawText(name, x0 + 6, rect.Bottom - 5, SKTextAlign.Left, font, label);
		canvas.Restore();

		canvas.DrawRoundRect(rect, 4, 4, outline);
		if (selected)
		{
			using var grip = new SKPaint { Color = SKColors.White };
			canvas.DrawRect(x0 - 1, rect.Top + 4, 3, rect.Height - 8, grip);
			canvas.DrawRect(x1 - 2, rect.Top + 4, 3, rect.Height - 8, grip);
		}
	}

	// Ruler labels every 1, 2, 5, 10, 20, 50… frames, whichever keeps them at least ~40 px apart
	private static int LabelStep(float frameWidth)
	{
		int[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };
		foreach (int s in steps)
			if (s * frameWidth >= 40) return s;
		return 2000;
	}

	// The same colour for the same animation name, every time (FNV-1a hash → hue)
	private static float HueOf(string name)
	{
		uint hash = 2166136261;
		foreach (char c in name)
		{
			hash ^= c;
			hash *= 16777619;
		}
		return hash % 360;
	}

	// ======================= scrolling & zoom =======================

	public void Wheel(Vector2 p, int delta)
	{
		var mods = Keyboard.Modifiers;
		if (mods.HasFlag(ModifierKeys.Control))
		{
			// Zoom around the mouse: the frame under it stays under it
			float frameUnder = XToFrameF(MathF.Max(p.X, LabelWidth));
			_zoom = Math.Clamp(_zoom * (delta > 0 ? 1.25f : 0.8f), 1f, 60f);
			_frameWidth = _fit * _zoom;
			_scrollX = LabelWidth + LeftPad + frameUnder * _frameWidth - MathF.Max(p.X, LabelWidth);
		}
		else if (mods.HasFlag(ModifierKeys.Shift))
		{
			_scrollX -= delta / 120f * 80f;
		}
		else
		{
			_scrollY -= delta / 120f * RowHeight * 2;
		}
		ClampScroll();
	}

	private void ClampScroll()
	{
		float contentX = LeftPad * 2 + SceneFrames * _frameWidth;
		float visibleX = MathF.Max(0, _view.X - LabelWidth);
		_scrollX = Math.Clamp(_scrollX, 0, MathF.Max(0, contentX - visibleX));

		float contentY = _scene().Objects.Count * RowHeight;
		float visibleY = MathF.Max(0, _view.Y - RulerHeight);
		_scrollY = Math.Clamp(_scrollY, 0, MathF.Max(0, contentY - visibleY));
	}

	// While playing, the view moves along so the playhead stays in sight
	private void FollowPlayhead()
	{
		if (!_state.Playing || _zoom <= 1f) return;
		float x = TimeToX(_sel.Time);
		if (x > _view.X - 20 || x < LabelWidth) _scrollX += x - (LabelWidth + LeftPad + 20);
	}

	// ======================= mouse =======================

	// The object row under a point (null over the ruler or below the last row)
	private SceneObject? ObjectAt(Vector2 p)
	{
		if (p.Y <= RulerHeight) return null;
		var rows = Rows;
		int row = RowAt(p.Y);
		return row >= 0 && row < rows.Count ? rows[row] : null;
	}

	// The clip under a point, and which part of it: its right edge, its left edge, or its body
	private SceneClip? HitClip(SceneObject obj, float x, out Drag part)
	{
		part = Drag.None;
		for (int i = obj.Clips.Count - 1; i >= 0; i--)
		{
			var clip = obj.Clips[i];
			float x0 = TimeToX(clip.Start), x1 = MathF.Max(x0 + 2, TimeToX(clip.End));
			if (x < x0 - EdgePx / 2 || x > x1 + EdgePx / 2) continue;
			part = x >= x1 - EdgePx ? Drag.End : x <= x0 + EdgePx ? Drag.Start : Drag.Move;
			return clip;
		}
		return null;
	}

	public void MouseDown(Vector2 p, MouseButton button, int clickCount)
	{
		var obj = ObjectAt(p);

		if (button == MouseButton.Right)
		{
			if (obj == null || p.X < LabelWidth) return;
			var hit = HitClip(obj, p.X, out _);
			_sel.Selected = obj;
			_sel.SelectedClip = hit;
			MenuRequested?.Invoke(obj, hit, Math.Max(0, XToFrame(p.X)));
			return;
		}
		if (button != MouseButton.Left) return;

		if (p.Y <= RulerHeight)
		{
			BeginScrub(p.X);
			return;
		}
		if (obj == null) return;

		// The name column: just select the object
		if (p.X < LabelWidth)
		{
			_sel.Selected = obj;
			return;
		}

		if (HitClip(obj, p.X, out var part) is { } clip)
		{
			_sel.Selected = obj;
			_sel.SelectedClip = clip;
			if (clickCount >= 2)
			{
				ClipOpened?.Invoke(obj, clip);
				return;
			}
			_drag = part;
			_dragObject = obj;
			_dragClip = clip;
			_pressX = p.X;
			_clipAtPress = (clip.Start, clip.Length, clip.Offset);
			_before = SceneSnapshot.Capture(_scene());
			return;
		}

		// An empty spot of a row: select the object and move the playhead there
		_sel.Selected = obj;
		BeginScrub(p.X);
	}

	private void BeginScrub(float x)
	{
		_drag = Drag.Scrub;
		_state.Playing = false;
		Scrub(x);
	}

	private void Scrub(float x) => _sel.Time = Math.Clamp(XToFrame(x), 0, SceneFrames) / (float)Fps;

	public void MouseMove(Vector2 p)
	{
		if (_drag == Drag.Scrub)
		{
			Scrub(p.X);
			return;
		}
		if (_dragObject is not { } obj || _dragClip is not { } clip) return;

		float moved = (p.X - _pressX) / _frameWidth / Fps;   // seconds
		float frame = 1f / Fps;
		var (start, length, offset) = _clipAtPress;
		switch (_drag)
		{
			case Drag.Move:
				SceneOps.MoveClip(obj, clip, Snap(start + moved));
				break;
			case Drag.End:
				SceneOps.SetClipEnd(obj, clip, Snap(start + length + moved), frame);
				break;
			case Drag.Start:
				// From the clip as it was at the press, so the trim always measures from there
				clip.Start = start;
				clip.Length = length;
				clip.Offset = offset;
				SceneOps.SetClipStart(obj, clip, Snap(start + moved), frame);
				break;
		}
	}

	public void MouseUp()
	{
		if (_before != null && _dragClip is { } clip && (clip.Start, clip.Length, clip.Offset) != _clipAtPress)
		{
			string what = _drag switch
			{
				Drag.Move => "Move",
				Drag.End => clip.Length > _clipAtPress.Length ? "Lengthen" : "Shorten",
				_ => "Trim"
			};
			_state.Undo.Push(new SnapshotCommand<SceneSnapshot>($"{what} clip {clip.Animation.Name} of {_dragObject?.Name}",
				_before, SceneSnapshot.Capture(_scene()), s => s.Restore()));
		}
		_drag = Drag.None;
		_dragObject = null;
		_dragClip = null;
		_before = null;
	}
}
