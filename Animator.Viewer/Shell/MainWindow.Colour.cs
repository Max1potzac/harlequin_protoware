using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer;

// Colour layers, like Photoshop's adjustment layers:
// - on a whole object (the rig panel's Colour view): hue / saturation, brightness / contrast, tint
//   and colour balance, each leaving out any parts it shouldn't change
// - inside a drawing (the paint editor's + Colour layer): those and Replace colour, changing the
//   layers under them in that drawing only
// Their numbers belong to the rest pose in Setup mode, and are keyed in Animate mode (or held,
// with auto key off, until S keys them). The editor always shows them; exports only bake them in
// when asked to.
public partial class MainWindow
{
	private static readonly ColorAdjustKind[] ObjectColourKinds =
		{ ColorAdjustKind.HueSaturation, ColorAdjustKind.BrightnessContrast, ColorAdjustKind.Tint, ColorAdjustKind.ColorBalance };

	private static readonly ColorAdjustKind[] DrawingColourKinds =
		{ ColorAdjustKind.HueSaturation, ColorAdjustKind.BrightnessContrast, ColorAdjustKind.Tint, ColorAdjustKind.ColorBalance, ColorAdjustKind.ReplaceColor };

	private bool _colourView;          // the rig panel shows the object's colour layers
	private ColorLayer? _colourLayer;  // the one selected there
	private bool _syncingColour;

	// What the settings show now: rebuilt only when that changes (not while a slider is dragged)
	private readonly record struct ColourShown(object? Layer, AdjustValues Values, ColorAdjustKind Kind, bool Colorize,
		bool KeepLight, bool Animating, string LeftOut);
	private ColourShown? _colourShown, _paintColourShown;

	private static bool DraggingASlider => Mouse.Captured is Thumb;

	// The numbers to show: at rest in Setup mode, as the animation has them here in Animate mode
	private AdjustValues ShownValues(ColorAdjust adjust) => _state.AnimateMode ? adjust.Now : adjust.Setup;

	private Vector3? PickColour(Vector3 color, string what)
	{
		var start = new SKColor((byte)Math.Clamp(color.X * 255, 0, 255), (byte)Math.Clamp(color.Y * 255, 0, 255), (byte)Math.Clamp(color.Z * 255, 0, 255));
		var dialog = new ColorPickerDialog(start, what) { Owner = this };
		if (dialog.ShowDialog() != true) return null;
		var c = dialog.Color;
		return new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
	}

	private ContextMenu KindMenu(UIElement under, IEnumerable<ColorAdjustKind> kinds, Action<ColorAdjustKind> add)
	{
		var menu = new ContextMenu { PlacementTarget = under, Placement = PlacementMode.Bottom };
		foreach (var kind in kinds)
		{
			var item = new MenuItem { Header = ColorAdjust.KindName(kind) };
			item.Click += (_, _) => add(kind);
			menu.Items.Add(item);
		}
		return menu;
	}

	// ======================= an object's colour layers =======================

	private void ColourAdd_Click(object sender, RoutedEventArgs e) =>
		KindMenu((UIElement)sender, ObjectColourKinds, AddObjectColour).IsOpen = true;

	private void AddObjectColour(ColorAdjustKind kind)
	{
		var layer = new ColorLayer { Name = ColorLayerOps.UniqueName(_skeleton, ColorAdjust.KindName(kind)), Adjust = new ColorAdjust(kind) };
		EditColourLayers($"Add colour layer {layer.Name}", () =>
		{
			_skeleton.ColorLayers.Add(layer);
			return true;
		});
		_colourLayer = layer;
		BuildColourPanel();
	}

