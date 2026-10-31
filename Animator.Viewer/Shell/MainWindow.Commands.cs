using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer;

// Keys, menus and the mouse buttons: every shortcut goes through the key map (Settings changes it,
// settings.json remembers the changes), and the menus, tool buttons and the status lines say
// what the keys are now
public partial class MainWindow
{
	private KeyMap _keys = new();

	private void InitKeys()
	{
		_keys = KeyMap.From(_appSettings.Shortcuts);
		ApplyMouseButtons();
		ApplyKeyMap();
	}

	// A command's key, for hints ("no key" when it has none)
	private string KeyOf(string id) => _keys.Gesture(id) is { Length: > 0 } key ? key : "no key";

	// ======================= settings =======================

	private void OpenSettings()
	{
		var dialog = new SettingsDialog(_keys, _tool.SelectButton, _tool.TransformButton, _tool.PanButton,
										_appSettings.AutosaveMinutes, _appSettings.BackupCount) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		_keys = dialog.Keys;
		_appSettings.Shortcuts = _keys.Changes();
		if (_appSettings.Shortcuts.Count == 0) _appSettings.Shortcuts = null;   // all the defaults
		_appSettings.SelectButton = dialog.SelectButton;
		_appSettings.TransformButton = dialog.TransformButton;
		_appSettings.PanButton = dialog.PanButton;
		_appSettings.AutosaveMinutes = dialog.AutosaveMinutes;   // the next autosave tick goes by it
		_appSettings.BackupCount = dialog.BackupCount;
		_appSettings.Save();
		ApplyMouseButtons();
		ApplyKeyMap();
	}

	private void ApplyMouseButtons()
	{
		var s = _appSettings;
		var (select, transform, pan) = s.MouseButtonsValid
			? (s.SelectButton, s.TransformButton, s.PanButton)
			: (MouseButton.Left, MouseButton.Right, MouseButton.Middle);
		_tool.SelectButton = _objectTool.SelectButton = select;
		_tool.TransformButton = _objectTool.TransformButton = transform;
		_tool.PanButton = _objectTool.PanButton = pan;

		// The hints that name the button
		SnapJointsBox.ToolTip = $"Setup mode: a new bone started near the selected bone's end (Ctrl+{MoveDrag}), or a joint dragged near its parent's end, snaps exactly onto it and stays there (\"On parent's end\" in the bone panel). Pull it further away to free it.";
		GiveBonesButton.ToolTip = $"Makes this drawn object a rigged one: a root bone you draw the rest from (Ctrl+{MoveDrag}), or bones suggested from its parts' shapes. The parts stay where they are.";
		AddBoneButton.ToolTip = $"Adds a child of the selected bone (or a root bone). Tip: Ctrl+{MoveDrag} in the viewport draws one.";
	}

	// "right-drag": a drag with the button that moves things
	private string MoveDrag => $"{ButtonName(_tool?.TransformButton ?? MouseButton.Right).ToLowerInvariant()}-drag";

	private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

