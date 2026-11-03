using System.Numerics;

namespace Animator.Core.Editing;

// Rigging an object that's already animated without spoiling its animations. Taken before a rig
// edit (bones moved or turned at rest, given another parent, parts put on other bones, new bones
// drawn over the art), and applied after it, it puts the keys right again, so every animation
// shows as it did:
//
// - a bone whose rest pose changed (or its parent's) keeps moving the way it moved, measured from
//   its rest: what's on it moves as before (with "parts stay put", the art stays exactly as it was)
// - a part now on another bone keeps showing where it showed, through placement keys
//
// Only what the edit changed gets new keys (on every frame, then thinned out where a straight
// line between keys does as well). Bones driven by constraints, detached, or turned in depth are
// left as they are. FollowPart makes a bone take over a part's motion instead (tracing a rig over
// an animation that moves its parts on their own).
public sealed class MotionKeeper
{
	private readonly Skeleton _skeleton;
	private readonly int _fps;
	private readonly Dictionary<Bone, Matrix3x2> _restWorld = new();
	private readonly Dictionary<Bone, Bone?> _parentOf = new();
	private readonly Dictionary<Slot, Bone> _boneOf = new();
	private readonly List<Taken> _animations = new();

	private sealed record Taken(Animation Animation, List<float> Times, List<Dictionary<Bone, Matrix3x2>> Bones,
		List<Dictionary<Slot, Matrix3x2>> Parts);

	private MotionKeeper(Skeleton skeleton, int fps)
	{
		_skeleton = skeleton;
		_fps = Math.Max(1, fps);
	}

	// What every animation with keys shows now, frame by frame (null: none has any)
	public static MotionKeeper? Capture(Skeleton skeleton, IEnumerable<Animation> animations, int fps)
	{
		var moving = animations.Where(a => a.Timelines.Any(t => t is not EventTimeline && t.KeyTimes.Any())).ToList();
		if (moving.Count == 0) return null;
		var keeper = new MotionKeeper(skeleton, fps);
		var shownNow = Shown.Take(skeleton);   // (put back after: the editor goes on from this pose)
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		foreach (var bone in skeleton.AllBones())
		{
			keeper._restWorld[bone] = bone.World;
			keeper._parentOf[bone] = bone.Parent;
		}
		foreach (var slot in skeleton.Slots) keeper._boneOf[slot] = slot.Bone;
		foreach (var animation in moving)
		{
			var times = keeper.Times(animation);
			var bones = new List<Dictionary<Bone, Matrix3x2>>();
			var parts = new List<Dictionary<Slot, Matrix3x2>>();
			foreach (float time in times)
			{
				Pose(skeleton, animation, time);
				bones.Add(skeleton.AllBones().ToDictionary(b => b, b => b.World));
				parts.Add(Images(skeleton));
			}
			keeper._animations.Add(new Taken(animation, times, bones, parts));
		}
		shownNow.Put();
		return keeper;
	}

	// The keys put right after the edit. False: nothing needed it.
	public bool Apply() => Apply(null, Array.Empty<Slot>());

	// After moving `slot` onto `bone`: the bone takes over the part's motion (keys on it that move
	// it as the part moved, the part's own placement keys gone), everything else kept as it was
	public bool FollowPart(Bone bone, Slot slot)
	{
		var drawingsOf = new Dictionary<Animation, List<Attachment?>>();
		foreach (var taken in _animations)
		{
			var list = new List<Attachment?>();
			foreach (float time in taken.Times)
			{
				Pose(_skeleton, taken.Animation, time);
				list.Add(_skeleton.ResolveAttachment(slot));
			}
			drawingsOf[taken.Animation] = list;
		}
		Matrix3x2? Follow(Taken taken, int f)
		{
			if (!taken.Parts[f].TryGetValue(slot, out var image) || drawingsOf[taken.Animation][f] is not { } drawing
				|| !_skeleton.Assets.TryGetValue(drawing.AssetId, out var asset)
				|| !Matrix3x2.Invert(drawing.LocalMatrix(asset.Width, asset.Height), out var toBone)) return null;
			return toBone * image;   // the bone where the part's picture, sitting on it at rest, shows then
		}
		return Apply((bone, Follow), new[] { slot });
	}

