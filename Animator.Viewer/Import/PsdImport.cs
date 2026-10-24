using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Import;

// One layer of a Photoshop file, as it will come in: a part
public sealed class PsdPart
{
	public string Name { get; set; } = "";
	public string Group { get; init; } = "";              // the groups it was in, for showing ("arms / left")
	public PixelRect Bounds { get; init; }                 // on the canvas, trimmed to what shows
	public SKBitmap Pixels { get; init; } = null!;         // in the editor's own format
	public float Opacity { get; init; } = 1f;
	public BlendMode Blend { get; init; }
	public bool HiddenInFile { get; init; }
	public bool Include { get; set; } = true;
	public RigRole Role { get; set; } = RigRole.Bone;      // in a suggested rig
	public int Target { get; set; } = -1;                  // the layer it hangs from / rides on (-1 = worked out)

	public Vector2 Middle => new(Bounds.X + Bounds.W / 2f, Bounds.Y + Bounds.H / 2f);
}

// What the Photoshop import dialog decided: which layers come in, and how (a drawn object, or a
// rigged one with a suggested skeleton)
public sealed class PsdImportPlan : IDisposable
{
	public string Name { get; set; } = "";
	public int Width { get; init; }
	public int Height { get; init; }
	public List<PsdPart> Parts { get; } = new();           // bottom to top, as in the file
	public List<string> Notes { get; } = new();            // what didn't come across as it was
	public bool SuggestRig { get; set; }
	public int Root { get; set; } = -1;                    // the layer whose bone everything hangs from (-1 = worked out)
	public float Scale { get; set; } = 1f;                 // of the pixels and the positions

	private RigMasks? _masks;
	public RigMasks Masks => _masks ??= RigMasks.Build(Width, Height,
		Parts.Select(p => p.Bounds).ToList(), Parts.Select(p => SheetImporter.Alpha(p.Pixels)).ToList());

	// The rig as the parts' roles say now
	public RigSuggestion Suggest() => RigSuggester.Suggest(Masks,
		Parts.Select(p => p.Include ? p.Role : RigRole.LeaveOut).ToList(), Parts.Select(p => p.Target).ToList(), Root);

	// Roles as the shapes suggest (details and backdrops ride on other layers' bones), choices cleared
	public void SuggestRoles()
	{
		var roles = RigSuggester.SuggestRoles(Masks, Parts.Select(p => p.Include).ToList());
		var ifTicked = RigSuggester.SuggestRoles(Masks);   // for the unticked ones, in case they get ticked
		for (int i = 0; i < Parts.Count; i++)
		{
			Parts[i].Role = roles[i] != RigRole.LeaveOut ? roles[i] : ifTicked[i];
			Parts[i].Target = -1;
		}
		Root = -1;
	}

	public void Dispose()
	{
		foreach (var part in Parts) part.Pixels.Dispose();
	}

	// The file's layers as parts: clipped layers drawn into the layers they're clipped to, empty
	// borders trimmed off, names made unique
	public static PsdImportPlan FromDocument(PsdDocument doc, string name)
	{
		var plan = new PsdImportPlan { Name = name, Width = doc.Width, Height = doc.Height };
		int merged = 0, empty = doc.EmptyLayers, styled = 0, otherBlends = 0;
		var bases = new List<(PsdLayer Layer, SKBitmap Pixels, bool Hidden)>();

		foreach (var layer in doc.Layers)
		{
			if (layer.HasStyle) styled++;
			var b = layer.Bounds;
			if (b.IsEmpty || layer.Pixels.Length < b.W * b.H * 4)
			{
				empty++;
				continue;
			}
			// A clipping mask: shows only where the layer below it shows, so it's drawn into that one
			if (layer.Clipped && bases.Count > 0 && bases[^1].Layer.Groups.SequenceEqual(layer.Groups))
			{
				var (below, pixels, hidden) = bases[^1];
				if (layer.Visible && !hidden) MergeClipped(pixels, below.Bounds, layer);
				merged++;
				continue;
			}
			bases.Add((layer, FromRgba(layer.Pixels, b.W, b.H), !layer.Visible));
		}

		var names = bases.GroupBy(x => x.Layer.Name.Trim()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
		foreach (var (layer, pixels, hidden) in bases)
		{
			var shown = Trim(pixels, layer.Bounds, out var bounds);
			if (shown == null)
			{
				empty++;
				continue;
			}
			string partName = string.IsNullOrWhiteSpace(layer.Name) ? "layer" : layer.Name.Trim();
			if (names.Contains(partName) && layer.Groups.Count > 0) partName = $"{layer.Groups[^1].Trim()} {partName}";   // "left arm", "right arm"
			var blend = ToBlend(layer.Blend, out bool known);
			if (!known) otherBlends++;
			plan.Parts.Add(new PsdPart
			{
				Name = partName,
				Group = string.Join(" / ", layer.Groups),
				Bounds = bounds,
				Pixels = shown,
				Opacity = Math.Clamp(layer.Opacity, 0f, 1f),
				Blend = blend,
				HiddenInFile = hidden,
				Include = !hidden
			});
		}

		if (doc.Flat) plan.Notes.Add("The file has no layers: it comes in as one picture.");
		if (merged > 0) plan.Notes.Add($"{merged} clipped layer(s) were drawn into the layers they're clipped to.");
		if (empty > 0) plan.Notes.Add($"{empty} layer(s) with nothing to show (adjustment and fill layers, empty layers) were left out.");
		if (styled > 0) plan.Notes.Add($"{styled} layer(s) have layer styles (shadows, strokes, glows…), which don't come across.");
		if (otherBlends > 0) plan.Notes.Add($"{otherBlends} layer(s) use blend modes Harlequin doesn't have: they come in as Normal.");
		return plan;
	}

	private static BlendMode ToBlend(string key, out bool known)
	{
		known = true;
		switch (key)
		{
			case "norm":
			case "pass": return BlendMode.Normal;
			case "lddg": return BlendMode.Add;
			case "mul ": return BlendMode.Multiply;
			case "scrn": return BlendMode.Screen;
			default:
				known = false;
				return BlendMode.Normal;
		}
	}

	// Straight RGBA bytes as a bitmap in the editor's format
	private static SKBitmap FromRgba(byte[] rgba, int w, int h)
	{
		using var straight = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
		System.Runtime.InteropServices.Marshal.Copy(rgba, 0, straight.GetPixels(), w * h * 4);
		straight.NotifyPixelsChanged();
		var bitmap = new SKBitmap(AssetStore.InfoFor(w, h));
		bitmap.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(bitmap))
			canvas.DrawBitmap(straight, 0, 0, AssetStore.Nearest, null);
		return bitmap;
	}