	// Everything that names a key says the one it has now
	private void ApplyKeyMap()
	{
		foreach (var item in MenuItems(MainMenu.Items))
			if (item.Tag is string id && KeyMap.Find(id) != null) item.InputGestureText = _keys.Gesture(id);

		foreach (var button in ToolButtons.Children.OfType<RadioButton>())
		{
			if (button.Tag is not string tool) continue;
			string key = _keys.Gesture(PaintToolCommand(tool));
			button.Content = key.Length > 0 ? $"{tool} ({key})" : tool;
		}

		PartFront.ToolTip = $"Draw the picked parts in front of one more part ({KeyOf("order.forward")}; {KeyOf("order.front")}: in front of all). In Animate mode, that keys the order.";
		PartBack.ToolTip = $"Draw the picked parts behind one more part ({KeyOf("order.backward")}; {KeyOf("order.back")}: behind all). In Animate mode, that keys the order.";
		NewDrawingButton.Content = WithKey("New", "frames.new");
		CopyDrawingButton.Content = WithKey("Copy", "frames.copy");
		ShorterButton.ToolTip = $"{KeyOf("frames.shorter")}: this drawing stays one frame less; the part's later drawings move one frame earlier (Animate mode)";
		LongerButton.ToolTip = $"{KeyOf("frames.longer")}: this drawing stays one frame more; the part's later drawings move one frame later (Animate mode)";
		SwapColorsButton.ToolTip = $"Swap colors ({KeyOf("paint.swap")})";
		InvertSelectionButton.ToolTip = $"Select everything that isn't selected ({KeyOf("paint.invert")})";
		DeselectButton.ToolTip = $"Drop the selection, so tools work everywhere again ({KeyOf("paint.deselect")})";
		LayersViewChoice.ToolTip = $"Every part in the order it's drawn, the top one in front, whatever bone it's on. Drag one up or down to change the order (picked ones go together); right-click for more ({KeyOf("order.forward")} / {KeyOf("order.backward")} a step, {KeyOf("order.front")} / {KeyOf("order.back")} all the way). In Animate mode, that keys the order at the playhead.";
		ClosePaintButton.Content = WithKey("Close", "edit.back");
		KeyPoseButton.Content = WithKey("Key", "anim.key");
		AutoKeyBox.Content = WithKey("Auto key", "anim.autoKey");
		GraphBox.Content = WithKey("Curves", "anim.graph");
		MirrorPoseButton.ToolTip = WithKey("Left and right swapped: the picked bones and parts (or everything) take their other side's pose, flipped. Animate mode: keys here; Setup: the rest pose", "pose.mirror");
		CopyPoseButton.ToolTip = WithKey("The pose of the picked bones and parts (or everything), to paste at another frame or in another animation", "pose.copy");
		PastePoseButton.ToolTip = WithKey("The copied pose, here", "pose.paste");
		PasteMirroredButton.ToolTip = WithKey("The copied pose on the other side, flipped (a left step pasted as a right one)", "pose.pasteMirrored");
		string pick = ButtonName(_tool.SelectButton).ToLowerInvariant();
		SelectToolButton.ToolTip = WithKey($"Select: {pick}-click picks, a {pick}-drag draws a box over several (the {ButtonName(_tool.TransformButton).ToLowerInvariant()} button still moves things)", "tool.select");
		MoveToolButton.ToolTip = WithKey($"Move: {pick}-drag a bone (its joint or its body) or a part to move it, and everything picked with it; on empty space, a box", "tool.move");
		RotateToolButton.ToolTip = WithKey($"Rotate: {pick}-drag a bone to turn it round its joint, a part round its pivot (Shift: 15° steps); on empty space, a box", "tool.rotate");
		PaintToolButton.ToolTip = WithKey("Paint: one click on a part opens it in the paint editor (a part of another object opens that object)", "tool.paint");
		_timeline.KeyHint = KeyOf("anim.key");
		SceneButton.ToolTip = $"Back to the scene, to place whole objects ({KeyOf("edit.back")}). At scene level: open the selected object ({KeyOf("scene.open")})";
		RefreshScenePanel();   // its hints name keys and buttons too
		UpdateStatus();
	}

	private string WithKey(string label, string id) => _keys.Gesture(id) is { Length: > 0 } key ? $"{label} ({key})" : label;

	private static IEnumerable<MenuItem> MenuItems(ItemCollection items)
	{
		foreach (var item in items.OfType<MenuItem>())
		{
			yield return item;
			foreach (var inner in MenuItems(item.Items)) yield return inner;
		}
	}

