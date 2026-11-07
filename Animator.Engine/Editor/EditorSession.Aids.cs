using System.Numerics;

namespace Animator.Viewer;

// The editing aids and the sound, edited (EditorSession's side of the eye and lock buttons, the
// bone colours, the selection sets, the guides and grid, and the Sound menu): what's hidden and
// locked, the bones' own colours, the sets of bones and parts; the scene's guides and grid size;
// an animation's sound track, the scene's sound map, and events brought in from a file. Each edit
// is one undo step, and says whether it changed anything.
public sealed partial class EditorSession
{
	// ======================= hiding and locking =======================

	public static string NameOf(object item) => item switch { Bone b => b.Name, Slot s => s.Name, _ => "" };

	public static bool IsHiddenItem(object item) => item switch { Bone b => b.IsHidden, Slot s => s.IsHidden, _ => false };

	// One undo step (none when nothing changed). What's hidden can't stay picked: it couldn't be
	// seen being moved; nor can what's just been locked, with letLockedGo.
	public bool EditAids(string name, Func<bool> change, bool letLockedGo = false)
	{
		var skeleton = State.Skeleton;
		var before = EditAidsSnapshot.Capture(skeleton);
		if (!change()) return false;
		var gone = State.Picked.Where(i => IsHiddenItem(i) || (letLockedGo && LockOps.IsLocked(i))).ToList();
		if (gone.Count > 0) State.Unpick(gone);
		State.Undo.Push(new SnapshotCommand<EditAidsSnapshot>(name, before, EditAidsSnapshot.Capture(skeleton), s => s.Restore()));
		return true;
	}

	public bool ToggleHidden(object item)
	{
		bool hide = item switch { Bone b => !b.Hidden, Slot s => !s.Hidden, _ => false };
		return EditAids($"{(hide ? "Hide" : "Show")} {NameOf(item)}", () => VisibilityOps.SetHidden(new[] { item }, hide));
	}

	public bool HidePicked()
	{
		var picked = State.Picked.ToList();
		return picked.Count > 0 && EditAids(picked.Count == 1 ? $"Hide {NameOf(picked[0])}" : $"Hide {picked.Count} picked",
			() => VisibilityOps.SetHidden(picked, true));
	}

	public bool HideAllButPicked()
	{
		var picked = State.Picked.ToList();
		return picked.Count > 0 && EditAids("Hide everything but the picked", () => VisibilityOps.HideOthers(State.Skeleton, picked));
	}

	public bool ShowEverything() => EditAids("Show everything", () => VisibilityOps.ShowAll(State.Skeleton));

	// (a locked one picked in the list stays picked: its numbers can still be changed)
	public bool ToggleLocked(object item)
	{
		bool locking = !LockOps.IsLocked(item);
		return EditAids($"{(locking ? "Lock" : "Unlock")} {NameOf(item)}", () => LockOps.SetLocked(new[] { item }, locking));
	}

	// The picked locked, and let go: they can't be dragged any more
	public bool LockPicked()
	{
		var picked = State.Picked.ToList();
		return picked.Count > 0 && EditAids(picked.Count == 1 ? $"Lock {NameOf(picked[0])}" : $"Lock {picked.Count} picked",
			() => LockOps.SetLocked(picked, true), letLockedGo: true);
	}

	// Everything but the picked locked: clicks only reach the picked ones
	public bool LockAllButPicked()
	{
		var picked = State.Picked.ToList();
		return picked.Count > 0 && EditAids("Lock everything but the picked", () => LockOps.LockOthers(State.Skeleton, picked));
	}

	public bool UnlockEverything() => EditAids("Unlock everything", () => LockOps.UnlockAll(State.Skeleton));

	// Bones drawn in a colour of their own (null: the usual one); `name` says which, for the undo step
	public bool ColourBones(IReadOnlyList<Bone> bones, Vector3? color, string name)
	{
		string what = bones.Count == 1 ? bones[0].Name : $"{bones.Count} bones";
		return EditAids($"{what}: {name.ToLowerInvariant()}", () =>
		{
			bool changed = false;
			foreach (var bone in bones)
			{
				if (bone.EditorColor == color) continue;
				bone.EditorColor = color;
				changed = true;
			}
			return changed;
		});
	}

	// ======================= selection sets =======================

