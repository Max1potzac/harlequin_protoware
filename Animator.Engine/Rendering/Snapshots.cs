using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// Still pictures of one object, without a window: one frame of an animation (or the rest pose),
// and a contact sheet of skins side by side (every head in the Head category, say), each with its
// name under it. The command line's render uses these to show what a project looks like.
public static class Snapshots
{
	// The object (in its own space, y up) posed at a time of an animation, or at rest with none.
	// Null when nothing shows.
	public static SKBitmap? Frame(SceneObject obj, AssetStore assets, Animation? animation, float time, float scale = 1f,
		bool colorLayers = true, SKColor? background = null, float margin = 4f)
	{
		Pose(obj, animation, time);
		var renderer = new SkeletonRenderer(assets) { ColorLayers = colorLayers };
		if (renderer.ImageBounds(obj.Skeleton, margin) is not { } box) return null;
		var picture = NewPicture(box, scale);
		using var canvas = new SKCanvas(picture);
		canvas.Clear(background ?? SKColors.Transparent);
		Draw(canvas, renderer, obj, box, scale);
		return picture;
	}

	// One cell per skin (each shown on its own in its category, the others as they are), all
	// posed alike and drawn to the same box, so they line up; the names go under them when a
	// typeface is given. Null when nothing shows.
	public static SKBitmap? SkinSheet(SceneObject obj, AssetStore assets, IReadOnlyList<Skin> skins, Animation? animation, float time,
		float scale = 1f, int columns = 0, SKTypeface? labels = null, SKColor? background = null)
	{
		if (skins.Count == 0) return null;
		var skeleton = obj.Skeleton;
		var shown = skeleton.ShownSkins.ToList();
		var renderer = new SkeletonRenderer(assets);
		try
		{
			// The box that holds every one of them
			SKRect? all = null;
			foreach (var skin in skins)
			{
				Show(skeleton, shown, skin);
				Pose(obj, animation, time);
				if (renderer.ImageBounds(skeleton, 4f) is { } box)
					all = all is { } a ? SKRect.Union(a, box) : box;
			}
			if (all is not { } cellBox) return null;

			int cols = columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(skins.Count));
			int rows = (skins.Count + cols - 1) / cols;
			int cellW = Math.Max(1, (int)Math.Ceiling(cellBox.Width * scale));
			int cellH = Math.Max(1, (int)Math.Ceiling(cellBox.Height * scale));
			float textSize = Math.Clamp(cellW / 10f, 11f, 22f);
			int labelH = labels != null ? (int)(textSize * 1.6f) : 0;
			var sheet = new SKBitmap(new SKImageInfo(cols * cellW, rows * (cellH + labelH), SKColorType.Rgba8888, SKAlphaType.Premul));
			using var canvas = new SKCanvas(sheet);
			canvas.Clear(background ?? new SKColor(0xF4, 0xF4, 0xF4));
			using var font = labels != null ? new SKFont(labels, textSize) : null;
			using var ink = new SKPaint { Color = new SKColor(0x30, 0x30, 0x30), IsAntialias = true };
			using var line = new SKPaint { Color = new SKColor(0xD0, 0xD0, 0xD0), Style = SKPaintStyle.Stroke };
			for (int i = 0; i < skins.Count; i++)
			{
				int x = i % cols * cellW, y = i / cols * (cellH + labelH);
				canvas.DrawRect(x, y, cellW - 1, cellH + labelH - 1, line);
				Show(skeleton, shown, skins[i]);
				Pose(obj, animation, time);
				canvas.Save();
				canvas.Translate(x, y);
				canvas.ClipRect(new SKRect(0, 0, cellW, cellH));
				Draw(canvas, renderer, obj, cellBox, scale);
				canvas.Restore();
				if (font != null)
					canvas.DrawText(Fit(skins[i].Name, font, cellW - 6), x + cellW / 2f, y + cellH + textSize * 1.15f, SKTextAlign.Center, font, ink);
			}
			return sheet;
		}
		finally
		{
			skeleton.ShownSkins.Clear();
			foreach (var s in shown) skeleton.ShownSkins.Add(s);
		}
	}

	// The project's picture in a save (a square, the scene's shown objects fitted in it). The
	// renderer's settings (pixel art, colour layers) apply; parts hidden while editing still show.
	public static SKBitmap Thumbnail(Scene scene, SkeletonRenderer renderer, int size)
	{
		var bmp = new SKBitmap(size, size);
		using var canvas = new SKCanvas(bmp);
		canvas.Clear(new SKColor(40, 44, 52));

		var shown = scene.Objects.Where(o => o.Visible).ToList();
		var points = new List<Vector2>();
		foreach (var obj in shown)
		{
			var box = ObjectGizmo.LocalBounds(obj, renderer);   // its parts and bones
			var toScene = scene.WorldTransform(obj);
			foreach (var corner in new[] { new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top),
										   new Vector2(box.Right, box.Bottom), new Vector2(box.Left, box.Bottom) })
				points.Add(Vector2.Transform(corner, toScene));
		}
		if (points.Count == 0) return bmp;

		var min = points.Aggregate(Vector2.Min);
		var max = points.Aggregate(Vector2.Max);
		var center = (min + max) / 2;
		float extent = MathF.Max(max.X - min.X, max.Y - min.Y) + 40;   // a little margin
		float scale = size / extent;

		canvas.Translate(size / 2f, size / 2f);
		canvas.Scale(scale, -scale);   // y-up, like the viewport
		canvas.Translate(-center.X, -center.Y);
		bool hiding = renderer.HideHidden;
		renderer.HideHidden = false;
		foreach (var obj in shown)
		{
			var m = SkeletonRenderer.ToSk(scene.WorldTransform(obj));
			canvas.Save();
			canvas.Concat(in m);
			renderer.DrawImages(canvas, obj.Skeleton);
			canvas.Restore();
		}
		renderer.HideHidden = hiding;
		return bmp;
	}

	private static void Show(Skeleton skeleton, List<Skin> shown, Skin skin)
	{
		skeleton.ShownSkins.Clear();
		foreach (var s in shown) skeleton.ShownSkins.Add(s);
		SkinOps.Show(skeleton, skin);
	}

	private static void Pose(SceneObject obj, Animation? animation, float time)
	{
		if (animation != null) ScenePlayer.Pose(obj, animation, time);
		else
		{
			obj.Skeleton.SetToSetupPose();
			obj.Skeleton.UpdatePose();
		}
	}

	private static SKBitmap NewPicture(SKRect box, float scale) =>
		new(new SKImageInfo(Math.Max(1, (int)Math.Ceiling(box.Width * scale)), Math.Max(1, (int)Math.Ceiling(box.Height * scale)),
			SKColorType.Rgba8888, SKAlphaType.Premul));

	// The scene's y points up, pictures' down
	private static void Draw(SKCanvas canvas, SkeletonRenderer renderer, SceneObject obj, SKRect box, float scale)
	{
		canvas.Save();
		canvas.Scale(scale, -scale);
		canvas.Translate(-box.Left, -box.Bottom);
		renderer.DrawImages(canvas, obj.Skeleton);
		canvas.Restore();
	}

	private static string Fit(string text, SKFont font, float width)
	{
		if (font.MeasureText(text) <= width) return text;
		while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
		return text + "…";
	}
}
