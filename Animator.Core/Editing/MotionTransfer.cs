using System.Numerics;

namespace Animator.Core.Editing;

// Another object's animation onto this one by where its parts show, frame by frame: for rigs
// that don't match bone for bone (two imports of similar characters each get a rig worked out
// from their own motion, with different bones in different places), the parts still match by
// name. Every frame of the source is posed; each bone here is put where its part (the one it
// carries that's matched, and shows the most) shows there, and its other matched parts get
// placement keys for whatever their bone doesn't cover. The result is keys on every frame.
//
// Made while both objects are posed alike (ObjectMerge lines them up): that pose says how each
// pair of drawings sits relative to each other (two imports can place the same picture a little
// differently), and the gap says how far apart the two stand.
public sealed class MotionTransfer
{
	private readonly Skeleton _source, _target;
	private readonly Dictionary<string, string> _sourceOf = new();   // part here → its source part
	private readonly Func<string, string, string> _drawingName;      // (source part, source drawing) → drawing name here
	private readonly Matrix3x2 _gap;
	private readonly Dictionary<(string Ours, string Theirs), Matrix3x2> _registration = new();

	public MotionTransfer(Skeleton source, Skeleton target, IReadOnlyDictionary<string, string?> parts,
		Func<string, string, string> drawingName, Vector2 gap)
	{
		_source = source;
		_target = target;
		_drawingName = drawingName;
		_gap = Matrix3x2.CreateTranslation(gap);
		foreach (var (theirs, ours) in parts)
			if (ours != null && target.FindSlot(ours) != null && source.FindSlot(theirs) != null && !_sourceOf.ContainsKey(ours))
				_sourceOf[ours] = theirs;

		// How each pair of drawings shown now sits relative to each other (posed alike)
		foreach (var (ours, theirs) in _sourceOf)
		{
			var s = source.FindSlot(theirs)!;
			var t = target.FindSlot(ours)!;
			if (source.ResolveAttachment(s) is not { } sd || t.ActiveAttachment is not { } shown || Drawing(t, shown) is not { } td) continue;
			if (ImageToWorld(source, s, sd, s.Offset, s.Rotation, s.Scale) is not { } ms ||
				ImageToWorld(target, t, td, t.Offset, t.Rotation, t.Scale) is not { } mt ||
				!Matrix3x2.Invert(ms * _gap, out var inv)) continue;
			_registration[(td.Name, sd.Name)] = mt * inv;
		}
	}

