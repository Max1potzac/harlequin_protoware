using System.Text.Json;
using Animator.Core.Serialization;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Step 22: meshes over pictures, weights to bones, deform keys
public class MeshTests
{
	private static void Near(Vector2 expected, Vector2 actual, float within = 0.05f) =>
		Assert.True(Vector2.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

	// An arm of two bones along x: upper (0..50), lower (50..100); a 100×20 picture over both, on
	// the upper bone: pixel (x, y) rests at (x, 10 - y)
	private static (Skeleton Skeleton, Bone Upper, Bone Lower, Slot Slot, Attachment Drawing) Arm()
	{
		var skeleton = new Skeleton();
		var upper = new Bone { Name = "upper", Length = 50 };
		var lower = new Bone { Name = "lower", Length = 50, SetupPosition = new Vector2(50, 0) };
		upper.AddChild(lower);
		skeleton.Roots.Add(upper);
		var asset = new Asset { Name = "arm", Width = 100, Height = 20 };
		skeleton.Assets[asset.Id] = asset;
		var drawing = new Attachment { Name = "arm", AssetId = asset.Id, Offset = new Vector2(50, 0) };
		skeleton.DefaultSkin.Set("arm", drawing);
		var slot = new Slot("arm", upper) { SetupAttachment = "arm" };
		skeleton.Slots.Add(slot);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		drawing.Mesh = MeshOps.Grid(100, 20, (0, 0, 100, 20), 10);
		return (skeleton, upper, lower, slot, drawing);
	}

	private static int PointAt(Mesh mesh, Vector2 pixel) =>
		Enumerable.Range(0, mesh.Points.Length).OrderBy(i => Vector2.Distance(mesh.Points[i], pixel)).First();

	[Fact]
	public void A_grid_mesh_covers_the_pixels_that_show_and_leaves_empty_cells_out()
	{
		var full = MeshOps.Grid(20, 10, (0, 0, 20, 10), 10);
		Assert.Equal(6, full.Points.Length);       // 3 × 2 corners
		Assert.Equal(12, full.Triangles.Length);   // 2 cells, 2 triangles each
		Assert.False(full.Weighted);

		var half = MeshOps.Grid(20, 10, (0, 0, 20, 10), 10, (x0, _, _, _) => x0 < 10);   // only the left cell has pixels
		Assert.Equal(4, half.Points.Length);
		Assert.Equal(6, half.Triangles.Length);
		Assert.All(half.Points, p => Assert.True(p.X <= 10));
	}

	[Fact]
	public void Without_weights_a_mesh_sits_where_its_picture_does()
	{
		var (_, upper, _, slot, drawing) = Arm();
		var posed = MeshOps.Posed(slot, drawing, drawing.Mesh!);
		Near(new Vector2(0, 10), posed[PointAt(drawing.Mesh!, new Vector2(0, 0))]);
		Near(new Vector2(100, -10), posed[PointAt(drawing.Mesh!, new Vector2(100, 20))]);
		Near(new Vector2(30, 5), MeshOps.PixelAt(drawing.Mesh!, posed, new Vector2(30, 5))!.Value);   // (30, 5) in the object: pixel (30, 5)

		upper.Rotation = MathF.PI / 2;   // the whole arm turned up: the picture turns with it
		upper.UpdateWorld();
		Near(new Vector2(10, 100), MeshOps.Posed(slot, drawing, drawing.Mesh!)[PointAt(drawing.Mesh!, new Vector2(100, 20))]);
	}

	[Fact]
	public void Weights_from_the_nearest_bones_bend_the_picture_at_the_elbow()
	{
		var (skeleton, upper, lower, slot, drawing) = Arm();
		var mesh = drawing.Mesh = MeshOps.AutoWeights(skeleton, slot, drawing, drawing.Mesh!);
		int hand = PointAt(mesh, new Vector2(100, 10)), shoulder = PointAt(mesh, new Vector2(0, 10));
		Assert.True(MeshOps.WeightOf(mesh, hand, lower) > 0.95f);
		Assert.True(MeshOps.WeightOf(mesh, shoulder, upper) > 0.95f);
		Assert.All(mesh.Weights!, w => Assert.Equal(1f, w.Sum(s => s.Weight), 3));
		Near(new Vector2(100, 0), MeshOps.Posed(slot, drawing, mesh)[hand]);   // at rest: where it was

		lower.Rotation = MathF.PI / 2;   // the forearm up
		upper.UpdateWorld();
		var posed = MeshOps.Posed(slot, drawing, mesh);
		Near(new Vector2(50, 50), posed[hand], 1f);
		Near(new Vector2(0, 0), posed[shoulder], 1f);

		// All on one bone: the whole picture goes with the forearm
		skeleton.SetToSetupPose();   // (weights are given at rest)
		skeleton.UpdateWorld();
		var stiff = MeshOps.AllOn(skeleton, slot, drawing, mesh.Rigid(), lower);
		lower.Rotation = MathF.PI / 2;
		upper.UpdateWorld();
		Near(new Vector2(50, -50), MeshOps.Posed(slot, drawing, stiff)[shoulder], 1f);   // the shoulder end swings round the elbow too
	}

	[Fact]
	public void Painting_a_bones_weight_raises_its_share_and_the_others_make_up_the_rest()
	{
		var (skeleton, upper, lower, slot, drawing) = Arm();
		var mesh = MeshOps.AllOn(skeleton, slot, drawing, drawing.Mesh!, upper);
		int middle = PointAt(mesh, new Vector2(50, 10));
		var painted = MeshOps.Paint(mesh, lower, new Dictionary<int, float> { [middle] = 1f }, 0.4f);
		Assert.Equal(0.4f, MeshOps.WeightOf(painted, middle, lower), 3);
		Assert.Equal(0.6f, MeshOps.WeightOf(painted, middle, upper), 3);
		Assert.Equal(0f, MeshOps.WeightOf(painted, PointAt(mesh, new Vector2(0, 0)), lower));
		Assert.Same(upper, mesh.Weights![middle].Single().Bone);   // (the old mesh as it was)

		var back = MeshOps.Paint(painted, lower, new Dictionary<int, float> { [middle] = 1f }, -1f);
		Assert.Equal(0f, MeshOps.WeightOf(back, middle, lower), 3);
		Assert.Equal(1f, MeshOps.WeightOf(back, middle, upper), 3);

		var alone = MeshOps.Paint(mesh, upper, new Dictionary<int, float> { [middle] = 1f }, -0.5f);
		Assert.Equal(1f, MeshOps.WeightOf(alone, middle, upper), 3);   // no other bone to give it to
	}

	[Fact]
	public void Deform_keys_push_the_points_of_the_drawing_they_are_for()
	{
		var (skeleton, _, _, slot, drawing) = Arm();
		var mesh = drawing.Mesh!;
		var squash = new Animation { Name = "squash", Duration = 1f };
		var down = Enumerable.Repeat(new Vector2(0, 5), mesh.Points.Length).ToArray();
		MeshOps.KeyDeform(squash, slot, drawing, new Vector2[mesh.Points.Length], 0, 30);
		MeshOps.KeyDeform(squash, slot, drawing, down, 30, 30);
		int corner = PointAt(mesh, new Vector2(0, 0));

		skeleton.SetToSetupPose();
		squash.Apply(skeleton, 0.5f);
		Near(new Vector2(0, 7.5f), MeshOps.Posed(slot, drawing, mesh)[corner]);   // halfway: 2.5 pixels down
		skeleton.SetToSetupPose();
		squash.Apply(skeleton, 1f);
		Near(new Vector2(0, 5), MeshOps.Posed(slot, drawing, mesh)[corner]);

		slot.ActiveAttachment = "other";   // another drawing showing: the keys aren't for it
		Assert.Null(slot.DeformOf(new Attachment { Name = "other", Mesh = mesh }));
		Assert.Equal("arm (mesh)", squash.Find<DeformTimeline>("arm")!.Label);
	}

	[Fact]
	public void Deleting_a_bone_gives_its_shares_to_the_others()
	{
		var (skeleton, upper, lower, slot, drawing) = Arm();
		drawing.Mesh = MeshOps.AutoWeights(skeleton, slot, drawing, drawing.Mesh!);
		RigOps.DeleteBone(skeleton, Array.Empty<Animation>(), lower);
		Assert.All(drawing.Mesh!.Weights!, w => Assert.Equal(upper, Assert.Single(w).Bone));
		Assert.False(drawing.Mesh.BoneBind.ContainsKey(lower));
	}

	[Fact]
	public void Meshes_weights_and_deform_keys_are_saved_and_undone()
	{
		var (skeleton, _, lower, slot, drawing) = Arm();
		drawing.Mesh = MeshOps.AutoWeights(skeleton, slot, drawing, drawing.Mesh!);
		var wave = new Animation { Name = "wave", Duration = 1f };
		var push = Enumerable.Range(0, drawing.Mesh.Points.Length).Select(i => new Vector2(i, -i)).ToArray();
		MeshOps.KeyDeform(wave, slot, drawing, push, 6, 30);

		var dto = ProjectMapper.ToDto(skeleton, new[] { wave }, 30);
		var (loaded, animations) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var mesh = loaded.DefaultSkin.Get("arm", "arm")!.Mesh!;
		Assert.Equal(drawing.Mesh.Points, mesh.Points);
		Assert.Equal(drawing.Mesh.Triangles, mesh.Triangles);
		Assert.Equal((100, 20), (mesh.Width, mesh.Height));
		int hand = PointAt(mesh, new Vector2(100, 10));
		Assert.Equal(MeshOps.WeightOf(drawing.Mesh, hand, lower), MeshOps.WeightOf(mesh, hand, loaded.FindBone("lower")!), 4);
		Assert.Equal(drawing.Mesh.Bind, mesh.Bind);
		var key = animations.Single().Find<DeformTimeline>("arm")!.Track.Keys.Single().Value;
		Assert.Equal("arm", key.Drawing);
		Assert.Equal(push, key.Offsets);

		var before = StructureSnapshot.Capture(skeleton, new List<Animation> { wave });
		var weighted = drawing.Mesh;
		drawing.Mesh = null;
		before.Restore();
		Assert.Same(weighted, drawing.Mesh);
	}

	[Fact]
	public void A_meshed_picture_is_drawn_over_its_triangles_where_its_points_are()
	{
		var store = new AssetStore();
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		skeleton.Roots.Add(root);
		var asset = new Asset { Name = "red", Width = 20, Height = 20 };
		using (var pixels = new SKBitmap(AssetStore.InfoFor(20, 20)))
		{
			pixels.Erase(SKColors.Red);
			store.AddLayer(asset, "Base", pixels);
		}
		skeleton.Assets[asset.Id] = asset;
		var drawing = new Attachment { Name = "red", AssetId = asset.Id, Mesh = MeshOps.Grid(20, 20, (0, 0, 20, 20), 10) };
		skeleton.DefaultSkin.Set("red", drawing);
		var slot = new Slot("red", root) { SetupAttachment = "red" };
		skeleton.Slots.Add(slot);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();

		SKBitmap Render()
		{
			var bitmap = new SKBitmap(AssetStore.InfoFor(100, 100));
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(SKColors.Transparent);
			canvas.Translate(50, 50);
			new SkeletonRenderer(store).DrawImages(canvas, skeleton);
			return bitmap;
		}
		using (var flat = Render())
		{
			Assert.Equal(SKColors.Red, flat.GetPixel(50, 50));
			Assert.Equal(0, flat.GetPixel(65, 50).Alpha);
		}

		slot.Deform = new DeformKey("red", Enumerable.Repeat(new Vector2(12, 0), drawing.Mesh.Points.Length).ToArray());   // all 12 pixels right
		using var pushed = Render();
		Assert.Equal(SKColors.Red, pushed.GetPixel(65, 50));
		Assert.Equal(0, pushed.GetPixel(45, 50).Alpha);
		Assert.Same(slot, new SkeletonRenderer(store).Pick(skeleton, new Vector2(15, 0))?.Slot);
		Assert.Null(new SkeletonRenderer(store).Pick(skeleton, new Vector2(-5, 0)));
	}
}
