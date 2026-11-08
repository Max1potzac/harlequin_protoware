using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// Building the rig, as the Windows editor does it (Parts, Rigging, Layers): new parts (blank or
// a picture), a drawn object given bones, parts put onto bones (the nearest, or a bone taking
// over a part's motion), the draw order (Ctrl+] / Ctrl+[, with Shift all the way; keyed in
// Animate) and clipping masks. The edits are EditorSession's.
public sealed partial class MainWindow
{
	// What the new part dialog and the picture picker are answered with (the tests answer them)
	internal Func<NewPartWindow, Task<bool>>? AnswerNewPart { get; set; }
	internal Func<Task<string?>>? AnswerPartPicture { get; set; }

	// ======================= new parts =======================

	private void ShowAddPartMenu(Control under)
	{
		var menu = new MenuFlyout();
		var blank = new MenuItem { Header = "Blank part…" };
		blank.Click += (_, _) => _ = NewBlankPart();
		var picture = new MenuItem { Header = "From a picture…" };
		picture.Click += (_, _) => _ = NewPartFromPicture();
		menu.Items.Add(blank);
		menu.Items.Add(picture);
		menu.ShowAt(under);
	}

	// The bone a new part goes on: the selected one, or a drawn object's one hidden bone
	private Bone? BoneForNewPart() => IsDrawn ? State.Skeleton.Roots.FirstOrDefault() : State.Selected;

	internal async Task NewBlankPart()
	{
		if (SceneLevel) return;
		if (BoneForNewPart() is not { } bone) { ShowRigHint("Pick the bone the part goes on first."); return; }
		var dialog = new NewPartWindow();
		bool ok = AnswerNewPart is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		var slot = _session.CreatePart(bone, dialog.PartName, null, dialog.PartWidth, dialog.PartHeight);
		PickPart(slot);
	}

	internal async Task NewPartFromPicture()
	{
		if (SceneLevel) return;
		if (BoneForNewPart() is not { } bone) { ShowRigHint("Pick the bone the part goes on first."); return; }
		string? file = AnswerPartPicture is { } answer ? await answer() : await PickPicture();
		if (file == null) return;
		using var bitmap = SKBitmap.Decode(file);
		if (bitmap == null)
		{
			await Dialogs.Tell(this, "New part", "That file couldn't be read as a picture.");
			return;
		}
		if (bitmap.Width > 4096 || bitmap.Height > 4096)
		{
			await Dialogs.Tell(this, "New part", "Pictures up to 4096 × 4096 can be parts.");
			return;
		}
		var slot = _session.CreatePart(bone, Path.GetFileNameWithoutExtension(file), bitmap, bitmap.Width, bitmap.Height);
		PickPart(slot);
	}

	private void PickPart(Slot slot)
	{
		State.SelectedSlot = slot;
		State.Selected = slot.Bone;
		RefreshRigList();
	}

