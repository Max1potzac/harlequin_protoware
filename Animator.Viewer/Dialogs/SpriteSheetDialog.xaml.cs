using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Animator.Viewer.Dialogs;

// File > Import sprite sheet: cut the sheet into pieces (a grid, its separate sprites, by hand,
// or as its JSON says), pick which pieces play as animations, and bring them in as drawn
// objects or as parts of the open object
public partial class SpriteSheetDialog : Window
{
	private sealed record AnchorChoice(FrameAnchor Anchor, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly AnchorChoice[] Anchors =
	{
		new(FrameAnchor.Centre, "Middle"),
		new(FrameAnchor.Bottom, "Bottom middle"),
		new(FrameAnchor.Origin, "The JSON's origin")
	};

	public const int MaxPieceSize = 4096;   // like any imported picture
	private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

	private readonly SKBitmap _original;
	private SKBitmap? _keyed;       // a copy with the background colour made see-through
	private SKBitmap _sheet;        // the one in use: one of those two
	private byte[] _alpha;          // its opacity, for cutting
	private readonly SpriteSheetJson? _json;
	private readonly int _fps;
	private readonly HashSet<SheetPiece> _picked = new();
	private bool _ready, _syncing;
	private bool _jsonOrder;   // cut by the JSON: new animations play its frames in its order

	// The sheet view: sheet pixel p shows at p × _zoom + _pan (in the view's units)
	private float _zoom = 1f;
	private Vector2 _pan;
	private bool _fitted;

	private enum Drag { None, Pan, Band, Draw }
	private Drag _drag;
	private Vector2 _lastView, _pressSheet, _nowSheet;

	// The preview plays the picked animation at the project's frame rate
	private readonly DispatcherTimer _timer;
	private SheetAnimation? _previewOf;
	private int _previewFrame;

	public SheetImportPlan Plan { get; }
	public bool IntoOpenObject => PartsChoice.IsChecked == true;
	public bool SharpPixels => SharpBox.IsChecked == true;

	// partsLabel: the "as parts of" choice, e.g. As parts of "hero", on its bone "arm".
	// sharpPixels: whether pictures are shown with hard pixel edges now.
	public SpriteSheetDialog(SKBitmap sheet, string name, SpriteSheetJson? json, string partsLabel, bool partsPossible,
		bool sharpPixels, int fps)
	{
		InitializeComponent();
		_original = _sheet = sheet;
		_alpha = SheetImporter.Alpha(sheet);
		_json = json;
		_fps = Math.Max(1, fps);
		Plan = new SheetImportPlan(sheet, name);

		SheetTitle.Text = $"{name}   ({sheet.Width} × {sheet.Height} pixels)";
		AnimationAnchor.ItemsSource = Anchors;

		// A sheet drawn on a flat colour: that colour can become see-through. It's ticked to begin
		// with when the sheet has no see-through pixels and the corners' colour covers much of it.
		var corner = sheet.GetPixel(0, 0);
		KeyBox.IsEnabled = corner.Alpha == 255;
		KeyBox.Content = corner.Alpha == 255
			? $"Make the background colour see-through (#{corner.Red:X2}{corner.Green:X2}{corner.Blue:X2}, as in the top-left corner)"
			: "The background is see-through already";
		if (corner.Alpha == 255 && _alpha.All(a => a == 255))
		{
			var keyed = SheetImporter.WithoutColourAt(sheet, 0, 0, out int taken);
			bool corners = new[]
			{
				sheet.GetPixel(sheet.Width - 1, 0), sheet.GetPixel(0, sheet.Height - 1), sheet.GetPixel(sheet.Width - 1, sheet.Height - 1)
			}.All(c => c == corner);
			if (corners && taken >= 0.3 * sheet.Width * sheet.Height)
			{
				_keyed = keyed;
				KeyBox.IsChecked = true;
				UseSheet(keyed);
			}
			else keyed.Dispose();
		}

		// A strip of square frames is a common sheet: guess that, else 64 × 64
		int shortSide = Math.Min(sheet.Width, sheet.Height), longSide = Math.Max(sheet.Width, sheet.Height);
		int cell = longSide >= 2 * shortSide && longSide % shortSide == 0 ? shortSide : Math.Min(64, shortSide);
		CellWidthBox.Text = CellHeightBox.Text = cell.ToString();
		OffsetXBox.Text = OffsetYBox.Text = GapXBox.Text = GapYBox.Text = "0";
		JoinBox.Text = "2";
		SmallestBox.Text = "3";

		JsonMode.IsEnabled = json != null;
		JsonHint.Text = json != null
			? $"Found {name}.json next to it, describing {json.Frames.Count} frame(s)" +
			  (json.Meta.FrameTags.Count > 0 ? $" and {json.Meta.FrameTags.Count} animation(s)." : ".")
			: "No JSON file with the same name next to the picture.";
		PartsChoice.Content = partsLabel;
		PartsChoice.IsEnabled = partsPossible;
		ObjectsChoice.IsChecked = true;
		TogetherBox.IsChecked = true;
		StillsBox.IsChecked = true;

		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / _fps) };
		_timer.Tick += (_, _) =>
		{
			if (CurrentAnimation == null) return;
			_previewFrame++;
			PreviewView.InvalidateVisual();
		};
		Loaded += (_, _) => _timer.Start();
		Closed += (_, _) =>
		{
			_timer.Stop();
			// The copy without the background goes with the plan when it's imported (whoever
			// imports it disposes of it); otherwise it's not needed any more
			if (_keyed != null && (DialogResult != true || Plan.Sheet != _keyed)) _keyed.Dispose();
		};

