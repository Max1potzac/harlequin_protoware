namespace Animator.Core.Assets;

public class Asset
{
	public Guid Id { get; init; } = Guid.NewGuid();
	public string Name { get; set; } = "";
	public int Width { get; set; }     // canvas size, shared by all layers
	public int Height { get; set; }

	// Bottom to top. Merged into one image whenever the rig draws this asset.
	public List<AssetLayer> Layers { get; } = new();

	// Incremented on every change, so cached composites know to rebuild
	public int Version { get; private set; }
	public void MarkEdited() => Version++;
}