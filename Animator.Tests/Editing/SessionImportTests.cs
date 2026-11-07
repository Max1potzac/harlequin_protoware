using System.Numerics;
using Animator.Core.Export;
using Animator.Core.Utilities;
using Animator.Viewer;
using Animator.Viewer.Import;
using SkiaSharp;

namespace Animator.Tests;

// Files brought into the project through the session (EditorSession.Import.cs), another object
// merged into the open one, and a part's blend, colour and opacity (EditorSession.Parts.cs)
public class SessionImportTests
{
	// One rigged object, open: a root bone with a body part
	private static (EditorSession Session, SceneObject Hero, Bone Root, Slot Body) Open()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 40 };
		skeleton.Roots.Add(root);
		var asset = new Asset { Name = "body", Width = 4, Height = 4 };
		skeleton.Assets[asset.Id] = asset;
		var body = new Slot("body", root) { SetupAttachment = "body", ActiveAttachment = "body" };
		skeleton.Slots.Add(body);
		skeleton.DefaultSkin.Set("body", new Attachment { Name = "body", AssetId = asset.Id });
		var walk = new Animation { Name = "walk", Duration = 1f, Loop = true };
		var hero = new SceneObject("hero", ObjectKind.Rigged, skeleton) { Animations = { walk } };
		var scene = new Scene();
		scene.Objects.Add(hero);
		var session = new EditorSession(new AssetStore(), new EditorState(skeleton, walk));
		session.Replace(scene, null, null, sceneLevel: false);
		skeleton.UpdateWorld();
		return (session, hero, root, body);
	}

	private static SheetImportPlan Squares(SKBitmap sheet)
	{
		var plan = new SheetImportPlan(sheet, "sheet");
		for (int i = 0; i < 2; i++)
			plan.Pieces.Add(new SheetPiece { Rect = new PixelRect(i * 10, 0, 10, 10), Name = $"square{i + 1}" });
		return plan;
	}

	[Fact]
	public void Files_that_cant_be_read_say_why_instead_of_throwing()
	{
		string folder = Path.Combine(Path.GetTempPath(), "hq-session-import-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			string psd = Path.Combine(folder, "junk.psd");
			File.WriteAllBytes(psd, new byte[] { 1, 2, 3, 4, 5 });
			Assert.Null(EditorSession.ReadPhotoshop(psd, out string? why));
			Assert.False(string.IsNullOrEmpty(why));

			string fla = Path.Combine(folder, "junk.fla");
			File.WriteAllBytes(fla, new byte[] { 1, 2, 3, 4, 5 });
			Assert.Null(EditorSession.ReadAnimate(fla, out why));
			Assert.False(string.IsNullOrEmpty(why));

			Assert.Null(EditorSession.ReadSheetJson(Path.Combine(folder, "sheet.png")));   // no JSON next to it
			File.WriteAllText(Path.Combine(folder, "sheet.json"), "not json");
			Assert.Null(EditorSession.ReadSheetJson(Path.Combine(folder, "sheet.png")));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void A_sprite_sheet_comes_in_as_parts_of_the_open_object_or_as_objects_of_the_scene()
	{
		var (session, hero, root, _) = Open();
		var state = session.State;
		using var sheet = new SKBitmap(AssetStore.InfoFor(20, 10));
		sheet.Erase(SKColors.Red);

		Assert.Same(root, session.BoneForSheet());
		var parts = session.ImportSheetIntoObject(Squares(sheet), root, "Import sprite sheet");
		Assert.Equal(2, parts.Count);
		Assert.Same(parts[0], state.SelectedSlot);
		Assert.Equal(3, hero.Skeleton.Slots.Count);
		state.Undo.Undo();
		Assert.Single(hero.Skeleton.Slots);

		var objects = session.ImportSheetIntoScene(Squares(sheet), new Vector2(100, 50), "Import sprite sheet");
		Assert.NotEmpty(objects);
		Assert.Equal(1 + objects.Count, session.Scene.Objects.Count);
		state.Undo.Undo();
		Assert.Single(session.Scene.Objects);
	}

	[Fact]
	public void Another_objects_animations_are_brought_into_the_open_one()
	{
		var (session, hero, root, _) = Open();
		var state = session.State;

		// A twin with a run that turns its root
		var twinRig = new Skeleton();
		twinRig.Roots.Add(new Bone { Name = "root", Length = 40 });
		var run = new Animation { Name = "run", Duration = 1f };
		run.GetOrCreate("root", n => new RotateTimeline(n)).Track.Add(0.5f, 0.3f);
		var twin = new SceneObject("twin", ObjectKind.Rigged, twinRig) { Animations = { run } };
		session.Scene.Objects.Add(twin);

		var plan = ObjectMerge.Suggest(twinRig, twin.Animations, hero.Skeleton);
		var result = session.BringIn(twin, plan);
		Assert.NotNull(result);
		Assert.Equal(1, result!.Animations);
		Assert.Contains(hero.Animations, a => a.Name == "run");
		state.Undo.Undo();
		Assert.DoesNotContain(hero.Animations, a => a.Name == "run");

		// Animate projects that can't be read are left out, saying so; nothing to undo
		int steps = state.Undo.UndoCount;
		var none = session.BringInFromAnimate(new[] { Path.Combine(Path.GetTempPath(), "no-such-project.fla") }, new BringInOptions(new[] { root }));
		Assert.Empty(none.Brought);
		Assert.Single(none.Problems);
		Assert.Equal(steps, state.Undo.UndoCount);
	}

	[Fact]
	public void A_parts_blend_colour_and_opacity_are_set_at_rest_or_keyed()
	{
		var (session, _, _, body) = Open();
		var state = session.State;

		Assert.True(session.SetBlend(body, BlendMode.Add));
		Assert.False(session.SetBlend(body, BlendMode.Add));
		Assert.Equal(BlendMode.Add, body.Blend);

		Assert.False(session.SetPartColor(body, null, null));
		Assert.True(session.SetPartColor(body, new Vector3(1, 0, 0), 0.5f));
		Assert.Equal(new Vector3(1, 0, 0), body.SetupColor);
		Assert.Equal(0.5f, body.SetupOpacity);
		state.Undo.Undo();
		Assert.Equal(1f, body.SetupOpacity);

		state.AnimateMode = true;
		Assert.True(session.SetPartColor(body, null, 0.25f));
		Assert.NotNull(state.Animation.Find<PartOpacityTimeline>(body.Name));
		Assert.Null(state.Animation.Find<PartColorTimeline>(body.Name));
	}
}
