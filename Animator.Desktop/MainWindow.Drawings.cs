using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Frame by frame, as the Windows editor has it (Drawings, Onion): the Drawing rows of the part
// panel (the drawing at the playhead, or the rest one in Build; its name; new, copy, shorter,
// longer, delete), the F5 / F6 / F7 keys, and the onion skin's settings (how many frames before
// and after, every how many). The paint editor follows the drawing at the playhead already
// (FollowPaint), and its light table is the onion skin's.
public sealed partial class MainWindow
{
	// An entry of the Drawing list (null: the part hidden)
	private sealed record DrawingChoice(string? Name, string Label)
	{
		public override string ToString() => Label;
	}

	internal readonly ComboBox DrawingBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox DrawingName = new();
	internal readonly TextBlock PartHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };
	private readonly Button _deleteDrawing = new() { Content = "Delete" };
	private readonly Button _shorter = new() { Content = "Shorter" };
	private readonly Button _longer = new() { Content = "Longer" };
	private Slot? _drawingsOf;   // the part the list is for

	// The rows, under the part's other properties
	private Control BuildDrawingRows()
	{
		Button B(string text, string command, Action click, string tip)
		{
			var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
			ToolTip.SetTip(b, $"{tip} ({_keys.Describe(command)})");
			b.Click += (_, _) => click();
			return b;
		}
		DrawingBox.SelectionChanged += (_, _) =>
		{
			if (_syncingProps || _drawingsOf is not { } slot || DrawingBox.SelectedItem is not DrawingChoice choice) return;
			if (!_session.ShowDrawing(slot, choice.Name)) SyncDrawingRows();   // (back to what's really there)
		};
		DrawingName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitDrawingName(); Viewport.Focus(); } };
		DrawingName.LostFocus += (_, _) => CommitDrawingName();
		ToolTip.SetTip(DrawingBox, "The drawing the part shows: at the playhead (Animate: picking one keys it there), or at rest (Build)");
		ToolTip.SetTip(_deleteDrawing, "The drawing, and the keys that show it (a part keeps one)");
		_deleteDrawing.Click += (_, _) => DeleteDrawing();
		_deleteDrawing.Margin = new Thickness(0, 0, 6, 6);
		ToolTip.SetTip(_shorter, $"The drawing at the playhead a frame shorter: the part's later drawings move up ({_keys.Describe("frames.shorter")})");
		_shorter.Click += (_, _) => ChangeHold(-1);
		_shorter.Margin = new Thickness(0, 0, 6, 6);
		ToolTip.SetTip(_longer, $"The drawing at the playhead a frame longer: the part's later drawings move on ({_keys.Describe("frames.longer")})");
		_longer.Click += (_, _) => ChangeHold(+1);
		_longer.Margin = new Thickness(0, 0, 6, 6);
		var buttons = new WrapPanel();
		buttons.Children.Add(B("New", "frames.new", () => NewDrawing(copy: false), "A new blank drawing: from the playhead on (Animate), or the rest one (Build)"));
		buttons.Children.Add(B("Copy", "frames.copy", () => NewDrawing(copy: true), "A copy of this drawing, to change a little"));
		buttons.Children.Add(_shorter);
		buttons.Children.Add(_longer);
		buttons.Children.Add(_deleteDrawing);
		return new StackPanel
		{
			Margin = new Thickness(0, 8, 0, 0),
			Spacing = 4,
			Children =
			{
				new TextBlock { Text = "Drawings (frame by frame)", FontWeight = FontWeight.SemiBold },
				Labelled("Drawing", DrawingBox),
				Labelled("Its name", DrawingName),
				buttons,
				PartHint
			}
		};
	}

	// The list made for a part (when another one is shown, or after an undo)
	private void FillDrawingRows(Slot slot)
	{
		_drawingsOf = slot;
		PartHint.Text = "";
		var choices = DrawingOps.DrawingNames(State.Skeleton, slot).Select(n => new DrawingChoice(n, n)).ToList();
		choices.Add(new DrawingChoice(null, "(hidden)"));
		DrawingBox.ItemsSource = choices;
		SyncDrawingRows();
	}

	// Every frame: the drawing there is now (without that counting as a choice)
	private void SyncDrawingRows()
	{
		if (_drawingsOf is not { } slot || DrawingBox.ItemsSource is not List<DrawingChoice> choices) return;
		if (choices.Count - 1 != DrawingOps.DrawingNames(State.Skeleton, slot).Count)
		{
			FillDrawingRows(slot);   // (a drawing made or deleted)
			return;
		}
		string? current = _session.CurrentDrawing(slot);
		_deleteDrawing.IsEnabled = current != null && choices.Count > 2;   // (a part keeps one drawing)
		DrawingName.IsEnabled = current != null;
		_shorter.IsEnabled = _longer.IsEnabled = State.AnimateMode;
		if (!DrawingName.IsFocused && DrawingName.Text != (current ?? "")) DrawingName.Text = current ?? "";
		if (DrawingBox.IsDropDownOpen) return;
		var match = choices.FirstOrDefault(c => c.Name == current);
		if (Equals(DrawingBox.SelectedItem, match)) return;
		bool was = _syncingProps;
		_syncingProps = true;
		DrawingBox.SelectedItem = match;
		_syncingProps = was;
	}

	internal void CommitDrawingName()
	{
		if (_syncingProps || _drawingsOf is not { } slot || _session.CurrentDrawing(slot) is not { } oldName) return;
		string newName = (DrawingName.Text ?? "").Trim();
		if (newName == oldName) return;
		if (!_session.RenameDrawing(slot, oldName, newName))
		{
			PartHint.Text = $"Can't rename to \"{newName}\": names can't be empty, and each drawing of a part needs its own.";
			DrawingName.Text = oldName;
		}
		else FillDrawingRows(slot);
	}

	private void DeleteDrawing()
	{
		if (_drawingsOf is not { } slot || _session.CurrentDrawing(slot) is not { } name) return;
		if (!_session.DeleteDrawing(slot, name)) PartHint.Text = "A part needs at least one drawing. To remove the whole part, use Delete part.";
	}

	// F7 (blank) / F6 (a copy of the drawing at the playhead): a new drawing for the selected part.
	// Animate: shown from the playhead on. Build: it becomes the rest drawing. (The paint editor,
	// when it's open, goes to it.)
	internal void NewDrawing(bool copy)
	{
		if (SceneLevel) return;
		if (State.SelectedSlot is not { } slot)
		{
			ShowRigHint("Select a part first: click it in the viewport, or in the list.");
			return;
		}
		if (_session.NewDrawing(slot, copy) == null)
		{
			PartHint.Text = "This part has no drawing to start from.";
			return;
		}
		FillDrawingRows(slot);
		if (_session.PaintOpen) FollowPaint();
	}

	// F5 (+1) / Shift+F5 (-1): the drawing at the playhead stays a frame longer or shorter, and the
	// part's later drawings move with it
	internal void ChangeHold(int frames)
	{
		if (SceneLevel) return;
		if (State.SelectedSlot is not { } slot)
		{
			ShowRigHint("Select a part first: click it in the viewport, or in the list.");
			return;
		}
		if (!State.AnimateMode)
		{
			PartHint.Text = $"Holds are part of the animation: switch to Animate first ({_keys.Describe("anim.mode")}).";
			return;
		}
		PartHint.Text = _session.ChangeHold(slot, frames) switch
		{
			HoldChange.NothingAfter => "Nothing comes after this drawing in the part's row: the last drawing always lasts until the end.",
			HoldChange.NoRoom => "No room: the part's last drawing would be pushed off the end. Make the animation longer first (Frames, above the timeline).",
			_ => ""
		};
	}

	private void DefineFrameCommands()
	{
		Add("frames.new", "_New drawing", () => NewDrawing(copy: false));
		Add("frames.copy", "_Copy of the drawing", () => NewDrawing(copy: true));
		Add("frames.longer", "Drawing a frame _longer", () => ChangeHold(+1));
		Add("frames.shorter", "Drawing a frame _shorter", () => ChangeHold(-1));
	}

	// ======================= the onion skin =======================

	internal OnionSkin Onion => _painter.Onion;
	internal readonly CheckBox OnionBox = new() { Content = "Onion" };
	internal readonly ComboBox OnionBefore = new() { ItemsSource = Enumerable.Range(0, 6).ToList() };
	internal readonly ComboBox OnionAfter = new() { ItemsSource = Enumerable.Range(0, 6).ToList() };
	internal readonly ComboBox OnionStep = new() { ItemsSource = Enumerable.Range(1, 4).ToList() };
	private bool _syncingOnion;

	// On the animation bar: the box, and its settings under ▾
	private IEnumerable<Control> BuildOnionTools()
	{
		ToolTip.SetTip(OnionBox, $"Faint ghosts of the frames before (red) and after (blue) in the viewport, and the part's other drawings under the one you paint ({_keys.Describe("view.onion")})");
		OnionBox.IsCheckedChanged += (_, _) => { if (!_syncingOnion) _painter.Onion.Enabled = OnionBox.IsChecked == true; };
		void Changed(object? sender, SelectionChangedEventArgs e)
		{
			if (_syncingOnion) return;
			if (OnionBefore.SelectedItem is int before) _painter.Onion.Before = before;
			if (OnionAfter.SelectedItem is int after) _painter.Onion.After = after;
			if (OnionStep.SelectedItem is int step) _painter.Onion.Step = step;
		}
		OnionBefore.SelectionChanged += Changed;
		OnionAfter.SelectionChanged += Changed;
		OnionStep.SelectionChanged += Changed;
		static Control Row(string label, Control box) => new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Children = { new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center }, box }
		};
		var settings = new Button { Content = "▾", Padding = new Thickness(6, 4) };
		ToolTip.SetTip(settings, "How many ghosts, and how far apart");
		settings.Flyout = new Flyout
		{
			Content = new StackPanel
			{
				Spacing = 6,
				Children =
				{
					Row("Frames before (red)", OnionBefore),
					Row("Frames after (blue)", OnionAfter),
					Row("Every how many frames", OnionStep),
					new TextBlock { Text = "(2: animating on twos)", Foreground = Brushes.Gray, FontSize = 12 }
				}
			}
		};
		SyncOnionUi();
		return new Control[] { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { OnionBox, settings } } };   // (they wrap together)
	}

	// Every frame (O switches it): the box and the settings as the onion skin has them
	private void SyncOnionUi()
	{
		var onion = _painter.Onion;
		if (OnionBox.IsChecked == onion.Enabled && Equals(OnionBefore.SelectedItem, onion.Before)
			&& Equals(OnionAfter.SelectedItem, onion.After) && Equals(OnionStep.SelectedItem, onion.Step)) return;
		_syncingOnion = true;
		OnionBox.IsChecked = onion.Enabled;
		OnionBefore.SelectedItem = onion.Before;
		OnionAfter.SelectedItem = onion.After;
		OnionStep.SelectedItem = onion.Step;
		_syncingOnion = false;
	}
}
