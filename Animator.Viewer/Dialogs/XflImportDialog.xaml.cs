using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Animator.Viewer.Dialogs;

// File > Import an Animate project: which timeline, which parts come in, and whether as a drawn
// object (its parts moving on their own) or a rigged one, with bones worked out from how the
// parts move, every choice of which can be changed (and is shown on the picture)
public partial class XflImportDialog : Window
{
	private sealed record Choice(int Part, string Label)
	{
		public override string ToString() => Label;
	}

	private readonly XflImportPlan _plan;
	private RigSuggestion? _rig;                 // the suggestion as things stand (null = no rig)
	private bool _rolesSuggested;
	private int _picked = -1;                    // the picked part (index in the plan's parts)
	private readonly Dictionary<int, (CheckBox Include, CheckBox InRig, TextBlock Text)> _rows = new();
	private bool _syncing;
	private int _frame;
	private readonly DispatcherTimer _player = new();

	// The picture view: timeline point p shows at p × _zoom + _pan (in the view's units)
	private float _zoom = 1f;
	private Vector2 _pan, _lastView;
	private bool _fitted, _panning;

	public XflImportDialog(XflImportPlan plan)
	{
		InitializeComponent();
		_plan = plan;
		ScaleBox.Text = "100";
		RigBox.IsChecked = plan.SuggestRig;
		SplitBox.IsChecked = plan.SplitAtLabels;
		_player.Tick += (_, _) => Step();
		_syncing = true;
		TimelineBox.ItemsSource = plan.Timelines;
		TimelineBox.SelectedItem = plan.Timeline;
		_syncing = false;
		Rebuild();
	}

	private void Window_Closed(object? sender, EventArgs e) => _player.Stop();

	// A new timeline: new parts
	private void Rebuild()
	{
		var doc = _plan.Document;
		FileTitle.Text = $"{_plan.Name}   ({doc.Width:0} × {doc.Height:0} pixels, {doc.FrameRate:0.##} frames a second)";
		var notes = _plan.Notes;
		NotesText.Text = string.Join("\n", notes);
		NotesText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		var labels = _plan.Data.Labels;
		TimelineText.Text = $"{_plan.Data.FrameCount} frame(s)" +
			(labels.Count > 0 ? $", labels: {string.Join(", ", labels.Select(l => l.Name))}" : ", no frame labels");
		SplitBox.IsEnabled = labels.Count > 0;

		_rows.Clear();
		PartList.Items.Clear();
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)   // the front one at the top
		{
			var include = new CheckBox { Tag = i, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Bring it in" };
			include.Click += Include_Click;
			var inRig = new CheckBox
			{
				Tag = i, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
				ToolTip = "Use it to work out the rig (untick skins and extras: they ride on the bone they move with)"
			};
			inRig.Click += InRig_Click;
			var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
			var row = new DockPanel();
			DockPanel.SetDock(include, Dock.Left);
			DockPanel.SetDock(inRig, Dock.Left);
			row.Children.Add(include);
			row.Children.Add(inRig);
			row.Children.Add(text);
			PartList.Items.Add(new ListBoxItem { Content = row, Tag = i });
			_rows[i] = (include, inRig, text);
		}
		_picked = -1;
		_rolesSuggested = false;
		FrameSlider.Maximum = Math.Max(0, _plan.Data.FrameCount - 1);
		_frame = _plan.SetupFrame;
		_syncing = true;
		FrameSlider.Value = _frame;
		_syncing = false;
		_fitted = false;
		Refresh();
	}

	// ======================= timeline, frames, animations =======================

