using Animator.Core.Editing;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

public partial class MainWindow
{
	private bool _syncingRig;
	private Bone? _shownBone;
	private object? _shownSelection;   // the Bone or Slot the panel currently shows
	private readonly Dictionary<Bone, TreeViewItem> _treeItems = new();
	private readonly Dictionary<Slot, TreeViewItem> _slotItems = new();

	private sealed record BoneChoice(Bone? Bone, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly string[] PoseFields = { "X", "Y", "Rotation", "ScaleX", "ScaleY", "Turn", "Tilt" };

	private object? CurrentSelection => _state.SelectedSlot != null ? _state.SelectedSlot : _state.Selected;

	// A drawn object's one bone is hidden: the panel shows its parts, like layers
	private bool IsDrawn => _active?.Kind == ObjectKind.Drawn;

	private void InitRigPanel()
	{
		LookShapeBox.ItemsSource = Enum.GetValues<LookShape>();
		KeepPartsBox.IsChecked = _tool.KeepPartsInPlace = _appSettings.KeepPartsInPlace;
		_state.Undo.Changed += RefreshRigPanel;
		RefreshRigPanel();
	}

	private LookConstraint? LookOf(Bone? bone) =>
		bone == null ? null : _skeleton.LookConstraints.Find(c => c.Bone == bone);

	// ======================= tree =======================

	private void RefreshRigPanel()
	{
		// An undo may have removed the selected part or bone
		if (_state.SelectedSlot != null && !_skeleton.Slots.Contains(_state.SelectedSlot))
			_state.SelectedSlot = null;
		if (_state.Selected != null && !_skeleton.AllBones().Contains(_state.Selected))
			_state.Selected = null;

		_syncingRig = true;
		BoneTree.Items.Clear();
		_treeItems.Clear();
		_slotItems.Clear();
		_dropMark = null;
		_listedOrder = CurrentOrder().ToList();
		_listedAnimating = _state.AnimateMode;
		if (ShowingLayers)
		{
			BuildLayers();   // front first, like layers
		}
		else
		{
			foreach (var root in _skeleton.Roots)
				BoneTree.Items.Add(MakeTreeItem(root));
		}
		SelectTreeItem();
		SyncLayersView();
		BuildColourPanel();   // (the Colour view's list, when it shows)
		BuildSetChips();
		_syncingRig = false;
		SyncTreePicks(force: true);

		SyncRigMode();
		ShowProperties();
	}

	// Rigged object: bones and parts. Drawn object: parts only (no bone tools, no snapping).
	private void SyncRigMode()
	{
		bool drawn = IsDrawn;
		RigTitle.Text = drawn ? "Parts" : "Rig";
		AddBoneButton.IsEnabled = BindButton.IsEnabled = !drawn;
		SnapJointsBox.Visibility = KeepPartsBox.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
		GiveBonesButton.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
		PartBoneLabel.Visibility = PartBone.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
		if (_tool is not null) _tool.BonesEnabled = !drawn;   // also after undoing "Give it bones"
	}

	private TreeViewItem MakeTreeItem(Bone bone)
	{
		string header = bone.Name;
		if (LookOf(bone) is { } look) header += $"  👁 {look.Target.Name}";

		var item = new TreeViewItem { Header = RowHeader(bone, new TextBlock { Text = header }), Tag = bone, IsExpanded = true, Foreground = Brushes.White };
		_treeItems[bone] = item;

		// Parts on this bone; the number is the draw order (higher = in front)
		foreach (var slot in _skeleton.Slots.Where(s => s.Bone == bone))
			item.Items.Add(MakeSlotItem(slot));

		foreach (var child in bone.Children)
			item.Items.Add(MakeTreeItem(child));
		return item;
	}

	private TreeViewItem MakeSlotItem(Slot slot)
	{
		var item = new TreeViewItem
		{
			Header = RowHeader(slot, new TextBlock { Text = $"▣ {slot.Name}   #{IndexIn(_listedOrder, slot) + 1}" }),   // its place in the draw order (1 = at the back)
			Tag = slot,
			Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 110)),
			FontStyle = FontStyles.Italic,
			ContextMenu = OrderMenu(slot)   // its place in the draw order, whatever bone it's on
		};
		_slotItems[slot] = item;
		return item;
	}

	// Marks the current selection (part first, else bone) in the tree
	private void SelectTreeItem()
	{
		TreeViewItem? item = null;
		if (_state.SelectedSlot != null) _slotItems.TryGetValue(_state.SelectedSlot, out item);
		else if (_state.Selected != null) _treeItems.TryGetValue(_state.Selected, out item);

		if (item != null)
		{
			item.IsSelected = true;
			item.BringIntoView();
		}
		else if (BoneTree.SelectedItem is TreeViewItem old)
		{
			old.IsSelected = false;
		}
	}

	private void BoneTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (_syncingRig) return;
		var tag = (e.NewValue as TreeViewItem)?.Tag;
		if (tag is Bone or Slot && _state.IsPicked(tag))
		{
			_state.Pick(_state.Picked, tag);   // one of the picked: they all stay picked (a drag takes them along)
			ShowProperties();
			UpdateStatus();
			return;
		}
		switch (tag)
		{
			case Slot slot:
				_state.SelectedSlot = slot;
				_state.Selected = slot.Bone;
				break;
			case Bone bone:
				_state.SelectedSlot = null;
				_state.Selected = bone;
				break;
			default:
				_state.SelectedSlot = null;
				_state.Selected = null;
				break;
		}
		ShowProperties();
		UpdateStatus();
	}

	// Called every frame
	private void SyncRigSelection()
	{
		if (!_sceneLevel) SyncTreePicks();   // picked in the viewport: the rows follow
		if (Equals(CurrentSelection, _shownSelection))
		{
			if (_state.SelectedSlot != null) RefreshPartValues();
			else RefreshBoneValues();
			return;
		}

		_syncingRig = true;
		SelectTreeItem();
		_syncingRig = false;
		ShowProperties();
	}

	// ======================= which panel =======================

	private void ShowProperties()
	{
		_shownSelection = CurrentSelection;
		bool part = _state.SelectedSlot != null;

		bool drawn = IsDrawn;

		PartProps.Visibility = part ? Visibility.Visible : Visibility.Collapsed;
		BoneProps.Visibility = part || drawn ? Visibility.Collapsed : Visibility.Visible;
		DrawnHint.Visibility = !part && drawn ? Visibility.Visible : Visibility.Collapsed;
		DeleteButton.Content = part || drawn ? "Delete" : "Delete ▾";   // a bone: what happens to what's on it
		DeleteButton.IsEnabled = part || (_state.Selected != null && !drawn);

		if (part) ShowPartProperties();
		else ShowBoneProperties();
	}

	private void ShowRigHint(string text)
	{
		RigHint.Text = text;
		PartHint.Text = text;
	}

	// ======================= bone properties =======================

	private void ShowBoneProperties()
	{
		_shownBone = _state.Selected;
		var bone = _shownBone;
		var look = LookOf(bone);
		var ik = IkEndingAt(bone);
		var inChain = IkContaining(bone);
		_syncingRig = true;

		BoneProps.IsEnabled = bone != null;

		SetField(BoneName, bone?.Name ?? "");
		var shade = bone != null ? SkeletonRenderer.BoneColor(bone) : SkeletonRenderer.DefaultBoneColor;
		BoneColorSwatch.Background = new SolidColorBrush(Color.FromRgb(shade.Red, shade.Green, shade.Blue));
		BoneInheritRotation.IsChecked = bone?.InheritRotation ?? true;
		BoneInheritScale.IsChecked = bone?.InheritScale ?? true;
		BoneConnected.IsChecked = bone is { Connected: true, Parent: not null };
		BoneConnected.IsEnabled = bone?.Parent != null;   // a root bone has no parent's end

		var parents = BoneChoices(bone, "(none: root bone)");
		BoneParent.ItemsSource = parents;
		BoneParent.SelectedItem = parents.FirstOrDefault(c => c.Bone == bone?.Parent);

		var targets = BoneChoices(bone, "(none)");
		LookTarget.ItemsSource = targets;
		LookTarget.SelectedItem = targets.FirstOrDefault(c => c.Bone == look?.Target);
		LookShapeBox.SelectedItem = look?.Shape ?? LookShape.Ellipse;
		LookShapeBox.IsEnabled = LookFollow.IsEnabled = LookRangeX.IsEnabled = LookRangeY.IsEnabled = look != null;

		// IK: chain lengths up to how many bones are above this one (including itself)
		int depth = 0;
		for (var b = bone; b != null; b = b.Parent) depth++;
		var lengths = new List<IkChoice> { new(0, "None") };
		for (int n = 1; n <= Math.Min(depth, 6); n++)
			lengths.Add(new IkChoice(n, n == 1 ? "1 bone (aim)" : $"{n} bones"));
		IkLength.ItemsSource = lengths;
		IkLength.SelectedItem = lengths.FirstOrDefault(c => c.Length == (ik?.Bones.Count ?? 0));
		IkLength.IsEnabled = ik != null || inChain == null;   // a middle bone of another chain can't start its own

		var ikTargets = new List<BoneChoice>();
		if (ik != null)
			ikTargets.AddRange(_skeleton.AllBones()
				.Where(b => !ik.Bones.Contains(b) && !ik.Bones.Any(c => b.IsDescendantOf(c)))
				.Select(b => new BoneChoice(b, b.Name)));
		IkTargetBox.ItemsSource = ikTargets;
		IkTargetBox.SelectedItem = ikTargets.FirstOrDefault(c => c.Bone == ik?.Target);
		IkTargetBox.IsEnabled = IkOn.IsEnabled = IkFlip.IsEnabled = ik != null;
		IkOn.IsChecked = ik != null && ik.SetupMix > 0.5f;
		IkFlip.IsChecked = ik != null && ik.BendDirection < 0;

		RigHint.Text = inChain != null && ik == null
			? $"Part of IK chain \"{inChain.Name}\". Select its last bone ({inChain.Bones[^1].Name}) to edit it."
			: "";

		ShowSpaceChoices(bone);

		_syncingRig = false;
		RefreshBoneValues();
	}

	private List<BoneChoice> BoneChoices(Bone? bone, string noneLabel)
	{
		var list = new List<BoneChoice> { new(null, noneLabel) };
		if (bone != null)
			list.AddRange(_skeleton.AllBones()
				.Where(b => b != bone && !b.IsDescendantOf(bone))
				.Select(b => new BoneChoice(b, b.Name)));
		return list;
	}

	private void RefreshBoneValues()
	{
		var bone = _shownBone;
		bool pose = _state.AnimateMode;

		PropsTitle.Text = bone == null ? "No bone selected"
			: pose ? (_state.AutoKey ? $"Pose at frame {_state.Frame}: typing sets keys" : $"Pose at frame {_state.Frame}: typing poses it ({KeyOf("anim.key")} keys it)")
			: "Rest pose (setup)";

		foreach (var box in new[] { BoneX, BoneY, BoneRotation, BoneLength, BoneScaleX, BoneScaleY,
									BoneTurn, BoneTilt, LookFollow, LookRangeX, LookRangeY })
		{
			string field = (string)box.Tag;
			bool usePose = pose && PoseFields.Contains(field);
			SetField(box, bone != null ? Num(FieldValue(bone, field, usePose)) : "");
		}

		SyncSpaceChoice();   // what the bone follows can change from frame to frame
	}

	private float? FieldValue(Bone bone, string field, bool pose)
	{
		var look = LookOf(bone);
		return field switch
		{
			"X" => pose ? bone.Position.X : bone.SetupPosition.X,
			"Y" => pose ? bone.Position.Y : bone.SetupPosition.Y,
			"Rotation" => Deg(pose ? bone.Rotation : bone.SetupRotation),
			"Length" => bone.Length,
			"ScaleX" => pose ? bone.Scale.X : bone.SetupScale.X,
			"ScaleY" => pose ? bone.Scale.Y : bone.SetupScale.Y,
			"Turn" => Deg(pose ? bone.Turn : bone.SetupTurn),
			"Tilt" => Deg(pose ? bone.Tilt : bone.SetupTilt),
			"Follow" => look?.Follow,
			"RangeX" => look?.Radius.X,
			"RangeY" => look?.Radius.Y,
			_ => null
		};
	}

	private void BoneField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitBoneField((TextBox)sender);
		Viewport.Focus();
	}

	private void BoneField_LostFocus(object sender, RoutedEventArgs e) => CommitBoneField((TextBox)sender);

	private void CommitBoneField(TextBox box)
	{
		if (_syncingRig || _shownBone is not { } bone) return;
		string field = (string)box.Tag;
		RigHint.Text = "";

		if (field == "Name")
		{
			string newName = box.Text.Trim();
			if (newName == bone.Name) return;
			string oldName = bone.Name;
			bool renamed = EditStructureAndScene(() =>
			{
				if (!RigOps.RenameBone(_skeleton, _state.Animations, bone, newName)) return null;
				if (_active != null) SceneOps.RenameHolderBone(_scene, _active, oldName, newName);   // attached objects follow
				return $"Rename bone to {newName}";
			});
			if (!renamed)
			{
				RigHint.Text = $"Can't rename to \"{newName}\": names must be unique and not empty.";
				box.Text = bone.Name;
			}
			return;
		}

		bool pose = _state.AnimateMode && PoseFields.Contains(field);
		var current = FieldValue(bone, field, pose);
		if (current == null || box.Text.Trim() == Num(current)) return;

		if (!TryNum(box.Text, out float v))
		{
			RigHint.Text = $"\"{box.Text}\" isn't a number.";
			box.Text = Num(current);
			return;
		}

		if (pose) KeyField(bone, field, v);
		else ApplySetupField(bone, field, v);
	}

	private void ApplySetupField(Bone bone, string field, float v)
	{
		var look = LookOf(bone);
		_state.EditStructure(() =>
		{
			// Parts stay put: the place, angle and length change, nothing else moves on screen
			if (_tool.KeepPartsInPlace && field is "X" or "Y" or "Rotation" or "Length")
			{
				if (field is "X" or "Y") RigOps.SetConnected(bone, false);   // typing a place frees it from the parent's end
				var frozen = FrozenRest.Capture(_skeleton);
				switch (field)
				{
					case "X" or "Y":
						var local = field == "X" ? new Vector2(v, bone.SetupPosition.Y) : new Vector2(bone.SetupPosition.X, v);
						var target = bone.Parent is { } parent ? Vector2.Transform(local, parent.World) : local;
						RestEdits.MoveJoint(frozen, bone, target);
						break;
					case "Rotation":
						RestEdits.Turn(frozen, bone, Rad(v));
						break;
					default:
						RestEdits.Stretch(frozen, bone, v);
						break;
				}
				return $"Set {bone.Name} {field} (parts stay put)";
			}

			switch (field)
			{
				case "X":
					RigOps.SetConnected(bone, false);   // typing a place frees it from the parent's end
					bone.SetupPosition = new Vector2(v, bone.SetupPosition.Y);
					break;
				case "Y":
					RigOps.SetConnected(bone, false);
					bone.SetupPosition = new Vector2(bone.SetupPosition.X, v);
					break;
				case "Rotation": bone.SetupRotation = Rad(v); break;
				case "Length": bone.Length = MathF.Max(0, v); break;
				case "ScaleX": bone.SetupScale = new Vector2(v, bone.SetupScale.Y); break;
				case "ScaleY": bone.SetupScale = new Vector2(bone.SetupScale.X, v); break;
				case "Turn": bone.SetupTurn = Rad(v); break;
				case "Tilt": bone.SetupTilt = Rad(v); break;
				case "Follow" when look != null: look.Follow = v; break;
				case "RangeX" when look != null: look.Radius = new Vector2(MathF.Max(0, v), look.Radius.Y); break;
				case "RangeY" when look != null: look.Radius = new Vector2(look.Radius.X, MathF.Max(0, v)); break;
				default: return null;
			}
			return $"Set {bone.Name} {field}";
		});
	}

	private void KeyField(Bone bone, string field, float v)
	{
		// Auto key off: the pose changes, no key yet
		if (HoldInsteadOfKey(() =>
		{
			switch (field)
			{
				case "X": bone.Position = new Vector2(v, bone.Position.Y); break;
				case "Y": bone.Position = new Vector2(bone.Position.X, v); break;
				case "Rotation": bone.Rotation = Rad(v); break;
				case "ScaleX": bone.Scale = new Vector2(v, bone.Scale.Y); break;
				case "ScaleY": bone.Scale = new Vector2(bone.Scale.X, v); break;
				case "Turn": bone.Turn = Rad(v); break;
				case "Tilt": bone.Tilt = Rad(v); break;
			}
		})) return;

		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		var anim = _state.Animation;
		float t = _state.Time;
		int frame = _state.Frame;

		_state.EditStructure(() =>
		{
			switch (field)
			{
				case "X":
					anim.GetOrCreate(bone.Name, n => new TranslateTimeline(n))
						.Track.Add(t, new Vector2(v, bone.Position.Y) - bone.SetupPosition);
					break;
				case "Y":
					anim.GetOrCreate(bone.Name, n => new TranslateTimeline(n))
						.Track.Add(t, new Vector2(bone.Position.X, v) - bone.SetupPosition);
					break;
				case "Rotation":
					anim.GetOrCreate(bone.Name, n => new RotateTimeline(n))
						.Track.Add(t, Rad(v) - bone.SetupRotation);
					break;
				case "ScaleX":
					anim.GetOrCreate(bone.Name, n => new ScaleTimeline(n))
						.Track.Add(t, new Vector2(Ratio(v, bone.SetupScale.X), Ratio(bone.Scale.Y, bone.SetupScale.Y)));
					break;
				case "ScaleY":
					anim.GetOrCreate(bone.Name, n => new ScaleTimeline(n))
						.Track.Add(t, new Vector2(Ratio(bone.Scale.X, bone.SetupScale.X), Ratio(v, bone.SetupScale.Y)));
					break;
				case "Turn":
					anim.GetOrCreate(bone.Name, n => new DepthTimeline(n))
						.Track.Add(t, new Vector2(bone.Tilt - bone.SetupTilt, Rad(v) - bone.SetupTurn));
					break;
				case "Tilt":
					anim.GetOrCreate(bone.Name, n => new DepthTimeline(n))
						.Track.Add(t, new Vector2(Rad(v) - bone.SetupTilt, bone.Turn - bone.SetupTurn));
					break;
				default:
					return null;
			}
			return $"Key {bone.Name} {field} @ frame {frame}";
		});
	}

	private void BoneConnected_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingRig || _shownBone is not { Parent: { } parent } bone) return;
		bool connected = BoneConnected.IsChecked == true;
		_state.EditStructure(() => RigOps.SetConnected(bone, connected)
			? (connected ? $"Snap {bone.Name} to the end of {parent.Name}" : $"Free {bone.Name} from the end of {parent.Name}")
			: null);
	}

	private void SnapJoints_Click(object sender, RoutedEventArgs e) => _tool.SnapJoints = SnapJointsBox.IsChecked == true;

	private void BoneInherit_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingRig || _shownBone is not { } bone) return;
		bool rotation = BoneInheritRotation.IsChecked == true;
		bool scale = BoneInheritScale.IsChecked == true;
		_state.EditStructure(() =>
		{
			bone.InheritRotation = rotation;
			bone.InheritScale = scale;
			return $"Inheritance of {bone.Name}";
		});
	}

	private void BoneParent_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownBone is not { } bone || BoneParent.SelectedItem is not BoneChoice choice) return;
		_state.EditStructure(() =>
			HangBone(bone, choice.Bone) ? $"Parent {bone.Name} to {choice.Label}" : null);
	}

	private void LookTarget_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownBone is not { } bone || LookTarget.SelectedItem is not BoneChoice choice) return;
		_state.EditStructure(() => RigOps.SetLookTarget(_skeleton, bone, choice.Bone)
			? (choice.Bone == null ? $"Remove look from {bone.Name}" : $"{bone.Name} looks at {choice.Bone.Name}")
			: null);
	}

	private void LookShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || LookOf(_shownBone) is not { } look || LookShapeBox.SelectedItem is not LookShape shape) return;
		if (look.Shape == shape) return;
		_state.EditStructure(() =>
		{
			look.Shape = shape;
			return $"Look shape: {shape}";
		});
	}

	// ======================= buttons =======================

	private void AddBone_Click(object sender, RoutedEventArgs e)
	{
		var parent = _state.Selected;
		_state.EditStructure(() =>
		{
			var position = parent == null ? Vector2.Zero : new Vector2(parent.Length, 0);
			var bone = RigOps.AddBone(_skeleton, parent, "bone", position, 0f, 50f);
			if (parent != null && _tool.SnapJoints) bone.Connected = true;   // on the parent's end, and it stays there
			_state.Selected = bone;
			_state.SelectedSlot = null;
			return $"Add bone {bone.Name}";
		});
	}

	// A part goes at once; for a bone, a menu: keep what's on it, or delete that too
	private void Delete_Click(object sender, RoutedEventArgs e)
	{
		if (_state.SelectedSlot is { } slot)
		{
			DeletePart(slot);
			return;
		}
		if (IsDrawn || _state.Selected is not { } bone) return;   // a drawn object's one bone stays

		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		if (bone.Parent is { } parent)
		{
			var keep = new MenuItem { Header = $"Delete \"{bone.Name}\": its parts and bones go to \"{parent.Name}\"" };
			keep.Click += (_, _) => DissolveSelectedBone();
			menu.Items.Add(keep);
		}
		var all = new MenuItem { Header = $"Delete \"{bone.Name}\" with everything on it" };
		all.Click += (_, _) => DeleteSelectedBone();
		menu.Items.Add(all);
		menu.IsOpen = true;
	}

	// Deletes the bone but not what's on it: its parts and child bones hang from its parent now,
	// staying exactly where they are
	private void DissolveSelectedBone()
	{
		if (_state.Selected is not { Parent: { } parent } bone) return;
		_state.SelectedKeys.Clear();
		EditStructureAndScene(() =>
		{
			if (_active != null) SceneOps.ReleaseFromBones(_scene, _active, new HashSet<string> { bone.Name });   // objects it carried stay put
			if (!RigOps.DissolveBone(_skeleton, _state.Animations, bone)) return null;
			_state.Selected = parent;
			return $"Delete bone {bone.Name} (what was on it went to {parent.Name})";
		});
	}

	private void DeleteSelectedBone()
	{
		if (_state.Selected is not { } bone) return;
		var parent = bone.Parent;
		_state.SelectedKeys.Clear();
		ClosePaintEditor();   // it may be showing a part that's about to disappear
		EditStructureAndScene(() =>
		{
			// Objects carried by these bones let go first, staying where they are
			var doomed = _skeleton.AllBones().Where(b => b == bone || b.IsDescendantOf(bone)).Select(b => b.Name).ToHashSet();
			if (_active != null) SceneOps.ReleaseFromBones(_scene, _active, doomed);
			RigOps.DeleteBone(_skeleton, _state.Animations, bone);
			_state.Selected = parent;
			return $"Delete bone {bone.Name}";
		});
	}

	// ======================= helpers =======================

	private static void SetField(TextBox box, string text)
	{
		if (!box.IsKeyboardFocused && box.Text != text) box.Text = text;
	}

	private static string Num(float? v) => v is { } f ? f.ToString("0.##", CultureInfo.CurrentCulture) : "";

	private static bool TryNum(string text, out float value) =>
		float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
		float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	private static float Deg(float radians) => radians * 180f / MathF.PI;
	private static float Rad(float degrees) => degrees * MathF.PI / 180f;
	private static float Ratio(float a, float b) => MathF.Abs(b) < 1e-6f ? a : a / b;
	private sealed record IkChoice(int Length, string Label)
	{
		public override string ToString() => Label;
	}

	// The IK constraint whose chain ENDS at this bone (that's where it's edited)
	private IkConstraint? IkEndingAt(Bone? bone) =>
		bone == null ? null : _skeleton.IkConstraints.Find(c => c.Bones.Count > 0 && c.Bones[^1] == bone);

	// Any IK constraint this bone is part of
	private IkConstraint? IkContaining(Bone? bone) =>
		bone == null ? null : _skeleton.IkConstraints.Find(c => c.Bones.Contains(bone));

	private void IkLength_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownBone is not { } bone || IkLength.SelectedItem is not IkChoice choice) return;
		var ik = IkEndingAt(bone);
		if (choice.Length == (ik?.Bones.Count ?? 0)) return;

		bool done = _state.EditStructure(() =>
		{
			if (ik == null)
				return RigOps.CreateIk(_skeleton, bone, choice.Length) is { } created
					? $"Create IK {created.Name}" : null;

			if (choice.Length == 0)
			{
				RigOps.DeleteIk(_skeleton, _state.Animations, ik);
				return $"Remove IK {ik.Name}";
			}

			return RigOps.SetIkLength(_skeleton, ik, choice.Length) ? $"IK {ik.Name}: {choice.Length} bones" : null;
		});

		if (!done)
		{
			ShowBoneProperties();   // put the dropdown back to what's really there
			RigHint.Text = "Can't make that chain: some of those bones are already in another IK chain, " +
						   "or the target would move along with the chain.";
		}
	}

	private void IkTarget_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || IkEndingAt(_shownBone) is not { } ik) return;
		if (IkTargetBox.SelectedItem is not BoneChoice { Bone: { } target }) return;
		_state.EditStructure(() => RigOps.SetIkTarget(ik, target) ? $"{ik.Name} targets {target.Name}" : null);
	}

	private void IkOptions_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingRig || IkEndingAt(_shownBone) is not { } ik) return;
		float mix = IkOn.IsChecked == true ? 1f : 0f;
		int bend = IkFlip.IsChecked == true ? -1 : 1;
		if (mix == ik.SetupMix && bend == ik.BendDirection) return;
		_state.EditStructure(() =>
		{
			ik.SetupMix = mix;
			ik.BendDirection = bend;
			return $"{ik.Name} settings";
		});
	}
}