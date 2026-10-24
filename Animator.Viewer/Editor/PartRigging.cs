using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer;

// Putting an object's parts on bones: the bone a part lies along, and a whole suggested rig made
// from the parts' shapes (the way the layers of an imported Photoshop file get one)
public static class PartRigging
{
	private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

	// "Give it bones": a drawn object becomes a rigged one. Its hidden bone becomes a root bone you
	// can see, at the object's origin, pointing up, `length` long; with `suggest`, bones suggested
	// from its parts' shapes hang from it, each part on its own. Every part stays exactly where it
	// is. Returns how many bones were suggested.
	public static int GiveBones(SceneObject obj, AssetStore store, float length, bool suggest)
	{
		var skeleton = obj.Skeleton;
		obj.Kind = ObjectKind.Rigged;
		var root = skeleton.Roots.FirstOrDefault() ?? RigOps.AddBone(skeleton, null, "root", Vector2.Zero, 0f, 0f);
		var frozen = FrozenRest.Capture(skeleton);
		root.Length = MathF.Max(1f, length);
		RestEdits.Turn(frozen, root, MathF.PI / 2);   // pointing up, the parts staying where they are
		return suggest ? SuggestBones(skeleton, store, root) : 0;
	}

	// "Bind": each part goes on the bone nearest to most of it (see NearestBone), staying where it
	// is. Returns how many moved.
	public static int PartsToNearestBones(Skeleton skeleton, AssetStore store, IEnumerable<Slot> slots)
	{
		int moved = 0;
		foreach (var slot in slots.ToList())
		{
			skeleton.SetToSetupPose();
			skeleton.UpdateWorld();
			if (NearestBone(skeleton, slot, store) is { } bone && PartOps.MoveToBone(skeleton, slot, bone)) moved++;
		}
		return moved;
	}

