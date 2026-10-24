using System.Numerics;

namespace Animator.Core.Import;

// What a layer gets in a suggested rig
public enum RigRole
{
	Bone,      // a bone of its own: a part that bends or turns (an arm, a head)
	OnBone,    // no bone: it rides on another layer's bone (eyes on the head, a belt on the body)
	LeaveOut   // not brought in at all
}

// A bone of a suggested rig, made for one layer. Points are canvas pixels (y down).
public sealed class SuggestedBone
{
	public int Layer { get; init; }
	public int Parent { get; init; } = -1;   // index in the suggestion's bones (-1 = the root)
	public Vector2 Head { get; init; }        // where it turns: the joint with its parent
	public Vector2 Tip { get; set; }
	public bool Connected { get; set; }       // its head is exactly its parent's tip
}

public sealed class RigSuggestion
{
	public List<SuggestedBone> Bones { get; } = new();          // parents before their children
	public int[] BoneOf { get; init; } = Array.Empty<int>();    // each layer's bone (index in Bones), -1 = left out
	public int RootLayer { get; init; } = -1;
}

// The layers' shapes on a coarse grid (at most about 256 cells across), and where each pair of
// them overlaps. Made once for a file, so a suggestion can follow every change of the roles at once.
public sealed class RigMasks
{
	private sealed class Mask
	{
		public PixelRect Bounds;                     // in canvas pixels
		public int X0, Y0, W, H;                     // its box on the grid, a cell bigger all round
		public bool[] Cells = Array.Empty<bool>();   // covered (by fairly opaque pixels)
		public bool[] Near = Array.Empty<bool>();    // covered, or next to a covered cell
		public int Area;
		public Vector2 Centre;

		public bool Has(int gx, int gy) => Inside(gx, gy) && Cells[(gy - Y0) * W + gx - X0];
		public bool IsNear(int gx, int gy) => Inside(gx, gy) && Near[(gy - Y0) * W + gx - X0];
		private bool Inside(int gx, int gy) => gx >= X0 && gy >= Y0 && gx < X0 + W && gy < Y0 + H;
	}

	private readonly Mask[] _masks;
	private readonly int[] _overlap, _touch;
	private readonly Vector2[] _joint;
	private readonly int _originX, _originY;

	public int Cell { get; }          // canvas pixels per grid cell
	public int Count => _masks.Length;
	public int CanvasCells { get; }   // cells the canvas itself spans

	private RigMasks(Mask[] masks, int originX, int originY, int cell, int canvasCells)
	{
		_masks = masks;
		_originX = originX;
		_originY = originY;
		Cell = cell;
		CanvasCells = canvasCells;
		int n = masks.Length;
		_overlap = new int[n * n];
		_touch = new int[n * n];
		_joint = new Vector2[n * n];
	}

	// bounds: where each layer is on the canvas; alpha: its opacity, bounds.W × bounds.H, row after row
	public static RigMasks Build(int width, int height, IReadOnlyList<PixelRect> bounds, IReadOnlyList<byte[]> alpha)
	{
		int minX = 0, minY = 0, maxX = Math.Max(1, width), maxY = Math.Max(1, height);
		foreach (var b in bounds.Where(b => !b.IsEmpty))
		{
			minX = Math.Min(minX, b.X);
			minY = Math.Min(minY, b.Y);
			maxX = Math.Max(maxX, b.Right);
			maxY = Math.Max(maxY, b.Bottom);
		}
		int cell = Math.Max(1, (int)Math.Ceiling(Math.Max(maxX - minX, maxY - minY) / 256.0));
		int canvasCells = (int)Math.Ceiling(Math.Max(1, width) / (double)cell) * (int)Math.Ceiling(Math.Max(1, height) / (double)cell);
		var masks = new Mask[bounds.Count];
		for (int i = 0; i < masks.Length; i++) masks[i] = BuildMask(bounds[i], alpha[i], minX, minY, cell);

		var result = new RigMasks(masks, minX, minY, cell, canvasCells);
		result.MeasurePairs();
		return result;
	}

