using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Animator.Viewer;

public partial class MainWindow
{
	private PaintSession _paint = null!;
	private PaintCanvas _paintCanvas = null!;
	private bool _syncingUi;

	private readonly ShapeSelectTool _select = new();
	private readonly MagicWandTool _wand = new();
	private readonly MoveTool _move = new();
	private readonly StrokeTool _pencil = new(StrokeTool.Kind.Pencil);
	private readonly StrokeTool _brush = new(StrokeTool.Kind.Brush);
	private readonly MixerTool _mixer = new();
	private readonly StrokeTool _eraser = new(StrokeTool.Kind.Eraser);
	private readonly EyedropperTool _picker = new();
	private readonly FillTool _fill = new();
	private readonly GradientTool _gradient = new();
	private readonly FigureTool _figure = new();

	private static readonly SKColor[] PaletteColors =
	{
		SKColors.Black, new(64, 64, 64), new(128, 128, 128), SKColors.White,
		new(220, 50, 50), new(240, 140, 40), new(250, 220, 60), new(70, 180, 80),
		new(60, 200, 220), new(60, 100, 220), new(140, 80, 200), new(240, 120, 180),
		new(245, 205, 160), new(215, 160, 120), new(120, 70, 40), new(70, 130, 200),
	};

	private void InitPaint()
	{
		_paint = new PaintSession(_assets, _state.Undo, _skeleton, _brush);
		_paintCanvas = new PaintCanvas(_paint);
		_paint.Changed += SyncPaintUi;
		_state.Undo.Changed += SyncPaintUi;

		foreach (var color in PaletteColors)
		{
			var swatch = new Button
			{
				Width = 18,
				Height = 18,
				Margin = new Thickness(0, 0, 3, 3),
				Background = new SolidColorBrush(ToWpf(color)),
				BorderBrush = Brushes.Black,
				ToolTip = $"{Hex(color)}  (left-click: primary, right-click: secondary)"
			};
			swatch.Click += (_, _) => _paint.SetColor(color);
			swatch.MouseRightButtonUp += (_, _) => _paint.SetSecondaryColor(color);
			Palette.Children.Add(swatch);
		}

		FigureShapeBox.ItemsSource = Enum.GetValues<FigureShape>();
		FigureShapeBox.SelectedItem = _paint.Figure;
		FigureStyleBox.ItemsSource = Enum.GetValues<FigureStyle>();
		FigureStyleBox.SelectedItem = _paint.FigureStyle;
		SelectShapeBox.ItemsSource = Enum.GetValues<SelectShape>();
		SelectShapeBox.SelectedItem = _paint.SelectShape;
		SelectModeBox.ItemsSource = Enum.GetValues<SelectMode>();
		SelectModeBox.SelectedItem = _paint.SelectMode;

		SelectTool("Brush");
	}

	// ---------- open / close ----------

	private bool TryOpenPaintAt(Vector2 screen)
	{
		var world = _camera.ScreenToWorld(screen);
		if (_renderer.Pick(_skeleton, world) is { } hit)
		{
			_state.Selected = hit.Slot.Bone;
			_state.SelectedSlot = hit.Slot;
			OpenPaintEditor(PixelsToPaint(hit.Slot, hit.Asset), hit.Slot);
			return true;
		}

		// A blank (or see-through) part has no pixels to hit: inside the selected part's box will do
		if (_state.SelectedSlot is { } slot && PartData(slot).Asset is { } asset && _tool.InsidePart(slot, world))
		{
			OpenPaintEditor(PixelsToPaint(slot, asset), slot);
			return true;
		}
		return false;
	}

	// slot: the part this drawing belongs to, so the editor can follow its drawings
	// along the timeline. fit: zoom to fit (off when stepping between same-size drawings).
	private void OpenPaintEditor(Asset asset, Slot? slot, bool fit = true)
	{
		_paint.Tool.Deactivate(_paint);   // finish any half-placed figure first
		// Another drawing of the same part: stay on the same layer number (lines, colors…)
		int layer = slot != null && slot == _paintSlot && _paint.Asset is { } previous && _paint.Layer is { } current
			? previous.Layers.IndexOf(current)
			: -1;
		_paint.Open(asset, layer);
		_paintSlot = slot;
		_paintFollowed = slot != null ? PartData(slot).Asset : null;
		_renderer.Highlight = asset;
		PaintColumn.Width = new GridLength(1, GridUnitType.Star);
		PaintSplitter.Visibility = Visibility.Visible;
		PaintPanel.Visibility = Visibility.Visible;
		if (fit) _paintCanvas.RequestFit();
	}

