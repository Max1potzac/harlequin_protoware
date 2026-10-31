using System.IO;
using Animator.Core.Import;
using Animator.Core.Utilities;
using Animator.Viewer.Import;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 11: Photoshop files in, as drawn objects or with a suggested rig
public class PsdImportTests
{
	// ======================= reading =======================

	[Fact]
	public void A_file_from_another_program_reads_with_its_layers()
	{
		var doc = PsdReader.Read(Path.Combine(AppContext.BaseDirectory, "Files", "imagemagick-layers.psd"));

		Assert.Equal((64, 48, 8), (doc.Width, doc.Height, doc.Depth));
		Assert.False(doc.Flat);
		Assert.Equal(new[] { "body", "head", "eye é" }, doc.Layers.Select(l => l.Name));   // that name is stored as UTF-8
		Assert.All(doc.Layers, l => Assert.True(l.Visible));

		var body = doc.Layers[0];
		Assert.Equal(new PixelRect(22, 14, 20, 24), body.Bounds);
		Assert.Equal(new byte[] { 255, 0, 0, 255 }, body.Pixels[..4]);   // no alpha channel: solid
		Assert.Equal("norm", body.Blend);                                // written back to front ("mron")

		var head = doc.Layers[1];
		Assert.Equal(new PixelRect(24, 0, 16, 16), head.Bounds);
		Assert.Equal(0, head.Pixels[3]);   // the corner of the circle's square
		int middle = (8 * 16 + 8) * 4;
		Assert.Equal(new byte[] { 0, 0, 255, 255 }, head.Pixels[middle..(middle + 4)]);

		Assert.Equal(new byte[] { 255, 255, 255, 128 }, doc.Layers[2].Pixels[..4]);   // half see-through
	}

	[Fact]
	public void Groups_masks_clipping_and_hidden_layers_read_as_photoshop_stores_them()
	{
		var mask = (Left: 20, Top: 10, Width: 4, Height: 4, Outside: (byte)0, Values: Enumerable.Repeat((byte)255, 16).ToArray());
		var bytes = PsdTestFile.Write(40, 30, new[]
		{
			PsdTestFile.Solid("back", 0, 0, 40, 30, 10, 20, 30),
			PsdTestFile.GroupBottom(),
			PsdTestFile.Solid("arm", 5, 5, 10, 6, 200, 100, 50),
			Change(PsdTestFile.Solid("shade", 5, 5, 4, 6, 0, 0, 0, 128), l => { l.Clipped = true; l.Blend = "mul "; l.Raw = true; }),
			PsdTestFile.GroupTop("left", opacity: 128),
			Change(PsdTestFile.Solid("ghost", 0, 0, 2, 2, 1, 2, 3), l => l.Hidden = true),
			Change(PsdTestFile.Solid("masked", 20, 10, 8, 4, 50, 60, 70), l => l.Mask = mask),
			Change(PsdTestFile.Solid("ünïcødé", 30, 20, 3, 3, 9, 9, 9), l => l.UnicodeName = true),
			PsdTestFile.GroupBottom(),
			PsdTestFile.Solid("in a hidden group", 1, 1, 2, 2, 7, 7, 7),
			PsdTestFile.GroupTop("hidden", hidden: true)
		});

		var doc = PsdReader.Read(bytes);

		Assert.Equal(new[] { "back", "arm", "shade", "ghost", "masked", "ünïcødé", "in a hidden group" }, doc.Layers.Select(l => l.Name));
		var arm = doc.Layers[1];
		Assert.Equal(new[] { "left" }, arm.Groups);
		Assert.Equal(128 / 255f, arm.Opacity, 3);   // the group's opacity
		Assert.Empty(doc.Layers[0].Groups);
		Assert.Equal(new byte[] { 200, 100, 50, 255 }, arm.Pixels[..4]);

		var shade = doc.Layers[2];
		Assert.True(shade.Clipped);
		Assert.Equal("mul ", shade.Blend);
		Assert.Equal(128, shade.Pixels[3]);

		Assert.False(doc.Layers[3].Visible);   // hidden itself
		Assert.False(doc.Layers[6].Visible);   // in a hidden group
		Assert.True(doc.Layers[4].Visible);

		var masked = doc.Layers[4];
		Assert.Equal(255, masked.Pixels[3]);           // inside the mask, which is white
		Assert.Equal(0, masked.Pixels[5 * 4 + 3]);     // right of it: the mask's outside is black
	}

