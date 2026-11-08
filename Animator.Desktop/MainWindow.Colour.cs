using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Colour, as the Windows editor has it (Effects, Colour):
// - a part's tint (its colour and opacity: keyed in Animate, at rest in Build), next to its
//   opacity and blend mode
// - colour layers on a whole object (the Rig tab's Colour view): hue / saturation, brightness /
//   contrast, tint, colour balance, each leaving out any parts it shouldn't change
// - colour layers inside a drawing (the paint editor's + ◐): those and Replace colour, changing
//   the layers under them in that drawing only
// Their numbers are the rest pose's in Build and keyed in Animate (held, with auto key off,
// until S keys them). The editor always shows them; exports bake them in only when asked.
public sealed partial class MainWindow
{
	private static readonly ColorAdjustKind[] ObjectColourKinds =
		{ ColorAdjustKind.HueSaturation, ColorAdjustKind.BrightnessContrast, ColorAdjustKind.Tint, ColorAdjustKind.ColorBalance };

	private static readonly ColorAdjustKind[] DrawingColourKinds =
		{ ColorAdjustKind.HueSaturation, ColorAdjustKind.BrightnessContrast, ColorAdjustKind.Tint, ColorAdjustKind.ColorBalance, ColorAdjustKind.ReplaceColor };

	internal readonly ToggleButton ColourView = new() { Content = "Colour" };
	internal readonly ListBox ColourList = new() { MaxHeight = 160 };
	internal readonly TextBox ColourName = new();
	internal readonly StackPanel ColourSettings = new() { Spacing = 2 };
	internal readonly StackPanel ColourLeaveOutList = new();
	internal readonly TextBlock ColourHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	private readonly Expander _leaveOut = new() { Header = "Leave out", HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly Button _colourDelete = new() { Content = "Delete" }, _colourUp = new() { Content = "▲" }, _colourDown = new() { Content = "▼" };
	private Control _colourPanel = null!;
	private bool _colourViewOn;
	private ColorLayer? _colourLayer;   // the one picked in the Colour view
	private bool _syncingColour;

	internal readonly StackPanel PaintColourSettings = new() { Spacing = 2 };
	internal readonly Button PartTint = new() { Padding = new Thickness(2) };
	private readonly Border _partTintSwatch = new() { Width = 40, Height = 16, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) };

	// What the settings show now: made again only when that changes (not while a slider is dragged)
	private readonly record struct ColourShown(object? Layer, AdjustValues Values, ColorAdjustKind Kind, bool Colorize,
		bool KeepLight, bool Animating, string LeftOut);
	private ColourShown? _colourShown, _paintColourShown;

	internal bool ShowingColour => _colourViewOn && !SceneLevel;
	internal ColorLayer? PickedColourLayer => _colourLayer;

	// What the colour picker is answered with (the tests answer it); null: the user's answer
	internal Func<Vector3, string, Task<Vector3?>>? AnswerColour { get; set; }

	private AdjustValues ShownValues(ColorAdjust adjust) => State.AnimateMode ? adjust.Now : adjust.Setup;

	private async Task<Vector3?> PickColour(Vector3 color, string what)
	{
		if (AnswerColour is { } answer) return await answer(color, what);
		var start = new SKColor((byte)Math.Clamp(color.X * 255, 0, 255), (byte)Math.Clamp(color.Y * 255, 0, 255), (byte)Math.Clamp(color.Z * 255, 0, 255));
		if (await ColourDialog.Ask(this, what, start) is not { } c) return null;
		return new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
	}

	private static MenuFlyout KindMenu(IEnumerable<ColorAdjustKind> kinds, Action<ColorAdjustKind> add)
	{
		var menu = new MenuFlyout();
		foreach (var kind in kinds)
		{
			var item = new MenuItem { Header = ColorAdjust.KindName(kind) };
			item.Click += (_, _) => add(kind);
			menu.Items.Add(item);
		}
		return menu;
	}

	// ======================= a part's tint =======================

