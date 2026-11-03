namespace Animator.Viewer;

// The open object's skins, edited (EditorSession's side of the Skins panel): made from parts,
// drawings or colour layers, filled and emptied, shown, ordered, renamed, put in categories,
// deleted, and the skin being edited (where changes to parts go). Each edit is one undo step and
// says whether it changed anything. Nothing here at scene level: skins belong to the open object.
public sealed partial class EditorSession
{
	Skeleton Rig => State.Skeleton;

	// One skin edit as one undo step (change: whether it changed anything); the rig posed after
	public bool EditSkins(string name, Func<bool> change)
	{
		if (SceneLevel) return false;
		bool changed = State.EditStructure(() => change() ? name : null);
		Rig.UpdateWorld();
		return changed;
	}

	// The picked parts with drawings of their own (in draw order)
	public List<Slot> PickedOwnParts() =>
		Rig.Slots.Where(s => State.Picked.Contains(s) && SkinOps.HasOwnDrawings(Rig, s)).ToList();

	// ======================= making skins =======================

	// The parts' drawings as one new skin (named after them)
	public Skin? MakeSkinFromParts(IReadOnlyList<Slot> parts)
	{
		Skin? made = null;
		EditSkins($"Make a skin of {Describe(parts)}", () =>
		{
			made = SkinOps.MakeSkin(Rig, parts.Count == 1 ? parts[0].Name : CommonName(parts) ?? "Skin", parts.ToList());
			return true;
		});
		return made;
	}

	// Each part a skin, alternatives in one category
	public List<Skin> MakeCategoryFromParts(string category, IReadOnlyList<Slot> parts)
	{
		var made = new List<Skin>();
		EditSkins($"Make a category {category} of {parts.Count} parts", () =>
		{
			made = SkinOps.MakeCategoryFromParts(Rig, category, parts.ToList());
			return made.Count > 0;
		});
		return made;
	}

	// Each of a part's drawings a skin, alternatives in one category (its drawing keys show the
	// skin's drawing from then on)
	public List<Skin> MakeCategoryFromDrawings(Slot slot, string category)
	{
		var made = new List<Skin>();
		EditSkins($"Make a category {category} of {slot.Name}'s drawings", () =>
		{
			made = SkinOps.MakeCategoryFromDrawings(Rig, State.Animations, slot, category);
			return made.Count > 0;
		});
		return made;
	}

	// Colour layers that apply only while the new skin is on
	public Skin? MakeSkinFromColours(IReadOnlyList<ColorLayer> layers)
	{
		Skin? made = null;
		string what = layers.Count == 1 ? layers[0].Name : $"{layers.Count} colour layers";
		EditSkins($"Make a skin of {what}", () =>
		{
			made = SkinOps.MakeSkin(Rig, layers.Count == 1 ? layers[0].Name : "Colours", colorLayers: layers);
			return true;
		});
		return made;
	}

	// A new, empty skin, being edited from the start: changes to parts go into it, the object's
	// own (skin 0) staying as it was
	public Skin? MakeEmptySkin()
	{
		Skin? made = null;
		EditSkins("Add a skin", () =>
		{
			made = SkinOps.MakeSkin(Rig, "Skin");
			return true;
		});
		if (made != null) State.EditSkin = made;
		return made;
	}

	// The skin changes to parts go into (null: the object's own), shown if it wasn't
	public void EditSkin(Skin? skin)
	{
		State.EditSkin = skin;
		if (skin != null) ShowSkin(skin, true);
	}

	// ======================= a skin =======================

	public bool SetColourInSkin(Skin skin, ColorLayer layer, bool inIt) =>
		EditSkins(inIt ? $"Put {layer.Name} in the skin {skin.Name}" : $"Take {layer.Name} out of the skin {skin.Name}", () =>
			inIt ? SkinOps.AddColorLayers(Rig, skin, new[] { layer }) > 0 : SkinOps.TakeOutColorLayers(skin, new[] { layer }) > 0);

	// Deleted, its drawings back to the object's own
	public bool DissolveSkin(Skin skin) =>
		EditSkins($"Dissolve the skin {skin.Name}", () => { SkinOps.Dissolve(Rig, skin); return true; });

	// Deleted with its drawings (parts left with none go too)
	public bool DeleteSkin(Skin skin) =>
		EditSkins($"Delete the skin {skin.Name}", () => { SkinOps.Delete(Rig, State.Animations, skin); return true; });

	// Up (-1) or down (+1) the list: the later one wins where two that are on draw the same part
	public bool MoveSkin(Skin skin, int direction) =>
		EditSkins(direction < 0 ? $"Move the skin {skin.Name} up" : $"Move the skin {skin.Name} down", () => SkinOps.Move(Rig, skin, direction));

	// On or off (the others of its category go off)
	public bool ShowSkin(Skin skin, bool on) =>
		Rig.ShownSkins.Contains(skin) != on
		&& EditSkins(on ? $"Show the skin {skin.Name}" : $"Hide the skin {skin.Name}", () => { SkinOps.Show(Rig, skin, on); return true; });

	// Parts' own drawings into the skin
	public bool AddPartsToSkin(Skin skin, IReadOnlyList<Slot> parts) =>
		parts.Count > 0 && EditSkins($"Put {Describe(parts)} in the skin {skin.Name}", () => SkinOps.AddParts(Rig, skin, parts.ToList()) > 0);