	private static SKBlendMode SkiaBlend(string key) => key switch
	{
		"mul " => SKBlendMode.Multiply,
		"scrn" => SKBlendMode.Screen,
		"over" => SKBlendMode.Overlay,
		"dark" => SKBlendMode.Darken,
		"lite" => SKBlendMode.Lighten,
		"div " => SKBlendMode.ColorDodge,
		"idiv" => SKBlendMode.ColorBurn,
		"lddg" => SKBlendMode.Plus,
		"sLit" => SKBlendMode.SoftLight,
		"hLit" => SKBlendMode.HardLight,
		"diff" => SKBlendMode.Difference,
		"smud" => SKBlendMode.Exclusion,
		"hue " => SKBlendMode.Hue,
		"sat " => SKBlendMode.Saturation,
		"colr" => SKBlendMode.Color,
		"lum " => SKBlendMode.Luminosity,
		_ => SKBlendMode.SrcOver
	};

	// Draws a clipped layer into the layer it's clipped to: only where that one shows, which keeps
	// its own see-through edges exactly as they were
	private static void MergeClipped(SKBitmap below, PixelRect belowBounds, PsdLayer layer)
	{
		using var clipped = FromRgba(layer.Pixels, layer.Bounds.W, layer.Bounds.H);
		using var masked = new SKBitmap(below.Info);
		masked.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(masked))
		{
			canvas.DrawBitmap(clipped, layer.Bounds.X - belowBounds.X, layer.Bounds.Y - belowBounds.Y, AssetStore.Nearest, null);
			using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
			canvas.DrawBitmap(below, 0, 0, AssetStore.Nearest, keep);
		}

		var mode = SkiaBlend(layer.Blend);
		var alpha = mode == SKBlendMode.SrcOver ? null : PixelBuffer.Read(below);
		using (var canvas = new SKCanvas(below))
		using (var paint = new SKPaint
		{
			BlendMode = mode == SKBlendMode.SrcOver ? SKBlendMode.SrcATop : mode,
			Color = SKColors.White.WithAlpha((byte)MathF.Round(Math.Clamp(layer.Opacity, 0f, 1f) * 255))
		})
			canvas.DrawBitmap(masked, 0, 0, AssetStore.Nearest, paint);
		if (alpha != null) KeepAlpha(below, alpha);
	}

	// Blend modes can make half see-through edges more solid: puts the opacity back as it was
	private static void KeepAlpha(SKBitmap bitmap, PixelBuffer before)
	{
		var after = PixelBuffer.Read(bitmap);
		for (int i = 0; i < after.Data.Length; i++)
		{
			uint was = (uint)before.Data[i] >> 24, now = (uint)after.Data[i] >> 24;
			if (was == now) continue;
			if (was == 0 || now == 0)
			{
				after.Data[i] = 0;
				continue;
			}
			uint px = (uint)after.Data[i];
			uint c0 = ((px & 255) * was + now / 2) / now;
			uint c1 = ((px >> 8 & 255) * was + now / 2) / now;
			uint c2 = ((px >> 16 & 255) * was + now / 2) / now;
			after.Data[i] = (int)(c0 | c1 << 8 | c2 << 16 | was << 24);
		}
		after.WriteTo(bitmap);
	}

	// The part of the bitmap that shows (null = none), and where that is on the canvas
	private static SKBitmap? Trim(SKBitmap pixels, PixelRect bounds, out PixelRect shown)
	{
		var alpha = SheetImporter.Alpha(pixels);
		int w = pixels.Width, h = pixels.Height;
		int x0 = w, y0 = h, x1 = -1, y1 = -1;
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
			{
				if (alpha[y * w + x] == 0) continue;
				x0 = Math.Min(x0, x);
				y0 = Math.Min(y0, y);
				x1 = Math.Max(x1, x);
				y1 = Math.Max(y1, y);
			}
		shown = default;
		if (x1 < 0)
		{
			pixels.Dispose();
			return null;
		}
		shown = new PixelRect(bounds.X + x0, bounds.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
		if (shown.W == w && shown.H == h) return pixels;

		var trimmed = new SKBitmap(AssetStore.InfoFor(shown.W, shown.H));
		trimmed.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(trimmed))
			canvas.DrawBitmap(pixels, -x0, -y0, AssetStore.Nearest, null);
		pixels.Dispose();
		return trimmed;
	}
}

