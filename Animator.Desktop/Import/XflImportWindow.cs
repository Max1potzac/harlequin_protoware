using Animator.Core.Import;
using Animator.Viewer.Import;
using Animator.Viewer.Painting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Animator.Desktop.Import;

// File > Import an Animate project: which timeline, which parts come in, and whether as a drawn
// object (its parts moving on their own) or a rigged one, with bones worked out from how the
// parts move, every choice of which can be changed (and is shown on the picture, played). The
// same as the Windows editor's dialog; ShowDialog<bool>: true, import as the plan now says.
public sealed class XflImportWindow : Window
{
	private sealed record Choice(int Part, string Label)
	{
		public override string ToString() => Label;
	}

	private readonly XflImportPlan _plan;
	private RigSuggestion? _rig;                 // the suggestion as things stand (null = no rig)
	private readonly Dictionary<int, Matrix3x2[]> _rigMotion = new();   // its bones over the timeline, as they're worked out
	private bool _rolesSuggested;
	private int _picked = -1;                    // the picked part (index in the plan's parts)
	private readonly Dictionary<int, (CheckBox Include, CheckBox InRig, CheckBox Effect, TextBlock Name, TextBlock Details)> _rows = new();
	private bool _syncing;
	internal int Frame { get; private set; }
	private readonly DispatcherTimer _player = new();

	// The picture: timeline point p shows at p × _zoom + _pan (in the view's units)
	private float _zoom = 1f;
	private Vector2 _pan, _lastView;
	private bool _fitted, _panning;
	private static bool _showGhosts = true;   // (kept for the next import, while the program runs)

