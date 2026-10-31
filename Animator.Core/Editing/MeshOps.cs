using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Meshes and weights (the part panel's Mesh box): a mesh made over a drawing, its points moved,
// weights given to bones (worked out from where they are, or painted), the posed points, and
// deform keys. Like the other ops, these change the model directly; the editor adds undo.
public static class MeshOps
{
	// ======================= making =======================

	// A grid over the picture's pixels (x, y, width, height: the part that shows), a point every
	// `step` pixels; cells with nothing in them (hasPixels: false) are left out
	public static Mesh Grid(int width, int height, (int X, int Y, int W, int H) area, int step, Func<int, int, int, int, bool>? hasPixels = null)
	{
		step = Math.Max(2, step);
		int columns = Math.Max(1, (int)MathF.Ceiling(area.W / (float)step));
		int rows = Math.Max(1, (int)MathF.Ceiling(area.H / (float)step));
		float cellW = area.W / (float)columns, cellH = area.H / (float)rows;
		var index = new Dictionary<(int, int), int>();
		var points = new List<Vector2>();
		var triangles = new List<int>();
		int Point(int c, int r)
		{
			if (index.TryGetValue((c, r), out int i)) return i;
			points.Add(new Vector2(area.X + c * cellW, area.Y + r * cellH));
			return index[(c, r)] = points.Count - 1;
		}
		for (int r = 0; r < rows; r++)
			for (int c = 0; c < columns; c++)
			{
				int x0 = (int)MathF.Floor(area.X + c * cellW), y0 = (int)MathF.Floor(area.Y + r * cellH);
				int x1 = (int)MathF.Ceiling(area.X + (c + 1) * cellW), y1 = (int)MathF.Ceiling(area.Y + (r + 1) * cellH);
				if (hasPixels != null && !hasPixels(x0, y0, x1, y1)) continue;
				int a = Point(c, r), b = Point(c + 1, r), d = Point(c, r + 1), e = Point(c + 1, r + 1);
				triangles.AddRange(new[] { a, b, e, a, e, d });
			}
		if (triangles.Count == 0)   // nothing showed: the whole area, as one cell
			return Grid(width, height, area, Math.Max(area.W, area.H) + 1);
		return new Mesh(width, height, points.ToArray(), triangles.ToArray());
	}

	// A point moved in the picture (Build): where it is at rest, and what it carries
	public static Mesh MovePoint(Mesh mesh, int point, Vector2 pixel)
	{
		var points = (Vector2[])mesh.Points.Clone();
		points[point] = pixel;
		return mesh.WithPoints(points);
	}

	// ======================= posing =======================

	// Where a point's pixels go in the object, as the bones are now: for a point of a weighted
	// mesh, its bones' moves blended by its shares; else as the part's picture is placed
	public static Matrix3x2 PointMatrix(Slot slot, Attachment drawing, Mesh mesh, int point)
	{
		if (mesh.Weights is not { } weights) return slot.ImageToBone(drawing, mesh.Width, mesh.Height) * slot.Bone.World;
		var sum = new Matrix3x2();
		foreach (var (bone, weight) in weights[point])
		{
			var m = mesh.Bind * BindOf(mesh, bone) * bone.World;
			sum.M11 += m.M11 * weight; sum.M12 += m.M12 * weight;
			sum.M21 += m.M21 * weight; sum.M22 += m.M22 * weight;
			sum.M31 += m.M31 * weight; sum.M32 += m.M32 * weight;
		}
		return sum;
	}

	private static Matrix3x2 BindOf(Mesh mesh, Bone bone) =>
		mesh.BoneBind.TryGetValue(bone, out var bind) ? bind
		: Matrix3x2.Invert(bone.World, out var now) ? now : Matrix3x2.Identity;   // (a bone given weight since: as it is now)

	// Every point where it is now, in the object (deform keys included)
	public static Vector2[] Posed(Slot slot, Attachment drawing, Mesh mesh)
	{
		var pushed = slot.DeformOf(drawing);
		var posed = new Vector2[mesh.Points.Length];
		var rigid = mesh.Weighted ? (Matrix3x2?)null : PointMatrix(slot, drawing, mesh, 0);
		for (int i = 0; i < posed.Length; i++)
		{
			var p = mesh.Points[i] + (pushed?[i] ?? Vector2.Zero);
			posed[i] = Vector2.Transform(p, rigid ?? PointMatrix(slot, drawing, mesh, i));
		}
		return posed;
	}

	// The picture's pixel under an object point (null: no triangle there)
	public static Vector2? PixelAt(Mesh mesh, Vector2[] posed, Vector2 world, Vector2[]? pushed = null)
	{
		var t = mesh.Triangles;
		for (int i = 0; i < t.Length; i += 3)
		{
			if (!Barycentric(world, posed[t[i]], posed[t[i + 1]], posed[t[i + 2]], out var u, out var v, out var w)) continue;
			Vector2 P(int k) => mesh.Points[k];
			return P(t[i]) * u + P(t[i + 1]) * v + P(t[i + 2]) * w;
		}
		return null;
	}

	private static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float u, out float v, out float w)
	{
		u = v = w = 0;
		float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
		if (MathF.Abs(d) < 1e-9f) return false;
		u = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d;
		v = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d;
		w = 1 - u - v;
		const float e = -1e-4f;
		return u >= e && v >= e && w >= e;
	}

	// ======================= weights =======================

	// Weights from where the bones are (the skeleton in its rest pose): each point shared by the
	// `most` bones nearest to it, the nearer the more. bones: those that may take a share (null: all).
	public static Mesh AutoWeights(Skeleton skeleton, Slot slot, Attachment drawing, Mesh mesh, IEnumerable<Bone>? bones = null, int most = 3)
	{
		var candidates = (bones ?? skeleton.AllBones()).ToList();
		if (candidates.Count == 0) candidates.Add(slot.Bone);
		var bind = slot.ImageToBone(drawing, mesh.Width, mesh.Height) * slot.Bone.World;
		var weights = new BoneWeight[mesh.Points.Length][];
		for (int i = 0; i < weights.Length; i++)
		{
			var at = Vector2.Transform(mesh.Points[i], bind);
			var near = candidates
				.Select(b => (Bone: b, Raw: Closeness(b, at)))
				.OrderByDescending(x => x.Raw)
				.Take(Math.Max(1, most))
				.ToList();
			float total = near.Sum(x => x.Raw);
			weights[i] = near.Select(x => new BoneWeight(x.Bone, total > 0 ? x.Raw / total : 1f / near.Count))
				.Where(w => w.Weight > 0.01f).ToArray();
			Normalize(weights[i], slot.Bone);
		}
		return Bound(skeleton, slot, drawing, mesh, weights);
	}

	// How much a bone pulls a point: falling off with the square of its distance from the bone
	// (head to tip), measured against the bone's length
	private static float Closeness(Bone bone, Vector2 point)
	{
		var head = bone.World.Translation;
		var tip = bone.TipWorld;
		var along = tip - head;
		float t = along.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(point - head, along) / along.LengthSquared(), 0f, 1f) : 0f;
		float d = Vector2.Distance(point, head + along * t);
		float soft = MathF.Max(4f, bone.Length * 0.15f);
		float q = 1f / (d * d + soft * soft);
		return q * q;
	}

	// Every point all on one bone (the part's own, say): bends as that bone does
	public static Mesh AllOn(Skeleton skeleton, Slot slot, Attachment drawing, Mesh mesh, Bone bone) =>
		Bound(skeleton, slot, drawing, mesh, mesh.Points.Select(_ => new[] { new BoneWeight(bone, 1f) }).ToArray());

	// Weights kept with where everything is now (the rest pose): the picture's place, and every bone's
	private static Mesh Bound(Skeleton skeleton, Slot slot, Attachment drawing, Mesh mesh, BoneWeight[][] weights)
	{
		var bind = drawing.LocalMatrix(mesh.Width, mesh.Height) * slot.Bone.World;
		var boneBind = new Dictionary<Bone, Matrix3x2>();
		foreach (var bone in skeleton.AllBones())
			if (Matrix3x2.Invert(bone.World, out var inverse)) boneBind[bone] = inverse;
		return mesh.WithWeights(weights, bind, boneBind);
	}

	// A bone's share of these points raised (amount > 0) or lowered, each by how much it's
	// touched (0..1); the other bones' shares make up the rest, as they were between them. A point
	// with no other bone keeps all of this one's.
	public static Mesh Paint(Mesh mesh, Bone bone, IReadOnlyDictionary<int, float> touched, float amount)
	{
		if (mesh.Weights is not { } old) return mesh;
		var weights = old.Select(w => w.ToArray()).ToArray();
		foreach (var (point, touch) in touched)
		{
			var shares = weights[point];
			float mine = shares.Where(s => s.Bone == bone).Sum(s => s.Weight);
			float target = Math.Clamp(mine + amount * touch, 0f, 1f);
			var others = shares.Where(s => s.Bone != bone && s.Weight > 0).ToList();
			float rest = others.Sum(s => s.Weight);
			if (rest <= 1e-6f) target = 1f;   // nothing else to give it to
			var made = new List<BoneWeight>();
			if (target > 0.001f) made.Add(new BoneWeight(bone, target));
			foreach (var o in others)
			{
				float w = o.Weight / rest * (1f - target);
				if (w > 0.001f) made.Add(new BoneWeight(o.Bone, w));
			}
			weights[point] = Normalized(made);
		}
		var boneBind = mesh.BoneBind.ContainsKey(bone) || !Matrix3x2.Invert(bone.World, out var inverse)
			? mesh.BoneBind
			: new Dictionary<Bone, Matrix3x2>(mesh.BoneBind) { [bone] = inverse };   // (a bone new to it: as it is now, at rest)
		return mesh.WithWeights(weights, mesh.Bind, boneBind);
	}

	// A bone's share of a point (0: none)
	public static float WeightOf(Mesh mesh, int point, Bone bone) =>
		mesh.Weights?[point].Where(w => w.Bone == bone).Sum(w => w.Weight) ?? 0f;

	private static BoneWeight[] Normalized(List<BoneWeight> shares)
	{
		float total = shares.Sum(s => s.Weight);
		return total <= 0 ? shares.ToArray() : shares.Select(s => s with { Weight = s.Weight / total }).ToArray();
	}

	private static void Normalize(BoneWeight[] shares, Bone fallback)
	{
		float total = shares.Sum(s => s.Weight);
		for (int i = 0; i < shares.Length; i++) shares[i] = shares[i] with { Weight = total > 0 ? shares[i].Weight / total : 1f / shares.Length };
	}

	// Bones deleted: their shares go to the others of each point (or, with none left, to the
	// part's bone if it's still there; else the mesh follows its part rigidly)
	public static void ForgetBones(Skeleton skeleton, IReadOnlySet<Bone> doomed)
	{
		foreach (var skin in skeleton.AllSkins())
			foreach (var (slotName, drawing) in skin.Entries.ToList())
			{
				if (drawing.Mesh is not { Weights: { } weights } mesh || !weights.Any(w => w.Any(s => doomed.Contains(s.Bone)))) continue;
				var slot = skeleton.FindSlot(slotName);
				var fallback = slot != null && !doomed.Contains(slot.Bone) ? slot.Bone : null;
				var kept = new BoneWeight[weights.Length][];
				bool lost = false;
				for (int i = 0; i < weights.Length; i++)
				{
					var left = weights[i].Where(s => !doomed.Contains(s.Bone)).ToList();
					if (left.Count == 0 && fallback != null) left.Add(new BoneWeight(fallback, 1f));
					if (left.Count == 0) lost = true;
					kept[i] = Normalized(left);
				}
				var boneBind = mesh.BoneBind.Where(p => !doomed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
				drawing.Mesh = lost ? mesh.Rigid() : mesh.WithWeights(kept, mesh.Bind, boneBind);
			}
	}

	// ======================= deform keys =======================

	// A drawing's points pushed by these offsets (pixels) from this frame on, in this animation
	public static void KeyDeform(Animation animation, Slot slot, Attachment drawing, Vector2[] offsets, int frame, int fps)
	{
		var row = animation.GetOrCreate(slot.Name, n => new DeformTimeline(n));
		row.Track.Add(frame / (float)fps, new DeformKey(drawing.Name, offsets));
	}
}
