using Animator.Viewer;
using Animator.Viewer.Painting;
using Animator.Viewer.Rendering;

namespace Animator.Tests;

// The paint editor's side of the session (EditorSession.Paint.cs): its tools, opening a part's
// drawing, and following the part's drawings as the pose changes
public class SessionPaintTests
{
	// One object, open: a root bone with a part that has two drawings, a and b (the bigger one)
	private static (EditorSession Session, Slot Part, Asset A, Asset B) Open()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 40 };
		skeleton.Roots.Add(root);
		var store = new AssetStore();
		Asset Drawing(string name, int size)
		{
			var asset = new Asset { Name = name, Width = size, Height = size };
			store.AddLayer(asset, "Base");
			skeleton.Assets[asset.Id] = asset;
			skeleton.DefaultSkin.Set("face", new Attachment { Name = name, AssetId = asset.Id });
			return asset;
		}
		var a = Drawing("a", 4);
		var b = Drawing("b", 8);
		var part = new Slot("face", root) { SetupAttachment = "a", ActiveAttachment = "a" };
		skeleton.Slots.Add(part);
		var walk = new Animation { Name = "walk", Duration = 1f, Loop = true };
		var obj = new SceneObject("hero", ObjectKind.Rigged, skeleton) { Animations = { walk } };
		var scene = new Scene();
		scene.Objects.Add(obj);
		var session = new EditorSession(store, new EditorState(skeleton, walk));
		session.Replace(scene, null, null, sceneLevel: false);
		return (session, part, a, b);
	}

	[Fact]
	public void Tools_are_picked_by_name()
	{
		var (session, _, _, _) = Open();
		Assert.Same(session.PaintTools.Brush, session.Paint.Tool);
		Assert.True(session.SelectPaintTool("Fill"));
		Assert.Same(session.PaintTools.Fill, session.Paint.Tool);
		Assert.False(session.SelectPaintTool("Chisel"));
		Assert.Same(session.PaintTools.Fill, session.Paint.Tool);
		Assert.Equal("Fill", session.PaintTools.NameOf(session.Paint.Tool));
		Assert.True(session.PaintTools.BySimilarColour(session.PaintTools.Wand));
		Assert.True(session.PaintTools.DrawsStrokes(session.PaintTools.Eraser));
	}

	[Fact]
	public void The_editor_follows_the_drawing_the_part_shows()
	{
		var (session, part, a, b) = Open();
		Assert.False(session.PaintOpen);
		Assert.True(session.OpenPaintOn(part));
		Assert.Same(a, session.Paint.Asset);
		Assert.Same(part, session.PaintSlot);
		Assert.Equal(EditorSession.PaintFollow.Stay, session.FollowPaint(busy: false));

		// Posed on b: not mid-stroke, nor while playing; then it switches (another size: fit it)
		part.ActiveAttachment = "b";
		Assert.Equal(EditorSession.PaintFollow.Stay, session.FollowPaint(busy: true));
		session.State.Playing = true;
		Assert.Equal(EditorSession.PaintFollow.Stay, session.FollowPaint(busy: false));
		session.State.Playing = false;
		Assert.Equal(EditorSession.PaintFollow.OpenedNewSize, session.FollowPaint(busy: false));
		Assert.Same(b, session.Paint.Asset);

		// b deleted, and the part shows nothing in its place: the editor closes
		session.State.Skeleton.Assets.Remove(b.Id);
		Assert.Equal(EditorSession.PaintFollow.Close, session.FollowPaint(busy: false));
		session.ClosePaint();
		Assert.False(session.PaintOpen);
		Assert.Null(session.PaintSlot);

		// A part showing no drawing can't be opened
		part.ActiveAttachment = null;
		Assert.False(session.OpenPaintOn(part));
	}
}