	private Control BuildPartTint()
	{
		PartTint.Content = _partTintSwatch;
		ToolTip.SetTip(PartTint, "The part's colour (a tint: white leaves it as drawn) and opacity. Animate: keyed here; Build: at rest.");
		PartTint.Click += async (_, _) =>
		{
			if (State.SelectedSlot is not { } slot) return;
			bool pose = State.AnimateMode;
			var color = pose ? slot.Color : slot.SetupColor;
			float opacity = pose ? slot.Opacity : slot.SetupOpacity;
			SKColor? picked = AnswerTint is { } answer ? await answer() : await ColourDialog.Ask(this, $"Colour of {slot.Name}", ToSkColor(color, opacity));
			if (picked is not { } c) return;
			var newColor = new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
			float newOpacity = c.Alpha / 255f;
			bool colorChanged = Vector3.DistanceSquared(newColor, color) > 1e-6f;
			bool opacityChanged = MathF.Abs(newOpacity - opacity) > 1e-3f;
			if (colorChanged || opacityChanged) _session.SetPartColor(slot, colorChanged ? newColor : null, opacityChanged ? newOpacity : null);
		};
		return PartTint;
	}

	internal Func<Task<SKColor?>>? AnswerTint { get; set; }

	// Every frame: the swatch shows the colour at the playhead (or at rest)
	private void SyncPartTint(Slot slot)
	{
		bool pose = State.AnimateMode;
		var c = ToSkColor(pose ? slot.Color : slot.SetupColor, pose ? slot.Opacity : slot.SetupOpacity);
		var colour = Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
		if (_partTintSwatch.Background is not ISolidColorBrush brush || brush.Color != colour)
			_partTintSwatch.Background = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(colour);
	}

