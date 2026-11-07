using System.Numerics;
using Animator.Viewer;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using SkiaSharp;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// What the viewport shows, drawn without a window (ViewportPainter)
public class ViewportPainterTests
{
	private static (EditorSession Session, ViewportPainter Painter, Rig Rig) Painter(bool sceneLevel)
	{
		var rig = new Rig();
		var obj = new SceneObject("hero", ObjectKind.Rigged, rig.Skeleton) { Animations = { rig.Animation } };
		var scene = new Scene();
		scene.Objects.Add(obj);
		var session = new EditorSession(rig.Store, rig.State);
		session.Replace(scene, null, null, sceneLevel: sceneLevel);
		var objects = new ObjectTool(() => session.Scene, session.SceneState, rig.Camera, rig.Renderer, rig.State.Undo);
		var guides = new GuideTool(session, rig.Camera);
		var mesh = new MeshEditTool(session, rig.Camera);
		var painter = new ViewportPainter(session, rig.Camera, rig.Renderer, rig.Tool, objects, guides, mesh) { ShowGrid = true };
		return (session, painter, rig);
	}

	private static int Coloured(SKBitmap bitmap)
	{
		int n = 0;
		for (int y = 0; y < bitmap.Height; y += 2)
			for (int x = 0; x < bitmap.Width; x += 2)
				if (bitmap.GetPixel(x, y).Alpha > 0) n++;
		return n;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_viewport_draws_inside_an_object_and_at_scene_level(bool sceneLevel)
	{
		var (session, painter, rig) = Painter(sceneLevel);
		session.Scene.Guides.Add(new Guide(vertical: true, 10f));
		painter.Onion.Enabled = true;   // (ghosts: the animation has no other frames to show)
		painter.ShowHitboxes = true;

		using var bitmap = new SKBitmap(200, 160);
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		painter.Draw(canvas, new Vector2(100, 80), dpi: 2f);
		canvas.Flush();

		Assert.True(Coloured(bitmap) > 0);
		Assert.Equal(new Vector2(100, 80), rig.Camera.ViewSize);
	}

	[Fact]
	public void Nothing_painted_means_no_light_table()
	{
		var (_, painter, _) = Painter(sceneLevel: false);
		painter.Onion.Enabled = true;
		Assert.Empty(painter.LightTable());
		Assert.Equal(new SKColor(0, 0, 255), ViewportPainter.Heat(0f));
		Assert.Equal(new SKColor(255, 0, 0), ViewportPainter.Heat(1f));
	}
}
