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

	private readonly RectSelectTool _select = new();
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

		SelectTool("Brush");
	}

	// ---------- open / close ----------

	private bool TryOpenPaintAt(Vector2 screen)
	{
		if (_renderer.Pick(_skeleton, _camera.ScreenToWorld(screen)) is not { } hit) return false;
		_state.Selected = hit.Slot.Bone;
		OpenPaintEditor(hit.Asset);
		return true;
	}

	private void OpenPaintEditor(Asset asset)
	{
		_paint.Tool.Deactivate(_paint);   // finish any half-placed figure first
		_paint.Open(asset);
		_renderer.Highlight = asset;
		PaintColumn.Width = new GridLength(1, GridUnitType.Star);
		PaintSplitter.Visibility = Visibility.Visible;
		PaintPanel.Visibility = Visibility.Visible;
		_paintCanvas.RequestFit();
	}

	private void ClosePaintEditor()
	{
		_paint.Tool.Deactivate(_paint);
		_paint.Close();
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
	}

	private void Tool_Click(object sender, RoutedEventArgs e) =>
		SelectTool((string)((FrameworkElement)sender).Tag);

	private bool HandlePaintKey(Key key)
	{
		if (_paint.Asset == null || !PaintView.IsMouseOver) return false;

		if (Keyboard.Modifiers == ModifierKeys.Control)
		{
			if (key == Key.A) { _paint.SelectAll(); return true; }
			if (key == Key.D) { _paint.Selection = null; return true; }
			return false;
		}
		if (Keyboard.Modifiers != ModifierKeys.None) return false;

		switch (key)
		{
			// Figure in progress: Enter finishes, Backspace removes a point, Esc cancels
			case Key.Enter when _figure.InProgress: _figure.Finish(_paint); return true;
			case Key.Back when _figure.InProgress: _figure.RemoveLastPoint(_paint); return true;
			case Key.Escape when _figure.InProgress: _figure.Cancel(_paint); return true;

			case Key.M: SelectTool("Select"); return true;
			case Key.V: SelectTool("Move"); return true;
			case Key.N: SelectTool("Pencil"); return true;
			case Key.B: SelectTool("Brush"); return true;
			case Key.X: SelectTool("Mixer"); return true;
			case Key.E: SelectTool("Eraser"); return true;
			case Key.I: SelectTool("Picker"); return true;
			case Key.G: SelectTool("Fill"); return true;
			case Key.D: SelectTool("Gradient"); return true;
			case Key.U: SelectTool("Figure"); return true;
			case Key.S: _paint.SwapColors(); return true;
			case Key.OemOpenBrackets: SetBrushSize(_paint.Size - 1); return true;
			case Key.OemCloseBrackets: SetBrushSize(_paint.Size + 1); return true;
			case Key.Delete when _paint.Selection != null: _paint.DeleteSelectedPixels(); return true;
			case Key.Escape when _paint.Selection != null: _paint.Selection = null; return true;
			default: return false;
		}
	}

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

	// Picking a shape also switches to the Figure tool
	private void FigureShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null) return;
		ReadFigureOptions();
		SelectTool("Figure");
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
	}
	private void FigureStyle_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingUi || _paint is null) return;
		ReadFigureOptions();
		SelectTool("Figure");
	}

	private void SwapColors_Click(object sender, RoutedEventArgs e) => _paint.SwapColors();

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

		PaintTitle.Text = $"Editing: {asset.Name}  ({asset.Width}×{asset.Height})";
		ColorSwatch.Background = new SolidColorBrush(ToWpf(_paint.Color));
		SecondarySwatch.Background = new SolidColorBrush(ToWpf(_paint.SecondaryColor));
		if (!ColorHex.IsKeyboardFocused) ColorHex.Text = Hex(_paint.Color);
		SizeSlider.Value = _paint.Size;
		SizeLabel.Text = _paint.Size.ToString();
		ToleranceSlider.Value = _paint.Tolerance;
		SampleAllBox.IsChecked = _paint.SampleAllLayers;
		GradientTransparentBox.IsChecked = _paint.GradientToTransparent;
		WetSlider.Value = _paint.MixWet * 100;
		LoadSlider.Value = _paint.MixLoad * 100;
		FigureShapeBox.SelectedItem = _paint.Figure;
		FigureStyleBox.SelectedItem = _paint.FigureStyle;
		FigureSmoothBox.IsChecked = _paint.FigureSmooth;
		SidesSlider.Value = _paint.PolygonSides;
		SidesLabel.Text = _paint.PolygonSides.ToString();

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
				Text = layer.Opacity < 1f ? $"{layer.Name}  ({layer.Opacity:P0})" : layer.Name,
				Foreground = Brushes.White
			});

			LayerList.Items.Add(row);
			if (layer == _paint.Layer) LayerList.SelectedItem = row;
		}

		_syncingUi = false;
		SyncLayerFields();
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

		_syncingUi = wasSyncing;
	}

	private static Color ToWpf(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
	private static string Hex(SKColor c) => $"#{c.Alpha:X2}{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
}