	private async Task<string?> PickPicture()
	{
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "A picture for the new part",
			AllowMultiple = false,
			FileTypeFilter = new[] { new FilePickerFileType("Pictures (PNG, JPEG, BMP, WebP)") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } } }
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	// ======================= a drawn object gets bones =======================

	private void ShowGiveBonesMenu(Control under)
	{
		var menu = new MenuFlyout();
		var root = new MenuItem { Header = "Just a root bone: I'll draw the rest" };
		root.Click += (_, _) => GiveBones(suggest: false);
		var suggest = new MenuItem { Header = "Bones suggested from its parts (where they overlap)", IsEnabled = State.Skeleton.Slots.Count > 0 };
		suggest.Click += (_, _) => GiveBones(suggest: true);
		menu.Items.Add(root);
		menu.Items.Add(suggest);
		menu.ShowAt(under);
	}

	// The drawn object becomes a rigged one (see PartRigging.GiveBones): every part stays exactly
	// where it is
	internal void GiveBones(bool suggest)
	{
		if (!IsDrawn) return;
		ClosePaintEditor();
		var skeleton = State.Skeleton;
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		float height = _renderer.ImageBounds(skeleton) is { } box ? box.Height : 200f;
		if (_session.GiveBones(Math.Clamp(height * 0.12f, 30f, 200f), suggest) is not { } suggested) return;
		_tool.BonesEnabled = _settings.ShowBones;
		_layersView = false;
		RefreshRigList();
		ShowRigHint(suggest
			? $"{suggested} bone(s) suggested. Drag a joint to fit it (the parts stay put), drag parts onto bones in the list, or Bind ▾."
			: "Ctrl+drag from the root bone to draw bones, then Bind ▾ puts every part on its nearest bone.");
	}

	// ======================= parts onto bones =======================

	private void ShowBindMenu(Control under)
	{
		var menu = new MenuFlyout();
		void Item(string header, bool enabled, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		var skeleton = State.Skeleton;
		bool bones = skeleton.AllBones().Skip(1).Any();
		var roots = skeleton.Roots.ToHashSet();
		var onRoots = skeleton.Slots.Where(s => roots.Contains(s.Bone)).ToList();
		Item("Every part onto its nearest bone", bones && skeleton.Slots.Count > 0, () => PartsToNearestBones(skeleton.Slots.ToList(), "every part"));
		Item($"Parts still on the root bone onto their nearest bones ({onRoots.Count})", bones && onRoots.Count > 0,
			() => PartsToNearestBones(onRoots, "the parts on the root"));
		if (State.SelectedSlot is { } selected)
			Item($"\"{selected.Name}\" onto its nearest bone", bones, () => PartsToNearestBones(new List<Slot> { selected }, selected.Name));

		// A bone and a part picked together (Ctrl+click): the bone takes over the part's motion
		var pickedBones = State.PickedBones.ToList();
		var pickedParts = State.PickedSlots.ToList();
		bool pair = pickedBones.Count == 1 && pickedParts.Count == 1;
		Item(pair ? $"\"{pickedBones[0].Name}\" follows \"{pickedParts[0].Name}\": carries it, moving as it moved in every animation"
				  : "A bone follows a part (pick the two together): carries it, moving as it moved in every animation",
			pair, () => BoneFollowsPart(pickedBones[0], pickedParts[0]));
		menu.Items.Add(new Separator());
		Item("By hand: drag parts onto bones in the list", false, () => { });
		Item("By hand: a part's Bone in its properties", false, () => { });
		menu.ShowAt(under);
	}

	// Each part goes on the bone nearest to most of it (not a root bone, unless there's no other)
	internal void PartsToNearestBones(List<Slot> slots, string what)
	{
		int moved = _session.PartsToNearestBones(slots, what);
		RefreshRigList();
		ShowRigHint(moved > 0 ? $"{moved} part(s) moved onto other bones." : "Every part already was on its nearest bone.");
	}

	// A bone takes over a part's motion: the part goes on it, the bone gets keys that move it as
	// the part moved in every animation (for tracing a rig over animated art)
	private void BoneFollowsPart(Bone bone, Slot slot)
	{
		bool done = State.BoneFollowsPart(bone, slot);
		RefreshRigList();
		if (done) ShowRigHint($"{bone.Name} now carries {slot.Name}, moving as it moved in every animation.");
	}

	// ======================= the draw order and masks =======================

	// The parts the order keys move: the picked ones, or the selected one
	private List<Slot> PartsToOrder() =>
		State.PickedSlots.ToList() is { Count: > 0 } picked ? picked
		: State.SelectedSlot is { } one ? new List<Slot> { one } : new List<Slot>();

	// The picked parts a step toward the front (+1) or the back (-1), or all the way (Animate: a
	// key at the playhead)
	internal void ReorderPicked(int direction, bool toEnd)
	{
		if (SceneLevel || PartsToOrder() is not { Count: > 0 } parts) return;
		if (_session.Reorder(parts, direction, toEnd)) RefreshRigList();
	}

	// A part's place in the order and its clipping (the row's right-click, Order and mask ▾)
	internal MenuFlyout OrderMenu(Slot slot)
	{
		var menu = new MenuFlyout();
		var parts = DraggedAlong(slot);
		var order = _session.CurrentOrder();
		var at = parts.Select(p => IndexOf(order, p)).ToList();
		string many = parts.Count > 1 ? $" ({parts.Count} parts)" : "";
		void Order(string header, string command, int direction, bool toEnd)
		{
			bool can = direction > 0 ? at.Min() < order.Count - parts.Count : at.Max() >= parts.Count;
			var keys = _keys.KeysOf(command);
			var item = new MenuItem
			{
				Header = header + many,
				IsEnabled = can,
				InputGesture = keys.Count > 0 ? new KeyGesture(keys[0].Key, keys[0].Modifiers) : null
			};
			item.Click += (_, _) =>
			{
				if (parts.Count == 1) PickPart(slot);
				if (_session.Reorder(parts, direction, toEnd)) RefreshRigList();
			};
			menu.Items.Add(item);
		}
		Order("Bring to front", "order.front", +1, true);
		Order("Bring forward", "order.forward", +1, false);
		Order("Send backward", "order.backward", -1, false);
		Order("Send to back", "order.back", -1, true);
		menu.Items.Add(new Separator());
		menu.Items.Add(MaskMenu(slot, order));
		return menu;
	}

	private static int IndexOf(IReadOnlyList<Slot> order, Slot slot)
	{
		for (int i = 0; i < order.Count; i++)
			if (order[i] == slot) return i;
		return -1;
	}

	// The part's clipping: a mask for the parts above it (all, or up to one), or changed
	private MenuItem MaskMenu(Slot slot, IReadOnlyList<Slot> order)
	{
		int at = IndexOf(order, slot);
		var above = order.Skip(at + 1).ToList();
		var menu = new MenuItem
		{
			Header = slot.IsMask ? $"Clipping mask ({MaskOps.Clipped(State.Skeleton, slot).Count} part(s))" : "Clip the parts above it",
			IsEnabled = (slot.IsMask || above.Count > 0) && !State.AnimateMode
		};
		if (State.AnimateMode) ToolTip.SetTip(menu, "Masks are part of the object: set them in Build");
		void Item(string header, bool check, Func<bool> change)
		{
			var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = check };
			item.Click += (_, _) =>
			{
				change();
				RefreshRigList();
			};
			menu.Items.Add(item);
		}
		Item($"All the parts above it ({above.Count})", slot.IsMask && slot.MaskUntil == null, () => _session.MakeMask(slot, null));
		foreach (var until in above)
			Item($"Up to {until.Name} ({IndexOf(order, until) - at})", slot.IsMask && slot.MaskUntil == until, () => _session.MakeMask(slot, until));
		if (slot.IsMask)
		{
			menu.Items.Add(new Separator());
			Item("The mask shows too", slot.MaskShows, () => _session.SetMaskShows(slot, !slot.MaskShows));
			Item("Stop clipping", false, () => _session.StopMask(slot));
		}
		return menu;
	}
}

