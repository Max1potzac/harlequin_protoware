namespace Animator.Core.Assets;

// A sub-layer of an asset. Pixels live in the viewer; this is just the description.
public class AssetLayer
{
	public Guid Id { get; init; } = Guid.NewGuid();
	public string Name { get; set; } = "Layer";
	public bool Visible { get; set; } = true;
	public float Opacity { get; set; } = 1f;
}