	private void Timeline_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || TimelineBox.SelectedItem is not XflTimelineChoice choice || choice == _plan.Timeline) return;
		Busy(() => _plan.Choose(choice));
		Rebuild();
	}

	private void Split_Click(object sender, RoutedEventArgs e)
	{
		_plan.SplitAtLabels = SplitBox.IsChecked == true;
		RefreshSummary();
	}

	private void FrameSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncing) return;
		_frame = (int)Math.Round(FrameSlider.Value);
		ShowFrame();
	}

	private void ShowFrame()
	{
		FrameText.Text = $"frame {_frame + 1} of {_plan.Data.FrameCount}" + (LabelAt(_frame) is { } label ? $"  ({label})" : "");
		PreviewView.InvalidateVisual();
	}

	private string? LabelAt(int frame) =>
		_plan.Data.Labels.Where(l => l.Frame <= frame).Select(l => l.Name).LastOrDefault();

	private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

	private void TogglePlay()
	{
		if (_player.IsEnabled)
		{
			_player.Stop();
			PlayButton.Content = "▶";
			return;
		}
		_player.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _plan.Document.FrameRate));
		_player.Start();
		PlayButton.Content = "⏸";
	}

	private void Step()
	{
		_frame = (_frame + 1) % Math.Max(1, _plan.Data.FrameCount);
		_syncing = true;
		FrameSlider.Value = _frame;
		_syncing = false;
		ShowFrame();
	}

	private void UseFrame_Click(object sender, RoutedEventArgs e)
	{
		_plan.SetupFrame = _frame;
		_plan.Forget();
		_rolesSuggested = false;
		Refresh();
	}

	// ======================= the parts =======================

	private void Include_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox { Tag: int i } tick) return;
		_plan.Parts[i].Include = tick.IsChecked == true;
		Refresh();
	}

	private void InRig_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox { Tag: int i } tick) return;
		_plan.Parts[i].InRig = tick.IsChecked == true;
		Refresh();
	}

	private void TickAll_Click(object sender, RoutedEventArgs e)
	{
		foreach (var part in _plan.Parts) part.Include = true;
		Refresh();
	}

	private void TickVisible_Click(object sender, RoutedEventArgs e)
	{
		foreach (var part in _plan.Parts) part.Include = !_plan.Data.Tracks[part.Track].Hidden;
		Refresh();
	}

	private void PartList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing) return;
		_picked = PartList.SelectedItem is ListBoxItem { Tag: int i } ? i : -1;
		ShowPicked();
		PreviewView.InvalidateVisual();
	}

	private void Pick(int part)
	{
		_picked = part;
		_syncing = true;
		var item = PartList.Items.Cast<ListBoxItem>().FirstOrDefault(it => it.Tag is int i && i == part);
		PartList.SelectedItem = item;
		if (item != null) PartList.ScrollIntoView(item);
		_syncing = false;
		ShowPicked();
		PreviewView.InvalidateVisual();
	}

	// ======================= the rig =======================

	private void Rig_Click(object sender, RoutedEventArgs e)
	{
		_plan.SuggestRig = RigBox.IsChecked == true;
		if (_plan.SuggestRig && _frame != _plan.SetupFrame)
		{
			_frame = _plan.SetupFrame;   // the rig shows on the rest pose
			_syncing = true;
			FrameSlider.Value = _frame;
			_syncing = false;
		}
		Refresh();
	}

	private void SuggestRoles_Click(object sender, RoutedEventArgs e)
	{
		_rolesSuggested = false;
		Refresh();
	}

	private void Root_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || RootBox.SelectedItem is not Choice choice) return;
		_plan.Root = choice.Part;
		Refresh();
	}

	private void Role_Changed(object sender, RoutedEventArgs e)
	{
		if (_syncing || _picked < 0) return;
		var part = _plan.Parts[_picked];
		var role = OnBoneChoice.IsChecked == true ? RigRole.OnBone : RigRole.Bone;
		if (part.Role == role) return;
		part.Role = role;
		part.Target = -1;
		Refresh();
	}

	private void Target_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || _picked < 0 || TargetBox.SelectedItem is not Choice choice) return;
		_plan.Parts[_picked].Target = choice.Part;
		Refresh();
	}

	// The name of a part's bone parent, or of the part whose bone it rides on, in the suggestion
	private string? SuggestedTarget(int part)
	{
		if (_rig == null || _rig.BoneOf[part] < 0) return null;
		var bone = _rig.Bones[_rig.BoneOf[part]];
		if (bone.Layer != part) return _plan.Parts[bone.Layer].Name;
		return bone.Parent >= 0 ? _plan.Parts[_rig.Bones[bone.Parent].Layer].Name : null;
	}

	private void Busy(Action work)
	{
		Mouse.OverrideCursor = Cursors.Wait;
		try { work(); }
		finally { Mouse.OverrideCursor = null; }
	}

	// ======================= size and import =======================

	private void Scale_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		Scale_Changed(sender, e);
		e.Handled = true;
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
			ErrorText.Text = "Tick at least one part to bring in.";
			return;
		}
		_player.Stop();
		DialogResult = true;
	}

	// ======================= refreshing =======================

	private void Refresh()
	{
		_rig = null;
		if (_plan.SuggestRig && _plan.Parts.Any(p => p.Include))
			Busy(() =>
			{
				if (!_rolesSuggested)
				{
					_plan.SuggestRoles();
					_rolesSuggested = true;
				}
				_rig = _plan.Suggest();
			});
		RigFields.IsEnabled = _plan.SuggestRig;
		SetupText.Text = $"frame {_plan.SetupFrame + 1}";

		_syncing = true;
		foreach (var (i, (include, inRig, text)) in _rows)
		{
			var part = _plan.Parts[i];
			include.IsChecked = part.Include;
			inRig.IsChecked = part.InRig;
			inRig.IsEnabled = part.Include && _plan.SuggestRig;
			text.Inlines.Clear();
			text.Inlines.Add(new Run(part.Name) { FontWeight = part.Include ? FontWeights.SemiBold : FontWeights.Normal });
			string details = string.Join(" · ", Details(i));
			if (details.Length > 0) text.Inlines.Add(new Run("   " + details) { Foreground = Brushes.Gray });
		}

		var roots = new List<Choice>
		{
			new(-1, _rig != null && _rig.RootLayer >= 0 ? $"Worked out ({_plan.Parts[_rig.RootLayer].Name})" : "Worked out")
		};
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (_plan.Parts[i].Include && _plan.Parts[i].InRig && _plan.Parts[i].Role == RigRole.Bone)
				roots.Add(new Choice(i, _plan.Parts[i].Name));
		RootBox.ItemsSource = roots;
		RootBox.SelectedItem = roots.FirstOrDefault(c => c.Part == _plan.Root) ?? roots[0];
		_syncing = false;

		ShowPicked();
		RefreshSummary();
		ShowFrame();
	}

	private IEnumerable<string> Details(int i)
	{
		var part = _plan.Parts[i];
		var track = _plan.Data.Tracks[part.Track];
		int drawings = track.Frames.Where(f => f != null).Select(f => f!.Value.Drawing).Distinct().Count();
		if (drawings > 1) yield return $"{drawings} drawings";
		if (track.Hidden) yield return "on a hidden layer";
		if (track.Frames[_plan.SetupFrame] == null) yield return "not shown at the rest pose";
		if (_rig == null || !part.Include) yield break;
		if (!part.InRig || _rig.BoneOf[i] < 0) yield return "rides on the bone it moves with";
		else if (_rig.RootLayer == i) yield return "root bone";
		else if (_rig.Bones[_rig.BoneOf[i]].Layer == i) yield return $"bone from {SuggestedTarget(i)}";
		else if (SuggestedTarget(i) is { } target) yield return $"on {target}'s bone";
	}

	private void ShowPicked()
	{
		_syncing = true;
		var part = _picked >= 0 ? _plan.Parts[_picked] : null;
		PartFields.IsEnabled = part != null && part.Include && part.InRig && _rig != null;
		if (part == null)
		{
			PartTitle.Text = "Pick a part to change what it gets";
			OwnBoneChoice.IsChecked = OnBoneChoice.IsChecked = false;
			TargetBox.ItemsSource = null;
			TargetLabel.Text = "Hangs from";
			_syncing = false;
			return;
		}

		PartTitle.Text = !part.Include ? $"{part.Name} (not brought in)" : !part.InRig ? $"{part.Name} (left out of the rig)" : part.Name;
		OwnBoneChoice.IsChecked = part.Role == RigRole.Bone;
		OnBoneChoice.IsChecked = part.Role == RigRole.OnBone;
		TargetLabel.Text = part.Role == RigRole.Bone ? "Hangs from" : "Rides on";

		bool isRoot = _rig != null && _rig.RootLayer == _picked;
		string worked = isRoot ? "Nothing: it's the root"
			: part.Target < 0 && SuggestedTarget(_picked) is { } suggested ? $"Worked out ({suggested})"
			: "Worked out from the overlaps and the motion";
		var choices = new List<Choice> { new(-1, worked) };
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (i != _picked && _plan.Parts[i].Include && _plan.Parts[i].InRig && (part.Role == RigRole.OnBone || _plan.Parts[i].Role == RigRole.Bone))
				choices.Add(new Choice(i, _plan.Parts[i].Name));
		TargetBox.ItemsSource = choices;
		TargetBox.SelectedItem = choices.FirstOrDefault(c => c.Part == part.Target) ?? choices[0];
		TargetBox.IsEnabled = !isRoot;
		_syncing = false;
	}

	private void RefreshSummary()
	{
		int parts = _plan.Parts.Count(p => p.Include);
		var animations = _plan.Animations();
		string size = Math.Abs(_plan.Scale - 1f) < 0.001f ? "" : $", at {_plan.Scale * 100:0.#}% of the size";
		string anims = animations.Count == 1 ? "1 animation" : $"{animations.Count} animations";
		if (parts == 0) Summary.Text = "Nothing is ticked.";
		else if (_rig == null) Summary.Text = $"Brings in a drawn object \"{_plan.Name}\" with {parts} part(s) and {anims}{size}.";
		else
			Summary.Text = $"Brings in a rigged object \"{_plan.Name}\": {_rig.Bones.Count} bone(s), {parts} part(s) and {anims}{size}.";
	}

	// ======================= the picture =======================

	private Vector2 ToView(Vector2 point) => point * _zoom + _pan;
	private Vector2 ToTimeline(Vector2 view) => (view - _pan) / _zoom;

	// The rig's canvas → the timeline's space (the picture's)
	private Vector2 RigToTimeline(Vector2 canvas)
	{
		var inObject = _plan.CanvasToObject(canvas);
		return Matrix3x2.Invert(_plan.TimelineToObject, out var back) ? Vector2.Transform(inObject, back) : inObject;
	}

	// What to fit in the view: the parts at this frame, and a scene's stage
	private SKRect ContentBounds()
	{
		var bounds = _plan.Timeline.Scene ? new SKRect(0, 0, _plan.Document.Width, _plan.Document.Height) : SKRect.Empty;
		for (int i = 0; i < _plan.Parts.Count; i++)
			if (_plan.Placement(i, _frame) is { } p)
			{
				var box = SkeletonRenderer.ToSk(p.Matrix).MapRect(_plan.Painter.Bounds(p.Drawing));
				bounds = bounds.IsEmpty ? box : SKRect.Union(bounds, box);
			}
		return bounds.IsEmpty ? new SKRect(-100, -100, 100, 100) : bounds;
	}

	private void Fit()
	{
		float w = (float)PreviewView.ActualWidth, h = (float)PreviewView.ActualHeight;
		if (w <= 0 || h <= 0) return;
		var content = ContentBounds();
		_zoom = Math.Clamp(MathF.Min(w / MathF.Max(1, content.Width), h / MathF.Max(1, content.Height)) * 0.9f, 0.01f, 8f);
		_pan = new Vector2(w / 2, h / 2) - new Vector2(content.MidX, content.MidY) * _zoom;
		_fitted = true;
	}

	private void PreviewView_Paint(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(40, 44, 52));
		if (PreviewView.ActualWidth <= 0) return;
		canvas.Scale((float)(e.Info.Width / PreviewView.ActualWidth));   // DPI
		if (!_fitted) Fit();
		float viewW = (float)PreviewView.ActualWidth, viewH = (float)PreviewView.ActualHeight;

		if (_plan.Timeline.Scene)
		{
			var stage = new SKRect(_pan.X, _pan.Y, _pan.X + _plan.Document.Width * _zoom, _pan.Y + _plan.Document.Height * _zoom);
			Checkerboard.Draw(canvas, stage, viewW, viewH);
		}

		// The parts brought in, at this frame (the others faintly)
		canvas.Save();
		canvas.Translate(_pan.X, _pan.Y);
		canvas.Scale(_zoom);
		for (int i = 0; i < _plan.Parts.Count; i++)
		{
			if (_plan.Placement(i, _frame) is not { } p) continue;
			var color = p.Color;
			if (!_plan.Parts[i].Include) color = color with { Multiply = color.Multiply * new Vector4(1, 1, 1, 0.15f) };
			canvas.Save();
			var place = SkeletonRenderer.ToSk(p.Matrix);
			canvas.Concat(in place);
			SKPaint? layer = null;
			SKColorFilter? filter = null;
			if (!color.IsIdentity || p.Blend != BlendMode.Normal)
			{
				filter = color.IsIdentity ? null : XflPainter.ColorFilter(color);
				layer = new SKPaint { ColorFilter = filter, BlendMode = SkeletonRenderer.ToSk(p.Blend) };
				canvas.SaveLayer(layer);
			}
			_plan.Painter.Draw(canvas, p.Drawing);
			if (layer != null) canvas.Restore();
			canvas.Restore();
			layer?.Dispose();
			filter?.Dispose();
		}
		canvas.Restore();

		if (_picked >= 0 && _plan.Placement(_picked, _frame) is { } picked)
		{
			using var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(255, 200, 40), IsAntialias = true };
			using var dash = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0);
			outline.PathEffect = dash;
			var box = _plan.Painter.Bounds(picked.Drawing);
			var corners = new[] { new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top), new Vector2(box.Right, box.Bottom), new Vector2(box.Left, box.Bottom) }
				.Select(c => ToView(Vector2.Transform(c, picked.Matrix))).ToList();
			using var path = ShapePaths.Polyline(corners, closed: true);
			canvas.DrawPath(path, outline);
		}
		if (_rig != null && _frame == _plan.SetupFrame) DrawRig(canvas, _rig);
		ViewHint.Text = _rig != null && _frame != _plan.SetupFrame
			? $"The rig shows on the rest pose (frame {_plan.SetupFrame + 1})   Click: pick a part   Wheel: zoom   Space: play"
			: "Click: pick a part   Wheel: zoom   Middle drag: move around   Home: fit   Space: play";
	}

	// Each bone as a long diamond from its joint (a dot: pink when it sits on its parent's end) to
	// its tip; the picked part's bone stands out
	private void DrawRig(SKCanvas canvas, RigSuggestion rig)
	{
		using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(30, 30, 30, 220), IsAntialias = true };
		using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var font = new SKFont { Size = 12 };
		using var label = new SKPaint { IsAntialias = true, Color = SKColors.White };
		using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 200) };
		int pickedBone = _picked >= 0 && _picked < rig.BoneOf.Length ? rig.BoneOf[_picked] : -1;

		for (int b = 0; b < rig.Bones.Count; b++)
		{
			var bone = rig.Bones[b];
			Vector2 head = ToView(RigToTimeline(bone.Head)), tip = ToView(RigToTimeline(bone.Tip));
			var along = tip - head;
			float length = along.Length();
			bool picked = b == pickedBone;
			if (length > 1)
			{
				var dir = along / length;
				var across = new Vector2(-dir.Y, dir.X) * Math.Clamp(length * 0.09f, 3f, 12f);
				var waist = head + dir * length * 0.18f;
				using var path = ShapePaths.Polyline(new[] { head, waist + across, tip, waist - across }, closed: true);
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

		// The frontmost part brought in that's there (else the frontmost one left out)
		var at = ToTimeline(p);
		int Hit(bool included)
		{
			for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			{
				if (_plan.Parts[i].Include != included || _plan.Placement(i, _frame) is not { } placed) continue;
				if (!Matrix3x2.Invert(placed.Matrix, out var back)) continue;
				var local = Vector2.Transform(at, back);
				if (_plan.Painter.Bounds(placed.Drawing).Contains(local.X, local.Y)) return i;
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
		var under = ToTimeline(p);
		_zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25f : 0.8f), 0.01f, 40f);
		_pan = p - under * _zoom;
		PreviewView.InvalidateVisual();
		e.Handled = true;
	}

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is TextBox) return;
		if (e.Key == Key.Home)
		{
			_fitted = false;
			PreviewView.InvalidateVisual();
			e.Handled = true;
		}
		else if (e.Key == Key.Space && Keyboard.FocusedElement is not ButtonBase)
		{
			TogglePlay();
			e.Handled = true;
		}
	}
}
