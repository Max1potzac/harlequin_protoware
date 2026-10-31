using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

// Skins (the rig panel's Skins view): drawings for one or several parts kept apart from the
// object's own, shown when the skin is on. Skins in a category are alternatives, one on at a time
// (a "Bucket" category: the bucket, dented, wrecked); skins in no category are outfits, one at a
// time too; overlays show over anything, as many as are on. Picked parts become a skin, each
// picked part a skin of a category, or one part's drawings a category. Undoable; saved with the
// object, with the ones that are on.
public partial class MainWindow
{
	private bool _skinsView;
	private Skin? _skin;   // the one selected in the list
	private bool _syncingSkins;

	private static readonly Brush SkinRowBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2E));
	private static readonly Brush SkinPickedBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x6E));
	private static readonly Brush SkinEditingBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x2A));
	private static readonly Brush SkinHeaderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2));

	private void EditSkins(string name, Func<bool> change)
	{
		if (_sceneLevel) return;
		_state.EditStructure(() => change() ? name : null);
		_skeleton.UpdateWorld();
	}

	// The picked parts with drawings of their own (in draw order)
	private List<Slot> PickedOwnParts() =>
		_skeleton.Slots.Where(s => _state.Picked.Contains(s) && SkinOps.HasOwnDrawings(_skeleton, s)).ToList();

	// ======================= making skins =======================

	private void SkinAdd_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) return;
		var parts = PickedOwnParts();
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Add(string header, bool enabled, string tip, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
			ToolTipService.SetShowOnDisabled(item, true);
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		string picked = parts.Count switch { 0 => "", 1 => $" ({parts[0].Name})", _ => $" ({parts.Count})" };
		Add($"The picked parts, as one skin{picked}", parts.Count > 0,
			"Their drawings go into a new skin: they show while it's on (a hat, a bucket and its handle…)", () => MakeSkinFromParts(parts));
		Add($"Each picked part a skin, in a category…{picked}", parts.Count > 1,
			"Layers drawn as alternatives (the bucket, the dented bucket, the wrecked one): each becomes a skin, one on at a time", () => MakeCategoryFromParts(parts));
		var one = _state.SelectedSlot;
		int drawings = one != null ? DrawingOps.DrawingNames(_skeleton, one).Count(n => _skeleton.DefaultSkin.Get(one.Name, n) != null) : 0;
		Add(one != null ? $"{one.Name}'s drawings, each a skin, in a category…" : "A part's drawings, each a skin, in a category…", drawings > 1,
			"A part that swaps between drawings (frame by frame): each drawing becomes a skin of one category, and the skin on says which it shows", () => MakeCategoryFromDrawings(one!));
		// Colour layers: one, or all of them, as a skin (more go in with the skin's Colour layers list)
		var colours = new MenuItem { Header = "A colour layer, as a skin", IsEnabled = _skeleton.ColorLayers.Count > 0,
			ToolTip = "It applies only while the skin is on (a red glow while hurt, say). Tick more in the skin's Colour layers list." };
		ToolTipService.SetShowOnDisabled(colours, true);
		foreach (var layer in _skeleton.ColorLayers)
		{
			var item = new MenuItem { Header = _skeleton.SkinOf(layer) is { } holder ? $"{layer.Name}   (now in {holder.Name})" : layer.Name };
			item.Click += (_, _) => MakeSkinFromColours(new[] { layer });
			colours.Items.Add(item);
		}
		menu.Items.Add(colours);
		Add($"All the colour layers, as one skin ({_skeleton.ColorLayers.Count})", _skeleton.ColorLayers.Count > 1,
			"Every colour layer of the object, applying only while the skin is on", () => MakeSkinFromColours(_skeleton.ColorLayers.ToList()));
		menu.Items.Add(new Separator());
		Add("A new skin, to edit", true, "A skin edited from the start: paint a part, place it, give it new drawings or add parts, and that goes into the skin (the object's own, skin 0, stays as it was). Picked parts can be added too.", () => MakeEmptySkin());
		menu.IsOpen = true;
	}

	private void MakeSkinFromParts(List<Slot> parts)
	{
		Skin? made = null;
		EditSkins($"Make a skin of {Describe(parts)}", () =>
		{
			made = SkinOps.MakeSkin(_skeleton, parts.Count == 1 ? parts[0].Name : CommonName(parts) ?? "Skin", parts);
			return true;
		});
		SelectSkin(made, $"{made?.Name}: {Describe(parts)} show while it's on. Give it a category to make it one of a set of alternatives, or make it an overlay to show it over the others.");
	}

	private void MakeCategoryFromParts(List<Slot> parts)
	{
		if (AskCategory(CommonName(parts) ?? parts[0].Name) is not { } category) return;
		List<Skin> made = new();
		EditSkins($"Make a category {category} of {parts.Count} parts", () =>
		{
			made = SkinOps.MakeCategoryFromParts(_skeleton, category, parts);
			return made.Count > 0;
		});
		SelectSkin(made.FirstOrDefault(), $"{category}: {made.Count} skins, one on at a time (tick another to swap).");
	}

	private void MakeCategoryFromDrawings(Slot slot)
	{
		if (AskCategory(slot.Name) is not { } category) return;
		List<Skin> made = new();
		EditSkins($"Make a category {category} of {slot.Name}'s drawings", () =>
		{
			made = SkinOps.MakeCategoryFromDrawings(_skeleton, _state.Animations, slot, category);
			return made.Count > 0;
		});
		SelectSkin(made.FirstOrDefault(), $"{category}: {made.Count} skins for {slot.Name}, one on at a time. Its drawing keys show the skin's drawing now.");
	}

	private void MakeSkinFromColours(IReadOnlyList<ColorLayer> layers)
	{
		Skin? made = null;
		string what = layers.Count == 1 ? layers[0].Name : $"{layers.Count} colour layers";
		EditSkins($"Make a skin of {what}", () =>
		{
			made = SkinOps.MakeSkin(_skeleton, layers.Count == 1 ? layers[0].Name : "Colours", colorLayers: layers);
			return true;
		});
		SelectSkin(made, $"{made?.Name}: {what} apply only while it's on (exported too, when colour layers are baked).");
	}

	private void SkinColour_Click(ColorLayer layer, bool inIt)
	{
		if (_skin is not { } skin) return;
		EditSkins(inIt ? $"Put {layer.Name} in the skin {skin.Name}" : $"Take {layer.Name} out of the skin {skin.Name}", () =>
			inIt ? SkinOps.AddColorLayers(_skeleton, skin, new[] { layer }) > 0 : SkinOps.TakeOutColorLayers(skin, new[] { layer }) > 0);
		BuildSkinsPanel();
	}

	// A new skin, being edited from the start: paint or place a part and that part's new look is
	// the skin's, the object's own (skin 0) staying as it was
	private void MakeEmptySkin()
	{
		Skin? made = null;
		EditSkins("Add a skin", () =>
		{
			made = SkinOps.MakeSkin(_skeleton, "Skin");
			return true;
		});
		_state.EditSkin = made;
		SelectSkin(made, $"Editing {made?.Name}: paint a part, move it, give it new drawings or add parts, and that goes into {made?.Name}. ✎ on Skin 0 to edit the object itself again.");
	}

	private void EditSkin(Skin? skin)
	{
		_state.EditSkin = skin;
		if (skin != null && !_skeleton.ShownSkins.Contains(skin)) ShowSkin(skin, true);
		_skin = skin ?? _skin;
		BuildSkinsPanel();
		SkinHint.Text = skin == null
			? "Editing the object itself (skin 0): changes to parts change its own drawings."
			: $"Editing {skin.Name}: changes to parts (painting, placing, new drawings, new parts) go into it; skin 0 stays as it was.";
	}

	// The ✎ button on a row: edit this skin (or skin 0)
	private Button EditButton(Skin? skin)
	{
		bool editing = _state.EditSkin == skin;
		var button = new Button
		{
			Content = "✎",
			Padding = new Thickness(4, 0, 4, 0),
			Margin = new Thickness(4, 0, 0, 0),
			FontWeight = editing ? FontWeights.Bold : FontWeights.Normal,
			Background = editing ? SkinEditingBrush : null,
			ToolTip = editing ? "Being edited" : skin == null ? "Edit the object itself (skin 0)" : $"Edit {skin.Name}: changes to parts go into it"
		};
		button.Click += (_, e) => { EditSkin(skin); e.Handled = true; };
		return button;
	}

	// Painting a part while editing a skin: the skin's own copy, with pixels of its own (made now,
	// undoably). The pixels to open.
	private Asset PixelsToPaint(Slot slot, Asset asset)
	{
		if (_state.EditSkin is not { } skin || _skeleton.ResolveAttachment(slot) is not { } shown || shown.AssetId != asset.Id) return asset;
		if (skin.Get(slot.Name, shown.Name) == shown && !SkinOps.SharesPixels(_skeleton, shown)) return asset;
		var result = asset;
		_state.EditStructure(() =>
		{
			if (SkinOps.Own(_skeleton, skin, slot, shown.Name) is not { } own) return null;
			if (SkinOps.SharesPixels(_skeleton, own))
			{
				result = CopyOfPixels(asset);
				own.AssetId = result.Id;
			}
			return $"Paint {slot.Name} in the skin {skin.Name}";
		});
		return result;
	}

	private Asset CopyOfPixels(Asset source)
	{
		var copy = new Asset { Name = source.Name, Width = source.Width, Height = source.Height };
		foreach (var layer in source.Layers)
		{
			var added = _assets.AddLayer(copy, layer.Name, _assets.GetLayer(layer));
			added.Visible = layer.Visible;
			added.Opacity = layer.Opacity;
			added.Adjust = layer.Adjust?.Clone();
		}
		_skeleton.Assets[copy.Id] = copy;
		return copy;
	}

	private string? AskCategory(string suggestion)
	{
		var dialog = new InputDialog("Skin category", "The category's name (its skins are alternatives: one on at a time):", suggestion,
			text => text.Trim().Length == 0 ? "A name, like Bucket." : null) { Owner = this };
		return dialog.ShowDialog() == true ? dialog.Answer.Trim() : null;
	}

	// What the names of these parts start with (bucket, bucket_dent, bucket_wrecked → bucket)
	private static string? CommonName(IReadOnlyList<Slot> parts)
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

	private static string Describe(IReadOnlyList<Slot> parts) =>
		parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts";

	private void SelectSkin(Skin? skin, string hint)
	{
		if (skin != null) _skin = skin;
		BuildSkinsPanel();
		if (skin != null) SkinHint.Text = hint;
	}

	// ======================= the selected skin =======================

	private void SkinDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_skin is not { } skin) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		var back = new MenuItem { Header = "Delete, its drawings back to the object", ToolTip = "Its parts show again whatever skins are on (as they did before they were made a skin)" };
		back.Click += (_, _) =>
		{
			EditSkins($"Dissolve the skin {skin.Name}", () => { SkinOps.Dissolve(_skeleton, skin); return true; });
			_skin = null;
			BuildSkinsPanel();
		};
		var gone = new MenuItem { Header = "Delete, with its drawings", ToolTip = "Parts left with no drawings at all go too" };
		gone.Click += (_, _) =>
		{
			EditSkins($"Delete the skin {skin.Name}", () => { SkinOps.Delete(_skeleton, _state.Animations, skin); return true; });
			_skin = null;
			RefreshRigPanel();
		};
		menu.Items.Add(back);
		menu.Items.Add(gone);
		menu.IsOpen = true;
	}

	private void SkinUp_Click(object sender, RoutedEventArgs e) => MoveSkin(-1);
	private void SkinDown_Click(object sender, RoutedEventArgs e) => MoveSkin(+1);

	private void MoveSkin(int direction)
	{
		if (_skin is not { } skin) return;
		EditSkins(direction < 0 ? $"Move the skin {skin.Name} up" : $"Move the skin {skin.Name} down", () => SkinOps.Move(_skeleton, skin, direction));
		BuildSkinsPanel();
	}

	private void ShowSkin(Skin skin, bool on)
	{
		if (_skeleton.ShownSkins.Contains(skin) == on) return;
		EditSkins(on ? $"Show the skin {skin.Name}" : $"Hide the skin {skin.Name}", () => { SkinOps.Show(_skeleton, skin, on); return true; });
		_skin = skin;
		BuildSkinsPanel();
	}

	private void SkinAddParts_Click(object sender, RoutedEventArgs e)
	{
		if (_skin is not { } skin) return;
		var parts = PickedOwnParts();
		if (parts.Count == 0)
		{
			SkinHint.Text = "Pick the parts to put in it (in the viewport, or in the Bones or Layers view): parts with drawings of their own, outside any skin.";
			return;
		}
		EditSkins($"Put {Describe(parts)} in the skin {skin.Name}", () => SkinOps.AddParts(_skeleton, skin, parts) > 0);
		SelectSkin(skin, $"{Describe(parts)} now show while {skin.Name} is on.");
	}

	private void SkinTakeOut_Click(object sender, RoutedEventArgs e)
	{
		if (_skin is not { } skin) return;
		var parts = _state.PickedSlots.Where(s => skin.AttachmentsFor(s.Name).Any()).ToList();
		if (parts.Count == 0)
		{
			SkinHint.Text = $"Pick parts of {skin.Name} to take them out (back to the object's own drawings).";
			return;
		}
		EditSkins($"Take {Describe(parts)} out of the skin {skin.Name}", () => SkinOps.TakeOutParts(_skeleton, skin, parts) > 0);
		SelectSkin(skin, $"{Describe(parts)} show whatever skins are on again.");
	}

	private void SkinPickParts_Click(object sender, RoutedEventArgs e)
	{
		if (_skin is not { } skin) return;
		var parts = SkinOps.PartsOf(_skeleton, skin);
		if (parts.Count == 0) return;
		_state.Pick(parts, parts[^1]);
		RefreshRigPanel();
	}

	private void SkinName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitSkinName();
		Viewport.Focus();
	}

	private void SkinName_LostFocus(object sender, RoutedEventArgs e) => CommitSkinName();

	private void CommitSkinName()
	{
		if (_syncingSkins || _skin is not { } skin) return;
		string name = SkinName.Text.Trim();
		if (name.Length == 0 || name == skin.Name) return;
		if (!SkinOps.IsFree(_skeleton, name, skin))
		{
			SkinHint.Text = $"Another skin is called \"{name}\".";
			SkinName.Text = skin.Name;
			return;
		}
		EditSkins($"Rename the skin to {name}", () => SkinOps.Rename(_skeleton, skin, name));
		BuildSkinsPanel();
	}

	private void SkinCategory_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitSkinKind();
		Viewport.Focus();
	}

	private void SkinCategory_LostFocus(object sender, RoutedEventArgs e) => CommitSkinKind();

	private void SkinCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingSkins || SkinCategory.SelectedItem is not string chosen) return;
		SkinCategory.Text = chosen;
		CommitSkinKind();
	}

	private void SkinOverlay_Click(object sender, RoutedEventArgs e) => CommitSkinKind();

	private void CommitSkinKind()
	{
		if (_syncingSkins || _skin is not { } skin) return;
		string category = SkinCategory.Text.Trim();
		bool overlay = SkinOverlay.IsChecked == true;
		if (category == skin.Category && overlay == skin.Overlay) return;
		string what = overlay != skin.Overlay ? (overlay ? $"Make {skin.Name} an overlay" : $"Make {skin.Name} not an overlay")
			: category.Length == 0 ? $"Take {skin.Name} out of its category" : $"Put {skin.Name} in the category {category}";
		EditSkins(what, () => { SkinOps.SetKind(_skeleton, skin, category, overlay); return true; });
		BuildSkinsPanel();
	}

	private void RenameCategory(string category)
	{
		var dialog = new InputDialog("Rename the category", $"A new name for {category}:", category,
			text => text.Trim().Length == 0 ? "A name." : null) { Owner = this };
		if (dialog.ShowDialog() != true || dialog.Answer.Trim() == category) return;
		string name = dialog.Answer.Trim();
		var skins = _skeleton.Skins.Where(s => SkinOps.SameCategory(s.Category, category)).ToList();
		EditSkins($"Rename the category {category} to {name}", () =>
		{
			foreach (var skin in skins) SkinOps.SetKind(_skeleton, skin, name, skin.Overlay);
			return skins.Count > 0;
		});
		BuildSkinsPanel();
	}

	// ======================= the list =======================

	// The list's groups: each category, then the outfits (no category), then the overlays in none
	private List<(string Title, string? Category, List<Skin> Skins)> SkinGroups()
	{
		var groups = new List<(string, string?, List<Skin>)>();
		foreach (var category in SkinOps.Categories(_skeleton))
			groups.Add((category, category, _skeleton.Skins.Where(s => SkinOps.SameCategory(s.Category, category)).ToList()));
		var outfits = _skeleton.Skins.Where(s => s.Category.Trim().Length == 0 && !s.Overlay).ToList();
		var overlays = _skeleton.Skins.Where(s => s.Category.Trim().Length == 0 && s.Overlay).ToList();
		if (outfits.Count > 0) groups.Add(("Outfits (one at a time)", null, outfits));
		if (overlays.Count > 0) groups.Add(("Overlays", null, overlays));
		return groups;
	}

	private FrameworkElement SkinRow(Skin skin)
	{
		bool on = _skeleton.ShownSkins.Contains(skin);
		var tick = new CheckBox
		{
			IsChecked = on,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(2, 0, 6, 0),
			ToolTip = skin.Overlay ? "On: shows over the other skins" : "On: shows instead of the others of its category"
		};
		tick.Click += (_, _) => ShowSkin(skin, tick.IsChecked == true);
		int parts = SkinOps.PartsOf(_skeleton, skin).Count, colours = skin.ColorLayers.Count;
		var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AdjustEditor.LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis };
		label.Inlines.Add(new System.Windows.Documents.Run(skin.Name));
		label.Inlines.Add(new System.Windows.Documents.Run($"   {(skin.Overlay ? "overlay · " : "")}{parts} part{(parts == 1 ? "" : "s")}{(colours > 0 ? $" · {colours} colour" : "")}") { Foreground = SkinHeaderBrush });
		var row = new DockPanel();
		DockPanel.SetDock(tick, Dock.Left);
		row.Children.Add(tick);
		var edit = EditButton(skin);
		DockPanel.SetDock(edit, Dock.Right);
		row.Children.Add(edit);
		row.Children.Add(label);
		var border = new Border
		{
			Child = row,
			Padding = new Thickness(4, 2, 4, 2),
			Margin = new Thickness(0, 0, 0, 1),
			Background = skin == _skin ? SkinPickedBrush : SkinRowBrush,
			BorderBrush = _state.EditSkin == skin ? SkinEditingBrush : null,
			BorderThickness = new Thickness(_state.EditSkin == skin ? 1 : 0),
			Tag = skin
		};
		border.MouseLeftButtonDown += (_, e) =>
		{
			_skin = skin;
			if (e.ClickCount == 2) ShowSkin(skin, !_skeleton.ShownSkins.Contains(skin));
			BuildSkinsPanel();
		};
		return border;
	}

	// Skin 0: the object's own drawings, how it looks by default (always there, always on)
	private FrameworkElement DefaultSkinRow()
	{
		var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = AdjustEditor.LabelBrush, Margin = new Thickness(22, 0, 0, 0) };
		label.Inlines.Add(new System.Windows.Documents.Run("Skin 0 · Default"));
		label.Inlines.Add(new System.Windows.Documents.Run("   the object's own drawings") { Foreground = SkinHeaderBrush });
		var row = new DockPanel();
		var edit = EditButton(null);
		DockPanel.SetDock(edit, Dock.Right);
		row.Children.Add(edit);
		row.Children.Add(label);
		bool editing = _state.EditSkin == null;
		return new Border
		{
			Child = row,
			Padding = new Thickness(4, 2, 4, 2),
			Margin = new Thickness(0, 0, 0, 1),
			Background = SkinRowBrush,
			BorderBrush = editing ? SkinEditingBrush : null,
			BorderThickness = new Thickness(editing ? 1 : 0),
			ToolTip = "How the object looks by default: what shows where no skin that's on has a drawing of its own"
		};
	}

	private void BuildSkinsPanel()
	{
		if (!_skinsView) return;
		if (_skin != null && !_skeleton.Skins.Contains(_skin)) _skin = null;
		_syncingSkins = true;
		SkinList.Children.Clear();
		SkinList.Children.Add(DefaultSkinRow());
		foreach (var (title, category, skins) in SkinGroups())
		{
			var header = new TextBlock { Text = title, Foreground = SkinHeaderBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 2) };
			if (category != null)
			{
				header.ToolTip = "One on at a time. Right-click to rename the category.";
				var menu = new ContextMenu();
				var rename = new MenuItem { Header = "Rename the category…" };
				rename.Click += (_, _) => RenameCategory(category);
				menu.Items.Add(rename);
				header.ContextMenu = menu;
			}
			SkinList.Children.Add(header);
			foreach (var skin in skins) SkinList.Children.Add(SkinRow(skin));
		}

		bool any = _skin != null;
		SkinSettings.IsEnabled = SkinDeleteButton.IsEnabled = SkinUpButton.IsEnabled = SkinDownButton.IsEnabled = any;
		if (!SkinName.IsKeyboardFocused) SkinName.Text = _skin?.Name ?? "";
		SkinCategory.Items.Clear();
		foreach (var category in SkinOps.Categories(_skeleton)) SkinCategory.Items.Add(category);
		if (!SkinCategory.IsKeyboardFocusWithin) SkinCategory.Text = _skin?.Category ?? "";
		SkinOverlay.IsChecked = _skin?.Overlay == true;
		SkinParts.Text = _skin == null ? "" : SkinOps.PartsOf(_skeleton, _skin) is { Count: > 0 } parts
			? "Parts: " + string.Join(", ", parts.Select(p => p.Name)) : "No parts yet: pick some, then Add the picked.";

		// The object's colour layers: ticked = in this skin (applying only while it's on)
		SkinColours.Children.Clear();
		foreach (var layer in _skeleton.ColorLayers.AsEnumerable().Reverse())
		{
			var holder = _skeleton.SkinOf(layer);
			var box = new CheckBox
			{
				Content = holder != null && holder != _skin ? $"{layer.Name}   (in {holder.Name})" : layer.Name,
				IsChecked = _skin != null && holder == _skin,
				Foreground = AdjustEditor.LabelBrush,
				Margin = new Thickness(0, 1, 0, 1),
				ToolTip = holder != null && holder != _skin ? $"Ticking moves it here, out of {holder.Name}" : null
			};
			var target = layer;
			box.Click += (_, _) => SkinColour_Click(target, box.IsChecked == true);
			SkinColours.Children.Add(box);
		}
		SkinColoursBox.Visibility = _skeleton.ColorLayers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		_syncingSkins = false;

		SkinHint.Text = _skeleton.Skins.Count == 0
			? "Pick parts and + Skin: they show only while their skin is on. A category holds alternatives, one on at a time (a bucket, the bucket dented, wrecked); an overlay shows over whatever else is on. Colour layers can be in a skin too."
			: "Tick a skin to put it on. Where two that are on have a drawing for the same part, an overlay wins, then the one lower in the list.";
	}
}
