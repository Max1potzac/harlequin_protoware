namespace Animator.Viewer;

// A copy or jiggle constraint's numbers (the Rig panel's sliders)
public enum ConstraintValue { CopyRotate, CopyMove, CopyScale, JiggleStiffness, JiggleDamping, JiggleGravity, Mix }

// The open object's draw order, clipping masks and follow/jiggle constraints, edited
// (EditorSession's side of the layer list, its menu and drags, and the constraint controls).
// Each edit is one undo step and says whether it changed anything.
public sealed partial class EditorSession
{
	// ======================= the draw order =======================

	// The order the layer list shows and changes: the animation's at the playhead (Animate), or the rest order
	public IReadOnlyList<Slot> CurrentOrder()
	{
		if (!State.AnimateMode) return Rig.Slots;
		Rig.ResetDrawOrder();
		State.Animation.DrawOrder?.Apply(Rig, State.Time);
		return Rig.DrawOrder;
	}

	// Parts a step toward the front (+1) or the back (-1), or all the way (a step passes the
	// nearest part that shows: past a hidden one, nothing would change to see)
	public bool Reorder(IReadOnlyCollection<Slot> parts, int direction, bool toEnd)
	{
		if (parts.Count == 0) return false;
		var order = CurrentOrder().ToList();
		var moved = toEnd ? DrawOrderOps.ToEnd(order, parts, direction > 0)
			: DrawOrderOps.Stepped(order, parts, direction, slot => Rig.ResolveAttachment(slot) != null);
		string what = parts.Count == 1 ? parts.First().Name : $"{parts.Count} parts";
		string how = toEnd ? (direction > 0 ? "to the front" : "to the back") : (direction > 0 ? "forward" : "backward");
		return ApplyOrder(moved, parts, $"{what} {how}");
	}

	// Parts in front of or behind another, keeping their own order
	public bool PlaceInOrder(IReadOnlyCollection<Slot> moving, Slot target, bool inFront)
	{
		var order = CurrentOrder().ToList();
		var others = order.Where(s => !moving.Contains(s)).ToList();
		int index = others.IndexOf(target) + (inFront ? 1 : 0);
		string what = moving.Count == 1 ? moving.First().Name : $"{moving.Count} parts";
		return ApplyOrder(DrawOrderOps.Moved(order, moving, index), moving, $"{what} {(inFront ? "in front of" : "behind")} {target.Name}");
	}

	// The order becomes this one: the rest order, or (Animate mode) a key at the playhead. Keys
	// that drew the moved parts as the order did before get the change too, so it sticks: in
	// Animate mode the animation's later keys, up to one that draws them otherwise; in Setup mode
	// every animation's keys that drew them as the rest order did.
	public bool ApplyOrder(IReadOnlyList<Slot> order, IReadOnlyCollection<Slot> moving, string name)
	{
		bool keyed = State.AnimateMode;
		float time = State.Time;
		var setup = Rig.Slots.ToList();
		var passed = DrawOrderOps.Passed(CurrentOrder(), order, moving);
		return State.EditStructure(() =>
		{
			bool changed;
			if (keyed)
			{
				changed = DrawOrderOps.Key(State.Animation, time, order, setup);
				changed |= DrawOrderOps.CarryToKeys(State.Animation, passed, time, all: false);
			}
			else
			{
				changed = DrawOrderOps.SetSetupOrder(Rig, order);
				foreach (var animation in State.Animations)
					changed |= DrawOrderOps.CarryToKeys(animation, passed, 0, all: true);
			}
			return changed ? (keyed ? $"Key the draw order at frame {State.Frame}: {name}" : $"Draw order: {name}") : null;
		});
	}

	// ======================= clipping masks (Setup) =======================

	// The part clips the parts above it: all of them (until null), or up to one
	public bool MakeMask(Slot slot, Slot? until) =>
		!State.AnimateMode && State.EditStructure(() =>
		{
			MaskOps.MakeMask(Rig, slot, until, slot.MaskShows);
			return until == null ? $"{slot.Name}: clip every part above it" : $"{slot.Name}: clip the parts up to {until.Name}";
		});

	public bool SetMaskShows(Slot slot, bool shows) =>
		!State.AnimateMode && slot.IsMask && slot.MaskShows != shows && State.EditStructure(() =>
		{
			slot.MaskShows = shows;
			return shows ? $"{slot.Name}: show the mask too" : $"{slot.Name}: the mask doesn't show";
		});

	public bool StopMask(Slot slot) =>
		!State.AnimateMode && slot.IsMask && State.EditStructure(() =>
		{
			MaskOps.ClearMask(slot);
			return $"{slot.Name}: not a mask";
		});

	// ======================= follow and jiggle =======================

