using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Animator.Viewer.Dialogs;

// File > Import a Photoshop file: which layers come in, and whether as a drawn object or a rigged
// one, with a suggested skeleton whose every choice can be changed (and is shown on the picture)
public partial class PsdImportDialog : Window
{
	private sealed record Choice(int Layer, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

	private readonly PsdImportPlan _plan;
	private RigSuggestion? _rig;                 // the suggestion as things stand (null = no rig)
	private int _picked = -1;                    // the picked layer (index in the plan's parts)
	private readonly Dictionary<int, (CheckBox Tick, TextBlock Text)> _rows = new();
	private bool _syncing;

	// The picture view: canvas pixel p shows at p × _zoom + _pan (in the view's units)
	private float _zoom = 1f;
	private Vector2 _pan, _lastView;
	private bool _fitted, _panning;

	public PsdImportDialog(PsdImportPlan plan)
	{
		InitializeComponent();
		_plan = plan;
		FileTitle.Text = $"{plan.Name}   ({plan.Width} × {plan.Height} pixels, {plan.Parts.Count} layer(s))";
		NotesText.Text = string.Join("\n", plan.Notes);
		NotesText.Visibility = plan.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		ScaleBox.Text = "100";
		RigBox.IsChecked = plan.SuggestRig;
		plan.SuggestRoles();   // ready for when a rig is asked for

		// Top to bottom, as Photoshop lists them
		for (int i = plan.Parts.Count - 1; i >= 0; i--)
		{
			var tick = new CheckBox { IsChecked = plan.Parts[i].Include, Tag = i, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
			tick.Click += Tick_Click;
			var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
			var row = new DockPanel();
			DockPanel.SetDock(tick, Dock.Left);
			row.Children.Add(tick);
			row.Children.Add(text);
			LayerList.Items.Add(new ListBoxItem { Content = row, Tag = i });
			_rows[i] = (tick, text);
		}
		Refresh();
	}

	// ======================= the layers =======================

	private void Tick_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox { Tag: int i } tick) return;
		_plan.Parts[i].Include = tick.IsChecked == true;
		Refresh();
	}

	private void TickAll_Click(object sender, RoutedEventArgs e)
	{
		foreach (var part in _plan.Parts) part.Include = true;
		Refresh();
	}

	private void TickVisible_Click(object sender, RoutedEventArgs e)
	{
		foreach (var part in _plan.Parts) part.Include = !part.HiddenInFile;
		Refresh();
	}

