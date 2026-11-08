using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The panel on the right: the picked bone's numbers and settings, or the picked part's (at
// scene level: the picked object's place). In Build the numbers are the rest pose; in Animate
// the pose at the playhead, and typing or dragging one keys it (EditorSession.TypeBoneValue).
public sealed partial class MainWindow
{
	private readonly TextBlock _propsTitle = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
	private readonly TextBlock _propsHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };
	private readonly List<NumberField> _numbers = new();

	// The bone's
	private readonly StackPanel _boneProps = new() { Spacing = 2 };
	internal readonly TextBox BoneName = new();
	private readonly ComboBox _boneParent = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly CheckBox _inheritRotation = new() { Content = "Turns with its parent" };
	private readonly CheckBox _inheritScale = new() { Content = "Scales with its parent" };
	private readonly CheckBox _connected = new() { Content = "Starts at its parent's end" };
	internal readonly Dictionary<BoneField, NumberField> BoneNumbers = new();

	// The part's
	private readonly StackPanel _partProps = new() { Spacing = 2 };
	internal readonly TextBox PartName = new();
	private readonly ComboBox _partBone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly ComboBox _partBlend = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = Enum.GetValues<BlendMode>() };
	internal readonly Dictionary<PartField, NumberField> PartNumbers = new();
	internal NumberField PartOpacity = null!;

	// The object's (scene level)
	private readonly StackPanel _objectProps = new() { Spacing = 2 };
	internal readonly TextBox ObjectName = new();
	internal readonly Dictionary<string, NumberField> ObjectNumbers = new();

	private object? _shownInProps;   // what the panel was filled in for
	private bool _syncingProps;

	private sealed record BoneChoice(Bone? Bone, string Label)
	{
		public override string ToString() => Label;
	}

	private Control BuildProperties()
	{
		NumberField Number(string label, float step, Func<float?> read, Action<float> commit, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
		{
			var field = new NumberField(label, step)
			{
				Read = read,
				Commit = commit,
				Min = min,
				Max = max,
				Begin = () => State.Undo.BeginGroup(),   // a drag is one undo step
				End = () => State.Undo.EndGroup(),
				Problem = text => _propsHint.Text = text
			};
			_numbers.Add(field);
			return field;
		}

		// A bone: where it is, how it's turned, long, scaled (in Animate: the pose here)
		foreach (var (field, label, step, min) in new[]
		{
			(BoneField.X, "X", 1f, float.NegativeInfinity), (BoneField.Y, "Y", 1f, float.NegativeInfinity),
			(BoneField.Rotation, "Rotation °", 1f, float.NegativeInfinity), (BoneField.Length, "Length", 1f, 0f),
			(BoneField.ScaleX, "Scale X", 0.01f, float.NegativeInfinity), (BoneField.ScaleY, "Scale Y", 0.01f, float.NegativeInfinity)
		})
		{
			var f = field;
			BoneNumbers[f] = Number(label, step,
				() => State.Selected is { } b && State.SelectedSlot == null ? _session.BoneValue(b, f) : null,
				v => { if (State.Selected is { } b) _session.TypeBoneValue(b, f, v, _tool.KeepPartsInPlace); }, min);
		}
		_boneProps.Children.Add(Labelled("Name", BoneName));
		foreach (var n in BoneNumbers.Values) _boneProps.Children.Add(n);
		_boneProps.Children.Add(Labelled("Parent", _boneParent));
		_boneProps.Children.Add(_connected);
		_boneProps.Children.Add(_inheritRotation);
		_boneProps.Children.Add(_inheritScale);
		var boneButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
		var addBone = new Button { Content = "+ Bone on it" };
		addBone.Click += (_, _) => { if (!State.AnimateMode && State.Selected is { } b) _session.AddBone(b, _tool.SnapJoints); };
		var deleteBone = new Button { Content = "Delete bone" };
		deleteBone.Click += (_, _) => { if (!State.AnimateMode && State.Selected is { } b) _session.DeleteBone(b); };
		var colour = new Button { Content = "Colour ▾" };
		ToolTip.SetTip(colour, "The colour the picked bones are drawn in, to tell them apart (left and right, say)");
		colour.Click += (_, _) => BoneColourMenu().ShowAt(colour);
		boneButtons.Children.Add(addBone);
		boneButtons.Children.Add(deleteBone);
		boneButtons.Children.Add(colour);
		_boneProps.Children.Add(boneButtons);

		BoneName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { RenameShown(); Viewport.Focus(); } };
		BoneName.LostFocus += (_, _) => RenameShown();
		_boneParent.SelectionChanged += (_, _) =>
		{
			if (_syncingProps || State.Selected is not { } bone || _boneParent.SelectedItem is not BoneChoice choice) return;
			_session.ParentBone(bone, choice.Bone, choice.Label, _tool.KeepPartsInPlace);
		};
		_connected.IsCheckedChanged += (_, _) =>
		{
			if (!_syncingProps && State.Selected is { } bone) _session.SetBoneConnected(bone, _connected.IsChecked == true);
		};
		void Inherit(object? s, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if (!_syncingProps && State.Selected is { } bone)
				_session.SetBoneInherit(bone, _inheritRotation.IsChecked == true, _inheritScale.IsChecked == true);
		}
		_inheritRotation.IsCheckedChanged += Inherit;
		_inheritScale.IsCheckedChanged += Inherit;

		// A part: where its drawing sits on the bone (in Animate: keyed placement), its pivot
		foreach (var (field, label, step) in new[]
		{
			(PartField.X, "X", 1f), (PartField.Y, "Y", 1f), (PartField.Rotation, "Rotation °", 1f),
			(PartField.ScaleX, "Scale X", 0.01f), (PartField.ScaleY, "Scale Y", 0.01f),
			(PartField.PivotX, "Pivot X", 1f), (PartField.PivotY, "Pivot Y", 1f)
		})
		{
			var f = field;
			PartNumbers[f] = Number(label, step,
				() => State.SelectedSlot is { } s && (EditorSession.IsPlacement(f) || !State.AnimateMode) ? _session.PartValue(s, f) : null,
				v => { if (State.SelectedSlot is { } s) _session.TypePartValue(s, f, v); });
		}
		PartOpacity = Number("Opacity %", 1f,
			() => State.SelectedSlot is { } s ? (State.AnimateMode ? s.Opacity : s.SetupOpacity) * 100f : null,
			v => { if (State.SelectedSlot is { } s) _session.SetPartColor(s, null, v / 100f); }, 0f, 100f);
		_partProps.Children.Add(Labelled("Name", PartName));
		foreach (var n in PartNumbers.Values) _partProps.Children.Add(n);
		_partProps.Children.Add(PartOpacity);
		_partProps.Children.Add(Labelled("Blend", _partBlend));
		_partProps.Children.Add(Labelled("On bone", _partBone));
		var order = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
		var front = new Button { Content = "Forward" };
		front.Click += (_, _) => { if (State.SelectedSlot is { } s) _session.Reorder(new[] { s }, +1, toEnd: false); };
		var back = new Button { Content = "Backward" };
		back.Click += (_, _) => { if (State.SelectedSlot is { } s) _session.Reorder(new[] { s }, -1, toEnd: false); };
		var delete = new Button { Content = "Delete part" };
		delete.Click += (_, _) => { if (State.SelectedSlot is { } s) _session.DeletePart(s); };
		var orderMenu = new Button { Content = "Order and mask ▾" };
		ToolTip.SetTip(orderMenu, "Its place in the draw order (with the other picked parts), and clipping the parts above it");
		orderMenu.Click += (_, _) => { if (State.SelectedSlot is { } s) OrderMenu(s).ShowAt(orderMenu); };
		var paintIt = new Button { Content = "Paint" };
		paintIt.Click += (_, _) => { if (State.SelectedSlot is { } s) OpenPaintEditorOn(s); };
		foreach (var b in new Control[] { front, back, orderMenu, paintIt, delete })
		{
			b.Margin = new Thickness(0, 0, 6, 6);
			order.Children.Add(b);
		}
		_partProps.Children.Add(order);
		_partProps.Children.Add(BuildDrawingRows());

		PartName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { RenameShown(); Viewport.Focus(); } };
		PartName.LostFocus += (_, _) => RenameShown();
		_partBlend.SelectionChanged += (_, _) =>
		{
			if (!_syncingProps && State.SelectedSlot is { } slot && _partBlend.SelectedItem is BlendMode blend) _session.SetBlend(slot, blend);
		};
		_partBone.SelectionChanged += (_, _) =>
		{
			if (!_syncingProps && State.SelectedSlot is { } slot && _partBone.SelectedItem is BoneChoice { Bone: { } bone }) _session.MovePartToBone(slot, bone);
		};

		// An object (scene level): where it's placed in the scene
		foreach (var (name, label, step) in new[] { ("X", "X", 1f), ("Y", "Y", 1f), ("Rotation", "Rotation °", 1f), ("ScaleX", "Scale X", 0.01f), ("ScaleY", "Scale Y", 0.01f) })
		{
			string n = name;
			ObjectNumbers[n] = Number(label, step, () => _session.SceneState.Selected is { } o ? ObjectValue(o, n) : null,
				v => { if (_session.SceneState.Selected is { } o) PlaceObject(o, n, v); });
		}
		_objectProps.Children.Add(Labelled("Name", ObjectName));
		foreach (var n in ObjectNumbers.Values) _objectProps.Children.Add(n);
		ObjectName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { RenameShown(); Viewport.Focus(); } };
		ObjectName.LostFocus += (_, _) => RenameShown();

		var panel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };
		panel.Children.Add(_propsTitle);
		panel.Children.Add(_boneProps);
		panel.Children.Add(_partProps);
		panel.Children.Add(_objectProps);
		panel.Children.Add(_propsHint);
		return new ScrollViewer { Content = panel };
	}

	private static Control Labelled(string label, Control control)
	{
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*"), Margin = new Thickness(0, 2) };
		var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
		Grid.SetColumn(control, 1);
		row.Children.Add(text);
		row.Children.Add(control);
		return row;
	}

	private static float ObjectValue(SceneObject o, string field) => field switch
	{
		"X" => o.Position.X,
		"Y" => o.Position.Y,
		"Rotation" => EditorSession.Deg(o.Rotation),
		"ScaleX" => o.Scale.X,
		_ => o.Scale.Y
	};

	private void PlaceObject(SceneObject o, string field, float v)
	{
		switch (field)
		{
			case "X": _session.PlaceObject(o, position: new Vector2(v, o.Position.Y)); break;
			case "Y": _session.PlaceObject(o, position: new Vector2(o.Position.X, v)); break;
			case "Rotation": _session.PlaceObject(o, rotation: EditorSession.Rad(v)); break;
			case "ScaleX": _session.PlaceObject(o, scale: new Vector2(v, o.Scale.Y)); break;
			default: _session.PlaceObject(o, scale: new Vector2(o.Scale.X, v)); break;
		}
	}

	// A name typed for what's shown
	internal void RenameShown()
	{
		if (_syncingProps) return;
		_propsHint.Text = "";
		switch (_shownInProps)
		{
			case Bone bone when (BoneName.Text?.Trim() ?? "") is var name && name != bone.Name:
				if (!_session.RenameBone(bone, name)) { _propsHint.Text = $"Can't rename to \"{name}\": names must be unique and not empty."; BoneName.Text = bone.Name; }
				break;
			case Slot slot when (PartName.Text?.Trim() ?? "") is var name && name != slot.Name:
				if (!_session.RenamePart(slot, name)) { _propsHint.Text = $"Can't rename to \"{name}\": part names must be unique and not empty."; PartName.Text = slot.Name; }
				break;
			case SceneObject obj when (ObjectName.Text?.Trim() ?? "") is var name && name != obj.Name:
				if (!_session.RenameObject(obj, name)) { _propsHint.Text = $"Can't rename to \"{name}\": object names must be unique and not empty."; ObjectName.Text = obj.Name; }
				break;
		}
	}

	// Every frame: the panel for what's picked (filled in again when that changes), its numbers current
	private void SyncProperties()
	{
		object? shown = SceneLevel ? _session.SceneState.Selected : (object?)State.SelectedSlot ?? State.Selected;
		if (shown != _shownInProps) FillProperties(shown);
		if (shown is Slot) SyncDrawingRows();
		foreach (var n in _numbers)
			if (n.IsEffectivelyVisible) n.Refresh();

		string title = shown switch
		{
			null => SceneLevel ? "Pick an object" : "Pick a bone or a part",
			SceneObject o => $"Object \"{o.Name}\": its place in the scene",
			_ when State.AnimateMode => State.AutoKey ? $"Pose at frame {State.Frame}: typing sets keys" : $"Pose at frame {State.Frame}: typing poses it (S keys it)",
			_ => "Rest pose (Build)"
		};
		if (_propsTitle.Text != title) _propsTitle.Text = title;
	}

	private void FillProperties(object? shown)
	{
		_shownInProps = shown;
		_propsHint.Text = "";
		_syncingProps = true;
		try
		{
			_boneProps.IsVisible = shown is Bone && _session.Active?.Kind != ObjectKind.Drawn;
			_partProps.IsVisible = shown is Slot;
			_objectProps.IsVisible = shown is SceneObject;
			switch (shown)
			{
				case Bone bone:
					BoneName.Text = bone.Name;
					var parents = new List<BoneChoice> { new(null, "(none: a root bone)") };
					parents.AddRange(State.Skeleton.AllBones().Where(b => b != bone && !b.IsDescendantOf(bone)).Select(b => new BoneChoice(b, b.Name)));
					_boneParent.ItemsSource = parents;
					_boneParent.SelectedItem = parents.FirstOrDefault(c => c.Bone == bone.Parent);
					_connected.IsChecked = bone is { Connected: true, Parent: not null };
					_connected.IsEnabled = bone.Parent != null;
					_inheritRotation.IsChecked = bone.InheritRotation;
					_inheritScale.IsChecked = bone.InheritScale;
					break;
				case Slot slot:
					PartName.Text = slot.Name;
					var bones = State.Skeleton.AllBones().Select(b => new BoneChoice(b, b.Name)).ToList();
					_partBone.ItemsSource = bones;
					_partBone.SelectedItem = bones.FirstOrDefault(c => c.Bone == slot.Bone);
					_partBone.IsEnabled = _session.Active?.Kind != ObjectKind.Drawn;
					_partBlend.SelectedItem = slot.Blend;
					FillDrawingRows(slot);
					break;
				case SceneObject obj:
					ObjectName.Text = obj.Name;
					break;
			}
		}
		finally
		{
			_syncingProps = false;
		}
		foreach (var n in _numbers) n.Refresh(force: true);
	}

	// After an undo, a rename, a new parent…: filled in again
	private void RefillProperties() => FillProperties(_shownInProps);
}