	private void ColourDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_colourLayer is not { } layer) return;
		EditColourAndKeys($"Delete colour layer {layer.Name}", () =>
		{
			_skeleton.ColorLayers.Remove(layer);
			SkinOps.Forget(_skeleton, layer);
			foreach (var animation in _state.Animations)
				animation.Timelines.RemoveAll(t => t is ColorLayerTimeline && t.Target == layer.Name);
			return true;
		});
		_colourLayer = _skeleton.ColorLayers.LastOrDefault();
		BuildColourPanel();
	}

	private void ColourUp_Click(object sender, RoutedEventArgs e) => MoveColour(-1);
	private void ColourDown_Click(object sender, RoutedEventArgs e) => MoveColour(+1);

	// Earlier (applied before the others) or later
	private void MoveColour(int by)
	{
		if (_colourLayer is not { } layer) return;
		var list = _skeleton.ColorLayers;
		int i = list.IndexOf(layer), j = i + by;
		if (i < 0 || j < 0 || j >= list.Count) return;
		EditColourLayers($"Move colour layer {layer.Name}", () =>
		{
			list.RemoveAt(i);
			list.Insert(j, layer);
			return true;
		});
	}

	private void ColourList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingColour || ColourList.SelectedItem is not FrameworkElement { Tag: ColorLayer layer }) return;
		_colourLayer = layer;
		BuildColourSettings(force: true);
	}

	private void ColourName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitColourName();
		Viewport.Focus();
	}

	private void ColourName_LostFocus(object sender, RoutedEventArgs e) => CommitColourName();

	private void CommitColourName()
	{
		if (_syncingColour || _colourLayer is not { } layer || ColourName.Text.Trim() == layer.Name) return;
		string name = ColourName.Text.Trim();
		bool renamed = false;
		EditColourAndKeys($"Rename colour layer to {name}", () => renamed = ColorLayerOps.Rename(_skeleton, _state.Animations, layer, name));
		if (!renamed)
		{
			ColourHint.Text = $"Can't call it \"{name}\": a colour layer needs a name of its own.";
			ColourName.Text = layer.Name;
		}
	}

	// One undo step for the colour layers themselves (none when nothing changed)
	private void EditColourLayers(string name, Func<bool> change)
	{
		var before = ColorLayersSnapshot.Capture(_skeleton);
		if (!change()) return;
		_state.Undo.Push(new SnapshotCommand<ColorLayersSnapshot>(name, before, ColorLayersSnapshot.Capture(_skeleton), s => s.Restore()));
	}

	// ...and with their keys in every animation (renaming, deleting)
	private void EditColourAndKeys(string name, Func<bool> change)
	{
		var layers = ColorLayersSnapshot.Capture(_skeleton);
		var rest = _state.CaptureStructure();
		if (!change()) return;
		_state.Undo.Push(new SnapshotCommand<(StructureSnapshot, ColorLayersSnapshot)>(name, (rest, layers),
			(_state.CaptureStructure(), ColorLayersSnapshot.Capture(_skeleton)),
			s =>
			{
				s.Item1.Restore();
				s.Item2.Restore();
			}));
	}

	// The list and the selected one's settings (the rig panel, in the Colour view)
	private void BuildColourPanel()
	{
		if (!_colourView) return;
		if (_colourLayer != null && !_skeleton.ColorLayers.Contains(_colourLayer)) _colourLayer = null;
		_colourLayer ??= _skeleton.ColorLayers.LastOrDefault();

		_syncingColour = true;
		ColourList.Items.Clear();
		foreach (var layer in _skeleton.ColorLayers.AsEnumerable().Reverse())   // the last applied on top
		{
			var shown = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "On or off" };
			var target = layer;
			shown.Click += (_, _) => EditColourLayers($"{(shown.IsChecked == true ? "Show" : "Hide")} colour layer {target.Name}", () =>
			{
				target.Visible = shown.IsChecked == true;
				return true;
			});
			var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = layer };
			row.Children.Add(shown);
			row.Children.Add(new TextBlock
			{
				Text = (layer.LeftOut.Count > 0 ? $"{layer.Name}  (leaves out {layer.LeftOut.Count})" : layer.Name)
					+ (_skeleton.SkinOf(layer) is { } skin ? $"  · skin {skin.Name}{(_skeleton.ShownSkins.Contains(skin) ? "" : " (off)")}" : ""),
				Foreground = AdjustEditor.LabelBrush
			});
			ColourList.Items.Add(row);
			if (layer == _colourLayer) ColourList.SelectedItem = row;
		}
		bool any = _colourLayer != null;
		ColourDeleteButton.IsEnabled = ColourUpButton.IsEnabled = ColourDownButton.IsEnabled = any;
		ColourNameRow.Visibility = ColourLeaveOut.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
		if (!ColourName.IsKeyboardFocused) ColourName.Text = _colourLayer?.Name ?? "";
		_syncingColour = false;
		BuildColourSettings(force: false);
	}

	private ColourShown ShownFor(object layer, ColorAdjust adjust, string leftOut) =>
		new(layer, ShownValues(adjust), adjust.Kind, adjust.Colorize, adjust.KeepLight, _state.AnimateMode, leftOut);

	private static string LeftOutKey(ColorLayer layer) => string.Join("\n", layer.LeftOut.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));

	private void BuildColourSettings(bool force)
	{
		if (_colourLayer != null && !_skeleton.ColorLayers.Contains(_colourLayer)) _colourLayer = null;   // (another object, or undone)
		if (_colourLayer is not { } layer)
		{
			ColourSettings.Children.Clear();
			ColourLeaveOutList.Children.Clear();
			_colourShown = null;
			ColourHint.Text = _skeleton.ColorLayers.Count == 0
				? "No colour layers yet. + Colour layer adds one: it changes the whole object's colours (parts can be left out). Exports bake them in only when asked."
				: "";
			return;
		}
		var shown = ShownFor(layer, layer.Adjust, LeftOutKey(layer));
		if (!force && _colourShown == shown) return;
		if (!force && DraggingASlider) return;
		_colourShown = shown;
		ColourHint.Text = !_state.AnimateMode ? "Setup: these are its numbers at rest."
			: _state.AutoKey ? $"Animate: changing a number keys it at frame {_state.Frame}."
			: $"Animate, auto key off: changes show here until {KeyOf("anim.key")} keys them.";

		AdjustEditor.Build(ColourSettings, layer.Adjust, shown.Values,
			(values, what) => SetObjectColour(layer, values, what),
			(what, change) =>
			{
				EditColourLayers($"{layer.Name}: {what}", () =>
				{
					change(layer.Adjust);
					return true;
				});
				BuildColourSettings(force: true);
			},
			PickColour);

		ColourLeaveOutList.Children.Clear();
		foreach (var slot in _skeleton.Slots.AsEnumerable().Reverse())
		{
			var box = new CheckBox
			{
				Content = slot.Name, IsChecked = layer.LeftOut.Contains(slot), Foreground = AdjustEditor.LabelBrush,
				Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(0, 1, 0, 1),
				ToolTip = "Ticked: this colour layer doesn't change it. (The mouse over it outlines it in the viewport; picked parts stand out here.)"
			};
			var part = slot;
			box.MouseEnter += (_, _) => _renderer.HoverSlot = part;   // outlined in the viewport
			box.MouseLeave += (_, _) => { if (_renderer.HoverSlot == part) _renderer.HoverSlot = null; };
			box.Click += (_, _) => EditColourLayers($"{layer.Name}: {(box.IsChecked == true ? "leave out" : "take in")} {part.Name}", () =>
				box.IsChecked == true ? layer.LeftOut.Add(part) : layer.LeftOut.Remove(part));
			ColourLeaveOutList.Children.Add(new Border { Child = box, Tag = slot, CornerRadius = new CornerRadius(2) });   // (its background: picked or not)
		}
		ColourLeaveOut.Header = layer.LeftOut.Count > 0 ? $"Leave out ({layer.LeftOut.Count})" : "Leave out";
		_leaveOutPicks = null;
		SyncLeaveOutPicks();
	}

	// A number changed: the rest value in Setup mode; in Animate mode a key here (or, with auto key
	// off, held until it's keyed). A slider drag is one undo step.
	private void SetObjectColour(ColorLayer layer, AdjustValues values, string what)
	{
		var adjust = layer.Adjust;
		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			if (!_state.AutoKey)
				_state.Hold.HoldColor(layer, values, _skeleton, _state.Animation, _state.Frame);
			else
			{
				var animation = _state.Animation;
				var before = AnimationSnapshot.Capture(animation);
				animation.GetOrCreate(layer.Name, n => new ColorLayerTimeline(n)).Track.Add(_state.Time, values);
				adjust.Now = values;
				_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"Key {layer.Name} {what.ToLowerInvariant()} @ frame {_state.Frame}",
					before, AnimationSnapshot.Capture(animation), s => s.Restore(), mergeKey: $"colour key {layer.Name} {what} {_state.Frame}"));
			}
		}
		else
		{
			var before = ColorLayersSnapshot.Capture(_skeleton);
			adjust.Setup = values;
			adjust.Now = values;
			_state.Undo.Push(new SnapshotCommand<ColorLayersSnapshot>($"{layer.Name}: {what.ToLowerInvariant()}",
				before, ColorLayersSnapshot.Capture(_skeleton), s => s.Restore(), mergeKey: $"colour {layer.Name} {what}"));
		}
		_colourShown = ShownFor(layer, adjust, LeftOutKey(layer)) with { Values = values };   // (no rebuild for this)
	}

	// ======================= colour layers in a drawing =======================

	private void LayerAddColour_Click(object sender, RoutedEventArgs e)
	{
		if (_paint.Asset == null) return;
		KindMenu((UIElement)sender, DrawingColourKinds, kind => _paint.AddColorLayer(kind)).IsOpen = true;
	}

	// The paint editor's settings for the selected layer, when it's a colour layer
	private void BuildPaintColourSettings(bool force)
	{
		if (_paint.Asset is not { } asset || _paint.Layer is not { Adjust: { } adjust } layer)
		{
			LayerAdjustHost.Children.Clear();
			_paintColourShown = null;
			return;
		}
		var shown = ShownFor(layer, adjust, "");
		if (!force && _paintColourShown == shown) return;
		if (!force && DraggingASlider) return;
		_paintColourShown = shown;

		AdjustEditor.Build(LayerAdjustHost, adjust, shown.Values,
			(values, what) => SetDrawingColour(asset, layer, values, what),
			(what, change) =>
			{
				var before = LayerAdjustSnapshot.Capture(asset, layer);
				change(adjust);
				asset.MarkEdited();
				_state.Undo.Push(new SnapshotCommand<LayerAdjustSnapshot>($"{layer.Name}: {what}", before,
					LayerAdjustSnapshot.Capture(asset, layer), s => s.Restore()));
				BuildPaintColourSettings(force: true);
			},
			PickColour);
		LayerAdjustHost.Children.Insert(0, new TextBlock
		{
			Text = _state.AnimateMode ? (_state.AutoKey ? $"Keys at frame {_state.Frame}" : "Held until keyed") : "At rest (Setup)",
			Foreground = AdjustEditor.FaintBrush,
			Margin = new Thickness(0, 0, 0, 2)
		});
	}

	private void SetDrawingColour(Asset asset, AssetLayer layer, AdjustValues values, string what)
	{
		if (layer.Adjust is not { } adjust) return;
		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			if (!_state.AutoKey)
				_state.Hold.HoldColor(asset, layer, values, _skeleton, _state.Animation, _state.Frame);
			else
			{
				var animation = _state.Animation;
				var before = AnimationSnapshot.Capture(animation);
				animation.GetOrCreate(LayerAdjustTimeline.TargetOf(layer), n => new LayerAdjustTimeline(n) { Caption = $"{asset.Name}: {layer.Name}" })
					.Track.Add(_state.Time, values);
				adjust.Now = values;
				asset.MarkEdited();
				_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"Key {layer.Name} {what.ToLowerInvariant()} @ frame {_state.Frame}",
					before, AnimationSnapshot.Capture(animation), s => s.Restore(), mergeKey: $"layer colour key {layer.Id} {what} {_state.Frame}"));
			}
		}
		else
		{
			var before = LayerAdjustSnapshot.Capture(asset, layer);
			adjust.Setup = values;
			adjust.Now = values;
			asset.MarkEdited();
			_state.Undo.Push(new SnapshotCommand<LayerAdjustSnapshot>($"{layer.Name}: {what.ToLowerInvariant()}", before,
				LayerAdjustSnapshot.Capture(asset, layer), s => s.Restore(), mergeKey: $"layer colour {layer.Id} {what}"));
		}
		_paintColourShown = ShownFor(layer, adjust, "") with { Values = values };
	}

	// The picked parts stand out in the Leave out list (in the viewport's selection colour), so
	// what's selected there is easy to find and tick
	private string? _leaveOutPicks;

	private void SyncLeaveOutPicks()
	{
		string now = string.Join("\n", _state.PickedSlots.Select(s => s.Name));
		if (now == _leaveOutPicks) return;
		_leaveOutPicks = now;
		var picked = _state.PickedSlots.ToHashSet();
		foreach (var row in ColourLeaveOutList.Children.OfType<Border>())
		{
			bool mine = row.Tag is Slot slot && picked.Contains(slot);
			row.Background = mine ? PickedRowBrush : System.Windows.Media.Brushes.Transparent;
			if (row.Child is CheckBox box) box.FontWeight = mine ? FontWeights.SemiBold : FontWeights.Normal;
		}
		if (picked.Count > 0 && ColourLeaveOutList.Children.OfType<Border>().FirstOrDefault(b => b.Tag is Slot s && picked.Contains(s)) is { } first)
			first.BringIntoView();
	}

	// Every frame: the settings follow the animation (another frame, another mode, an undo)
	private void SyncColourPanels()
	{
		if (!_colourView && _renderer.HoverSlot != null) _renderer.HoverSlot = null;
		if (_colourView && !_sceneLevel)
		{
			BuildColourSettings(force: false);
			SyncLeaveOutPicks();
		}
		if (PaintPanel.Visibility == Visibility.Visible) BuildPaintColourSettings(force: false);
	}
}
