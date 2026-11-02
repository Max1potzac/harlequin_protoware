using System.Numerics;
using Animator.Core.Serialization;

namespace Animator.Core.Editing;

// Bringing another object in: its animations, and its drawings as a skin (one zombie's head on
// another's body). The two are matched by name: bones to bones, parts to parts, drawings to
// drawings. Names often differ only by what they start with ("zombie_egypt_jaw" and
// "zombie_pirate_basic_jaw"), so each side's usual start (its prefix) is set aside to match them.
//
// Animations are copied key by key onto the matched bones and parts. Bone keys are offsets from
// the rest pose; "as they look" (Absolute) works them out again from this object's rest pose,
// so a pose looks as it did even where the two rests differ.
public sealed class MergePlan
{
	public string SourcePrefix { get; set; } = "";
	public string TargetPrefix { get; set; } = "";
	public Dictionary<string, string?> Bones { get; } = new();   // source bone → this object's (null: none)
	public Dictionary<string, string?> Parts { get; } = new();   // source part → this object's (null: none)
	public HashSet<string> NewParts { get; } = new();            // source parts with no match, added as new parts
	public HashSet<string> SkinParts { get; } = new();           // source parts whose drawings go into the skin

	public List<string> Animations { get; } = new();             // the source's animations to copy
	public string Suffix { get; set; } = "";                     // added to their names ("walk" + "_mummy")
	public bool Absolute { get; set; } = true;                   // poses as they look (else as offsets from rest)

	// Where the drawings land: placed on their bones as they were on the other object's (false), or
	// where they show in the other object (true: right for characters that share animations; lined
	// up at the start of an animation both have, idle if they have it, else at rest)
	public bool AsPlaced { get; set; }

	// The animations by where the parts show, frame by frame (MotionTransfer), not by bone: for
	// rigs that don't match bone for bone. Needs AsPlaced (the two lined up).
	public bool ByParts { get; set; }
	public int Fps { get; set; } = 30;                           // ByParts: keys on every frame of this rate

	// Exact placement, for objects imported from Animate projects whose parts are symbols (games'
	// characters): each drawing's pixels → its symbol's own space, for drawings of both objects
	// (filled in as drawings are made), and the source object's space → this one's. With these, a
	// drawing goes where its symbol puts it, whatever pose the two are in; without, they're lined
	// up by how they show (see AsPlaced).
	public IDictionary<Attachment, Matrix3x2>? Symbols { get; set; }
	public Matrix3x2? Space { get; set; }
	// ByParts: the parts here that take the source's drawing where they have none like it (hands)
	public Func<string, bool>? CopyMissing { get; set; }

	public string? SkinName { get; set; }                        // null: no skin
	public string SkinCategory { get; set; } = "";
	public bool SkinOverlay { get; set; }
}

public sealed record MergeResult(int Animations, int Keys, int DroppedTimelines, Skin? Skin, List<Slot> NewParts,
	Dictionary<Guid, Guid> LayerSources, List<string> Notes);

public static class ObjectMerge
{
	// ======================= names =======================

	// The start most of these names share ("zombie_egypt_"), or ""
	public static string GuessPrefix(IEnumerable<string> names)
	{
		var list = names.Where(n => n.Length > 0).Distinct().ToList();
		if (list.Count < 2) return "";
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (string n in list)
			for (int i = 1; i < n.Length - 1; i++)
				if (n[i] == '_')
				{
					string prefix = n[..(i + 1)];
					counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
				}
		double enough = Math.Max(2, list.Count * 0.4);
		return counts.Where(c => c.Value >= enough).OrderByDescending(c => c.Key.Length).Select(c => c.Key).FirstOrDefault() ?? "";
	}

	public static string Core(string name, string prefix) =>
		prefix.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length ? name[prefix.Length..] : name;

