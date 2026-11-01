using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer;

// Frame-by-frame parts: the "Drawings" rows of the part panel, the F5 / F6 / F7 keys,
// and the paint editor following the drawing at the playhead
public partial class MainWindow
{
	// An entry in the "Drawing" dropdown; ToString is what the ComboBox displays
	private sealed record DrawingChoice(string? Name, string Label)
	{
		public override string ToString() => Label;
	}

	private Slot? _paintSlot;        // the part whose drawing the paint editor shows (null = none)
	private Asset? _paintFollowed;   // the drawing that part showed when the editor last looked

	// The drawing the panel works on: the one at the playhead (Animate mode),
	// or the part's rest drawing (Setup mode)
	private string? CurrentDrawing(Slot slot) =>
		_state.AnimateMode ? DrawingOps.DrawingAt(_state.Animation, slot, _state.Time) : slot.SetupAttachment;

	// ======================= panel =======================

	// Rebuilds the dropdown (when the selected part or the rig changes)
	private void ShowDrawingChoices(Slot slot)
	{
		var choices = DrawingOps.DrawingNames(_skeleton, slot).Select(n => new DrawingChoice(n, n)).ToList();
		choices.Add(new DrawingChoice(null, "(hidden)"));
		DrawingBox.ItemsSource = choices;
		SyncDrawingChoice();
	}

	// Called every frame: shows the current drawing, without that counting as a choice
	private void SyncDrawingChoice()
	{
		if (_shownSlot is not { } slot || DrawingBox.ItemsSource is not List<DrawingChoice> choices) return;

		string? current = CurrentDrawing(slot);
		bool hasDrawing = current != null;
		DeleteDrawingButton.IsEnabled = hasDrawing && choices.Count > 2;   // a part keeps one drawing
		DrawingName.IsEnabled = hasDrawing;
		ShorterButton.IsEnabled = LongerButton.IsEnabled = _state.AnimateMode;
		if (!DrawingName.IsKeyboardFocused && DrawingName.Text != (current ?? ""))
			DrawingName.Text = current ?? "";

		if (DrawingBox.IsDropDownOpen) return;
		var match = choices.FirstOrDefault(c => c.Name == current);
		if (Equals(DrawingBox.SelectedItem, match)) return;

		bool wasSyncing = _syncingRig;
		_syncingRig = true;   // so this doesn't count as you choosing something
		DrawingBox.SelectedItem = match;
		_syncingRig = wasSyncing;
	}

