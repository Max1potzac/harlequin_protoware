using Animator.Core.Import;
using PixelRect = Animator.Core.Utilities.PixelRect;
using Animator.Viewer.Import;
using Animator.Viewer.Painting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop.Import;

// File > Import a Photoshop file: which layers come in, and whether as a drawn object or a rigged
// one, with a suggested skeleton whose every choice can be changed (and is shown on the picture).
// The same as the Windows editor's dialog; ShowDialog<bool>: true, import as the plan now says.
public sealed class PsdImportWindow : Window
{
	private sealed record Choice(int Layer, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

	private readonly PsdImportPlan _plan;
	private RigSuggestion? _rig;                 // the suggestion as things stand (null = no rig)
	private int _picked = -1;                    // the picked layer (index in the plan's parts)
	private readonly Dictionary<int, (CheckBox Tick, TextBlock Name, TextBlock Details)> _rows = new();
	private bool _syncing;

	// The picture: canvas pixel p shows at p × _zoom + _pan (in the view's units)
	private float _zoom = 1f;
	private Vector2 _pan, _lastView;
	private bool _fitted, _panning;

	internal readonly ListBox LayerList = new() { MaxHeight = 300 };
	internal readonly CheckBox RigBox = new() { Content = "Suggest a rig: bones where the layers overlap" };
	private readonly StackPanel _rigFields = new() { Spacing = 6 };
	private readonly ComboBox _rootBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly TextBlock _layerTitle = new() { FontWeight = FontWeight.SemiBold };
	private readonly StackPanel _layerFields = new() { Spacing = 4 };
	private readonly RadioButton _ownBone = new() { Content = "Its own bone (a part that bends or turns)", GroupName = "role" };
	private readonly RadioButton _onBone = new() { Content = "No bone: it rides on another layer's bone", GroupName = "role" };
	private readonly TextBlock _targetLabel = new() { Text = "Hangs from", VerticalAlignment = VerticalAlignment.Center, Width = 80 };
	private readonly ComboBox _targetBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox ScaleBox = new() { Text = "100", Width = 64 };
	internal readonly TextBlock Summary = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _error = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly SkiaView Preview = new();

	public PsdImportWindow(PsdImportPlan plan)
	{
		_plan = plan;
		Title = "Import a Photoshop file";
		Width = 1100;
		Height = 720;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		RigBox.IsChecked = plan.SuggestRig;
		plan.SuggestRoles();   // ready for when a rig is asked for

		Content = BuildLayout();
		KeyDown += (_, e) =>
		{
			if (e.Key == Key.Home && FocusManager?.GetFocusedElement() is not TextBox) { _fitted = false; Preview.InvalidateVisual(); e.Handled = true; }
			else if (e.Key == Key.Escape) Close(false);
		};
		Refresh();
	}

	private Control BuildLayout()
	{
		static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 8, 0, 2) };
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };

		var settings = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
		settings.Children.Add(new TextBlock { Text = $"{_plan.Name}   ({_plan.Width} × {_plan.Height} pixels, {_plan.Parts.Count} layer(s))", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
		if (_plan.Notes.Count > 0) settings.Children.Add(new TextBlock { Text = string.Join("\n", _plan.Notes), Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap });

		settings.Children.Add(Heading("Layers"));
		settings.Children.Add(Note("Ticked layers come in as parts, in the file's order (the top layer is at the top here). Click a layer, here or on the picture, to pick it."));
		var all = new Button { Content = "Tick them all" };
		all.Click += (_, _) => { foreach (var p in _plan.Parts) p.Include = true; Refresh(); };
		var visible = new Button { Content = "Only the visible ones" };
		visible.Click += (_, _) => { foreach (var p in _plan.Parts) p.Include = !p.HiddenInFile; Refresh(); };
		settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { all, visible } });

		// Top to bottom, as Photoshop lists them
		var items = new List<ListBoxItem>();
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
		{
			int index = i;
			var tick = new CheckBox { IsChecked = _plan.Parts[i].Include, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0) };
			tick.IsCheckedChanged += (_, _) =>
			{
				if (_syncing) return;
				_plan.Parts[index].Include = tick.IsChecked == true;
				Refresh();
			};
			var name = new TextBlock { TextWrapping = TextWrapping.Wrap };
			var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11 };
			var row = new DockPanel();
			DockPanel.SetDock(tick, Dock.Left);
			row.Children.Add(tick);
			row.Children.Add(new StackPanel { Children = { name, details } });
			items.Add(new ListBoxItem { Content = row, Tag = i, Padding = new Thickness(4, 2) });
			_rows[i] = (tick, name, details);
		}
		LayerList.ItemsSource = items;
		LayerList.SelectionChanged += (_, _) =>
		{
			if (_syncing) return;
			_picked = LayerList.SelectedItem is ListBoxItem { Tag: int i } ? i : -1;
			ShowPicked();
			Preview.InvalidateVisual();
		};
		settings.Children.Add(LayerList);

		// The rig
		settings.Children.Add(Heading("Rig"));
		RigBox.IsCheckedChanged += (_, _) => { if (!_syncing) { _plan.SuggestRig = RigBox.IsChecked == true; Refresh(); } };
		settings.Children.Add(RigBox);
		_rigFields.Children.Add(Note("Each layer gets a bone that turns where it overlaps the layer it hangs from. Details lying on another layer (eyes, a belt) ride on that layer's bone instead. Change any of it below; the picture shows the result."));
		_rigFields.Children.Add(Row("Root", _rootBox));
		_rootBox.SelectionChanged += (_, _) =>
		{
			if (_syncing || _rootBox.SelectedItem is not Choice c) return;
			_plan.Root = c.Layer;
			Refresh();
		};
		_layerFields.Children.Add(_ownBone);
		_layerFields.Children.Add(_onBone);
		var target = new DockPanel();
		DockPanel.SetDock(_targetLabel, Dock.Left);
		target.Children.Add(_targetLabel);
		target.Children.Add(_targetBox);
		_layerFields.Children.Add(target);
		_ownBone.IsCheckedChanged += (_, _) => RoleChanged();
		_onBone.IsCheckedChanged += (_, _) => RoleChanged();
		_targetBox.SelectionChanged += (_, _) =>
		{
			if (_syncing || _picked < 0 || _targetBox.SelectedItem is not Choice c) return;
			_plan.Parts[_picked].Target = c.Layer;
			Refresh();
		};
		_rigFields.Children.Add(_layerTitle);
		_rigFields.Children.Add(_layerFields);
		var again = new Button { Content = "Suggest the roles again" };
		ToolTip.SetTip(again, "Forgets the changes above and works out each layer's role from the shapes again");
		again.Click += (_, _) => { _plan.SuggestRoles(); Refresh(); };
		_rigFields.Children.Add(again);
		settings.Children.Add(_rigFields);

		// Size
		settings.Children.Add(Heading("Size"));
		ScaleBox.LostFocus += (_, _) => { if (ReadScale() is { } s) _plan.Scale = s; RefreshSummary(); };
		ScaleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (ReadScale() is { } s) _plan.Scale = s; RefreshSummary(); e.Handled = true; } };
		settings.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			Children = { ScaleBox, new TextBlock { Text = "% of the file's size", VerticalAlignment = VerticalAlignment.Center } }
		});

		// Import / Cancel, under the summary
		var import = new Button { Content = "Import", MinWidth = 90, IsDefault = true };
		import.Click += (_, _) => TryImport();
		var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
		cancel.Click += (_, _) => Close(false);
		var bottom = new DockPanel { Margin = new Thickness(12, 8) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, cancel } };
		DockPanel.SetDock(buttons, Dock.Right);
		bottom.Children.Add(buttons);
		bottom.Children.Add(new StackPanel { Children = { Summary, _error } });

		// The picture
		Preview.Draw = DrawPreview;
		Preview.PointerPressed += Preview_PointerPressed;
		Preview.PointerMoved += (_, e) =>
		{
			var p = Input.At(e, Preview);
			if (_panning) { _pan += p - _lastView; Preview.InvalidateVisual(); }
			_lastView = p;
		};
		Preview.PointerReleased += (_, e) => { if (_panning) { _panning = false; e.Pointer.Capture(null); } };
		Preview.PointerWheelChanged += (_, e) =>
		{
			var p = Input.At(e, Preview);
			var under = ToCanvas(p);
			_zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25f : 0.8f), 0.01f, 40f);
			_pan = p - under * _zoom;   // the pixel under the mouse stays there
			Preview.InvalidateVisual();
			e.Handled = true;
		};
		var picture = new DockPanel();
		var help = new TextBlock { Text = "Click: pick a layer   Wheel: zoom   Middle drag: move around   Home: fit", Foreground = Brushes.Gray, Margin = new Thickness(8, 4) };
		DockPanel.SetDock(help, Dock.Bottom);
		picture.Children.Add(help);
		picture.Children.Add(Preview);

		var main = new Grid { ColumnDefinitions = new ColumnDefinitions("380,4,*") };
		main.Children.Add(new ScrollViewer { Content = settings });
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

	private static Control Row(string label, Control control)
	{
		var row = new DockPanel();
		var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 80 };
		DockPanel.SetDock(text, Dock.Left);
		row.Children.Add(text);
		row.Children.Add(control);
		return row;
	}

	// ======================= the layers and the rig =======================

	private void Pick(int layer)
	{
		_picked = layer;
		_syncing = true;
		var item = (LayerList.ItemsSource as List<ListBoxItem>)?.FirstOrDefault(it => it.Tag is int i && i == layer);
		LayerList.SelectedItem = item;
		if (item != null) LayerList.ScrollIntoView(item);
		_syncing = false;
		ShowPicked();
		Preview.InvalidateVisual();
	}

	internal void PickLayer(int layer) => Pick(layer);

	private void RoleChanged()
	{
		if (_syncing || _picked < 0) return;
		var part = _plan.Parts[_picked];
		var role = _onBone.IsChecked == true ? RigRole.OnBone : RigRole.Bone;
		if (part.Role == role) return;
		part.Role = role;
		part.Target = -1;   // what it hung from isn't what it should ride on, and back
		Refresh();
	}

	// The name of a layer's bone parent (a bone) or of the layer it rides on, in the suggestion
	private string? SuggestedTarget(int layer)
	{
		if (_rig == null || _rig.BoneOf[layer] < 0) return null;
		var bone = _rig.Bones[_rig.BoneOf[layer]];
		if (bone.Layer != layer) return _plan.Parts[bone.Layer].Name;   // rides on another layer's bone
		return bone.Parent >= 0 ? _plan.Parts[_rig.Bones[bone.Parent].Layer].Name : null;   // null: the root
	}

	// ======================= size and import =======================

	private float? ReadScale()
	{
		if (float.TryParse((ScaleBox.Text ?? "").Trim().TrimEnd('%'), out float percent) && percent >= 1 && percent <= 400)
		{
			_error.Text = "";
			return percent / 100f;
		}
		_error.Text = "The size is a percentage from 1 to 400.";
		return null;
	}

	internal void TryImport()
	{
		if (ReadScale() is not { } scale) return;
		_plan.Scale = scale;
		if (!_plan.Parts.Any(p => p.Include))
		{
			_error.Text = "Tick at least one layer to bring in.";
			return;
		}
		Close(true);
	}

	// ======================= refreshing =======================

	private void Refresh()
	{
		_rig = _plan.SuggestRig && _plan.Parts.Any(p => p.Include) ? _plan.Suggest() : null;
		_rigFields.IsEnabled = _plan.SuggestRig;

		_syncing = true;
		RigBox.IsChecked = _plan.SuggestRig;
		foreach (var (i, (tick, name, details)) in _rows)
		{
			var part = _plan.Parts[i];
			tick.IsChecked = part.Include;
			name.Text = part.Name;
			name.FontWeight = part.Include ? FontWeight.SemiBold : FontWeight.Normal;
			details.Text = string.Join(" · ", Details(i));
			details.IsVisible = details.Text.Length > 0;
		}

		// The root: worked out, or any layer with a bone of its own
		var roots = new List<Choice> { new(-1, _rig != null && _rig.RootLayer >= 0 ? $"Worked out ({_plan.Parts[_rig.RootLayer].Name})" : "Worked out") };
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (_plan.Parts[i].Include && _plan.Parts[i].Role == RigRole.Bone) roots.Add(new Choice(i, _plan.Parts[i].Name));
		_rootBox.ItemsSource = roots;
		_rootBox.SelectedItem = roots.FirstOrDefault(c => c.Layer == _plan.Root) ?? roots[0];
		_syncing = false;

		ShowPicked();
		RefreshSummary();
		Preview.InvalidateVisual();
	}

	private IEnumerable<string> Details(int i)
	{
		var part = _plan.Parts[i];
		if (part.Group.Length > 0) yield return $"in {part.Group}";
		if (part.HiddenInFile) yield return "hidden in the file";
		if (_rig == null || !part.Include) yield break;
		string? target = SuggestedTarget(i);
		if (_rig.RootLayer == i) yield return "root bone";
		else if (_rig.BoneOf[i] >= 0 && _rig.Bones[_rig.BoneOf[i]].Layer == i) yield return $"bone from {target}";
		else if (target != null) yield return $"on {target}'s bone";
	}

	private void ShowPicked()
	{
		_syncing = true;
		try
		{
			var part = _picked >= 0 ? _plan.Parts[_picked] : null;
			_layerFields.IsEnabled = part != null && part.Include && _rig != null;
			if (part == null)
			{
				_layerTitle.Text = "Pick a layer to change what it gets";
				_ownBone.IsChecked = _onBone.IsChecked = false;
				_targetBox.ItemsSource = null;
				_targetLabel.Text = "Hangs from";
				return;
			}

			_layerTitle.Text = part.Include ? part.Name : $"{part.Name} (not ticked)";
			_ownBone.IsChecked = part.Role == RigRole.Bone;
			_onBone.IsChecked = part.Role == RigRole.OnBone;
			_targetLabel.Text = part.Role == RigRole.Bone ? "Hangs from" : "Rides on";

			bool isRoot = _rig != null && _rig.RootLayer == _picked;
			string worked = isRoot ? "Nothing: it's the root"
				: part.Target < 0 && SuggestedTarget(_picked) is { } suggested ? $"Worked out ({suggested})"
				: "Worked out from the overlaps";
			var choices = new List<Choice> { new(-1, worked) };
			for (int i = _plan.Parts.Count - 1; i >= 0; i--)
				if (i != _picked && _plan.Parts[i].Include && (part.Role == RigRole.OnBone || _plan.Parts[i].Role == RigRole.Bone))
					choices.Add(new Choice(i, _plan.Parts[i].Name));
			_targetBox.ItemsSource = choices;
			_targetBox.SelectedItem = choices.FirstOrDefault(c => c.Layer == part.Target) ?? choices[0];
			_targetBox.IsEnabled = !isRoot;
		}
		finally
		{
			_syncing = false;
		}
	}

	private void RefreshSummary()
	{
		int parts = _plan.Parts.Count(p => p.Include);
		float scale = _plan.Scale;
		string size = Math.Abs(scale - 1f) < 0.001f ? "" : $", at {scale * 100:0.#}% of the size";
		if (parts == 0) Summary.Text = "Nothing is ticked.";
		else if (_rig == null) Summary.Text = $"Brings in a drawn object \"{_plan.Name}\" with {parts} part(s){size}.";
		else
		{
			int riding = Enumerable.Range(0, _plan.Parts.Count)
				.Count(i => _plan.Parts[i].Include && _rig.BoneOf[i] >= 0 && _rig.Bones[_rig.BoneOf[i]].Layer != i);
			Summary.Text = $"Brings in a rigged object \"{_plan.Name}\": {_rig.Bones.Count} bone(s) and {parts} part(s), " +
				$"{riding} of them riding on other layers' bones{size}.";
		}
	}

	// ======================= the picture =======================

	private Vector2 ToView(Vector2 canvas) => canvas * _zoom + _pan;
	private Vector2 ToCanvas(Vector2 view) => (view - _pan) / _zoom;
	private SKRect ToView(PixelRect r) => new(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Right * _zoom + _pan.X, r.Bottom * _zoom + _pan.Y);

	private void Fit(Vector2 view)
	{
		if (view.X <= 0 || view.Y <= 0) return;
		_zoom = Math.Clamp(MathF.Min(view.X / _plan.Width, view.Y / _plan.Height) * 0.92f, 0.01f, 8f);
		_pan = new Vector2((view.X - _plan.Width * _zoom) / 2, (view.Y - _plan.Height * _zoom) / 2);
		_fitted = true;
	}

	private static SKBlendMode SkiaBlend(BlendMode blend) => blend switch
	{
		BlendMode.Add => SKBlendMode.Plus,
		BlendMode.Multiply => SKBlendMode.Multiply,
		BlendMode.Screen => SKBlendMode.Screen,
		_ => SKBlendMode.SrcOver
	};

	private void DrawPreview(SKCanvas canvas, Vector2 view, float dpi)
	{
		canvas.Clear(new SKColor(40, 44, 52));
		canvas.Scale(dpi);
		if (!_fitted) Fit(view);

		// The ticked layers, as they'll look
		Checkerboard.Draw(canvas, ToView(new PixelRect(0, 0, _plan.Width, _plan.Height)), view.X, view.Y);
		canvas.Save();
		canvas.Translate(_pan.X, _pan.Y);
		canvas.Scale(_zoom);
		using (var paint = new SKPaint())
			foreach (var part in _plan.Parts.Where(p => p.Include))
			{
				paint.BlendMode = SkiaBlend(part.Blend);
				paint.Color = SKColors.White.WithAlpha((byte)MathF.Round(part.Opacity * 255));
				canvas.DrawBitmap(part.Pixels, part.Bounds.X, part.Bounds.Y, _zoom >= 1 ? AssetStore.Nearest : Smooth, paint);
			}
		canvas.Restore();

		using var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(255, 200, 40), IsAntialias = true };
		using var dash = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0);
		if (_picked >= 0)
		{
			outline.PathEffect = dash;
			canvas.DrawRect(ToView(_plan.Parts[_picked].Bounds), outline);
			outline.PathEffect = null;
		}
		if (_rig != null) RigPicture.Draw(canvas, _rig, ToView, _picked, i => _plan.Parts[i].Name, i => _plan.Parts[i].Middle);
	}

	private void Preview_PointerPressed(object? sender, PointerPressedEventArgs e)
	{
		var point = e.GetCurrentPoint(Preview);
		var p = Input.At(e, Preview);
		_lastView = p;
		if (point.Properties.IsMiddleButtonPressed)
		{
			_panning = true;
			e.Pointer.Capture(Preview);
			return;
		}
		if (!point.Properties.IsLeftButtonPressed) return;

		// The topmost ticked layer that shows there (else the topmost unticked one)
		var at = ToCanvas(p);
		int x = (int)MathF.Floor(at.X), y = (int)MathF.Floor(at.Y);
		int Hit(bool ticked)
		{
			for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			{
				var part = _plan.Parts[i];
				if (part.Include != ticked || !part.Bounds.Contains(x, y)) continue;
				if (part.Pixels.GetPixel(x - part.Bounds.X, y - part.Bounds.Y).Alpha > 0) return i;
			}
			return -1;
		}
		int hit = Hit(true);
		Pick(hit >= 0 ? hit : Hit(false));
	}
}