	private readonly TextBlock _fileTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _notes = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly ComboBox TimelineBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly TextBlock _timelineText = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
	internal readonly CheckBox SplitBox = new() { Content = "An animation for each frame label" };
	private readonly CheckBox _followBox = new() { Content = "Parts follow their drawings from layer to layer" };
	internal readonly ListBox PartList = new() { MaxHeight = 320 };
	internal readonly CheckBox RigBox = new() { Content = "Suggest a rig from the animation" };
	private readonly StackPanel _rigFields = new() { Spacing = 6 };
	private readonly TextBlock _setupText = new() { VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBlock _rigAnimationsLabel = new() { Text = "Worked out from these animations:" };
	private readonly WrapPanel _rigAnimations = new();
	private readonly ComboBox _rootBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly StackPanel _partFields = new() { Spacing = 4 };
	private readonly TextBlock _partTitle = new() { FontWeight = FontWeight.SemiBold };
	private readonly RadioButton _ownBone = new() { Content = "Its own bone (a part that bends or turns)", GroupName = "xrole" };
	private readonly RadioButton _onBone = new() { Content = "No bone: it rides on another part's bone", GroupName = "xrole" };
	private readonly RadioButton _effect = new() { Content = "An effect (VFX): no bone, it moves on its own", GroupName = "xrole" };
	private readonly TextBlock _targetLabel = new() { Text = "Hangs from", VerticalAlignment = VerticalAlignment.Center, Width = 80 };
	private readonly ComboBox _targetBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox ScaleBox = new() { Text = "100", Width = 64 };
	internal readonly TextBlock Summary = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _error = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly SkiaView Preview = new();
	private readonly Button _play = new() { Content = "▶" };
	private readonly CheckBox _ghosts = new() { Content = "Show left-out parts" };
	private readonly TextBlock _frameText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 170 };
	private readonly Slider _frameSlider = new() { Minimum = 0, SmallChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1 };
	private readonly TextBlock _viewHint = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4) };

	public XflImportWindow(XflImportPlan plan)
	{
		_plan = plan;
		Title = "Import an Animate project";
		Width = 1200;
		Height = 780;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		_ghosts.IsChecked = _showGhosts;
		RigBox.IsChecked = plan.SuggestRig;
		SplitBox.IsChecked = plan.SplitAtLabels;
		_player.Tick += (_, _) => StepBy(+1);
		Content = BuildLayout();
		_syncing = true;
		TimelineBox.ItemsSource = plan.Timelines;
		TimelineBox.SelectedItem = plan.Timeline;
		_syncing = false;
		Closed += (_, _) => _player.Stop();
		AddHandler(KeyDownEvent, Window_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);   // (before a tick box or a button takes Space)
		Rebuild();
	}

	private Control BuildLayout()
	{
		static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 8, 0, 2) };
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
		Button B(string text, Action click, string? tip = null)
		{
			var b = new Button { Content = text };
			b.Click += (_, _) => click();
			if (tip != null) ToolTip.SetTip(b, tip);
			return b;
		}

		var s = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
		s.Children.Add(_fileTitle);
		s.Children.Add(_notes);

		s.Children.Add(Heading("Timeline"));
		ToolTip.SetTip(TimelineBox, "A scene, or a symbol with its own animation (a character in a bigger scene, say)");
		s.Children.Add(TimelineBox);
		s.Children.Add(_timelineText);
		ToolTip.SetTip(SplitBox, "Each label starts an animation that runs to the next label (idle, walk, attack…). Without it, the whole timeline is one animation.");
		s.Children.Add(SplitBox);
		TimelineBox.SelectionChanged += (_, _) =>
		{
			if (_syncing || TimelineBox.SelectedItem is not XflTimelineChoice choice || choice == _plan.Timeline) return;
			_plan.Choose(choice);
			Rebuild();
		};
		SplitBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncing) return;
			_plan.SplitAtLabels = SplitBox.IsChecked == true;
			_plan.NotForRig.Clear();   // the animations aren't the same ones any more
			_plan.Forget();
			_rolesSuggested = false;
			ShowRigAnimations();
			Refresh();
		};

		s.Children.Add(Heading("Parts"));
		s.Children.Add(Note("Each thing that moves on its own becomes a part (the top one is at the top here). In: bring it in. Rig: use it to work out the rig; untick skins, extras and things shown alongside their alternatives, and they'll ride on the bone they move with. FX: an effect (VFX), such as crumbs, splashes or sparks: left out of the rig, it moves on its own and is hidden at rest. Parts that only show for a moment start as effects."));
		ToolTip.SetTip(_followBox, "For projects where a part moves to another layer from one animation to the next, or a layer shows something else for a while. Without it, each layer is a part.");
		s.Children.Add(_followBox);
		_followBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncing) return;
			bool follow = _followBox.IsChecked == true;
			if (follow == _plan.FollowDrawings) return;
			_plan.FollowDrawings = follow;
			Rebuild();
		};
		var heads = new Grid { ColumnDefinitions = new ColumnDefinitions("28,28,30,*") };
		foreach (var (text, col) in new[] { ("In", 0), ("Rig", 1), ("FX", 2), ("Part (hover it for the layers it's on)", 3) })
		{
			var t = new TextBlock { Text = text, Foreground = Brushes.Gray, FontSize = 12 };
			Grid.SetColumn(t, col);
			heads.Children.Add(t);
		}
		s.Children.Add(heads);
		s.Children.Add(PartList);
		PartList.SelectionChanged += (_, _) =>
		{
			if (_syncing) return;
			_picked = PartList.SelectedItem is ListBoxItem { Tag: int i } ? i : -1;
			ShowPicked();
			Preview.InvalidateVisual();
		};
		s.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			Children =
			{
				B("Bring them all in", () => { foreach (var p in _plan.Parts) p.Include = true; Refresh(); }),
				B("Only the visible ones", () => { foreach (var p in _plan.Parts) p.Include = !_plan.Data.Tracks[p.Track].Hidden; Refresh(); },
					"Brings in the parts on layers shown in Animate, and leaves out the ones on hidden layers")
			}
		});

		s.Children.Add(Heading("Rig"));
		ToolTip.SetTip(RigBox, "Bones where the parts turn around each other, keyed to move as they did. Without it, the project comes in as a drawn object: its parts, moving on their own.");
		s.Children.Add(RigBox);
		RigBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncing) return;
			_plan.SuggestRig = RigBox.IsChecked == true;
			if (_plan.SuggestRig && Frame != _plan.SetupFrame) SetFrame(_plan.SetupFrame);   // the rig shows on the rest pose
			Refresh();
		};
		_rigFields.Children.Add(Note("Parts that turn around a point they share get bones jointed there; details lying on another part ride on its bone. Change any of it below; the picture shows it on the rest pose."));
		_rigFields.Children.Add(Row("Rest pose", new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Children = { _setupText, B("Use the frame shown", () => { _plan.SetupFrame = Frame; _plan.Forget(); _rolesSuggested = false; Refresh(); }) }
		}));
		ToolTip.SetTip(_rigAnimationsLabel, "Untick animations where parts come apart (a head falling off, an arm dropping): they'd hide the joints the others show");
		_rigFields.Children.Add(_rigAnimationsLabel);
		_rigFields.Children.Add(_rigAnimations);
		_rigFields.Children.Add(Row("Root", _rootBox));
		_rootBox.SelectionChanged += (_, _) =>
		{
			if (_syncing || _rootBox.SelectedItem is not Choice c) return;
			_plan.Root = c.Part;
			Refresh();
		};
		_partFields.Children.Add(_ownBone);
		_partFields.Children.Add(_onBone);
		_partFields.Children.Add(_effect);
		_partFields.Children.Add(RowWith(_targetLabel, _targetBox));
		foreach (var r in new[] { _ownBone, _onBone, _effect }) r.IsCheckedChanged += (_, _) => RoleChanged();
		_targetBox.SelectionChanged += (_, _) =>
		{
			if (_syncing || _picked < 0 || _targetBox.SelectedItem is not Choice c) return;
			_plan.Parts[_picked].Target = c.Part;
			Refresh();
		};
		_rigFields.Children.Add(_partTitle);
		_rigFields.Children.Add(_partFields);
		_rigFields.Children.Add(B("Suggest the roles again", () =>
		{
			foreach (var part in _plan.Parts) part.Effect = _plan.Data.Tracks[part.Track].ShortLived;
			_rolesSuggested = false;
			Refresh();
		}, "Forgets the changes above and works out each part's role from the shapes and the motion again"));
		s.Children.Add(_rigFields);

		s.Children.Add(Heading("Size"));
		ToolTip.SetTip(ScaleBox, "Big projects can come in smaller: 50 = half the size, in pixels and in placement");
		ScaleBox.LostFocus += (_, _) => { if (ReadScale() is { } v) _plan.Scale = v; RefreshSummary(); };
		ScaleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (ReadScale() is { } v) _plan.Scale = v; RefreshSummary(); e.Handled = true; } };
		s.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ScaleBox, new TextBlock { Text = "% of the project's size", VerticalAlignment = VerticalAlignment.Center } } });

		// Import / Cancel, under the summary
		var import = new Button { Content = "Import", MinWidth = 90, IsDefault = true };
		import.Click += (_, _) => TryImport();
		var cancel = new Button { Content = "Cancel", MinWidth = 90 };
		cancel.Click += (_, _) => Close(false);
		var bottom = new DockPanel { Margin = new Thickness(12, 8) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, cancel } };
		DockPanel.SetDock(buttons, Dock.Right);
		bottom.Children.Add(buttons);
		bottom.Children.Add(new StackPanel { Children = { Summary, _error } });

		// The picture, with its frame bar
		ToolTip.SetTip(_play, "Play the timeline (Space)");
		_play.Click += (_, _) => TogglePlay();
		ToolTip.SetTip(_ghosts, "The parts that won't be brought in (In unticked), drawn faintly. Untick to see just what you'll get.");
		_ghosts.IsCheckedChanged += (_, _) => { _showGhosts = _ghosts.IsChecked == true; Preview.InvalidateVisual(); };
		_frameSlider.PropertyChanged += (_, e) =>
		{
			if (e.Property != RangeBase.ValueProperty || _syncing) return;
			Frame = (int)Math.Round(_frameSlider.Value);
			ShowFrame();
		};
		var bar = new DockPanel { Margin = new Thickness(8, 4) };
		var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _play, _ghosts, _frameText } };
		DockPanel.SetDock(left, Dock.Left);
		bar.Children.Add(left);
		bar.Children.Add(_frameSlider);
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
			var under = ToTimeline(p);
			_zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25f : 0.8f), 0.01f, 40f);
			_pan = p - under * _zoom;
			Preview.InvalidateVisual();
			e.Handled = true;
		};
		var picture = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		picture.Children.Add(bar);
		DockPanel.SetDock(_viewHint, Dock.Bottom);
		picture.Children.Add(_viewHint);
		picture.Children.Add(Preview);

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

	private static Control Row(string label, Control control) =>
		RowWith(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 80 }, control);

	private static Control RowWith(Control label, Control control)
	{
		var row = new DockPanel();
		DockPanel.SetDock(label, Dock.Left);
		row.Children.Add(label);
		row.Children.Add(control);
		return row;
	}

	// A new timeline (or parts following their drawings): new parts
	private void Rebuild()
	{
		var doc = _plan.Document;
		_fileTitle.Text = $"{_plan.Name}   ({doc.Width:0} × {doc.Height:0} pixels, {doc.FrameRate:0.##} frames a second)";
		_notes.Text = string.Join("\n", _plan.Notes);
		_notes.IsVisible = _plan.Notes.Count > 0;
		var labels = _plan.Data.Labels;
		_timelineText.Text = $"{_plan.Data.FrameCount} frame(s)" + (labels.Count > 0 ? $", labels: {string.Join(", ", labels.Select(l => l.Name))}" : ", no frame labels");
		SplitBox.IsEnabled = labels.Count > 0;
		_syncing = true;
		_followBox.IsChecked = _plan.FollowDrawings;
		_syncing = false;

		_rows.Clear();
		var items = new List<ListBoxItem>();
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)   // the front one at the top
		{
			int index = i;
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,28,30,*") };
			CheckBox Tick(int column, string tip, Action<bool> set)
			{
				var tick = new CheckBox { VerticalAlignment = VerticalAlignment.Top, MinWidth = 0, Padding = new Thickness(0) };
				ToolTip.SetTip(tick, tip);
				tick.IsCheckedChanged += (_, _) => { if (!_syncing) { set(tick.IsChecked == true); Refresh(); } };
				Grid.SetColumn(tick, column);
				row.Children.Add(tick);
				return tick;
			}
			var include = Tick(0, "Bring it in (I, for the picked part)", v => _plan.Parts[index].Include = v);
			var inRig = Tick(1, "Use it to work out the rig (R, for the picked part)", v => _plan.Parts[index].InRig = v);
			var effect = Tick(2, "An effect (VFX; X, for the picked part): left out of the rig, it moves on its own and is hidden at rest", v => _plan.Parts[index].Effect = v);
			var name = new TextBlock { TextWrapping = TextWrapping.Wrap };
			var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11 };
			var text = new StackPanel { Children = { name, details } };
			Grid.SetColumn(text, 3);
			row.Children.Add(text);
			var item = new ListBoxItem { Content = row, Tag = i, Padding = new Thickness(4, 2) };
			if (LayersOf(i) is { } layers) ToolTip.SetTip(item, layers);
			items.Add(item);
			_rows[i] = (include, inRig, effect, name, details);
		}
		PartList.ItemsSource = items;
		_picked = -1;
		_rolesSuggested = false;
		_frameSlider.Maximum = Math.Max(0, _plan.Data.FrameCount - 1);
		SetFrame(_plan.SetupFrame);
		_fitted = false;
		ShowRigAnimations();
		Refresh();
	}

	// The animations the rig is worked out from (shown when there are several)
	private void ShowRigAnimations()
	{
		_rigAnimations.Children.Clear();
		var animations = _plan.Animations();
		_rigAnimationsLabel.IsVisible = _rigAnimations.IsVisible = animations.Count > 1;
		if (animations.Count < 2) return;
		foreach (var (name, _, _) in animations)
		{
			string n = name;
			var tick = new CheckBox { Content = name, IsChecked = !_plan.NotForRig.Contains(name), Margin = new Thickness(0, 0, 10, 2) };
			tick.IsCheckedChanged += (_, _) =>
			{
				if (tick.IsChecked == true) _plan.NotForRig.Remove(n);
				else _plan.NotForRig.Add(n);
				_plan.Forget();
				_rolesSuggested = false;
				Refresh();
			};
			_rigAnimations.Children.Add(tick);
		}
	}

	// Where a part's frames come from, for its tooltip (counted from 1, as Animate counts them)
	private string? LayersOf(int part)
	{
		var pieces = _plan.Data.Tracks[_plan.Parts[part].Track].Pieces;
		if (pieces.Count == 0) return null;
		static string Span(XflTrackPiece p) => p.Start == p.End ? $"{p.Start + 1}" : $"{p.Start + 1}–{p.End + 1}";
		var lines = pieces.GroupBy(p => p.Layer).Select(g =>
		{
			var spans = g.Select(Span).ToList();
			string frames = spans.Count > 8 ? string.Join(", ", spans.Take(8)) + $" and {spans.Count - 8} more" : string.Join(", ", spans);
			return $"{(g.Key.Trim().Length > 0 ? g.Key.Trim() : "(a layer with no name)")}: frames {frames}";
		}).ToList();
		if (lines.Count > 12) lines = lines.Take(12).Append($"… and {lines.Count - 12} more layers").ToList();
		return string.Join("\n", lines);
	}

	// ======================= frames =======================

	private void SetFrame(int frame)
	{
		Frame = frame;
		_syncing = true;
		_frameSlider.Value = frame;
		_syncing = false;
		ShowFrame();
	}

	private void ShowFrame()
	{
		_frameText.Text = $"frame {Frame + 1} of {_plan.Data.FrameCount}" + (LabelAt(Frame) is { } label ? $"  ({label})" : "");
		Preview.InvalidateVisual();
	}

	private string? LabelAt(int frame) => _plan.Data.Labels.Where(l => l.Frame <= frame).Select(l => l.Name).LastOrDefault();

	internal void TogglePlay()
	{
		if (_player.IsEnabled)
		{
			_player.Stop();
			_play.Content = "▶";
			return;
		}
		_player.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _plan.Document.FrameRate));
		_player.Start();
		_play.Content = "⏸";
	}

	// A frame on or back (round to the other end)
	internal void StepBy(int by)
	{
		int count = Math.Max(1, _plan.Data.FrameCount);
		SetFrame(((Frame + by) % count + count) % count);
	}

	// ======================= the parts and the rig =======================

	private void Pick(int part)
	{
		_picked = part;
		_syncing = true;
		var item = (PartList.ItemsSource as List<ListBoxItem>)?.FirstOrDefault(it => it.Tag is int i && i == part);
		PartList.SelectedItem = item;
		if (item != null) PartList.ScrollIntoView(item);
		_syncing = false;
		ShowPicked();
		Preview.InvalidateVisual();
	}

	internal void PickPart(int part) => Pick(part);

	// An effect, or a role in the rig (which puts it in the rig)
	private void RoleChanged()
	{
		if (_syncing || _picked < 0) return;
		var part = _plan.Parts[_picked];
		if (_effect.IsChecked == true)
		{
			if (part.Effect) return;
			part.Effect = true;
		}
		else
		{
			if (_ownBone.IsChecked != true && _onBone.IsChecked != true) return;   // (the one being unticked)
			var role = _onBone.IsChecked == true ? RigRole.OnBone : RigRole.Bone;
			if (!part.Effect && part.InRig && part.Role == role) return;
			part.Effect = false;
			part.InRig = true;
			part.Role = role;
			part.Target = -1;
		}
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
			_error.Text = "Tick at least one part to bring in.";
			return;
		}
		_player.Stop();
		Close(true);
	}

	// ======================= refreshing =======================

	private void Refresh()
	{
		_rig = null;
		_rigMotion.Clear();
		if (_plan.SuggestRig && _plan.Parts.Any(p => p.Include))
		{
			if (!_rolesSuggested)
			{
				_plan.SuggestRoles();
				_rolesSuggested = true;
			}
			_rig = _plan.Suggest();
		}
		_rigFields.IsEnabled = _plan.SuggestRig;
		_setupText.Text = $"frame {_plan.SetupFrame + 1}";

		_syncing = true;
		RigBox.IsChecked = _plan.SuggestRig;
		foreach (var (i, (include, inRig, effect, name, details)) in _rows)
		{
			var part = _plan.Parts[i];
			include.IsChecked = part.Include;
			inRig.IsChecked = part.InRig && !part.Effect;
			inRig.IsEnabled = part.Include && _plan.SuggestRig && !part.Effect;
			effect.IsChecked = part.Effect;
			effect.IsEnabled = part.Include;
			name.Text = part.Name;
			name.FontWeight = part.Include ? FontWeight.SemiBold : FontWeight.Normal;
			details.Text = string.Join(" · ", Details(i));
			details.IsVisible = details.Text.Length > 0;
		}

		var roots = new List<Choice> { new(-1, _rig != null && _rig.RootLayer >= 0 ? $"Worked out ({_plan.Parts[_rig.RootLayer].Name})" : "Worked out") };
		for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			if (_plan.Parts[i].Include && _plan.Parts[i].InRig && !_plan.Parts[i].Effect && _plan.Parts[i].Role == RigRole.Bone)
				roots.Add(new Choice(i, _plan.Parts[i].Name));
		_rootBox.ItemsSource = roots;
		_rootBox.SelectedItem = roots.FirstOrDefault(c => c.Part == _plan.Root) ?? roots[0];
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
		if (part.Effect)
		{
			yield return $"effect, shown on {track.Shown.Count()} of {_plan.Data.FrameCount} frames";
			yield break;
		}
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
		try
		{
			var part = _picked >= 0 ? _plan.Parts[_picked] : null;
			_partFields.IsEnabled = part != null && part.Include && _rig != null;
			if (part == null)
			{
				_partTitle.Text = "Pick a part to change what it gets";
				_ownBone.IsChecked = _onBone.IsChecked = _effect.IsChecked = false;
				_targetBox.ItemsSource = null;
				_targetLabel.Text = "Hangs from";
				return;
			}

			_partTitle.Text = !part.Include ? $"{part.Name} (not brought in)"
				: part.Effect ? $"{part.Name} (an effect)"
				: !part.InRig ? $"{part.Name} (left out of the rig)" : part.Name;
			bool inRig = part.InRig && !part.Effect;
			_ownBone.IsChecked = inRig && part.Role == RigRole.Bone;
			_onBone.IsChecked = inRig && part.Role == RigRole.OnBone;
			_effect.IsChecked = part.Effect;
			_targetLabel.Text = part.Role == RigRole.Bone ? "Hangs from" : "Rides on";

			bool isRoot = _rig != null && _rig.RootLayer == _picked;
			string worked = isRoot ? "Nothing: it's the root"
				: part.Target < 0 && SuggestedTarget(_picked) is { } suggested ? $"Worked out ({suggested})"
				: "Worked out from the overlaps and the motion";
			var choices = new List<Choice> { new(-1, worked) };
			for (int i = _plan.Parts.Count - 1; i >= 0; i--)
				if (i != _picked && _plan.Parts[i].Include && _plan.Parts[i].InRig && !_plan.Parts[i].Effect && (part.Role == RigRole.OnBone || _plan.Parts[i].Role == RigRole.Bone))
					choices.Add(new Choice(i, _plan.Parts[i].Name));
			_targetBox.ItemsSource = choices;
			_targetBox.SelectedItem = choices.FirstOrDefault(c => c.Part == part.Target) ?? choices[0];
			_targetBox.IsEnabled = !isRoot && inRig;
		}
		finally
		{
			_syncing = false;
		}
	}

	private void RefreshSummary()
	{
		int parts = _plan.Parts.Count(p => p.Include);
		var animations = _plan.Animations();
		string size = Math.Abs(_plan.Scale - 1f) < 0.001f ? "" : $", at {_plan.Scale * 100:0.#}% of the size";
		string anims = animations.Count == 1 ? "1 animation" : $"{animations.Count} animations";
		if (parts == 0) Summary.Text = "Nothing is ticked.";
		else if (_rig == null) Summary.Text = $"Brings in a drawn object \"{_plan.Name}\" with {parts} part(s) and {anims}{size}.";
		else Summary.Text = $"Brings in a rigged object \"{_plan.Name}\": {_rig.Bones.Count} bone(s), {parts} part(s) and {anims}{size}.";
	}

	// ======================= the picture =======================

	private Vector2 ToView(Vector2 point) => point * _zoom + _pan;
	private Vector2 ToTimeline(Vector2 view) => (view - _pan) / _zoom;

	// What to fit in the view: the parts at this frame, and a scene's stage
	private SKRect ContentBounds()
	{
		var bounds = _plan.Timeline.Scene ? new SKRect(0, 0, _plan.Document.Width, _plan.Document.Height) : SKRect.Empty;
		for (int i = 0; i < _plan.Parts.Count; i++)
			if (_plan.Placement(i, Frame) is { } p)
			{
				var box = SkeletonRenderer.ToSk(p.Matrix).MapRect(_plan.Painter.Bounds(p.Drawing));
				bounds = bounds.IsEmpty ? box : SKRect.Union(bounds, box);
			}
		return bounds.IsEmpty ? new SKRect(-100, -100, 100, 100) : bounds;
	}

	private void Fit(Vector2 view)
	{
		if (view.X <= 0 || view.Y <= 0) return;
		var content = ContentBounds();
		_zoom = Math.Clamp(MathF.Min(view.X / MathF.Max(1, content.Width), view.Y / MathF.Max(1, content.Height)) * 0.9f, 0.01f, 8f);
		_pan = view / 2 - new Vector2(content.MidX, content.MidY) * _zoom;
		_fitted = true;
	}

	private void DrawPreview(SKCanvas canvas, Vector2 view, float dpi)
	{
		canvas.Clear(new SKColor(40, 44, 52));
		canvas.Scale(dpi);
		if (!_fitted) Fit(view);
		bool ghosts = _ghosts.IsChecked == true;

		if (_plan.Timeline.Scene)
		{
			var stage = new SKRect(_pan.X, _pan.Y, _pan.X + _plan.Document.Width * _zoom, _pan.Y + _plan.Document.Height * _zoom);
			Checkerboard.Draw(canvas, stage, view.X, view.Y);
		}

		// The parts brought in, at this frame (the others faintly)
		canvas.Save();
		canvas.Translate(_pan.X, _pan.Y);
		canvas.Scale(_zoom);
		for (int i = 0; i < _plan.Parts.Count; i++)
		{
			if (!_plan.Parts[i].Include && !ghosts) continue;
			if (_plan.Placement(i, Frame) is not { } p) continue;
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

		if (_picked >= 0 && _plan.Placement(_picked, Frame) is { } picked)
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
		if (_rig != null) DrawRig(canvas, _rig, Frame);
		_viewHint.Text = _rig != null
			? "The bones move as they'll be animated (red: a joint the parts don't turn around)   Click: pick a part   Wheel: zoom   Space: play   < >: a frame back / on   I / R / X: the picked part's In / Rig / FX"
			: "Click: pick a part   Wheel: zoom   Middle drag: move around   Home: fit   Space: play   < >: a frame back / on   I / R / X: the picked part's In / Rig / FX";
	}

	// The object's space → the timeline's (the picture's)
	private Vector2 ObjectToTimeline(Vector2 point) =>
		Matrix3x2.Invert(_plan.TimelineToObject, out var back) ? Vector2.Transform(point, back) : point;

	// A bone's frame from its joint and end: there, turned along it
	private static Matrix3x2 FrameOf((Vector2 Head, Vector2 Tip) bone)
	{
		var along = bone.Tip - bone.Head;
		float angle = along.LengthSquared() > 1e-8f ? MathF.Atan2(along.Y, along.X) : 0f;
		return Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(bone.Head);
	}

	// Each bone posed as the import will animate it at this frame (faded while its part isn't
	// shown). A joint that doesn't stay where its parent's bone carries it gets a red line to
	// where it should be.
	private void DrawRig(SKCanvas canvas, RigSuggestion rig, int frame)
	{
		var posed = _plan.RigAt(rig, frame, _rigMotion);
		var rest = _plan.RigAt(rig, _plan.SetupFrame, _rigMotion);
		RigPicture.Draw(canvas, rig, p => ToView(ObjectToTimeline(p)), _picked, i => _plan.Parts[i].Name,
			i => _plan.Placement(i, frame) is { } p ? Vector2.Transform(Vector2.Zero, p.Matrix) : Vector2.Zero,
			b => (posed[b].Head, posed[b].Tip));

		using var drift = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = new SKColor(255, 60, 60), IsAntialias = true };
		for (int b = 0; b < rig.Bones.Count; b++)
		{
			var bone = rig.Bones[b];
			if (bone.Parent < 0) continue;
			var head = ToView(ObjectToTimeline(posed[b].Head));
			var carried = bone.Connected
				? posed[bone.Parent].Tip
				: Matrix3x2.Invert(FrameOf(rest[bone.Parent]), out var fromParent)
					? Vector2.Transform(Vector2.Transform(rest[b].Head, fromParent), FrameOf(posed[bone.Parent]))
					: posed[b].Head;
			var should = ToView(ObjectToTimeline(carried));
			if (Vector2.Distance(should, head) > 3f)
			{
				canvas.DrawLine(should.X, should.Y, head.X, head.Y, drift);
				canvas.DrawCircle(head.X, head.Y, 7f, drift);
			}
		}
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

		// The frontmost part brought in that's there (else the frontmost one left out)
		var at = ToTimeline(p);
		int Hit(bool included)
		{
			for (int i = _plan.Parts.Count - 1; i >= 0; i--)
			{
				if (_plan.Parts[i].Include != included || _plan.Placement(i, Frame) is not { } placed) continue;
				if (!Matrix3x2.Invert(placed.Matrix, out var back)) continue;
				var local = Vector2.Transform(at, back);
				if (_plan.Painter.Bounds(placed.Drawing).Contains(local.X, local.Y)) return i;
			}
			return -1;
		}
		int hit = Hit(true);
		Pick(hit >= 0 || _ghosts.IsChecked != true ? hit : Hit(false));
	}

	private void Window_KeyDown(object? sender, KeyEventArgs e)
	{
		if (FocusManager?.GetFocusedElement() is TextBox) return;
		if (e.Key == Key.Home)
		{
			_fitted = false;
			Preview.InvalidateVisual();
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			Close(false);
			e.Handled = true;
		}
		else if (e.Key == Key.Space)
		{
			TogglePlay();
			e.Handled = true;
		}
		else if (e.Key is Key.OemComma or Key.OemPeriod)
		{
			// < and > (with or without Shift): a frame back or on
			if (_player.IsEnabled) TogglePlay();
			StepBy(e.Key == Key.OemPeriod ? +1 : -1);
			e.Handled = true;
		}
		else if (e.Key is Key.I or Key.R or Key.X && _picked >= 0 && _picked < _plan.Parts.Count)
		{
			// The picked part's ticks: I = bring it in, R = use it for the rig, X = an effect
			var part = _plan.Parts[_picked];
			if (e.Key == Key.I) part.Include = !part.Include;
			else if (e.Key == Key.R) part.InRig = !part.InRig;
			else part.Effect = !part.Effect;
			Refresh();
			e.Handled = true;
		}
	}
}
