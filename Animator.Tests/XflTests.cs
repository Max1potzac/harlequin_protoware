using System.IO;
using System.IO.Compression;
using System.Text;
using Animator.Core.Import;
using Animator.Viewer.Import;
using Animator.Viewer.Rendering;
using SkiaSharp;
using static Animator.Tests.XflTestProject;

namespace Animator.Tests;

// Milestone 12: Adobe Animate (XFL) projects. Reading them, playing their timelines the way
// Animate does, drawing their pictures and shapes, and bringing them in as objects whose
// animations put every part where Animate puts it, on every frame, with a rig worked out from
// how the parts move.
public class XflTests
{
	private static void Near(Vector2 expected, Vector2 actual, float within = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

	private static string TempFolder() =>
		Path.Combine(Path.GetTempPath(), "harlequin-xfl-" + Guid.NewGuid().ToString("N"));

	private static void Remove(string folder)
	{
		try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	// ======================= a small Animate project =======================

	// A ball tweening across the stage (eased, turning, growing and fading), a dot going round in
	// an animated graphic symbol, a drawn square, a hidden layer and a guide. Labels "bounce" and
	// "roll" split its 20 frames.
	private static XflTestProject Bouncing()
	{
		var project = new XflTestProject { Width = 400, Height = 300, FrameRate = 30 };
		project.Picture("media/ball", 20, 20, SKColors.Red, round: true);
		project.Symbol("ball", Layer("1", Frame(0, 1, Bitmap("media/ball", Matrix3x2.CreateTranslation(-10, -10)))));
		project.Symbol("spinner", Layer("dot", Enumerable.Range(0, 4)
			.Select(k => Frame(k, 1, Bitmap("media/ball", Matrix3x2.CreateTranslation(-10 + 10 * k, -10)))).ToArray()));
		project.Scene("Scene 1",
			Layer("labels",
				Frame(0, 10, "", "name=\"bounce\" labelType=\"name\""),
				Frame(10, 10, "", "name=\"roll\" labelType=\"name\"")) +
			Layer("ball",
				Frame(0, 10, Instance("ball", Matrix3x2.CreateTranslation(100, 100), Vector2.Zero), "tweenType=\"motion\" acceleration=\"100\""),
				Frame(10, 10, Instance("ball", Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(300, 120),
					Vector2.Zero, alpha: 0.5f))) +
			Layer("spin", Frame(0, 20, Instance("spinner", Matrix3x2.CreateTranslation(200, 250)))) +
			LayerWith("layerType=\"guide\"", "guide", Frame(0, 20, Instance("ball", Matrix3x2.CreateTranslation(10, 10)))) +
			LayerWith("visible=\"false\"", "hidden", Frame(0, 20, Instance("ball", Matrix3x2.CreateTranslation(50, 50)))) +
			Layer("square", Frame(0, 20, Shape("!400 400|1200 400|1200 1200|400 1200|400 400"))));
		return project;
	}

	[Fact]
	public void A_project_is_read_alike_from_its_folder_and_as_a_fla()
	{
		string folder = TempFolder();
		try
		{
			var project = Bouncing();
			var fromFolder = XflReader.Read(project.SaveFolder(Path.Combine(folder, "bouncing")));
			var fromFla = XflReader.Read(project.SaveFla(Path.Combine(folder, "bouncing.fla")));
			foreach (var doc in new[] { fromFolder, fromFla })
			{
				Assert.Equal(400, doc.Width);
				Assert.Equal(300, doc.Height);
				Assert.Equal(30, doc.FrameRate);
				Assert.NotNull(doc.BitmapBytes(doc.Bitmaps["media/ball"]));
				Assert.Equal(XflSymbolKind.Graphic, doc.Symbol("spinner")!.Kind);
				Assert.Equal(4, doc.Symbol("spinner")!.Timeline.Length);

				var scene = Assert.Single(doc.Scenes);
				Assert.Equal(new[] { "labels", "ball", "spin", "guide", "hidden", "square" }, scene.Layers.Select(l => l.Name));
				Assert.Equal(XflLayerKind.Guide, scene.Layers[3].Kind);
				Assert.False(scene.Layers[4].Visible);
				Assert.Equal(new string?[] { "bounce", "roll" }, scene.Layers[0].Frames.Select(f => f.Label));
				var tween = scene.Layers[1].Frames[0];
				Assert.Equal(XflTween.Classic, tween.Tween);
				Assert.Equal(100, tween.Acceleration);
				var end = Assert.IsType<XflInstance>(scene.Layers[1].Frames[1].Elements[0]);
				Assert.Equal(0.5f, end.Color.Multiply.W);
				Assert.Equal(20, scene.Length);
			}
		}
		finally
		{
			Remove(folder);
		}
	}

	// ======================= playing timelines =======================

	[Fact]
	public void A_classic_tween_turns_the_short_way_around_its_transformation_point()
	{
		var from = new XflInstance { Matrix = Matrix3x2.CreateTranslation(100, 50), Pivot = new Vector2(10, 0) };
		var to = new XflInstance { Matrix = Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(200, 50), Pivot = new Vector2(10, 0) };
		var start = Vector2.Transform(from.Pivot, from.Matrix);   // (110, 50)
		var finish = Vector2.Transform(to.Pivot, to.Matrix);      // (200, 60)

		// Halfway: turned 45°, the transformation point halfway along a straight line
		var (m, _) = XflMath.Classic(new XflFrame { Duration = 10, Tween = XflTween.Classic }, from, to, 0.5f);
		Near(new Vector2(MathF.Cos(MathF.PI / 4), MathF.Sin(MathF.PI / 4)), new Vector2(m.M11, m.M12));
		Near((start + finish) / 2, Vector2.Transform(from.Pivot, m));

		// Eased (as the file has it, 100: a slow start): a quarter of the way at half time
		(m, _) = XflMath.Classic(new XflFrame { Duration = 10, Tween = XflTween.Classic, Acceleration = 100 }, from, to, 0.5f);
		Near(new Vector2(MathF.Cos(MathF.PI / 8), MathF.Sin(MathF.PI / 8)), new Vector2(m.M11, m.M12));
		Near(Vector2.Lerp(start, finish, 0.25f), Vector2.Transform(from.Pivot, m));

		// Clockwise once more than it needs: 90° + 360° in all, so 225° at half time
		(m, _) = XflMath.Classic(new XflFrame { Duration = 10, Tween = XflTween.Classic, Rotate = XflRotate.Clockwise, RotateTimes = 1 }, from, to, 0.5f);
		Near(new Vector2(MathF.Cos(1.25f * MathF.PI), MathF.Sin(1.25f * MathF.PI)), new Vector2(m.M11, m.M12));

		// Anticlockwise: the long way round, -270°, so -67.5° a quarter of the way in
		(m, _) = XflMath.Classic(new XflFrame { Duration = 10, Tween = XflTween.Classic, Rotate = XflRotate.CounterClockwise }, from, to, 0.25f);
		Near(new Vector2(MathF.Cos(-0.375f * MathF.PI), MathF.Sin(-0.375f * MathF.PI)), new Vector2(m.M11, m.M12));
	}

	[Fact]
	public void A_timeline_plays_its_tweens_and_opens_up_its_animated_symbols()
	{
		string folder = TempFolder();
		try
		{
			var doc = XflReader.Read(Bouncing().SaveFolder(Path.Combine(folder, "bouncing")));
			var data = XflAnimationData.Evaluate(doc, doc.Scenes[0], XflEvaluator.SceneKey("Scene 1"));

			// Back to front: the square, the hidden ball, the spinner's dot, the ball (no guide)
			Assert.Equal(new[] { "square", "hidden", "dot", "ball" }, data.Tracks.Select(t => t.LayerName));
			Assert.True(data.Tracks[1].Hidden);
			Assert.Equal(new[] { ("bounce", 0), ("roll", 10) }, data.Labels);

			// The ball, eased: at frame 5 a quarter of the way, grown 1/8, turned 22.5°, a little faded
			var ball = data.Tracks[3].Frames[5]!.Value;
			Assert.Equal(XflDrawingKind.Symbol, ball.Drawing.Kind);
			float s = 1.125f, angle = MathF.PI / 8;
			Near(new Vector2(s * MathF.Cos(angle), s * MathF.Sin(angle)), new Vector2(ball.Matrix.M11, ball.Matrix.M12));
			Near(new Vector2(150, 105), ball.Matrix.Translation);
			Assert.Equal(0.875f, ball.Color.Multiply.W, 3);
			Near(new Vector2(300, 120), data.Tracks[3].Frames[15]!.Value.Matrix.Translation);   // held after the tween

			// The dot goes round the spinner's four frames, over and over
			for (int f = 0; f < 20; f++)
				Near(new Vector2(190 + 10 * (f % 4), 240), data.Tracks[2].Frames[f]!.Value.Matrix.Translation);
		}
		finally
		{
			Remove(folder);
		}
	}

	[Fact]
	public void Graphic_symbols_play_by_their_loop_setting_and_movie_clips_on_their_own()
	{
		// A four-frame symbol whose dot sits 10 pixels further right on each frame
		var project = new XflTestProject();
		project.Picture("media/dot", 4, 4, SKColors.Black);
		string counting = Layer("dot", Enumerable.Range(0, 4).Select(k => Frame(k, 1, Bitmap("media/dot", Matrix3x2.CreateTranslation(10 * k, 0)))).ToArray());
		project.Symbol("counter", counting);
		project.Symbol("clip", counting, type: "movie clip");
		project.Scene("Scene 1",
			Layer("loop", Frame(0, 6, Instance("counter", Matrix3x2.Identity, attributes: "firstFrame=\"1\""))) +
			Layer("once", Frame(0, 6, Instance("counter", Matrix3x2.Identity, loop: "play once", attributes: "firstFrame=\"2\""))) +
			Layer("single", Frame(0, 6, Instance("counter", Matrix3x2.Identity, loop: "single frame", attributes: "firstFrame=\"3\""))) +
			Layer("clip", Frame(0, 6, Instance("clip", Matrix3x2.Identity, type: "movie clip"))));
		string folder = TempFolder();
		try
		{
			var doc = XflReader.Read(project.SaveFolder(Path.Combine(folder, "counting")));
			var data = XflAnimationData.Evaluate(doc, doc.Scenes[0], XflEvaluator.SceneKey("Scene 1"));
			int[] Shown(int layer) =>
				Enumerable.Range(0, 6).Select(f => (int)MathF.Round(data.Tracks.Single(t => t.Id == $"/{layer}#0/0#0").Frames[f]!.Value.Matrix.M31 / 10)).ToArray();
			Assert.Equal(new[] { 1, 2, 3, 0, 1, 2 }, Shown(0));   // loop, from frame 1
			Assert.Equal(new[] { 2, 3, 3, 3, 3, 3 }, Shown(1));   // play once, from frame 2
			Assert.Equal(new[] { 3, 3, 3, 3, 3, 3 }, Shown(2));   // single frame 3
			Assert.Equal(new[] { 0, 1, 2, 3, 0, 1 }, Shown(3));   // a movie clip plays on its own
		}
		finally
		{
			Remove(folder);
		}
	}

	// ======================= drawings =======================

	[Fact]
	public void A_shapes_edges_become_closed_fills_with_their_holes()
	{
		// A 100-pixel square (written partly in hex fixed point, with a selection hint) and a
		// 40-pixel hole: the square has its fill on its right, the hole on its left
		var shape = new XflShape();
		shape.Fills[1] = new XflSolid();
		shape.Edges.Add(new XflEdge(0, 1, 0, "!0 0|#7D0 0|#7D0 #7D0S2|0 #7D0.00|0 0"));
		shape.Edges.Add(new XflEdge(1, 0, 0, "!600 600|1400 600|1400 1400|600 1400|600 600"));
		var (fills, strokes) = XflShapeGeometry.Build(shape);
		Assert.Empty(strokes);
		var (style, loops) = Assert.Single(fills);
		Assert.Equal(1, style);
		Assert.Equal(2, loops.Count);
		Assert.All(loops, loop => Assert.True(loop.Closed));
		var points = loops.Select(l => l.Curves.Select(c => c.End).ToList()).ToList();
		Assert.Contains(points, p => p.Contains(new Vector2(100, 100)));
		Assert.Contains(points, p => p.Contains(new Vector2(70, 70)));

		// Negative hex numbers, decimals and quadratic curves
		var piece = Assert.Single(XflShapeGeometry.Parse("!#FFFFEC 10.5[20 -40 #28.80 0"));
		Near(new Vector2(-1, 0.525f), piece.Start);
		var curve = Assert.Single(piece.Curves);
		Assert.True(curve.Quad);
		Near(new Vector2(1, -2), curve.Control);
		Near(new Vector2(2.025f, 0), curve.End);
	}

	[Fact]
	public void Animates_own_pictures_in_bin_are_read()
	{
		// Three pixels, alpha first: opaque red, nothing, and blue half see-through (Animate
		// premultiplies by alpha + 1), zlib-compressed and cut into chunks as Animate writes them
		byte[] argb = { 255, 255, 0, 0, 0, 0, 0, 0, 128, 0, 0, 100 };
		var packed = new MemoryStream();
		using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
			zlib.Write(argb, 0, argb.Length);
		byte[] compressed = packed.ToArray();

		var dat = new List<byte> { 3, 5 };
		void Le16(int v) => dat.AddRange(new[] { (byte)v, (byte)(v >> 8) });
		void Le32(int v) => dat.AddRange(BitConverter.GetBytes(v));
		Le16(3 * 4);   // bytes a row
		Le16(3);
		Le16(1);
		Le32(0);
		Le32(3 * 20);
		Le32(0);
		Le32(20);
		dat.Add(1);    // it has alpha
		dat.Add(1);    // compressed
		for (int at = 0; at < compressed.Length; at += 5)
		{
			int n = Math.Min(5, compressed.Length - at);
			Le16(n);
			dat.AddRange(compressed.Skip(at).Take(n));
		}
		Le16(0);

		using var picture = XflPainter.Decode(dat.ToArray())!;
		Assert.Equal(3, picture.Width);
		Assert.Equal(1, picture.Height);
		Assert.Equal(new SKColor(255, 0, 0, 255), picture.GetPixel(0, 0));
		Assert.Equal(0, picture.GetPixel(1, 0).Alpha);
		var blue = picture.GetPixel(2, 0);
		Assert.Equal(127, blue.Alpha);
		Assert.InRange(blue.Blue, 199, 203);   // 100 × 256 / 127
	}

	// ======================= how parts move =======================

	[Fact]
	public void Motion_finds_the_point_two_parts_turn_around()
	{
		const int frames = 20;
		var body = new Matrix3x2?[frames];
		var arm = new Matrix3x2?[frames];
		var badge = new Matrix3x2?[frames];
		var slider = new Matrix3x2?[frames];
		var joint = new Vector2(10, 50);   // in the body's space
		for (int f = 0; f < frames; f++)
		{
			float t = f / (float)(frames - 1);
			var onStage = Matrix3x2.CreateRotation(0.2f * t) * Matrix3x2.CreateTranslation(100 + 20 * t, 50);
			body[f] = onStage;
			arm[f] = Matrix3x2.CreateRotation(-0.6f + 1.2f * t) * Matrix3x2.CreateTranslation(joint) * onStage;   // swings 70° on the joint
			badge[f] = Matrix3x2.CreateTranslation(-5, 20) * onStage;                                          // stuck on the body
			slider[f] = Matrix3x2.CreateTranslation(-15 + 30 * t, 70) * onStage;                               // slides along it
		}
		var bounds = new (Vector2, Vector2)[] { (new(-20, -20), new(20, 100)), (new(-5, -5), new(5, 40)), (new(0, 0), new(10, 10)), (new(0, 0), new(10, 10)) };
		var hints = MotionHints.Fit(new[] { body, arm, badge, slider }, bounds, setup: 0);

		Assert.True(hints.AnythingMoves);
		Assert.True(hints.Quality(0, 1) > 0.9);
		Near(Vector2.Transform(joint, body[0]!.Value), hints.Joint(0, 1)!.Value, 0.05f);
		Assert.True(hints.Rigid(0, 2));
		Assert.Null(hints.Quality(0, 2));
		Assert.True(hints.MovesAgainst(0, 3));
		Assert.True(hints.Quality(0, 3) < 0.2);
	}

	// ======================= bringing projects in =======================

	// Plays every animation the import made and checks each part on every frame: it shows the
	// drawing the project shows there (or nothing), placed where Animate places it (turned,
	// scaled and mirrored the same) within `within` pixels, at the same opacity. Skew is brought
	// in turned and scaled the nearest way: where Animate skews a part a little, its pixels may
	// be off by that much more (the skew's angle times how far they are from its transformation
	// point); where it skews it a lot, only that point is checked. Returns how many frames of
	// parts were skewed a lot, and how far off a pixel was at worst.
	internal static (int Skewed, float Worst) MatchesEveryFrame(XflImportPlan plan, SceneObject obj,
		Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)> pictures, float within = 0.1f)
	{
		var skeleton = obj.Skeleton;
		var animations = plan.Animations();
		Assert.Equal(animations.Count, obj.Animations.Count);
		float fps = plan.Data.FrameRate;
		int skewed = 0;
		float worst = 0;
		for (int a = 0; a < animations.Count; a++)
		{
			var (_, start, end) = animations[a];
			var animation = obj.Animations[a];
			for (int f = start; f < end; f++)
			{
				skeleton.SetToSetupPose();
				animation.Apply(skeleton, (f - start) / fps);
				skeleton.UpdatePose();
				for (int i = 0; i < plan.Parts.Count; i++)
				{
					var part = plan.Parts[i];
					if (!part.Include) continue;
					var slot = skeleton.FindSlot(part.Name);
					var drawing = slot == null ? null : skeleton.ResolveAttachment(slot);
					if (plan.Placement(i, f) is not { } p || plan.Painter.Bounds(p.Drawing).IsEmpty)
					{
						Assert.True(drawing == null, $"{part.Name} shows at frame {f}, where the project shows nothing");
						continue;
					}
					Assert.True(slot != null && drawing != null, $"{part.Name} is missing at frame {f}");
					var (key, pixelToDrawing) = pictures[drawing!];
					Assert.Equal(p.Drawing, key);

					var asset = skeleton.Assets[drawing!.AssetId];
					var actual = SkeletonRenderer.ImageToWorld(slot!, drawing, asset.Width, asset.Height);
					var expected = pixelToDrawing * p.Matrix * plan.TimelineToObject;
					var (_, _, ax, ay) = XflMath.Decompose(p.Matrix);
					float skew = MathF.Abs(XflMath.Mod(ay - ax + MathF.PI, 2 * MathF.PI) - MathF.PI);
					bool muchSkewed = skew > 2f * MathF.PI / 180;
					if (muchSkewed) skewed++;
					var pivot = new Vector2(drawing.Pivot.X * asset.Width, drawing.Pivot.Y * asset.Height);
					var points = muchSkewed
						? new[] { pivot }
						: new[] { Vector2.Zero, new Vector2(asset.Width, 0), new Vector2(0, asset.Height), new Vector2(asset.Width, asset.Height) };
					foreach (var point in points)
					{
						float off = Vector2.Distance(Vector2.Transform(point, expected), Vector2.Transform(point, actual));
						float slant = skew * Vector2.Distance(Vector2.Transform(point, expected), Vector2.Transform(pivot, expected));
						worst = MathF.Max(worst, off - slant);
						Assert.True(off <= within + slant, $"{part.Name} is {off:0.###} pixels off at frame {f} ({animation.Name}, skewed {skew * 180 / MathF.PI:0.##}°)");
					}
					Assert.True(MathF.Abs(slot!.Opacity - Math.Clamp(p.Color.Multiply.W, 0, 1)) < 0.01f,
						$"{part.Name} at frame {f}: opacity {slot.Opacity}, Animate's {p.Color.Multiply.W}");
				}
			}
		}
		skeleton.SetToSetupPose();
		skeleton.UpdatePose();
		return (skewed, worst);
	}

	[Fact]
	public void A_project_comes_in_as_a_drawn_object_moving_as_in_Animate()
	{
		string folder = TempFolder();
		try
		{
			using var plan = XflImportPlan.Load(Bouncing().SaveFla(Path.Combine(Directory.CreateDirectory(folder).FullName, "bouncing.fla")), "bouncing");
			Assert.Equal(new Vector2(200, 150), plan.Origin);   // the stage's middle
			Assert.Equal(new[] { "square", "hidden", "dot", "ball" }, plan.Parts.Select(p => p.Name));
			Assert.False(plan.Parts[1].Include);                // hidden in Animate: left out at first
			Assert.Equal(new[] { ("bounce", 0, 10), ("roll", 10, 20) }, plan.Animations());

			var pictures = new Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>();
			var obj = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
			Assert.Equal(ObjectKind.Drawn, obj.Kind);
			Assert.Equal(new[] { "bounce", "roll" }, obj.Animations.Select(a => a.Name));
			Assert.Equal(new[] { "square", "dot", "ball" }, obj.Skeleton.Slots.Select(s => s.Name));   // back to front
			Assert.Equal(0, MatchesEveryFrame(plan, obj, pictures).Skewed);
		}
		finally
		{
			Remove(folder);
		}
	}

	// ----- a character made the way PvZ2 animations are -----

	private sealed record Piece(string Name, float X0, float Y0, float X1, float Y1, float JointX, float JointY, string? Parent, bool Round = false)
	{
		public Vector2 Joint => new(JointX, JointY);
	}

	// Its parts back to front, where they are at rest (pixels, y down, its feet at 0), each with
	// the joint it turns on and what it hangs from. The eye, the hair and the cone (a skin: it'll
	// be swapped for others) go with the head.
	private static readonly Piece[] Zombie =
	{
		new("arm_upper_back", 19, -182, 37, -124, 28, -175, "body"),
		new("arm_lower_back", 20, -136, 36, -84, 28, -130, "arm_upper_back"),
		new("leg_upper_back", 1, -96, 23, -38, 12, -90, "body"),
		new("leg_lower_back", 2, -50, 22, 6, 12, -45, "leg_upper_back"),
		new("body", -30, -200, 30, -80, 0, -90, null),
		new("head", -40, -265, 40, -193, 0, -195, "body", Round: true),
		new("hair", -46, -250, -32, -214, -39, -232, "head"),
		new("eye", 10, -240, 22, -228, 16, -234, "head", Round: true),
		new("cone", -20, -300, 20, -255, 0, -260, "head"),
		new("leg_upper_front", -23, -96, -1, -38, -12, -90, "body"),
		new("leg_lower_front", -22, -50, -2, 6, -12, -45, "leg_upper_front"),
		new("arm_upper_front", -37, -182, -19, -124, -28, -175, "body"),
		new("arm_lower_front", -36, -136, -20, -84, -28, -130, "arm_upper_front")
	};

	// How much a part is turned on its parent at a frame: idling (frames 0-29) then walking
	private static float TurnOf(string piece, int frame)
	{
		double phase = 2 * Math.PI * (frame % 30) / 30, swing = frame >= 30 ? 1 : 0.35;
		double degrees = piece switch
		{
			"body" => 2 * Math.Sin(phase),
			"head" => 8 * Math.Sin(phase + 0.5),
			"arm_upper_front" => 30 * swing * Math.Sin(phase),
			"arm_lower_front" => 20 + 15 * swing * Math.Sin(phase + 1),
			"arm_upper_back" => -30 * swing * Math.Sin(phase),
			"arm_lower_back" => 20 - 15 * swing * Math.Sin(phase + 1),
			"leg_upper_front" => 25 * swing * Math.Sin(phase + 2),
			"leg_lower_front" => 15 + 15 * swing * Math.Sin(phase + 3),
			"leg_upper_back" => -25 * swing * Math.Sin(phase + 2),
			"leg_lower_back" => 15 - 15 * swing * Math.Sin(phase + 3),
			_ => 0   // fixed on the head
		};
		return (float)(degrees * Math.PI / 180);
	}

	// Each part's own space (its origin on its joint) → the character's, at a frame
	private static Matrix3x2 PoseOf(Piece piece, int frame)
	{
		var parent = piece.Parent == null ? null : Zombie.First(p => p.Name == piece.Parent);
		var bob = new Vector2(0, 3 * MathF.Sin(2 * MathF.PI * (frame % 30) / 30));
		var local = Matrix3x2.CreateRotation(TurnOf(piece.Name, frame)) *
					Matrix3x2.CreateTranslation(parent == null ? piece.Joint + bob : piece.Joint - parent.Joint);
		return parent == null ? local : local * PoseOf(parent, frame);
	}

	// The layout PAM converters write: each part's picture in LIBRARY/media (at twice the size,
	// shown at half: a 2400 resolution), a "source" symbol showing it and an "image" symbol placing
	// it on its joint, a "main" symbol with a numbered layer per part and a keyframe on every frame,
	// and a scene with the labels on a "flow" layer and "main" placed at the stage's corner
	private static XflTestProject ZombieProject()
	{
		var project = new XflTestProject { Width = 390, Height = 390, FrameRate = 30 };
		for (int i = 0; i < Zombie.Length; i++)
		{
			var p = Zombie[i];
			project.Picture($"media/{p.Name}", (int)(2 * (p.X1 - p.X0)), (int)(2 * (p.Y1 - p.Y0)),
				new SKColor((byte)(40 + 15 * i), 150, (byte)(230 - 12 * i)), p.Round);
			project.Symbol($"source/{p.Name}", Layer("1", Frame(0, 1, Bitmap($"media/{p.Name}", Matrix3x2.CreateScale(0.5f)))));
			project.Symbol($"image/{p.Name}", Layer("1", Frame(0, 1,
				Instance($"source/{p.Name}", Matrix3x2.CreateTranslation(p.X0 - p.JointX, p.Y0 - p.JointY)))));
		}
		var layers = new StringBuilder();
		for (int i = Zombie.Length - 1; i >= 0; i--)   // the front one first, as Animate lists them
		{
			var p = Zombie[i];
			layers.Append(Layer($"{i}", Enumerable.Range(0, 60).Select(f => Frame(f, 1, Instance($"image/{p.Name}", PoseOf(p, f)))).ToArray()));
		}
		project.Symbol("main", layers.ToString());
		project.Scene("animation",
			Layer("flow", Frame(0, 30, "", "name=\"idle\" labelType=\"name\""), Frame(30, 30, "", "name=\"walk\" labelType=\"name\"")) +
			Layer("command", Frame(0, 60)) +
			Layer("sprite", Frame(0, 60, Instance("main", Matrix3x2.Identity))));
		return project;
	}

	[Fact]
	public void A_PvZ2_style_character_gets_its_rig_from_how_it_moves()
	{
		string folder = TempFolder();
		try
		{
			using var plan = XflImportPlan.Load(ZombieProject().SaveFolder(Path.Combine(folder, "zombie")), "zombie");
			Assert.Equal(Vector2.Zero, plan.Origin);   // it stands on the stage's corner: its feet stay at the object's origin
			Assert.Equal(Zombie.Select(p => p.Name), plan.Parts.Select(p => p.Name));
			Assert.Equal(new[] { ("idle", 0, 30), ("walk", 30, 60) }, plan.Animations());

			// The cone is a skin: left out of working out the rig
			plan.Parts.Single(p => p.Name == "cone").InRig = false;
			plan.SuggestRig = true;
			plan.SuggestRoles();
			var rig = plan.Suggest();

			// A bone for each part that turns on its own, hanging from the part it turns on
			string NameOf(int bone) => plan.Parts[rig.Bones[bone].Layer].Name;
			var parents = Enumerable.Range(0, rig.Bones.Count).ToDictionary(NameOf, b => rig.Bones[b].Parent < 0 ? "-" : NameOf(rig.Bones[b].Parent));
			var expected = Zombie.Where(p => p.Name is not ("eye" or "hair" or "cone")).ToDictionary(p => p.Name, p => p.Parent ?? "-");
			Assert.Equal(expected.OrderBy(e => e.Key), parents.OrderBy(e => e.Key));

			// ...on the joints it turns on (where they are at the rest pose, frame 1)
			Assert.Equal(0, plan.SetupFrame);
			for (int b = 0; b < rig.Bones.Count; b++)
			{
				if (rig.Bones[b].Parent < 0) continue;
				var piece = Zombie.First(p => p.Name == NameOf(b));
				var joint = Vector2.Transform(Vector2.Transform(Vector2.Zero, PoseOf(piece, 0)), plan.TimelineToObject);
				Assert.True(Vector2.Distance(joint, plan.CanvasToObject(rig.Bones[b].Head)) < 1f,
					$"{piece.Name}'s joint is at {plan.CanvasToObject(rig.Bones[b].Head)}, not {joint}");
			}
			var head = rig.Bones.FindIndex(b => plan.Parts[b.Layer].Name == "head");
			foreach (string rider in new[] { "eye", "hair" })
				Assert.Equal(head, rig.BoneOf[plan.Parts.FindIndex(p => p.Name == rider)]);   // they ride on the head's bone

			// Brought in: the bones carry the motion, every part lands where Animate puts it
			var pictures = new Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>();
			var obj = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
			Assert.Equal(ObjectKind.Rigged, obj.Kind);
			var skeleton = obj.Skeleton;
			Assert.Equal(0, MatchesEveryFrame(plan, obj, pictures).Skewed);
			foreach (string rider in new[] { "eye", "hair", "cone" })
				Assert.Equal("head", skeleton.FindSlot(rider)!.Bone.Name);
			Assert.Equal("arm_lower_front", skeleton.FindSlot("arm_lower_front")!.Bone.Name);
			foreach (var animation in obj.Animations)
			{
				Assert.NotNull(animation.Find<RotateTimeline>("arm_lower_front"));
				foreach (var slot in skeleton.Slots)   // fixed on their bones: no keys of their own
				{
					Assert.Null(animation.Find<PartRotateTimeline>(slot.Name));
					Assert.Null(animation.Find<PartTranslateTimeline>(slot.Name));
				}
			}

			// And as a drawn object, each part moving on its own
			plan.SuggestRig = false;
			pictures.Clear();
			var drawn = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
			Assert.Equal(ObjectKind.Drawn, drawn.Kind);
			Assert.Equal(0, MatchesEveryFrame(plan, drawn, pictures).Skewed);
		}
		finally
		{
			Remove(folder);
		}
	}

	// ======================= your own projects =======================

	// Put Animate projects in Animator.Tests/PrivateFiles (a .fla, or an XFL folder; git leaves
	// them out) and run the tests: each scene comes in drawn and rigged, every part on every frame
	// where Animate puts it. Where a project has Animate's own published values beside it
	// (expected.csv: the first instance's matrix and colour on each frame), it's checked
	// against those too. Every project is tried; what went wrong with any is listed at the end
	// (and in xfl-report.txt beside the tests). (Nothing there: nothing to check.)
	[Fact]
	public void Animate_projects_of_your_own_come_in_frame_for_frame()
	{
		string folder = Path.Combine(AppContext.BaseDirectory, "PrivateFiles");
		if (!Directory.Exists(folder)) return;
		var projects = Directory.GetFiles(folder, "DOMDocument.xml", SearchOption.AllDirectories)
			.Concat(Directory.GetFiles(folder, "*.fla", SearchOption.AllDirectories))
			.OrderBy(p => p, StringComparer.Ordinal).ToList();
		var report = new List<string>();
		var failed = new List<string>();
		foreach (string path in projects)
		{
			string name = Path.GetFileName(path) == "DOMDocument.xml"
				? Path.GetRelativePath(folder, Path.GetDirectoryName(path)!)
				: Path.GetRelativePath(folder, path);
			void Try(string what, Action check)
			{
				try { check(); }
				catch (Exception e)
				{
					string line = $"{name} {what}: FAILED: {e.GetType().Name}: {e.Message.Split('\n')[0]}";
					failed.Add(line);
					report.Add(line);
				}
			}
			XflImportPlan? plan = null;
			Try("(reading)", () => plan = XflImportPlan.Load(path, Path.GetFileNameWithoutExtension(name)));
			if (plan == null) continue;
			using (plan)
			{
				foreach (var scene in plan.Timelines.Where(t => t.Scene).ToList())
				{
					Try($"/ {scene.Label}", () =>
					{
						plan.Choose(scene);
						var pictures = new Dictionary<Attachment, (XflDrawingKey Key, Matrix3x2 PixelToDrawing)>();
						var drawn = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
						var (skewed, worstDrawn) = MatchesEveryFrame(plan, drawn, pictures, within: 0.15f);

						plan.SuggestRig = true;
						try
						{
							plan.SuggestRoles();
							pictures.Clear();
							var rigged = XflImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, pictures);
							var (_, worstRigged) = MatchesEveryFrame(plan, rigged, pictures, within: 0.15f);
							var bones = rigged.Skeleton.AllBones().Skip(1).ToList();   // under the object's own root bone
							report.Add($"{name} / {scene.Label}: {plan.Parts.Count} part(s), {plan.Data.FrameCount} frame(s), " +
									   $"{bones.Count} bone(s), off by {worstDrawn:0.###} px drawn and {worstRigged:0.###} px rigged at worst" +
									   (skewed > 0 ? $", {skewed} much skewed part-frame(s)" : "") +
									   (bones.Count is > 1 and <= 24 ? $"; bones: {string.Join(", ", bones.Select(b => b.Parent == rigged.Skeleton.Roots[0] ? b.Name : $"{b.Name} on {b.Parent!.Name}"))}" : "") +
									   (plan.Notes.Count > 0 ? $"; notes: {string.Join(" | ", plan.Notes)}" : ""));
						}
						finally
						{
							plan.SuggestRig = false;
						}
					});
				}

				string animateValues = Path.Combine(Path.GetDirectoryName(path)!, "expected.csv");
				if (Path.GetFileName(path) == "DOMDocument.xml" && File.Exists(animateValues))
					Try("(Animate's values)", () =>
					{
						plan.Choose(plan.Timelines.First(t => t.Scene));
						report.Add($"{name}: Animate's values: {SameAsAnimate(plan, animateValues)}");
					});
			}
		}
		File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "xfl-report.txt"), report);
		Assert.True(failed.Count == 0, $"{failed.Count} of {projects.Count} project(s) went wrong:\n" + string.Join("\n", failed));
	}

	// Compares the first instance on stage with Animate's own values on every frame (matrix in
	// 16.16 fixed point, position in twips, colour multipliers in 8.8, offsets); fails beyond
	// rounding, and says how far off it was at worst
	private static string SameAsAnimate(XflImportPlan plan, string csv)
	{
		var lines = File.ReadAllLines(csv);
		var columns = lines[0].Split(',').Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
		float worstMatrix = 0, worstPosition = 0, worstColour = 0;
		var track = plan.Data.Tracks.First(t => t.Frames.Any(f => f is { Drawing.Kind: not XflDrawingKind.Shapes }));
		foreach (string line in lines.Skip(1).Where(l => l.Trim().Length > 0))
		{
			var cells = line.Split(',');
			float Cell(string column) => float.Parse(cells[columns[column]], System.Globalization.CultureInfo.InvariantCulture);
			int f = (int)Cell("frameIndex");
			if (f >= plan.Data.FrameCount || track.Frames[f] is not { } p) continue;
			var m = p.Matrix;
			worstMatrix = new[] { m.M11 - Cell("aFixed16") / 65536, m.M12 - Cell("bFixed16") / 65536, m.M21 - Cell("cFixed16") / 65536, m.M22 - Cell("dFixed16") / 65536 }
				.Select(MathF.Abs).Append(worstMatrix).Max();
			worstPosition = MathF.Max(worstPosition, Vector2.Distance(m.Translation, new Vector2(Cell("txTwips"), Cell("tyTwips")) / 20));
			var c = p.Color;
			worstColour = new[]
			{
				c.Multiply.X - Cell("rMultFixed8") / 256, c.Multiply.Y - Cell("gMultFixed8") / 256, c.Multiply.Z - Cell("bMultFixed8") / 256, c.Multiply.W - Cell("aMultFixed8") / 256,
				(c.Offset.X - Cell("rOffset")) / 255, (c.Offset.Y - Cell("gOffset")) / 255, (c.Offset.Z - Cell("bOffset")) / 255, (c.Offset.W - Cell("aOffset")) / 255
			}.Select(MathF.Abs).Append(worstColour).Max();
		}
		string said = $"matrix {worstMatrix:0.####}, position {worstPosition:0.###} px, colour {worstColour:0.###}";
		Assert.True(worstMatrix < 0.01f && worstPosition < 0.2f && worstColour < 0.02f, $"{csv}: {said}");
		return said;
	}
}
