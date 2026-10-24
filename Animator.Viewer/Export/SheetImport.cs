using System.IO;
using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Export;

// How the frames of an animation line up when they aren't all the same size
public enum FrameAnchor { Centre, Bottom, Origin }

// One piece cut from a sprite sheet
public sealed class SheetPiece
{
	public PixelRect Rect { get; set; }
	public string Name { get; set; } = "";
	public bool LeftOut { get; set; }
	public SheetAnimation? Animation { get; set; }

	// From a sheet's JSON: how many frames this one shows for (0 = the animation's setting), and
	// where it sat in its frame before the empty border was trimmed off (null = it wasn't)
	public int Hold { get; set; }
	public PixelRect? Untrimmed { get; set; }
}

// Pieces played one after another
public sealed class SheetAnimation
{
	public string Name { get; set; } = "animation";
	public List<SheetPiece> Frames { get; } = new();
	public int Hold { get; set; } = 1;             // frames each piece shows for (unless the piece says)
	public bool Loop { get; set; } = true;
	public FrameAnchor Anchor { get; set; } = FrameAnchor.Centre;

	public int HoldOf(SheetPiece piece) => Math.Max(1, piece.Hold > 0 ? piece.Hold : Hold);
}

// What the sprite sheet dialog decided: the pieces, which make animations, and what to make
public sealed class SheetImportPlan
{
	public SheetImportPlan(SKBitmap sheet, string name)
	{
		Sheet = sheet;
		Name = name;
	}

	public SKBitmap Sheet { get; }
	public string Name { get; set; }
	public List<SheetPiece> Pieces { get; } = new();
	public List<SheetAnimation> Animations { get; } = new();
	public bool Stills { get; set; } = true;               // pieces in no animation come in as stills (else not at all)
	public bool AnimationsTogether { get; set; } = true;   // one object with every animation (else an object each)
	public Vector2? Origin { get; set; }                   // a JSON's origin, in pixels from each frame's top-left

	public IEnumerable<SheetPiece> StillPieces => Pieces.Where(p => p.Animation == null && !p.LeftOut);
	public IEnumerable<SheetAnimation> LiveAnimations => Animations.Where(a => a.Frames.Any(f => !f.LeftOut));

	// Pieces and animations as a sheet's JSON describes them: each frame is a piece, each frame
	// tag an animation, and each frame's duration how long it shows. Rotated frames are left out
	// (returns how many).
	public int ReadJson(SpriteSheetJson json, int fps)
	{
		Pieces.Clear();
		Animations.Clear();
		Origin = json.Meta.Origin is { } o ? new Vector2(o.X, o.Y) : null;

		int rotated = 0;
		var byIndex = new SheetPiece?[json.Frames.Count];
		for (int i = 0; i < json.Frames.Count; i++)
		{
			var f = json.Frames[i];
			var rect = new PixelRect(f.Frame.X, f.Frame.Y, f.Frame.W, f.Frame.H);
			if (f.Rotated) { rotated++; continue; }
			if (rect.W <= 0 || rect.H <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > Sheet.Width || rect.Bottom > Sheet.Height) continue;

			var piece = new SheetPiece
			{
				Rect = rect,
				Name = PieceName(f.Filename, i),
				Hold = Math.Max(1, (int)MathF.Round(f.Duration * fps / 1000f))
			};
			if (f.Trimmed && f.SourceSize.W > 0 && f.SourceSize.H > 0)
				piece.Untrimmed = new PixelRect(f.SpriteSourceSize.X, f.SpriteSourceSize.Y, f.SourceSize.W, f.SourceSize.H);
			byIndex[i] = piece;
			Pieces.Add(piece);
		}

		foreach (var tag in json.Meta.FrameTags)
		{
			var animation = new SheetAnimation
			{
				Name = string.IsNullOrWhiteSpace(tag.Name) ? "animation" : tag.Name.Trim(),
				Loop = tag.Loop ?? true,
				Anchor = Origin != null ? FrameAnchor.Origin : FrameAnchor.Centre
			};
			for (int i = Math.Max(0, tag.From); i <= tag.To && i < byIndex.Length; i++)
				if (byIndex[i] is { Animation: null } piece)
				{
					piece.Animation = animation;
					animation.Frames.Add(piece);
				}
			if (animation.Frames.Count == 0) continue;

			if (tag.Direction == "reverse") animation.Frames.Reverse();
			else if (tag.Direction == "pingpong")   // there and back, without playing the ends twice
				for (int i = animation.Frames.Count - 2; i >= 1; i--) animation.Frames.Add(animation.Frames[i]);
			Animations.Add(animation);
		}

		// No tags: frames named alike but for a number at the end (walk_01, walk_02… or walk/01)
		// make an animation of that name, in the order of their numbers
		if (json.Meta.FrameTags.Count == 0)
			foreach (var group in Pieces.GroupBy(p => Stem(p.Name)).Where(g => g.Key.Length > 0 && g.Count() >= 2))
			{
				var animation = new SheetAnimation
				{
					Name = group.Key,
					Anchor = Origin != null ? FrameAnchor.Origin : FrameAnchor.Centre
				};
				foreach (var piece in group.OrderBy(p => Number(p.Name)))
				{
					piece.Animation = animation;
					animation.Frames.Add(piece);
				}
				Animations.Add(animation);
			}
		return rotated;
	}