	private static SKColor ToSkColor(Vector3 color, float opacity) => new(
		(byte)Math.Clamp(MathF.Round(color.X * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(color.Y * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(color.Z * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(opacity * 255f), 0, 255));

	// ======================= an object's colour layers =======================

	// The Colour view of the Rig tab: the list (the last applied on top), its buttons, the picked
	// one's name, settings and parts left out
	private Control BuildColourPanel()
	{
		ToolTip.SetTip(ColourView, "The object's colour layers: like adjustment layers, over all its parts (some can be left out)");
		ColourView.Click += (_, _) => { _colourViewOn = true; _boxesViewOn = false; RefreshRigList(); };
		var add = new Button { Content = "+ Colour layer ▾" };
		add.Click += (_, _) => KindMenu(ObjectColourKinds, AddObjectColour).ShowAt(add);
		_colourDelete.Click += (_, _) =>
		{
			if (_colourLayer is not { } layer) return;
			_session.DeleteColourLayer(layer);
			_colourLayer = State.Skeleton.ColorLayers.LastOrDefault();
			FillColourPanel();
		};
		ToolTip.SetTip(_colourUp, "Applied later (on top of the others)");
		ToolTip.SetTip(_colourDown, "Applied earlier");
		_colourUp.Click += (_, _) => { if (_colourLayer is { } l) _session.MoveColourLayer(l, +1); FillColourPanel(); };
		_colourDown.Click += (_, _) => { if (_colourLayer is { } l) _session.MoveColourLayer(l, -1); FillColourPanel(); };
		ColourList.SelectionChanged += (_, _) =>
		{
			if (_syncingColour || ColourList.SelectedItem is not ListBoxItem { Tag: ColorLayer layer }) return;
			_colourLayer = layer;
			FillColourPanel();
		};
		ColourName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitColourName(); Viewport.Focus(); } };
		ColourName.LostFocus += (_, _) => CommitColourName();
		_leaveOut.Content = new ScrollViewer { MaxHeight = 200, Content = ColourLeaveOutList };
		var buttons = new WrapPanel();
		foreach (var b in new Control[] { add, _colourDelete, _colourUp, _colourDown })
		{
			b.Margin = new Thickness(0, 0, 4, 4);
			buttons.Children.Add(b);
		}
		return new ScrollViewer
		{
			Content = new StackPanel
			{
				Margin = new Thickness(4),
				Spacing = 4,
				Children = { buttons, ColourList, Labelled("Name", ColourName), ColourSettings, _leaveOut, ColourHint }
			}
		};
	}

	internal void AddObjectColour(ColorAdjustKind kind)
	{
		_colourLayer = _session.AddColourLayer(kind);
		FillColourPanel();
	}

	private void CommitColourName()
	{
		if (_syncingColour || _colourLayer is not { } layer || (ColourName.Text?.Trim() ?? "") == layer.Name) return;
		string name = ColourName.Text?.Trim() ?? "";
		if (!_session.RenameColourLayer(layer, name))
		{
			ColourHint.Text = $"Can't call it \"{name}\": a colour layer needs a name of its own.";
			ColourName.Text = layer.Name;
		}
		else FillColourPanel();
	}

	// The list and the picked one's settings
	private void FillColourPanel()
	{
		if (!ShowingColour) return;
		var skeleton = State.Skeleton;
		if (_colourLayer != null && !skeleton.ColorLayers.Contains(_colourLayer)) _colourLayer = null;
		_colourLayer ??= skeleton.ColorLayers.LastOrDefault();
		_syncingColour = true;
		var items = new List<ListBoxItem>();
		foreach (var layer in skeleton.ColorLayers.AsEnumerable().Reverse())   // (the last applied on top)
		{
			var target = layer;
			var on = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
			ToolTip.SetTip(on, "On or off");
			on.IsCheckedChanged += (_, _) => { if (!_syncingColour) _session.ShowColourLayer(target, on.IsChecked == true); };
			var text = (layer.LeftOut.Count > 0 ? $"{layer.Name}  (leaves out {layer.LeftOut.Count})" : layer.Name)
				+ (skeleton.SkinOf(layer) is { } skin ? $"  · skin {skin.Name}{(skeleton.ShownSkins.Contains(skin) ? "" : " (off)")}" : "");
			items.Add(new ListBoxItem
			{
				Tag = layer,
				Padding = new Thickness(4, 2),
				Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { on, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } }
			});
		}
		ColourList.ItemsSource = items;
		ColourList.SelectedItem = items.FirstOrDefault(i => i.Tag == _colourLayer);
		bool any = _colourLayer != null;
		_colourDelete.IsEnabled = _colourUp.IsEnabled = _colourDown.IsEnabled = any;
		ColourName.IsEnabled = any;
		_leaveOut.IsVisible = any;
		if (!ColourName.IsFocused) ColourName.Text = _colourLayer?.Name ?? "";
		_syncingColour = false;
		_colourShown = null;
		FillColourSettings(force: true);
	}

	private ColourShown ShownFor(object layer, ColorAdjust adjust, string leftOut) =>
		new(layer, ShownValues(adjust), adjust.Kind, adjust.Colorize, adjust.KeepLight, State.AnimateMode, leftOut);