	// The keys that make `copy` (already holding the source's other rows, renamed for here) play
	// `animation` as the source shows it: its bone and part-placement rows are replaced
	public void Bake(Animation animation, Animation copy, int fps)
	{
		copy.Timelines.RemoveAll(t => t is RotateTimeline or TranslateTimeline or ScaleTimeline or DepthTimeline or SpaceTimeline
			or IkMixTimeline or PartTranslateTimeline or PartRotateTimeline or PartScaleTimeline);

		int frames = Math.Max(1, (int)MathF.Round(animation.Duration * Math.Max(1, fps)));
		var times = Enumerable.Range(0, frames + 1).Select(i => MathF.Min(i / (float)fps, animation.Duration)).Distinct().ToList();

		// Where each matched drawing here should show, frame by frame (null: the source hides it)
		var shown = new List<Dictionary<Slot, (Attachment Drawing, Matrix3x2 World)>>();
		foreach (float time in times)
		{
			_source.SetToSetupPose();
			animation.Apply(_source, time);
			_source.UpdatePose();
			var frame = new Dictionary<Slot, (Attachment, Matrix3x2)>();
			foreach (var (ours, theirs) in _sourceOf)
			{
				var s = _source.FindSlot(theirs)!;
				var t = _target.FindSlot(ours)!;
				if (_source.ResolveAttachment(s) is not { } sd || Drawing(t, _drawingName(theirs, sd.Name)) is not { } td) continue;
				if (ImageToWorld(_source, s, sd, s.Offset, s.Rotation, s.Scale) is not { } ms || Size(_target, td) is not { } size) continue;
				var registration = _registration.TryGetValue((td.Name, sd.Name), out var r) ? r : Centred(td, size, sd);
				frame[t] = (td, registration * ms * _gap);
			}
			shown.Add(frame);
		}
		_source.SetToSetupPose();
		_source.UpdatePose();

		// Each bone follows one of its matched parts: the one shown the most (then the biggest)
		var leader = new Dictionary<Bone, Slot>();
		foreach (var group in _target.Slots.Where(s => _sourceOf.ContainsKey(s.Name)).GroupBy(s => s.Bone))
			leader[group.Key] = group.OrderByDescending(s => shown.Count(f => f.ContainsKey(s)))
				.ThenByDescending(s => Area(s)).First();

		var bones = _target.AllBones().ToList();   // (parents first)
		var local = bones.ToDictionary(b => b, Setup);
		var placement = new Dictionary<Slot, (Vector2 Offset, float Rotation, Vector2 Scale)>();
		var boneKeys = bones.ToDictionary(b => b, _ => new List<(float, Vector2, float, Vector2)>());
		var partKeys = new Dictionary<Slot, List<(float, Vector2, float, Vector2)>>();
		var world = new Dictionary<Bone, Matrix3x2>();
		for (int f = 0; f < times.Count; f++)
		{
			var frame = shown[f];
			foreach (var bone in bones)
			{
				var parent = bone.Parent != null ? world[bone.Parent] : Matrix3x2.Identity;
				if (leader.TryGetValue(bone, out var lead) && frame.TryGetValue(lead, out var at) && Size(_target, at.Drawing) is { } size
					&& Matrix3x2.Invert(at.Drawing.LocalMatrix(size.X, size.Y), out var toBone)
					&& Matrix3x2.Invert(parent, out var fromParent))
					local[bone] = toBone * at.World * fromParent;   // (else as it was the frame before)
				world[bone] = local[bone] * parent;
				var (position, rotation, scale) = Decompose(local[bone]);
				if (boneKeys[bone].Count > 0) rotation = Near(rotation, boneKeys[bone][^1].Item3 + bone.SetupRotation);
				boneKeys[bone].Add((times[f], position - bone.SetupPosition, rotation - bone.SetupRotation,
					new Vector2(Ratio(scale.X, bone.SetupScale.X), Ratio(scale.Y, bone.SetupScale.Y))));
			}
			foreach (var slot in _target.Slots.Where(s => _sourceOf.ContainsKey(s.Name)))
			{
				if (leader.GetValueOrDefault(slot.Bone) == slot) continue;   // its bone puts it there
				if (frame.TryGetValue(slot, out var at) && Size(_target, at.Drawing) is { } size
					&& Matrix3x2.Invert(world[slot.Bone], out var fromBone))
					placement[slot] = Placement(at.Drawing, size, at.World * fromBone);
				if (!placement.TryGetValue(slot, out var p)) continue;
				if (!partKeys.TryGetValue(slot, out var keys)) partKeys[slot] = keys = new();
				if (keys.Count > 0) p.Rotation = Near(p.Rotation, keys[^1].Item3);
				keys.Add((times[f], p.Offset, p.Rotation, p.Scale));
			}
		}

		foreach (var (bone, keys) in boneKeys)
		{
			if (keys.Any(k => k.Item2.Length() > 0.01f)) Add(copy, new TranslateTimeline(bone.Name), keys, k => k.Item2);
			if (keys.Any(k => MathF.Abs(k.Item3) > 1e-4f)) Add(copy, new RotateTimeline(bone.Name), keys, k => k.Item3);
			if (keys.Any(k => (k.Item4 - Vector2.One).Length() > 1e-4f)) Add(copy, new ScaleTimeline(bone.Name), keys, k => k.Item4);
		}
		foreach (var (slot, keys) in partKeys)
		{
			if (keys.Any(k => k.Item2.Length() > 0.01f)) Add(copy, new PartTranslateTimeline(slot.Name), keys, k => k.Item2);
			if (keys.Any(k => MathF.Abs(k.Item3) > 1e-4f)) Add(copy, new PartRotateTimeline(slot.Name), keys, k => k.Item3);
			if (keys.Any(k => (k.Item4 - Vector2.One).Length() > 1e-4f)) Add(copy, new PartScaleTimeline(slot.Name), keys, k => k.Item4);
		}

		// Parts here the source hasn't: the ones only in skins (a hat, a hairflap) show and hide
		// with the part their bone follows; the others stay hidden (the source shows nothing there)
		foreach (var slot in _target.Slots.Where(s => !_sourceOf.ContainsKey(s.Name)))
		{
			if (copy.Timelines.OfType<AttachmentTimeline>().Any(x => x.Target == slot.Name)) continue;
			bool onlyInSkins = !_target.DefaultSkin.AttachmentsFor(slot.Name).Any();
			var row = new AttachmentTimeline(slot.Name);
			if (onlyInSkins)
			{
				if (leader.GetValueOrDefault(slot.Bone) is not { } lead
					|| copy.Timelines.OfType<AttachmentTimeline>().FirstOrDefault(x => x.Target == lead.Name) is not { } theirs) continue;
				foreach (var key in theirs.Track.Keys) row.Add(key.Time, key.Value == null ? null : slot.SetupAttachment);
			}
			else row.Add(0, null);
			copy.Timelines.Add(row);
		}
	}