	private static readonly string[] PictureExtensions = { ".png", ".gif", ".jpg", ".jpeg", ".bmp", ".webp", ".tga", ".psd", ".aseprite", ".ase" };

	// "walk 3.png" → "walk 3", "hero/run/0004.png" → "hero_run_0004"
	private string PieceName(string filename, int index)
	{
		string name = (filename ?? "").Trim();
		if (PictureExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } extension)
			name = name[..^extension.Length];
		name = name.Replace('/', '_').Replace('\\', '_').Trim();
		return name.Length > 0 ? name : $"{Name}_{index + 1}";
	}

	// The name without its number at the end: "walk_0003" → "walk" ("" when there's no number)
	public static string Stem(string name)
	{
		int end = name.Length;
		while (end > 0 && char.IsDigit(name[end - 1])) end--;
		if (end == name.Length) return "";
		return name[..end].TrimEnd(' ', '_', '-', '.', '#').Trim();
	}

	private static int Number(string name)
	{
		int start = name.Length;
		while (start > 0 && char.IsDigit(name[start - 1])) start--;
		return int.TryParse(name[start..], out int n) ? n : 0;
	}
}

// Makes the objects or parts a plan describes. The pixels go into the asset store (where they stay,
// so undo can bring them back); everything else is new model objects, for the editor's undo step.
public static class SheetImporter
{
	// Each piece becomes a drawn object where it was on the sheet (the sheet's middle at `centre`).
	// Animations: one object playing them all (each its own animation), or an object each.
	public static List<SceneObject> IntoScene(Scene scene, AssetStore store, SheetImportPlan plan, Vector2 centre, int fps)
	{
		var made = new List<SceneObject>();
		var animations = plan.LiveAnimations.ToList();
		var groups = plan.AnimationsTogether
			? (animations.Count > 0 ? new List<List<SheetAnimation>> { animations } : new List<List<SheetAnimation>>())
			: animations.Select(a => new List<SheetAnimation> { a }).ToList();

		foreach (var group in groups)
		{
			string name = plan.AnimationsTogether ? plan.Name : group[0].Name;
			var first = group[0].Frames.First(f => !f.LeftOut);
			var at = OnScene(plan.Sheet, Corner(first.Rect) + AnchorPixel(group[0], first, plan.Origin), centre);
			var obj = SceneOps.AddDrawn(scene, name, at);
			AddAnimatedPart(obj.Skeleton, obj.Skeleton.Roots[0], obj.Animations, store, plan, group, name, Vector2.Zero, 0f, fps,
				reuseFirstAnimation: true);
			made.Add(obj);
		}

		if (plan.Stills)
			foreach (var piece in plan.StillPieces)
			{
				var obj = SceneOps.AddDrawn(scene, piece.Name, OnScene(plan.Sheet, Middle(piece.Rect), centre));
				var asset = NewAsset(store, plan.Sheet, piece.Rect, piece.Name);
				PartOps.CreatePart(obj.Skeleton, obj.Skeleton.Roots[0], asset, piece.Name, Vector2.Zero, 0f);
				made.Add(obj);
			}
		return made;
	}

