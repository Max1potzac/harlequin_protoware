using System.Numerics;

namespace Animator.Core.Editing;

// Another object's animation onto this one by where its parts show, frame by frame: for rigs
// that don't match bone for bone (two imports of similar characters each get a rig worked out
// from their own motion, with different bones in different places), the parts still match by
// name. Every frame of the source is posed; each bone here is put where its part (the one it
// carries that's matched, and shows the most) shows there, and its other matched parts get
// placement keys for whatever their bone doesn't cover. Which drawing each part shows, frame by
// frame, follows the source too. The result is keys on every frame.
//
// Where a drawing here and the source's sit relative to each other: through their symbols, when
// both are known (games' characters converted from Animate: the same symbols, other pictures in
// them), else as they show while the two are posed alike (made then: ObjectMerge lines them up).
public sealed class MotionTransfer
{
	private readonly Skeleton _source, _target;
	private readonly Dictionary<string, string> _sourceOf = new();   // part here → its source part
	private readonly Func<string, string, string> _drawingName;      // (source part, source drawing) → drawing name here
	private readonly Matrix3x2 _space;                               // the source's object space → this one's
	private readonly IDictionary<Attachment, Matrix3x2>? _symbols;   // a drawing's pixels → its symbol's space
	private readonly Func<Slot, Slot, Attachment, Attachment?>? _supply;   // (part here, source part, source drawing) → a copy here
	private readonly Dictionary<(string Ours, string Theirs), Matrix3x2> _registration = new();
	// A limb here turned to theirs (in its symbol's space) and stretched along itself (how far, and
	// which way, in its symbol's space): see FitLimbs
	private readonly Dictionary<Slot, (Matrix3x2 Turn, Vector2 Along, float Stretch)> _fit = new();

	public MotionTransfer(Skeleton source, Skeleton target, IReadOnlyDictionary<string, string?> parts,
		Func<string, string, string> drawingName, Matrix3x2 space,
		IDictionary<Attachment, Matrix3x2>? symbols = null, Func<Slot, Slot, Attachment, Attachment?>? supply = null)
	{
		_source = source;
		_target = target;
		_drawingName = drawingName;
		_space = space;
		_symbols = symbols;
		_supply = supply;
		foreach (var (theirs, ours) in parts)
			if (ours != null && target.FindSlot(ours) != null && source.FindSlot(theirs) != null && !_sourceOf.ContainsKey(ours))
				_sourceOf[ours] = theirs;

		// How each pair of drawings shown now sits relative to each other (posed alike)
		foreach (var (ours, theirs) in _sourceOf)
		{
			var s = source.FindSlot(theirs)!;
			var t = target.FindSlot(ours)!;
			if (source.ResolveAttachment(s) is not { } sd || t.ActiveAttachment is not { } shown || Drawing(t, shown) is not { } td) continue;
			if (ImageToWorld(source, s, sd) is not { } ms || ImageToWorld(target, t, td) is not { } mt ||
				!Matrix3x2.Invert(ms * _space, out var inv)) continue;
			_registration[(td.Name, sd.Name)] = mt * inv;
		}
		if (_symbols != null) FitLimbs();
	}

	// Limbs drawn longer or shorter than theirs: each is turned and stretched along itself so it
	// reaches from its own joint to where theirs holds the next part (a shin from the knee to
	// the foot), or the next part would show apart from it. Measured as both are posed now, in
	// each part's symbol space: the joint of the one part it leads to (its bone's only child here
	// with a match, shown in both).
	private void FitLimbs()
	{
		Matrix3x2? Symbol(Skeleton skeleton, Slot slot)
		{
			var drawing = skeleton == _target ? (slot.ActiveAttachment is { } name ? Drawing(slot, name) : null) : skeleton.ResolveAttachment(slot);
			if (drawing == null || !_symbols!.TryGetValue(drawing, out var toSymbol) || ImageToWorld(skeleton, slot, drawing) is not { } world
				|| !Matrix3x2.Invert(toSymbol, out var fromPixels)) return null;
			return fromPixels * world;   // symbol → world
		}
		foreach (var (ours, theirs) in _sourceOf)
		{
			var t = _target.FindSlot(ours)!;
			var s = _source.FindSlot(theirs)!;
			var next = _target.Slots.Where(c => c != t && c.Bone.Parent == t.Bone && _sourceOf.ContainsKey(c.Name)
				&& Symbol(_target, c) != null && Symbol(_source, _source.FindSlot(_sourceOf[c.Name])!) != null).ToList();
			if (next.Count != 1 || Symbol(_target, t) is not { } tSpace || Symbol(_source, s) is not { } sSpace
				|| !Matrix3x2.Invert(tSpace, out var intoT) || !Matrix3x2.Invert(sSpace, out var intoS)) continue;
			var c = next[0];
			var reachT = Vector2.Transform(Symbol(_target, c)!.Value.Translation, intoT);
			var reachS = Vector2.Transform(Symbol(_source, _source.FindSlot(_sourceOf[c.Name])!)!.Value.Translation, intoS);
			if (reachT.Length() < 2 || reachS.Length() < 2) continue;
			float stretch = Math.Clamp(reachS.Length() / reachT.Length(), 0.6f, 1.6f);
			_fit[t] = (Matrix3x2.CreateRotation(MathF.Atan2(reachS.Y, reachS.X) - MathF.Atan2(reachT.Y, reachT.X)),
				Vector2.Normalize(reachT), stretch);
		}
	}

