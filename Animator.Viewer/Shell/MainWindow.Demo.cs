using System.Numerics;
using Animator.Core;
using Animator.Core.Assets;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;
using SkiaSharp;

namespace Animator.Viewer;

public partial class MainWindow
{
	private void BuildDemoRig()
	{
		float quarter = MathF.PI / 2;

		// Bones point along their local X axis. The torso is rotated 90 degrees
		// so it points UP; its children are positioned in that rotated space.
		var torso = new Bone { Name = "torso", Length = 120, SetupPosition = new(0, -100), SetupRotation = quarter };
		var head = new Bone { Name = "head", Length = 60, Connected = true };   // on the torso's end
		torso.AddChild(head);

		// Shoulder sits near the torso's top, on its right-hand side
		// (local -Y maps to screen right because the torso is rotated).
		var upper = new Bone { Name = "upper", Length = 100, SetupPosition = new(100, -40), SetupRotation = -quarter };
		var fore = new Bone { Name = "fore", Length = 80, Connected = true };   // on the upper arm's end
		upper.AddChild(fore);
		torso.AddChild(upper);

		var target = new Bone { Name = "handTarget", Length = 10, SetupPosition = new(150, 0) };

		_skeleton.Roots.Add(torso);
		_skeleton.Roots.Add(target);

		_ik = new IkConstraint { Name = "arm_ik", Target = target, BendDirection = 1 };
		_ik.Bones.Add(upper);
		_ik.Bones.Add(fore);
		_skeleton.IkConstraints.Add(_ik);

		// --- placeholder art, generated in code ---
		var torsoImg = AddAsset("torso", 80, 120, c => Fill(c, new SKColor(70, 130, 200), 80, 120));
		var headImg = AddAsset("head", 90, 90, DrawHead);
		var mouthA = AddAsset("mouth_a", 30, 20, c => DrawMouth(c, 0));
		var mouthB = AddAsset("mouth_b", 30, 20, c => DrawMouth(c, 1));
		var mouthC = AddAsset("mouth_c", 30, 20, c => DrawMouth(c, 2));
		var upperImg = AddAsset("upper", 100, 24, c => Fill(c, new SKColor(230, 170, 120), 100, 24));
		var foreImg = AddAsset("fore", 80, 20, c => Fill(c, new SKColor(215, 150, 100), 80, 20));

		// Sprites are drawn "pointing up" in the image, but the torso/head bones point
		// along X, which is rotated 90 degrees. So we counter-rotate the attachment by -90.
		// List order = draw order.
		AddSlot("torso", torso, "torso", torsoImg, new(60, 0), -quarter);
		AddSlot("head", head, "head", headImg, new(35, 0), -quarter);
		AddSlot("mouth", head, "mouth_a", mouthA, new(18, 0), -quarter);
		AddSlot("upper", upper, "upper", upperImg, new(50, 0), 0);
		AddSlot("fore", fore, "fore", foreImg, new(40, 0), 0);

		// Extra mouth frames live in the same slot, different attachment names
		_skeleton.DefaultSkin.Set("mouth", new Attachment { Name = "mouth_b", AssetId = mouthB.Id, Offset = new(18, 0), Rotation = -quarter });
		_skeleton.DefaultSkin.Set("mouth", new Attachment { Name = "mouth_c", AssetId = mouthC.Id, Offset = new(18, 0), Rotation = -quarter });
	}

	private void BuildDemoAnimation()
	{
		_anim = new Animation { Name = "idle", Duration = 2f, Loop = true };

		var bob = new RotateTimeline("head");
		bob.Track.Add(0f, 0f, CurveType.Smooth);
		bob.Track.Add(0.5f, 0.15f, CurveType.Smooth);
		bob.Track.Add(1f, 0f, CurveType.Smooth);
		bob.Track.Add(1.5f, -0.15f, CurveType.Smooth);
		bob.Track.Add(2f, 0f);
		_anim.Timelines.Add(bob);

		// The IK target circles around (diamond-ish), and the arm chases it
		var move = new TranslateTimeline("handTarget");
		move.Track.Add(0f, new Vector2(0, 40), CurveType.Smooth);
		move.Track.Add(0.5f, new Vector2(40, 0), CurveType.Smooth);
		move.Track.Add(1f, new Vector2(0, -40), CurveType.Smooth);
		move.Track.Add(1.5f, new Vector2(-40, 0), CurveType.Smooth);
		move.Track.Add(2f, new Vector2(0, 40));
		_anim.Timelines.Add(move);

		// Frame-by-frame: the mouth cycles through three drawings
		var mouth = new AttachmentTimeline("mouth");
		mouth.Add(0f, "mouth_a");
		mouth.Add(0.4f, "mouth_b");
		mouth.Add(0.8f, "mouth_c");
		mouth.Add(1.2f, "mouth_b");
		mouth.Add(1.6f, "mouth_a");
		_anim.Timelines.Add(mouth);

		// FK motion for the arm. Visible only in FK mode; IK overrides it otherwise.
		var swing = new RotateTimeline("upper");
		swing.Track.Add(0f, 0f, CurveType.Smooth);
		swing.Track.Add(1f, -0.6f, CurveType.Smooth);
		swing.Track.Add(2f, 0f);
		_anim.Timelines.Add(swing);

		var elbow = new RotateTimeline("fore");
		elbow.Track.Add(0f, 0f, CurveType.Smooth);
		elbow.Track.Add(1f, 0.9f, CurveType.Smooth);
		elbow.Track.Add(2f, 0f);
		_anim.Timelines.Add(elbow);
	}

	// ---------- helpers ----------

	private Asset AddAsset(string name, int w, int h, Action<SKCanvas> draw)
	{
		var bmp = new SKBitmap(w, h);
		using (var c = new SKCanvas(bmp))
		{
			c.Clear(SKColors.Transparent);
			draw(c);
		}
		var asset = new Asset { Name = name };
		_skeleton.Assets[asset.Id] = asset;
		_assets.Set(asset, bmp);
		return asset;
	}

	private void AddSlot(string slotName, Bone bone, string attName, Asset asset, Vector2 offset, float rotation)
	{
		_skeleton.Slots.Add(new Slot(slotName, bone) { SetupAttachment = attName });
		_skeleton.DefaultSkin.Set(slotName, new Attachment
		{
			Name = attName,
			AssetId = asset.Id,
			Offset = offset,
			Rotation = rotation
		});
	}

	private static void Fill(SKCanvas c, SKColor color, int w, int h)
	{
		using var p = new SKPaint { Color = color, IsAntialias = true };
		c.DrawRoundRect(0, 0, w, h, 8, 8, p);
	}

	private static void DrawHead(SKCanvas c)
	{
		using var skin = new SKPaint { Color = new SKColor(245, 205, 160), IsAntialias = true };
		using var eye = new SKPaint { Color = SKColors.Black, IsAntialias = true };
		c.DrawCircle(45, 45, 44, skin);
		c.DrawCircle(30, 32, 5, eye);
		c.DrawCircle(60, 32, 5, eye);
	}

	private static void DrawMouth(SKCanvas c, int shape)
	{
		using var p = new SKPaint { Color = new SKColor(120, 30, 30), IsAntialias = true, StrokeWidth = 4 };
		if (shape == 0) c.DrawLine(3, 10, 27, 10, p);                 // closed
		else if (shape == 1) c.DrawOval(15, 10, 12, 6, p);            // half open
		else c.DrawOval(15, 10, 10, 9, p);                            // wide open
	}
}