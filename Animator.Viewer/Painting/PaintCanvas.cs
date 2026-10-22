using System.Numerics;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer.Painting;

public class PaintCanvas
{
	private readonly PaintSession _s;
	private readonly EyedropperTool _altPicker = new();

	public float Zoom { get; private set; } = 4f;
	public Vector2 Pan { get; private set; }
	public Vector2 ViewSize { get; set; }

	private bool _needsFit;
	private PaintTool? _active;
	private bool _panning;
	private Vector2 _lastScreen;
	private Vector2 _mouse;
	private bool _mouseInside;

	// Busy = mid-drag, or a figure is still being placed. Undo waits until it's done.
	public bool IsBusy => _active != null || _s.Tool.InProgress;

	public PaintCanvas(PaintSession session) => _s = session;

	public Vector2 ScreenToImage(Vector2 p) => (p - ViewSize / 2) / Zoom + Pan;
	public Vector2 ImageToScreen(Vector2 p) => (p - Pan) * Zoom + ViewSize / 2;

	public void RequestFit() => _needsFit = true;

	private void Fit(Asset asset)
	{
		float z = MathF.Min(ViewSize.X / asset.Width, ViewSize.Y / asset.Height) * 0.85f;
		Zoom = z >= 1 ? MathF.Floor(z) : z;
		Pan = new Vector2(asset.Width, asset.Height) / 2;
	}

	// ---------- drawing ----------