		_ready = true;
		// The JSON if there is one, else the separate sprites of a see-through sheet, else a grid
		(json != null ? JsonMode : _alpha.Any(a => a < 255) ? FindMode : GridMode).IsChecked = true;

		// Small pieces with hard edges are most likely pixel art
		var sizes = Plan.Pieces.Select(p => Math.Max(p.Rect.W, p.Rect.H)).OrderBy(s => s).ToList();
		bool small = sizes.Count > 0 && sizes[sizes.Count / 2] <= 128;
		SharpBox.IsChecked = sharpPixels || small && _alpha.All(a => a is 0 or 255);
	}

	private bool Hand => HandMode.IsChecked == true;

	// ======================= the background colour =======================

	private void UseSheet(SKBitmap sheet)
	{
		_sheet = sheet;
		_alpha = SheetImporter.Alpha(sheet);
		Plan.Sheet = sheet;
	}

	private void Key_Click(object sender, RoutedEventArgs e)
	{
		if (KeyBox.IsChecked == true)
		{
			_keyed ??= SheetImporter.WithoutColourAt(_original, 0, 0, out _);
			UseSheet(_keyed);
		}
		else
		{
			UseSheet(_original);
		}
		if (Hand) RefreshAll(CurrentAnimation);   // drawn by hand: the pieces stay, only what's in them changes
		else Cut();                               // finding sprites needs the see-through pixels
	}

	// ======================= cutting =======================

	// "Or a grid of 8 × 4": the frame size that splits the sheet evenly (after the offset and gaps)
	private void Split_Changed(object sender, TextChangedEventArgs e)
	{
		if (!_ready || !int.TryParse(SplitXBox.Text.Trim(), out int across) || !int.TryParse(SplitYBox.Text.Trim(), out int down) ||
			across < 1 || down < 1)
			return;
		int Number(TextBox box) => int.TryParse(box.Text.Trim(), out int n) ? Math.Max(0, n) : 0;
		int w = (_sheet.Width - Number(OffsetXBox) - (across - 1) * Number(GapXBox)) / across;
		int h = (_sheet.Height - Number(OffsetYBox) - (down - 1) * Number(GapYBox)) / down;
		if (w < 1 || h < 1) return;
		CellWidthBox.Text = w.ToString();
		CellHeightBox.Text = h.ToString();
	}

	private void Mode_Changed(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		GridFields.IsEnabled = GridMode.IsChecked == true;
		FindFields.IsEnabled = FindMode.IsChecked == true;
		CutButton.IsEnabled = !Hand;
		if (!Hand) Cut();   // by hand: you go on from the pieces there are (remove some, draw more)
		else RefreshAll(CurrentAnimation);
	}

	private void Cut_Click(object sender, RoutedEventArgs e) => Cut();

	private void Field_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		Cut();
		e.Handled = true;   // not the Import button
	}

	// Cuts the sheet the chosen way (the animations made so far go)
	private void Cut()
	{
		ErrorText.Text = "";
		_picked.Clear();

		_jsonOrder = JsonMode.IsChecked == true && _json != null;
		if (_jsonOrder)
		{
			int rotated = Plan.ReadJson(_json!, _fps);
			if (rotated > 0)
				ErrorText.Text = $"{rotated} frame(s) are stored turned on their side, which isn't supported: they were left out.";
			RefreshAll(Plan.Animations.FirstOrDefault());
			return;
		}

		List<PixelRect> rects;
		if (GridMode.IsChecked == true)
		{
			if (!Whole(CellWidthBox, 1, _sheet.Width, out int w, "frame width") ||
				!Whole(CellHeightBox, 1, _sheet.Height, out int h, "frame height") ||
				!Whole(OffsetXBox, 0, _sheet.Width - 1, out int ox, "first frame's left edge") ||
				!Whole(OffsetYBox, 0, _sheet.Height - 1, out int oy, "first frame's top edge") ||
				!Whole(GapXBox, 0, _sheet.Width, out int gx, "gap across") ||
				!Whole(GapYBox, 0, _sheet.Height, out int gy, "gap down"))
				return;
			rects = SpriteSheetSlicer.Grid(_sheet.Width, _sheet.Height, w, h, ox, oy, gx, gy, _alpha);
		}
		else
		{
			if (!Whole(JoinBox, 0, 64, out int join, "joining distance") ||
				!Whole(SmallestBox, 1, MaxPieceSize, out int smallest, "smallest size"))
				return;
			rects = SpriteSheetSlicer.Find(_alpha, _sheet.Width, _sheet.Height, join, smallest);
		}

		Plan.Pieces.Clear();
		Plan.Animations.Clear();
		Plan.Origin = null;
		for (int i = 0; i < rects.Count; i++)
			Plan.Pieces.Add(new SheetPiece { Rect = rects[i], Name = $"{Plan.Name}_{i + 1}" });
		if (rects.Count == 0) ErrorText.Text = "That cut found no pieces with anything visible in them.";
		RefreshAll();
	}

	private bool Whole(TextBox box, int min, int max, out int value, string what)
	{
		if (int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max) return true;
		ErrorText.Text = $"The {what} is a whole number from {min} to {max}.";
		box.Focus();
		return false;
	}

	// ======================= animations =======================

	private SheetAnimation? CurrentAnimation => (AnimationList.SelectedItem as ListBoxItem)?.Tag as SheetAnimation;

	private void MakeAnimation_Click(object sender, RoutedEventArgs e)
	{
		ErrorText.Text = "";
		var picked = Plan.Pieces.Where(p => _picked.Contains(p) && !p.LeftOut).ToList();
		if (picked.Count == 0)
		{
			ErrorText.Text = "Pick the pieces on the sheet first (click them, or drag around them).";
			return;
		}
		// Left to right, top to bottom; a packed sheet's frames can be anywhere, so its JSON's order
		var frames = _jsonOrder ? picked : SpriteSheetSlicer.Rows(picked, p => p.Rect).SelectMany(row => row).ToList();
		var made = MakeAnimation(frames, "animation");
		_picked.Clear();
		RefreshAll(made);
	}

	// Each row of pieces (the picked ones, or else all the stills) becomes an animation
	private void AnimationPerRow_Click(object sender, RoutedEventArgs e)
	{
		ErrorText.Text = "";
		var pool = (_picked.Count > 0 ? Plan.Pieces.Where(_picked.Contains) : Plan.StillPieces).Where(p => !p.LeftOut);
		var rows = SpriteSheetSlicer.Rows(pool, p => p.Rect).Where(row => row.Count >= 2).ToList();
		if (rows.Count == 0)
		{
			ErrorText.Text = "There's no row of two pieces or more to make an animation of.";
			return;
		}
		var made = rows.Select((row, i) => MakeAnimation(row, $"row{i + 1}")).ToList();
		_picked.Clear();
		RefreshAll(made[0]);
	}

	// The pieces, in this order, become a new animation's frames (leaving any animation they were in)
	private SheetAnimation MakeAnimation(List<SheetPiece> frames, string baseName)
	{
		foreach (var piece in frames)
			if (piece.Animation is { } old)
			{
				old.Frames.RemoveAll(f => f == piece);
				piece.Animation = null;
			}
		Plan.Animations.RemoveAll(a => a.Frames.Count == 0);

		// Frames of one size line up by their middles; different sizes by their bottoms, so feet
		// stay on the ground. A Harlequin sheet says where its origin is.
		static (int W, int H) SizeOf(SheetPiece p) => p.Untrimmed is { } u ? (u.W, u.H) : (p.Rect.W, p.Rect.H);
		bool sameSize = frames.All(p => SizeOf(p) == SizeOf(frames[0]));
		var animation = new SheetAnimation
		{
			Name = UniqueAnimationName(baseName),
			Anchor = Plan.Origin != null ? FrameAnchor.Origin : sameSize ? FrameAnchor.Centre : FrameAnchor.Bottom
		};
		foreach (var piece in frames)
		{
			piece.Animation = animation;
			animation.Frames.Add(piece);
		}
		Plan.Animations.Add(animation);
		return animation;
	}

	private string UniqueAnimationName(string baseName)
	{
		if (Plan.Animations.All(a => a.Name != baseName)) return baseName;
		string stem = char.IsDigit(baseName[^1]) ? baseName + "_" : baseName;
		for (int i = 2; ; i++)
			if (Plan.Animations.All(a => a.Name != stem + i)) return stem + i;
	}

	private void LeaveOut_Click(object sender, RoutedEventArgs e)
	{
		ErrorText.Text = "";
		var picked = Plan.Pieces.Where(_picked.Contains).ToList();
		if (picked.Count == 0)
		{
			ErrorText.Text = "Pick the pieces to leave out first.";
			return;
		}
		bool leaveOut = picked.Any(p => !p.LeftOut);   // all left out already: they come back
		foreach (var piece in picked) piece.LeftOut = leaveOut;
		RefreshAll(CurrentAnimation);
	}

	private void AnimationList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing) return;
		ShowAnimation();
		SheetView.InvalidateVisual();
		PreviewView.InvalidateVisual();
	}

	private void ShowAnimation()
	{
		var animation = CurrentAnimation;
		AnimationFields.IsEnabled = animation != null;
		_syncing = true;
		AnimationName.Text = animation?.Name ?? "";
		bool perFrame = animation != null && animation.Frames.Any(f => f.Hold > 0);
		AnimationHold.Text = animation == null || perFrame ? "" : animation.Hold.ToString();
		HoldUnit.Text = perFrame ? "frame(s): as its JSON says (type a number for all)" : "frame(s)";
		AnimationLoop.IsChecked = animation?.Loop ?? false;
		AnimationAnchor.SelectedItem = animation == null ? null : Anchors.First(a => a.Anchor == animation.Anchor);
		_syncing = false;
	}

	private void AnimationField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		AnimationField_Changed(sender, e);
		e.Handled = true;   // not the Import button
	}

	private void AnimationField_Changed(object sender, RoutedEventArgs e)
	{
		if (_syncing || CurrentAnimation is not { } animation) return;
		ErrorText.Text = "";
		if (sender == AnimationName)
		{
			string name = AnimationName.Text.Trim();
			if (name.Length > 0) animation.Name = name;
		}
		else if (sender == AnimationHold && AnimationHold.Text.Trim().Length > 0)
		{
			if (!Whole(AnimationHold, 1, 100, out int hold, "number of frames each one shows")) return;
			animation.Hold = hold;
			foreach (var piece in animation.Frames) piece.Hold = 0;   // one timing for every frame now
		}
		RefreshAll(animation);
	}

	private void AnimationAnchor_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || CurrentAnimation is not { } animation || AnimationAnchor.SelectedItem is not AnchorChoice choice) return;
		if (choice.Anchor == FrameAnchor.Origin && Plan.Origin == null)
		{
			ErrorText.Text = "Only a sheet cut \"as its JSON file says\", from a JSON Harlequin wrote, has an origin.";
			ShowAnimation();
			return;
		}
		ErrorText.Text = "";
		animation.Anchor = choice.Anchor;
		SheetView.InvalidateVisual();
		PreviewView.InvalidateVisual();
	}

	private void AnimationLoop_Click(object sender, RoutedEventArgs e)
	{
		if (_syncing || CurrentAnimation is not { } animation) return;
		animation.Loop = AnimationLoop.IsChecked == true;
		RefreshAll(animation);
	}

	private void RemoveAnimation_Click(object sender, RoutedEventArgs e)
	{
		if (CurrentAnimation is not { } animation) return;
		foreach (var piece in animation.Frames) piece.Animation = null;
		Plan.Animations.Remove(animation);
		RefreshAll();
	}

	// ======================= bringing them in =======================

	private void Into_Changed(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		RefreshSummary();
	}

	private void RefreshSummary()
	{
		TogetherBox.IsEnabled = ObjectsChoice.IsChecked == true;
		Plan.Stills = StillsBox.IsChecked == true;
		Plan.AnimationsTogether = TogetherBox.IsChecked == true;

		int animations = Plan.LiveAnimations.Count(), stills = Plan.Stills ? Plan.StillPieces.Count() : 0;
		string what;
		if (IntoOpenObject)
			what = $"{animations + stills} part(s): {animations} animated (each with a new animation of its own) and {stills} still";
		else if (Plan.AnimationsTogether && animations > 0)
			what = $"one object \"{Plan.Name}\" playing {animations} animation(s), and {stills} still object(s)";
		else
			what = $"{animations} animated object(s) and {stills} still object(s)";
		Summary.Text = animations + stills == 0 ? "Nothing to bring in yet." : $"Brings in {what}.";
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		ErrorText.Text = "";
		RefreshSummary();
		var coming = Plan.LiveAnimations.SelectMany(a => a.Frames.Where(f => !f.LeftOut))
			.Concat(Plan.Stills ? Plan.StillPieces : Enumerable.Empty<SheetPiece>())
			.ToList();
		if (coming.Count == 0)
		{
			ErrorText.Text = "There's nothing to bring in: make an animation, or tick \"Bring in the stills too\".";
			return;
		}
		if (coming.FirstOrDefault(p => p.Rect.W > MaxPieceSize || p.Rect.H > MaxPieceSize) is { } big)
		{
			ErrorText.Text = $"Pieces can be up to {MaxPieceSize} × {MaxPieceSize} pixels; \"{big.Name}\" is {big.Rect.W} × {big.Rect.H}.";
			return;
		}
		DialogResult = true;
	}

	// ======================= refreshing =======================

	// Everything that shows the plan; `keep` = the animation to have picked in the list
	private void RefreshAll(SheetAnimation? keep = null)
	{
		int leftOut = Plan.Pieces.Count(p => p.LeftOut);
		PieceCount.Text = $"{Plan.Pieces.Count} piece(s)" +
			(leftOut > 0 ? $", {leftOut} left out" : "") +
			(_picked.Count > 0 ? $", {_picked.Count} picked" : "");

		// The list's items stay when the animations do, so a click on one isn't lost while it refreshes
		_syncing = true;
		var items = AnimationList.Items.Cast<ListBoxItem>().ToList();
		if (items.Count != Plan.Animations.Count || items.Where((item, i) => item.Tag != Plan.Animations[i]).Any())
		{
			AnimationList.Items.Clear();
			foreach (var animation in Plan.Animations) AnimationList.Items.Add(new ListBoxItem { Tag = animation });
		}
		foreach (ListBoxItem item in AnimationList.Items)
		{
			string text = Describe((SheetAnimation)item.Tag);
			if (!Equals(item.Content, text)) item.Content = text;
		}
		AnimationList.SelectedItem = AnimationList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == keep);
		_syncing = false;

		ShowAnimation();
		RefreshSummary();
		SheetView.InvalidateVisual();
		PreviewView.InvalidateVisual();
	}

	private static string Describe(SheetAnimation animation)
	{
		int frames = animation.Frames.Count(f => !f.LeftOut);
		string timing = animation.Frames.Any(f => f.Hold > 0) ? "timed by its JSON" : $"{animation.Hold} each";
		return $"{animation.Name}:  {frames} frame(s), {timing}{(animation.Loop ? ", loops" : "")}";
	}

	// ======================= the sheet =======================

	private static SKColor ColorOf(int animationIndex) => SKColor.FromHsl(animationIndex * 67 % 360, 75, 62);

	private SKRect ToView(PixelRect r) =>
		new(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Right * _zoom + _pan.X, r.Bottom * _zoom + _pan.Y);

	private Vector2 ToSheet(Vector2 view) => (view - _pan) / _zoom;

	private void Fit()
	{
		float w = (float)SheetView.ActualWidth, h = (float)SheetView.ActualHeight;
		if (w <= 0 || h <= 0) return;
		_zoom = Math.Clamp(MathF.Min(w / _sheet.Width, h / _sheet.Height) * 0.95f, 0.02f, 8f);
		_pan = new Vector2((w - _sheet.Width * _zoom) / 2, (h - _sheet.Height * _zoom) / 2);
		_fitted = true;
	}

	private void SheetView_Paint(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(40, 44, 52));
		if (SheetView.ActualWidth <= 0) return;
		canvas.Scale((float)(e.Info.Width / SheetView.ActualWidth));   // DPI
		if (!_fitted) Fit();
		float viewW = (float)SheetView.ActualWidth, viewH = (float)SheetView.ActualHeight;

		var whole = ToView(new PixelRect(0, 0, _sheet.Width, _sheet.Height));
		DrawChecker(canvas, whole, viewW, viewH);
		canvas.Save();
		canvas.Translate(_pan.X, _pan.Y);
		canvas.Scale(_zoom);
		canvas.DrawBitmap(_sheet, 0, 0, _zoom >= 1 ? AssetStore.Nearest : Smooth, null);   // pixels when zoomed in
		canvas.Restore();

		// The pieces, in the view's units so lines and labels keep their size at any zoom
		using var font = new SKFont { Size = 11 };
		using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
		using var fill = new SKPaint { Color = new SKColor(255, 220, 60, 60) };
		using var text = new SKPaint { IsAntialias = true };
		using var cross = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 80, 200) };
		using var dash = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0);
		var current = CurrentAnimation;

		for (int i = 0; i < Plan.Pieces.Count; i++)
		{
			var piece = Plan.Pieces[i];
			var r = ToView(piece.Rect);
			if (r.Right < 0 || r.Bottom < 0 || r.Left > viewW || r.Top > viewH) continue;
			int a = piece.Animation != null ? Plan.Animations.IndexOf(piece.Animation) : -1;
			bool picked = _picked.Contains(piece);
			bool mine = current != null && piece.Animation == current;

			if (picked) canvas.DrawRect(r, fill);
			line.Color = piece.LeftOut ? new SKColor(230, 70, 70)
				: picked ? new SKColor(255, 220, 60)
				: a >= 0 ? ColorOf(a)
				: new SKColor(255, 255, 255, 170);
			line.StrokeWidth = picked || mine ? 2.5f : 1.2f;
			line.PathEffect = piece.LeftOut ? dash : null;
			canvas.DrawRect(r, line);

			// Where the picked animation's frames line up
			if (mine && !piece.LeftOut)
			{
				var anchor = SheetImporter.AnchorPixel(current, piece, Plan.Origin);
				float ax = (piece.Rect.X + anchor.X) * _zoom + _pan.X, ay = (piece.Rect.Y + anchor.Y) * _zoom + _pan.Y;
				canvas.DrawLine(ax - 5, ay, ax + 5, ay, cross);
				canvas.DrawLine(ax, ay - 5, ax, ay + 5, cross);
			}

			// A label when there's room: a still's number, or which frame of which animation
			if (r.Width < 22 || r.Height < 15) continue;
			string label = piece.LeftOut ? "left out"
				: piece.Animation is { } animation ? $"{animation.Name} {animation.Frames.IndexOf(piece) + 1}"
				: $"#{i + 1}";
			text.Color = line.Color.WithAlpha(255);
			canvas.Save();
			canvas.ClipRect(r);
			canvas.DrawText(label, r.Left + 3, r.Top + 12, SKTextAlign.Left, font, text);
			canvas.Restore();
		}

		// The band being dragged, or the piece being drawn
		if (_drag is Drag.Band or Drag.Draw)
		{
			using var band = new SKPaint
			{
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 1,
				Color = new SKColor(120, 200, 255),
				PathEffect = dash
			};
			canvas.DrawRect(ToView(BandRect()), band);
		}
	}

	// A checkerboard behind the sheet, so see-through pixels show (only the part in view)
	private static void DrawChecker(SKCanvas canvas, SKRect sheet, float viewW, float viewH)
	{
		var area = SKRect.Intersect(sheet, new SKRect(0, 0, viewW, viewH));
		if (area.IsEmpty) return;
		using var light = new SKPaint { Color = new SKColor(204, 204, 204) };
		using var dark = new SKPaint { Color = new SKColor(158, 158, 158) };
		canvas.DrawRect(area, light);

		const float cell = 8;
		int cx0 = (int)((area.Left - sheet.Left) / cell), cy0 = (int)((area.Top - sheet.Top) / cell);
		canvas.Save();
		canvas.ClipRect(area);
		for (int cy = cy0; sheet.Top + cy * cell < area.Bottom; cy++)
			for (int cx = cx0; sheet.Left + cx * cell < area.Right; cx++)
				if ((cx + cy) % 2 == 1)
					canvas.DrawRect(SKRect.Create(sheet.Left + cx * cell, sheet.Top + cy * cell, cell, cell), dark);
		canvas.Restore();
	}

	// The dragged band in whole sheet pixels, inside the sheet
	private PixelRect BandRect()
	{
		int x0 = Math.Clamp((int)MathF.Floor(MathF.Min(_pressSheet.X, _nowSheet.X)), 0, _sheet.Width);
		int y0 = Math.Clamp((int)MathF.Floor(MathF.Min(_pressSheet.Y, _nowSheet.Y)), 0, _sheet.Height);
		int x1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(_pressSheet.X, _nowSheet.X)), 0, _sheet.Width);
		int y1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(_pressSheet.Y, _nowSheet.Y)), 0, _sheet.Height);
		return new PixelRect(x0, y0, x1 - x0, y1 - y0);
	}

	private SheetPiece? PieceAt(Vector2 sheetPoint)
	{
		int x = (int)MathF.Floor(sheetPoint.X), y = (int)MathF.Floor(sheetPoint.Y);
		for (int i = Plan.Pieces.Count - 1; i >= 0; i--)
			if (Plan.Pieces[i].Rect.Contains(x, y)) return Plan.Pieces[i];
		return null;
	}

	private static Vector2 PosIn(IInputElement element, MouseEventArgs e)
	{
		var p = e.GetPosition(element);
		return new Vector2((float)p.X, (float)p.Y);
	}

	private void SheetView_MouseDown(object sender, MouseButtonEventArgs e)
	{
		SheetView.Focus();   // out of any text box, so Del and Ctrl+A work on the pieces
		var p = PosIn(SheetView, e);
		_lastView = p;
		if (e.ChangedButton == MouseButton.Middle)
		{
			_drag = Drag.Pan;
			SheetView.CaptureMouse();
			return;
		}
		if (e.ChangedButton != MouseButton.Left) return;

		_pressSheet = _nowSheet = ToSheet(p);
		bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		if (PieceAt(_pressSheet) is { } hit)
		{
			if (!add)
			{
				_picked.Clear();
				_picked.Add(hit);
			}
			else if (!_picked.Remove(hit))
			{
				_picked.Add(hit);
			}
			RefreshAll(hit.Animation ?? CurrentAnimation);   // a frame shows its animation
			return;
		}

		// On empty sheet: a band that picks what it touches, or (by hand) a new piece
		if (!add) _picked.Clear();
		_drag = Hand && !add ? Drag.Draw : Drag.Band;
		SheetView.CaptureMouse();
		RefreshAll(CurrentAnimation);
	}

	private void SheetView_MouseMove(object sender, MouseEventArgs e)
	{
		var p = PosIn(SheetView, e);
		if (_drag == Drag.Pan)
		{
			_pan += p - _lastView;
			SheetView.InvalidateVisual();
		}
		else if (_drag is Drag.Band or Drag.Draw)
		{
			_nowSheet = ToSheet(p);
			SheetView.InvalidateVisual();
		}
		_lastView = p;
	}

	private void SheetView_MouseUp(object sender, MouseButtonEventArgs e)
	{
		var drag = _drag;
		_drag = Drag.None;
		SheetView.ReleaseMouseCapture();

		var band = BandRect();
		if (drag == Drag.Band)
		{
			foreach (var piece in Plan.Pieces.Where(piece => piece.Rect.Intersects(band)))
				_picked.Add(piece);
		}
		else if (drag == Drag.Draw && band.W >= 2 && band.H >= 2)
		{
			var piece = new SheetPiece { Rect = band, Name = $"{Plan.Name}_{Plan.Pieces.Count + 1}" };
			Plan.Pieces.Add(piece);
			_picked.Clear();
			_picked.Add(piece);
		}
		if (drag is Drag.Band or Drag.Draw) RefreshAll(CurrentAnimation);
	}

	private void SheetView_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		var p = PosIn(SheetView, e);
		var under = ToSheet(p);
		_zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25f : 0.8f), 0.02f, 40f);
		_pan = p - under * _zoom;   // the pixel under the mouse stays there
		SheetView.InvalidateVisual();
		e.Handled = true;
	}

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is TextBox) return;
		bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		if (e.Key == Key.Delete && _picked.Count > 0)
		{
			var current = CurrentAnimation;
			if (Hand)
			{
				// By hand, pieces go for good; otherwise they're left out (Leave out / put back
				// brings them back)
				foreach (var piece in _picked)
				{
					piece.Animation?.Frames.RemoveAll(f => f == piece);
					Plan.Pieces.Remove(piece);
				}
				Plan.Animations.RemoveAll(a => a.Frames.Count == 0);
				_picked.Clear();
			}
			else
			{
				foreach (var piece in _picked) piece.LeftOut = true;
			}
			RefreshAll(current != null && Plan.Animations.Contains(current) ? current : null);
		}
		else if (e.Key == Key.A && ctrl)
		{
			foreach (var piece in Plan.Pieces) _picked.Add(piece);
			RefreshAll(CurrentAnimation);
		}
		else if (e.Key == Key.Home)
		{
			_fitted = false;
			SheetView.InvalidateVisual();
		}
		else return;
		e.Handled = true;
	}

	// ======================= the preview =======================

	private void PreviewView_Paint(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(40, 44, 52));
		if (PreviewView.ActualWidth <= 0) return;
		canvas.Scale((float)(e.Info.Width / PreviewView.ActualWidth));   // DPI
		float w = (float)PreviewView.ActualWidth, h = (float)PreviewView.ActualHeight;

		var animation = CurrentAnimation;
		var frames = animation?.Frames.Where(f => !f.LeftOut).ToList() ?? new List<SheetPiece>();
		if (animation == null || frames.Count == 0)
		{
			using var font = new SKFont { Size = 12 };
			using var grey = new SKPaint { Color = new SKColor(154, 164, 178), IsAntialias = true };
			canvas.DrawText("Pick an animation to see it play", w / 2, h / 2 + 4, SKTextAlign.Center, font, grey);
			return;
		}
		if (_previewOf != animation)
		{
			_previewOf = animation;
			_previewFrame = 0;
		}

		// The frame showing now: each holds for its number of the project's frames
		int t = _previewFrame % Math.Max(1, frames.Sum(f => animation.HoldOf(f)));
		var piece = frames[^1];
		foreach (var f in frames)
		{
			int hold = animation.HoldOf(f);
			if (t < hold)
			{
				piece = f;
				break;
			}
			t -= hold;
		}

		// Every frame's anchor on one spot, and room for the biggest of them
		var bounds = SKRect.Empty;
		foreach (var f in frames)
		{
			var a = SheetImporter.AnchorPixel(animation, f, Plan.Origin);
			var r = new SKRect(-a.X, -a.Y, f.Rect.W - a.X, f.Rect.H - a.Y);
			bounds = bounds.IsEmpty ? r : SKRect.Union(bounds, r);
		}
		float scale = MathF.Min((w - 12) / bounds.Width, (h - 12) / bounds.Height);
		if (scale >= 1) scale = MathF.Floor(scale);   // whole steps keep pixel art even

		canvas.Translate(w / 2 - bounds.MidX * scale, h / 2 - bounds.MidY * scale);
		canvas.Scale(scale);
		using (var area = new SKPaint { Color = new SKColor(255, 255, 255, 18) })
			canvas.DrawRect(bounds, area);

		var anchor = SheetImporter.AnchorPixel(animation, piece, Plan.Origin);
		var at = new SKRect(-anchor.X, -anchor.Y, piece.Rect.W - anchor.X, piece.Rect.H - anchor.Y);
		canvas.Save();
		canvas.ClipRect(at);
		canvas.DrawBitmap(_sheet, at.Left - piece.Rect.X, at.Top - piece.Rect.Y, SharpPixels ? AssetStore.Nearest : Smooth, null);
		canvas.Restore();

		// The anchor
		using var cross = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale, Color = new SKColor(255, 80, 200, 200) };
		canvas.DrawLine(-6 / scale, 0, 6 / scale, 0, cross);
		canvas.DrawLine(0, -6 / scale, 0, 6 / scale, cross);
	}
}