	[Fact]
	public void Sixteen_bit_large_documents_and_files_without_layers_read_too()
	{
		var deep = PsdReader.Read(PsdTestFile.Write(8, 4, new[] { PsdTestFile.Solid("dot", 1, 1, 3, 2, 10, 128, 250, 200) }, depth: 16, psb: true));
		Assert.Equal(16, deep.Depth);
		var dot = Assert.Single(deep.Layers);
		Assert.Equal(new PixelRect(1, 1, 3, 2), dot.Bounds);
		Assert.Equal(new byte[] { 10, 128, 250, 200 }, dot.Pixels[..4]);

		var flat = PsdReader.Read(PsdTestFile.Write(8, 4, Array.Empty<PsdTestLayer>()));
		Assert.True(flat.Flat);
		var picture = Assert.Single(flat.Layers);
		Assert.Equal(new PixelRect(0, 0, 8, 4), picture.Bounds);
		Assert.Equal(new byte[] { 255, 255, 255, 255 }, picture.Pixels[..4]);
	}

	[Fact]
	public void Files_it_cant_read_say_why()
	{
		var cmyk = PsdTestFile.Write(4, 4, new[] { PsdTestFile.Solid("a", 0, 0, 1, 1, 1, 1, 1) });
		cmyk[25] = 4;   // the colour mode
		Assert.Contains("CMYK", Assert.Throws<NotSupportedException>(() => PsdReader.Read(cmyk)).Message);

		var cut = PsdTestFile.Write(4, 4, new[] { PsdTestFile.Solid("a", 0, 0, 2, 2, 1, 1, 1) })[..60];
		Assert.Throws<InvalidDataException>(() => PsdReader.Read(cut));
		Assert.Throws<InvalidDataException>(() => PsdReader.Read(new byte[] { 1, 2, 3, 4, 5 }));
	}

	private static PsdTestLayer Change(PsdTestLayer layer, Action<PsdTestLayer> change)
	{
		change(layer);
		return layer;
	}

	// ======================= suggesting a rig =======================

	// A shape's opacity inside its box: solid where `inside` says (everywhere if not given)
	private static (PixelRect Bounds, byte[] Alpha) Shape(int x, int y, int w, int h, Func<int, int, bool>? inside = null)
	{
		var alpha = new byte[w * h];
		for (int j = 0; j < h; j++)
			for (int i = 0; i < w; i++)
				alpha[j * w + i] = inside == null || inside(i, j) ? (byte)255 : (byte)0;
		return (new PixelRect(x, y, w, h), alpha);
	}

	private static bool Circle(int i, int j, int r) => (i - r + 0.5) * (i - r + 0.5) + (j - r + 0.5) * (j - r + 0.5) <= r * r;

	// On a 200 × 300 canvas: a backdrop, a body, a head with an eye, and an arm in two pieces
	internal static readonly (string Name, (PixelRect Bounds, byte[] Alpha) Shape)[] Figure =
	{
		("background", Shape(0, 0, 200, 300)),
		("torso", Shape(70, 80, 60, 100)),
		("head", Shape(68, 18, 64, 64, (i, j) => Circle(i, j, 32))),
		("upper arm", Shape(120, 85, 50, 16)),
		("forearm", Shape(162, 87, 36, 12)),
		("eye", Shape(85, 40, 8, 8))
	};

	private static RigMasks FigureMasks() =>
		RigMasks.Build(200, 300, Figure.Select(f => f.Shape.Bounds).ToList(), Figure.Select(f => f.Shape.Alpha).ToList());

	private static int ParentLayer(RigSuggestion rig, int layer)
	{
		var bone = rig.Bones[rig.BoneOf[layer]];
		return bone.Parent < 0 ? -1 : rig.Bones[bone.Parent].Layer;
	}