	private void ClosePaintEditor()
	{
		_paint.Tool.Deactivate(_paint);
		_paint.Close();
		_paintSlot = null;
		_paintFollowed = null;
		_renderer.Highlight = null;
		PaintColumn.Width = new GridLength(0);
		PaintSplitter.Visibility = Visibility.Collapsed;
		PaintPanel.Visibility = Visibility.Collapsed;
	}

	private void ClosePaint_Click(object sender, RoutedEventArgs e) => ClosePaintEditor();

	private void CanvasSize_Click(object sender, RoutedEventArgs e)
	{
		if (_paint.Asset is not { } asset) return;

		// Owner = this centers the dialog over our window and keeps it on top of it
		var dialog = new CanvasSizeDialog(asset.Width, asset.Height) { Owner = this };
		if (dialog.ShowDialog() == true)   // ShowDialog waits until the dialog closes
		{
			_paint.ResizeCanvas(dialog.NewWidth, dialog.NewHeight, dialog.AnchorX, dialog.AnchorY);
			_paintCanvas.RequestFit();
		}
	}

	// ---------- canvas ----------

	private void OnPaintViewPaint(object? sender, SKPaintSurfaceEventArgs e)
	{
		if (PaintView.ActualWidth <= 0) return;
		var canvas = e.Surface.Canvas;
		canvas.Scale((float)(e.Info.Width / PaintView.ActualWidth));
		_paintCanvas.ViewSize = new((float)PaintView.ActualWidth, (float)PaintView.ActualHeight);
		_paintCanvas.Draw(canvas);
	}

	private void PaintView_MouseDown(object sender, MouseButtonEventArgs e)
	{
		PaintView.Focus();
		_paintCanvas.MouseDown(PosIn(PaintView, e), e.ChangedButton,
							   Keyboard.Modifiers.HasFlag(ModifierKeys.Alt), e.ClickCount);
		PaintView.CaptureMouse();
	}

	private void PaintView_MouseMove(object sender, MouseEventArgs e) =>
		_paintCanvas.MouseMove(PosIn(PaintView, e));