// A blank part: its name and size
public sealed class NewPartWindow : Window
{
	internal readonly TextBox NameBox = new() { Text = "part" };
	internal readonly TextBox WidthBox = new() { Text = "64", Width = 70 };
	internal readonly TextBox HeightBox = new() { Text = "64", Width = 70 };
	internal readonly TextBlock ErrorText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };

	public string PartName { get; private set; } = "part";
	public int PartWidth { get; private set; } = 64;
	public int PartHeight { get; private set; } = 64;

	public NewPartWindow()
	{
		Title = "New part";
		Width = 340;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		static TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Words("Size"), WidthBox, Words("×"), HeightBox, Words("pixels") } };
		var ok = new Button { Content = "Make it", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		Content = new StackPanel
		{
			Margin = new Thickness(14),
			Spacing = 8,
			Children =
			{
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Words("Name"), NameBox } },
				size,
				new TextBlock { Text = "A see-through picture to paint on (Paint, or double-click it).", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 },
				ErrorText,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
			}
		};
		NameBox.Width = 220;
		Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
		UiSize.Fit(this);
	}

	internal void TryOk()
	{
		if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h) || w < 1 || h < 1 || w > 4096 || h > 4096)
		{
			ErrorText.Text = "The width and height are whole numbers from 1 to 4096.";
			return;
		}
		PartName = string.IsNullOrWhiteSpace(NameBox.Text) ? "part" : NameBox.Text.Trim();
		PartWidth = w;
		PartHeight = h;
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