	private void LayerList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing) return;
		_picked = LayerList.SelectedItem is ListBoxItem { Tag: int i } ? i : -1;
		ShowPicked();
		PreviewView.InvalidateVisual();
	}

	private void Pick(int layer)
	{
		_picked = layer;
		_syncing = true;
		var item = LayerList.Items.Cast<ListBoxItem>().FirstOrDefault(it => it.Tag is int i && i == layer);
		LayerList.SelectedItem = item;
		if (item != null) LayerList.ScrollIntoView(item);
		_syncing = false;
		ShowPicked();
		PreviewView.InvalidateVisual();
	}

	// ======================= the rig =======================

	private void Rig_Click(object sender, RoutedEventArgs e)
	{
		_plan.SuggestRig = RigBox.IsChecked == true;
		Refresh();
	}

	private void SuggestRoles_Click(object sender, RoutedEventArgs e)
	{
		_plan.SuggestRoles();
		Refresh();
	}

	private void Root_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || RootBox.SelectedItem is not Choice choice) return;
		_plan.Root = choice.Layer;
		Refresh();
	}

	private void Role_Changed(object sender, RoutedEventArgs e)
	{
		if (_syncing || _picked < 0) return;
		var part = _plan.Parts[_picked];
		var role = OnBoneChoice.IsChecked == true ? RigRole.OnBone : RigRole.Bone;
		if (part.Role == role) return;
		part.Role = role;
		part.Target = -1;   // what it hung from isn't what it should ride on, and back
		Refresh();
	}

	private void Target_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || _picked < 0 || TargetBox.SelectedItem is not Choice choice) return;
		_plan.Parts[_picked].Target = choice.Layer;
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

	private void Scale_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		Scale_Changed(sender, e);
		e.Handled = true;   // not the Import button
	}

	private void Scale_Changed(object sender, RoutedEventArgs e)
	{
		if (ReadScale() is { } scale) _plan.Scale = scale;
		RefreshSummary();
	}

	private float? ReadScale()
	{
		if (float.TryParse(ScaleBox.Text.Trim().TrimEnd('%'), out float percent) && percent >= 1 && percent <= 400)
		{
			ErrorText.Text = "";
			return percent / 100f;
		}
		ErrorText.Text = "The size is a percentage from 1 to 400.";
		return null;
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		if (ReadScale() is not { } scale) return;
		_plan.Scale = scale;
		if (!_plan.Parts.Any(p => p.Include))
		{
			ErrorText.Text = "Tick at least one layer to bring in.";
			return;
		}
		DialogResult = true;
	}

	// ======================= refreshing =======================

	private void Refresh()
	{
		_rig = _plan.SuggestRig && _plan.Parts.Any(p => p.Include) ? _plan.Suggest() : null;
		RigFields.IsEnabled = _plan.SuggestRig;

		_syncing = true;
		foreach (var (i, (tick, text)) in _rows)
		{
			var part = _plan.Parts[i];
			tick.IsChecked = part.Include;
			text.Inlines.Clear();
			text.Inlines.Add(new Run(part.Name) { FontWeight = part.Include ? FontWeights.SemiBold : FontWeights.Normal });
			string details = string.Join(" · ", Details(i));
			if (details.Length > 0) text.Inlines.Add(new Run("   " + details) { Foreground = Brushes.Gray });
		}

		// The root: worked out, or any layer with a bone of its own
		var roots = new List<Choice>
		{
			new(-1, _rig != null && _rig.RootLayer >= 0 ? $"Worked out ({_plan.Parts[_rig.RootLayer].Name})" : "Worked out")
		};
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (_plan.Parts[i].Include && _plan.Parts[i].Role == RigRole.Bone)
				roots.Add(new Choice(i, _plan.Parts[i].Name));
		RootBox.ItemsSource = roots;
		RootBox.SelectedItem = roots.FirstOrDefault(c => c.Layer == _plan.Root) ?? roots[0];
		_syncing = false;

		ShowPicked();
		RefreshSummary();
		PreviewView.InvalidateVisual();
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
		var part = _picked >= 0 ? _plan.Parts[_picked] : null;
		LayerFields.IsEnabled = part != null && part.Include && _rig != null;
		if (part == null)
		{
			LayerTitle.Text = "Pick a layer to change what it gets";
			OwnBoneChoice.IsChecked = OnBoneChoice.IsChecked = false;
			TargetBox.ItemsSource = null;
			TargetLabel.Text = "Hangs from";
			_syncing = false;
			return;
		}

		LayerTitle.Text = part.Include ? part.Name : $"{part.Name} (not ticked)";
		OwnBoneChoice.IsChecked = part.Role == RigRole.Bone;
		OnBoneChoice.IsChecked = part.Role == RigRole.OnBone;
		TargetLabel.Text = part.Role == RigRole.Bone ? "Hangs from" : "Rides on";

		bool isRoot = _rig != null && _rig.RootLayer == _picked;
		string worked = isRoot ? "Nothing: it's the root"
			: part.Target < 0 && SuggestedTarget(_picked) is { } suggested ? $"Worked out ({suggested})"
			: "Worked out from the overlaps";
		var choices = new List<Choice> { new(-1, worked) };
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (i != _picked && _plan.Parts[i].Include && (part.Role == RigRole.OnBone || _plan.Parts[i].Role == RigRole.Bone))
				choices.Add(new Choice(i, _plan.Parts[i].Name));
		TargetBox.ItemsSource = choices;
		TargetBox.SelectedItem = choices.FirstOrDefault(c => c.Layer == part.Target) ?? choices[0];
		TargetBox.IsEnabled = !isRoot;
		_syncing = false;
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

	private SKRect ToView(PixelRect r) =>
		new(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Right * _zoom + _pan.X, r.Bottom * _zoom + _pan.Y);

	private void Fit()
	{
		float w = (float)PreviewView.ActualWidth, h = (float)PreviewView.ActualHeight;
		if (w <= 0 || h <= 0) return;
		_zoom = Math.Clamp(MathF.Min(w / _plan.Width, h / _plan.Height) * 0.92f, 0.01f, 8f);
		_pan = new Vector2((w - _plan.Width * _zoom) / 2, (h - _plan.Height * _zoom) / 2);
		_fitted = true;
	}

	private static SKBlendMode SkiaBlend(BlendMode blend) => blend switch
	{
		BlendMode.Add => SKBlendMode.Plus,
		BlendMode.Multiply => SKBlendMode.Multiply,
		BlendMode.Screen => SKBlendMode.Screen,
		_ => SKBlendMode.SrcOver
	};

	private void PreviewView_Paint(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(40, 44, 52));
		if (PreviewView.ActualWidth <= 0) return;
		canvas.Scale((float)(e.Info.Width / PreviewView.ActualWidth));   // DPI
		if (!_fitted) Fit();
		float viewW = (float)PreviewView.ActualWidth, viewH = (float)PreviewView.ActualHeight;

		// The ticked layers, as they'll look
		Checkerboard.Draw(canvas, ToView(new PixelRect(0, 0, _plan.Width, _plan.Height)), viewW, viewH);
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
		if (_rig != null) DrawRig(canvas, _rig);
	}

	// Each bone as a long diamond from its joint (a dot: pink when it sits on its parent's end) to
	// its tip; the picked layer's bone stands out, and a part riding on a bone points at it
	private void DrawRig(SKCanvas canvas, RigSuggestion rig)
	{
		using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(30, 30, 30, 220), IsAntialias = true };
		using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var font = new SKFont { Size = 12 };
		using var label = new SKPaint { IsAntialias = true, Color = SKColors.White };
		using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 200) };
		int pickedBone = _picked >= 0 && _picked < rig.BoneOf.Length ? rig.BoneOf[_picked] : -1;

		if (pickedBone >= 0 && rig.Bones[pickedBone].Layer != _picked)
		{
			// The picked part rides on this bone
			using var link = new SKPaint
			{
				Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 200, 40), IsAntialias = true,
				PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0)
			};
			var from = ToView(_plan.Parts[_picked].Middle);
			var to = ToView((rig.Bones[pickedBone].Head + rig.Bones[pickedBone].Tip) / 2);
			canvas.DrawLine(from.X, from.Y, to.X, to.Y, link);
		}

		for (int b = 0; b < rig.Bones.Count; b++)
		{
			var bone = rig.Bones[b];
			Vector2 head = ToView(bone.Head), tip = ToView(bone.Tip);
			var along = tip - head;
			float length = along.Length();
			bool picked = b == pickedBone;
			if (length > 1)
			{
				var dir = along / length;
				var across = new Vector2(-dir.Y, dir.X) * Math.Clamp(length * 0.09f, 3f, 12f);
				var waist = head + dir * length * 0.18f;
				using var path = new SKPath();
				path.MoveTo(head.X, head.Y);
				path.LineTo(waist.X + across.X, waist.Y + across.Y);
				path.LineTo(tip.X, tip.Y);
				path.LineTo(waist.X - across.X, waist.Y - across.Y);
				path.Close();
				fill.Color = picked ? new SKColor(255, 140, 30, 230) : bone.Parent < 0 ? new SKColor(120, 230, 120, 210) : new SKColor(255, 230, 80, 200);
				canvas.DrawPath(path, fill);
				canvas.DrawPath(path, edge);
			}
			dot.Color = bone.Connected ? new SKColor(255, 90, 200) : new SKColor(60, 200, 255);
			canvas.DrawCircle(head.X, head.Y, picked ? 5.5f : 4f, dot);
			canvas.DrawCircle(head.X, head.Y, picked ? 5.5f : 4f, edge);

			if (picked)
			{
				string name = _plan.Parts[bone.Layer].Name;
				var at = (head + tip) / 2 + new Vector2(8, -6);
				canvas.DrawText(name, at.X + 1, at.Y + 1, SKTextAlign.Left, font, shadow);
				canvas.DrawText(name, at.X, at.Y, SKTextAlign.Left, font, label);
			}
		}
	}

	private static Vector2 PosIn(IInputElement element, MouseEventArgs e)
	{
		var p = e.GetPosition(element);
		return new Vector2((float)p.X, (float)p.Y);
	}

	private void PreviewView_MouseDown(object sender, MouseButtonEventArgs e)
	{
		PreviewView.Focus();
		var p = PosIn(PreviewView, e);
		_lastView = p;
		if (e.ChangedButton == MouseButton.Middle)
		{
			_panning = true;
			PreviewView.CaptureMouse();
			return;
		}
		if (e.ChangedButton != MouseButton.Left) return;

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

	private void PreviewView_MouseMove(object sender, MouseEventArgs e)
	{
		var p = PosIn(PreviewView, e);
		if (_panning)
		{
			_pan += p - _lastView;
			PreviewView.InvalidateVisual();
		}
		_lastView = p;
	}

	private void PreviewView_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_panning) return;
		_panning = false;
		PreviewView.ReleaseMouseCapture();
	}

	private void PreviewView_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		var p = PosIn(PreviewView, e);
		var under = ToCanvas(p);
		_zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25f : 0.8f), 0.01f, 40f);
		_pan = p - under * _zoom;   // the pixel under the mouse stays there
		PreviewView.InvalidateVisual();
		e.Handled = true;
	}

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is TextBox || e.Key != Key.Home) return;
		_fitted = false;
		PreviewView.InvalidateVisual();
		e.Handled = true;
	}
}
