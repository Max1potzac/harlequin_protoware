using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using SkiaSharp;

namespace Animator.Viewer;

public partial class MainWindow
{
	private Slot? _shownSlot;

	private (Attachment? Att, Asset? Asset) PartData(Slot? slot)
	{
		if (slot == null || _skeleton.ResolveAttachment(slot) is not { } att) return (null, null);
		return (att, _skeleton.Assets.TryGetValue(att.AssetId, out var asset) ? asset : null);
	}

	// ======================= panel =======================

	private void ShowPartProperties()
	{
		_shownSlot = _state.SelectedSlot;
		if (_shownSlot is not { } slot) return;
		var (att, asset) = PartData(slot);
		int index = _skeleton.Slots.IndexOf(slot);
		_syncingRig = true;

		string drawing = att != null && att.Name != slot.Name ? $", showing \"{att.Name}\"" : "";
		string size = asset != null ? $", {asset.Width}×{asset.Height} px" : "";
		PartTitle.Text = $"Part {index + 1} of {_skeleton.Slots.Count} in draw order{size}{drawing}";

		SetField(PartName, slot.Name);
		var bones = _skeleton.AllBones().Select(b => new BoneChoice(b, b.Name)).ToList();
		PartBone.ItemsSource = bones;
		PartBone.SelectedItem = bones.FirstOrDefault(c => c.Bone == slot.Bone);

		PartFront.IsEnabled = index < _skeleton.Slots.Count - 1;
		PartBack.IsEnabled = index > 0;
		EditPixelsButton.IsEnabled = asset != null;

		_syncingRig = false;
		RefreshPartValues();
	}

	// The numbers only; runs every frame, so drags show live
	private void RefreshPartValues()
	{
		var (att, asset) = PartData(_shownSlot);
		foreach (var box in new[] { PartX, PartY, PartRotation, PartScaleX, PartScaleY, PartPivotX, PartPivotY })
			SetField(box, att != null ? Num(PartValue(att, asset, (string)box.Tag)) : "");
	}

	private static float? PartValue(Attachment att, Asset? asset, string field) => field switch
	{
		"PX" => att.Offset.X,
		"PY" => att.Offset.Y,
		"PRotation" => Deg(att.Rotation),
		"PScaleX" => att.Scale.X,
		"PScaleY" => att.Scale.Y,
		"PPivotX" => asset != null ? att.Pivot.X * asset.Width : null,
		"PPivotY" => asset != null ? att.Pivot.Y * asset.Height : null,
		_ => null
	};