	// A source name as this object would say it: its prefix for the source's
	public static string Renamed(string name, string sourcePrefix, string targetPrefix) =>
		sourcePrefix.Length > 0 && name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) && name.Length > sourcePrefix.Length
			? targetPrefix + name[sourcePrefix.Length..]
			: name;

	// Each source name's match among these: the same name; else the same once the prefixes are set
	// aside; else the same but for a number at the end (foot_toe_01 ↔ foot_toe), when that's the
	// only one on each side
	public static Dictionary<string, string?> Match(IEnumerable<string> source, IEnumerable<string> target, string sourcePrefix, string targetPrefix)
	{
		var names = target.Distinct().ToList();
		var sources = source.Distinct().ToList();
		static string Bare(string core) => System.Text.RegularExpressions.Regex.Replace(core, @"_?\d+$", "").ToLowerInvariant();
		var bareTargets = names.GroupBy(n => Bare(Core(n, targetPrefix))).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
		var bareSources = sources.GroupBy(n => Bare(Core(n, sourcePrefix))).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
		var exact = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var byCore = names.GroupBy(n => Core(n, targetPrefix), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var result = new Dictionary<string, string?>();
		foreach (string s in sources)
		{
			string bare = Bare(Core(s, sourcePrefix));
			result[s] = exact.TryGetValue(s, out var same) ? same
				: byCore.TryGetValue(Core(s, sourcePrefix), out var core) ? core
				: bare.Length > 0 && bareSources.Contains(bare) && bareTargets.TryGetValue(bare, out var near) ? near
				: null;
		}
		// (a name taken by an earlier, exact match isn't given out again to a near one)
		var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string s in sources)
			if (result[s] is { } t && (exact.ContainsKey(s) || byCore.ContainsKey(Core(s, sourcePrefix)))) taken.Add(t);
		foreach (string s in sources)
			if (result[s] is { } t && !exact.ContainsKey(s) && !byCore.ContainsKey(Core(s, sourcePrefix)) && taken.Contains(t)) result[s] = null;
		return result;
	}

	// A plan to start from: everything matched by name, every animation, no skin
	public static MergePlan Suggest(Skeleton source, IEnumerable<Animation> sourceAnimations, Skeleton target)
	{
		var sourceNames = source.AllBones().Select(b => b.Name).Concat(source.Slots.Select(s => s.Name)).ToList();
		var targetNames = target.AllBones().Select(b => b.Name).Concat(target.Slots.Select(s => s.Name)).ToList();
		var plan = new MergePlan { SourcePrefix = GuessPrefix(sourceNames), TargetPrefix = GuessPrefix(targetNames) };
		foreach (var (s, t) in Match(source.AllBones().Select(b => b.Name), target.AllBones().Select(b => b.Name), plan.SourcePrefix, plan.TargetPrefix))
			plan.Bones[s] = t;
		foreach (var (s, t) in Match(source.Slots.Select(x => x.Name), target.Slots.Select(x => x.Name), plan.SourcePrefix, plan.TargetPrefix))
			plan.Parts[s] = t;
		plan.Animations.AddRange(sourceAnimations.Select(a => a.Name));
		return plan;
	}

	// The source parts that would go on these bones here (or the bones under them): the matched
	// ones (their match is on one), and the ones with no match whose bone, or a bone above it,
	// matches one. A head: pick the head bone, and these are its parts and its accessories.
	public static (List<string> Matched, List<string> New) PartsOn(Skeleton source, Skeleton target, MergePlan plan, IReadOnlyCollection<Bone> bones)
	{
		bool Under(Bone? b) => b != null && bones.Any(p => b == p || b.IsDescendantOf(p));
		var matched = new List<string>();
		var made = new List<string>();
		foreach (var slot in source.Slots)
		{
			if (plan.Parts.GetValueOrDefault(slot.Name) is { } name && target.FindSlot(name) is { } here)
			{
				if (Under(here.Bone)) matched.Add(slot.Name);
				continue;
			}
			Bone? bone = null;
			for (var b = slot.Bone; b != null && bone == null; b = b.Parent)
				if (plan.Bones.GetValueOrDefault(b.Name) is { } n) bone = target.FindBone(n);
			if (Under(bone)) made.Add(slot.Name);
		}
		return (matched, made);
	}

	// A name for what a file brings: "zombie_egypt_basic.pam.xfl" → "egypt"
	public static string ShortName(string name)
	{
		string n = name;
		foreach (string end in new[] { ".xfl", ".fla", ".zip", ".pam", ".json" })
			if (n.EndsWith(end, StringComparison.OrdinalIgnoreCase)) n = n[..^end.Length];
		if (n.StartsWith("zombie_", StringComparison.OrdinalIgnoreCase) && n.Length > 7) n = n[7..];
		n = n.Replace("_basic", "", StringComparison.OrdinalIgnoreCase);
		return n.Trim('_').Length > 0 ? n.Trim('_') : name;
	}

	// ======================= bringing it in =======================

	public static MergeResult Merge(Skeleton source, IReadOnlyList<Animation> sourceAnimations, Skeleton target, List<Animation> targetAnimations, MergePlan plan)
	{
		var notes = new List<string>();
		var layerSources = new Dictionary<Guid, Guid>();   // new layer → the source layer it copies
		var parts = new Dictionary<string, string?>(plan.Parts);

		// ----- parts this object hasn't: added, on the bone their own is matched to -----
		var added = new List<Slot>();
		for (int i = 0; i < source.Slots.Count; i++)
		{
			var s = source.Slots[i];
			if (!plan.NewParts.Contains(s.Name) || parts.GetValueOrDefault(s.Name) is { } already && target.FindSlot(already) != null) continue;
			var slot = new Slot(PartOps.UniqueSlotName(target, Renamed(s.Name, plan.SourcePrefix, plan.TargetPrefix)), BoneFor(s.Bone, target, plan))
			{
				Blend = s.Blend,
				SetupColor = s.SetupColor,
				SetupOpacity = s.SetupOpacity
			};
			// In the draw order just in front of the part the one below it went to
			int at = 0;
			for (int j = i - 1; j >= 0; j--)
				if (parts.GetValueOrDefault(source.Slots[j].Name) is { } below && target.FindSlot(below) is { } belowSlot)
				{
					at = target.Slots.IndexOf(belowSlot) + 1;
					break;
				}
			target.Slots.Insert(at, slot);
			parts[s.Name] = slot.Name;
			added.Add(slot);
		}

		// ----- drawings: each source drawing's name here -----
		var drawingNames = new Dictionary<(string Part, string Drawing), string>();
		foreach (var (sourcePart, targetPart) in parts)
		{
			if (targetPart == null) continue;
			var theirs = source.DefaultSkin.AttachmentsFor(sourcePart).Select(a => a.Name).ToList();
			var ours = target.DefaultSkin.AttachmentsFor(targetPart).Select(a => a.Name).ToList();
			var matched = Match(theirs, ours, plan.SourcePrefix, plan.TargetPrefix);
			foreach (string d in theirs)
				drawingNames[(sourcePart, d)] = matched[d]
					?? (theirs.Count == 1 && ours.Count == 1 ? ours[0] : Renamed(d, plan.SourcePrefix, plan.TargetPrefix));
		}
		string DrawingName(string sourcePart, string drawing) =>
			drawingNames.TryGetValue((sourcePart, drawing), out var n) ? n : Renamed(drawing, plan.SourcePrefix, plan.TargetPrefix);

		// ----- the skin (and the new parts' drawings) -----
		Skin? skin = null;
		if (plan.SkinName is { } skinName)
		{
			skin = new Skin { Name = SkinOps.UniqueName(target, skinName), Category = plan.SkinCategory.Trim(), Overlay = plan.SkinOverlay };
			target.Skins.Add(skin);
		}
		var copies = new Dictionary<Guid, Asset>();
		int copiedDrawings = 0;
		bool meshesDropped = false;
		var gap = plan.AsPlaced ? LineUp(source, sourceAnimations, target, targetAnimations, plan, parts, notes) : Matrix3x2.Identity;
		var companions = skin != null && plan.AsPlaced ? Companions(source, target, plan, parts, added) : new Dictionary<Slot, Slot>();
		var transfer = plan.ByParts && plan.AsPlaced && plan.Animations.Count > 0
			? new MotionTransfer(source, target, parts, DrawingName, gap, plan.Symbols, plan.CopyMissing != null ? Supply : null,
				sourceAnimations, targetAnimations.ToList()) : null;   // (posed alike now)
		// A drawing placed through its symbol (see MergePlan.Symbols): measured against the part
		// here it lands on, or the one it rides on (its companion), as Animate draws it there.
		// False: there's nothing to measure it against that way.
		bool OnSymbol(Attachment made, Attachment drawing, Slot s, Slot here)
		{
			if (plan.Symbols is not { } symbols || !symbols.TryGetValue(drawing, out var mine)) return false;
			var anchor = companions.TryGetValue(here, out var c) ? c : here;
			if (added.Contains(anchor) || OwnDrawing(target, anchor) is not { } theirs || !symbols.TryGetValue(theirs, out var anchorSymbol)
				|| Size(target, theirs) is not { } anchorSize || Size(source, drawing) is not { } size) return false;
			var toSymbol = mine;   // its pixels → the anchor's symbol space (in the source)
			if (anchor != here)
			{
				var cs = source.Slots.FirstOrDefault(x => parts.GetValueOrDefault(x.Name) == anchor.Name);
				if (cs == null || source.ResolveAttachment(cs) is not { } shown || !symbols.TryGetValue(shown, out var shownSymbol)
					|| Size(source, shown) is not { } shownSize) return false;
				var world = drawing.LocalMatrix(size.X, size.Y, s.Offset, s.Rotation, s.Scale) * s.Bone.World;
				var anchorWorld = shown.LocalMatrix(shownSize.X, shownSize.Y, cs.Offset, cs.Rotation, cs.Scale) * cs.Bone.World;
				if (!Matrix3x2.Invert(anchorWorld, out var fromAnchor)) return false;
				toSymbol = world * fromAnchor * shownSymbol;
			}
			if (!Matrix3x2.Invert(anchorSymbol, out var fromSymbol)) return false;
			SetLocal(made, size, toSymbol * fromSymbol * theirs.LocalMatrix(anchorSize.X, anchorSize.Y));
			return true;
		}

		// ByParts: a drawing of the source's, copied onto a part here that has none like it
		Attachment? Supply(Slot here, Slot s, Attachment drawing)
		{
			if (plan.CopyMissing?.Invoke(here.Name) != true || !source.Assets.TryGetValue(drawing.AssetId, out var asset)) return null;
			if (!copies.TryGetValue(asset.Id, out var copy))
				copies[asset.Id] = copy = CopyAsset(asset, target, layerSources);
			var made = new Attachment
			{
				Name = DrawingName(s.Name, drawing.Name),
				AssetId = copy.Id,
				Offset = drawing.Offset,
				Rotation = drawing.Rotation,
				Scale = drawing.Scale,
				Pivot = drawing.Pivot
			};
			if (target.DefaultSkin.Get(here.Name, made.Name) != null) made.Name = UniqueDrawingName(target, here, made.Name);
			if (!OnSymbol(made, drawing, s, here)) PlaceAsShown(made, drawing, s, here, gap);
			if (plan.Symbols != null && plan.Symbols.TryGetValue(drawing, out var symbol)) plan.Symbols[made] = symbol;
			target.DefaultSkin.Set(here.Name, made);
			copiedDrawings++;
			return made;
		}

		foreach (var s in source.Slots)
		{
			if (parts.GetValueOrDefault(s.Name) is not { } targetPart) continue;
			bool isNew = added.Any(a => a.Name == targetPart);
			bool inSkin = skin != null && plan.SkinParts.Contains(s.Name);
			if (!inSkin && !isNew) continue;
			var into = inSkin ? skin! : target.DefaultSkin;
			foreach (var drawing in source.DefaultSkin.AttachmentsFor(s.Name).ToList())
			{
				if (!source.Assets.TryGetValue(drawing.AssetId, out var asset)) continue;
				if (!copies.TryGetValue(asset.Id, out var copy))
					copies[asset.Id] = copy = CopyAsset(asset, target, layerSources);
				meshesDropped |= drawing.Mesh != null;
				var made = new Attachment
				{
					Name = DrawingName(s.Name, drawing.Name),
					AssetId = copy.Id,
					Offset = drawing.Offset,
					Rotation = drawing.Rotation,
					Scale = drawing.Scale,
					Pivot = drawing.Pivot
				};
				if (plan.AsPlaced && target.FindSlot(targetPart) is { } here && !OnSymbol(made, drawing, s, here))
					PlaceAsShown(made, drawing, s, here, gap);
				if (plan.Symbols != null && plan.Symbols.TryGetValue(drawing, out var symbol)) plan.Symbols[made] = symbol;
				into.Set(targetPart, made);
			}
		}
		foreach (var slot in added)
		{
			var s = source.Slots.First(x => parts.GetValueOrDefault(x.Name) == slot.Name);
			slot.SetupAttachment = s.SetupAttachment is { } rest ? DrawingName(s.Name, rest) : null;
			slot.ResetToSetup();
		}
		if (plan.AsPlaced)
		{
			source.SetToSetupPose();
			target.SetToSetupPose();
			source.UpdateWorld();
			target.UpdateWorld();
		}
		if (meshesDropped) notes.Add("Meshes weren't brought along: the drawings are flat here (make their meshes again).");
		foreach (var (accessory, companion) in companions) ShowWith(accessory, companion, targetAnimations);

		// ----- animations -----
		var oldToNew = layerSources.ToDictionary(p => p.Value.ToString("N"), p => p.Key.ToString("N"));
		int count = 0, keys = 0, dropped = 0;
		bool deformsDropped = false;
		foreach (string name in plan.Animations)
		{
			var animation = sourceAnimations.FirstOrDefault(a => a.Name == name);
			if (animation == null) continue;
			var copy = new Animation
			{
				Name = UniqueAnimationName(targetAnimations, name + plan.Suffix),
				Duration = animation.Duration,
				Loop = animation.Loop,
				Sound = animation.Sound
			};
			int keysBefore = keys;
			foreach (var t in ProjectMapper.TimelinesOf(animation))
			{
				if (t.Type == "deform") deformsDropped = true;
				if (Retarget(t, source, target, plan, parts, DrawingName, oldToNew) is { } moved)
				{
					copy.Timelines.Add(ProjectMapper.TimelineOf(moved));
					keys += moved.Keys.Count;
				}
				else dropped++;
			}
			if (transfer != null)
			{
				transfer.Bake(animation, copy, plan.Fps);
				keys = keysBefore + copy.Timelines.Sum(t => t.KeyTimes.Count());
			}
			targetAnimations.Add(copy);
			count++;
		}
		if (deformsDropped) notes.Add("Mesh deform keys weren't brought along (the meshes aren't).");
		if (copiedDrawings > 0) notes.Add($"{copiedDrawings} drawing(s) this object had nothing like were copied over for its animations (hands).");
		if (dropped > 0) notes.Add($"{dropped} row(s) of keys were left out: for bones, parts, colour layers or hitboxes this object hasn't.");
		return new MergeResult(count, keys, dropped, skin, added, layerSources, notes);
	}

	// Both posed alike for lining drawings up: the start of an animation both have (idle first), or
	// rest. Returns how far the source's space sits from this one's: two imports can stand their
	// characters at different origins, so the parts both have that stay as they are (the body, when
	// a head comes in) say where the source's drawings go here (the middle of their gaps).
	private static Matrix3x2 LineUp(Skeleton source, IReadOnlyList<Animation> sourceAnimations, Skeleton target, List<Animation> targetAnimations,
		MergePlan plan, Dictionary<string, string?> parts, List<string> notes)
	{
		var names = targetAnimations.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var common = sourceAnimations.Where(a => names.Contains(a.Name)).OrderBy(a => a.Name.Equals("idle", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
		source.SetToSetupPose();
		target.SetToSetupPose();
		if (common != null)
		{
			common.Apply(source, 0);
			targetAnimations.First(a => a.Name.Equals(common.Name, StringComparison.OrdinalIgnoreCase)).Apply(target, 0);
			notes.Add($"Drawings lined up as they show at the start of {common.Name}.");
		}
		else notes.Add("No animation has the same name in both: drawings lined up at rest.");
		source.UpdatePose();
		target.UpdatePose();
		if (plan.Space is { } exact) return exact;

		var xs = new List<float>();
		var ys = new List<float>();
		foreach (var (s, t) in parts)
		{
			if (t == null || plan.SkinParts.Contains(s) || plan.NewParts.Contains(s)) continue;
			if (source.FindSlot(s) is not { } theirs || target.FindSlot(t) is not { } ours) continue;
			if (Middle(source, theirs) is not { } a || Middle(target, ours) is not { } b) continue;
			xs.Add(b.X - a.X);
			ys.Add(b.Y - a.Y);
		}
		if (xs.Count == 0) return Matrix3x2.Identity;
		var gap = new Vector2(Median(xs), Median(ys));
		if (gap.Length() > 0.5f) notes.Add($"The two stand {gap.Length():0} apart: the drawings were moved to match ({xs.Count} part(s) both have said where).");
		return Matrix3x2.CreateTranslation(gap);
	}

	// A skin's new parts (a hat, a hairflap, gum) ride on the part of the skin they sit nearest as
	// lined up (the skull, the jaw): on its bone, so they move as it moves in this object's own
	// animations. Called posed by LineUp, before the drawings are placed (they're placed on these bones).
	private static Dictionary<Slot, Slot> Companions(Skeleton source, Skeleton target, MergePlan plan, Dictionary<string, string?> parts, List<Slot> added)
	{
		var result = new Dictionary<Slot, Slot>();
		var riders = new List<(Slot Here, Vector2 At)>();
		var held = new List<(Slot Here, Vector2 At)>();
		foreach (var s in source.Slots)
		{
			if (!plan.SkinParts.Contains(s.Name) || parts.GetValueOrDefault(s.Name) is not { } name || target.FindSlot(name) is not { } here) continue;
			if (Middle(source, s) is not { } at)
			{
				if (source.DefaultSkin.AttachmentsFor(s.Name).FirstOrDefault() is { } first && source.Assets.TryGetValue(first.AssetId, out var asset))
					at = Vector2.Transform(new Vector2(asset.Width / 2f, asset.Height / 2f), s.ImageToBone(first, asset.Width, asset.Height) * s.Bone.World);
				else continue;
			}
			(added.Contains(here) ? riders : held).Add((here, at));
		}
		if (held.Count == 0) return result;
		foreach (var (rider, at) in riders)
		{
			var nearest = held.MinBy(h => Vector2.DistanceSquared(h.At, at)).Here;
			rider.Bone = nearest.Bone;
			result[rider] = nearest;
		}
		return result;
	}

	// A new part shows while its companion shows, and hides when it hides (a head falling off in
	// die): the companion's drawing keys, copied, in every animation it has them
	private static void ShowWith(Slot accessory, Slot companion, List<Animation> animations)
	{
		foreach (var animation in animations)
		{
			if (animation.Timelines.OfType<AttachmentTimeline>().FirstOrDefault(x => x.Target == companion.Name) is not { } keys) continue;
			if (animation.Timelines.OfType<AttachmentTimeline>().Any(x => x.Target == accessory.Name)) continue;
			var copy = new AttachmentTimeline(accessory.Name);
			foreach (var key in keys.Track.Keys) copy.Add(key.Time, key.Value == null ? null : accessory.SetupAttachment);
			animation.Timelines.Add(copy);
		}
	}

	// Where the drawing a part shows now has its middle, in the object's space (null: none shows)
	private static Vector2? Middle(Skeleton skeleton, Slot slot)
	{
		if (skeleton.ResolveAttachment(slot) is not { } drawing || !skeleton.Assets.TryGetValue(drawing.AssetId, out var asset)) return null;
		return Vector2.Transform(new Vector2(asset.Width / 2f, asset.Height / 2f), slot.ImageToBone(drawing, asset.Width, asset.Height) * slot.Bone.World);
	}

	private static float Median(List<float> values)
	{
		values.Sort();
		int n = values.Count;
		return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2f;
	}

	// A drawing placed on this part so it shows where the source drawing shows now (both posed by
	// LineUp), this part's own placement keys taken into account
	private static void PlaceAsShown(Attachment made, Attachment drawing, Slot sourceSlot, Slot here, Matrix3x2 gap)
	{
		var shown = Matrix3x2.CreateScale(drawing.Scale * sourceSlot.Scale) * Matrix3x2.CreateRotation(drawing.Rotation + sourceSlot.Rotation)
			* Matrix3x2.CreateTranslation(drawing.Offset + sourceSlot.Offset) * sourceSlot.Bone.World * gap;
		PartOps.PlaceFrame(made, shown, here.Bone.World);
		made.Offset -= here.Offset;
		made.Rotation -= here.Rotation;
		made.Scale = new Vector2(Ratio(made.Scale.X, here.Scale.X, made.Scale.X), Ratio(made.Scale.Y, here.Scale.Y, made.Scale.Y));
	}

	// A part's own drawing (its rest one, else any): what the skins' drawings for it are measured against
	private static Attachment? OwnDrawing(Skeleton skeleton, Slot slot) =>
		(slot.SetupAttachment is { } rest ? skeleton.DefaultSkin.Get(slot.Name, rest) : null)
		?? skeleton.DefaultSkin.AttachmentsFor(slot.Name).FirstOrDefault();

	private static (int X, int Y)? Size(Skeleton skeleton, Attachment drawing) =>
		skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) ? (asset.Width, asset.Height) : null;

	// A drawing's placement on its bone set from where its pixels go there (image → bone)
	private static void SetLocal(Attachment made, (int X, int Y) size, Matrix3x2 local)
	{
		var before = Matrix3x2.CreateTranslation(-made.Pivot.X * size.X, -made.Pivot.Y * size.Y) * Matrix3x2.CreateScale(1, -1);
		if (!Matrix3x2.Invert(before, out var undo)) return;
		PartOps.PlaceFrame(made, undo * local, Matrix3x2.Identity);
	}

	private static string UniqueDrawingName(Skeleton skeleton, Slot slot, string name)
	{
		string unique = name;
		for (int i = 2; skeleton.AllSkins().Any(s => s.Get(slot.Name, unique) != null); i++) unique = $"{name} {i}";
		return unique;
	}

	// The bone a source bone's things go on: its match, or the match of the bone it hangs from
	private static Bone BoneFor(Bone sourceBone, Skeleton target, MergePlan plan)
	{
		for (var b = sourceBone; b != null; b = b.Parent)
			if (plan.Bones.GetValueOrDefault(b.Name) is { } name && target.FindBone(name) is { } bone)
				return bone;
		return target.Roots.FirstOrDefault() ?? throw new InvalidOperationException("This object has no bones to put parts on.");
	}

	private static Asset CopyAsset(Asset asset, Skeleton target, Dictionary<Guid, Guid> layerSources)
	{
		var copy = new Asset { Name = asset.Name, Width = asset.Width, Height = asset.Height };
		foreach (var layer in asset.Layers)
		{
			var l = new AssetLayer { Name = layer.Name, Visible = layer.Visible, Opacity = layer.Opacity, Adjust = layer.Adjust?.Clone() };
			copy.Layers.Add(l);
			layerSources[l.Id] = layer.Id;
		}
		target.Assets[copy.Id] = copy;
		return copy;
	}

	private static string UniqueAnimationName(List<Animation> animations, string name)
	{
		string unique = name;
		for (int i = 2; animations.Any(a => a.Name == unique); i++) unique = $"{name} {i}";
		return unique;
	}

	// One row of keys moved onto this object (null: there's nothing here for it)
	private static TimelineDto? Retarget(TimelineDto t, Skeleton source, Skeleton target, MergePlan plan,
		Dictionary<string, string?> parts, Func<string, string, string> drawingName, Dictionary<string, string> layers)
	{
		string? Bone(string name) => plan.Bones.GetValueOrDefault(name) is { } b && target.FindBone(b) != null ? b : null;
		string? Part(string name) => parts.GetValueOrDefault(name) is { } p && target.FindSlot(p) != null ? p : null;
		switch (t.Type)
		{
			case "rotate" or "translate" or "scale" or "depth" or "space":
			{
				if (Bone(t.Target) is not { } to) return null;
				var from = source.FindBone(t.Target);
				var here = target.FindBone(to)!;
				foreach (var k in t.Keys)
				{
					if (t.Type == "space")
					{
						if (!string.IsNullOrEmpty(k.Name)) k.Name = Bone(k.Name) ?? "";   // (a holder that isn't here: detached)
						continue;
					}
					if (!plan.Absolute || from == null) continue;
					float x = k.X ?? 0, y = k.Y ?? 0;
					switch (t.Type)
					{
						case "rotate":
							k.X = from.SetupRotation + x - here.SetupRotation;
							break;
						case "translate":
							k.X = from.SetupPosition.X + x - here.SetupPosition.X;
							k.Y = from.SetupPosition.Y + y - here.SetupPosition.Y;
							break;
						case "scale":
							k.X = Ratio(from.SetupScale.X * x, here.SetupScale.X, x);
							k.Y = Ratio(from.SetupScale.Y * y, here.SetupScale.Y, y);
							break;
						case "depth":
							k.X = from.SetupTilt + x - here.SetupTilt;
							k.Y = from.SetupTurn + y - here.SetupTurn;
							break;
					}
				}
				t.Target = to;
				return t;
			}
			case "ikMix":
			{
				var ik = source.FindIkConstraint(t.Target);
				string? end = ik?.Bones.Count > 0 ? Bone(ik.Bones[^1].Name) : null;
				var mine = target.FindIkConstraint(t.Target) ?? target.IkConstraints.FirstOrDefault(c => c.Bones.Count > 0 && c.Bones[^1].Name == end);
				if (mine == null) return null;
				t.Target = mine.Name;
				return t;
			}
			case "attachment" or "partTranslate" or "partRotate" or "partScale" or "partColor" or "partOpacity":
			{
				if (Part(t.Target) is not { } to) return null;
				if (t.Type == "attachment")
					foreach (var k in t.Keys)
						if (k.Name != null) k.Name = drawingName(t.Target, k.Name);
				t.Target = to;
				return t;
			}
			case "drawOrder":
				foreach (var k in t.Keys)
					k.Order = k.Order?.Select(Part).Where(n => n != null).Select(n => n!).ToList();
				return t.Keys.Any(k => k.Order is { Count: > 0 }) ? t : null;
			case "events":
				return t;
			case "colorLayer":
				return target.FindColorLayer(t.Target) != null ? t : null;
			case "hitbox":
				return target.FindHitbox(t.Target) != null ? t : null;
			case "layerAdjust":
				if (!layers.TryGetValue(t.Target, out var layer)) return null;
				t.Target = layer;
				return t;
			default:
				return null;   // (deform: the meshes aren't brought along)
		}
	}

	private static float Ratio(float value, float by, float otherwise) => MathF.Abs(by) > 1e-6f ? value / by : otherwise;
}