	// Each piece becomes a part on `bone` (laid out as on the sheet around the object's origin,
	// turned to look upright); each animation becomes a part showing its frames, and a new
	// animation of the object that plays them
	public static List<Slot> IntoObject(SceneObject obj, Bone bone, AssetStore store, SheetImportPlan plan, int fps)
	{
		var made = new List<Slot>();
		var toBone = Matrix3x2.Invert(bone.World, out var inverse) ? inverse : Matrix3x2.Identity;
		float upright = -bone.WorldRotation;
		Vector2 OnBone(Vector2 sheetPoint) => Vector2.Transform(OnScene(plan.Sheet, sheetPoint, Vector2.Zero), toBone);

		foreach (var animation in plan.LiveAnimations)
		{
			var first = animation.Frames.First(f => !f.LeftOut);
			var at = OnBone(Corner(first.Rect) + AnchorPixel(animation, first, plan.Origin));
			made.Add(AddAnimatedPart(obj.Skeleton, bone, obj.Animations, store, plan, new List<SheetAnimation> { animation },
				animation.Name, at, upright, fps, reuseFirstAnimation: false));
		}

		if (plan.Stills)
			foreach (var piece in plan.StillPieces)
			{
				var asset = NewAsset(store, plan.Sheet, piece.Rect, piece.Name);
				made.Add(PartOps.CreatePart(obj.Skeleton, bone, asset, piece.Name, OnBone(Middle(piece.Rect)), upright));
			}
		return made;
	}

	// One part whose drawings are the animations' frames, each animation keying its frames in turn.
	// The first animation can take over the object's own (a new drawn object comes with one).
	// A frame that comes back (a ping-pong, or a sheet that lists the same picture twice) shows the
	// drawing made the first time.
	private static Slot AddAnimatedPart(Skeleton skeleton, Bone bone, List<Animation> animations, AssetStore store,
		SheetImportPlan plan, List<SheetAnimation> group, string partName, Vector2 offset, float rotation, int fps,
		bool reuseFirstAnimation)
	{
		Slot? slot = null;
		Attachment? template = null;
		var made = new Dictionary<(PixelRect Rect, Vector2 Anchor), Attachment>();
		for (int a = 0; a < group.Count; a++)
		{
			var sheetAnimation = group[a];
			Animation animation;
			if (a == 0 && reuseFirstAnimation && animations.Count > 0)
			{
				animation = animations[0];
				animation.Timelines.Clear();
			}
			else
			{
				animation = new Animation();
				animations.Add(animation);
			}
			animation.Name = UniqueAnimationName(animations, animation, sheetAnimation.Name);
			animation.Loop = sheetAnimation.Loop;

			int frame = 0;
			foreach (var piece in sheetAnimation.Frames.Where(f => !f.LeftOut))
			{
				// Every frame's anchor point lands on the same spot
				var anchor = AnchorPixel(sheetAnimation, piece, plan.Origin);
				if (!made.TryGetValue((piece.Rect, anchor), out var drawing))
				{
					var asset = NewAsset(store, plan.Sheet, piece.Rect, piece.Name);
					if (slot == null)
					{
						slot = PartOps.CreatePart(skeleton, bone, asset, partName, offset, rotation);
						drawing = skeleton.DefaultSkin.Get(slot.Name, slot.Name)!;   // a new part's drawing has its name
					}
					else
					{
						drawing = DrawingOps.AddDrawing(skeleton, slot, template, asset);
					}
					template ??= drawing;
					drawing.Pivot = new Vector2(anchor.X / piece.Rect.W, anchor.Y / piece.Rect.H);
					made[(piece.Rect, anchor)] = drawing;
				}

				animation.GetOrCreate(slot!.Name, n => new AttachmentTimeline(n)).Add(frame / (float)fps, drawing.Name);
				frame += sheetAnimation.HoldOf(piece);
			}
			animation.Duration = Math.Max(1, frame) / (float)fps;
		}
		return slot!;
	}

