using Animator.Core.Editing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The Skins tab: drawings for some parts kept apart from the object's own, shown while the skin
// is on. Skins of a category are alternatives (one on at a time), skins of none are outfits (one
// at a time too), overlays show over anything. Made from the picked parts (or one part's
// drawings); the selected one renamed, given a category, its parts added, taken out or hidden,
// moved, deleted; ✎ edits one (changes to parts go into it). The edits are EditorSession's.
public sealed partial class MainWindow
{
	internal readonly StackPanel SkinList = new() { Spacing = 1 };
	internal readonly TextBox SkinName = new();
	internal readonly TextBox SkinCategory = new() { PlaceholderText = "(none: an outfit)" };
	internal readonly CheckBox SkinOverlay = new() { Content = "Overlay (shows over the others)" };
	private readonly TextBlock _skinHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };
	private readonly StackPanel _skinProps = new() { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
	internal Skin? PickedSkin { get; private set; }
	private bool _syncingSkins;

	private static readonly IBrush SkinRowBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x37));
	private static readonly IBrush SkinPickedBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0x9F));
	private static readonly IBrush SkinEditingBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x40));

	private Control BuildSkinsTab()
	{
		var make = new Button { Content = "+ Skin ▾" };
		make.Click += (_, _) => ShowMakeSkinMenu(make);

		Button B(string text, Action click)
		{
			var b = new Button { Content = text };
			b.Click += (_, _) => click();
			return b;
		}
		static WrapPanel Row(params Control[] controls)
		{
			var row = new WrapPanel();
			foreach (var c in controls) { c.Margin = new Thickness(0, 0, 4, 4); row.Children.Add(c); }
			return row;
		}

		var delete = new Button { Content = "Delete ▾" };
		delete.Click += (_, _) => ShowDeleteSkinMenu(delete);
		_skinProps.Children.Add(Labelled("Name", SkinName));
		_skinProps.Children.Add(Labelled("Category", SkinCategory));
		_skinProps.Children.Add(SkinOverlay);
		_skinProps.Children.Add(Row(B("Add the picked parts", AddPickedToSkin), B("Take them out", TakePickedOutOfSkin)));
		_skinProps.Children.Add(Row(B("Hide the picked", () => HidePickedInSkin(true)), B("Show them", () => HidePickedInSkin(false)), B("Pick its parts", PickSkinParts)));
		_skinProps.Children.Add(Row(B("▲", () => MoveSkin(-1)), B("▼", () => MoveSkin(+1)), delete));

		SkinName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSkinName(); Viewport.Focus(); } };
		SkinName.LostFocus += (_, _) => CommitSkinName();
		SkinCategory.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSkinKind(); Viewport.Focus(); } };
		SkinCategory.LostFocus += (_, _) => CommitSkinKind();
		SkinOverlay.IsCheckedChanged += (_, _) => CommitSkinKind();

		var panel = new StackPanel { Margin = new Thickness(6), Spacing = 4 };
		panel.Children.Add(make);
		panel.Children.Add(SkinList);
		panel.Children.Add(_skinProps);
		panel.Children.Add(_skinHint);
		return new ScrollViewer { Content = panel };
	}

	// ======================= making skins =======================

	private void ShowMakeSkinMenu(Control under)
	{
		if (SceneLevel) return;
		var parts = _session.PickedOwnParts();
		var menu = new ContextMenu();
		void Add(string header, bool enabled, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		string picked = parts.Count switch { 0 => "", 1 => $" ({parts[0].Name})", _ => $" ({parts.Count})" };
		Add($"The picked parts, as one skin{picked}", parts.Count > 0, () => MakeSkinFromParts(parts));
		Add($"Each picked part a skin, in a category…{picked}", parts.Count > 1, async () =>
		{
			if (await AskCategory(EditorSession.CommonName(parts) ?? parts[0].Name) is { } category)
				SelectSkin(_session.MakeCategoryFromParts(category, parts).FirstOrDefault(), $"{category}: skins of it are alternatives, one on at a time.");
		});
		var one = State.SelectedSlot;
		var rig = State.Skeleton;
		int drawings = one != null ? DrawingOps.DrawingNames(rig, one).Count(n => rig.DefaultSkin.Get(one.Name, n) != null) : 0;
		Add(one != null ? $"{one.Name}'s drawings, each a skin, in a category…" : "A part's drawings, each a skin, in a category…", drawings > 1, async () =>
		{
			if (await AskCategory(one!.Name) is { } category)
				SelectSkin(_session.MakeCategoryFromDrawings(one, category).FirstOrDefault(), $"{category}: a skin for each of {one.Name}'s drawings.");
		});
		Add($"All the colour layers, as one skin ({rig.ColorLayers.Count})", rig.ColorLayers.Count > 0,
			() => SelectSkin(_session.MakeSkinFromColours(rig.ColorLayers.ToList()), "The colour layers apply only while it's on."));
		menu.Items.Add(new Separator());
		Add("A new skin, to edit", true, () =>
		{
			var made = _session.MakeEmptySkin();
			SelectSkin(made, $"Editing {made?.Name}: paint a part, move it, give it new drawings or add parts, and that goes into it. ✎ on Skin 0 edits the object itself again.");
		});
		menu.Open(under);
	}

	internal void MakeSkinFromParts(IReadOnlyList<Slot> parts)
	{
		var made = _session.MakeSkinFromParts(parts);
		SelectSkin(made, $"{made?.Name}: {EditorSession.Describe(parts)} show while it's on. Give it a category to make it one of a set of alternatives.");
	}

	private Task<string?> AskCategory(string suggestion) => Dialogs.AskText(this, "Skin category",
		"The category's name (its skins are alternatives: one on at a time):", suggestion, t => t.Length == 0 ? "A name, like Bucket." : null);

	private void SelectSkin(Skin? skin, string hint)
	{
		if (skin != null) PickedSkin = skin;
		RefreshSkins();
		if (skin != null) _skinHint.Text = hint;
	}

	// ======================= the picked skin =======================

	internal void ShowSkin(Skin skin, bool on)
	{
		if (!_session.ShowSkin(skin, on)) return;
		PickedSkin = skin;
		RefreshSkins();
	}

	internal void EditSkin(Skin? skin)
	{
		_session.EditSkin(skin);
		PickedSkin = skin ?? PickedSkin;
		RefreshSkins();
		_skinHint.Text = skin == null
			? "Editing the object itself (skin 0): changes to parts change its own drawings."
			: $"Editing {skin.Name}: changes to parts go into it; skin 0 stays as it was.";
	}

	internal void AddPickedToSkin()
	{
		if (PickedSkin is not { } skin) return;
		var parts = _session.PickedOwnParts();
		if (parts.Count == 0) { _skinHint.Text = "Pick the parts to put in it: parts with drawings of their own, outside any skin."; return; }
		_session.AddPartsToSkin(skin, parts);
		SelectSkin(skin, $"{EditorSession.Describe(parts)} now show while {skin.Name} is on.");
	}

	private void TakePickedOutOfSkin()
	{
		if (PickedSkin is not { } skin) return;
		var parts = State.PickedSlots.Where(s => skin.AttachmentsFor(s.Name).Any()).ToList();
		if (parts.Count == 0) { _skinHint.Text = $"Pick parts of {skin.Name} to take them out."; return; }
		_session.TakePartsOutOfSkin(skin, parts);
		SelectSkin(skin, $"{EditorSession.Describe(parts)} show whatever skins are on again.");
	}

	// The picked parts show nothing while the skin is on (legs under a long skirt), or show again
	internal void HidePickedInSkin(bool hide)
	{
		if (PickedSkin is not { } skin) return;
		var parts = State.PickedSlots.Where(s => skin.Hides.Contains(s.Name) != hide).ToList();
		if (parts.Count == 0)
		{
			_skinHint.Text = hide ? $"Pick the parts {skin.Name} covers: they show nothing while it's on." : $"Pick parts {skin.Name} hides to show them again.";
			return;
		}
		_session.HidePartsInSkin(skin, parts, hide);
		SelectSkin(skin, hide ? $"{EditorSession.Describe(parts)} show nothing while {skin.Name} is on." : $"{EditorSession.Describe(parts)} show again with {skin.Name} on.");
	}

	private void PickSkinParts()
	{
		if (PickedSkin is not { } skin) return;
		var parts = SkinOps.PartsOf(State.Skeleton, skin);
		if (parts.Count > 0) State.Pick(parts, parts[^1]);
	}

	private void MoveSkin(int direction)
	{
		if (PickedSkin is { } skin) _session.MoveSkin(skin, direction);
		RefreshSkins();
	}

	private void ShowDeleteSkinMenu(Control under)
	{
		if (PickedSkin is not { } skin) return;
		var menu = new ContextMenu();
		var back = new MenuItem { Header = "Delete, its drawings back to the object" };
		back.Click += (_, _) => { _session.DissolveSkin(skin); PickedSkin = null; RefreshSkins(); };
		var gone = new MenuItem { Header = "Delete, with its drawings" };
		gone.Click += (_, _) => { _session.DeleteSkin(skin); PickedSkin = null; RefreshSkins(); };
		menu.Items.Add(back);
		menu.Items.Add(gone);
		menu.Open(under);
	}

	internal void CommitSkinName()
	{
		if (_syncingSkins || PickedSkin is not { } skin) return;
		string name = SkinName.Text?.Trim() ?? "";
		if (name.Length == 0 || name == skin.Name) return;
		if (!SkinOps.IsFree(State.Skeleton, name, skin))
		{
			_skinHint.Text = $"Another skin is called \"{name}\".";
			SkinName.Text = skin.Name;
			return;
		}
		_session.RenameSkin(skin, name);
		RefreshSkins();
	}

	internal void CommitSkinKind()
	{
		if (_syncingSkins || PickedSkin is not { } skin) return;
		if (_session.SetSkinKind(skin, SkinCategory.Text?.Trim() ?? "", SkinOverlay.IsChecked == true)) RefreshSkins();
	}

	// ======================= the list =======================

	private Control SkinRow(Skin? skin)
	{
		var rig = State.Skeleton;
		bool editing = State.EditSkin == skin;
		var row = new DockPanel();
		if (skin != null)
		{
			var tick = new CheckBox { IsChecked = rig.ShownSkins.Contains(skin), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
			tick.IsCheckedChanged += (_, _) => { if (!_syncingSkins) ShowSkin(skin, tick.IsChecked == true); };
			DockPanel.SetDock(tick, Dock.Left);
			row.Children.Add(tick);
		}
		var edit = new Button { Content = "✎", Padding = new Thickness(6, 0), FontWeight = editing ? FontWeight.Bold : FontWeight.Normal };
		ToolTip.SetTip(edit, editing ? "Being edited" : skin == null ? "Edit the object itself (skin 0)" : $"Edit {skin.Name}: changes to parts go into it");
		edit.Click += (_, e) => { EditSkin(skin); e.Handled = true; };
		DockPanel.SetDock(edit, Dock.Right);
		row.Children.Add(edit);
		string info = skin == null ? "the object's own drawings"
			: $"{(skin.Overlay ? "overlay · " : "")}{SkinOps.PartsOf(rig, skin).Count} part(s){(skin.ColorLayers.Count > 0 ? $" · {skin.ColorLayers.Count} colour" : "")}{(skin.Hides.Count > 0 ? $" · hides {skin.Hides.Count}" : "")}";
		row.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(skin == null ? 24 : 0, 0, 0, 0),
			Children =
			{
				new TextBlock { Text = skin?.Name ?? "Skin 0 · Default" },
				new TextBlock { Text = info, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis }
			}
		});
		var border = new Border
		{
			Child = row,
			Padding = new Thickness(4, 2),
			Background = skin != null && skin == PickedSkin ? SkinPickedBrush : SkinRowBrush,
			BorderBrush = editing ? SkinEditingBrush : null,
			BorderThickness = new Thickness(editing ? 1 : 0),
			Tag = skin
		};
		if (skin != null)
			border.PointerPressed += (_, e) =>
			{
				PickedSkin = skin;
				if (e.ClickCount == 2) ShowSkin(skin, !rig.ShownSkins.Contains(skin));
				else RefreshSkins();
			};
		return border;
	}

	// The list made again (an edit, an undo, another object)
	internal void RefreshSkins()
	{
		var rig = State.Skeleton;
		if (PickedSkin != null && !rig.Skins.Contains(PickedSkin)) PickedSkin = null;
		_syncingSkins = true;
		try
		{
			SkinList.Children.Clear();
			SkinList.Children.Add(SkinRow(null));
			foreach (var (title, category, skins) in _session.SkinGroups())
			{
				var header = new TextBlock { Text = title, Foreground = Brushes.Gray, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6, 0, 2) };
				if (category != null)
				{
					string c = category;
					header.DoubleTapped += async (_, _) =>
					{
						if (await Dialogs.AskText(this, "Rename the category", $"A new name for {c}:", c, t => t.Length == 0 ? "A name." : null) is { } name && name != c)
						{
							_session.RenameCategory(c, name);
							RefreshSkins();
						}
					};
					ToolTip.SetTip(header, "Double-click to rename the category");
				}
				SkinList.Children.Add(header);
				foreach (var skin in skins) SkinList.Children.Add(SkinRow(skin));
			}
			_skinProps.IsVisible = PickedSkin != null;
			if (PickedSkin is { } picked)
			{
				SkinName.Text = picked.Name;
				SkinCategory.Text = picked.Category;
				SkinOverlay.IsChecked = picked.Overlay;
			}
			if (rig.Skins.Count == 0)
				_skinHint.Text = "Pick parts (a hat, an outfit's pieces) and + Skin makes them a skin: they show while it's on.";
		}
		finally
		{
			_syncingSkins = false;
		}
	}
}
