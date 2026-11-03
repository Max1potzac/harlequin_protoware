using System.Numerics;

namespace Animator.Viewer;

// Bringing another character's parts into an object straight from its Animate project (the
// editor's Edit → Bring in from Animate projects…, and the command line's bring-in): the project
// is imported aside (into a scene of its own, not the target's), matched to the target by name,
// and the parts on the chosen bones, with what rides on them, become a skin named after the file.
// Except: parts left out whose names have any of these words in them (armor, ink…), or, written
// "=name", that whole name. Map: source
// part → part here, for the ones whose names don't say (a head Animate left called "symbol_105x74").
// Include: source parts that come too, wherever their bones are (a hairflap hung off the torso;
// a whole body, with no bones picked). ByParts: animations by where the parts show, frame by
// frame at Fps (imports each get their own rig, so bone for bone rarely fits).
// ImportedFrom: the Animate project the target object was imported from: with it, drawings are
// placed exactly, through their symbols (see MergePlan.Symbols). Rig: the files imported aside
// get this rig (see XflImportPlan.RigByNames). CopyMissing: with Animations, the source's parts
// and drawings with these words in their names that the target has nothing like (hands) are
// copied over for them, hidden at rest. Context: shared by the files of one bring-in.
// FitByJoints: parts with these words in their names (limbs) fitted by their joints. ByPose:
// parts with these words placed as they show posed alike, not through their symbols (another
// character's own art: see MergePlan.ByPose). OwnArt: with Animations, parts with these words show
// the source's own drawings, copied (hands: see MergePlan.OwnArt).
public sealed record BringInOptions(IReadOnlyCollection<Bone> Bones, string Category = "Head", bool Animations = false,
	bool AsPlaced = true, bool Accessories = true, IReadOnlyCollection<string>? Except = null,
	IReadOnlyDictionary<string, string>? Map = null, IReadOnlyCollection<string>? Include = null,
	bool ByParts = true, int Fps = 30, string? ImportedFrom = null, IReadOnlyList<(string Part, string? Parent)>? Rig = null,
	IReadOnlyCollection<string>? CopyMissing = null, BringInContext? Context = null, IReadOnlyCollection<string>? FitByJoints = null,
	IReadOnlyCollection<string>? ByPose = null, IReadOnlyCollection<string>? OwnArt = null);

// What the files of one bring-in share: every drawing's symbol (theirs, as each is imported, and
// the target's, from the project it was imported from)
public sealed class BringInContext
{
	public Dictionary<Attachment, Matrix3x2> Symbols { get; } = new();
	public Matrix3x2? TargetSpace { get; private set; }   // the target's Animate timeline → its object
	private Skeleton? _readFor;

	// The target's drawings' symbols, from the Animate project it was imported from (matched by
	// part and drawing name), once
	public void Read(string path, Skeleton target)
	{
		if (_readFor == target) return;
		using var plan = XflImportPlan.Load(AnimateBringIn.AnimateFile(path), AnimateBringIn.ProjectName(path));
		plan.SuggestRig = false;
		var pictures = new Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>();
		var aside = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
		var byName = new Dictionary<(string Part, string Drawing), Matrix3x2>();
		foreach (var slot in aside.Skeleton.Slots)
			foreach (var drawing in aside.Skeleton.DefaultSkin.AttachmentsFor(slot.Name))
				if (pictures.TryGetValue(drawing, out var picture)) byName[(slot.Name, drawing.Name)] = picture.PixelToDrawing;
		foreach (var slot in target.Slots)
			foreach (var drawing in target.DefaultSkin.AttachmentsFor(slot.Name))
				if (byName.TryGetValue((slot.Name, drawing.Name), out var symbol)) Symbols[drawing] = symbol;
		TargetSpace = plan.TimelineToObject;
		_readFor = target;
	}
}

public sealed record BroughtIn(string Name, MergeResult? Result, MergePlan Plan, string? Problem);

