using Animator.Core.Assets;
using Animator.Core.Keyframing;

namespace Animator.Core.Rigging;

// A colour layer on a whole object (the Colour view of the rig panel): it changes the colours of
// every part but the ones left out (the eyes of a body made green, say). Several apply in turn,
// first to last. Its numbers can be keyed (a ColorLayerTimeline, by its name).
public sealed class ColorLayer
{
	public string Name { get; set; } = "Colour";
	public bool Visible { get; set; } = true;
	public ColorAdjust Adjust { get; set; } = new();
	public HashSet<Slot> LeftOut { get; } = new();

	public bool Changes(Slot slot) => Visible && !LeftOut.Contains(slot);
}

public static class ColorLayerOps
{
	public static string UniqueName(Skeleton skeleton, string baseName)
	{
		var used = skeleton.ColorLayers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
	}

	// Renamed, with its keys in every animation. false: the name is empty or taken.
	public static bool Rename(Skeleton skeleton, IEnumerable<Animation> animations, ColorLayer layer, string name)
	{
		name = name.Trim();
		if (name.Length == 0) return false;
		if (name == layer.Name) return true;
		if (skeleton.ColorLayers.Any(l => l != layer && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
		foreach (var timeline in animations.SelectMany(a => a.Timelines).OfType<ColorLayerTimeline>())
			if (timeline.Target == layer.Name) timeline.Target = name;
		layer.Name = name;
		return true;
	}

	// The drawing layer a layer key is for, by its id as a key's target names it
	public static (Asset Asset, AssetLayer Layer)? FindAssetLayer(Skeleton skeleton, string id)
	{
		if (!Guid.TryParse(id, out var guid)) return null;
		foreach (var asset in skeleton.Assets.Values)
			foreach (var layer in asset.Layers)
				if (layer.Id == guid) return (asset, layer);
		return null;
	}
}
