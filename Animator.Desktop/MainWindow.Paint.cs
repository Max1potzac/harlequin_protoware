using Animator.Viewer.Painting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The paint editor, beside the viewport while it's open: a part's drawing (double-click a part,
// or Paint on the part panel) on the engine's PaintCanvas, the tools (with only the options the
// one in hand uses), the two colours and a palette, the drawing's layers. It follows the part's
// drawings along the timeline (EditorSession.FollowPaint), and its keys work while the mouse
// is over the canvas (the Windows editor's: B brush, E eraser, [ ] the size…).
public sealed partial class MainWindow
{
	private PaintCanvas _paintCanvas = null!;
	private PaintSession Paint => _session.Paint;
	internal bool PaintBusy => _paintCanvas.IsBusy;   // a stroke (or a figure) under way
	private PaintToolbox PaintTools => _session.PaintTools;

	internal readonly SkiaView PaintView = new();
	internal Control PaintPanel { get; private set; } = null!;
	private ColumnDefinition _paintColumn = null!;
	private GridSplitter _paintSplitter = null!;
	private readonly TextBlock _paintTitle = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
	private readonly TextBlock _toolTitle = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
	private readonly Dictionary<string, ToggleButton> _paintToolButtons = new();
	private readonly Border _primarySwatch = Swatch(28);
	private readonly Border _secondarySwatch = Swatch(22);
	internal readonly TextBox PaintHex = new() { Width = 96 };
	private bool _syncingPaint;

	// The options, each shown only for the tools that use it
	internal readonly Slider PaintSize = new() { Minimum = 1, Maximum = 64, Width = 110, SmallChange = 1 };
	private readonly Slider _tolerance = new() { Minimum = 0, Maximum = 255, Width = 110, SmallChange = 1 };
	private readonly CheckBox _contiguous = new() { Content = "Touching only" };
	private readonly CheckBox _sampleAll = new() { Content = "All layers" };
	private readonly Slider _wet = new() { Minimum = 0, Maximum = 100, Width = 90 };
	private readonly Slider _load = new() { Minimum = 0, Maximum = 100, Width = 90 };
	private readonly CheckBox _toTransparent = new() { Content = "To transparent" };
	private readonly ComboBox _selectShape = new() { ItemsSource = Enum.GetValues<SelectShape>() };
	private readonly ComboBox _selectMode = new() { ItemsSource = Enum.GetValues<SelectMode>() };
	private readonly ComboBox _figureShape = new() { ItemsSource = Enum.GetValues<FigureShape>() };
	private readonly ComboBox _figureStyle = new() { ItemsSource = Enum.GetValues<FigureStyle>() };
	private readonly CheckBox _figureSmooth = new() { Content = "Smooth" };
	private readonly Slider _sides = new() { Minimum = 3, Maximum = 12, Width = 80, SmallChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1 };
	private Control _sizeOption = null!, _toleranceOption = null!, _mixOptions = null!, _selectShapeOption = null!, _selectModeOption = null!,
		_figureOptions = null!, _sidesOption = null!, _selectionButtons = null!;

	// The layers
	internal readonly ListBox LayerList = new() { MinHeight = 120 };
	internal readonly TextBox LayerName = new();
	private readonly Slider _layerOpacity = new() { Minimum = 0, Maximum = 100 };

	private static readonly SKColor[] PaletteColors =
	{
		SKColors.Black, new(64, 64, 64), new(128, 128, 128), SKColors.White,
		new(220, 50, 50), new(240, 140, 40), new(250, 220, 60), new(70, 180, 80),
		new(60, 200, 220), new(60, 100, 220), new(140, 80, 200), new(240, 120, 180),
		new(245, 205, 160), new(215, 160, 120), new(120, 70, 40), new(70, 130, 200),
	};

	private static Border Swatch(double size) => new()
	{
		Width = size,
		Height = size,
		BorderBrush = Brushes.Gray,
		BorderThickness = new Thickness(1),
		CornerRadius = new CornerRadius(3),
		Cursor = new Cursor(StandardCursorType.Hand)
	};

	private Control BuildPaintPanel(ColumnDefinition column)
	{
		_paintColumn = column;
		_paintCanvas = new PaintCanvas(Paint) { Modifiers = () => _mods, LightTable = _painter.LightTable };
		Paint.Changed += SyncPaintUi;
		State.Undo.Changed += SyncPaintUi;

		static WrapPanel Wrap() => new() { Orientation = Orientation.Horizontal };
		static Control Labelled(string label, Control c) => new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			Margin = new Thickness(0, 0, 12, 4),
			Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, c }
		};
		Button B(string text, Action click)
		{
			var b = new Button { Content = text, Margin = new Thickness(0, 0, 4, 4) };
			b.Click += (_, _) => click();
			return b;
		}

		// Title: what's open; close it, or change its canvas size
		var close = new Button { Content = "✕ Close" };
		close.Click += (_, _) => ClosePaintEditor();
		var size = new Button { Content = "Canvas size…" };
		size.Click += async (_, _) => await AskCanvasSize();
		var title = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
		var titleButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { size, close } };
		DockPanel.SetDock(titleButtons, Dock.Right);
		title.Children.Add(titleButtons);
		title.Children.Add(_paintTitle);

		// The tools
		var tools = Wrap();
		var keys = new Dictionary<string, string>
		{
			["Select"] = "M", ["Wand"] = "W", ["Move"] = "V", ["Pencil"] = "N", ["Brush"] = "B", ["Mixer"] = "X",
			["Eraser"] = "E", ["Picker"] = "I", ["Fill"] = "G", ["Gradient"] = "D", ["Figure"] = "U"
		};
		foreach (string name in PaintToolbox.Names)
		{
			string n = name;
			var button = new ToggleButton { Content = name, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3) };
			ToolTip.SetTip(button, $"{name} ({keys[name]})");
			button.Click += (_, _) => SelectPaintTool(n);
			_paintToolButtons[n] = button;
			tools.Children.Add(button);
		}

		// The options
		var options = Wrap();
		_sizeOption = Labelled("Size", PaintSize);
		_toleranceOption = Labelled("Tolerance", _tolerance);
		_mixOptions = new StackPanel { Orientation = Orientation.Horizontal, Children = { Labelled("Wet", _wet), Labelled("Load", _load) } };
		_selectShapeOption = Labelled("Shape", _selectShape);
		_selectModeOption = Labelled("Mode", _selectMode);
		_selectionButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { B("Invert", () => Paint.InvertSelection()), B("Deselect", () => Paint.Deselect()) } };
		_sidesOption = Labelled("Sides", _sides);
		_figureOptions = new StackPanel { Orientation = Orientation.Horizontal, Children = { Labelled("Figure", _figureShape), Labelled("Style", _figureStyle), _figureSmooth } };
		foreach (var c in new Control[] { _toolTitle, _sizeOption, _toleranceOption, _contiguous, _sampleAll, _mixOptions, _toTransparent,
			_selectShapeOption, _selectModeOption, _selectionButtons, _figureOptions, _sidesOption })
		{
			c.Margin = new Thickness(0, 0, 12, 4);
			options.Children.Add(c);
		}
		_toolTitle.MaxWidth = 230;

		PaintSize.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncingPaint) Paint.Size = (int)PaintSize.Value; };
		_tolerance.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncingPaint) Paint.Tolerance = (int)_tolerance.Value; };
		_wet.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncingPaint) Paint.MixWet = (float)(_wet.Value / 100); };
		_load.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncingPaint) Paint.MixLoad = (float)(_load.Value / 100); };
		_sides.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncingPaint) { Paint.PolygonSides = (int)_sides.Value; } };
		_contiguous.IsCheckedChanged += (_, _) => { if (!_syncingPaint) Paint.Contiguous = _contiguous.IsChecked == true; };
		_sampleAll.IsCheckedChanged += (_, _) => { if (!_syncingPaint) Paint.SampleAllLayers = _sampleAll.IsChecked == true; };
		_toTransparent.IsCheckedChanged += (_, _) => { if (!_syncingPaint) Paint.GradientToTransparent = _toTransparent.IsChecked == true; };
		_figureSmooth.IsCheckedChanged += (_, _) => { if (!_syncingPaint) Paint.FigureSmooth = _figureSmooth.IsChecked == true; };
		_selectShape.SelectionChanged += (_, _) =>
		{
			if (_syncingPaint || _selectShape.SelectedItem is not SelectShape shape) return;
			PaintTools.Select.Cancel();   // a shape half-made with the old setting is dropped
			Paint.SelectShape = shape;
		};
		_selectMode.SelectionChanged += (_, _) => { if (!_syncingPaint && _selectMode.SelectedItem is SelectMode mode) Paint.SelectMode = mode; };
		_figureShape.SelectionChanged += (_, _) => { if (!_syncingPaint && _figureShape.SelectedItem is FigureShape f) { Paint.Figure = f; SyncToolOptions(); } };
		_figureStyle.SelectionChanged += (_, _) => { if (!_syncingPaint && _figureStyle.SelectedItem is FigureStyle f) { Paint.FigureStyle = f; SyncToolOptions(); } };

		// The colours: primary and secondary (click to pick), swapped, typed; a palette (left
		// click: primary, right: secondary)
		var colours = Wrap();
		_primarySwatch.PointerPressed += async (_, _) => { if (await ColourDialog.Ask(this, "Primary colour", Paint.Color) is { } c) Paint.SetColor(c); };
		_secondarySwatch.PointerPressed += async (_, _) => { if (await ColourDialog.Ask(this, "Secondary colour", Paint.SecondaryColor) is { } c) Paint.SetSecondaryColor(c); };
		ToolTip.SetTip(_primarySwatch, "The primary colour (click to pick)");
		ToolTip.SetTip(_secondarySwatch, "The secondary colour (click to pick)");
		var swap = B("⇄", () => Paint.SwapColors());
		ToolTip.SetTip(swap, "Swap the colours (S)");
		PaintHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPaintHex(); PaintView.Focus(); } };
		PaintHex.LostFocus += (_, _) => CommitPaintHex();
		foreach (var c in new Control[] { _primarySwatch, _secondarySwatch, swap, PaintHex }) { c.Margin = new Thickness(0, 0, 6, 4); c.VerticalAlignment = VerticalAlignment.Center; colours.Children.Add(c); }
		foreach (var colour in PaletteColors)
		{
			var c = colour;
			var swatch = Swatch(18);
			swatch.Background = new SolidColorBrush(ColourDialog.ToAvalonia(c));
			swatch.Margin = new Thickness(0, 0, 3, 4);
			swatch.VerticalAlignment = VerticalAlignment.Center;
			ToolTip.SetTip(swatch, $"{ColourDialog.Hex(c)} (left-click: primary, right-click: secondary)");
			swatch.PointerPressed += (_, e) =>
			{
				if (e.GetCurrentPoint(swatch).Properties.IsRightButtonPressed) Paint.SetSecondaryColor(c);
				else Paint.SetColor(c);
			};
			colours.Children.Add(swatch);
		}

		// The layers, top first
		var layerButtons = Wrap();
		layerButtons.Children.Add(B("+", () => Paint.AddLayer()));
		layerButtons.Children.Add(B("−", () => Paint.DeleteLayer()));
		layerButtons.Children.Add(B("▲", () => Paint.MoveLayer(+1)));
		layerButtons.Children.Add(B("▼", () => Paint.MoveLayer(-1)));
		LayerList.SelectionChanged += (_, _) =>
		{
			if (_syncingPaint || LayerList.SelectedItem is not ListBoxItem { Tag: AssetLayer layer }) return;
			Paint.Layer = layer;
			SyncLayerFields();
		};
		LayerName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitLayerName(); PaintView.Focus(); } };
		LayerName.LostFocus += (_, _) => CommitLayerName();
		_layerOpacity.PropertyChanged += (_, e) =>
		{
			if (e.Property == RangeBase.ValueProperty && !_syncingPaint && Paint.Layer is { } layer) Paint.SetLayerOpacity(layer, (float)(_layerOpacity.Value / 100));
		};
		var layers = new StackPanel
		{
			Width = 190,
			Margin = new Thickness(6, 0, 0, 0),
			Spacing = 4,
			Children = { new TextBlock { Text = "Layers", FontWeight = FontWeight.SemiBold }, LayerList, layerButtons, LayerName, new TextBlock { Text = "Opacity" }, _layerOpacity }
		};

		// The canvas
		PaintView.Draw = (canvas, size, dpi) =>
		{
			canvas.Clear(CanvasTheme.Sk("Viewport"));
			canvas.Scale(dpi);
			_paintCanvas.ViewSize = size;
			_paintCanvas.Draw(canvas);
		};
		PaintView.PointerPressed += (_, e) =>
		{
			PaintView.Focus();
			_mods = Input.ModsOf(e.KeyModifiers);
			if (Input.ButtonOf(e.GetCurrentPoint(PaintView).Properties.PointerUpdateKind) is not { } button) return;
			_paintCanvas.MouseDown(Input.At(e, PaintView), button, e.KeyModifiers.HasFlag(KeyModifiers.Alt), e.ClickCount);
			e.Pointer.Capture(PaintView);
			e.Handled = true;
		};
		PaintView.PointerMoved += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			_paintCanvas.MouseMove(Input.At(e, PaintView));
		};
		PaintView.PointerReleased += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			if (Input.ButtonOf(e.InitialPressMouseButton) is { } button) _paintCanvas.MouseUp(button);
			e.Pointer.Capture(null);
		};
		PaintView.PointerExited += (_, _) => _paintCanvas.MouseLeave();
		PaintView.PointerWheelChanged += (_, e) =>
		{
			_paintCanvas.Wheel(Input.At(e, PaintView), (int)Math.Round(e.Delta.Y * 120));
			e.Handled = true;
		};

		// The title on top; the tools, their options, the colours and the layers down the side
		// (scrolling when short), so the canvas has all the height there is
		layers.Width = double.NaN;
		layers.Margin = new Thickness(0, 8, 0, 0);
		var side = new ScrollViewer
		{
			Width = 250,
			Content = new StackPanel { Margin = new Thickness(6, 0, 8, 4), Children = { tools, options, colours, layers } }
		};
		title.Margin = new Thickness(6, 4);
		var body = new DockPanel();
		DockPanel.SetDock(title, Dock.Top);
		body.Children.Add(title);
		DockPanel.SetDock(side, Dock.Right);
		body.Children.Add(side);
		body.Children.Add(PaintView);
		PaintPanel = body;
		PaintPanel.IsVisible = false;
		SelectPaintTool("Brush");
		return PaintPanel;
	}

	// ======================= open and close =======================

	// A double-click on a part: its drawing in the editor (a blank part: inside the picked one's box)
	internal bool TryOpenPaintAt(Vector2 screen)
	{
		var world = _camera.ScreenToWorld(screen);
		if (_renderer.Pick(State.Skeleton, world) is { } hit)
		{
			State.Selected = hit.Slot.Bone;
			State.SelectedSlot = hit.Slot;
			return OpenPaintEditorOn(hit.Slot);
		}
		return State.SelectedSlot is { } slot && _session.PictureOf(slot) != null && _tool.InsidePart(slot, world) && OpenPaintEditorOn(slot);
	}

	// The part's drawing (editing a skin: the skin's own)
	internal bool OpenPaintEditorOn(Slot slot)
	{
		if (!_session.OpenPaintOn(slot)) return false;
		ShowPaintEditor(fit: true);
		return true;
	}

	private void ShowPaintEditor(bool fit)
	{
		_renderer.Highlight = Paint.Asset;
		_paintColumn.Width = new GridLength(1, GridUnitType.Star);
		PaintPanel.IsVisible = true;
		_paintSplitter.IsVisible = true;
		if (fit) _paintCanvas.RequestFit();
		SyncPaintUi();
	}

	internal void ClosePaintEditor()
	{
		_session.ClosePaint();
		_renderer.Highlight = null;
		_paintColumn.Width = new GridLength(0);
		PaintPanel.IsVisible = false;
		_paintSplitter.IsVisible = false;
	}

	// Every frame: the editor follows the part's drawing along the timeline
	private void FollowPaint()
	{
		if (!_session.PaintOpen)
		{
			if (PaintPanel.IsVisible) ClosePaintEditor();   // (closed by the session: another object, another file)
			return;
		}
		switch (_session.FollowPaint(busy: _paintCanvas.IsBusy))
		{
			case EditorSession.PaintFollow.Close: ClosePaintEditor(); break;
			case EditorSession.PaintFollow.Opened: ShowPaintEditor(fit: false); break;
			case EditorSession.PaintFollow.OpenedNewSize: ShowPaintEditor(fit: true); break;
		}
		PaintView.InvalidateVisual();
	}

	private async Task AskCanvasSize()
	{
		if (Paint.Asset is not { } asset) return;
		string? answer = await Dialogs.AskText(this, "Canvas size", "The new size, width × height (pixels; the picture stays in the middle):",
			$"{asset.Width} x {asset.Height}", t => ParseSize(t) == null ? "Two whole numbers, like 256 x 128." : null);
		if (answer == null || ParseSize(answer) is not { } wh) return;
		Paint.ResizeCanvas(wh.W, wh.H, 1, 1);
		_paintCanvas.RequestFit();
	}

	private static (int W, int H)? ParseSize(string text)
	{
		var parts = text.Split(new[] { 'x', 'X', '×', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
		return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w is > 0 and <= 8192 && h is > 0 and <= 8192
			? (w, h) : null;
	}

	// ======================= tools =======================

	internal void SelectPaintTool(string name)
	{
		_session.SelectPaintTool(name);
		foreach (var (n, button) in _paintToolButtons) button.IsChecked = n == name;
		SyncToolOptions();
	}

	// Only the options the tool in hand uses show
	private void SyncToolOptions()
	{
		var t = Paint.Tool;
		bool strokes = PaintTools.DrawsStrokes(t);
		bool outline = t == PaintTools.Figure && Paint.FigureStyle != FigureStyle.Filled;
		bool bySimilarColour = PaintTools.BySimilarColour(t);
		_sizeOption.IsVisible = strokes || outline;
		_toleranceOption.IsVisible = bySimilarColour;
		_contiguous.IsVisible = bySimilarColour;
		_sampleAll.IsVisible = bySimilarColour || t == PaintTools.Mixer;
		_mixOptions.IsVisible = t == PaintTools.Mixer;
		_toTransparent.IsVisible = t == PaintTools.Gradient;
		_selectShapeOption.IsVisible = t == PaintTools.Select;
		_selectModeOption.IsVisible = PaintTools.PicksShapes(t);
		_selectionButtons.IsVisible = PaintTools.PicksShapes(t) || t == PaintTools.Move;
		_figureOptions.IsVisible = t == PaintTools.Figure;
		_sidesOption.IsVisible = t == PaintTools.Figure && Paint.Figure == FigureShape.Polygon;
		_toolTitle.Text = t switch
		{
			_ when t == PaintTools.Move => "Move: drag the selected pixels (or the whole layer when nothing is selected).",
			_ when t == PaintTools.Picker => "Picker: click or drag over the picture to take its colour (Alt+click works with most tools).",
			_ => $"{PaintTools.NameOf(t)}:"
		};
	}

	private void CommitPaintHex()
	{
		if (SKColor.TryParse(PaintHex.Text?.Trim() ?? "", out var colour)) Paint.SetColor(colour);
		else PaintHex.Text = ColourDialog.Hex(Paint.Color);
	}

	private void CommitLayerName()
	{
		if (!_syncingPaint && Paint.Layer is { } layer && (LayerName.Text?.Trim() ?? "") is { Length: > 0 } name && name != layer.Name) Paint.RenameLayer(layer, name);
	}

	// ======================= keys over the canvas =======================

	private static readonly Dictionary<Key, string> PaintToolKeys = new()
	{
		[Key.M] = "Select", [Key.W] = "Wand", [Key.V] = "Move", [Key.N] = "Pencil", [Key.B] = "Brush", [Key.X] = "Mixer",
		[Key.E] = "Eraser", [Key.I] = "Picker", [Key.G] = "Fill", [Key.D] = "Gradient", [Key.U] = "Figure"
	};

	// While the mouse is over the canvas: the paint editor's keys first (true: handled)
	internal bool HandlePaintKey(Key key, KeyModifiers mods)
	{
		if (!_session.PaintOpen || !PaintView.IsPointerOver) return false;
		var tools = PaintTools;
		switch (key, mods)
		{
			case (_, KeyModifiers.None) when PaintToolKeys.TryGetValue(key, out var tool): SelectPaintTool(tool); return true;
			case (Key.S, KeyModifiers.None): Paint.SwapColors(); return true;
			case (Key.OemOpenBrackets, KeyModifiers.None): Paint.Size = Math.Max(1, Paint.Size - 1); SyncPaintUi(); return true;
			case (Key.OemCloseBrackets, KeyModifiers.None): Paint.Size = Math.Min(64, Paint.Size + 1); SyncPaintUi(); return true;
			case (Key.A, KeyModifiers.Control): Paint.SelectAll(); return true;
			case (Key.D, KeyModifiers.Control): Paint.Deselect(); return true;
			case (Key.I, KeyModifiers.Control | KeyModifiers.Shift): Paint.InvertSelection(); return true;
			case (Key.Delete, KeyModifiers.None) when Paint.Selection != null: Paint.DeleteSelectedPixels(); return true;
			case (Key.Enter, KeyModifiers.None) when tools.Figure.InProgress: tools.Figure.Finish(Paint); return true;
			case (Key.Enter, KeyModifiers.None) when tools.Select.PlacingPoints: tools.Select.Finish(Paint); return true;
			case (Key.Back, KeyModifiers.None) when tools.Figure.InProgress: tools.Figure.RemoveLastPoint(Paint); return true;
			case (Key.Back, KeyModifiers.None) when tools.Select.PlacingPoints: tools.Select.RemoveLastPoint(Paint); return true;
			case (Key.Escape, KeyModifiers.None) when tools.Figure.InProgress: tools.Figure.Cancel(Paint); return true;
			case (Key.Escape, KeyModifiers.None) when tools.Select.PlacingPoints: tools.Select.Cancel(); return true;
			case (Key.Escape, KeyModifiers.None) when Paint.Selection != null: Paint.Deselect(); return true;
			default: return false;
		}
	}

	// ======================= the controls follow the paint session =======================

	private void SyncPaintUi()
	{
		if (Paint.Asset is not { } asset || PaintPanel == null) return;
		_syncingPaint = true;
		try
		{
			string part = _session.PaintSlot is { } slot && slot.Name != asset.Name ? $" of {slot.Name}" : "";
			_paintTitle.Text = $"Painting: {asset.Name}{part}  ({asset.Width}×{asset.Height})";
			_primarySwatch.Background = new SolidColorBrush(ColourDialog.ToAvalonia(Paint.Color));
			_secondarySwatch.Background = new SolidColorBrush(ColourDialog.ToAvalonia(Paint.SecondaryColor));
			if (!PaintHex.IsFocused) PaintHex.Text = ColourDialog.Hex(Paint.Color);
			PaintSize.Value = Paint.Size;
			_tolerance.Value = Paint.Tolerance;
			_contiguous.IsChecked = Paint.Contiguous;
			_sampleAll.IsChecked = Paint.SampleAllLayers;
			_toTransparent.IsChecked = Paint.GradientToTransparent;
			_wet.Value = Paint.MixWet * 100;
			_load.Value = Paint.MixLoad * 100;
			_selectShape.SelectedItem = Paint.SelectShape;
			_selectMode.SelectedItem = Paint.SelectMode;
			_figureShape.SelectedItem = Paint.Figure;
			_figureStyle.SelectedItem = Paint.FigureStyle;
			_figureSmooth.IsChecked = Paint.FigureSmooth;
			_sides.Value = Paint.PolygonSides;

			var items = new List<ListBoxItem>();
			ListBoxItem? picked = null;
			for (int i = asset.Layers.Count - 1; i >= 0; i--)
			{
				var layer = asset.Layers[i];
				var visible = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center };
				visible.IsCheckedChanged += (_, _) => { if (!_syncingPaint) Paint.SetLayerVisible(layer, visible.IsChecked == true); };
				var item = new ListBoxItem
				{
					Tag = layer,
					Padding = new Thickness(4, 1),
					Content = new StackPanel
					{
						Orientation = Orientation.Horizontal,
						Children =
						{
							visible,
							new TextBlock
							{
								Text = (layer.IsColorLayer ? "◐ " : "") + (layer.Opacity < 1f ? $"{layer.Name}  ({layer.Opacity:P0})" : layer.Name),
								VerticalAlignment = VerticalAlignment.Center
							}
						}
					}
				};
				items.Add(item);
				if (layer == Paint.Layer) picked = item;
			}
			LayerList.ItemsSource = items;
			LayerList.SelectedItem = picked;
		}
		finally
		{
			_syncingPaint = false;
		}
		SyncLayerFields();
		SyncToolOptions();
	}

	private void SyncLayerFields()
	{
		_syncingPaint = true;
		try
		{
			if (!LayerName.IsFocused) LayerName.Text = Paint.Layer?.Name ?? "";
			_layerOpacity.Value = (Paint.Layer?.Opacity ?? 1f) * 100;
		}
		finally
		{
			_syncingPaint = false;
		}
	}
}
