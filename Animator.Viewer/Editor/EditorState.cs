using Animator.Core.Editing;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Viewer;

public class EditorState
{
	public Skeleton Skeleton { get; set; }   // the rig of the object being edited
	public Animation Animation { get; set; }
	public UndoStack Undo { get; } = new();
	// Every animation in the project. "Animation" is the one being edited.
	public List<Animation> Animations { get; set; } = new();
	public Bone? Selected
	{
		get => _selected;
		set
		{
			_selected = value;
			ForgetOtherPicks();
		}
	}
	private Bone? _selected;

	// The selected part (null = none). When set, Selected is that part's bone.
	public Slot? SelectedSlot
	{
		get => _selectedSlot;
		set
		{
			_selectedSlot = value;
			ForgetOtherPicks();
		}
	}
	private Slot? _selectedSlot;

	public bool AnimateMode { get; set; }

	// ----- several picked at once -----
	// Bones and parts can be picked together (Ctrl / Shift / a box in the viewport, Ctrl+A). The
	// panels show one of them: Selected / SelectedSlot, the last picked. Anything that selects
	// one thing on its own (Selected = ...) picks just that, unless it's one of the picked ones.
	private readonly List<object> _picked = new();

	private void ForgetOtherPicks()
	{
		if (Primary is not { } primary || !_picked.Contains(primary)) _picked.Clear();
	}

	// The one the panels show: the selected part, else the selected bone
	public object? Primary => (object?)SelectedSlot ?? Selected;

	// Everything picked, the primary among them, in the order picked
	public IReadOnlyList<object> Picked
	{
		get
		{
			var primary = Primary;
			if (primary == null) return Array.Empty<object>();
			if (!_picked.Contains(primary)) return new[] { primary };
			return _picked.Where(Exists).ToList();
		}
	}

	public IEnumerable<Bone> PickedBones => Picked.OfType<Bone>();
	public IEnumerable<Slot> PickedSlots => Picked.OfType<Slot>();
	public bool IsPicked(object item) => Picked.Contains(item);

	private bool Exists(object item) => item switch
	{
		Bone bone => Skeleton.AllBones().Contains(bone),
		Slot slot => Skeleton.Slots.Contains(slot),
		_ => false
	};

	// Picks these (and nothing else); the panels show `primary`, or the last of them
	public void Pick(IEnumerable<object> items, object? primary = null)
	{
		_picked.Clear();
		foreach (var item in items)
			if (item is Bone or Slot && !_picked.Contains(item)) _picked.Add(item);
		if (primary != null && !_picked.Contains(primary)) _picked.Add(primary);
		Show(primary ?? _picked.LastOrDefault());
	}

	// Picks these too (the last becomes the one shown)
	public void PickMore(IEnumerable<object> items)
	{
		var now = Picked.ToList();
		var added = items.Where(i => !now.Contains(i)).ToList();
		Pick(now.Concat(added), added.LastOrDefault() ?? Primary);
	}

	// Lets these go (the one shown becomes the last of the others, if it went)
	public void Unpick(IEnumerable<object> items)
	{
		var gone = items.ToHashSet();
		var left = Picked.Where(i => !gone.Contains(i)).ToList();
		var primary = Primary;
		Pick(left, primary != null && !gone.Contains(primary) ? primary : left.LastOrDefault());
	}

	// Picked or not, the other way round
	public void TogglePick(object item)
	{
		if (IsPicked(item)) Unpick(new[] { item });
		else PickMore(new[] { item });
	}

	public void ClearPicks() => Pick(Array.Empty<object>());

	// (the fields: the picks are already as they should be)
	private void Show(object? item)
	{
		switch (item)
		{
			case Slot slot:
				_selectedSlot = slot;
				_selected = slot.Bone;
				break;
			case Bone bone:
				_selectedSlot = null;
				_selected = bone;
				break;
			default:
				_selectedSlot = null;
				_selected = null;
				break;
		}
	}
	public bool Playing { get; set; } = true;
	public int Fps { get; set; } = 30;
	public float Time { get; set; }
	// Selected keys, identified by (timeline, frame)
	public HashSet<(Timeline Tl, int Frame)> SelectedKeys { get; } = new();