	public CopyConstraint? CopyOf(Bone? bone) => bone == null ? null : Rig.CopyConstraints.Find(c => c.Bone == bone);
	public JiggleConstraint? JiggleOf(Bone? bone) => bone == null ? null : Rig.JiggleConstraints.Find(c => c.Bone == bone);

	// A constraint edit as one undo step; the pose updated after
	public bool EditConstraints(string name, Func<bool> change)
	{
		if (SceneLevel) return false;
		bool changed = State.EditStructure(() => change() ? name : null);
		Rig.UpdatePose();
		return changed;
	}

	// The bone follows another (null: nothing), keeping where it is at rest
	public bool SetCopySource(Bone bone, Bone? source)
	{
		var copy = CopyOf(bone);
		if (source == copy?.Source) return false;
		if (source == null)
			return EditConstraints($"{bone.Name}: follow nothing", () => copy != null && Rig.CopyConstraints.Remove(copy));
		return EditConstraints($"{bone.Name}: follow {source.Name}", () =>
		{
			if (copy == null)
			{
				copy = new CopyConstraint { Name = $"{bone.Name}_follow", Bone = bone };
				Rig.CopyConstraints.Add(copy);
			}
			copy.Source = source;
			Rig.SetToSetupPose();   // (offsets: where it is at rest)
			Rig.UpdateWorld();
			copy.KeepOffsets();
			return true;
		});
	}

	// Follows how its source moves (local), or where it is
	public bool SetCopyLocal(CopyConstraint copy, bool local) =>
		copy.Local != local && EditConstraints(local ? $"{copy.Bone.Name}: follow how {copy.Source.Name} moves" : $"{copy.Bone.Name}: follow where {copy.Source.Name} is",
			() => { copy.Local = local; return true; });

	// Where it is at rest now becomes its place relative to its source
	public bool KeepCopyPlace(CopyConstraint copy) =>
		EditConstraints($"{copy.Bone.Name}: keep its place from {copy.Source.Name}", () =>
		{
			float mix = copy.Mix;
			Rig.UpdatePose();
			copy.Mix = 0f;   // (where it is without the constraint)
			Rig.SetToSetupPose();
			Rig.UpdateWorld();
			copy.KeepOffsets();
			copy.Mix = mix;
			return true;
		});

	public bool SetJiggle(Bone bone, bool on)
	{
		var jiggle = JiggleOf(bone);
		if (on && jiggle == null)
			return EditConstraints($"{bone.Name}: jiggle", () =>
			{
				Rig.JiggleConstraints.Add(new JiggleConstraint { Name = $"{bone.Name}_jiggle", Bone = bone });
				return true;
			});
		if (!on && jiggle != null) return EditConstraints($"{bone.Name}: no jiggle", () => Rig.JiggleConstraints.Remove(jiggle));
		return false;
	}

	public static string NameOf(ConstraintValue which) => which switch
	{
		ConstraintValue.CopyRotate => "follow its turn",
		ConstraintValue.CopyMove => "follow its place",
		ConstraintValue.CopyScale => "follow its size",
		ConstraintValue.JiggleStiffness => "jiggle stiffness",
		ConstraintValue.JiggleDamping => "jiggle damping",
		ConstraintValue.JiggleGravity => "jiggle gravity",
		_ => "constraint mix"
	};

	// A number set as it is (a slider being dragged: one undo step for the drag, from the window);
	// false: the bone has no constraint it belongs to
	public bool SetConstraintValue(Bone bone, ConstraintValue which, float v)
	{
		var copy = CopyOf(bone);
		var jiggle = JiggleOf(bone);
		switch (which)
		{
			case ConstraintValue.CopyRotate when copy != null: copy.RotateMix = v; return true;
			case ConstraintValue.CopyMove when copy != null: copy.MoveMix = v; return true;
			case ConstraintValue.CopyScale when copy != null: copy.ScaleMix = v; return true;
			case ConstraintValue.JiggleStiffness when jiggle != null: jiggle.Stiffness = v; return true;
			case ConstraintValue.JiggleDamping when jiggle != null: jiggle.Damping = v; return true;
			case ConstraintValue.JiggleGravity when jiggle != null: jiggle.Gravity = v; return true;
			case ConstraintValue.Mix when copy != null || jiggle != null:
				if (copy != null) copy.SetupMix = copy.Mix = v;
				if (jiggle != null) jiggle.SetupMix = jiggle.Mix = v;
				return true;
			default: return false;
		}
	}

	// ...or as an undo step of its own (a click on the slider)
	public bool StepConstraintValue(Bone bone, ConstraintValue which, float v)
	{
		var before = State.CaptureStructure();
		if (!SetConstraintValue(bone, which, v)) return false;
		State.PushStructure(before, $"{bone.Name}: {NameOf(which)}");
		return true;
	}
}