	private void PaintView_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_paintCanvas.MouseUp(e.ChangedButton);
		if (e.LeftButton == MouseButtonState.Released &&
			e.MiddleButton == MouseButtonState.Released &&
			e.RightButton == MouseButtonState.Released)
			PaintView.ReleaseMouseCapture();
	}

	private void PaintView_MouseWheel(object sender, MouseWheelEventArgs e) =>
		_paintCanvas.Wheel(PosIn(PaintView, e), e.Delta);

	private void PaintView_MouseLeave(object sender, MouseEventArgs e) => _paintCanvas.MouseLeave();

	// ---------- tools & settings ----------

	private void SelectTool(string name)
	{
		PaintTool tool = name switch
		{
			"Select" => _select,
			"Wand" => _wand,
			"Move" => _move,
			"Pencil" => _pencil,
			"Brush" => _brush,
			"Mixer" => _mixer,
			"Eraser" => _eraser,
			"Picker" => _picker,
			"Fill" => _fill,
			"Gradient" => _gradient,
			"Figure" => _figure,
			_ => _paint.Tool
		};
		if (tool != _paint.Tool) _paint.Tool.Deactivate(_paint);
		_paint.Tool = tool;

		foreach (var rb in ToolButtons.Children.OfType<RadioButton>())
			rb.IsChecked = (string)rb.Tag == name;
		SyncToolOptions();
	}

	// Only the options the current tool uses are shown (changing one never switches tools)
	private void SyncToolOptions()
	{
		var t = _paint.Tool;
		bool strokes = t == _pencil || t == _brush || t == _eraser || t == _mixer;
		bool outline = t == _figure && _paint.FigureStyle != FigureStyle.Filled;   // the figure's line width
		bool bySimilarColour = t == _fill || t == _wand;

		Show(SizeOption, strokes || outline);
		Show(ToleranceOption, bySimilarColour);
		Show(ContiguousBox, bySimilarColour);
		Show(SampleAllBox, bySimilarColour || t == _mixer);
		Show(MixOptions, t == _mixer);
		Show(GradientTransparentBox, t == _gradient);
		Show(SelectShapeOption, t == _select);
		Show(SelectModeOption, t == _select || t == _wand);
		Show(FigureOptions, t == _figure);
		Show(SidesOption, _paint.Figure == FigureShape.Polygon);

		ToolOptionsTitle.Text = t switch
		{
			_ when t == _move => "Move: drag the selected pixels (or the whole layer when nothing is selected).",
			_ when t == _picker => "Picker: click or drag over the picture to take its colour (Alt+click works with most tools).",
			_ => $"{ToolLabel(t)}:"
		};

		static void Show(UIElement element, bool visible) =>
			element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
	}

	private string ToolLabel(PaintTool t) =>
		ToolButtons.Children.OfType<RadioButton>().FirstOrDefault(rb => rb.IsChecked == true)?.Tag as string ?? t.Name;

	private void Tool_Click(object sender, RoutedEventArgs e) =>
		SelectTool((string)((FrameworkElement)sender).Tag);

	private void SetBrushSize(int size)
	{
		_paint.Size = Math.Clamp(size, 1, 64);
		SyncPaintUi();
	}

	private void SizeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingUi || _paint is null) return;
		_paint.Size = (int)e.NewValue;
		SizeLabel.Text = _paint.Size.ToString();
	}

	private void ToleranceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingUi || _paint is null) return;
		_paint.Tolerance = (int)e.NewValue;
		ToleranceLabel.Text = _paint.Tolerance.ToString();
	}

	private void SampleAll_Click(object sender, RoutedEventArgs e)
	{
		if (_paint is null) return;
		_paint.SampleAllLayers = SampleAllBox.IsChecked == true;
	}

	private void MixSliders_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingUi || _paint is null) return;
		_paint.MixWet = (float)(WetSlider.Value / 100);
		_paint.MixLoad = (float)(LoadSlider.Value / 100);
	}

	private void FigureShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null) return;
		ReadFigureOptions();
	}

	private void FigureOption_Click(object sender, RoutedEventArgs e) => ReadFigureOptions();
	private void SidesSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ReadFigureOptions();

	private void ReadFigureOptions()
	{
		if (_syncingUi || _paint is null) return;
		if (FigureShapeBox.SelectedItem is FigureShape shape) _paint.Figure = shape;
		if (FigureStyleBox.SelectedItem is FigureStyle style) _paint.FigureStyle = style;
		_paint.FigureSmooth = FigureSmoothBox.IsChecked == true;
		_paint.PolygonSides = (int)SidesSlider.Value;
		SidesLabel.Text = _paint.PolygonSides.ToString();
		SyncToolOptions();   // sides only for polygons, a line width only with an outline
	}

	private void FigureStyle_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null) return;
		ReadFigureOptions();
	}

	private void SwapColors_Click(object sender, RoutedEventArgs e) => _paint.SwapColors();

	// ---------- colour picker ----------

	private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
	{
		var dialog = new ColorPickerDialog(_paint.Color, "Primary colour") { Owner = this };
		if (dialog.ShowDialog() == true) _paint.SetColor(dialog.Color);
	}

	private void SecondarySwatch_Click(object sender, MouseButtonEventArgs e)
	{
		var dialog = new ColorPickerDialog(_paint.SecondaryColor, "Secondary colour") { Owner = this };
		if (dialog.ShowDialog() == true) _paint.SetSecondaryColor(dialog.Color);
	}

	// ---------- selection options ----------

	private void SelectShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null || SelectShapeBox.SelectedItem is not SelectShape shape) return;
		_select.Cancel();   // a shape half-made with the old setting is dropped
		_paint.SelectShape = shape;
	}

	private void SelectMode_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null || SelectModeBox.SelectedItem is not SelectMode mode) return;
		_paint.SelectMode = mode;
	}

	private void Contiguous_Click(object sender, RoutedEventArgs e)
	{
		if (_paint is null) return;
		_paint.Contiguous = ContiguousBox.IsChecked == true;
	}

	private void InvertSelection_Click(object sender, RoutedEventArgs e) => _paint.InvertSelection();
	private void Deselect_Click(object sender, RoutedEventArgs e) => _paint.Deselect();

	private void GradientTransparent_Click(object sender, RoutedEventArgs e)
	{
		if (_paint is null) return;
		_paint.GradientToTransparent = GradientTransparentBox.IsChecked == true;
	}

	private void ColorHex_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitHex();
		PaintView.Focus();
	}

	private void ColorHex_Commit(object sender, RoutedEventArgs e) => CommitHex();

	private void CommitHex()
	{
		if (SKColor.TryParse(ColorHex.Text.Trim(), out var color)) _paint.SetColor(color);
		else ColorHex.Text = Hex(_paint.Color);
	}

	// ---------- layers ----------

	private void LayerAdd_Click(object sender, RoutedEventArgs e) => _paint.AddLayer();
	private void LayerDelete_Click(object sender, RoutedEventArgs e) => _paint.DeleteLayer();
	private void LayerUp_Click(object sender, RoutedEventArgs e) => _paint.MoveLayer(+1);
	private void LayerDown_Click(object sender, RoutedEventArgs e) => _paint.MoveLayer(-1);

	private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi) return;
		if (LayerList.SelectedItem is FrameworkElement { Tag: AssetLayer layer })
		{
			_paint.Layer = layer;
			SyncLayerFields();
		}
	}

	private void LayerOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingUi || _paint is null || _paint.Layer is not { } layer) return;
		_paint.SetLayerOpacity(layer, (float)(e.NewValue / 100));
	}

	private void LayerName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitLayerName();
		PaintView.Focus();
	}

	private void LayerName_Commit(object sender, RoutedEventArgs e) => CommitLayerName();

	private void CommitLayerName()
	{
		if (_paint?.Layer is not { } layer) return;
		_paint.RenameLayer(layer, LayerName.Text);
	}

	// ---------- keep the controls in sync with the session ----------

	private void SyncPaintUi()
	{
		if (_paint.Asset is not { } asset) return;
		_syncingUi = true;

		string part = _paintSlot != null && _paintSlot.Name != asset.Name ? $" of {_paintSlot.Name}" : "";
		PaintTitle.Text = $"Editing: {asset.Name}{part}  ({asset.Width}×{asset.Height})";
		ColorSwatch.Fill = new SolidColorBrush(ToWpf(_paint.Color));
		SecondarySwatch.Fill = new SolidColorBrush(ToWpf(_paint.SecondaryColor));
		if (!ColorHex.IsKeyboardFocused) ColorHex.Text = Hex(_paint.Color);
		SizeSlider.Value = _paint.Size;
		SizeLabel.Text = _paint.Size.ToString();
		ToleranceSlider.Value = _paint.Tolerance;
		ToleranceLabel.Text = _paint.Tolerance.ToString();
		SampleAllBox.IsChecked = _paint.SampleAllLayers;
		GradientTransparentBox.IsChecked = _paint.GradientToTransparent;
		WetSlider.Value = _paint.MixWet * 100;
		LoadSlider.Value = _paint.MixLoad * 100;
		FigureShapeBox.SelectedItem = _paint.Figure;
		FigureStyleBox.SelectedItem = _paint.FigureStyle;
		FigureSmoothBox.IsChecked = _paint.FigureSmooth;
		SidesSlider.Value = _paint.PolygonSides;
		SidesLabel.Text = _paint.PolygonSides.ToString();
		SelectShapeBox.SelectedItem = _paint.SelectShape;
		SelectModeBox.SelectedItem = _paint.SelectMode;
		ContiguousBox.IsChecked = _paint.Contiguous;

		LayerList.Items.Clear();
		for (int i = asset.Layers.Count - 1; i >= 0; i--)
		{
			var layer = asset.Layers[i];

			var visible = new CheckBox
			{
				IsChecked = layer.Visible,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 6, 0),
				ToolTip = "Visible"
			};
			visible.Click += (_, _) => _paint.SetLayerVisible(layer, visible.IsChecked == true);

			var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = layer };
			row.Children.Add(visible);
			row.Children.Add(new TextBlock
			{
				Text = (layer.IsColorLayer ? "◐ " : "") + (layer.Opacity < 1f ? $"{layer.Name}  ({layer.Opacity:P0})" : layer.Name),
				Foreground = layer.IsColorLayer ? new SolidColorBrush(Color.FromRgb(150, 210, 255)) : Brushes.White,
				ToolTip = layer.IsColorLayer ? $"A colour layer ({ColorAdjust.KindName(layer.Adjust!.Kind)}): it changes the layers under it" : null
			});

			LayerList.Items.Add(row);
			if (layer == _paint.Layer) LayerList.SelectedItem = row;
		}

		_syncingUi = false;
		SyncLayerFields();
		SyncToolOptions();
	}

	// Name and opacity of the active layer
	private void SyncLayerFields()
	{
		bool wasSyncing = _syncingUi;
		_syncingUi = true;

		var layer = _paint.Layer;
		LayerName.IsEnabled = LayerOpacity.IsEnabled = layer != null;
		if (!LayerName.IsKeyboardFocused) LayerName.Text = layer?.Name ?? "";
		LayerOpacity.Value = (layer?.Opacity ?? 1f) * 100;
		BuildPaintColourSettings(force: false);   // a colour layer: its settings under the list

		_syncingUi = wasSyncing;
	}

	private static Color ToWpf(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
	private static string Hex(SKColor c) => $"#{c.Alpha:X2}{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
}