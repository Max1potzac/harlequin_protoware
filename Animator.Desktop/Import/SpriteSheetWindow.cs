using Animator.Core.Export;
using Animator.Core.Import;
using Animator.Viewer.Import;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PixelRect = Animator.Core.Utilities.PixelRect;

namespace Animator.Desktop.Import;

// File > Import sprite sheet: cut the sheet into pieces (a grid, its separate sprites, by hand,
// or as its JSON says), pick which pieces play as animations, and bring them in as drawn
// objects or as parts of the open object. The same as the Windows editor's dialog;
// ShowDialog<bool>: true, import as Plan now says.
public sealed class SpriteSheetWindow : Window
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
	internal readonly HashSet<SheetPiece> Picked = new();
	private bool _ready, _syncing, _imported;
	private bool _jsonOrder;   // cut by the JSON: new animations play its frames in its order

	// The sheet: sheet pixel p shows at p × _zoom + _pan (in the view's units)
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
	public bool IntoOpenObject => _partsChoice.IsChecked == true;
	public bool SharpPixels => _sharpBox.IsChecked == true;

	private readonly CheckBox _keyBox = new();
	internal readonly RadioButton GridMode = new() { Content = "A grid (frames of one size)", GroupName = "cut" };
	internal readonly RadioButton FindMode = new() { Content = "Find the separate sprites (on see-through sheets)", GroupName = "cut" };
	internal readonly RadioButton HandMode = new() { Content = "Draw them by hand", GroupName = "cut" };
	internal readonly RadioButton JsonMode = new() { Content = "As its JSON file says", GroupName = "cut" };
	private readonly StackPanel _gridFields = new() { Spacing = 4, Margin = new Thickness(24, 0, 0, 0) };
	private readonly WrapPanel _findFields = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, Margin = new Thickness(24, 0, 0, 0) };   // (on two lines when narrow)
	internal readonly TextBox CellWidth = Num(), CellHeight = Num(), OffsetX = Num(), OffsetY = Num(), GapX = Num(), GapY = Num(), SplitX = Num(), SplitY = Num(), Join = Num(), Smallest = Num();
	private readonly TextBlock _jsonHint = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(24, 0, 0, 0) };
	private readonly Button _cutButton = new() { Content = "Cut again" };
	internal readonly TextBlock PieceCount = new();
	internal readonly ListBox AnimationList = new() { MaxHeight = 160 };
	private readonly StackPanel _animationFields = new() { Spacing = 4 };
	private readonly TextBox _animationName = new();
	private readonly TextBox _animationHold = new() { Width = 56 };
	private readonly TextBlock _holdUnit = new() { Text = "frame(s)", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
	private readonly ComboBox _anchorBox = new() { ItemsSource = Anchors, HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly CheckBox _loopBox = new() { Content = "Loop" };
	internal readonly SkiaView PreviewView = new() { Height = 170 };
	private readonly RadioButton _objectsChoice = new() { Content = "As new drawn objects", GroupName = "into" };
	private readonly CheckBox _togetherBox = new() { Content = "All the animations in one object (one character's moves)", Margin = new Thickness(24, 0, 0, 0) };
	private readonly RadioButton _partsChoice = new() { GroupName = "into" };
	private readonly CheckBox _stillsBox = new() { Content = "Bring in the stills too (the pieces in no animation)" };
	private readonly CheckBox _sharpBox = new() { Content = "Show pixels sharp (pixel art; P switches it later)" };
	internal readonly TextBlock Summary = new() { TextWrapping = TextWrapping.Wrap };
	internal readonly TextBlock Error = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly SkiaView SheetView = new();

	private static TextBox Num() => new() { Width = 56 };

	// partsLabel: the "as parts of" choice, e.g. As parts of "hero", on its bone "arm".
	// sharpPixels: whether pictures are shown with hard pixel edges now.
	public SpriteSheetWindow(SKBitmap sheet, string name, SpriteSheetJson? json, string partsLabel, bool partsPossible, bool sharpPixels, int fps)
	{
		Title = "Import a sprite sheet";
		Width = 1200;
		Height = 800;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		_original = _sheet = sheet;
		_alpha = SheetImporter.Alpha(sheet);
		_json = json;
		_fps = Math.Max(1, fps);
		Plan = new SheetImportPlan(sheet, name);
		Content = BuildLayout(name, partsLabel);

		// A sheet drawn on a flat colour: that colour can become see-through. It's ticked to begin
		// with when the sheet has no see-through pixels and the corners' colour covers much of it.
		var corner = sheet.GetPixel(0, 0);
		_keyBox.IsEnabled = corner.Alpha == 255;
		_keyBox.Content = corner.Alpha == 255
			? $"Make the background colour see-through (#{corner.Red:X2}{corner.Green:X2}{corner.Blue:X2}, as in the top-left corner)"
			: "The background is see-through already";
		if (corner.Alpha == 255 && _alpha.All(a => a == 255))
		{
			var keyed = SheetImporter.WithoutColourAt(sheet, 0, 0, out int taken);
			bool corners = new[] { sheet.GetPixel(sheet.Width - 1, 0), sheet.GetPixel(0, sheet.Height - 1), sheet.GetPixel(sheet.Width - 1, sheet.Height - 1) }.All(c => c == corner);
			if (corners && taken >= 0.3 * sheet.Width * sheet.Height)
			{
				_keyed = keyed;
				_keyBox.IsChecked = true;
				UseSheet(keyed);
			}
			else keyed.Dispose();
		}

		// A strip of square frames is a common sheet: guess that, else 64 × 64
		int shortSide = Math.Min(sheet.Width, sheet.Height), longSide = Math.Max(sheet.Width, sheet.Height);
		int cell = longSide >= 2 * shortSide && longSide % shortSide == 0 ? shortSide : Math.Min(64, shortSide);
		CellWidth.Text = CellHeight.Text = cell.ToString();
		OffsetX.Text = OffsetY.Text = GapX.Text = GapY.Text = "0";
		Join.Text = "2";
		Smallest.Text = "3";

		JsonMode.IsEnabled = json != null;
		_jsonHint.Text = json != null
			? $"Found {name}.json next to it, describing {json.Frames.Count} frame(s)" + (json.Meta.FrameTags.Count > 0 ? $" and {json.Meta.FrameTags.Count} animation(s)." : ".")
			: "No JSON file with the same name next to the picture.";
		_partsChoice.IsEnabled = partsPossible;
		_objectsChoice.IsChecked = true;
		_togetherBox.IsChecked = true;
		_stillsBox.IsChecked = true;

		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / _fps) };
		_timer.Tick += (_, _) =>
		{
			if (CurrentAnimation == null) return;
			_previewFrame++;
			PreviewView.InvalidateVisual();
		};
		Opened += (_, _) => _timer.Start();
		Closed += (_, _) =>
		{
			_timer.Stop();
			// The copy without the background goes with the plan when it's imported (whoever
			// imports it disposes of it); otherwise it's not needed any more
			if (_keyed != null && (!_imported || Plan.Sheet != _keyed)) _keyed.Dispose();
		};

		_ready = true;
		// The JSON if there is one, else the separate sprites of a see-through sheet, else a grid
		(json != null ? JsonMode : _alpha.Any(a => a < 255) ? FindMode : GridMode).IsChecked = true;   // (cuts it)

		// Small pieces with hard edges are most likely pixel art
		var sizes = Plan.Pieces.Select(p => Math.Max(p.Rect.W, p.Rect.H)).OrderBy(s => s).ToList();
		bool small = sizes.Count > 0 && sizes[sizes.Count / 2] <= 128;
		_sharpBox.IsChecked = sharpPixels || small && _alpha.All(a => a is 0 or 255);
		AddHandler(KeyDownEvent, Window_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
	}

	private Control BuildLayout(string name, string partsLabel)
	{
		static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 2) };
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
		static TextBlock T(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		static StackPanel H(params Control[] c)
		{
			var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
			foreach (var x in c) p.Children.Add(x);
			return p;
		}
		Button B(string text, Action click)
		{
			var b = new Button { Content = text };
			b.Click += (_, _) => click();
			return b;
		}

		var s = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
		s.Children.Add(new TextBlock { Text = $"{name}   ({_original.Width} × {_original.Height} pixels)", FontWeight = FontWeight.SemiBold });
		s.Children.Add(_keyBox);
		_keyBox.IsCheckedChanged += (_, _) => { if (_ready) KeyChanged(); };

		// 1. Cutting
		s.Children.Add(Heading("1. Cut it into pieces"));
		s.Children.Add(GridMode);
		_gridFields.Children.Add(H(T("Frame size"), CellWidth, T("×"), CellHeight));
		_gridFields.Children.Add(H(T("First one at"), OffsetX, T(","), OffsetY));
		_gridFields.Children.Add(H(T("Gap between"), GapX, T(","), GapY));
		_gridFields.Children.Add(H(T("Or a grid of"), SplitX, T("×"), SplitY));
		s.Children.Add(_gridFields);
		s.Children.Add(FindMode);
		_findFields.Children.Add(T("join bits closer than"));
		_findFields.Children.Add(Join);
		_findFields.Children.Add(T("px, skip smaller than"));
		_findFields.Children.Add(Smallest);
		s.Children.Add(_findFields);
		s.Children.Add(HandMode);
		s.Children.Add(new TextBlock { Text = "Drag on the sheet to draw a piece (Ctrl+drag picks pieces instead). Del removes the picked ones. The pieces there are stay, so you can fix up a cut.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(24, 0, 0, 0) });
		s.Children.Add(JsonMode);
		s.Children.Add(_jsonHint);
		_cutButton.Click += (_, _) => Cut();
		s.Children.Add(H(_cutButton, PieceCount));
		foreach (var mode in new[] { GridMode, FindMode, HandMode, JsonMode })
			mode.IsCheckedChanged += (_, _) => { if (mode.IsChecked == true) ModeChanged(); };
		foreach (var box in new[] { CellWidth, CellHeight, OffsetX, OffsetY, GapX, GapY, Join, Smallest })
			box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Cut(); e.Handled = true; } };
		SplitX.TextChanged += (_, _) => SplitChanged();
		SplitY.TextChanged += (_, _) => SplitChanged();

		// 2. Animations
		s.Children.Add(Heading("2. Make animations"));
		s.Children.Add(Note("Pick pieces on the sheet (click; Ctrl+click adds or removes; drag around several), then make them an animation: they play left to right, top to bottom (cut by its JSON: in the JSON's order). Pieces in no animation are stills."));
		s.Children.Add(new WrapPanel { Children = { Pad(B("Make an animation", MakeAnimationOfPicked)), Pad(B("An animation per row", AnimationPerRow)), Pad(B("Leave out / put back", LeaveOut)) } });
		s.Children.Add(AnimationList);
		AnimationList.SelectionChanged += (_, _) =>
		{
			if (_syncing) return;
			ShowAnimation();
			SheetView.InvalidateVisual();
			PreviewView.InvalidateVisual();
		};
		_animationFields.Children.Add(H(T("Name"), _animationName));
		_animationName.MinWidth = 160;
		_animationFields.Children.Add(H(T("Each frame shows"), _animationHold, _holdUnit));
		_animationFields.Children.Add(H(T("Line up by"), _anchorBox));
		_animationFields.Children.Add(_loopBox);
		_animationFields.Children.Add(B("Remove this animation (its pieces become stills)", RemoveAnimation));
		s.Children.Add(_animationFields);
		foreach (var box in new[] { _animationName, _animationHold })
		{
			var b = box;
			b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AnimationFieldChanged(b); e.Handled = true; } };
			b.LostFocus += (_, _) => AnimationFieldChanged(b);
		}
		_anchorBox.SelectionChanged += (_, _) => AnchorChanged();
		_loopBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncing || CurrentAnimation is not { } animation) return;
			animation.Loop = _loopBox.IsChecked == true;
			RefreshAll(animation);
		};
		PreviewView.Draw = DrawPreview;
		s.Children.Add(PreviewView);

		// 3. Bringing them in
		s.Children.Add(Heading("3. Bring them in"));
		s.Children.Add(_objectsChoice);
		s.Children.Add(_togetherBox);
		_partsChoice.Content = partsLabel;
		s.Children.Add(_partsChoice);
		s.Children.Add(_stillsBox);
		s.Children.Add(_sharpBox);
		foreach (var c in new ToggleButtonLike[] { new(_objectsChoice), new(_partsChoice), new(_togetherBox), new(_stillsBox) })
			c.Changed(() => { if (_ready) RefreshSummary(); });
		_sharpBox.IsCheckedChanged += (_, _) => PreviewView.InvalidateVisual();

		var import = new Button { Content = "Import", MinWidth = 90, IsDefault = true };
		import.Click += (_, _) => TryImport();
		var cancel = new Button { Content = "Cancel", MinWidth = 90 };
		cancel.Click += (_, _) => Close(false);
		var bottom = new DockPanel { Margin = new Thickness(12, 8) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, cancel } };
		DockPanel.SetDock(buttons, Dock.Right);
		bottom.Children.Add(buttons);
		bottom.Children.Add(new StackPanel { Children = { Summary, Error } });

		// The sheet
		SheetView.Draw = DrawSheet;
		SheetView.PointerPressed += SheetView_PointerPressed;
		SheetView.PointerMoved += SheetView_PointerMoved;
		SheetView.PointerReleased += SheetView_PointerReleased;
		SheetView.PointerWheelChanged += (_, e) =>
		{
			var p = Input.At(e, SheetView);
			var under = ToSheet(p);
			_zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25f : 0.8f), 0.02f, 40f);
			_pan = p - under * _zoom;   // the pixel under the mouse stays there
			SheetView.InvalidateVisual();
			e.Handled = true;
		};
		var picture = new DockPanel();
		var help = new TextBlock { Text = "Wheel: zoom   Middle drag: move around   Home: fit", Foreground = Brushes.Gray, Margin = new Thickness(8, 4) };
		DockPanel.SetDock(help, Dock.Bottom);
		picture.Children.Add(help);
		picture.Children.Add(SheetView);

		var main = new Grid { ColumnDefinitions = new ColumnDefinitions("420,4,*") };
		main.Children.Add(new ScrollViewer { Content = s });
		var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
		Grid.SetColumn(splitter, 1);
		main.Children.Add(splitter);
		Grid.SetColumn(picture, 2);
		main.Children.Add(picture);
		var root = new DockPanel();
		DockPanel.SetDock(bottom, Dock.Bottom);
		root.Children.Add(bottom);
		root.Children.Add(main);
		return root;
	}

	private static Control Pad(Control c)
	{
		c.Margin = new Thickness(0, 0, 6, 4);
		return c;
	}

	// A check box or radio button's changes, one way for both
	private sealed record ToggleButtonLike(Avalonia.Controls.Primitives.ToggleButton Button)
	{
		public void Changed(Action changed) => Button.IsCheckedChanged += (_, _) => changed();
	}

	private bool Hand => HandMode.IsChecked == true;

	// ======================= the background colour =======================

	private void UseSheet(SKBitmap sheet)
	{
		_sheet = sheet;
		_alpha = SheetImporter.Alpha(sheet);
		Plan.Sheet = sheet;
	}

	private void KeyChanged()
	{
		if (_keyBox.IsChecked == true)
		{
			_keyed ??= SheetImporter.WithoutColourAt(_original, 0, 0, out _);
			UseSheet(_keyed);
		}
		else UseSheet(_original);
		if (Hand) RefreshAll(CurrentAnimation);   // drawn by hand: the pieces stay, only what's in them changes
		else Cut();                               // finding sprites needs the see-through pixels
	}

	// ======================= cutting =======================

	// "Or a grid of 8 × 4": the frame size that splits the sheet evenly (after the offset and gaps)
	private void SplitChanged()
	{
		if (!_ready || !int.TryParse(SplitX.Text?.Trim(), out int across) || !int.TryParse(SplitY.Text?.Trim(), out int down) || across < 1 || down < 1) return;
		static int Number(TextBox box) => int.TryParse(box.Text?.Trim(), out int n) ? Math.Max(0, n) : 0;
		int w = (_sheet.Width - Number(OffsetX) - (across - 1) * Number(GapX)) / across;
		int h = (_sheet.Height - Number(OffsetY) - (down - 1) * Number(GapY)) / down;
		if (w < 1 || h < 1) return;
		CellWidth.Text = w.ToString();
		CellHeight.Text = h.ToString();
	}

	private void ModeChanged()
	{
		if (!_ready) return;
		_gridFields.IsEnabled = GridMode.IsChecked == true;
		_findFields.IsEnabled = FindMode.IsChecked == true;
		_cutButton.IsEnabled = !Hand;
		if (!Hand) Cut();   // by hand: you go on from the pieces there are (remove some, draw more)
		else RefreshAll(CurrentAnimation);
	}

	// Cuts the sheet the chosen way (the animations made so far go)
	internal void Cut()
	{
		Error.Text = "";
		Picked.Clear();

		_jsonOrder = JsonMode.IsChecked == true && _json != null;
		if (_jsonOrder)
		{
			int rotated = Plan.ReadJson(_json!, _fps);
			if (rotated > 0) Error.Text = $"{rotated} frame(s) are stored turned on their side, which isn't supported: they were left out.";
			RefreshAll(Plan.Animations.FirstOrDefault());
			return;
		}

		List<PixelRect> rects;
		if (GridMode.IsChecked == true)
		{
			if (!Whole(CellWidth, 1, _sheet.Width, out int w, "frame width") ||
				!Whole(CellHeight, 1, _sheet.Height, out int h, "frame height") ||
				!Whole(OffsetX, 0, _sheet.Width - 1, out int ox, "first frame's left edge") ||
				!Whole(OffsetY, 0, _sheet.Height - 1, out int oy, "first frame's top edge") ||
				!Whole(GapX, 0, _sheet.Width, out int gx, "gap across") ||
				!Whole(GapY, 0, _sheet.Height, out int gy, "gap down"))
				return;
			rects = SpriteSheetSlicer.Grid(_sheet.Width, _sheet.Height, w, h, ox, oy, gx, gy, _alpha);
		}
		else
		{
			if (!Whole(Join, 0, 64, out int join, "joining distance") || !Whole(Smallest, 1, MaxPieceSize, out int smallest, "smallest size")) return;
			rects = SpriteSheetSlicer.Find(_alpha, _sheet.Width, _sheet.Height, join, smallest);
		}

		Plan.Pieces.Clear();
		Plan.Animations.Clear();
		Plan.Origin = null;
		for (int i = 0; i < rects.Count; i++) Plan.Pieces.Add(new SheetPiece { Rect = rects[i], Name = $"{Plan.Name}_{i + 1}" });
		if (rects.Count == 0) Error.Text = "That cut found no pieces with anything visible in them.";
		RefreshAll();
	}

	private bool Whole(TextBox box, int min, int max, out int value, string what)
	{
		if (int.TryParse(box.Text?.Trim(), out value) && value >= min && value <= max) return true;
		Error.Text = $"The {what} is a whole number from {min} to {max}.";
		box.Focus();
		return false;
	}

	// ======================= animations =======================

	internal SheetAnimation? CurrentAnimation => (AnimationList.SelectedItem as ListBoxItem)?.Tag as SheetAnimation;

	internal void MakeAnimationOfPicked()
	{
		Error.Text = "";
		var picked = Plan.Pieces.Where(p => Picked.Contains(p) && !p.LeftOut).ToList();
		if (picked.Count == 0)
		{
			Error.Text = "Pick the pieces on the sheet first (click them, or drag around them).";
			return;
		}
		// Left to right, top to bottom; a packed sheet's frames can be anywhere, so its JSON's order
		var frames = _jsonOrder ? picked : SpriteSheetSlicer.Rows(picked, p => p.Rect).SelectMany(row => row).ToList();
		var made = MakeAnimation(frames, "animation");
		Picked.Clear();
		RefreshAll(made);
	}

	// Each row of pieces (the picked ones, or else all the stills) becomes an animation
	internal void AnimationPerRow()
	{
		Error.Text = "";
		var pool = (Picked.Count > 0 ? Plan.Pieces.Where(Picked.Contains) : Plan.StillPieces).Where(p => !p.LeftOut);
		var rows = SpriteSheetSlicer.Rows(pool, p => p.Rect).Where(row => row.Count >= 2).ToList();
		if (rows.Count == 0)
		{
			Error.Text = "There's no row of two pieces or more to make an animation of.";
			return;
		}
		var made = rows.Select((row, i) => MakeAnimation(row, $"row{i + 1}")).ToList();
		Picked.Clear();
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

	private void LeaveOut()
	{
		Error.Text = "";
		var picked = Plan.Pieces.Where(Picked.Contains).ToList();
		if (picked.Count == 0)
		{
			Error.Text = "Pick the pieces to leave out first.";
			return;
		}
		bool leaveOut = picked.Any(p => !p.LeftOut);   // all left out already: they come back
		foreach (var piece in picked) piece.LeftOut = leaveOut;
		RefreshAll(CurrentAnimation);
	}

	private void ShowAnimation()
	{
		var animation = CurrentAnimation;
		_animationFields.IsEnabled = animation != null;
		_syncing = true;
		_animationName.Text = animation?.Name ?? "";
		bool perFrame = animation != null && animation.Frames.Any(f => f.Hold > 0);
		_animationHold.Text = animation == null || perFrame ? "" : animation.Hold.ToString();
		_holdUnit.Text = perFrame ? "frame(s): as its JSON says (type a number for all)" : "frame(s)";
		_loopBox.IsChecked = animation?.Loop ?? false;
		_anchorBox.SelectedItem = animation == null ? null : Anchors.First(a => a.Anchor == animation.Anchor);
		_syncing = false;
	}

	private void AnimationFieldChanged(TextBox box)
	{
		if (_syncing || CurrentAnimation is not { } animation) return;
		Error.Text = "";
		if (box == _animationName)
		{
			string name = _animationName.Text?.Trim() ?? "";
			if (name.Length == 0 || name == animation.Name) return;
			animation.Name = name;
		}
		else if (box == _animationHold && (_animationHold.Text?.Trim().Length ?? 0) > 0)
		{
			if (!Whole(_animationHold, 1, 100, out int hold, "number of frames each one shows")) return;
			if (hold == animation.Hold && animation.Frames.All(f => f.Hold == 0)) return;
			animation.Hold = hold;
			foreach (var piece in animation.Frames) piece.Hold = 0;   // one timing for every frame now
		}
		else return;
		RefreshAll(animation);
	}

	private void AnchorChanged()
	{
		if (_syncing || CurrentAnimation is not { } animation || _anchorBox.SelectedItem is not AnchorChoice choice) return;
		if (choice.Anchor == FrameAnchor.Origin && Plan.Origin == null)
		{
			Error.Text = "Only a sheet cut \"as its JSON file says\", from a JSON Harlequin wrote, has an origin.";
			ShowAnimation();
			return;
		}
		Error.Text = "";
		animation.Anchor = choice.Anchor;
		SheetView.InvalidateVisual();
		PreviewView.InvalidateVisual();
	}

	private void RemoveAnimation()
	{
		if (CurrentAnimation is not { } animation) return;
		foreach (var piece in animation.Frames) piece.Animation = null;
		Plan.Animations.Remove(animation);
		RefreshAll();
	}

	// ======================= bringing them in =======================

	private void RefreshSummary()
	{
		_togetherBox.IsEnabled = _objectsChoice.IsChecked == true;
		Plan.Stills = _stillsBox.IsChecked == true;
		Plan.AnimationsTogether = _togetherBox.IsChecked == true;

		int animations = Plan.LiveAnimations.Count(), stills = Plan.Stills ? Plan.StillPieces.Count() : 0;
		string what;
		if (IntoOpenObject) what = $"{animations + stills} part(s): {animations} animated (each with a new animation of its own) and {stills} still";
		else if (Plan.AnimationsTogether && animations > 0) what = $"one object \"{Plan.Name}\" playing {animations} animation(s), and {stills} still object(s)";
		else what = $"{animations} animated object(s) and {stills} still object(s)";
		Summary.Text = animations + stills == 0 ? "Nothing to bring in yet." : $"Brings in {what}.";
	}

	internal void TryImport()
	{
		Error.Text = "";
		RefreshSummary();
		var coming = Plan.LiveAnimations.SelectMany(a => a.Frames.Where(f => !f.LeftOut))
			.Concat(Plan.Stills ? Plan.StillPieces : Enumerable.Empty<SheetPiece>())
			.ToList();
		if (coming.Count == 0)
		{
			Error.Text = "There's nothing to bring in: make an animation, or tick \"Bring in the stills too\".";
			return;
		}
		if (coming.FirstOrDefault(p => p.Rect.W > MaxPieceSize || p.Rect.H > MaxPieceSize) is { } big)
		{
			Error.Text = $"Pieces can be up to {MaxPieceSize} × {MaxPieceSize} pixels; \"{big.Name}\" is {big.Rect.W} × {big.Rect.H}.";
			return;
		}
		_imported = true;
		Close(true);
	}

	// ======================= refreshing =======================

	// Everything that shows the plan; `keep` = the animation to have picked in the list
	internal void RefreshAll(SheetAnimation? keep = null)
	{
		int leftOut = Plan.Pieces.Count(p => p.LeftOut);
		PieceCount.Text = $"{Plan.Pieces.Count} piece(s)" + (leftOut > 0 ? $", {leftOut} left out" : "") + (Picked.Count > 0 ? $", {Picked.Count} picked" : "");

		_syncing = true;
		var items = Plan.Animations.Select(a => new ListBoxItem { Tag = a, Content = Describe(a) }).ToList();
		AnimationList.ItemsSource = items;
		AnimationList.SelectedItem = items.FirstOrDefault(i => i.Tag == keep);
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

	private SKRect ToView(PixelRect r) => new(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Right * _zoom + _pan.X, r.Bottom * _zoom + _pan.Y);
	private Vector2 ToSheet(Vector2 view) => (view - _pan) / _zoom;
	internal Vector2 SheetToView(Vector2 sheet) => sheet * _zoom + _pan;

	private void Fit(Vector2 view)
	{
		if (view.X <= 0 || view.Y <= 0) return;
		_zoom = Math.Clamp(MathF.Min(view.X / _sheet.Width, view.Y / _sheet.Height) * 0.95f, 0.02f, 8f);
		_pan = new Vector2((view.X - _sheet.Width * _zoom) / 2, (view.Y - _sheet.Height * _zoom) / 2);
		_fitted = true;
	}

	private void DrawSheet(SKCanvas canvas, Vector2 view, float dpi)
	{
		canvas.Clear(new SKColor(40, 44, 52));
		canvas.Scale(dpi);
		if (!_fitted) Fit(view);

		Checkerboard.Draw(canvas, ToView(new PixelRect(0, 0, _sheet.Width, _sheet.Height)), view.X, view.Y);
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
			if (r.Right < 0 || r.Bottom < 0 || r.Left > view.X || r.Top > view.Y) continue;
			int a = piece.Animation != null ? Plan.Animations.IndexOf(piece.Animation) : -1;
			bool picked = Picked.Contains(piece);
			bool mine = current != null && piece.Animation == current;

			if (picked) canvas.DrawRect(r, fill);
			line.Color = piece.LeftOut ? new SKColor(230, 70, 70) : picked ? new SKColor(255, 220, 60) : a >= 0 ? ColorOf(a) : new SKColor(255, 255, 255, 170);
			line.StrokeWidth = picked || mine ? 2.5f : 1.2f;
			line.PathEffect = piece.LeftOut ? dash : null;
			canvas.DrawRect(r, line);

			// Where the picked animation's frames line up
			if (mine && !piece.LeftOut)
			{
				var anchor = SheetImporter.AnchorPixel(current!, piece, Plan.Origin);
				float ax = (piece.Rect.X + anchor.X) * _zoom + _pan.X, ay = (piece.Rect.Y + anchor.Y) * _zoom + _pan.Y;
				canvas.DrawLine(ax - 5, ay, ax + 5, ay, cross);
				canvas.DrawLine(ax, ay - 5, ax, ay + 5, cross);
			}

			// A label when there's room: a still's number, or which frame of which animation
			if (r.Width < 22 || r.Height < 15) continue;
			string label = piece.LeftOut ? "left out" : piece.Animation is { } animation ? $"{animation.Name} {animation.Frames.IndexOf(piece) + 1}" : $"#{i + 1}";
			text.Color = line.Color.WithAlpha(255);
			canvas.Save();
			canvas.ClipRect(r);
			canvas.DrawText(label, r.Left + 3, r.Top + 12, SKTextAlign.Left, font, text);
			canvas.Restore();
		}

		// The band being dragged, or the piece being drawn
		if (_drag is Drag.Band or Drag.Draw)
		{
			using var band = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(120, 200, 255), PathEffect = dash };
			canvas.DrawRect(ToView(BandRect()), band);
		}
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

	private void SheetView_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		SheetView.Focus();   // out of any text box, so Del and Ctrl+A work on the pieces
		var point = e.GetCurrentPoint(SheetView);
		var p = Input.At(e, SheetView);
		_lastView = p;
		if (point.Properties.IsMiddleButtonPressed)
		{
			_drag = Drag.Pan;
			e.Pointer.Capture(SheetView);
			return;
		}
		if (!point.Properties.IsLeftButtonPressed) return;

		_pressSheet = _nowSheet = ToSheet(p);
		bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
		if (PieceAt(_pressSheet) is { } hit)
		{
			if (!add)
			{
				Picked.Clear();
				Picked.Add(hit);
			}
			else if (!Picked.Remove(hit)) Picked.Add(hit);
			RefreshAll(hit.Animation ?? CurrentAnimation);   // a frame shows its animation
			return;
		}

		// On empty sheet: a band that picks what it touches, or (by hand) a new piece
		if (!add) Picked.Clear();
		_drag = Hand && !add ? Drag.Draw : Drag.Band;
		e.Pointer.Capture(SheetView);
		RefreshAll(CurrentAnimation);
	}

	private void SheetView_PointerMoved(object? sender, PointerEventArgs e)
	{
		var p = Input.At(e, SheetView);
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

	private void SheetView_PointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		var drag = _drag;
		_drag = Drag.None;
		e.Pointer.Capture(null);
		var band = BandRect();
		if (drag == Drag.Band)
		{
			foreach (var piece in Plan.Pieces.Where(piece => piece.Rect.Intersects(band))) Picked.Add(piece);
		}
		else if (drag == Drag.Draw && band.W >= 2 && band.H >= 2)
		{
			var piece = new SheetPiece { Rect = band, Name = $"{Plan.Name}_{Plan.Pieces.Count + 1}" };
			Plan.Pieces.Add(piece);
			Picked.Clear();
			Picked.Add(piece);
		}
		if (drag is Drag.Band or Drag.Draw) RefreshAll(CurrentAnimation);
	}

	private void Window_KeyDown(object? sender, KeyEventArgs e)
	{
		if (FocusManager?.GetFocusedElement() is TextBox) return;
		bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
		if (e.Key == Key.Delete && Picked.Count > 0)
		{
			var current = CurrentAnimation;
			if (Hand)
			{
				// By hand, pieces go for good; otherwise they're left out (Leave out / put back brings them back)
				foreach (var piece in Picked)
				{
					piece.Animation?.Frames.RemoveAll(f => f == piece);
					Plan.Pieces.Remove(piece);
				}
				Plan.Animations.RemoveAll(a => a.Frames.Count == 0);
				Picked.Clear();
			}
			else foreach (var piece in Picked) piece.LeftOut = true;
			RefreshAll(current != null && Plan.Animations.Contains(current) ? current : null);
		}
		else if (e.Key == Key.A && ctrl)
		{
			foreach (var piece in Plan.Pieces) Picked.Add(piece);
			RefreshAll(CurrentAnimation);
		}
		else if (e.Key == Key.Home)
		{
			_fitted = false;
			SheetView.InvalidateVisual();
		}
		else if (e.Key == Key.Escape) Close(false);
		else return;
		e.Handled = true;
	}

	// ======================= the preview =======================

	private void DrawPreview(SKCanvas canvas, Vector2 view, float dpi)
	{
		canvas.Clear(new SKColor(40, 44, 52));
		canvas.Scale(dpi);
		float w = view.X, h = view.Y;

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