	private static string UniqueAnimationName(List<Animation> animations, Animation self, string baseName)
	{
		baseName = string.IsNullOrWhiteSpace(baseName) ? "animation" : baseName.Trim();
		bool Taken(string n) => animations.Any(a => a != self && a.Name == n);
		if (!Taken(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!Taken($"{baseName}{i}")) return $"{baseName}{i}";
	}

	// The point of a frame that lines up with the other frames, in its own pixels (y down):
	// its middle, its bottom middle, or the JSON's origin. A trimmed frame measures from where it
	// was before the trim.
	public static Vector2 AnchorPixel(SheetAnimation? animation, SheetPiece piece, Vector2? origin)
	{
		var frame = piece.Untrimmed ?? new PixelRect(0, 0, piece.Rect.W, piece.Rect.H);
		var point = (animation?.Anchor ?? FrameAnchor.Centre) switch
		{
			FrameAnchor.Bottom => new Vector2(frame.W / 2f, frame.H),
			FrameAnchor.Origin when origin is { } o => o,
			_ => new Vector2(frame.W / 2f, frame.H / 2f)
		};
		return point - new Vector2(frame.X, frame.Y);   // into the trimmed piece's own pixels
	}

	private static Vector2 Corner(PixelRect r) => new(r.X, r.Y);
	private static Vector2 Middle(PixelRect r) => new(r.X + r.W / 2f, r.Y + r.H / 2f);

	// A point of the sheet (y down) in the scene (y up), with the sheet's middle at `centre`
	private static Vector2 OnScene(SKBitmap sheet, Vector2 sheetPoint, Vector2 centre) =>
		centre + new Vector2(sheetPoint.X - sheet.Width / 2f, sheet.Height / 2f - sheetPoint.Y);

	private static Asset NewAsset(AssetStore store, SKBitmap sheet, PixelRect r, string name)
	{
		var asset = new Asset { Name = name, Width = r.W, Height = r.H };
		using var pixels = new SKBitmap(AssetStore.InfoFor(r.W, r.H));
		pixels.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(pixels))
			canvas.DrawBitmap(sheet, -r.X, -r.Y, AssetStore.Nearest, null);   // whole pixels: an exact copy
		store.AddLayer(asset, "Base", pixels);
		return asset;
	}

	// A picture file as the editor keeps pictures (its own pixel format). null = unreadable.
	public static SKBitmap? LoadPicture(string path)
	{
		using var decoded = SKBitmap.Decode(path);
		if (decoded == null) return null;
		var picture = new SKBitmap(AssetStore.InfoFor(decoded.Width, decoded.Height));
		picture.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(picture))
			canvas.DrawBitmap(decoded, 0, 0, AssetStore.Nearest, null);
		return picture;
	}

	// Each pixel's opacity, for cutting the sheet up
	public static byte[] Alpha(SKBitmap picture)
	{
		var pixels = picture.GetPixelSpan();
		var alpha = new byte[picture.Width * picture.Height];
		for (int i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];   // RGBA or BGRA: alpha is last
		return alpha;
	}
}
