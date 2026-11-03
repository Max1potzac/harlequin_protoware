namespace Animator.Viewer;

// The open object's animations, edited (EditorSession's side of the Animations panel): switching
// to one, new, duplicated, deleted (one always stays), renamed, looping, their length, the
// frame rate, and picked frames made an animation sequence of their own. Each edit is one undo
// step, and says whether it changed anything.
public sealed partial class EditorSession
{
	// Edits that animation from its start, stopped; selected keys (and picked frames) belonged
	// to the one before
	public void SwitchAnimation(Animation animation)
	{
		State.Animation = animation;
		State.SelectedKeys.Clear();
		State.Time = 0;
		State.Playing = false;
	}

	// The name, or the name with the first number after it that no animation of the open object has
	public string UniqueAnimationName(string baseName)
	{
		var used = State.Animations.Select(a => a.Name).ToHashSet();
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName}{i}")) return $"{baseName}{i}";
	}

	// A new animation (two seconds, looping), switched to
	public Animation NewAnimation(string baseName = "animation")
	{
		Animation? made = null;
		State.EditStructure(() =>
		{
			made = new Animation { Name = UniqueAnimationName(baseName), Duration = 2f, Loop = true };
			State.Animations.Add(made);
			SwitchAnimation(made);
			return $"New animation {made.Name}";
		});
		return made!;
	}

	// A copy of the animation being edited, just after it, switched to
	public Animation DuplicateAnimation()
	{
		var source = State.Animation;
		Animation? copy = null;
		State.EditStructure(() =>
		{
			copy = ProjectMapper.CloneAnimation(source, UniqueAnimationName($"{source.Name}_copy"));
			State.Animations.Insert(State.Animations.IndexOf(source) + 1, copy);
			SwitchAnimation(copy);
			return $"Duplicate {source.Name}";
		});
		return copy!;
	}

	// The animation being edited gone (not the last one): the next one (or the one before) is edited
	public bool DeleteAnimation()
	{
		if (State.Animations.Count <= 1) return false;
		var anim = State.Animation;
		return State.EditStructure(() =>
		{
			int i = State.Animations.IndexOf(anim);
			State.Animations.Remove(anim);
			SwitchAnimation(State.Animations[Math.Min(i, State.Animations.Count - 1)]);
			return $"Delete animation {anim.Name}";
		});
	}

	public bool SetAnimationLoop(bool loop)
	{
		var anim = State.Animation;
		return State.EditStructure(() =>
		{
			if (anim.Loop == loop) return null;
			anim.Loop = loop;
			return $"{anim.Name} {(loop ? "loops" : "plays once")}";
		});
	}

	// False: the name is empty or another animation's. Its fades in the mix plan follow, and the
	// object's preview, which names it (clips point at the animation itself).
	public bool RenameAnimation(string name)
	{
		var anim = State.Animation;
		name = name.Trim();
		if (name == anim.Name || name.Length == 0 || State.Animations.Any(a => a != anim && a.Name == name)) return false;
		string oldName = anim.Name;
		return EditStructureAndScene(() =>
		{
			anim.Name = name;
			State.Skeleton.Mixes.Rename(oldName, name);
			if (Active != null && Active.PreviewAnimation == oldName) Active.PreviewAnimation = name;
			return $"Rename animation to {name}";
		});
	}

	// The animation being edited this many frames long (1 or more); the playhead stays inside
	public bool SetAnimationLength(int frames)
	{
		frames = Math.Max(1, frames);
		if (frames == State.FrameCount) return false;
		var anim = State.Animation;
		float duration = frames / (float)State.Fps;
		bool changed = State.EditStructure(() =>
		{
			anim.Duration = duration;
			return $"{anim.Name} length: {frames} frames";
		});
		State.SetFrame(State.Frame);
		return changed;
	}

	// Frames a second (1 to 240), for the whole project
	public bool SetFps(int fps)
	{
		fps = Math.Clamp(fps, 1, 240);
		if (fps == State.Fps) return false;
		int before = State.Fps;
		State.Fps = fps;
		State.Undo.Push(new SnapshotCommand<int>($"FPS: {fps}", before, fps, v => State.Fps = v));
		return true;
	}

	// Picked frames (first to last) of the animation being edited as an animation sequence of their
	// own, just after it, starting at frame 0. Split: they leave this animation, the frames after
	// them moving up (the playhead goes where they were); else it keeps them. Stays on this
	// animation, so more can be split off one after another.
	public Animation? MakeSequence(int first, int last, string name, bool split)
	{
		var source = State.Animation;
		int fps = State.Fps;
		Animation? piece = null;
		State.Playing = false;
		if (!State.EditStructure(() =>
		{
			piece = split ? AnimationOps.Split(source, first, last, fps, name) : AnimationOps.Extract(source, first, last, fps, name);
			State.Animations.Insert(State.Animations.IndexOf(source) + 1, piece);
			return $"{(split ? "Split" : "Copy")} frames {first}–{last} of {source.Name} into {name}";
		})) return null;
		if (split) State.SetFrame(Math.Min(first, State.FrameCount - 1));
		return piece;
	}
}
