using System.Numerics;

namespace Animator.Core.Rigging;

// A deform key's value: the drawing it's for, and how far each of its mesh points is pushed
public sealed record DeformKey(string Drawing, Vector2[] Offsets)
{
	// Partway from a to b: only between keys of the same drawing (else a holds)
	public static DeformKey Lerp(DeformKey a, DeformKey b, float t)
	{
		if (a.Drawing != b.Drawing || a.Offsets.Length != b.Offsets.Length) return a;
		var offsets = new Vector2[a.Offsets.Length];
		for (int i = 0; i < offsets.Length; i++) offsets[i] = Vector2.Lerp(a.Offsets[i], b.Offsets[i], t);
		return new DeformKey(a.Drawing, offsets);
	}
}

// One bone's share of a mesh point (the shares of a point add up to 1)
public readonly record struct BoneWeight(Bone Bone, float Weight);

// A mesh over a drawing's picture: points (in the picture's pixels, which also say what part of
// the picture each one carries) joined in triangles, so the picture can bend instead of staying
// a flat rectangle.
// - No weights: the points follow the part's bone as the picture would (placement keys too), and
//   deform keys can push them about (a face squashed, a cape fluttering).
// - Weights: each point follows several bones, by its shares (an arm bending smoothly at the
//   elbow). Where the bones were when the weights were given (Bind, BoneBind) is kept, so each
//   point moves as those bones move from there.
// Never changed in place: edits make a new mesh (so undo can keep the old one).
public sealed class Mesh
{
	public int Width { get; }    // the picture's size it was made for
	public int Height { get; }
	public Vector2[] Points { get; }
	public int[] Triangles { get; }          // three point indices each
	public BoneWeight[][]? Weights { get; }  // per point (null: it follows the part's bone)
	public Matrix3x2 Bind { get; }           // pixels → the object, when the weights were given
	public IReadOnlyDictionary<Bone, Matrix3x2> BoneBind { get; }   // the object → each bone's space, then

	public Mesh(int width, int height, Vector2[] points, int[] triangles,
		BoneWeight[][]? weights = null, Matrix3x2? bind = null, IReadOnlyDictionary<Bone, Matrix3x2>? boneBind = null)
	{
		if (triangles.Length % 3 != 0) throw new ArgumentException("Triangles come in threes.");
		if (weights != null && weights.Length != points.Length) throw new ArgumentException("One set of weights per point.");
		Width = width;
		Height = height;
		Points = points;
		Triangles = triangles;
		Weights = weights;
		Bind = bind ?? Matrix3x2.Identity;
		BoneBind = boneBind ?? new Dictionary<Bone, Matrix3x2>();
	}

	public bool Weighted => Weights != null;

	public Mesh WithPoints(Vector2[] points) => new(Width, Height, points, Triangles, Weights, Bind, BoneBind);
	public Mesh WithWeights(BoneWeight[][]? weights, Matrix3x2 bind, IReadOnlyDictionary<Bone, Matrix3x2> boneBind) =>
		new(Width, Height, Points, Triangles, weights, bind, boneBind);
	public Mesh Rigid() => new(Width, Height, Points, Triangles);
}