	private void DrawingBox_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownSlot is not { } slot || DrawingBox.SelectedItem is not DrawingChoice choice) return;

		bool key = _state.AnimateMode;
		int frame = SnapToFrame(key);
		bool changed = _state.EditStructure(() =>
			DrawingOps.Show(_state.Animation, slot, choice.Name, frame, _state.Fps, key)
				? key ? $"Show {choice.Label} on {slot.Name} @ frame {frame}" : $"{slot.Name} rests on {choice.Label}"
				: null);

		if (!changed) SyncDrawingChoice();   // put the dropdown back to what's really there
	}

	private void NewDrawing_Click(object sender, RoutedEventArgs e) => NewDrawing(copy: false);
	private void DuplicateDrawing_Click(object sender, RoutedEventArgs e) => NewDrawing(copy: true);
	private void Shorter_Click(object sender, RoutedEventArgs e) => ChangeHold(-1);
	private void Longer_Click(object sender, RoutedEventArgs e) => ChangeHold(+1);

	private void DeleteDrawing_Click(object sender, RoutedEventArgs e)
	{
		if (_shownSlot is not { } slot || CurrentDrawing(slot) is not { } name) return;
		bool deleted = _state.EditStructure(() =>
			DrawingOps.DeleteDrawing(_skeleton, _state.Animations, slot, name) ? $"Delete drawing {name}" : null);
		if (!deleted) PartHint.Text = "A part needs at least one drawing. To remove the whole part, use Delete part.";
	}

	private void DrawingName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitDrawingName();
		Viewport.Focus();
	}

	private void DrawingName_LostFocus(object sender, RoutedEventArgs e) => CommitDrawingName();

	private void CommitDrawingName()
	{
		if (_syncingRig || _shownSlot is not { } slot || CurrentDrawing(slot) is not { } oldName) return;
		string newName = DrawingName.Text.Trim();
		if (newName == oldName) return;

		bool renamed = _state.EditStructure(() =>
			DrawingOps.RenameDrawing(_skeleton, _state.Animations, slot, oldName, newName)
				? $"Rename drawing {oldName} to {newName}"
				: null);
		if (!renamed)
		{
			PartHint.Text = $"Can't rename to \"{newName}\": names can't be empty, and each drawing of a part needs its own.";
			DrawingName.Text = oldName;
		}
	}

	// ======================= F5 / F6 / F7 =======================

	// Stops playback and puts the playhead exactly on a frame (only needed when keying)
	private int SnapToFrame(bool key)
	{
		if (key)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
		}
		return _state.Frame;
	}

	// F7 (blank) / F6 (copy of the current drawing): a new drawing for the selected part.
	// Animate mode: shown from the playhead on. Setup mode: it becomes the rest drawing.
	private void NewDrawing(bool copy)
	{
		if (_state.SelectedSlot is not { } slot)
		{
			ShowRigHint("Select a part first: click it in the viewport, or in the tree.");
			return;
		}

		bool key = _state.AnimateMode;
		int frame = SnapToFrame(key);

		// Made from the current drawing; if the part is hidden here, from its rest drawing (or any)
		var template = DrawingOps.Find(_skeleton, slot, CurrentDrawing(slot))
			?? DrawingOps.Find(_skeleton, slot, slot.SetupAttachment)
			?? DrawingOps.DrawingNames(_skeleton, slot).Select(n => DrawingOps.Find(_skeleton, slot, n)).FirstOrDefault(a => a != null);
		if (template == null || !_skeleton.Assets.TryGetValue(template.AssetId, out var source))
		{
			PartHint.Text = "This part has no drawing to start from.";
			return;
		}

		Attachment? created = null;
		_state.EditStructure(() =>
		{
			// Same size and the same layers (names, visibility, opacity); pixels only when copying
			var pixels = new Asset { Width = source.Width, Height = source.Height };
			foreach (var layer in source.Layers)
			{
				var added = _assets.AddLayer(pixels, layer.Name, copy ? _assets.GetLayer(layer) : null);
				added.Visible = layer.Visible;
				added.Opacity = layer.Opacity;
			}

			created = DrawingOps.AddDrawing(_skeleton, slot, template, pixels, _state.EditSkin);
			if (_state.EditSkin is { } skin) SkinOps.Show(_skeleton, skin);
			DrawingOps.Show(_state.Animation, slot, created.Name, frame, _state.Fps, key);
			return $"{(copy ? "Duplicate" : "New")} drawing {created.Name}{(key ? $" @ frame {frame}" : "")}";
		});

		// Keep drawing: if the paint editor is open, it moves to the new drawing
		if (created != null && PaintPanel.Visibility == Visibility.Visible
			&& _skeleton.Assets.TryGetValue(created.AssetId, out var drawing))
			OpenPaintEditor(drawing, slot, fit: _paint.Asset is not { } open
												|| open.Width != drawing.Width || open.Height != drawing.Height);
	}

	// F5 (+1) / Shift+F5 (-1): the drawing at the playhead stays one frame longer or shorter,
	// and the part's later drawings move with it
	private void ChangeHold(int frames)
	{
		if (_state.SelectedSlot is not { } slot)
		{
			ShowRigHint("Select a part first: click it in the viewport, or in the tree.");
			return;
		}
		if (!_state.AnimateMode)
		{
			PartHint.Text = "Holds are part of the animation: switch to Animate first (K).";
			return;
		}

		int frame = SnapToFrame(key: true);
		var result = HoldChange.NothingAfter;
		_state.EditStructure(() =>
		{
			result = frames > 0
				? DrawingOps.InsertFrame(_state.Animation, slot, frame, _state.Fps, _state.FrameCount)
				: DrawingOps.RemoveFrame(_state.Animation, slot, frame, _state.Fps);
			return result == HoldChange.Done
				? $"{(frames > 0 ? "Longer" : "Shorter")} hold on {slot.Name} @ frame {frame}"
				: null;
		});

		PartHint.Text = result switch
		{
			HoldChange.NothingAfter => "Nothing comes after this drawing in the part's row: the last drawing always lasts until the end.",
			HoldChange.NoRoom => "No room: the part's last drawing would be pushed off the end. Make the animation longer first (Length, above the timeline).",
			_ => ""
		};
	}

	// ======================= paint editor follows the playhead =======================

	// Step or scrub to a frame where the part shows another drawing, and the editor switches
	// to it, so you can draw frame by frame. Not while playing, and never mid-stroke.
	private void FollowPaintDrawing()
	{
		if (_paint.Asset is not { } open) return;

		bool gone = !_skeleton.Assets.ContainsKey(open.Id);   // deleted, or undone out of the project
		if (_paintSlot is { } part && !_skeleton.Slots.Contains(part)) _paintSlot = null;
		if (_paintSlot is not { } slot)
		{
			if (gone) ClosePaintEditor();
			return;
		}
		if (_paintCanvas.IsBusy || (_state.Playing && !gone)) return;

		var shown = PartData(slot).Asset;   // what the part shows in the current pose
		if (shown == null)
		{
			if (gone) ClosePaintEditor();   // hidden here, and the old drawing is gone
			return;
		}
		if (shown == _paintFollowed && !gone) return;

		_paintFollowed = shown;
		if (shown != open)
			OpenPaintEditor(shown, slot, fit: shown.Width != open.Width || shown.Height != open.Height);
	}
}