	// The drawing a part shows in the rest pose, its pixels, and where they land (image pixels → world)
	private static (SKBitmap Image, Matrix3x2 ToWorld)? RestShape(Skeleton skeleton, Slot slot, AssetStore store)
	{
		if (slot.SetupAttachment is not { } name) return null;
		var drawing = skeleton.ActiveSkin?.Get(slot.Name, name) ?? skeleton.DefaultSkin.Get(slot.Name, name);
		if (drawing == null || !skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) || store.Get(asset) is not { } image)
			return null;
		return (image, drawing.LocalMatrix(image.Width, image.Height) * slot.Bone.World);
	}

	// The bone nearest to most of a part's visible pixels, in the rest pose (the skeleton must be
	// in it). Root bones only count when there's nothing else: they're the handle for the whole rig.
	public static Bone? NearestBone(Skeleton skeleton, Slot slot, AssetStore store)
	{
		var bones = skeleton.AllBones().Where(b => b.Parent != null).ToList();
		if (bones.Count == 0) bones = skeleton.AllBones().ToList();
		if (bones.Count == 0 || RestShape(skeleton, slot, store) is not { } shape) return null;

		var (image, toWorld) = shape;
		var alpha = SheetImporter.Alpha(image);
		int step = Math.Max(1, (int)MathF.Sqrt(image.Width * image.Height / 400f));   // about 400 samples
		var votes = new int[bones.Count];
		for (int y = step / 2; y < image.Height; y += step)
			for (int x = step / 2; x < image.Width; x += step)
				if (alpha[y * image.Width + x] >= 64)
					votes[Nearest(bones, Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), toWorld))]++;
		if (votes.All(v => v == 0))   // nothing solid: its middle decides
			votes[Nearest(bones, Vector2.Transform(new Vector2(image.Width / 2f, image.Height / 2f), toWorld))]++;
		return bones[Array.IndexOf(votes, votes.Max())];
	}

	private static int Nearest(IReadOnlyList<Bone> bones, Vector2 point)
	{
		int best = 0;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < bones.Count; i++)
		{
			float d = DistanceToSegment(point, bones[i].World.Translation, bones[i].TipWorld);
			if (d >= bestDistance) continue;
			bestDistance = d;
			best = i;
		}
		return best;
	}

	private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		float lengthSquared = ab.LengthSquared();
		if (lengthSquared < 1e-6f) return Vector2.Distance(p, a);
		float t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
		return Vector2.Distance(p, a + ab * t);
	}

	// Suggests bones from the parts' shapes, each part a layer (see RigSuggester), under `root`;
	// every part then moves onto its bone, staying where it is. Returns how many bones it added.
	public static int SuggestBones(Skeleton skeleton, AssetStore store, Bone root)
	{
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var parts = new List<(Slot Slot, SKBitmap Image, Matrix3x2 ToWorld)>();
		foreach (var slot in skeleton.Slots)
			if (RestShape(skeleton, slot, store) is { } shape)
				parts.Add((slot, shape.Image, shape.ToWorld));
		if (parts.Count == 0) return 0;

		// A canvas over all of them (y down, like a picture), at most 2048 pixels across
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		foreach (var (_, image, toWorld) in parts)
			foreach (var corner in Corners(image))
			{
				var p = Vector2.Transform(corner, toWorld);
				minX = MathF.Min(minX, p.X);
				minY = MathF.Min(minY, p.Y);
				maxX = MathF.Max(maxX, p.X);
				maxY = MathF.Max(maxY, p.Y);
			}
		float span = MathF.Max(maxX - minX, maxY - minY);
		float scale = span > 2048 ? 2048 / span : 1f;
		var worldToCanvas = Matrix3x2.CreateTranslation(-minX, -maxY) * Matrix3x2.CreateScale(scale, -scale);
		Vector2 ToWorld(Vector2 canvas) => new(canvas.X / scale + minX, maxY - canvas.Y / scale);
		int width = (int)MathF.Ceiling((maxX - minX) * scale) + 1, height = (int)MathF.Ceiling((maxY - minY) * scale) + 1;

		var bounds = new List<PixelRect>();
		var alphas = new List<byte[]>();
		foreach (var (_, image, toWorld) in parts)
		{
			var toCanvas = toWorld * worldToCanvas;
			var cornersOnCanvas = Corners(image).Select(c => Vector2.Transform(c, toCanvas)).ToList();
			int x0 = (int)MathF.Floor(cornersOnCanvas.Min(c => c.X)), y0 = (int)MathF.Floor(cornersOnCanvas.Min(c => c.Y));
			int x1 = (int)MathF.Ceiling(cornersOnCanvas.Max(c => c.X)), y1 = (int)MathF.Ceiling(cornersOnCanvas.Max(c => c.Y));
			var box = new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));

			using var drawn = new SKBitmap(AssetStore.InfoFor(box.W, box.H));
			drawn.Erase(SKColors.Transparent);
			using (var canvas = new SKCanvas(drawn))
			{
				var place = SkeletonRenderer.ToSk(toCanvas * Matrix3x2.CreateTranslation(-x0, -y0));
				canvas.Concat(in place);
				canvas.DrawBitmap(image, 0, 0, Smooth, null);
			}
			bounds.Add(box);
			alphas.Add(SheetImporter.Alpha(drawn));
		}

		var masks = RigMasks.Build(width, height, bounds, alphas);
		var roles = RigSuggester.SuggestRoles(masks);
		var rig = RigSuggester.Suggest(masks, roles, Enumerable.Repeat(-1, parts.Count).ToList());
		var bones = RigOps.AddSuggestedBones(skeleton, root, rig, ToWorld, parts.Select(p => p.Slot.Name).ToList());
		for (int i = 0; i < parts.Count; i++)
			if (rig.BoneOf[i] >= 0)
				PartOps.MoveToBone(skeleton, parts[i].Slot, bones[rig.BoneOf[i]]);
		return bones.Length;
	}

	private static Vector2[] Corners(SKBitmap image) =>
		new[] { Vector2.Zero, new Vector2(image.Width, 0), new Vector2(image.Width, image.Height), new Vector2(0, image.Height) };
}