	// The keys that make `copy` (already holding the source's other rows, renamed for here) play
	// `animation` as the source shows it: its bone, part-placement and drawing rows are replaced
	public void Bake(Animation animation, Animation copy, int fps)
	{
		copy.Timelines.RemoveAll(t => t is RotateTimeline or TranslateTimeline or ScaleTimeline or DepthTimeline or SpaceTimeline
			or IkMixTimeline or PartTranslateTimeline or PartRotateTimeline or PartScaleTimeline
			|| t is AttachmentTimeline a && _sourceOf.ContainsKey(a.Target));

		int frames = Math.Max(1, (int)MathF.Round(animation.Duration * Math.Max(1, fps)));
		var times = Enumerable.Range(0, frames + 1).Select(i => MathF.Min(i / (float)fps, animation.Duration)).Distinct().ToList();

		// Where each matched drawing here should show, frame by frame (missing: the source hides it)
		// (World: where it goes; Plain: the same, not stretched: what its bone follows)
		var shown = new List<Dictionary<Slot, (Attachment Drawing, Matrix3x2 Plain, Matrix3x2 World)>>();
		foreach (float time in times)
		{
			_source.SetToSetupPose();
			animation.Apply(_source, time);
			_source.UpdatePose();
			var frame = new Dictionary<Slot, (Attachment, Matrix3x2, Matrix3x2)>();
			foreach (var (ours, theirs) in _sourceOf)
			{
				var s = _source.FindSlot(theirs)!;
				var t = _target.FindSlot(ours)!;
				if (_source.ResolveAttachment(s) is not { } sd || DrawingFor(t, s, sd) is not { } td) continue;
				if (ImageToWorld(_source, s, sd) is not { } ms || Size(_target, td) is not { } size) continue;
				var plain = Registration(t, td, size, sd) * ms * _space;
				frame[t] = (td, plain, Stretch(t, td, size) * plain);
			}
			shown.Add(frame);
		}
		_source.SetToSetupPose();
		_source.UpdatePose();

		// Each bone follows one of its matched parts: the one named after it (a rig with a bone per
		// part), else the one shown the most, then the biggest. Never the object's own bone: it
		// stays put, and what's on it gets placement keys (a root following a part would carry the
		// whole rig along, everything else pulling back against it between frames).
		var leader = new Dictionary<Bone, Slot>();
		foreach (var group in _target.Slots.Where(s => _sourceOf.ContainsKey(s.Name) && s.Bone.Parent != null).GroupBy(s => s.Bone))
			leader[group.Key] = group.OrderByDescending(s => s.Name == group.Key.Name)
				.ThenByDescending(s => shown.Count(f => f.ContainsKey(s)))
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
					local[bone] = toBone * at.Plain * fromParent;   // (else as it was the frame before, on its parent)
				world[bone] = local[bone] * parent;
				var (position, rotation, scale) = Decompose(local[bone]);
				var keys = boneKeys[bone];
				(rotation, scale) = Continue(rotation, scale, keys.Count > 0 ? keys[^1].Item3 + bone.SetupRotation : bone.SetupRotation);
				keys.Add((times[f], position - bone.SetupPosition, rotation - bone.SetupRotation,
					new Vector2(Ratio(scale.X, bone.SetupScale.X), Ratio(scale.Y, bone.SetupScale.Y))));
			}
			foreach (var slot in _target.Slots.Where(s => _sourceOf.ContainsKey(s.Name)))
			{
				if (leader.GetValueOrDefault(slot.Bone) == slot && !_fit.ContainsKey(slot)) continue;   // its bone puts it there
				if (frame.TryGetValue(slot, out var at) && Size(_target, at.Drawing) is { } size
					&& Matrix3x2.Invert(world[slot.Bone], out var fromBone))
					placement[slot] = Placement(at.Drawing, size, at.World * fromBone);
				if (!placement.TryGetValue(slot, out var p)) continue;
				if (!partKeys.TryGetValue(slot, out var keys)) partKeys[slot] = keys = new();
				(p.Rotation, p.Scale) = Continue(p.Rotation, p.Scale, keys.Count > 0 ? keys[^1].Item3 : 0f);
				placement[slot] = p;
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

		// Which drawing each matched part shows: as the source shows its own, frame by frame
		foreach (var slot in _target.Slots.Where(s => _sourceOf.ContainsKey(s.Name)))
		{
			var row = new AttachmentTimeline(slot.Name);
			string? before = "\0";
			for (int f = 0; f < times.Count; f++)
			{
				string? now = shown[f].TryGetValue(slot, out var at) ? at.Drawing.Name : null;
				if (now == before) continue;
				row.Add(times[f], now);
				before = now;
			}
			copy.Timelines.Add(row);
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

	// ======================= drawings =======================

	// The drawing here for one of the source's: by name; else a copy of it (a hand this object
	// has nothing like, see ObjectMerge); else the part's own rest drawing, in its place
	private Attachment? DrawingFor(Slot ours, Slot theirs, Attachment drawing) =>
		Drawing(ours, _drawingName(theirs.Name, drawing.Name))
		?? _supply?.Invoke(ours, theirs, drawing)
		?? (ours.SetupAttachment is { } rest ? Drawing(ours, rest) : null);

	private Attachment? Drawing(Slot slot, string name) =>
		_target.DefaultSkin.Get(slot.Name, name) ?? _target.AllSkins().Select(s => s.Get(slot.Name, name)).FirstOrDefault(a => a != null);

	// Our drawing's pixels → theirs: through their symbols (exact), else as they showed posed
	// alike, else their middles on each other
	private Matrix3x2 Registration(Slot slot, Attachment ours, (int X, int Y) size, Attachment theirs)
	{
		if (_symbols != null && _symbols.TryGetValue(ours, out var mine) && _symbols.TryGetValue(theirs, out var other)
			&& Matrix3x2.Invert(other, out var fromOther))
			return mine * (_fit.TryGetValue(slot, out var fit) ? fit.Turn : Matrix3x2.Identity) * fromOther;
		if (_registration.TryGetValue((ours.Name, theirs.Name), out var seen)) return seen;
		return Centred(ours, size, theirs);
	}

	// A fitted limb's picture stretched along itself, about its joint (in its pixels: along the
	// picture's own side nearest the limb's length, so a drawing's own scale can carry it)
	private Matrix3x2 Stretch(Slot slot, Attachment ours, (int X, int Y) size)
	{
		if (!_fit.TryGetValue(slot, out var fit) || MathF.Abs(fit.Stretch - 1) < 1e-3f || _symbols == null
			|| !_symbols.TryGetValue(ours, out var toSymbol) || !Matrix3x2.Invert(toSymbol, out var fromSymbol)) return Matrix3x2.Identity;
		var joint = Vector2.Transform(Vector2.Zero, fromSymbol);
		var along = Vector2.TransformNormal(fit.Along, fromSymbol);
		var scale = MathF.Abs(along.Y) >= MathF.Abs(along.X) ? new Vector2(1, fit.Stretch) : new Vector2(fit.Stretch, 1);
		return Matrix3x2.CreateTranslation(-joint) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(joint);
	}

	// Two drawings never seen together: their middles on each other, each at its own pixel size
	private Matrix3x2 Centred(Attachment ours, (int X, int Y) size, Attachment theirs)
	{
		var other = Size(_source, theirs) ?? size;
		return Matrix3x2.CreateTranslation(-size.X / 2f, -size.Y / 2f)
			* Matrix3x2.CreateScale(Ratio(ours.Scale.X, theirs.Scale.X), Ratio(ours.Scale.Y, theirs.Scale.Y))
			* Matrix3x2.CreateTranslation(other.X / 2f, other.Y / 2f);
	}

	private static (int X, int Y)? Size(Skeleton skeleton, Attachment drawing) =>
		skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) ? (asset.Width, asset.Height) : null;

	private float Area(Slot slot) =>
		slot.SetupAttachment is { } name && Drawing(slot, name) is { } d && Size(_target, d) is { } s ? s.X * s.Y : 0;

	private static Matrix3x2? ImageToWorld(Skeleton skeleton, Slot slot, Attachment drawing) =>
		Size(skeleton, drawing) is { } size ? drawing.LocalMatrix(size.X, size.Y, slot.Offset, slot.Rotation, slot.Scale) * slot.Bone.World : null;

	// ======================= the maths =======================

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

	// The same turn and scale written the way nearest the frame before: the turn nearest it, and
	// a half turn with both scales negated where that's nearer (a part flipping over, or turning
	// past upside down, would otherwise spin round between frames as it plays)
	public static (float Rotation, Vector2 Scale) Continue(float rotation, Vector2 scale, float previous)
	{
		float plain = Near(rotation, previous);
		float flipped = Near(rotation + MathF.PI, previous);
		return MathF.Abs(plain - previous) <= MathF.Abs(flipped - previous) ? (plain, scale) : (flipped, -scale);
	}

	// The placement keys that put a drawing at `toBone` (image → bone) on its part
	private static (Vector2 Offset, float Rotation, Vector2 Scale) Placement(Attachment drawing, (int X, int Y) size, Matrix3x2 toBone)
	{
		var before = Matrix3x2.CreateTranslation(-drawing.Pivot.X * size.X, -drawing.Pivot.Y * size.Y) * Matrix3x2.CreateScale(1, -1);
		Matrix3x2.Invert(before, out var undo);
		var (position, rotation, scale) = Decompose(undo * toBone);
		// (placement turns and scales on top of the drawing's own, about the pivot at its offset)
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
