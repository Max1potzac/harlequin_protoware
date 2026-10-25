namespace Animator.Core.Scenes;

// A guide line in the viewport, dragged out of a ruler: up and down (Vertical, at this x) or across
// (at this y), in the units the panels measure in (the open object's, or the scene's). Things
// dragged near it snap onto it.
public sealed class Guide
{
	public Guide(bool vertical, float at)
	{
		Vertical = vertical;
		At = at;
	}

	public bool Vertical { get; }
	public float At { get; set; }
}
