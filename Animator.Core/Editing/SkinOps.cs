using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Skins (the rig panel's Skins view): drawings for some parts kept apart from the object's own,
// shown when the skin is on. Skins in a category are alternatives, one on at a time (a "Bucket"
// category: the bucket, dented, wrecked); skins in none are outfits, one on at a time too;
// overlays stack over anything. Existing parts, or one part's drawings, become skins here.
// Like the other ops, these change the model directly; the editor adds undo.
public static class SkinOps
{
	public static bool SameCategory(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

	// A name no other skin has (nor the object's own drawings: "default")
	public static string UniqueName(Skeleton skeleton, string baseName)
	{
		baseName = baseName.Trim().Length > 0 ? baseName.Trim() : "Skin";
		if (IsFree(skeleton, baseName, null)) return baseName;
		for (int i = 2; ; i++)
			if (IsFree(skeleton, $"{baseName} {i}", null)) return $"{baseName} {i}";
	}

	public static bool IsFree(Skeleton skeleton, string name, Skin? except) =>
		!string.Equals(name, skeleton.DefaultSkin.Name, StringComparison.OrdinalIgnoreCase)
		&& !skeleton.Skins.Any(s => s != except && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

	public static bool Rename(Skeleton skeleton, Skin skin, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == skin.Name || !IsFree(skeleton, name, skin)) return false;
		skin.Name = name;
		return true;
	}

	// The categories in use, in list order
	public static List<string> Categories(Skeleton skeleton) =>
		skeleton.Skins.Select(s => s.Category.Trim()).Where(c => c.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

	// On or off. On turns off the others it can't show with: the other skins of its category
	// (or the other outfits). Overlays stack, so turning one on turns nothing off.
	public static void Show(Skeleton skeleton, Skin skin, bool on = true)
	{
		if (!on)
		{
			skeleton.ShownSkins.Remove(skin);
			return;
		}
		if (!skin.Overlay)
			skeleton.ShownSkins.RemoveWhere(s => s != skin && !s.Overlay && SameCategory(s.Category, skin.Category));
		skeleton.ShownSkins.Add(skin);
	}

	// A skin moved to another category, or made (or no longer) an overlay: if it's on, the
	// ones it can't show with go off
	public static void SetKind(Skeleton skeleton, Skin skin, string category, bool overlay)
	{
		skin.Category = category.Trim();
		skin.Overlay = overlay;
		if (skeleton.ShownSkins.Contains(skin)) Show(skeleton, skin);
	}

	// The parts a skin has drawings for, in draw order
	public static List<Slot> PartsOf(Skeleton skeleton, Skin skin)
	{
		var names = skin.Entries.Select(e => e.Slot).ToHashSet();
		return skeleton.Slots.Where(s => names.Contains(s.Name)).ToList();
	}

	// The skins that have drawings for a part
	public static List<Skin> SkinsWith(Skeleton skeleton, Slot slot) =>
		skeleton.Skins.Where(s => s.AttachmentsFor(slot.Name).Any()).ToList();

	// Whether the part has drawings of its own (outside any skin)
	public static bool HasOwnDrawings(Skeleton skeleton, Slot slot) =>
		skeleton.DefaultSkin.AttachmentsFor(slot.Name).Any();

	// Editing in a skin: the skin's own copy of the drawing a part shows by this name (its rest
	// drawing when null), made from the one showing now if the skin has none: placed the same,
	// the same pixels (copy them before painting it: see SharesPixels). From then on, changing it
	// changes only the skin; the object's own drawing (skin 0) stays as it was. The skin is on.
	public static Attachment? Own(Skeleton skeleton, Skin skin, Slot slot, string? name = null)
	{
		name ??= slot.ActiveAttachment ?? slot.SetupAttachment;
		if (name == null) return null;
		if (skin.Get(slot.Name, name) is { } mine)
		{
			Show(skeleton, skin);
			return mine;
		}
		var from = skeleton.FindAttachment(slot.Name, name);   // (what shows now, before turning the skin on)
		if (from == null) return null;
		var copy = new Attachment
		{
			Name = name,
			AssetId = from.AssetId,
			Offset = from.Offset,
			Rotation = from.Rotation,
			Scale = from.Scale,
			Pivot = from.Pivot
		};
		skin.Set(slot.Name, copy);
		Show(skeleton, skin);
		return copy;
	}

	// Whether another drawing shows the same pixels (painting it would change that one too)
	public static bool SharesPixels(Skeleton skeleton, Attachment drawing) =>
		skeleton.AllAttachments().Any(a => a != drawing && a.AssetId == drawing.AssetId);

	// The parts' own drawings moved into the skin (from then on they show only while it's on).
	// A drawing the skin has already, by the same name, stays the skin's. How many parts moved.
	public static int AddParts(Skeleton skeleton, Skin skin, IEnumerable<Slot> parts)
	{
		int moved = 0;
		foreach (var slot in parts)
		{
			var own = skeleton.DefaultSkin.AttachmentsFor(slot.Name).ToList();
			if (own.Count == 0) continue;
			foreach (var drawing in own)
			{
				skeleton.DefaultSkin.Remove(slot.Name, drawing.Name);
				if (skin.Get(slot.Name, drawing.Name) == null) skin.Set(slot.Name, drawing);
			}
			moved++;
		}
		PartOps.RemoveUnusedAssets(skeleton);
		return moved;
	}

	// The skin's drawings back to the object's own (where it has none by that name)
	public static int TakeOutParts(Skeleton skeleton, Skin skin, IEnumerable<Slot> parts)
	{
		int moved = 0;
		foreach (var slot in parts)
		{
			var drawings = skin.AttachmentsFor(slot.Name).ToList();
			if (drawings.Count == 0) continue;
			foreach (var drawing in drawings)
			{
				skin.Remove(slot.Name, drawing.Name);
				if (skeleton.DefaultSkin.Get(slot.Name, drawing.Name) == null) skeleton.DefaultSkin.Set(slot.Name, drawing);
			}
			moved++;
		}
		PartOps.RemoveUnusedAssets(skeleton);
		return moved;
	}

	// Colour layers of the object into the skin (out of any other they were in): from then on they
	// apply only while it's on, in the same order as ever. How many moved.
	public static int AddColorLayers(Skeleton skeleton, Skin skin, IEnumerable<ColorLayer> layers)
	{
		int moved = 0;
		foreach (var layer in layers)
		{
			if (!skeleton.ColorLayers.Contains(layer) || skin.ColorLayers.Contains(layer)) continue;
			foreach (var other in skeleton.Skins) other.ColorLayers.Remove(layer);
			skin.ColorLayers.Add(layer);
			moved++;
		}
		return moved;
	}

	// Colour layers out of the skin: the object's own again (they apply whatever skins are on)
	public static int TakeOutColorLayers(Skin skin, IEnumerable<ColorLayer> layers) =>
		layers.ToList().Count(skin.ColorLayers.Remove);

	// A colour layer gone from the object: out of its skin too
	public static void Forget(Skeleton skeleton, ColorLayer layer)
	{
		foreach (var skin in skeleton.Skins) skin.ColorLayers.Remove(layer);
	}

	// A new skin (at the end of the list, on), with these parts' own drawings and these colour layers in it
	public static Skin MakeSkin(Skeleton skeleton, string name, IEnumerable<Slot>? parts = null, string category = "", bool overlay = false,
		IEnumerable<ColorLayer>? colorLayers = null)
	{
		var skin = new Skin { Name = UniqueName(skeleton, name), Category = category.Trim(), Overlay = overlay };
		skeleton.Skins.Add(skin);
		if (parts != null) AddParts(skeleton, skin, parts);
		if (colorLayers != null) AddColorLayers(skeleton, skin, colorLayers);
		Show(skeleton, skin);
		return skin;
	}

	// Each part a skin of its own, in one category (layers drawn as alternatives: the bucket,
	// the dented bucket, the wrecked one). The first is on.
	public static List<Skin> MakeCategoryFromParts(Skeleton skeleton, string category, IEnumerable<Slot> parts)
	{
		var made = new List<Skin>();
		foreach (var slot in parts)
		{
			if (!HasOwnDrawings(skeleton, slot)) continue;
			var skin = new Skin { Name = UniqueName(skeleton, slot.Name), Category = category.Trim() };
			skeleton.Skins.Add(skin);
			AddParts(skeleton, skin, new[] { slot });
			made.Add(skin);
		}
		if (made.Count > 0) Show(skeleton, made[0]);
		return made;
	}

	// One part's drawings, each a skin of its own in one category: the part keeps one name for
	// its drawing (its rest drawing's), and which skin is on says what it looks like. The keys
	// that swapped between them show that drawing instead. The one at rest is on.
	public static List<Skin> MakeCategoryFromDrawings(Skeleton skeleton, IEnumerable<Animation> animations, Slot slot, string category)
	{
		var own = DrawingOps.DrawingNames(skeleton, slot).Select(n => skeleton.DefaultSkin.Get(slot.Name, n)).OfType<Attachment>().ToList();
		if (own.Count < 2) return new List<Skin>();
		string shared = slot.SetupAttachment is { } rest && own.Any(d => d.Name == rest) ? rest : own[0].Name;
		var names = own.Select(d => d.Name).ToHashSet();

		var made = new List<Skin>();
		Skin? atRest = null;
		foreach (var drawing in own)
		{
			var skin = new Skin { Name = UniqueName(skeleton, drawing.Name), Category = category.Trim() };
			skin.Set(slot.Name, new Attachment
			{
				Name = shared,
				AssetId = drawing.AssetId,
				Offset = drawing.Offset,
				Rotation = drawing.Rotation,
				Scale = drawing.Scale,
				Pivot = drawing.Pivot
			});
			skeleton.DefaultSkin.Remove(slot.Name, drawing.Name);
			skeleton.Skins.Add(skin);
			made.Add(skin);
			if (drawing.Name == shared) atRest = skin;
		}

		foreach (var row in animations.Select(a => a.Find<AttachmentTimeline>(slot.Name)).OfType<AttachmentTimeline>())
		{
			var keys = row.Track.Keys;
			for (int i = 0; i < keys.Count; i++)
				if (keys[i].Value is { } value && names.Contains(value) && value != shared) keys[i] = keys[i] with { Value = shared };
		}
		if (slot.SetupAttachment != null) slot.SetupAttachment = shared;
		Show(skeleton, atRest ?? made[0]);
		return made;
	}

	// The skin gone with its drawings and colour layers (and their keys); parts left with no
	// drawings at all go too
	public static void Delete(Skeleton skeleton, IEnumerable<Animation> animations, Skin skin)
	{
		var parts = PartsOf(skeleton, skin);
		animations = animations.ToList();
		foreach (var layer in skin.ColorLayers)
		{
			skeleton.ColorLayers.Remove(layer);
			foreach (var animation in animations)
				animation.Timelines.RemoveAll(t => t is ColorLayerTimeline && t.Target == layer.Name);
		}
		skeleton.Skins.Remove(skin);
		skeleton.ShownSkins.Remove(skin);
		foreach (var slot in parts)
			if (!skeleton.AllSkins().Any(s => s.AttachmentsFor(slot.Name).Any()))
				PartOps.DeletePart(skeleton, animations, slot);
		PartOps.RemoveUnusedAssets(skeleton);
	}

	// The skin gone, its drawings and colour layers back to the object's own (drawings where it has
	// none by those names)
	public static void Dissolve(Skeleton skeleton, Skin skin)
	{
		TakeOutParts(skeleton, skin, PartsOf(skeleton, skin));
		skin.ColorLayers.Clear();
		skeleton.Skins.Remove(skin);
		skeleton.ShownSkins.Remove(skin);
		PartOps.RemoveUnusedAssets(skeleton);
	}

	// A step earlier (-1) or later (+1) in the list: later ones win where two that are on overlap
	public static bool Move(Skeleton skeleton, Skin skin, int direction)
	{
		int i = skeleton.Skins.IndexOf(skin), j = i + direction;
		if (i < 0 || j < 0 || j >= skeleton.Skins.Count) return false;
		(skeleton.Skins[i], skeleton.Skins[j]) = (skeleton.Skins[j], skeleton.Skins[i]);
		return true;
	}
}