	private static Mask BuildMask(PixelRect b, byte[] alpha, int originX, int originY, int cell)
	{
		var m = new Mask { Bounds = b };
		if (b.IsEmpty || alpha.Length < b.W * b.H)
		{
			m.Centre = new Vector2(b.X + b.W / 2f, b.Y + b.H / 2f);
			return m;
		}
		int gx0 = (b.X - originX) / cell - 1, gy0 = (b.Y - originY) / cell - 1;
		int gx1 = (b.Right - 1 - originX) / cell + 1, gy1 = (b.Bottom - 1 - originY) / cell + 1;
		m.X0 = gx0;
		m.Y0 = gy0;
		m.W = gx1 - gx0 + 1;
		m.H = gy1 - gy0 + 1;
		m.Cells = new bool[m.W * m.H];

		var column = new int[b.W];
		for (int x = 0; x < b.W; x++) column[x] = (b.X + x - originX) / cell - gx0;
		foreach (int threshold in new[] { 64, 1 })   // faint all over: anything that shows counts
		{
			for (int y = 0; y < b.H; y++)
			{
				int row = ((b.Y + y - originY) / cell - gy0) * m.W;
				for (int x = 0; x < b.W; x++)
					if (alpha[y * b.W + x] >= threshold) m.Cells[row + column[x]] = true;
			}
			if (Array.IndexOf(m.Cells, true) >= 0) break;
		}

		m.Near = new bool[m.Cells.Length];
		double sumX = 0, sumY = 0;
		for (int y = 0; y < m.H; y++)
			for (int x = 0; x < m.W; x++)
			{
				if (!m.Cells[y * m.W + x]) continue;
				m.Area++;
				sumX += x + gx0 + 0.5;
				sumY += y + gy0 + 0.5;
				for (int dy = -1; dy <= 1; dy++)
					for (int dx = -1; dx <= 1; dx++)
					{
						int nx = x + dx, ny = y + dy;
						if (nx >= 0 && ny >= 0 && nx < m.W && ny < m.H) m.Near[ny * m.W + nx] = true;
					}
			}
		m.Centre = m.Area > 0
			? new Vector2((float)(originX + sumX / m.Area * cell), (float)(originY + sumY / m.Area * cell))
			: new Vector2(b.X + b.W / 2f, b.Y + b.H / 2f);
		return m;
	}

	private void MeasurePairs()
	{
		int n = _masks.Length;
		for (int i = 0; i < n; i++)
			for (int j = i + 1; j < n; j++)
			{
				var a = _masks[i];
				var b = _masks[j];
				int x0 = Math.Max(a.X0, b.X0), y0 = Math.Max(a.Y0, b.Y0);
				int x1 = Math.Min(a.X0 + a.W, b.X0 + b.W), y1 = Math.Min(a.Y0 + a.H, b.Y0 + b.H);
				int both = 0, near = 0;
				double sumX = 0, sumY = 0;
				for (int gy = y0; gy < y1; gy++)
					for (int gx = x0; gx < x1; gx++)
					{
						if (a.Has(gx, gy) && b.Has(gx, gy)) both++;
						if (!a.IsNear(gx, gy) || !b.IsNear(gx, gy)) continue;
						near++;
						sumX += gx + 0.5;
						sumY += gy + 0.5;
					}
				_overlap[i * n + j] = _overlap[j * n + i] = both;
				_touch[i * n + j] = _touch[j * n + i] = near;
				if (near > 0)
					_joint[i * n + j] = _joint[j * n + i] = ToCanvas(sumX / near, sumY / near);
			}
	}

	private Vector2 ToCanvas(double gx, double gy) => new((float)(_originX + gx * Cell), (float)(_originY + gy * Cell));

	public PixelRect Bounds(int layer) => _masks[layer].Bounds;
	public int Area(int layer) => _masks[layer].Area;              // in cells
	public Vector2 Centre(int layer) => _masks[layer].Centre;      // the middle of its shape
	public int Overlap(int a, int b) => _overlap[a * Count + b];   // cells both cover
	public int Touch(int a, int b) => _touch[a * Count + b];       // cells both cover or nearly do
	public Vector2 Joint(int a, int b) => _joint[a * Count + b];   // the middle of where they touch

	// The middle of the layer's cell nearest to / farthest from a point (canvas pixels)
	public Vector2 Nearest(int layer, Vector2 point) => Extreme(layer, point, farthest: false);
	public Vector2 Farthest(int layer, Vector2 point) => Extreme(layer, point, farthest: true);

	private Vector2 Extreme(int layer, Vector2 point, bool farthest)
	{
		var m = _masks[layer];
		var best = m.Centre;
		float bestDistance = farthest ? -1f : float.MaxValue;
		for (int y = 0; y < m.H; y++)
			for (int x = 0; x < m.W; x++)
			{
				if (!m.Cells[y * m.W + x]) continue;
				var p = ToCanvas(m.X0 + x + 0.5, m.Y0 + y + 0.5);
				float d = Vector2.DistanceSquared(p, point);
				if (farthest ? d > bestDistance : d < bestDistance)
				{
					bestDistance = d;
					best = p;
				}
			}
		return best;
	}

