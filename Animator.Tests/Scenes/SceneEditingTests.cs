using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 9.5 step 2: adding, copying, renaming, deleting and placing objects
public class SceneEditingTests
{
	private static SceneObject Character(string name, string boneName = "torso")
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = boneName, Length = 40, SetupRotation = MathF.PI / 2 });
		var obj = new SceneObject(name, ObjectKind.Rigged, skeleton);
		obj.Animations.Add(new Animation { Name = "idle", Duration = 1f, Loop = true });
		return obj;
	}

	private static Scene SceneOf(params SceneObject[] objects)
	{
		var scene = new Scene();
		scene.Objects.AddRange(objects);
		return scene;
	}

	// A part with one 4×4 drawing of two layers, its pixels in the store
	private static Asset AddPart(SceneObject obj, AssetStore store)
	{
		var asset = new Asset { Name = "body", Width = 4, Height = 4 };
		store.AddLayer(asset, "colour");
		store.AddLayer(asset, "lines");
		obj.Skeleton.Assets[asset.Id] = asset;
		obj.Skeleton.Slots.Add(new Slot("body", obj.Skeleton.Roots[0]) { SetupAttachment = "body" });
		obj.Skeleton.DefaultSkin.Set("body", new Attachment { Name = "body", AssetId = asset.Id });
		return asset;
	}

	[Fact]
	public void A_new_rigged_object_has_a_root_bone_and_an_animation()
	{
		var scene = SceneOf(Character("object"));

		var added = SceneOps.AddRigged(scene, "object", new Vector2(50, 20));

		Assert.Equal("object2", added.Name);
		Assert.Same(added, scene.Objects[^1]);   // in front of everything
		Assert.Equal(new Vector2(50, 20), added.Position);
		Assert.Single(added.Skeleton.Roots);
		Assert.Single(added.Animations);
	}

	[Fact]
	public void A_copy_has_its_own_rig_animations_and_pictures()
	{
		var store = new AssetStore();
		var zombie = Character("zombie");
		var asset = AddPart(zombie, store);
		zombie.Position = new Vector2(10, 5);
		var scene = SceneOf(zombie, Character("tree"));

		var (copy, layers) = SceneOps.Duplicate(scene, zombie);
		foreach (var (newLayer, oldLayer) in layers) store.CopyPixels(oldLayer, newLayer);

		Assert.Equal(new[] { "zombie", "zombie2", "tree" }, scene.Objects.Select(o => o.Name));
		Assert.Equal(zombie.Position, copy.Position);
		Assert.NotSame(zombie.Skeleton, copy.Skeleton);
		Assert.NotSame(zombie.Skeleton.Roots[0], copy.Skeleton.Roots[0]);
		Assert.Equal("idle", Assert.Single(copy.Animations).Name);
		Assert.NotSame(zombie.Animations[0], copy.Animations[0]);

		// The copy's picture is a different asset, with different layers, and the part shows it
		var copiedAsset = Assert.Single(copy.Skeleton.Assets.Values);
		Assert.NotEqual(asset.Id, copiedAsset.Id);
		Assert.Equal(2, layers.Count);
		Assert.DoesNotContain(copiedAsset.Layers, l => asset.Layers.Any(o => o.Id == l.Id));
		Assert.Equal(copiedAsset.Id, copy.Skeleton.DefaultSkin.Get("body", "body")!.AssetId);

		// Painting the copy leaves the original alone
		store.GetLayer(copiedAsset.Layers[1])!.SetPixel(1, 1, SKColors.Red);
		Assert.Equal(255, (int)store.GetLayer(copiedAsset.Layers[1])!.GetPixel(1, 1).Alpha);
		Assert.Equal(0, (int)store.GetLayer(asset.Layers[1])!.GetPixel(1, 1).Alpha);

		// And changing the copy's rig leaves the original's alone
		copy.Skeleton.Roots[0].Length = 99;
		Assert.Equal(40f, zombie.Skeleton.Roots[0].Length);
	}

	[Fact]
	public void Copies_are_numbered_after_the_original()
	{
		var tree = Character("tree");
		var tree2 = Character("tree2");
		var scene = SceneOf(tree, tree2);

		Assert.Equal("tree3", SceneOps.Duplicate(scene, tree2).Copy.Name);   // not "tree22"
		Assert.Equal("tree4", SceneOps.Duplicate(scene, tree).Copy.Name);
	}

	[Fact]
	public void Renaming_an_object_keeps_what_is_attached_to_it()
	{
		var zombie = Character("zombie");
		var hat = Character("hat", "root");
		hat.AttachObject = "zombie";
		hat.AttachBone = "torso";
		var scene = SceneOf(zombie, hat);

		Assert.True(SceneOps.RenameObject(scene, zombie, "boss"));
		Assert.Equal("boss", hat.AttachObject);
		Assert.Same(zombie.Skeleton.Roots[0], scene.AttachmentBone(hat));

		Assert.False(SceneOps.RenameObject(scene, zombie, "hat"));   // taken
		Assert.False(SceneOps.RenameObject(scene, zombie, "  "));    // empty
		Assert.Equal("boss", zombie.Name);
	}

	[Fact]
	public void Deleting_an_object_lets_go_of_what_it_carried_and_keeps_one_object()
	{
		var zombie = Character("zombie");
		var hat = Character("hat", "root");
		hat.AttachObject = "zombie";
		hat.AttachBone = "torso";
		var scene = SceneOf(zombie, hat);

		Assert.True(SceneOps.DeleteObject(scene, zombie));
		Assert.Null(hat.AttachObject);
		Assert.Null(hat.AttachBone);
		Assert.False(SceneOps.DeleteObject(scene, hat));   // the last one stays
		Assert.Single(scene.Objects);
	}

	[Fact]
	public void Undo_brings_back_the_same_objects_in_the_same_order()
	{
		var a = Character("a");
		var b = Character("b");
		var c = Character("c");
		b.Position = new Vector2(3, 4);
		var scene = SceneOf(a, b, c);
		var before = SceneSnapshot.Capture(scene);

		SceneOps.DeleteObject(scene, b);
		SceneOps.MoveInOrder(scene, c, -1);
		SceneOps.RenameObject(scene, a, "renamed");
		before.Restore();

		Assert.Equal(new[] { a, b, c }, scene.Objects);   // the very same instances
		Assert.Equal("a", a.Name);
		Assert.Equal(new Vector2(3, 4), b.Position);
	}

	[Fact]
	public void Objects_move_forward_and_backward_in_the_draw_order()
	{
		var a = Character("a");
		var b = Character("b");
		var scene = SceneOf(a, b);

		Assert.True(SceneOps.MoveInOrder(scene, a, +1));
		Assert.Equal(new[] { b, a }, scene.Objects);
		Assert.False(SceneOps.MoveInOrder(scene, a, +1));   // already at the front
		Assert.True(SceneOps.MoveInOrder(scene, a, -1));
		Assert.Equal(new[] { a, b }, scene.Objects);
	}

	[Fact]
	public void An_attached_object_follows_its_holders_bone()
	{
		var zombie = Character("zombie");   // its bone points up (turned 90°)
		zombie.Position = new Vector2(100, 0);
		var hat = Character("hat", "root");
		hat.Position = new Vector2(10, 0);   // 10 along the bone
		hat.AttachObject = "zombie";
		hat.AttachBone = "torso";
		var scene = SceneOf(zombie, hat);
		zombie.Skeleton.SetToSetupPose();
		zombie.Skeleton.UpdatePose();

		var p = Vector2.Transform(Vector2.Zero, scene.WorldTransform(hat));

		Assert.True(Vector2.Distance(new Vector2(100, 10), p) < 1e-3f, p.ToString());
	}

	[Fact]
	public void Objects_attached_to_each_other_do_not_hang()
	{
		var a = Character("a", "root");
		var b = Character("b", "root");
		a.AttachObject = "b";
		a.AttachBone = "root";
		b.AttachObject = "a";
		b.AttachBone = "root";
		var scene = SceneOf(a, b);
		a.Skeleton.UpdatePose();
		b.Skeleton.UpdatePose();

		var m = scene.WorldTransform(a);   // stops after a few levels instead of looping forever

		Assert.False(float.IsNaN(m.M31));
	}
}
