using Animator.Core.Assets;
using Animator.Core.Rigging;

namespace Animator.Core.Keyframing;

// Keys for an object's colour layer (by its name): its numbers, blended from key to key
public class ColorLayerTimeline : TrackTimeline<AdjustValues>
{
	public ColorLayerTimeline(string layer) : base(layer, AdjustValues.Lerp) { }

	public override void Apply(Skeleton skeleton, float time)
	{
		if (skeleton.FindColorLayer(Target) is { } layer && Track.TrySample(time, out var v))
			layer.Adjust.Now = v;
	}

	public override string Label => $"{Target} (colour)";
}

// Keys for a colour layer inside a drawing (by the layer's id; Caption is what the row says)
public class LayerAdjustTimeline : TrackTimeline<AdjustValues>
{
	public LayerAdjustTimeline(string layerId) : base(layerId, AdjustValues.Lerp) { }

	public string Caption { get; set; } = "";

	public override void Apply(Skeleton skeleton, float time)
	{
		if (ColorLayerOps.FindAssetLayer(skeleton, Target) is not { } found || found.Layer.Adjust is not { } adjust) return;
		if (!Track.TrySample(time, out var v) || adjust.Now == v) return;
		adjust.Now = v;
		found.Asset.MarkEdited();   // its picture is made again
	}

	public override string Label => $"{(Caption.Length > 0 ? Caption : "Drawing")} (colour layer)";

	public static string TargetOf(AssetLayer layer) => layer.Id.ToString("N");
}