	// The top edge of the layer's shape around a column (canvas pixels)
	public float TopAbove(int layer, float x)
	{
		var m = _masks[layer];
		int column = (int)MathF.Floor((x - _originX) / Cell);
		for (int y = 0; y < m.H; y++)
			for (int gx = column - 1; gx <= column + 1; gx++)
				if (m.Has(gx, m.Y0 + y))
					return _originY + (m.Y0 + y) * Cell;
		return m.Bounds.Y;
	}
}

// Suggests a skeleton for a picture made of layers (a Photoshop file): a bone for each layer that
// bends, joints where the layers overlap, and the details riding on the bones of the layers
// they're on.
public static class RigSuggester
{
	// Roles a rig would likely give the layers. A backdrop (covering most of the canvas), a layer
	// over the whole figure that meets most of the others (line art for everything), or one lying
	// inside a bigger layer (an eye, a belt) rides on another layer's bone; the rest get bones.
	// included: which layers come in at all (null = all); the others are left out.
	public static RigRole[] SuggestRoles(RigMasks m, IReadOnlyList<bool>? included = null)
	{
		int n = m.Count;
		bool In(int i) => included == null || included[i];
		bool Backdrop(int i) => m.Area(i) >= 0.6 * m.CanvasCells;
		var layers = Enumerable.Range(0, n).Where(In).ToList();

		PixelRect? figure = null;
		foreach (int i in layers.Where(i => !Backdrop(i) && !m.Bounds(i).IsEmpty))
			figure = figure is { } f ? PixelRect.Union(f, m.Bounds(i)) : m.Bounds(i);

		var roles = Enumerable.Repeat(RigRole.LeaveOut, n).ToArray();
		foreach (int i in layers)
		{
			if (Backdrop(i))
			{
				roles[i] = RigRole.OnBone;
				continue;
			}
			if (figure is { } whole && layers.Count >= 5)
			{
				var b = m.Bounds(i);
				double cover = (double)b.W * b.H / ((double)whole.W * whole.H);
				int meets = layers.Count(j => j != i && m.Touch(i, j) > 0);
				if (cover >= 0.7 && meets > (layers.Count - 1) / 2.0)
				{
					roles[i] = RigRole.OnBone;
					continue;
				}
			}
			bool inside = layers.Any(j =>
				j != i && !Backdrop(j) && m.Area(j) > m.Area(i) && m.Overlap(i, j) >= 0.85 * m.Area(i));
			roles[i] = inside ? RigRole.OnBone : RigRole.Bone;
		}
		return roles;
	}