	// ======================= the maths =======================

	private Attachment? Drawing(Slot slot, string name) =>
		_target.DefaultSkin.Get(slot.Name, name) ?? _target.AllSkins().Select(s => s.Get(slot.Name, name)).FirstOrDefault(a => a != null);

	private static (int X, int Y)? Size(Skeleton skeleton, Attachment drawing) =>
		skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) ? (asset.Width, asset.Height) : null;

	private float Area(Slot slot) =>
		slot.SetupAttachment is { } name && Drawing(slot, name) is { } d && Size(_target, d) is { } s ? s.X * s.Y : 0;

	private static Matrix3x2? ImageToWorld(Skeleton skeleton, Slot slot, Attachment drawing, Vector2 offset, float rotation, Vector2 scale) =>
		Size(skeleton, drawing) is { } size ? drawing.LocalMatrix(size.X, size.Y, offset, rotation, scale) * slot.Bone.World : null;

	// Two drawings never seen together: their middles on each other, each at its own pixel size
	private Matrix3x2 Centred(Attachment ours, (int X, int Y) size, Attachment theirs)
	{
		var other = Size(_source, theirs) ?? size;
		return Matrix3x2.CreateTranslation(-size.X / 2f, -size.Y / 2f)
			* Matrix3x2.CreateScale(Ratio(ours.Scale.X, theirs.Scale.X), Ratio(ours.Scale.Y, theirs.Scale.Y))
			* Matrix3x2.CreateTranslation(other.X / 2f, other.Y / 2f);
	}

	private static Matrix3x2 Setup(Bone bone) =>
		Matrix3x2.CreateScale(bone.SetupScale) * Matrix3x2.CreateRotation(bone.SetupRotation) * Matrix3x2.CreateTranslation(bone.SetupPosition);

	// A scale, then a turn, then a move (bones and drawings are put together that way)
	public static (Vector2 Position, float Rotation, Vector2 Scale) Decompose(Matrix3x2 m)
	{
		float angle = MathF.Atan2(m.M12, m.M11);
		float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
		float sy = m.M22 * MathF.Cos(angle) - m.M21 * MathF.Sin(angle);
		return (m.Translation, angle, new Vector2(sx, sy));
	}

	// The placement keys that put a drawing at `toBone` (image → bone) on its part
	private static (Vector2 Offset, float Rotation, Vector2 Scale) Placement(Attachment drawing, (int X, int Y) size, Matrix3x2 toBone)
	{
		var before = Matrix3x2.CreateTranslation(-drawing.Pivot.X * size.X, -drawing.Pivot.Y * size.Y) * Matrix3x2.CreateScale(1, -1);
		Matrix3x2.Invert(before, out var undo);
		var (position, rotation, scale) = Decompose(undo * toBone);
		return (position - drawing.Offset, rotation - drawing.Rotation,
			new Vector2(Ratio(scale.X, drawing.Scale.X), Ratio(scale.Y, drawing.Scale.Y)));
	}

	private static float Ratio(float a, float b) => MathF.Abs(b) > 1e-6f ? a / b : 1f;

	// The same angle, the turn nearest the one before (so keys don't spin the long way round)
	private static float Near(float angle, float previous) =>
		angle + 2 * MathF.PI * MathF.Round((previous - angle) / (2 * MathF.PI));

	private static void Add<T>(Animation copy, TrackTimeline<T> row, List<(float, Vector2, float, Vector2)> keys,
		Func<(float, Vector2, float, Vector2), T> value)
	{
		foreach (var k in keys) row.Track.Add(k.Item1, value(k), CurveType.Linear);
		copy.Timelines.Add(row);
	}
}