	// Why a set can't have this name (null: it can)
	public string? SetNameProblem(string name, SelectionSet? renaming) =>
		name.Length == 0 ? "A set needs a name."
		: State.Skeleton.Sets.Any(s => s != renaming && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ? "Another set has that name."
		: null;

	// One undo step
	public bool EditSets(string name, Func<bool> change)
	{
		var skeleton = State.Skeleton;
		var before = SetsSnapshot.Capture(skeleton);
		if (!change()) return false;
		State.Undo.Push(new SnapshotCommand<SetsSnapshot>(name, before, SetsSnapshot.Capture(skeleton), s => s.Restore()));
		return true;
	}

	// A set of the picked bones and parts (null: nothing is picked, or the name won't do)
	public SelectionSet? NewSetFromPicked(string name)
	{
		var picked = State.Picked.ToList();
		if (picked.Count == 0 || SetNameProblem(name, null) != null) return null;
		var set = new SelectionSet { Name = name };
		set.Bones.AddRange(picked.OfType<Bone>());
		set.Slots.AddRange(picked.OfType<Slot>());
		EditSets($"New set {set.Name}", () =>
		{
			State.Skeleton.Sets.Add(set);
			return true;
		});
		return set;
	}

	// The picked added to the set, or taken out of it
	public bool ChangeSet(SelectionSet set, bool add)
	{
		var picked = State.Picked.ToList();
		return EditSets(add ? $"Add {picked.Count} to the set {set.Name}" : $"Take {picked.Count} out of the set {set.Name}", () =>
		{
			bool changed = false;
			foreach (var bone in picked.OfType<Bone>())
				changed |= add ? AddOnce(set.Bones, bone) : set.Bones.Remove(bone);
			foreach (var slot in picked.OfType<Slot>())
				changed |= add ? AddOnce(set.Slots, slot) : set.Slots.Remove(slot);
			return changed;
		});
	}

	private static bool AddOnce<T>(List<T> list, T item)
	{
		if (list.Contains(item)) return false;
		list.Add(item);
		return true;
	}

	// Just the picked in the set
	public bool ReplaceSet(SelectionSet set)
	{
		var picked = State.Picked.ToList();
		var bones = picked.OfType<Bone>().ToList();
		var slots = picked.OfType<Slot>().ToList();
		if (set.Bones.SequenceEqual(bones) && set.Slots.SequenceEqual(slots)) return false;
		return EditSets($"The set {set.Name}: just the picked", () =>
		{
			set.Bones.Clear();
			set.Bones.AddRange(bones);
			set.Slots.Clear();
			set.Slots.AddRange(slots);
			return true;
		});
	}

	public bool RenameSet(SelectionSet set, string name)
	{
		if (name == set.Name || SetNameProblem(name, set) != null) return false;
		string old = set.Name;
		return EditSets($"Rename the set {old} to {name}", () =>
		{
			set.Name = name;
			return true;
		});
	}

	public bool DeleteSet(SelectionSet set) => EditSets($"Delete the set {set.Name}", () => State.Skeleton.Sets.Remove(set));

	// ======================= guides and grid =======================

	// Every guide and where it is, for an undo step around a drag
	public List<(Guide Guide, float At)> GuidesNow() => Scene.Guides.Select(g => (g, g.At)).ToList();

	private void RestoreGuides(List<(Guide Guide, float At)> guides)
	{
		Scene.Guides.Clear();
		foreach (var (guide, at) in guides)
		{
			guide.At = at;
			Scene.Guides.Add(guide);
		}
	}

	// A guide drag let go: one undo step from how the guides were (added, taken away or moved)
	public bool GuidesChanged(List<(Guide Guide, float At)> before)
	{
		var after = GuidesNow();
		if (after.SequenceEqual(before)) return false;
		string name = after.Count > before.Count ? "Add a guide" : after.Count < before.Count ? "Take a guide away" : "Move a guide";
		State.Undo.Push(new SnapshotCommand<List<(Guide Guide, float At)>>(name, before, after, RestoreGuides));
		return true;
	}

	public bool ClearGuides()
	{
		if (Scene.Guides.Count == 0) return false;
		var before = GuidesNow();
		Scene.Guides.Clear();
		return GuidesChanged(before);
	}

	public bool SetGridSize(float size, string name)
	{
		if (!(size > 0) || MathF.Abs(size - Scene.GridSize) < 1e-6f) return false;
		float before = Scene.GridSize;
		Scene.GridSize = size;
		State.Undo.Push(new SnapshotCommand<float>(name, before, size, v => Scene.GridSize = v));
		return true;
	}

	// ======================= sound =======================

	// A sound file as the project keeps it: relative to the project's folder when it can be
	public string StoredSoundPath(string file)
	{
		if (ProjectFolder is not { } folder || !string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(file), StringComparison.OrdinalIgnoreCase))
			return file;
		return Path.GetRelativePath(folder, file);
	}

	// The open animation's sound track (null: none)
	public bool SetSoundTrack(SoundTrack? track, string name)
	{
		var animation = State.Animation;
		return State.EditStructure(() =>
		{
			if (animation.Sound == track) return null;
			animation.Sound = track;
			return name;
		});
	}

	// An events file into the open object's animations (replacing theirs, or added to them). The
	// sound tracks' paths in it are relative to `folder`, where it was read from. Null: nothing changed.
	public EventsFile.Merged? ImportEvents(EventsFile file, string folder, bool replace, string name)
	{
		foreach (var a in file.Animations)
			if (a.Sound is { Path.Length: > 0 } s && !Path.IsPathRooted(s.Path))
				s.Path = StoredSoundPath(Path.GetFullPath(Path.Combine(folder, s.Path)));
		EventsFile.Merged? merged = null;
		bool changed = State.EditStructure(() =>
		{
			merged = file.MergeInto(State.Animations, State.Fps, replace);
			return merged.Added + merged.Replaced + merged.SoundTracks > 0 ? name : null;
		});
		return changed ? merged : null;
	}

	// A sound map's sounds into the scene's (each name to its file; paths relative to `folder`)
	public bool ImportSoundMap(SoundMapFile file, string folder, string name) => EditScene(() =>
	{
		bool changed = false;
		foreach (var (sound, path) in file.Sounds)
		{
			if (sound.Trim().Length == 0 || path.Trim().Length == 0) continue;
			string stored = StoredSoundPath(Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(folder, path)));
			if (Scene.Sounds.GetValueOrDefault(sound.Trim()) == stored) continue;
			Scene.Sounds[sound.Trim()] = stored;
			changed = true;
		}
		return changed ? name : null;
	});
}