	[Fact]
	public void A_figure_gets_bones_that_turn_where_its_layers_overlap()
	{
		var masks = FigureMasks();
		var roles = RigSuggester.SuggestRoles(masks);
		Assert.Equal(new[] { RigRole.OnBone, RigRole.Bone, RigRole.Bone, RigRole.Bone, RigRole.Bone, RigRole.OnBone }, roles);   // backdrop and eye ride along

		var rig = RigSuggester.Suggest(masks, roles, Enumerable.Repeat(-1, 6).ToList());

		Assert.Equal(1, rig.RootLayer);   // the body
		Assert.Equal(4, rig.Bones.Count);
		Assert.Equal(-1, ParentLayer(rig, 1));
		Assert.Equal(1, ParentLayer(rig, 2));
		Assert.Equal(1, ParentLayer(rig, 3));
		Assert.Equal(3, ParentLayer(rig, 4));

		// The root runs up the body to the neck, where the head turns, and the arm bends at the elbow
		var body = rig.Bones[rig.BoneOf[1]];
		var head = rig.Bones[rig.BoneOf[2]];
		var upperArm = rig.Bones[rig.BoneOf[3]];
		var forearm = rig.Bones[rig.BoneOf[4]];
		Assert.Equal(100, body.Head.X, 0);
		Assert.InRange(body.Head.Y, 126, 134);
		Assert.InRange(Vector2.Distance(head.Head, new Vector2(100, 81)), 0, 5);
		Assert.True(head.Connected);
		Assert.Equal(head.Head, body.Tip);
		Assert.InRange(Vector2.Distance(upperArm.Head, new Vector2(125, 93)), 0, 5);
		Assert.InRange(Vector2.Distance(forearm.Head, new Vector2(166, 93)), 0, 5);
		Assert.True(forearm.Connected);
		Assert.Equal(forearm.Head, upperArm.Tip);
		Assert.True(forearm.Tip.X > forearm.Head.X + 20);   // along the forearm, outwards

		Assert.Equal(rig.BoneOf[2], rig.BoneOf[5]);   // the eye rides on the head
		Assert.Equal(rig.BoneOf[1], rig.BoneOf[0]);   // the backdrop on the body
	}

	[Fact]
	public void Chosen_roots_parents_and_bones_to_ride_on_win()
	{
		var masks = FigureMasks();
		var roles = RigSuggester.SuggestRoles(masks).ToList();
		var targets = Enumerable.Repeat(-1, 6).ToList();
		targets[4] = 1;   // the forearm hangs from the body (though they don't meet)
		targets[5] = 1;   // the eye rides on the body
		roles[3] = RigRole.LeaveOut;

		var rig = RigSuggester.Suggest(masks, roles, targets, root: 2);

		Assert.Equal(2, rig.RootLayer);
		Assert.Equal(2, ParentLayer(rig, 1));
		Assert.Equal(1, ParentLayer(rig, 4));
		Assert.Equal(rig.BoneOf[1], rig.BoneOf[5]);
		Assert.Equal(-1, rig.BoneOf[3]);
		Assert.Equal(3, rig.Bones.Count);
	}

	[Fact]
	public void An_arm_across_the_body_is_a_limb_and_the_hips_are_the_root_even_under_a_big_head()
	{
		var parts = new (string Name, (PixelRect Bounds, byte[] Alpha) Shape)[]
		{
			("torso", Shape(80, 180, 80, 120)),
			("hips", Shape(75, 290, 90, 40)),
			("head", Shape(50, 50, 140, 140, (i, j) => Circle(i, j, 70))),   // bigger than the body
			("thigh", Shape(90, 320, 25, 60)),
			("shin", Shape(88, 375, 25, 20)),
			("upper arm", Shape(100, 200, 65, 20)),  // lies on the body…
			("forearm", Shape(155, 205, 70, 14)),    // …and goes on beyond it
			("eye", Shape(100, 100, 10, 10))
		};
		var masks = RigMasks.Build(300, 400, parts.Select(p => p.Shape.Bounds).ToList(), parts.Select(p => p.Shape.Alpha).ToList());

		var roles = RigSuggester.SuggestRoles(masks);
		Assert.Equal(RigRole.Bone, roles[5]);     // the upper arm: a limb, not a detail of the body
		Assert.Equal(RigRole.OnBone, roles[7]);   // the eye: a detail of the head

		var rig = RigSuggester.Suggest(masks, roles, Enumerable.Repeat(-1, parts.Length).ToList());
		Assert.Equal(1, rig.RootLayer);           // the hips: the middle of the figure
		Assert.Equal(5, ParentLayer(rig, 6));     // the forearm hangs from the upper arm
		Assert.Equal(0, ParentLayer(rig, 5));     // which hangs from the body
	}

	[Fact]
	public void Line_art_over_the_whole_figure_rides_along_instead_of_taking_over()
	{
		var parts = new List<(PixelRect Bounds, byte[] Alpha)>
		{
			Shape(40, 40, 40, 60), Shape(45, 10, 30, 32), Shape(78, 45, 30, 10), Shape(12, 45, 30, 10), Shape(45, 98, 12, 40)
		};
		// One layer with the outline of every part
		var all = parts.Select(p => p.Bounds).Aggregate(PixelRect.Union);
		var lines = new byte[all.W * all.H];
		foreach (var (b, _) in parts)
			for (int y = b.Y; y < b.Bottom; y++)
				for (int x = b.X; x < b.Right; x++)
					if (x == b.X || y == b.Y || x == b.Right - 1 || y == b.Bottom - 1)
						lines[(y - all.Y) * all.W + x - all.X] = 255;
		parts.Add((all, lines));

		var masks = RigMasks.Build(200, 200, parts.Select(p => p.Bounds).ToList(), parts.Select(p => p.Alpha).ToList());
		var roles = RigSuggester.SuggestRoles(masks);

		Assert.Equal(RigRole.OnBone, roles[5]);
		Assert.All(roles.Take(5), r => Assert.Equal(RigRole.Bone, r));
		Assert.Equal(0, RigSuggester.Suggest(masks, roles, Enumerable.Repeat(-1, 6).ToList()).RootLayer);
	}

