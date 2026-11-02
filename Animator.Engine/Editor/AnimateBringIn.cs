using System.Numerics;

namespace Animator.Viewer;

// Bringing another character's parts into an object straight from its Animate project (the
// editor's Edit → Bring in from Animate projects…, and the command line's bring-in): the project
// is imported aside (into a scene of its own, not the target's), matched to the target by name,
// and the parts on the chosen bones, with what rides on them, become a skin named after the file.
// Except: parts left out whose names have any of these words in them (armor, ink…). Map: source
// part → part here, for the ones whose names don't say (a head Animate left called "symbol_105x74")
public sealed record BringInOptions(IReadOnlyCollection<Bone> Bones, string Category = "Head", bool Animations = false,
	bool AsPlaced = true, bool Accessories = true, IReadOnlyCollection<string>? Except = null,
	IReadOnlyDictionary<string, string>? Map = null);

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
		using var plan = XflImportPlan.Load(AnimateFile(path), name);
		plan.SuggestRig = true;
		var source = XflImporter.IntoScene(new Scene(), assets, plan, Vector2.Zero);   // (aside: not into the target's scene)
		var merge = ObjectMerge.Suggest(source.Skeleton, source.Animations, target);
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
		if (options.Except is { Count: > 0 } except)
		{
			bool Left(string part) => except.Any(word => part.Contains(word, StringComparison.OrdinalIgnoreCase));
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
		if (merge.SkinParts.Count == 0 && merge.Animations.Count == 0)
			return new BroughtIn(word, null, merge, "none of its parts match the ones on the chosen bones");
		var result = ObjectMerge.Merge(source.Skeleton, source.Animations, target, targetAnimations, merge);
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
