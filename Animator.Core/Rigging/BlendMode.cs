namespace Animator.Core.Rigging;

// How a part mixes with what's drawn behind it (the usual set game engines support too)
public enum BlendMode
{
	Normal,     // covers what's behind
	Add,        // adds light: glows, sparks, muzzle flashes, magic
	Multiply,   // darkens: shadows, stains
	Screen      // lightens more gently than Add: haze, soft light
}