public static class AnimateBringIn
{
	// What an Animate project is called: an .fla's or a zip's own name, an XFL folder's name
	public static string ProjectName(string path)
	{
		if (Directory.Exists(path)) return new DirectoryInfo(path).Name;
		return Path.GetExtension(path).ToLowerInvariant() is ".fla" or ".zip"
			? Path.GetFileNameWithoutExtension(path)
			: new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!).Name;
	}

	// The file to open for a path given: an XFL folder means the .xfl (or DOMDocument.xml) in it
	public static string AnimateFile(string path)
	{
		if (!Directory.Exists(path)) return path;
		return Directory.GetFiles(path, "*.xfl").FirstOrDefault()
			?? (File.Exists(Path.Combine(path, "DOMDocument.xml")) ? Path.Combine(path, "DOMDocument.xml")
				: throw new FileNotFoundException($"{path} has no .xfl or DOMDocument.xml in it."));
	}

	// One Animate project's parts (on the options' bones) into the target, as a skin. The pixels
	// are copied in the store; a file that can't be read throws (InvalidDataException and the like).
	public static BroughtIn FromAnimate(string path, Skeleton target, List<Animation> targetAnimations, AssetStore assets, BringInOptions options)
	{
		string name = ProjectName(path);
		string word = ObjectMerge.ShortName(name);
		var context = options.Context ?? new BringInContext();
		using var plan = XflImportPlan.Load(AnimateFile(path), name);
		plan.SuggestRig = true;
		plan.NamedRig = options.Rig;
		var pictures = new Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>();
		var source = XflImporter.IntoScene(new Scene(), assets, plan, Vector2.Zero, pictures);   // (aside: not into the target's scene)
		var merge = ObjectMerge.Suggest(source.Skeleton, source.Animations, target);
		if (options.ImportedFrom is { } targetProject)
		{
			context.Read(targetProject, target);
			foreach (var (drawing, picture) in pictures) context.Symbols[drawing] = picture.PixelToDrawing;
			merge.Symbols = context.Symbols;
			if (context.TargetSpace is { } space && Matrix3x2.Invert(plan.TimelineToObject, out var toTimeline))
				merge.Space = toTimeline * space;
		}
		merge.AsPlaced = options.AsPlaced;
		merge.SkinName = word;
		merge.SkinCategory = options.Category;
		merge.Suffix = "_" + word;
		foreach (var (from, to) in options.Map ?? new Dictionary<string, string>())
		{
			if (source.Skeleton.FindSlot(from) == null) continue;   // (a map for many files: this one hasn't it)
			if (target.FindSlot(to) == null) throw new InvalidOperationException($"There's no part \"{to}\" here to put {from} on.");
			foreach (var other in merge.Parts.Where(p => p.Value == to).Select(p => p.Key).ToList()) merge.Parts[other] = null;
			merge.Parts[from] = to;
		}
		var (matched, extra) = ObjectMerge.PartsOn(source.Skeleton, target, merge, options.Bones);
		foreach (string part in options.Include ?? Array.Empty<string>())
		{
			if (source.Skeleton.FindSlot(part) == null || matched.Contains(part) || extra.Contains(part)) continue;
			if (merge.Parts.GetValueOrDefault(part) is { } here && target.FindSlot(here) != null) matched.Add(part);
			else extra.Add(part);
		}
		if (options.Except is { Count: > 0 } except)
		{
			// (a word starting "=" is a whole name: =flag leaves out "flag" but not "flag_stick")
			bool Left(string part) => except.Any(word => word.StartsWith('=')
				? part.Equals(word[1..], StringComparison.OrdinalIgnoreCase)
				: part.Contains(word, StringComparison.OrdinalIgnoreCase));
			matched.RemoveAll(Left);
			extra.RemoveAll(Left);
		}
		merge.SkinParts.UnionWith(matched);
		if (options.Accessories)
		{
			merge.NewParts.UnionWith(extra);
			merge.SkinParts.UnionWith(extra);
		}
		if (!options.Animations) merge.Animations.Clear();
		var hiddenNew = new HashSet<string>();
		if (options.Animations && options.CopyMissing is { Count: > 0 } words)
		{
			bool Copied(string part) => words.Any(w => part.Contains(w, StringComparison.OrdinalIgnoreCase));
			merge.CopyMissing = Copied;
			foreach (var slot in source.Skeleton.Slots)
				if (Copied(slot.Name) && !merge.SkinParts.Contains(slot.Name)
					&& (merge.Parts.GetValueOrDefault(slot.Name) is not { } here || target.FindSlot(here) == null))
				{
					merge.NewParts.Add(slot.Name);
					hiddenNew.Add(slot.Name);
				}
		}
		merge.ByParts = options.ByParts;
		if (options.FitByJoints is { Count: > 0 } limbs)
			merge.FitByJoints = part => limbs.Any(w => part.Contains(w, StringComparison.OrdinalIgnoreCase));
		if (options.ByPose is { Count: > 0 } posed)
			merge.ByPose = part => posed.Any(w => part.Contains(w, StringComparison.OrdinalIgnoreCase));
		if (options.Animations && options.OwnArt is { Count: > 0 } own)
			merge.OwnArt = part => own.Any(w => part.Contains(w, StringComparison.OrdinalIgnoreCase));
		merge.Fps = options.Fps;
		if (merge.SkinParts.Count == 0) merge.SkinName = null;   // just its animations
		if (merge.SkinParts.Count == 0 && merge.Animations.Count == 0)
			return new BroughtIn(word, null, merge, "none of its parts match the ones on the chosen bones");
		var result = ObjectMerge.Merge(source.Skeleton, source.Animations, target, targetAnimations, merge);
		foreach (var slot in result.NewParts)   // (copied for the animations: hidden unless they show them)
			if (target.DefaultSkin.AttachmentsFor(slot.Name).Any() && hiddenNew.Count > 0)
			{
				slot.SetupAttachment = null;
				slot.ResetToSetup();
			}
		foreach (var (newLayer, oldLayer) in result.LayerSources) assets.CopyPixels(oldLayer, newLayer);
		return new BroughtIn(word, result, merge, null);
	}

	// A line saying what came in
	public static string Describe(BroughtIn b) => b.Result is not { } r ? $"{b.Name}: {b.Problem}"
		: r.Skin is { } skin
			? $"{skin.Name}: {b.Plan.SkinParts.Count} part(s)" + (r.NewParts.Count > 0 ? $" (new: {string.Join(", ", r.NewParts.Select(p => p.Name))})" : "")
			  + (r.Animations > 0 ? $", {r.Animations} animation(s)" : "")
			: $"{b.Name}: {r.Animations} animation(s)";
}