	// ======================= bringing it in =======================

	private static PsdLayer Layer(string name, int x, int y, int w, int h, SKColor colour, Func<int, int, bool>? inside = null,
		params string[] groups)
	{
		var pixels = new byte[w * h * 4];
		for (int j = 0; j < h; j++)
			for (int i = 0; i < w; i++)
			{
				if (inside != null && !inside(i, j)) continue;
				int at = (j * w + i) * 4;
				(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (colour.Red, colour.Green, colour.Blue, colour.Alpha);
			}
		var layer = new PsdLayer { Name = name, Bounds = new PixelRect(x, y, w, h), Pixels = pixels };
		layer.Groups.AddRange(groups);
		return layer;
	}

	private static PsdDocument Document(int width, int height, params PsdLayer[] layers)
	{
		var doc = new PsdDocument { Width = width, Height = height, Depth = 8 };
		doc.Layers.AddRange(layers);
		return doc;
	}

	private static SKColor PixelOf(AssetStore store, SceneObject obj, string part, int x, int y)
	{
		var slot = obj.Skeleton.FindSlot(part)!;
		var drawing = obj.Skeleton.DefaultSkin.Get(slot.Name, slot.SetupAttachment!)!;
		return store.Get(obj.Skeleton.Assets[drawing.AssetId])!.GetPixel(x, y);
	}

	[Fact]
	public void Layers_come_in_as_parts_of_a_drawn_object_where_they_were()
	{
		var doc = Document(100, 80,
			Layer("back", 0, 0, 100, 80, new SKColor(128, 128, 128)),
			Layer("arm", 10, 10, 20, 10, SKColors.Red, null, "left"),
			Layer("arm", 60, 10, 20, 10, SKColors.Blue, null, "right"),
			Change(Layer("shade", 60, 10, 10, 10, new SKColor(0, 0, 0, 128), null, "right"), l => { l.Clipped = true; l.Blend = "mul "; }),
			Change(Layer("ghost", 0, 0, 5, 5, SKColors.Green), l => l.Visible = false),
			Layer("nothing", 0, 0, 5, 5, SKColors.Transparent),
			Change(Layer("dot", 40, 40, 20, 20, SKColors.White, (i, j) => i >= 8 && i < 12 && j >= 8 && j < 12), l => l.Opacity = 0.5f));
		using var plan = PsdImportPlan.FromDocument(doc, "file");

		Assert.Equal(new[] { "back", "left arm", "right arm", "ghost", "dot" }, plan.Parts.Select(p => p.Name));   // names made unique by their groups
		Assert.False(plan.Parts[3].Include);                          // hidden in the file
		Assert.Equal(new PixelRect(48, 48, 4, 4), plan.Parts[4].Bounds);   // trimmed to what shows
		Assert.Equal(2, plan.Notes.Count);                            // a clipped layer, an empty one

		var store = new AssetStore();
		var obj = PsdImporter.IntoScene(new Scene(), store, plan, new Vector2(500, 300));

		Assert.Equal((ObjectKind.Drawn, "file"), (obj.Kind, obj.Name));
		Assert.Equal(new Vector2(500, 300), obj.Position);
		Assert.Equal(new[] { "back", "left arm", "right arm", "dot" }, obj.Skeleton.Slots.Select(s => s.Name));
		var arm = obj.Skeleton.DefaultSkin.Get("left arm", "left arm")!;
		Assert.Equal(new Vector2(-30, 25), arm.Offset);   // its middle (20, 15) from the canvas's middle (50, 40), y up
		Assert.Equal(0.5f, obj.Skeleton.FindSlot("dot")!.SetupOpacity);

		// The shading clipped to the right arm darkened it where it was, and only there
		var shaded = PixelOf(store, obj, "right arm", 2, 5);
		Assert.InRange(shaded.Blue, 100, 160);
		Assert.Equal(255, shaded.Alpha);
		Assert.Equal(SKColors.Blue, PixelOf(store, obj, "right arm", 15, 5));

		// Half the size: smaller pixels, nearer the middle
		plan.Scale = 0.5f;
		var small = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
		var smallArm = small.Skeleton.DefaultSkin.Get("left arm", "left arm")!;
		Assert.Equal(new Vector2(-15, 12.5f), smallArm.Offset);
		Assert.Equal((10, 5), (small.Skeleton.Assets[smallArm.AssetId].Width, small.Skeleton.Assets[smallArm.AssetId].Height));
	}

	private static PsdLayer Change(PsdLayer layer, Action<PsdLayer> change)
	{
		change(layer);
		return layer;
	}

	// Your own files: put Photoshop files in Animator.Tests/PrivateFiles (git leaves them out) and
	// run the tests. Each must come in as a drawn object and with a suggested rig, every layer
	// where it was in the file. (Nothing there: nothing to check.)
	[Fact]
	public void Photoshop_files_of_your_own_come_in_both_ways()
	{
		string folder = Path.Combine(AppContext.BaseDirectory, "PrivateFiles");
		if (!Directory.Exists(folder)) return;
		foreach (string path in Directory.GetFiles(folder).Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
																	   f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase)))
		{
			var doc = PsdReader.Read(path);
			using var plan = PsdImportPlan.FromDocument(doc, Path.GetFileNameWithoutExtension(path));
			var store = new AssetStore();

			var drawn = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
			Assert.Equal(plan.Parts.Count(p => p.Include), drawn.Skeleton.Slots.Count);

			plan.SuggestRig = true;
			plan.SuggestRoles();
			var rigged = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
			Assert.Single(rigged.Skeleton.Roots);
			var included = plan.Parts.Where(p => p.Include).ToList();   // the parts, in the order they came in
			Assert.Equal(included.Count, rigged.Skeleton.Slots.Count);
			for (int i = 0; i < included.Count; i++)
			{
				var (part, slot) = (included[i], rigged.Skeleton.Slots[i]);
				var drawing = rigged.Skeleton.DefaultSkin.Get(slot.Name, slot.Name)!;
				var placed = Vector2.Transform(drawing.Offset, slot.Bone.World);
				var expected = new Vector2(part.Middle.X - plan.Width / 2f, plan.Height / 2f - part.Middle.Y);
				Assert.True(Vector2.Distance(placed, expected) < 0.05f, $"{Path.GetFileName(path)}: {slot.Name} moved");
			}
		}
	}

	[Fact]
	public void A_suggested_rig_brings_bones_with_each_part_on_its_bone_upright()
	{
		var colours = new[] { SKColors.LightBlue, SKColors.Red, SKColors.Bisque, SKColors.Orange, SKColors.Gold, SKColors.Black };
		var layers = Figure.Select((f, k) =>
		{
			var b = f.Shape.Bounds;
			var alpha = f.Shape.Alpha;
			return Layer(f.Name, b.X, b.Y, b.W, b.H, colours[k], (i, j) => alpha[j * b.W + i] > 0);
		}).ToArray();
		using var plan = PsdImportPlan.FromDocument(Document(200, 300, layers), "hero");
		plan.SuggestRig = true;
		plan.SuggestRoles();

		var obj = PsdImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero);

		Assert.Equal(ObjectKind.Rigged, obj.Kind);
		var skeleton = obj.Skeleton;
		var root = Assert.Single(skeleton.Roots);   // at the object's origin: a handle for the whole rig
		var bones = skeleton.AllBones().ToDictionary(b => b.Name);
		Assert.Equal(new[] { "forearm", "head", "root", "torso", "upper arm" }, bones.Keys.OrderBy(k => k));
		var torso = bones["torso"];
		Assert.Same(root, torso.Parent);
		Assert.Same(torso, bones["head"].Parent);
		Assert.Same(bones["upper arm"], bones["forearm"].Parent);
		Assert.True(bones["forearm"].Connected);

		Assert.Same(bones["head"], skeleton.FindSlot("eye")!.Bone);
		Assert.Same(torso, skeleton.FindSlot("background")!.Bone);

		// Every part sits where it was in the file (its middle from the canvas's middle, y up), upright
		foreach (var slot in skeleton.Slots)
		{
			var part = plan.Parts.Single(p => p.Name == slot.Name);
			var drawing = skeleton.DefaultSkin.Get(slot.Name, slot.Name)!;
			var placed = Vector2.Transform(drawing.Offset, slot.Bone.World);
			var expected = new Vector2(part.Middle.X - 100, 150 - part.Middle.Y);
			Assert.InRange(Vector2.Distance(placed, expected), 0, 0.01f);
			Assert.Equal(0, MathUtil.ShortestAngle(slot.Bone.WorldRotation + drawing.Rotation), 3);
		}
	}
}
