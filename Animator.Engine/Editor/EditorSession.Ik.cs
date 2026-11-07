namespace Animator.Viewer;

// IK, space switches and throws (EditorSession's side of the IK keys and the bone panel's Space
// and Throw): the IK constraint picked, IK matched to FK or baked into it, switched at the
// playhead, turned on and off at rest, its bend flipped; a bone made to follow another from a
// frame on; a bone thrown, its flight baked into keys. Each is one undo step, and says whether it
// changed anything. Keying ones stop playback and work exactly on the playhead's frame.
public sealed partial class EditorSession
{
	// The constraint the selected bone belongs to (as target or chain), else the first one
	public IkConstraint? ActiveIk()
	{
		var selected = State.Selected;
		return Rig.IkConstraints.Find(c => selected != null && (c.Target == selected || c.Bones.Contains(selected)))
			?? Rig.IkConstraints.FirstOrDefault();
	}

	// An edit that may touch any bone's rest pose and the animation, as one undo step
	private bool EditRig(string name, Func<bool> change)
	{
		var before = RigSnapshot.Capture(Rig, State.Animation);
		if (!change())
		{
			before.Restore();
			return false;
		}
		State.Undo.Push(new SnapshotCommand<RigSnapshot>(name, before, RigSnapshot.Capture(Rig, State.Animation), s => s.Restore()));
		return true;
	}

	// The playhead stopped exactly on its frame; that frame
	private int StopOnFrame()
	{
		State.Playing = false;
		State.SetFrame(State.Frame);
		return State.Frame;
	}

	// IK's target snapped to the chain's tip (bake false), or the IK pose baked into FK: keyed at the
	// playhead (Animate) or at rest
	public bool MatchIk(bool bake)
	{
		if (ActiveIk() is not { } c) return false;
		StopOnFrame();
		bool key = State.AnimateMode;
		float time = State.Time;
		return bake
			? EditRig($"Bake {c.Name} IK into FK", () => { IkMatching.BakeIkToFk(Rig, State.Animation, c, time, key); return true; })
			: EditRig($"Snap {c.Target.Name} to {c.Bones[^1].Name}", () => { IkMatching.SnapTargetToTip(Rig, State.Animation, c, time, key); return true; });
	}

	// Animate: the IK switched to the other mode (IK ↔ FK) from the playhead on, the pose kept
	public bool SwitchIkHere()
	{
		if (ActiveIk() is not { } c || !State.AnimateMode) return false;   // a keyed switch only makes sense when animating
		int frame = StopOnFrame();
		IkMatching.Evaluate(Rig, State.Animation, State.Time);   // which mode it's in at this frame
		bool toIk = c.Mix < 0.5f;
		return EditRig($"Switch {c.Name} to {(toIk ? "IK" : "FK")} @ frame {frame}",
			() => { IkMatching.SwitchMode(Rig, State.Animation, c, frame, State.Fps, toIk); return true; });
	}

	// The IK on or off at rest
	public bool ToggleIkMode()
	{
		if (ActiveIk() is not { } ik) return false;
		float before = ik.SetupMix;
		float after = before > 0.5f ? 0f : 1f;
		ik.SetupMix = after;
		State.Undo.Push(new SnapshotCommand<float>($"{ik.Name}: {(after > 0.5f ? "IK" : "FK")}", before, after, v => ik.SetupMix = v));
		return true;
	}

	// The chain bends the other way
	public bool FlipIkBend()
	{
		if (ActiveIk() is not { } ik) return false;
		int before = ik.BendDirection;
		ik.BendDirection = -before;
		State.Undo.Push(new SnapshotCommand<int>($"Flip {ik.Name} bend", before, -before, v => ik.BendDirection = v));
		return true;
	}

	// Animate: the bone follows another (space: its name; null or "": nothing, the world) from the
	// playhead on, staying where it is. False: in Setup, or the holder follows the bone here (a loop).
	public bool SwitchSpace(Bone bone, string? space, string label)
	{
		if (!State.AnimateMode) return false;
		int frame = StopOnFrame();
		return State.EditStructure(() =>
			SpaceSwitching.Switch(Rig, State.Animation, bone, frame, State.Fps, space) ? $"{bone.Name} follows {label} @ frame {frame}" : null);
	}

	// Animate: the bone thrown from the playhead, its flight (arc, landing, bounces) baked into keys.
	// False: in Setup, no frames after this one, or nothing to bake before the end (or the next attach).
	public bool Throw(Bone bone, ThrowSettings settings)
	{
		if (!State.AnimateMode) return false;
		int frame = StopOnFrame();
		if (frame >= State.FrameCount) return false;
		return State.EditStructure(() =>
			ThrowBake.Bake(Rig, State.Animation, bone, frame, State.Fps, settings) ? $"Throw {bone.Name} @ frame {frame}" : null);
	}
}
