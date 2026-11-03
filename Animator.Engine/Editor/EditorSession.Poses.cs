namespace Animator.Viewer;

// What a pose change came to: keys set (Animate), held unkeyed (auto key off), the rest pose
// changed (Setup), nothing changed, or nothing done (at scene level)
public enum PoseChange { Keyed, Held, Rest, Unchanged, None }

// Poses (EditorSession's side of the Poses panel and the pose shortcuts): saved poses put on
// (partly, mirrored, or on the picked alone), the pose mirrored, copied and pasted, a rig made
// symmetric, and the list of saved poses kept, updated, renamed and deleted; and the animation's
// events. Each edit is one undo step.
public sealed partial class EditorSession
{
	// Copy pose: what Paste puts on
	public SavedPose? PoseClipboard { get; private set; }

	// Changes the pose (`change` works on how the rig is posed now), then keys what changed at the
	// playhead (Animate mode; auto key off: holds it), or makes it the rest pose (Setup mode)
	public PoseChange PoseEdit(string name, Action change)
	{
		if (SceneLevel) return PoseChange.None;
		var animation = State.Animation;
		if (State.AnimateMode)
		{
			State.Playing = false;
			State.SetFrame(State.Frame);
			var before = PoseValues.Capture(Rig);
			change();
			var after = PoseValues.Capture(Rig);
			if (!State.AutoKey)
			{
				State.Hold.Take(before, after, Rig, animation, State.Frame);
				return PoseChange.Held;
			}
			var keys = AnimationSnapshot.Capture(animation);
			var changed = new PoseHold();
			if (!changed.Take(before, after, Rig, animation, State.Frame)) return PoseChange.Unchanged;
			changed.KeyInto(animation, State.Time);
			State.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"{name} @ frame {State.Frame}", keys,
				AnimationSnapshot.Capture(animation), s => s.Restore()));
			return PoseChange.Keyed;
		}

		// Setup: the rest pose (bones; parts keep their rest placement)
		return State.EditStructure(() =>
		{
			Rig.SetToSetupPose();
			Rig.UpdateWorld();
			change();
			bool any = false;
			foreach (var bone in Rig.AllBones())
			{
				if (bone.Position == bone.SetupPosition && bone.Rotation == bone.SetupRotation && bone.Scale == bone.SetupScale &&
					bone.Turn == bone.SetupTurn && bone.Tilt == bone.SetupTilt) continue;
				bone.SetupPosition = bone.Position;
				bone.SetupRotation = bone.Rotation;
				bone.SetupScale = bone.Scale;
				bone.SetupTurn = bone.Turn;
				bone.SetupTilt = bone.Tilt;
				any = true;
			}
			return any ? $"{name} (rest pose)" : null;
		}) ? PoseChange.Rest : PoseChange.Unchanged;
	}

	// A saved pose put on: some of the way (amount 0..1), mirrored, or on the picked alone (null: all)
	public PoseChange ApplyPose(SavedPose pose, bool mirrored, float amount = 1f, IReadOnlyCollection<object>? only = null)
	{
		string how = amount < 0.999f ? $" ({amount:P0})" : "";
		return mirrored
			? PoseEdit($"Put on {pose.Name} mirrored{how}", () => PoseOps.ApplyMirrored(Rig, pose, amount))
			: PoseEdit($"Put on {pose.Name}{how}", () => PoseOps.Apply(Rig, pose, amount, only));
	}

	// The picked bones and parts, or null when nothing's picked (meaning all)
	public List<Bone>? PickedBonesOrAll() => State.Picked.Count > 0 ? State.PickedBones.ToList() : null;
	public List<Slot>? PickedPartsOrAll() => State.Picked.Count > 0 ? State.PickedSlots.ToList() : null;

	// Left and right swapped (the picked, or everything)
	public PoseChange MirrorPose()
	{
		var bones = PickedBonesOrAll();
		var parts = PickedPartsOrAll();
		return PoseEdit(bones == null ? "Mirror the pose" : $"Mirror {bones.Count + (parts?.Count ?? 0)} picked", () => PoseOps.Mirror(Rig, bones, parts));
	}

	// The picked's pose (or everything's) kept to paste
	public SavedPose? CopyPose()
	{
		if (SceneLevel) return null;
		return PoseClipboard = PoseOps.Capture(Rig, "Copied pose", PickedBonesOrAll(), PickedPartsOrAll());
	}

	public PoseChange PastePose(bool mirrored)
	{
		if (PoseClipboard is not { } pose) return PoseChange.None;
		return mirrored
			? PoseEdit("Paste the pose mirrored", () => PoseOps.ApplyMirrored(Rig, pose))
			: PoseEdit("Paste the pose", () => PoseOps.Apply(Rig, pose));
	}

	// Setup: the other side made the mirror image of these bones (the ones whose names say a
	// side). How many bones were made (null: none of them says a side, or it's not Setup).
	public int? Symmetrize(IReadOnlyList<Bone> bones)
	{
		if (SceneLevel || State.AnimateMode) return null;
		var sided = bones.Where(b => MirrorNames.Other(b.Name) != null).ToList();
		if (sided.Count == 0) return null;
		int made = 0;
		State.EditStructure(() =>
		{
			made = PoseOps.Symmetrize(Rig, sided).Count;
			return $"Symmetrize {sided.Count} bone(s)";
		});
		return made;
	}

	// ======================= the saved poses =======================

	public string UniquePoseName(string baseName)
	{
		var used = Rig.Poses.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
	}

	// A change to the list of poses (and their names) as one undo step
	public void EditPoses(string name, Action change)
	{
		var rig = Rig;
		var before = rig.Poses.Select(p => (p, p.Name)).ToList();
		change();
		var after = rig.Poses.Select(p => (p, p.Name)).ToList();
		State.Undo.Push(new SnapshotCommand<List<(SavedPose, string)>>(name, before, after, list =>
		{
			rig.Poses.Clear();
			foreach (var (pose, poseName) in list)
			{
				pose.Name = poseName;
				rig.Poses.Add(pose);
			}
		}));
	}

	// The pose as it is now (the picked's, or everything's) kept in the list, named after where
	// it was taken (the animation and frame, or Rest)
	public SavedPose? KeepPose()
	{
		if (SceneLevel) return null;
		var pose = PoseOps.Capture(Rig, UniquePoseName(State.AnimateMode ? $"{State.Animation.Name} {State.Frame}" : "Rest"),
			PickedBonesOrAll(), PickedPartsOrAll());
		EditPoses($"Keep the pose {pose.Name}", () => Rig.Poses.Add(pose));
		return pose;
	}

	// A saved pose kept again, as its bones and parts are posed now (the new one, in its place)
	public SavedPose UpdatePose(SavedPose old)
	{
		var bones = old.Bones.Keys.Select(Rig.FindBone).OfType<Bone>().ToList();
		var parts = old.Parts.Keys.Select(Rig.FindSlot).OfType<Slot>().ToList();
		var fresh = PoseOps.Capture(Rig, old.Name, bones, parts);
		int i = Rig.Poses.IndexOf(old);
		if (i < 0) return old;
		EditPoses($"Update the pose {old.Name}", () => Rig.Poses[i] = fresh);
		return fresh;
	}

	public bool DeletePose(SavedPose pose)
	{
		if (!Rig.Poses.Contains(pose)) return false;
		EditPoses($"Delete the pose {pose.Name}", () => Rig.Poses.Remove(pose));
		return true;
	}

	// False: empty, unchanged, or another pose's name (whatever the case)
	public bool RenamePose(SavedPose pose, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == pose.Name || Rig.Poses.Any(p => p != pose && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
			return false;
		EditPoses($"Rename the pose to {name}", () => pose.Name = name);
		return true;
	}

	// ======================= events =======================

	// An event at a frame of the animation being edited (one there already is replaced); a sound
	// for its name, if given (a path as the project keeps it)
	public bool AddEvent(int frame, AnimationEvent e, string? sound = null)
	{
		var animation = State.Animation;
		float time = frame / (float)State.Fps;
		return EditStructureAndScene(() =>
		{
			animation.GetOrCreateEvents().Add(time, e);
			if (sound != null) Scene.Sounds[e.Name] = sound;
			return $"Event {e.Name} @ frame {frame}";
		});
	}

	// The event at a frame changed (and a sound for its name, if given). False: none there, or
	// nothing changed.
	public bool EditEvent(EventTimeline events, int frame, AnimationEvent updated, string? sound = null)
	{
		int Find() => events.Track.Keys.FindIndex(k => (int)MathF.Round(k.Time * State.Fps) == frame);
		int at = Find();
		if (at < 0 || events.Track.Keys[at].Value == updated && sound == null) return false;
		return EditStructureAndScene(() =>
		{
			int i = Find();
			if (i < 0) return null;
			events.Track.Keys[i] = events.Track.Keys[i] with { Value = updated };
			if (sound != null) Scene.Sounds[updated.Name] = sound;
			return $"Edit event {updated.Name}";
		});
	}
}
