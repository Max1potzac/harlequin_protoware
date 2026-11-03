using System.Numerics;
using Animator.Viewer;

namespace Animator.Tests;

// The open object's rig and skins, edited through the session (EditorSession.Rig.cs, .Skins.cs):
// what the Rig, Bind and Skins panels call
public class SessionRigTests
{
	// One object, open: a root bone, an arm on its end with a hand part, and a hat part
	private static (EditorSession Session, Bone Root, Bone Arm, Slot Hand, Slot Hat) Rig()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 40 };
		skeleton.Roots.Add(root);
		var arm = new Bone { Name = "arm", Length = 30, SetupPosition = new Vector2(40, 0), Connected = true };
		root.AddChild(arm);
		Slot Part(string name, Bone bone)
		{
			var asset = new Asset { Name = name, Width = 4, Height = 4 };
			skeleton.Assets[asset.Id] = asset;
			var slot = new Slot(name, bone) { SetupAttachment = name, ActiveAttachment = name };
			skeleton.Slots.Add(slot);
			skeleton.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = asset.Id });
			return slot;
		}
		var hand = Part("hand", arm);
		var hat = Part("hat", root);
		var walk = new Animation { Name = "walk", Duration = 1f, Loop = true };
		var obj = new SceneObject("hero", ObjectKind.Rigged, skeleton) { Animations = { walk } };
		var scene = new Scene();
		scene.Objects.Add(obj);
		var session = new EditorSession(new AssetStore(), new EditorState(skeleton, walk));
		session.Replace(scene, null, null, sceneLevel: false);
		skeleton.UpdateWorld();
		return (session, root, arm, hand, hat);
	}

	[Fact]
	public void Bones_are_added_renamed_set_and_deleted_as_undo_steps()
	{
		var (session, root, arm, hand, _) = Rig();
		var made = session.AddBone(arm, snap: true);
		Assert.Same(arm, made.Parent);
		Assert.True(made.Connected);
		Assert.Same(made, session.State.Selected);

		Assert.False(session.RenameBone(made, "arm"));   // taken
		Assert.True(session.RenameBone(made, "finger"));
		Assert.Equal("finger", made.Name);

		Assert.True(session.SetBoneRest(arm, BoneField.Rotation, 0.5f, keepParts: false));
		Assert.Equal(0.5f, arm.SetupRotation, 4);
		Assert.True(session.SetBoneRest(arm, BoneField.X, 10f, keepParts: false));
		Assert.False(arm.Connected);                    // placed: off its parent's end
		Assert.False(session.SetBoneRest(arm, BoneField.Follow, 1f, keepParts: false));   // it has no look

		Assert.True(session.KeyBone(arm, BoneField.Rotation, 1f));
		Assert.Single(session.State.Animation.Timelines.OfType<RotateTimeline>());

		// Deleted keeping what's on it: the hand goes to the root
		Assert.True(session.DissolveBone(arm));
		Assert.Same(root, hand.Bone);
		Assert.Same(root, session.State.Selected);
		session.State.Undo.Undo();
		Assert.Same(arm, hand.Bone);
	}

	[Fact]
	public void Ik_look_and_parts_on_bones_are_edited_through_the_session()
	{
		var (session, root, arm, hand, hat) = Rig();
		var tip = session.AddBone(arm, snap: true);
		var target = session.AddBone(null, snap: false);
		Assert.True(session.SetIkLength(tip, 2));
		var ik = session.IkEndingAt(tip)!;
		Assert.Same(ik, session.IkContaining(arm));
		Assert.True(session.SetIkTarget(ik, target));
		Assert.True(session.SetIkOptions(ik, on: true, flip: true));
		Assert.False(session.SetIkOptions(ik, on: true, flip: true));
		Assert.True(session.SetIkLength(tip, 0));
		Assert.Null(session.IkEndingAt(tip));

		Assert.True(session.SetLookTarget(arm, target));
		Assert.NotNull(session.LookOf(arm));
		Assert.True(session.SetBoneRest(arm, BoneField.Follow, 0.25f, keepParts: false));

		Assert.Equal(1, session.PutPartsOnBone(new[] { hat }, arm));
		Assert.Same(arm, hat.Bone);
		var loose = session.AddBone(null, snap: false);
		Assert.Equal(1, session.HangBones(new[] { loose }, arm, keepParts: true));
		Assert.Same(arm, loose.Parent);
	}

	[Fact]
	public void Skins_are_made_shown_filled_and_renamed_through_the_session()
	{
		var (session, _, _, hand, hat) = Rig();
		var skin = session.MakeSkinFromParts(new[] { hat })!;
		Assert.Equal("hat", skin.Name);
		Assert.Contains(hat, SkinOps.PartsOf(session.State.Skeleton, skin));
		Assert.True(session.ShowSkin(skin, true));
		Assert.False(session.ShowSkin(skin, true));   // on already
		Assert.True(session.SetSkinKind(skin, "Head", overlay: false));
		Assert.True(session.RenameCategory("Head", "Hats"));
		Assert.Equal("Hats", skin.Category);
		Assert.True(session.HidePartsInSkin(skin, new[] { hand }, hide: true));
		Assert.Null(session.State.Skeleton.ResolveAttachment(hand));

		var other = session.MakeEmptySkin()!;
		Assert.Same(other, session.State.EditSkin);
		Assert.False(session.RenameSkin(other, "hat"));   // taken
		Assert.True(session.RenameSkin(other, "spare"));
		Assert.True(session.DeleteSkin(other));
		Assert.DoesNotContain(other, session.State.Skeleton.Skins);

		Assert.Equal("hat", EditorSession.CommonName(new[] { hat }));
		Assert.Single(session.SkinGroups());

		// Nothing at scene level: skins belong to the open object
		session.Leave();
		Assert.False(session.DissolveSkin(skin));
	}
}