	// A menu item whose Tag is a command runs it
	private void MenuCommand_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not MenuItem { Tag: string id }) return;
		if (Viewport.IsMouseCaptured || TimelineView.IsMouseCaptured || _paintCanvas.IsBusy) return;
		if (RunCommand(id)) UpdateStatus();
	}

	// The Edit menu: what makes sense where you are
	private void EditMenu_Opened(object sender, RoutedEventArgs e)
	{
		foreach (var item in MenuItems(EditMenu.Items))
			if (item.Tag is string id && id.StartsWith("pick.", StringComparison.Ordinal))
				item.IsEnabled = !_sceneLevel;
	}

	// ======================= keys =======================

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		// Typing in a text box (like the color code) must not trigger shortcuts.
		// Without this, typing "B" in "#FFB000" would flip the elbow instead.
		if (Keyboard.FocusedElement is TextBox) return;
		// In an open menu, letters are its items' access keys (O: Open…), not shortcuts
		if (Keyboard.FocusedElement is MenuItem) return;
		if (KeyChord.From(e) is not { } chord) return;   // Ctrl, Shift or Alt on its own

		// Paint keys go first while the mouse is over the paint canvas
		if (HandlePaintKey(chord)) { e.Handled = true; return; }
		if (_keys.CommandFor(chord, KeyScope.Main) is not { } id) return;

		// Nothing that changes the project in the middle of a stroke, or a drag (its undo step
		// would come out wrong)
		if (_paintCanvas.IsBusy && !SafeWhilePainting(id)) { e.Handled = true; return; }
		if ((Viewport.IsMouseCaptured || TimelineView.IsMouseCaptured) && !SafeWhileDragging(id)) { e.Handled = true; return; }

		if (!RunCommand(id)) return;
		e.Handled = true;
		UpdateStatus();
	}

	private static bool SafeWhilePainting(string id) => id is "view.images" or "view.pixels" or "view.onion" or "view.grid" or "view.guides" or "view.rulers";
	private static bool SafeWhileDragging(string id) => id is "view.images" or "view.pixels" or "view.onion" or "view.grid" or "view.guides"
		or "view.snapGrid" or "view.snapGuides" or "file.save" or "file.saveAs";

	// Does what a command does here. false: nothing to do here (the key goes on to whatever else
	// wants it).
	private bool RunCommand(string id)
	{
		switch (id)
		{
			case "file.new": NewProject(); return true;
			case "file.open": OpenProject(); return true;
			case "file.save": SaveProject(); return true;
			case "file.saveAs": SaveProjectAs(); return true;
			case "file.import": ImportFile(); return true;
			case "file.export": ExportProject(); return true;
			case "file.settings": OpenSettings(); return true;
			case "edit.undo":
				_state.SelectedKeys.Clear();   // selection may point at keys that won't exist after undo
				_state.Undo.Undo();
				return true;
			case "edit.redo":
				_state.SelectedKeys.Clear();
				_state.Undo.Redo();
				return true;
			case "play.toggle": TogglePlaying(); return true;
			case "view.images": _renderer.ShowImages = !_renderer.ShowImages; return true;
			case "view.pixels": _renderer.PixelArt = !_renderer.PixelArt; return true;
			case "view.reset": ResetView(); return true;
			case "view.onion": ToggleOnion(); return true;
			case "view.hitboxes": ToggleHitboxes(); return true;
			case "view.rulers": ToggleViewAid("rulers"); return true;
			case "view.grid": ToggleViewAid("grid"); return true;
			case "view.guides": ToggleViewAid("guides"); return true;
			case "view.snapGrid": ToggleViewAid("snapGrid"); return true;
			case "view.snapGuides": ToggleViewAid("snapGuides"); return true;
			case "view.lockGuides": ToggleViewAid("lockGuides"); return true;
		}
		return _sceneLevel ? RunSceneCommand(id) : RunObjectCommand(id);
	}

	// At scene level: whole objects, playback and the view
	private bool RunSceneCommand(string id)
	{
		switch (id)
		{
			case "play.restart": _sceneState.Time = 0; return true;
			case "play.back": StepScene(-1); return true;
			case "play.on": StepScene(+1); return true;
			case "scene.open" when _objectTool.Selected is { } obj: EnterObject(obj); return true;
			case "edit.delete" when _sceneState.SelectedClip != null: DeleteSelectedClip(); return true;
			case "edit.delete": DeleteCurrentObject(); return true;
			case "edit.back": _objectTool.Selected = null; return true;
			default: return false;
		}
	}

	// Inside an object
	private bool RunObjectCommand(string id)
	{
		switch (id)
		{
			case "edit.copy": _timeline.Copy(); return true;
			case "edit.paste": _timeline.Paste(); return true;
			case "edit.delete":
				// Selected keys first; otherwise the picked one's keys at this frame
				if (!_timeline.DeleteSelected()) DeleteKeysAtFrame();
				return true;
			case "edit.back":
				// Closes the paint editor first, then lets go of picked frames, then goes back to the scene
				if (PaintPanel.Visibility == Visibility.Visible) ClosePaintEditor();
				else if (_state.Hold.Any) _state.Hold.Clear();   // the unkeyed changes go
				else if (_timeline.Range != null) _timeline.ClearRange();
				else LeaveObject();
				return true;

			case "pick.all": PickEverythingShown(); return true;
			case "pick.keys": SelectKeysOfPicked(); return true;
			case "pick.hide": HidePicked(); return true;
			case "pick.hideOthers": HideAllButPicked(); return true;
			case "pick.showAll": ShowEverything(); return true;
			case "pick.lock": LockPicked(); return true;
			case "pick.lockOthers": LockAllButPicked(); return true;
			case "pick.unlockAll": UnlockEverything(); return true;

			case "play.restart": _state.Time = 0; return true;
			case "play.back": _state.Playing = false; _state.SetFrame(_state.Frame - 1); return true;
			case "play.on": _state.Playing = false; _state.SetFrame(_state.Frame + 1); return true;
			case "play.prevKey": _timeline.JumpToKey(-1); return true;
			case "play.nextKey": _timeline.JumpToKey(+1); return true;

			case "anim.mode": _state.AnimateMode = !_state.AnimateMode; return true;
			case "anim.key": KeyThePose(); return true;
			case "anim.autoKey": SetAutoKey(!_state.AutoKey); return true;
			case "anim.graph": SetGraphMode(!_graphMode); return true;
			case "pose.mirror": MirrorThePose(); return true;
			case "pose.copy": CopyThePose(); return true;
			case "pose.paste": PasteThePose(mirrored: false); return true;
			case "pose.pasteMirrored": PasteThePose(mirrored: true); return true;
			case "tool.select": SetViewTool(ViewTool.Select); return true;
			case "tool.move": SetViewTool(ViewTool.Move); return true;
			case "tool.rotate": SetViewTool(ViewTool.Rotate); return true;
			case "tool.paint": SetViewTool(ViewTool.Paint); return true;
			case "keys.earlier": _timeline.Nudge(-1); return true;
			case "keys.later": _timeline.Nudge(+1); return true;
			case "keys.reverse": _timeline.ReverseSelected(); return true;
			case "time.insert": _timeline.InsertFrames(_state.Frame, 1); return true;
			case "time.remove": _timeline.RemoveFrames(_state.Frame, 1); return true;
			case "anim.event": AddEventAt(_state.Frame); return true;
			case "order.forward": ReorderPicked(+1, toEnd: false); return true;
			case "order.backward": ReorderPicked(-1, toEnd: false); return true;
			case "order.front": ReorderPicked(+1, toEnd: true); return true;
			case "order.back": ReorderPicked(-1, toEnd: true); return true;

			case "ik.toggle": ToggleIkMode(); return true;
			case "ik.switch": SwitchIkHere(); return true;
			case "ik.snap": MatchIk(bake: false); return true;
			case "ik.bake": MatchIk(bake: true); return true;
			case "ik.flip": FlipElbow(); return true;

			case "frames.new": NewDrawing(copy: false); return true;
			case "frames.copy": NewDrawing(copy: true); return true;
			case "frames.longer": ChangeHold(+1); return true;
			case "frames.shorter": ChangeHold(-1); return true;
			default: return false;
		}
	}

	// The picked parts (or the selected one) a step toward the front or the back, or all the way
	private void ReorderPicked(int direction, bool toEnd)
	{
		var parts = _state.PickedSlots.ToList();
		if (parts.Count == 0 && _state.SelectedSlot is { } slot) parts.Add(slot);
		Reorder(parts, direction, toEnd);
	}

	// ======================= paint keys =======================

	private static readonly string[] PaintTools = { "Select", "Wand", "Move", "Pencil", "Brush", "Mixer", "Eraser", "Picker", "Fill", "Gradient", "Figure" };

	private static string PaintToolCommand(string tool) => "paint." + tool.ToLowerInvariant();

	private static string? PaintToolOf(string id) =>
		PaintTools.FirstOrDefault(tool => PaintToolCommand(tool) == id);

	// While the mouse is over the paint canvas (true = handled)
	private bool HandlePaintKey(KeyChord chord)
	{
		if (_paint.Asset == null || !PaintView.IsMouseOver) return false;
		return _keys.CommandFor(chord, KeyScope.Paint) is { } id && RunPaintCommand(id);
	}

	private bool RunPaintCommand(string id)
	{
		switch (id)
		{
			case "paint.selectAll": _paint.SelectAll(); return true;
			case "paint.deselect": _paint.Deselect(); return true;
			case "paint.invert": _paint.InvertSelection(); return true;
			case "paint.swap": _paint.SwapColors(); return true;
			case "paint.smaller": SetBrushSize(_paint.Size - 1); return true;
			case "paint.bigger": SetBrushSize(_paint.Size + 1); return true;
			case "paint.delete" when _paint.Selection != null: _paint.DeleteSelectedPixels(); return true;

			// A figure or selection shape in progress: finish it, take back a point, or cancel it
			case "paint.finish" when _figure.InProgress: _figure.Finish(_paint); return true;
			case "paint.finish" when _select.PlacingPoints: _select.Finish(_paint); return true;
			case "paint.undoPoint" when _figure.InProgress: _figure.RemoveLastPoint(_paint); return true;
			case "paint.undoPoint" when _select.PlacingPoints: _select.RemoveLastPoint(_paint); return true;
			case "paint.cancel" when _figure.InProgress: _figure.Cancel(_paint); return true;
			case "paint.cancel" when _select.PlacingPoints: _select.Cancel(); return true;
			case "paint.cancel" when _paint.Selection != null: _paint.Deselect(); return true;
		}
		if (PaintToolOf(id) is not { } tool) return false;
		SelectTool(tool);
		return true;
	}
}
