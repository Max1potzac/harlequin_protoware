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

	private static readonly string[] PlacementFields = { "PX", "PY", "PRotation", "PScaleX", "PScaleY" };

	private void ShowPartProperties()
	{
		_shownSlot = _state.SelectedSlot;
		if (_shownSlot is not { } slot) return;
		var (_, asset) = PartData(slot);
		_syncingRig = true;

		SetField(PartName, slot.Name);
		var bones = _skeleton.AllBones().Select(b => new BoneChoice(b, b.Name)).ToList();
		PartBone.ItemsSource = bones;
		PartBone.SelectedItem = bones.FirstOrDefault(c => c.Bone == slot.Bone);

		EditPixelsButton.IsEnabled = asset != null;
		ShowDrawingChoices(slot);

		_syncingRig = false;
		RefreshPartValues();
	}

	// The numbers and the title; runs every frame, so drags and keys show live.
	// Animate mode shows where the part is at this frame (placement keys included).
	private void RefreshPartValues()
	{
		if (_shownSlot is not { } slot) return;
		var (att, asset) = PartData(slot);
		bool pose = _state.AnimateMode;

		var order = pose ? _skeleton.DrawOrder : _skeleton.Slots;   // the one drawn at this frame, or the rest order
		int index = IndexIn(order, slot);
		PartFront.IsEnabled = index < order.Count - 1;
		PartBack.IsEnabled = index > 0;
		string drawing = att != null && att.Name != slot.Name ? $", showing \"{att.Name}\"" : "";
		string size = asset != null ? $", {asset.Width}×{asset.Height} px" : "";
		string values = pose ? $"Placement at frame {_state.Frame}: typing or dragging sets keys" : "Rest placement (Build)";
		string title = $"Part {index + 1} of {order.Count} in {(pose ? "this frame's" : "the")} draw order{size}{drawing}\n{values}";
		if (PartTitle.Text != title) PartTitle.Text = title;

		foreach (var box in new[] { PartX, PartY, PartRotation, PartScaleX, PartScaleY, PartPivotX, PartPivotY })
			SetField(box, att != null ? Num(PartValue(slot, att, asset, (string)box.Tag, pose)) : "");
		PartPivotX.IsEnabled = PartPivotY.IsEnabled = !pose;   // the pivot is part of the rest setup
		SyncDrawingChoice();
		SyncEffects(slot);
	}

	private static float? PartValue(Slot slot, Attachment att, Asset? asset, string field, bool pose) => field switch
	{
		"PX" => att.Offset.X + (pose ? slot.Offset.X : 0f),
		"PY" => att.Offset.Y + (pose ? slot.Offset.Y : 0f),
		"PRotation" => Deg(att.Rotation + (pose ? slot.Rotation : 0f)),
		"PScaleX" => att.Scale.X * (pose ? slot.Scale.X : 1f),
		"PScaleY" => att.Scale.Y * (pose ? slot.Scale.Y : 1f),
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
			bool renamed = _session.RenamePart(slot, newName);
			if (!renamed)
			{
				PartHint.Text = $"Can't rename to \"{newName}\": part names must be unique and not empty.";
				box.Text = slot.Name;
			}
			return;
		}

		var (att, asset) = PartData(slot);
		if (att == null) return;
		bool pose = _state.AnimateMode && PlacementFields.Contains(field);
		var current = PartValue(slot, att, asset, field, pose);
		if (current == null || box.Text.Trim() == Num(current)) return;

		if (!TryNum(box.Text, out float v))
		{
			PartHint.Text = $"\"{box.Text}\" isn't a number.";
			box.Text = Num(current);
			return;
		}

		if (pose)
		{
			KeyPartField(slot, att, field, v);
			return;
		}
		if (Enum.TryParse<PartField>(field[1..], out var which))   // [1..] drops the "P" prefix
			_session.SetPartRest(slot, which, field == "PRotation" ? Rad(v) : v);
	}

	// Animate mode: a typed number becomes a placement key at this frame (see EditorSession.KeyPart)
	private void KeyPartField(Slot slot, Attachment att, string field, float v)
	{
		// Auto key off: the placement changes, no key yet
		if (HoldInsteadOfKey(() =>
		{
			switch (field)
			{
				case "PX": slot.Offset = new Vector2(v - att.Offset.X, slot.Offset.Y); break;
				case "PY": slot.Offset = new Vector2(slot.Offset.X, v - att.Offset.Y); break;
				case "PRotation": slot.Rotation = Rad(v) - att.Rotation; break;
				case "PScaleX": slot.Scale = new Vector2(Ratio(v, att.Scale.X), slot.Scale.Y); break;
				case "PScaleY": slot.Scale = new Vector2(slot.Scale.X, Ratio(v, att.Scale.Y)); break;
			}
		})) return;
		if (Enum.TryParse<PartField>(field[1..], out var which)) _session.KeyPart(slot, which, field == "PRotation" ? Rad(v) : v);
	}

	private void PartBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownSlot is not { } slot || PartBone.SelectedItem is not BoneChoice { Bone: { } bone }) return;
		_session.MovePartToBone(slot, bone);
	}

	private void PartFront_Click(object sender, RoutedEventArgs e) => MovePartInOrder(+1);
	private void PartBack_Click(object sender, RoutedEventArgs e) => MovePartInOrder(-1);

	// The picked parts a step forward or back in the order (keyed at the playhead in Animate mode)
	private void MovePartInOrder(int direction) => ReorderPicked(direction, toEnd: false);

	private void EditPixels_Click(object sender, RoutedEventArgs e)
	{
		if (_state.SelectedSlot is { } slot) OpenPaintEditorOn(slot);
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

	// The bone a new part goes on: the selected one, or a drawn object's one hidden bone
	private Bone? BoneForNewPart() => IsDrawn ? _skeleton.Roots.FirstOrDefault() : _state.Selected;

	private void NewBlankPart()
	{
		if (BoneForNewPart() is not { } bone) { ShowRigHint("Select the bone the part should belong to first."); return; }

		var dialog = new NewPartDialog { Owner = this };
		if (dialog.ShowDialog() != true) return;
		CreatePart(bone, dialog.PartName, null, dialog.PartWidth, dialog.PartHeight);
	}

	private void ImportPart()
	{
		if (BoneForNewPart() is not { } bone) { ShowRigHint("Select the bone the part should belong to first."); return; }
		if (PickImage() is not { } image) return;
		using var bitmap = image.Bitmap;
		CreatePart(bone, image.Name, bitmap, bitmap.Width, bitmap.Height);
	}

	// Asks for a picture file and reads it. null = cancelled, or it couldn't be used (already told).
	// The caller disposes the bitmap.
	private (string Name, SKBitmap Bitmap)? PickImage()
	{
		var dialog = new OpenFileDialog
		{
			Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
		};
		if (dialog.ShowDialog(this) != true) return null;

		var bitmap = SKBitmap.Decode(dialog.FileName);
		if (bitmap == null)
		{
			MessageBox.Show(this, "That file couldn't be read as an image.", "Import image",
							MessageBoxButton.OK, MessageBoxImage.Error);
			return null;
		}
		if (bitmap.Width > 4096 || bitmap.Height > 4096)
		{
			bitmap.Dispose();
			MessageBox.Show(this, "Images up to 4096×4096 can be imported.", "Import image",
							MessageBoxButton.OK, MessageBoxImage.Warning);
			return null;
		}
		return (Path.GetFileNameWithoutExtension(dialog.FileName), bitmap);
	}

	private void CreatePart(Bone bone, string name, SKBitmap? pixels, int width, int height) =>
		_session.CreatePart(bone, name, pixels, width, height);

	private void DeletePart(Slot slot)
	{
		ClosePaintEditor();
		_session.DeletePart(slot);
	}
}