using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The bone panel's constraints, as the Windows editor has them (Rig, Constraints, Space):
// - look at: the bone (an iris, say) moves toward another one, inside a boundary
// - IK: the bone as the end of a chain (2 bones = an arm or a leg, 1 = aim), its target, on or
//   off, which way the elbow bends; and the IK keys (I, Shift+I, T, Shift+T, B)
// - follows: what the bone follows from the playhead on (Animate): its parent, nothing
//   (detached), or another bone holding it; and the throw / fall helper
// - follow and jiggle: a copy constraint (another bone's turn, place or size) and a jiggle one
//   (it lags behind and springs back)
// The edits are EditorSession's; a slider dragged is one undo step.
public sealed partial class MainWindow
{
	private sealed record IkChoice(int Length, string Label)
	{
		public override string ToString() => Label;
	}

	private sealed record SpaceChoice(string? Space, string Label)
	{
		public override string ToString() => Label;
	}

	private sealed record SourceChoice(Bone? Bone, string Label)
	{
		public override string ToString() => Label;
	}

	internal readonly ComboBox LookTarget = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly ComboBox LookShapeBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly ComboBox IkLength = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly ComboBox IkTarget = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly CheckBox IkOn = new() { Content = "IK on" };
	internal readonly CheckBox IkFlip = new() { Content = "Flip the elbow" };
	internal readonly ComboBox SpaceBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly Button _throw = new() { Content = "Throw / fall…" };
	internal readonly TextBlock BoneHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };

	internal readonly ComboBox CopySource = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly CheckBox CopyLocal = new() { Content = "From its own rest (local)" };
	private readonly Button _copyKeep = new() { Content = "Keep it where it is" };
	internal readonly CheckBox JiggleOn = new() { Content = "Jiggle" };
	internal readonly Dictionary<ConstraintValue, Slider> ConstraintSliders = new();
	private readonly Dictionary<ConstraintValue, TextBlock> _sliderTexts = new();
	internal readonly TextBlock ConstraintHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	private readonly Border _constraintBox = new() { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
	private StructureSnapshot? _constraintDrag;   // before a slider drag
	private ThrowSettings _lastThrow = new();     // the throw dialog remembers the last settings

	// What the throw dialog is answered with (the tests answer it); null: the user's answer
	internal Func<ThrowWindow, Task<bool>>? AnswerThrow { get; set; }

	private Bone? ShownBone => _shownInProps as Bone;

	// The rows under the bone's numbers
	private IEnumerable<Control> BuildConstraintRows(Func<string, Control, Control> labelled)
	{
		LookShapeBox.ItemsSource = Enum.GetValues<LookShape>();
		ToolTip.SetTip(LookTarget, "Makes this bone (an iris, say) move toward the chosen bone, inside a boundary");
		ToolTip.SetTip(IkLength, "Make this bone the END of an IK chain (hand, foot, tail tip). 2 bones = arm or leg, 1 = aim.");
		ToolTip.SetTip(IkOn, $"Off: the chain plays its FK keys ({_keys.Describe("ik.toggle")})");
		ToolTip.SetTip(IkFlip, $"Which way the middle joint (elbow, knee) bends ({_keys.Describe("ik.flip")})");
		ToolTip.SetTip(SpaceBox, "What this bone follows from the current frame on (Animate). Detach it to drop or throw it, or attach it to another bone to be held.");
		ToolTip.SetTip(_throw, "Detaches the bone here and bakes a thrown or falling flight into keys (Animate)");

		LookTarget.SelectionChanged += (_, _) =>
		{
			if (!_syncingProps && ShownBone is { } bone && LookTarget.SelectedItem is BoneChoice choice) _session.SetLookTarget(bone, choice.Bone);
		};
		LookShapeBox.SelectionChanged += (_, _) =>
		{
			if (!_syncingProps && ShownBone is { } bone && LookShapeBox.SelectedItem is LookShape shape) _session.SetLookShape(bone, shape);
		};
		IkLength.SelectionChanged += (_, _) =>
		{
			if (_syncingProps || ShownBone is not { } bone || IkLength.SelectedItem is not IkChoice choice) return;
			if (choice.Length == (_session.IkEndingAt(bone)?.Bones.Count ?? 0)) return;
			if (!_session.SetIkLength(bone, choice.Length))
			{
				RefillProperties();   // (back to what's really there)
				BoneHint.Text = "Can't make that chain: some of those bones are already in another IK chain, or the target would move along with the chain.";
			}
		};
		IkTarget.SelectionChanged += (_, _) =>
		{
			if (!_syncingProps && _session.IkEndingAt(ShownBone) is { } ik && IkTarget.SelectedItem is BoneChoice { Bone: { } target }) _session.SetIkTarget(ik, target);
		};
		void IkOptions()
		{
			if (!_syncingProps && _session.IkEndingAt(ShownBone) is { } ik) _session.SetIkOptions(ik, IkOn.IsChecked == true, IkFlip.IsChecked == true);
		}
		IkOn.IsCheckedChanged += (_, _) => IkOptions();
		IkFlip.IsCheckedChanged += (_, _) => IkOptions();
		SpaceBox.SelectionChanged += (_, _) => SpaceChosen();
		_throw.Click += (_, _) => _ = ThrowShown();

		var throwRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(78, 2, 0, 2), Children = { _throw } };
		return new Control[]
		{
			labelled("Look at", LookTarget),
			labelled("Its shape", LookShapeBox),
			labelled("IK chain", IkLength),
			labelled("IK target", IkTarget),
			new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(78, 2, 0, 2), Children = { IkOn, IkFlip } },
			labelled("Follows", SpaceBox),
			throwRow,
			BoneHint,
			BuildConstraintBox(labelled)
		};
	}

	// ======================= filled in for a bone =======================

	private void FillConstraintRows(Bone bone)
	{
		BoneHint.Text = "";
		var look = _session.LookOf(bone);
		var targets = new List<BoneChoice> { new(null, "(none)") };
		targets.AddRange(State.Skeleton.AllBones().Where(b => b != bone).Select(b => new BoneChoice(b, b.Name)));
		LookTarget.ItemsSource = targets;
		LookTarget.SelectedItem = targets.FirstOrDefault(c => c.Bone == look?.Target);
		LookShapeBox.SelectedItem = look?.Shape ?? LookShape.Ellipse;
		LookShapeBox.IsEnabled = look != null;

		// IK: chain lengths up to how many bones are above this one (itself included)
		var ik = _session.IkEndingAt(bone);
		var inChain = _session.IkContaining(bone);
		int depth = 0;
		for (var b = bone; b != null; b = b.Parent) depth++;
		var lengths = new List<IkChoice> { new(0, "None") };
		for (int n = 1; n <= Math.Min(depth, 6); n++) lengths.Add(new IkChoice(n, n == 1 ? "1 bone (aim)" : $"{n} bones"));
		IkLength.ItemsSource = lengths;
		IkLength.SelectedItem = lengths.FirstOrDefault(c => c.Length == (ik?.Bones.Count ?? 0));
		IkLength.IsEnabled = ik != null || inChain == null;   // (a middle bone of another chain can't start its own)
		var ikTargets = new List<BoneChoice>();
		if (ik != null)
			ikTargets.AddRange(State.Skeleton.AllBones()
				.Where(b => !ik.Bones.Contains(b) && !ik.Bones.Any(c => b.IsDescendantOf(c)))
				.Select(b => new BoneChoice(b, b.Name)));
		IkTarget.ItemsSource = ikTargets;
		IkTarget.SelectedItem = ikTargets.FirstOrDefault(c => c.Bone == ik?.Target);
		IkTarget.IsEnabled = IkOn.IsEnabled = IkFlip.IsEnabled = ik != null;
		IkOn.IsChecked = ik != null && ik.SetupMix > 0.5f;
		IkFlip.IsChecked = ik != null && ik.BendDirection < 0;
		if (inChain != null && ik == null) BoneHint.Text = $"Part of IK chain \"{inChain.Name}\". Select its last bone ({inChain.Bones[^1].Name}) to edit it.";

		// What it can follow: its parent (or nothing, for a root), nothing, or another bone holding it
		var spaces = new List<SpaceChoice>();
		if (bone.Parent is { } parent)
		{
			spaces.Add(new SpaceChoice(null, $"its parent ({parent.Name})"));
			spaces.Add(new SpaceChoice(SpaceTimeline.World, "nothing (detached)"));
		}
		else spaces.Add(new SpaceChoice(null, "nothing (root bone)"));
		spaces.AddRange(State.Skeleton.AllBones()
			.Where(b => b != bone && b != bone.Parent && !b.IsDescendantOf(bone))
			.Select(b => new SpaceChoice(b.Name, $"held by {b.Name}")));
		SpaceBox.ItemsSource = spaces;

		var sources = new List<SourceChoice> { new(null, "(none)") };
		sources.AddRange(State.Skeleton.AllBones().Where(b => b != bone).Select(b => new SourceChoice(b, b.Name)));
		CopySource.ItemsSource = sources;
		_constraintsShownFor = null;
		SyncConstraintRows();
	}

	private object? _constraintsShownFor;   // (the constraint rows are filled in again when this changes)

	// Every frame: what follows what at the playhead, and the constraints' values
	private void SyncConstraintRows()
	{
		if (ShownBone is not { } bone) return;
		bool was = _syncingProps;
		_syncingProps = true;
		try
		{
			bool animate = State.AnimateMode;
			SpaceBox.IsEnabled = _throw.IsEnabled = animate;
			if (!SpaceBox.IsDropDownOpen && SpaceBox.ItemsSource is List<SpaceChoice> spaces)
			{
				string? current = SpaceSwitching.SpaceAt(State.Animation, bone, State.Time);
				var match = spaces.FirstOrDefault(c => c.Space == current);
				if (!Equals(SpaceBox.SelectedItem, match)) SpaceBox.SelectedItem = match;
			}

			var copy = _session.CopyOf(bone);
			var jiggle = _session.JiggleOf(bone);
			var key = (bone, copy, copy?.Source, copy?.Local, jiggle, State.Undo.NextUndoName);
			if (Equals(_constraintsShownFor, key) && _constraintDrag == null) return;
			_constraintsShownFor = key;
			_constraintBox.IsVisible = !IsDrawn;
			if (CopySource.ItemsSource is List<SourceChoice> sources)
				CopySource.SelectedItem = sources.FirstOrDefault(c => c.Bone == copy?.Source);
			CopyLocal.IsChecked = copy?.Local == true;
			CopyLocal.IsEnabled = copy != null;
			_copyKeep.IsEnabled = copy != null && !copy.Local;
			JiggleOn.IsChecked = jiggle != null;
			SetSlider(ConstraintValue.CopyRotate, copy?.RotateMix ?? 1f, copy != null);
			SetSlider(ConstraintValue.CopyMove, copy?.MoveMix ?? 0f, copy != null);
			SetSlider(ConstraintValue.CopyScale, copy?.ScaleMix ?? 0f, copy != null);
			SetSlider(ConstraintValue.JiggleStiffness, jiggle?.Stiffness ?? 80f, jiggle != null);
			SetSlider(ConstraintValue.JiggleDamping, jiggle?.Damping ?? 6f, jiggle != null);
			SetSlider(ConstraintValue.JiggleGravity, jiggle?.Gravity ?? 0f, jiggle != null);
			SetSlider(ConstraintValue.Mix, copy?.SetupMix ?? jiggle?.SetupMix ?? 1f, copy != null || jiggle != null);
			ConstraintHint.Text = (copy, jiggle) switch
			{
				(null, null) => "Pick a bone for it to follow, or let it jiggle.",
				_ => string.Join(" ", new[]
				{
					copy == null ? null : copy.Local ? $"It moves as {copy.Source.Name} moves." : $"It follows {copy.Source.Name} in the object.",
					jiggle == null ? null : $"It jiggles in Animate (play to see it swing{(State.AnimateMode ? "" : ": switch to Animate")})."
				}.Where(s => s != null))
			};
		}
		finally
		{
			_syncingProps = was;
		}
	}

	// ======================= follows: detach, attach, throw =======================

	private void SpaceChosen()
	{
		if (_syncingProps || !State.AnimateMode || ShownBone is not { } bone || SpaceBox.SelectedItem is not SpaceChoice choice) return;
		if (_session.SwitchSpace(bone, choice.Space, choice.Label)) return;   // (exactly on the playhead's frame)
		_constraintsShownFor = null;
		SyncConstraintRows();   // (back to what's really there)
		if (choice.Space is { Length: > 0 } holder)
			BoneHint.Text = $"{holder} can't hold {bone.Name} at this frame: {holder} itself follows {bone.Name} here, so they would go round in a loop.";
	}

	internal async Task ThrowShown()
	{
		if (ShownBone is not { } bone || !State.AnimateMode) return;
		State.Playing = false;
		State.SetFrame(State.Frame);
		int frame = State.Frame;
		if (frame >= State.FrameCount)
		{
			BoneHint.Text = "Move the playhead back a bit: the flight needs frames after this one to happen in.";
			return;
		}
		// The floor starts at the character's lowest point, as it is right now
		var dialog = new ThrowWindow(bone.Name, frame, _lastThrow with { GroundY = LowestPoint() });
		bool ok = AnswerThrow is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		_lastThrow = dialog.Settings;
		if (!_session.Throw(bone, dialog.Settings)) BoneHint.Text = "Nothing to bake: there are no frames between here and the end (or the next attach).";
	}

	// The lowest point of every part in the pose now (of the bones, with no parts)
	private float LowestPoint()
	{
		var skeleton = State.Skeleton;
		float lowest = float.MaxValue;
		foreach (var slot in skeleton.Slots)
			if (PartGizmo.For(skeleton, slot, _camera.EditZoom) is { } handles)
				foreach (var corner in handles.Corners)
					lowest = MathF.Min(lowest, corner.Y);
		if (lowest == float.MaxValue)
			foreach (var b in skeleton.AllBones())
				lowest = MathF.Min(lowest, MathF.Min(b.World.Translation.Y, b.TipWorld.Y));
		return lowest == float.MaxValue ? 0f : lowest;
	}

	// ======================= follow and jiggle =======================

	private Control BuildConstraintBox(Func<string, Control, Control> labelled)
	{
		ToolTip.SetTip(_constraintBox, "Constraints: follow another bone's turn, place or size; or lag behind and spring back (hair, tails)");
		ToolTip.SetTip(CopySource, "The bone it follows (none: it doesn't)");
		ToolTip.SetTip(CopyLocal, "On: it takes how the other bone turns, moves and grows from its own rest (a gear, a mirrored limb). Off: it takes the other's angle, place and size in the object, keeping the offsets it had.");
		ToolTip.SetTip(_copyKeep, "The offsets from the other bone become what they are right now");
		CopySource.SelectionChanged += (_, _) =>
		{
			if (_syncingProps || ShownBone is not { } bone || CopySource.SelectedItem is not SourceChoice choice) return;
			_session.SetCopySource(bone, choice.Bone);
		};
		CopyLocal.IsCheckedChanged += (_, _) =>
		{
			if (!_syncingProps && _session.CopyOf(ShownBone) is { } copy) _session.SetCopyLocal(copy, CopyLocal.IsChecked == true);
		};
		_copyKeep.Click += (_, _) => { if (_session.CopyOf(ShownBone) is { } copy) _session.KeepCopyPlace(copy); };
		JiggleOn.IsCheckedChanged += (_, _) =>
		{
			if (!_syncingProps && ShownBone is { } bone) _session.SetJiggle(bone, JiggleOn.IsChecked == true);
		};

		Control SliderRow(ConstraintValue which, string label, double min, double max, string tip)
		{
			var slider = new Slider { Minimum = min, Maximum = max, Width = 130, Tag = which };
			ToolTip.SetTip(slider, tip);
			var text = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center };
			ConstraintSliders[which] = slider;
			_sliderTexts[which] = text;
			slider.AddHandler(Thumb.DragStartedEvent, (_, _) => _constraintDrag ??= State.CaptureStructure());
			slider.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
			{
				if (_constraintDrag is not { } before) return;
				_constraintDrag = null;
				State.PushStructure(before, $"{ShownBone?.Name}: {EditorSession.NameOf(which)}");
				_constraintsShownFor = null;
			});
			slider.PropertyChanged += (_, e) =>
			{
				if (e.Property != RangeBase.ValueProperty || _syncingProps || ShownBone is not { } bone) return;
				float v = (float)slider.Value;
				if (_constraintDrag == null) _session.StepConstraintValue(bone, which, v);   // (a click: a step of its own)
				else _session.SetConstraintValue(bone, which, v);
				text.Text = Show(which, v);
			};
			return labelled(label, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { slider, text } });
		}
		_constraintBox.Child = new StackPanel
		{
			Spacing = 2,
			Children =
			{
				new TextBlock { Text = "Follow and jiggle", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
				labelled("Follows", CopySource),
				CopyLocal,
				SliderRow(ConstraintValue.CopyRotate, "Turn", 0, 1, "How much of the other bone's turn it takes"),
				SliderRow(ConstraintValue.CopyMove, "Place", 0, 1, "How much of the other bone's place it takes"),
				SliderRow(ConstraintValue.CopyScale, "Size", 0, 1, "How much of the other bone's size it takes"),
				_copyKeep,
				JiggleOn,
				SliderRow(ConstraintValue.JiggleStiffness, "Stiffness", 5, 400, "How hard it springs back (more: quicker, tighter)"),
				SliderRow(ConstraintValue.JiggleDamping, "Damping", 0, 30, "How quickly the swinging dies down"),
				SliderRow(ConstraintValue.JiggleGravity, "Gravity", -1000, 1000, "A pull downwards (hanging hair, a tail); below 0, upwards"),
				SliderRow(ConstraintValue.Mix, "Mix", 0, 1, "How much the constraints have their way (keyed in Animate)"),
				ConstraintHint
			}
		};
		return _constraintBox;
	}

	private static string Show(ConstraintValue which, float v) =>
		which is ConstraintValue.JiggleStiffness or ConstraintValue.JiggleGravity ? v.ToString("0", CultureInfo.CurrentCulture)
		: which == ConstraintValue.JiggleDamping ? v.ToString("0.#", CultureInfo.CurrentCulture)
		: v.ToString("P0", CultureInfo.CurrentCulture);

	private void SetSlider(ConstraintValue which, float value, bool enabled)
	{
		var slider = ConstraintSliders[which];
		slider.IsEnabled = enabled;
		if (_constraintDrag == null) slider.Value = value;
		_sliderTexts[which].Text = Show(which, value);
	}

	// ======================= the IK keys =======================

	private void DefineIkCommands()
	{
		Add("ik.toggle", "_IK / FK", () => { if (!SceneLevel) _session.ToggleIkMode(); });
		Add("ik.switch", "_Switch IK / FK at this frame", () => { if (!SceneLevel) _session.SwitchIkHere(); });
		Add("ik.snap", "Snap the IK _target to the hand", () => { if (!SceneLevel) _session.MatchIk(bake: false); });
		Add("ik.bake", "_Bake IK into FK", () => { if (!SceneLevel) _session.MatchIk(bake: true); });
		Add("ik.flip", "_Flip the elbow", () => { if (!SceneLevel) _session.FlipIkBend(); });
	}
}