	// roles: what each layer gets. targets: for a Bone layer, the layer it hangs from; for an
	// OnBone layer, the layer whose bone it rides on; -1 = worked out from the overlaps. root: the
	// layer whose bone everything hangs from (-1 = the biggest layer that meets the most others).
	public static RigSuggestion Suggest(RigMasks m, IReadOnlyList<RigRole> roles, IReadOnlyList<int> targets, int root = -1)
	{
		int n = m.Count;
		var isBone = new bool[n];
		for (int i = 0; i < n; i++) isBone[i] = roles[i] == RigRole.Bone;
		if (Array.IndexOf(isBone, true) < 0)
		{
			// No layer wants a bone: the biggest one that's brought in gets one
			int biggest = -1;
			for (int i = 0; i < n; i++)
				if (roles[i] != RigRole.LeaveOut && (biggest < 0 || m.Area(i) > m.Area(biggest))) biggest = i;
			if (biggest < 0) return new RigSuggestion { BoneOf = Enumerable.Repeat(-1, n).ToArray() };
			isBone[biggest] = true;
		}
		var bones = Enumerable.Range(0, n).Where(i => isBone[i]).ToList();

		if (root < 0 || root >= n || !isBone[root])
			root = bones
				.OrderByDescending(i => (long)m.Area(i) * (1 + bones.Count(j => j != i && m.Touch(i, j) > 0)))
				.ThenBy(i => i)
				.First();

		// The tree, from the root: each step adds the layer that overlaps the rig so far the most.
		// A layer told what it hangs from waits for that one; one meeting nothing joins the nearest.
		int Chosen(int c) => targets[c] >= 0 && targets[c] < n && targets[c] != c && isBone[targets[c]] ? targets[c] : -1;
		var parentOf = new Dictionary<int, int> { [root] = -1 };
		var order = new List<int> { root };
		var rest = new SortedSet<int>(bones.Where(i => i != root));
		while (rest.Count > 0)
		{
			int child = -1, parent = -1;
			long best = 0;
			foreach (int c in rest)
			{
				int chosen = Chosen(c);
				if (chosen >= 0)
				{
					if (!parentOf.ContainsKey(chosen)) continue;
					child = c;
					parent = chosen;
					break;
				}
				foreach (int p in order)
				{
					long w = m.Touch(p, c);
					if (w <= best) continue;
					best = w;
					child = c;
					parent = p;
				}
			}
			if (child < 0)
			{
				// Nothing left meets the rig, or every one left waits for another in a circle
				var pool = rest.Where(c => Chosen(c) < 0).ToList();
				if (pool.Count == 0) pool = rest.ToList();
				float nearest = float.MaxValue;
				foreach (int c in pool)
					foreach (int p in order)
					{
						float d = Vector2.Distance(m.Centre(c), m.Centre(p));
						if (d >= nearest) continue;
						nearest = d;
						child = c;
						parent = p;
					}
			}
			parentOf[child] = parent;
			order.Add(child);
			rest.Remove(child);
		}

		// Where the bones go. The root runs from the middle of its layer up to its top; every other
		// bone from its joint with its parent towards the far end of its layer.
		var result = new RigSuggestion { RootLayer = root, BoneOf = Enumerable.Repeat(-1, n).ToArray() };
		var boneIndex = new Dictionary<int, int>();
		var ends = new List<Vector2>();
		foreach (int layer in order)
		{
			int p = parentOf[layer];
			Vector2 head, reach;
			if (p < 0)
			{
				head = m.Centre(layer);
				reach = new Vector2(0, m.TopAbove(layer, head.X) - head.Y);
			}
			else
			{
				head = m.Touch(p, layer) > 0 ? m.Joint(p, layer) : m.Nearest(layer, m.Centre(p));
				reach = m.Farthest(layer, head) - head;
			}
			if (reach.Length() < m.Cell) reach = new Vector2(0, -2f * m.Cell);   // too small to tell: point up
			boneIndex[layer] = result.Bones.Count;
			result.BoneOf[layer] = result.Bones.Count;
			result.Bones.Add(new SuggestedBone
			{
				Layer = layer,
				Parent = p < 0 ? -1 : boneIndex[p],
				Head = head,
				Tip = head + (p < 0 ? 1f : 0.9f) * reach   // a little short of the far edge
			});
			ends.Add(head + reach);
		}

		// A bone ends on a child's joint when one is about where it would end: they're connected
		for (int b = 0; b < result.Bones.Count; b++)
		{
			var bone = result.Bones[b];
			float length = Vector2.Distance(bone.Head, ends[b]);
			SuggestedBone? best = null;
			float bestDistance = float.MaxValue;
			foreach (var child in result.Bones.Where(c => c.Parent == b))
			{
				float d = Vector2.Distance(child.Head, ends[b]);
				if (d > 0.35f * length || Vector2.Distance(child.Head, bone.Head) < 0.5f * length || d >= bestDistance) continue;
				best = child;
				bestDistance = d;
			}
			if (best == null) continue;
			bone.Tip = best.Head;
			best.Connected = true;
		}

		// The rest ride on bones: the one chosen (through layers riding on others), or else the bone
		// layer they overlap the most
		int Automatic(int i)
		{
			int owner = bones[0];
			var key = (-1, -1, float.MinValue);
			foreach (int j in bones)
			{
				var k = (m.Overlap(i, j), m.Touch(i, j), -Vector2.Distance(m.Centre(i), m.Centre(j)));
				if (k.CompareTo(key) <= 0) continue;
				key = k;
				owner = j;
			}
			return owner;
		}
		int OwnerOf(int i)
		{
			var seen = new HashSet<int>();
			for (int at = i; seen.Add(at);)
			{
				int t = targets[at];
				if (t < 0 || t >= n || t == at || roles[t] == RigRole.LeaveOut && !isBone[t]) return Automatic(at);
				if (isBone[t]) return t;
				at = t;   // it goes on a layer that goes on another one
			}
			return Automatic(i);
		}
		for (int i = 0; i < n; i++)
			if (!isBone[i] && roles[i] != RigRole.LeaveOut)
				result.BoneOf[i] = boneIndex[OwnerOf(i)];
		return result;
	}
}