	private static string LeftOutKey(ColorLayer layer) => string.Join("\n", layer.LeftOut.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));

	private void FillColourSettings(bool force)
	{
		var skeleton = State.Skeleton;
		if (_colourLayer != null && !skeleton.ColorLayers.Contains(_colourLayer)) _colourLayer = null;   // (another object, or undone)
		if (_colourLayer is not { } layer)
		{
			ColourSettings.Children.Clear();
			ColourLeaveOutList.Children.Clear();
			_colourShown = null;
			ColourHint.Text = skeleton.ColorLayers.Count == 0
				? "No colour layers yet. + Colour layer adds one: it changes the whole object's colours (parts can be left out). Exports bake them in only when asked."
				: "";
			return;
		}
		var shown = ShownFor(layer, layer.Adjust, LeftOutKey(layer));
		if (!force && (_colourShown == shown || AdjustEditor.Dragging > 0)) return;
		_colourShown = shown;
		ColourHint.Text = !State.AnimateMode ? "Build: these are its numbers at rest."
			: State.AutoKey ? $"Animate: changing a number keys it at frame {State.Frame}."
			: $"Animate, auto key off: changes show here until {_keys.Describe("anim.key")} keys them.";
		AdjustEditor.Build(ColourSettings, layer.Adjust, shown.Values,
			(values, what) =>
			{
				_session.SetColourValues(layer, values, what);
				_colourShown = ShownFor(layer, layer.Adjust, LeftOutKey(layer)) with { Values = values };   // (no rebuild for this)
			},
			(what, change) =>
			{
				_session.EditColourLayers($"{layer.Name}: {what}", () =>
				{
					change(layer.Adjust);
					return true;
				});
				FillColourSettings(force: true);
			},
			PickColour);

		ColourLeaveOutList.Children.Clear();
		var picked = State.PickedSlots.ToHashSet();
		foreach (var slot in skeleton.Slots.AsEnumerable().Reverse())
		{
			var part = slot;
			var box = new CheckBox { Content = slot.Name, IsChecked = layer.LeftOut.Contains(slot), Tag = slot, FontWeight = picked.Contains(slot) ? FontWeight.SemiBold : FontWeight.Normal };
			ToolTip.SetTip(box, "Ticked: this colour layer doesn't change it (the mouse over it outlines it in the viewport)");
			box.PointerEntered += (_, _) => _renderer.HoverSlot = part;
			box.PointerExited += (_, _) => { if (_renderer.HoverSlot == part) _renderer.HoverSlot = null; };
			box.IsCheckedChanged += (_, _) =>
			{
				_session.LeaveOut(layer, part, box.IsChecked == true);
				_leaveOut.Header = layer.LeftOut.Count > 0 ? $"Leave out ({layer.LeftOut.Count})" : "Leave out";
			};
			ColourLeaveOutList.Children.Add(box);
		}
		_leaveOut.Header = layer.LeftOut.Count > 0 ? $"Leave out ({layer.LeftOut.Count})" : "Leave out";
	}

	// ======================= colour layers in a drawing =======================

	private Control BuildPaintColourButton()
	{
		var add = new Button { Content = "+ ◐", Margin = new Thickness(0, 0, 4, 4) };
		ToolTip.SetTip(add, "A colour layer: changes the colours of the layers under it, in this drawing only");
		add.Click += (_, _) => { if (Paint.Asset != null) KindMenu(DrawingColourKinds, kind => Paint.AddColorLayer(kind)).ShowAt(add); };
		return add;
	}

	// The paint editor's settings for the picked layer, when it's a colour layer
	private void FillPaintColourSettings(bool force)
	{
		if (Paint.Asset is not { } asset || Paint.Layer is not { Adjust: { } adjust } layer)
		{
			PaintColourSettings.Children.Clear();
			_paintColourShown = null;
			return;
		}
		var shown = ShownFor(layer, adjust, "");
		if (!force && (_paintColourShown == shown || AdjustEditor.Dragging > 0)) return;
		_paintColourShown = shown;
		AdjustEditor.Build(PaintColourSettings, adjust, shown.Values,
			(values, what) =>
			{
				_session.SetDrawingColourValues(asset, layer, values, what);
				_paintColourShown = ShownFor(layer, adjust, "") with { Values = values };
			},
			(what, change) =>
			{
				_session.EditDrawingColour(asset, layer, what, change);
				FillPaintColourSettings(force: true);
			},
			PickColour);
		PaintColourSettings.Children.Insert(0, new TextBlock
		{
			Text = State.AnimateMode ? (State.AutoKey ? $"Keys at frame {State.Frame}" : "Held until keyed") : "At rest (Build)",
			Foreground = Brushes.Gray
		});
	}

	// Every frame: the settings follow the animation (another frame, another mode, an undo)
	private void SyncColourPanels()
	{
		if (!ShowingColour && _renderer.HoverSlot != null) _renderer.HoverSlot = null;
		if (ShowingColour) FillColourSettings(force: false);
		if (_session.PaintOpen) FillPaintColourSettings(force: false);
	}
}