// The throw / fall helper: how the bone flies from the playhead on (speed, spin, gravity, the
// floor, bounce, friction) and how often its flight is keyed
public sealed class ThrowWindow : Window
{
	internal readonly TextBox SpeedX = new() { Width = 80 }, SpeedY = new() { Width = 80 }, Spin = new() { Width = 80 };
	internal readonly TextBox Gravity = new() { Width = 80 }, Ground = new() { Width = 80 }, Bounce = new() { Width = 80 };
	internal readonly TextBox Friction = new() { Width = 80 }, KeyEvery = new() { Width = 80 };
	internal readonly TextBlock ErrorText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };

	public ThrowSettings Settings { get; private set; }

	public ThrowWindow(string boneName, int frame, ThrowSettings initial)
	{
		Title = "Throw / fall";
		SizeToContent = SizeToContent.WidthAndHeight;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Settings = initial;
		static string Num(float v) => v.ToString("0.##", CultureInfo.CurrentCulture);
		SpeedX.Text = Num(initial.Velocity.X);
		SpeedY.Text = Num(initial.Velocity.Y);
		Spin.Text = Num(initial.SpinDegrees);
		Gravity.Text = Num(initial.Gravity);
		Ground.Text = Num(initial.GroundY);
		Bounce.Text = Num(initial.Bounce);
		Friction.Text = Num(initial.Friction);
		KeyEvery.Text = initial.KeyEvery.ToString();

		static Control Row(string label, Control a, Control? b = null, string? note = null)
		{
			var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
			row.Children.Add(new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center });
			row.Children.Add(a);
			if (b != null) row.Children.Add(b);
			if (note != null) row.Children.Add(new TextBlock { Text = note, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
			return row;
		}
		var ok = new Button { Content = "Throw", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		Content = new StackPanel
		{
			Margin = new Thickness(14),
			Spacing = 6,
			Children =
			{
				new TextBlock { Text = $"Throw \"{boneName}\" from frame {frame}", FontWeight = FontWeight.SemiBold },
				Row("Speed X / Y", SpeedX, SpeedY, "per second (+Y: up)"),
				Row("Spin", Spin, note: "° per second (+: anticlockwise)"),
				Row("Gravity", Gravity, note: "pull down, per second²"),
				Row("Floor at", Ground, note: "(starts at the lowest point now)"),
				Row("Bounce", Bounce, note: "0 = thud … 1 = forever"),
				Row("Friction", Friction, note: "0 = slides … 1 = stops dead"),
				Row("Key every", KeyEvery, note: "frame(s)"),
				new TextBlock { Text = "It's detached at this frame, and the flight baked into keys you can change afterwards.", Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
				ErrorText,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
			}
		};
		Opened += (_, _) => { SpeedX.Focus(); SpeedX.SelectAll(); };
		UiSize.Fit(this);
	}

	// Numbers in the regional format ("12,5") or with a dot ("12.5")
	private bool Read(TextBox box, out float value)
	{
		string text = (box.Text ?? "").Trim();
		if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
			float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
			return true;
		ErrorText.Text = $"\"{text}\" isn't a number.";
		box.Focus();
		return false;
	}

	internal void TryOk()
	{
		if (!Read(SpeedX, out float sx) || !Read(SpeedY, out float sy) || !Read(Spin, out float spin) || !Read(Gravity, out float gravity) ||
			!Read(Ground, out float ground) || !Read(Bounce, out float bounce) || !Read(Friction, out float friction))
			return;
		if (!int.TryParse((KeyEvery.Text ?? "").Trim(), out int every) || every < 1 || every > 30)
		{
			ErrorText.Text = "\"Key every\" is a whole number of frames from 1 to 30.";
			return;
		}
		ErrorText.Text = "";
		Settings = new ThrowSettings
		{
			Velocity = new Vector2(sx, sy),
			SpinDegrees = spin,
			Gravity = gravity,
			GroundY = ground,
			Bounce = Math.Clamp(bounce, 0f, 1f),
			Friction = Math.Clamp(friction, 0f, 1f),
			KeyEvery = every
		};
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