// Makes the object a plan describes: the canvas's middle at the object's origin, each layer a part
// where it was. With a suggested rig, the bones too, each layer's part on its bone.
public static class PsdImporter
{
	private static readonly SKSamplingOptions Shrink = new(SKFilterMode.Linear, SKMipmapMode.Linear);
	private static readonly SKSamplingOptions Grow = new(SKFilterMode.Linear, SKMipmapMode.None);

	public static SceneObject IntoScene(Scene scene, AssetStore store, PsdImportPlan plan, Vector2 position)
	{
		float scale = Math.Clamp(plan.Scale, 0.01f, 16f);
		Vector2 ToObject(Vector2 canvas) => new Vector2(canvas.X - plan.Width / 2f, plan.Height / 2f - canvas.Y) * scale;

		if (!plan.SuggestRig)
		{
			var drawn = SceneOps.AddDrawn(scene, plan.Name, position);
			var bone = drawn.Skeleton.Roots[0];
			foreach (var part in plan.Parts.Where(p => p.Include))
				AddPart(drawn.Skeleton, bone, store, part, ToObject(part.Middle), 0f, scale);
			return drawn;
		}

		var rig = plan.Suggest();
		var obj = SceneOps.AddRigged(scene, plan.Name, position);
		var skeleton = obj.Skeleton;
		skeleton.Roots.Clear();   // the new object's own root bone makes way for the suggested ones

		var bones = new Bone[rig.Bones.Count];
		for (int b = 0; b < bones.Length; b++)
		{
			var suggested = rig.Bones[b];
			var parent = suggested.Parent >= 0 ? bones[suggested.Parent] : null;
			Vector2 head = ToObject(suggested.Head), tip = ToObject(suggested.Tip);
			var along = tip - head;
			float angle = MathF.Atan2(along.Y, along.X);
			var local = head;
			if (parent != null && Matrix3x2.Invert(parent.World, out var toParent))
			{
				local = Vector2.Transform(head, toParent);
				angle -= parent.WorldRotation;
			}
			var bone = RigOps.AddBone(skeleton, parent, plan.Parts[suggested.Layer].Name, local, angle, MathF.Max(1f, along.Length()));
			if (suggested.Connected && parent != null) bone.Connected = true;
			bone.ResetToSetup();
			bone.UpdateWorld();
			bones[b] = bone;
		}

		for (int i = 0; i < plan.Parts.Count; i++)
		{
			var part = plan.Parts[i];
			if (!part.Include || rig.BoneOf[i] < 0) continue;
			var bone = bones[rig.BoneOf[i]];
			var offset = Matrix3x2.Invert(bone.World, out var toBone)
				? Vector2.Transform(ToObject(part.Middle), toBone)
				: Vector2.Zero;
			AddPart(skeleton, bone, store, part, offset, -bone.WorldRotation, scale);   // upright, as in the file
		}
		skeleton.SetToSetupPose();
		skeleton.UpdatePose();
		return obj;
	}

	private static Slot AddPart(Skeleton skeleton, Bone bone, AssetStore store, PsdPart part, Vector2 offset, float rotation, float scale)
	{
		int w = Math.Max(1, (int)MathF.Round(part.Bounds.W * scale)), h = Math.Max(1, (int)MathF.Round(part.Bounds.H * scale));
		var asset = new Asset { Name = part.Name, Width = w, Height = h };
		if (w == part.Bounds.W && h == part.Bounds.H)
		{
			store.AddLayer(asset, "Base", part.Pixels);
		}
		else
		{
			using var resized = new SKBitmap(AssetStore.InfoFor(w, h));
			resized.Erase(SKColors.Transparent);
			using (var canvas = new SKCanvas(resized))
			{
				canvas.Scale(w / (float)part.Bounds.W, h / (float)part.Bounds.H);
				canvas.DrawBitmap(part.Pixels, 0, 0, scale < 1 ? Shrink : Grow, null);
			}
			store.AddLayer(asset, "Base", resized);
		}
		var slot = PartOps.CreatePart(skeleton, bone, asset, part.Name, offset, rotation);
		slot.Blend = part.Blend;
		slot.SetupOpacity = part.Opacity;
		slot.ResetToSetup();
		return slot;
	}
}