	public EditorState(Skeleton skeleton, Animation animation)
	{
		Skeleton = skeleton;
		Animation = animation;
		Animations.Add(animation);
	}
	// Runs a structural rig edit as one undo step.
	// The change returns the step's name, or null if it ended up changing nothing.

	public StructureSnapshot CaptureStructure() => StructureSnapshot.Capture(Skeleton, Animations);

	// For edits spread over time (like a drag): capture at the start, push at the end
	public void PushStructure(StructureSnapshot before, string name) =>
		Undo.Push(new SnapshotCommand<StructureSnapshot>(name, before, CaptureStructure(), s => s.Restore()));

	// One-shot structural edit. The change returns the step's name, or null if nothing changed.
	public bool EditStructure(Func<string?> change)
	{
		var before = CaptureStructure();
		string? name = change();
		if (name == null)
		{
			before.Restore();   // safety net: undo anything a change did before giving up
			return false;
		}
		PushStructure(before, name);
		return true;
	}

	public int Frame => (int)MathF.Round(Time * Fps);
	public int FrameCount => (int)MathF.Round(Animation.Duration * Fps);

	// Keys are always placed at exact frame times, so the playhead and keys line up
	public void SetFrame(int frame) => Time = Math.Clamp(frame, 0, FrameCount) / (float)Fps;

	// ----- auto key -----
	// On: posing in Animate mode sets keys. Off: it only changes the pose, held here until it's
	// keyed (S) or the playhead moves on (see PoseHold).
	public bool AutoKey { get; set; } = true;
	public PoseHold Hold { get; } = new();

	// ----- playback -----
	public float Speed { get; set; } = 1f;       // 0.5 = half speed
	public bool PingPong { get; set; }           // forwards, then backwards, and so on
	public (int First, int Last)? PlayRange { get; set; }   // just these frames, over and over (null: all of it)
	public bool PlayingBackwards => _direction < 0;
	private int _direction = 1;

	// The playhead moved on by dt seconds of play. What it went past, as (from, to] spans of time
	// (for the events to fire), from < to whichever way it went.
	public IReadOnlyList<(float From, float To)> Advance(float dt)
	{
		var spans = new List<(float, float)>(2);
		float d = Animation.Duration;
		if (d <= 0) { Time = 0; return spans; }

		float start = 0, end = d;
		bool loop = Animation.Loop;
		if (PlayRange is { } range && range.Last >= range.First)
		{
			start = Math.Clamp(range.First, 0, FrameCount) / (float)Fps;
			end = Math.Clamp(range.Last + 1, 0, FrameCount) / (float)Fps;   // through the last picked frame
			loop = true;
			if (end - start < 1e-6f) { Time = start; return spans; }
			if (Time < start || Time > end) Time = start;
		}
		if (!PingPong) _direction = 1;

		float step = dt * MathF.Max(0.01f, Speed);
		float from = Time;
		if (PingPong)
		{
			float t = Time + step * _direction;
			if (t > end)
			{
				spans.Add((from, end));
				t = MathF.Max(start, end - (t - end));
				_direction = -1;
				spans.Add((t, end));
			}
			else if (t < start)
			{
				spans.Add((start - 1e-4f, from));
				t = MathF.Min(end, start + (start - t));
				_direction = 1;
				spans.Add((start - 1e-4f, t));
			}
			else spans.Add(_direction > 0 ? (from, t) : (t, from));
			Time = t;
			return spans;
		}

		Time += step;
		if (Time > end)
		{
			spans.Add((from, end));
			if (loop)
			{
				Time = start + (Time - start) % (end - start);
				spans.Add((start - 1e-4f, Time));
			}
			else { Time = end; Playing = false; }
		}
		else spans.Add((from, Time));
		return spans;
	}
}