	// The skin's drawings of these parts back to the object's own
	public bool TakePartsOutOfSkin(Skin skin, IReadOnlyList<Slot> parts) =>
		parts.Count > 0 && EditSkins($"Take {Describe(parts)} out of the skin {skin.Name}", () => SkinOps.TakeOutParts(Rig, skin, parts.ToList()) > 0);

	// Parts that show nothing while the skin is on (hide), or show again
	public bool HidePartsInSkin(Skin skin, IReadOnlyList<Slot> parts, bool hide) =>
		parts.Count > 0 && EditSkins(hide ? $"{skin.Name} hides {Describe(parts)}" : $"{skin.Name} shows {Describe(parts)} again",
			() => SkinOps.Hide(skin, parts, hide) > 0);

	// False: empty, or another skin's name
	public bool RenameSkin(Skin skin, string name)
	{
		name = name.Trim();
		return name.Length > 0 && name != skin.Name && SkinOps.IsFree(Rig, name, skin)
			&& EditSkins($"Rename the skin to {name}", () => SkinOps.Rename(Rig, skin, name));
	}

	// Its category ("" none: an outfit) and whether it's an overlay
	public bool SetSkinKind(Skin skin, string category, bool overlay)
	{
		category = category.Trim();
		if (category == skin.Category && overlay == skin.Overlay) return false;
		string what = overlay != skin.Overlay ? (overlay ? $"Make {skin.Name} an overlay" : $"Make {skin.Name} not an overlay")
			: category.Length == 0 ? $"Take {skin.Name} out of its category" : $"Put {skin.Name} in the category {category}";
		return EditSkins(what, () => { SkinOps.SetKind(Rig, skin, category, overlay); return true; });
	}

	// Every skin of a category moved to another name
	public bool RenameCategory(string category, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == category) return false;
		var skins = Rig.Skins.Where(s => SkinOps.SameCategory(s.Category, category)).ToList();
		return EditSkins($"Rename the category {category} to {name}", () =>
		{
			foreach (var skin in skins) SkinOps.SetKind(Rig, skin, name, skin.Overlay);
			return skins.Count > 0;
		});
	}

	// The list's groups: each category, then the outfits (no category), then the overlays in none
	public List<(string Title, string? Category, List<Skin> Skins)> SkinGroups()
	{
		var groups = new List<(string, string?, List<Skin>)>();
		foreach (var category in SkinOps.Categories(Rig))
			groups.Add((category, category, Rig.Skins.Where(s => SkinOps.SameCategory(s.Category, category)).ToList()));
		var outfits = Rig.Skins.Where(s => s.Category.Trim().Length == 0 && !s.Overlay).ToList();
		var overlays = Rig.Skins.Where(s => s.Category.Trim().Length == 0 && s.Overlay).ToList();
		if (outfits.Count > 0) groups.Add(("Outfits (one at a time)", null, outfits));
		if (overlays.Count > 0) groups.Add(("Overlays", null, overlays));
		return groups;
	}

	// ======================= painting in a skin =======================

	// Painting a part while editing a skin: the skin's own copy of its drawing, with pixels of its
	// own (made now, as an undo step). The pixels to open.
	public Asset PixelsToPaint(Slot slot, Asset asset)
	{
		if (State.EditSkin is not { } skin || Rig.ResolveAttachment(slot) is not { } shown || shown.AssetId != asset.Id) return asset;
		if (skin.Get(slot.Name, shown.Name) == shown && !SkinOps.SharesPixels(Rig, shown)) return asset;
		var result = asset;
		State.EditStructure(() =>
		{
			if (SkinOps.Own(Rig, skin, slot, shown.Name) is not { } own) return null;
			if (SkinOps.SharesPixels(Rig, own))
			{
				result = CopyOfPixels(asset);
				own.AssetId = result.Id;
			}
			return $"Paint {slot.Name} in the skin {skin.Name}";
		});
		return result;
	}

	// A picture with the same layers, pixels copied (in the open object)
	public Asset CopyOfPixels(Asset source)
	{
		var copy = new Asset { Name = source.Name, Width = source.Width, Height = source.Height };
		foreach (var layer in source.Layers)
		{
			var added = Assets.AddLayer(copy, layer.Name, Assets.GetLayer(layer));
			added.Visible = layer.Visible;
			added.Opacity = layer.Opacity;
			added.Adjust = layer.Adjust?.Clone();
		}
		Rig.Assets[copy.Id] = copy;
		return copy;
	}

	// ======================= names =======================

	// What the names of these parts start with (bucket, bucket_dent, bucket_wrecked → bucket)
	public static string? CommonName(IReadOnlyList<Slot> parts)
	{
		if (parts.Count == 0) return null;
		string prefix = parts[0].Name;
		foreach (var slot in parts.Skip(1))
		{
			int n = 0;
			while (n < prefix.Length && n < slot.Name.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(slot.Name[n])) n++;
			prefix = prefix[..n];
		}
		prefix = prefix.TrimEnd(' ', '_', '-', '.');
		return prefix.Length >= 2 ? prefix : null;
	}

	public static string Describe(IReadOnlyList<Slot> parts) => parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts";
}