	private bool Apply((Bone Bone, Func<Taken, int, Matrix3x2?> World)? follow, IReadOnlyCollection<Slot> carried)
	{
		_skeleton.SetToSetupPose();
		_skeleton.UpdateWorld();
		var restNow = _skeleton.AllBones().ToDictionary(b => b, b => b.World);
		var fixedBones = Untouchable();
		bool changed = false;

		foreach (var taken in _animations)
		{
			var animation = taken.Animation;
			if (!_skeleton.AllBones().Any()) break;
			foreach (var slot in carried) RemovePlacement(animation, slot);

			// Where each bone should be, frame by frame: its old motion measured from its new rest
			// (a new bone: as its own keys and its parent have it); the one following a part, there
			var bones = _skeleton.AllBones().ToList();   // (parents first)
			var wanted = bones.ToDictionary(b => b, _ => new Matrix3x2[taken.Times.Count]);
			var locals = bones.ToDictionary(b => b, _ => new Matrix3x2[taken.Times.Count]);
			var redo = new HashSet<Bone>();
			for (int f = 0; f < taken.Times.Count; f++)
			{
				Pose(_skeleton, animation, taken.Times[f], world: false);
				foreach (var bone in bones)
				{
					var parent = bone.SpaceParent != null ? wanted[bone.SpaceParent][f] : Matrix3x2.Identity;
					var current = Local(bone);
					Matrix3x2 world;
					if (follow is { } fo && fo.Bone == bone && !fixedBones.Contains(bone))
					{
						world = fo.World(taken, f) ?? (f > 0 ? locals[bone][f - 1] * parent : current * parent);
					}
					else if (!fixedBones.Contains(bone) && taken.Bones[f].TryGetValue(bone, out var was)
						&& _restWorld.TryGetValue(bone, out var oldRest) && Matrix3x2.Invert(oldRest, out var fromOldRest))
						world = restNow[bone] * fromOldRest * was;
					else world = current * parent;
					wanted[bone][f] = world;
					locals[bone][f] = Matrix3x2.Invert(parent, out var fromParent) ? world * fromParent : current;
					if (!Near(locals[bone][f], current)) redo.Add(bone);
				}
			}
			foreach (var bone in bones.Where(redo.Contains))
			{
				Rekey(animation, bone, taken.Times, locals[bone]);
				changed = true;
			}

			// Parts on other bones than before (and ones a bone now carries): where they showed
			foreach (var slot in _skeleton.Slots)
			{
				bool moved = _boneOf.TryGetValue(slot, out var before) && before != slot.Bone;
				if (!moved || carried.Contains(slot)) continue;
				RemovePlacement(animation, slot);
				var keys = new List<(float, Vector2, float, Vector2)>();
				var last = (Offset: Vector2.Zero, Rotation: 0f, Scale: Vector2.One);
				for (int f = 0; f < taken.Times.Count; f++)
				{
					Pose(_skeleton, animation, taken.Times[f]);
					if (taken.Parts[f].TryGetValue(slot, out var image) && _skeleton.ResolveAttachment(slot) is { } drawing
						&& _skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) && Matrix3x2.Invert(slot.Bone.World, out var toBone))
					{
						var p = Placement(drawing, asset.Width, asset.Height, image * toBone);
						(p.Rotation, p.Scale) = MotionTransfer.Continue(p.Rotation, p.Scale, keys.Count > 0 ? last.Rotation : 0f);
						last = p;
					}
					keys.Add((taken.Times[f], last.Offset, last.Rotation, last.Scale));
				}
				AddPlacement(animation, slot, keys);
				changed = true;
			}
		}
		_skeleton.SetToSetupPose();
		_skeleton.UpdatePose();
		return changed;
	}

	// A pose as the editor shows it, to put back after posing for measurements
	private sealed class Shown
	{
		private readonly Skeleton _skeleton;
		private readonly List<(Bone Bone, Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt)> _bones = new();
		private readonly List<(Slot Slot, Vector2 Offset, float Rotation, Vector2 Scale, string? Drawing, Vector3 Color, float Opacity)> _parts = new();
		private readonly string[] _order;

		private Shown(Skeleton skeleton)
		{
			_skeleton = skeleton;
			foreach (var b in skeleton.AllBones()) _bones.Add((b, b.Position, b.Rotation, b.Scale, b.Turn, b.Tilt));
			foreach (var s in skeleton.Slots) _parts.Add((s, s.Offset, s.Rotation, s.Scale, s.ActiveAttachment, s.Color, s.Opacity));
			_order = skeleton.DrawOrder.Select(s => s.Name).ToArray();
		}

		public static Shown Take(Skeleton skeleton) => new(skeleton);

		public void Put()
		{
			foreach (var (b, position, rotation, scale, turn, tilt) in _bones)
			{
				b.Position = position;
				b.Rotation = rotation;
				b.Scale = scale;
				b.Turn = turn;
				b.Tilt = tilt;
			}
			foreach (var (s, offset, rotation, scale, drawing, color, opacity) in _parts)
			{
				s.Offset = offset;
				s.Rotation = rotation;
				s.Scale = scale;
				s.ActiveAttachment = drawing;
				s.Color = color;
				s.Opacity = opacity;
			}
			_skeleton.SetDrawOrder(_order);
			_skeleton.UpdateWorld();
		}
	}

	// ======================= keys =======================

	private void Rekey(Animation animation, Bone bone, List<float> times, Matrix3x2[] locals)
	{
		animation.Timelines.RemoveAll(t => t.Target == bone.Name && t is RotateTimeline or TranslateTimeline or ScaleTimeline);
		var move = new List<(float, Vector2)>();
		var turn = new List<(float, float)>();
		var size = new List<(float, Vector2)>();
		float previous = bone.SetupRotation;
		for (int f = 0; f < times.Count; f++)
		{
			var (position, rotation, scale) = MotionTransfer.Decompose(locals[f]);
			(rotation, scale) = MotionTransfer.Continue(rotation, scale, previous);
			previous = rotation;
			move.Add((times[f], position - bone.SetupPosition));
			turn.Add((times[f], rotation - bone.SetupRotation));
			size.Add((times[f], new Vector2(Ratio(scale.X, bone.SetupScale.X), Ratio(scale.Y, bone.SetupScale.Y))));
		}
		if (move.Any(k => k.Item2.Length() > 0.01f)) Put(animation, new TranslateTimeline(bone.Name), move, 0.02f);
		if (turn.Any(k => MathF.Abs(k.Item2) > 1e-4f)) Put(animation, new RotateTimeline(bone.Name), turn, 0.0005f);
		if (size.Any(k => (k.Item2 - Vector2.One).Length() > 1e-4f)) Put(animation, new ScaleTimeline(bone.Name), size, 0.0005f);
	}

	private static void RemovePlacement(Animation animation, Slot slot) =>
		animation.Timelines.RemoveAll(t => t.Target == slot.Name && t is PartTranslateTimeline or PartRotateTimeline or PartScaleTimeline);

	private static void AddPlacement(Animation animation, Slot slot, List<(float Time, Vector2 Offset, float Rotation, Vector2 Scale)> keys)
	{
		if (keys.Any(k => k.Offset.Length() > 0.01f)) Put(animation, new PartTranslateTimeline(slot.Name), keys.Select(k => (k.Time, k.Offset)).ToList(), 0.02f);
		if (keys.Any(k => MathF.Abs(k.Rotation) > 1e-4f)) Put(animation, new PartRotateTimeline(slot.Name), keys.Select(k => (k.Time, k.Rotation)).ToList(), 0.0005f);
		if (keys.Any(k => (k.Scale - Vector2.One).Length() > 1e-4f)) Put(animation, new PartScaleTimeline(slot.Name), keys.Select(k => (k.Time, k.Scale)).ToList(), 0.0005f);
	}

	// Keys on every frame, thinned out: one stays only where a straight line from the last one
	// kept to the next misses a frame between by more than `tolerance`
	private static void Put(Animation animation, TrackTimeline<Vector2> row, List<(float Time, Vector2 Value)> keys, float tolerance) =>
		Put(animation, row, keys, tolerance, (a, b, t) => Vector2.Lerp(a, b, t), (a, b) => Vector2.Distance(a, b));

	private static void Put(Animation animation, TrackTimeline<float> row, List<(float Time, float Value)> keys, float tolerance) =>
		Put(animation, row, keys, tolerance, (a, b, t) => a + (b - a) * t, (a, b) => MathF.Abs(a - b));

	private static void Put<T>(Animation animation, TrackTimeline<T> row, List<(float Time, T Value)> keys, float tolerance,
		Func<T, T, float, T> lerp, Func<T, T, float> distance)
	{
		int kept = 0;
		row.Track.Add(keys[0].Time, keys[0].Value, CurveType.Linear);
		for (int i = 2; i < keys.Count; i++)
		{
			var (t0, v0) = keys[kept];
			var (t1, v1) = keys[i];
			bool fits = true;
			for (int j = kept + 1; j < i && fits; j++)
				fits = distance(lerp(v0, v1, (keys[j].Time - t0) / (t1 - t0)), keys[j].Value) <= tolerance;
			if (fits) continue;
			kept = i - 1;
			row.Track.Add(keys[kept].Time, keys[kept].Value, CurveType.Linear);
		}
		if (keys.Count > 1) row.Track.Add(keys[^1].Time, keys[^1].Value, CurveType.Linear);
		animation.Timelines.Add(row);
	}

	// ======================= posing and measuring =======================

	private List<float> Times(Animation animation)
	{
		int frames = Math.Max(1, (int)MathF.Round(animation.Duration * _fps));
		return Enumerable.Range(0, frames + 1).Select(i => MathF.Min(i / (float)_fps, animation.Duration)).Distinct().ToList();
	}

	private static void Pose(Skeleton skeleton, Animation animation, float time, bool world = true)
	{
		skeleton.SetToSetupPose();
		animation.Apply(skeleton, time);
		if (world) skeleton.UpdatePose();
	}

	// Each shown part's picture → the object
	private static Dictionary<Slot, Matrix3x2> Images(Skeleton skeleton)
	{
		var images = new Dictionary<Slot, Matrix3x2>();
		foreach (var slot in skeleton.Slots)
			if (skeleton.ResolveAttachment(slot) is { } drawing && skeleton.Assets.TryGetValue(drawing.AssetId, out var asset))
				images[slot] = drawing.LocalMatrix(asset.Width, asset.Height, slot.Offset, slot.Rotation, slot.Scale) * slot.Bone.World;
		return images;
	}

	// Bones whose keys are best left alone: driven by constraints, detached, turned in depth, or
	// not taking all of their parent's turn and scale
	private HashSet<Bone> Untouchable()
	{
		var bones = new HashSet<Bone>();
		foreach (var ik in _skeleton.IkConstraints) bones.UnionWith(ik.Bones);
		foreach (var c in _skeleton.LookConstraints) bones.Add(c.Bone);
		foreach (var c in _skeleton.CopyConstraints) bones.Add(c.Bone);
		foreach (var bone in _skeleton.AllBones())
			if (bone.SpaceOverridden || !bone.InheritRotation || !bone.InheritScale || bone.SetupTilt != 0 || bone.SetupTurn != 0)
				bones.Add(bone);
		foreach (var taken in _animations)
			foreach (var t in taken.Animation.Timelines.Where(t => t is DepthTimeline or SpaceTimeline))
				if (_skeleton.FindBone(t.Target) is { } b) bones.Add(b);
		return bones;
	}

	private static Matrix3x2 Local(Bone bone) =>
		Matrix3x2.CreateScale(bone.Scale) * Matrix3x2.CreateRotation(bone.Rotation) * Matrix3x2.CreateTranslation(bone.Position);

	private static bool Near(Matrix3x2 a, Matrix3x2 b) =>
		Vector2.Distance(a.Translation, b.Translation) < 0.01f
		&& MathF.Abs(a.M11 - b.M11) < 1e-4f && MathF.Abs(a.M12 - b.M12) < 1e-4f
		&& MathF.Abs(a.M21 - b.M21) < 1e-4f && MathF.Abs(a.M22 - b.M22) < 1e-4f;

	private static (Vector2 Offset, float Rotation, Vector2 Scale) Placement(Attachment drawing, int width, int height, Matrix3x2 toBone)
	{
		var before = Matrix3x2.CreateTranslation(-drawing.Pivot.X * width, -drawing.Pivot.Y * height) * Matrix3x2.CreateScale(1, -1);
		Matrix3x2.Invert(before, out var undo);
		var (position, rotation, scale) = MotionTransfer.Decompose(undo * toBone);
		return (position - drawing.Offset, rotation - drawing.Rotation,
			new Vector2(Ratio(scale.X, drawing.Scale.X), Ratio(scale.Y, drawing.Scale.Y)));
	}

	private static float Ratio(float a, float b) => MathF.Abs(b) > 1e-6f ? a / b : 1f;
}
