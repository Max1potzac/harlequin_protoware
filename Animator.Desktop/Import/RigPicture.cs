using Animator.Core.Import;
using Animator.Viewer.Painting;

namespace Animator.Desktop.Import;

// A suggested rig drawn over an import's picture: each bone a long diamond from its joint (a dot:
// pink when it sits on its parent's end) to its tip; the picked layer's bone stands out, named,
// and a part riding on a bone points at it
public static class RigPicture
{
	public static void Draw(SKCanvas canvas, RigSuggestion rig, Func<Vector2, Vector2> toView, int picked,
		Func<int, string> nameOf, Func<int, Vector2> middleOf, Func<int, (Vector2 Head, Vector2 Tip)>? placeOf = null)
	{
		using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(30, 30, 30, 220), IsAntialias = true };
		using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
		using var font = new SKFont { Size = 12 };
		using var label = new SKPaint { IsAntialias = true, Color = SKColors.White };
		using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 200) };
		int pickedBone = picked >= 0 && picked < rig.BoneOf.Length ? rig.BoneOf[picked] : -1;
		(Vector2 Head, Vector2 Tip) Place(int b) => placeOf?.Invoke(b) ?? (rig.Bones[b].Head, rig.Bones[b].Tip);

		if (pickedBone >= 0 && rig.Bones[pickedBone].Layer != picked)
		{
			// The picked part rides on this bone
			using var link = new SKPaint
			{
				Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 200, 40), IsAntialias = true,
				PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0)
			};
			var (h, t) = Place(pickedBone);
			var from = toView(middleOf(picked));
			var to = toView((h + t) / 2);
			canvas.DrawLine(from.X, from.Y, to.X, to.Y, link);
		}

		for (int b = 0; b < rig.Bones.Count; b++)
		{
			var bone = rig.Bones[b];
			var (h, t) = Place(b);
			Vector2 head = toView(h), tip = toView(t);
			var along = tip - head;
			float length = along.Length();
			bool isPicked = b == pickedBone;
			if (length > 1)
			{
				var dir = along / length;
				var across = new Vector2(-dir.Y, dir.X) * Math.Clamp(length * 0.09f, 3f, 12f);
				var waist = head + dir * length * 0.18f;
				using var path = ShapePaths.Polyline(new[] { head, waist + across, tip, waist - across }, closed: true);
				fill.Color = isPicked ? new SKColor(255, 140, 30, 230) : bone.Parent < 0 ? new SKColor(120, 230, 120, 210) : new SKColor(255, 230, 80, 200);
				canvas.DrawPath(path, fill);
				canvas.DrawPath(path, edge);
			}
			dot.Color = bone.Connected ? new SKColor(255, 90, 200) : new SKColor(60, 200, 255);
			float r = isPicked ? 5.5f : 4f;
			canvas.DrawCircle(head.X, head.Y, r, dot);
			canvas.DrawCircle(head.X, head.Y, r, edge);

			if (isPicked)
			{
				string name = nameOf(bone.Layer);
				var at = (head + tip) / 2 + new Vector2(8, -6);
				canvas.DrawText(name, at.X + 1, at.Y + 1, SKTextAlign.Left, font, shadow);
				canvas.DrawText(name, at.X, at.Y, SKTextAlign.Left, font, label);
			}
		}
	}
}