	public void Draw(SKCanvas c)
	{
		c.Clear(new SKColor(36, 39, 46));
		if (_s.Asset is not { } asset || _s.Composite is not { } img) return;

		if (_needsFit && ViewSize.X > 0) { Fit(asset); _needsFit = false; }
		_s.ViewZoom = Zoom;

		var tl = ImageToScreen(Vector2.Zero);
		var br = ImageToScreen(new Vector2(asset.Width, asset.Height));
		var rect = new SKRect(tl.X, tl.Y, br.X, br.Y);

		DrawChecker(c, rect);

		c.Save();
		c.Translate(ViewSize.X / 2, ViewSize.Y / 2);
		c.Scale(Zoom);
		c.Translate(-Pan.X, -Pan.Y);
		c.DrawBitmap(img, 0, 0, AssetStore.Nearest, null);
		c.Restore();

		if (Zoom >= 8) DrawGrid(c, asset, rect);

		using (var border = new SKPaint { Color = new SKColor(255, 255, 255, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
			c.DrawRect(rect, border);

		DrawSelection(c);
		DrawGradientGuide(c);
		DrawFigureGuide(c);
		DrawCursor(c);
	}

	private void DrawChecker(SKCanvas c, SKRect rect)
	{
		var vis = rect;
		vis.Intersect(new SKRect(0, 0, ViewSize.X, ViewSize.Y));
		if (vis.IsEmpty) return;

		const float cell = 8;
		using var light = new SKPaint { Color = new SKColor(200, 200, 200) };
		using var dark = new SKPaint { Color = new SKColor(160, 160, 160) };
		c.DrawRect(vis, light);

		int x0 = (int)((vis.Left - rect.Left) / cell), x1 = (int)MathF.Ceiling((vis.Right - rect.Left) / cell);
		int y0 = (int)((vis.Top - rect.Top) / cell), y1 = (int)MathF.Ceiling((vis.Bottom - rect.Top) / cell);

		c.Save();
		c.ClipRect(vis);
		for (int y = y0; y < y1; y++)
			for (int x = x0; x < x1; x++)
				if (((x + y) & 1) == 1)
					c.DrawRect(rect.Left + x * cell, rect.Top + y * cell, cell, cell, dark);
		c.Restore();
	}

	private void DrawGrid(SKCanvas c, Asset asset, SKRect rect)
	{
		using var grid = new SKPaint { Color = new SKColor(0, 0, 0, 45), StrokeWidth = 1 };
		for (int x = 0; x <= asset.Width; x++)
		{
			float sx = ImageToScreen(new Vector2(x, 0)).X;
			if (sx >= 0 && sx <= ViewSize.X) c.DrawLine(sx, rect.Top, sx, rect.Bottom, grid);
		}
		for (int y = 0; y <= asset.Height; y++)
		{
			float sy = ImageToScreen(new Vector2(0, y)).Y;
			if (sy >= 0 && sy <= ViewSize.Y) c.DrawLine(rect.Left, sy, rect.Right, sy, grid);
		}
	}

	private void DrawSelection(SKCanvas c)
	{
		if (_s.Selection is not { } r) return;
		var a = ImageToScreen(new Vector2(r.Left, r.Top));
		var b = ImageToScreen(new Vector2(r.Right, r.Bottom));
		var rect = new SKRect(a.X, a.Y, b.X, b.Y);

		float phase = Environment.TickCount64 / 60 % 8;
		using var dash = SKPathEffect.CreateDash(new[] { 4f, 4f }, phase);
		using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
		using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = dash };
		c.DrawRect(rect, black);
		c.DrawRect(rect, white);
	}

	private void DrawGradientGuide(SKCanvas c)
	{
		if (_s.Tool is not GradientTool { Dragging: true } g) return;
		var a = ImageToScreen(g.Start);
		var b = ImageToScreen(g.End);
		using var line = new SKPaint { Color = SKColors.White, StrokeWidth = 1.5f, IsAntialias = true };
		using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
		c.DrawLine(a.X, a.Y, b.X, b.Y, line);
		c.DrawCircle(a.X, a.Y, 3, dot);
	}

	// Small squares on placed polyline/curve points. The first one is bigger: click it to close.
	private void DrawFigureGuide(SKCanvas c)
	{
		if (_s.Tool is not FigureTool { PlacingPoints: true } f) return;
		using var fill = new SKPaint { Color = SKColors.White };
		using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
		for (int i = 0; i < f.Points.Count; i++)
		{
			var p = ImageToScreen(f.Points[i]);
			float r = i == 0 ? 5f : 3.5f;
			c.DrawRect(p.X - r, p.Y - r, r * 2, r * 2, fill);
			c.DrawRect(p.X - r, p.Y - r, r * 2, r * 2, edge);
		}
	}

	private void DrawCursor(SKCanvas c)
	{
		bool square = _s.Tool is StrokeTool { ToolKind: StrokeTool.Kind.Pencil };
		bool round = _s.Tool is MixerTool || _s.Tool is StrokeTool { ToolKind: not StrokeTool.Kind.Pencil };
		if (!_mouseInside || !(square || round)) return;

		using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
		using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

		if (square)
		{
			var img = ScreenToImage(_mouse);
			int half = (_s.Size - 1) / 2;
			var tl = ImageToScreen(new Vector2(MathF.Floor(img.X) - half, MathF.Floor(img.Y) - half));
			float side = _s.Size * Zoom;
			c.DrawRect(tl.X, tl.Y, side, side, dark);
			c.DrawRect(tl.X, tl.Y, side, side, light);
		}
		else
		{
			float r = MathF.Max(2, _s.Size * Zoom / 2);
			c.DrawCircle(_mouse.X, _mouse.Y, r, dark);
			c.DrawCircle(_mouse.X, _mouse.Y, r, light);
		}
	}

	// ---------- input ----------

	private void ReadModifiers()
	{
		var m = Keyboard.Modifiers;
		_s.ShiftHeld = m.HasFlag(ModifierKeys.Shift);
		_s.CtrlHeld = m.HasFlag(ModifierKeys.Control);
	}

	public void MouseDown(Vector2 screen, MouseButton button, bool alt, int clickCount)
	{
		_lastScreen = screen;
		if (button is MouseButton.Middle or MouseButton.Right) { _panning = true; return; }
		if (button != MouseButton.Left || _s.Asset == null || _active != null) return;

		ReadModifiers();
		_s.ClickCount = clickCount;
		_active = alt ? _altPicker : _s.Tool;
		_active.Down(_s, ScreenToImage(screen));
	}

	public void MouseMove(Vector2 screen)
	{
		_mouse = screen;
		_mouseInside = true;
		ReadModifiers();

		if (_panning) Pan -= (screen - _lastScreen) / Zoom;
		else if (_active != null) _active.Move(_s, ScreenToImage(screen));
		else if (_s.Asset != null) _s.Tool.Hover(_s, ScreenToImage(screen));

		_lastScreen = screen;
	}

	public void MouseUp(MouseButton button)
	{
		if (button is MouseButton.Middle or MouseButton.Right) _panning = false;
		else if (button == MouseButton.Left && _active != null)
		{
			_active.Up(_s);
			_active = null;
		}
	}

	public void MouseLeave() => _mouseInside = false;

	public void Wheel(Vector2 screen, int delta)
	{
		var before = ScreenToImage(screen);
		Zoom = Math.Clamp(Zoom * (delta > 0 ? 1.25f : 0.8f), 0.25f, 64f);
		Pan += before - ScreenToImage(screen);
	}
}