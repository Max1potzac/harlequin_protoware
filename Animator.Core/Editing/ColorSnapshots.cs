using Animator.Core.Assets;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// An object's colour layers, for undo: which there are, in what order, and everything about each
public sealed class ColorLayersSnapshot
{
	private sealed record State(ColorLayer Layer, string Name, bool Visible, ColorAdjust Adjust, List<Slot> LeftOut);

	private readonly Skeleton _skeleton;
	private readonly List<State> _layers;

	private ColorLayersSnapshot(Skeleton skeleton)
	{
		_skeleton = skeleton;
		_layers = skeleton.ColorLayers.Select(l => new State(l, l.Name, l.Visible, l.Adjust.Clone(), l.LeftOut.ToList())).ToList();
	}

	public static ColorLayersSnapshot Capture(Skeleton skeleton) => new(skeleton);

	public void Restore()
	{
		_skeleton.ColorLayers.Clear();
		foreach (var (layer, name, visible, adjust, leftOut) in _layers)
		{
			layer.Name = name;
			layer.Visible = visible;
			layer.Adjust = adjust.Clone();
			layer.LeftOut.Clear();
			layer.LeftOut.UnionWith(leftOut);
			_skeleton.ColorLayers.Add(layer);
		}
	}
}

// One colour layer of a drawing, for undo: its settings (the layer list itself is undone apart)
public sealed class LayerAdjustSnapshot
{
	private readonly Asset _asset;
	private readonly AssetLayer _layer;
	private readonly ColorAdjust? _adjust;

	private LayerAdjustSnapshot(Asset asset, AssetLayer layer)
	{
		_asset = asset;
		_layer = layer;
		_adjust = layer.Adjust?.Clone();
	}

	public static LayerAdjustSnapshot Capture(Asset asset, AssetLayer layer) => new(asset, layer);

	public void Restore()
	{
		_layer.Adjust = _adjust?.Clone();
		_asset.MarkEdited();
	}
}