	private void PartField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitPartField((TextBox)sender);
		Viewport.Focus();
	}

	private void PartField_LostFocus(object sender, RoutedEventArgs e) => CommitPartField((TextBox)sender);

	private void CommitPartField(TextBox box)
	{
		if (_syncingRig || _shownSlot is not { } slot) return;
		string field = (string)box.Tag;
		PartHint.Text = "";

		if (field == "PName")
		{
			string newName = box.Text.Trim();
			if (newName == slot.Name) return;
			bool renamed = _state.EditStructure(() =>
				PartOps.RenamePart(_skeleton, _state.Animations, slot, newName) ? $"Rename part to {newName}" : null);
			if (!renamed)
			{
				PartHint.Text = $"Can't rename to \"{newName}\": part names must be unique and not empty.";
				box.Text = slot.Name;
			}
			return;
		}

		var (att, asset) = PartData(slot);
		if (att == null) return;
		var current = PartValue(att, asset, field);
		if (current == null || box.Text.Trim() == Num(current)) return;

		if (!TryNum(box.Text, out float v))
		{
			PartHint.Text = $"\"{box.Text}\" isn't a number.";
			box.Text = Num(current);
			return;
		}

		_state.EditStructure(() =>
		{
			switch (field)
			{
				case "PX": att.Offset = new Vector2(v, att.Offset.Y); break;
				case "PY": att.Offset = new Vector2(att.Offset.X, v); break;
				case "PRotation": att.Rotation = Rad(v); break;
				case "PScaleX": att.Scale = new Vector2(v, att.Scale.Y); break;
				case "PScaleY": att.Scale = new Vector2(att.Scale.X, v); break;
				case "PPivotX" when asset != null:
					PartOps.SetPivotKeepingPlace(att, asset.Width, asset.Height,
						new Vector2(v, att.Pivot.Y * asset.Height));
					break;
				case "PPivotY" when asset != null:
					PartOps.SetPivotKeepingPlace(att, asset.Width, asset.Height,
						new Vector2(att.Pivot.X * asset.Width, v));
					break;
				default: return null;
			}
			return $"Set part {slot.Name} {field[1..]}";   // [1..] drops the "P" prefix
		});
	}

	private void PartBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownSlot is not { } slot || PartBone.SelectedItem is not BoneChoice { Bone: { } bone }) return;
		_state.EditStructure(() =>
		{
			if (!PartOps.MoveToBone(_skeleton, slot, bone)) return null;
			_state.Selected = bone;
			return $"Move part {slot.Name} to {bone.Name}";
		});
	}

	private void PartFront_Click(object sender, RoutedEventArgs e) => MovePartInOrder(+1);
	private void PartBack_Click(object sender, RoutedEventArgs e) => MovePartInOrder(-1);

	private void MovePartInOrder(int direction)
	{
		if (_state.SelectedSlot is not { } slot) return;
		_state.EditStructure(() => PartOps.MoveInDrawOrder(_skeleton, slot, direction)
			? $"{slot.Name} {(direction > 0 ? "forward" : "backward")}" : null);
	}

	private void EditPixels_Click(object sender, RoutedEventArgs e)
	{
		if (PartData(_state.SelectedSlot).Asset is { } asset) OpenPaintEditor(asset);
	}

	// ======================= creating & deleting =======================

	private void AddPart_Click(object sender, RoutedEventArgs e)
	{
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

		var blank = new MenuItem { Header = "Blank part…" };
		blank.Click += (_, _) => NewBlankPart();
		menu.Items.Add(blank);

		var import = new MenuItem { Header = "Import image…" };
		import.Click += (_, _) => ImportPart();
		menu.Items.Add(import);

		menu.IsOpen = true;
	}

	private void NewBlankPart()
	{
		if (_state.Selected is not { } bone) { ShowRigHint("Select the bone the part should belong to first."); return; }

		var dialog = new NewPartDialog { Owner = this };
		if (dialog.ShowDialog() != true) return;
		CreatePart(bone, dialog.PartName, null, dialog.PartWidth, dialog.PartHeight);
	}

	private void ImportPart()
	{
		if (_state.Selected is not { } bone) { ShowRigHint("Select the bone the part should belong to first."); return; }

		var dialog = new OpenFileDialog
		{
			Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
		};
		if (dialog.ShowDialog(this) != true) return;

		using var bitmap = SKBitmap.Decode(dialog.FileName);
		if (bitmap == null)
		{
			MessageBox.Show(this, "That file couldn't be read as an image.", "Import image",
							MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}
		if (bitmap.Width > 4096 || bitmap.Height > 4096)
		{
			MessageBox.Show(this, "Images up to 4096×4096 can be imported.", "Import image",
							MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		CreatePart(bone, Path.GetFileNameWithoutExtension(dialog.FileName), bitmap, bitmap.Width, bitmap.Height);
	}

	private void CreatePart(Bone bone, string name, SKBitmap? pixels, int width, int height)
	{
		_state.EditStructure(() =>
		{
			var asset = new Asset { Name = name, Width = width, Height = height };
			_assets.AddLayer(asset, "Base", pixels);   // null pixels = a blank layer

			// Halfway along the bone, turned so it looks upright on screen right now
			var offset = new Vector2(bone.Length / 2, 0);
			float rotation = -bone.WorldRotation;
			var slot = PartOps.CreatePart(_skeleton, bone, asset, name, offset, rotation);

			_state.SelectedSlot = slot;
			_state.Selected = bone;
			return $"Create part {slot.Name}";
		});
	}

	private void DeletePart(Slot slot)
	{
		ClosePaintEditor();
		_state.SelectedKeys.Clear();
		_state.EditStructure(() =>
		{
			PartOps.DeletePart(_skeleton, _state.Animations, slot);
			_state.SelectedSlot = null;
			return $"Delete part {slot.Name}";
		});
	}
}