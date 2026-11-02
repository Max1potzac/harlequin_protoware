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

	// Each source name's match among these (the same name; else the same once the prefixes are set aside)
	public static Dictionary<string, string?> Match(IEnumerable<string> source, IEnumerable<string> target, string sourcePrefix, string targetPrefix)
	{
		var names = target.Distinct().ToList();
		var exact = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var byCore = names.GroupBy(n => Core(n, targetPrefix), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var result = new Dictionary<string, string?>();
		foreach (string s in source.Distinct())
			result[s] = exact.TryGetValue(s, out var same) ? same
				: byCore.TryGetValue(Core(s, sourcePrefix), out var core) ? core
				: null;
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
		bool meshesDropped = false;
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
				into.Set(targetPart, new Attachment
				{
					Name = DrawingName(s.Name, drawing.Name),
					AssetId = copy.Id,
					Offset = drawing.Offset,
					Rotation = drawing.Rotation,
					Scale = drawing.Scale,
					Pivot = drawing.Pivot
				});
			}
		}
		foreach (var slot in added)
		{
			var s = source.Slots.First(x => parts.GetValueOrDefault(x.Name) == slot.Name);
			slot.SetupAttachment = s.SetupAttachment is { } rest ? DrawingName(s.Name, rest) : null;
			slot.ResetToSetup();
		}
		if (meshesDropped) notes.Add("Meshes weren't brought along: the drawings are flat here (make their meshes again).");

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
			targetAnimations.Add(copy);
			count++;
		}
		if (deformsDropped) notes.Add("Mesh deform keys weren't brought along (the meshes aren't).");
		if (dropped > 0) notes.Add($"{dropped} row(s) of keys were left out: for bones, parts, colour layers or hitboxes this object hasn't.");
		return new MergeResult(count, keys, dropped, skin, added, layerSources, notes);
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
