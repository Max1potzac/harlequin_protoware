using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;

namespace Animator.Viewer;

// Effects (VFX) in the part panel: blend mode, tint and opacity.
// Animate mode keys tint and opacity at the playhead; Setup mode changes the part's rest
// values. The blend mode is a rest setting for the whole animation (like in game engines).
public partial class MainWindow
{
	private void InitEffects()
	{
		BlendBox.ItemsSource = Enum.GetValues<BlendMode>();
	}

	// Called every frame (from RefreshPartValues): shows this frame's colour in Animate mode
	private void SyncEffects(Slot slot)
	{
		bool pose = _state.AnimateMode;
		var shown = ToSkColor(pose ? slot.Color : slot.SetupColor, pose ? slot.Opacity : slot.SetupOpacity);
		var wpf = ToWpf(shown);
		if (PartColorSwatch.Fill is not SolidColorBrush brush || brush.Color != wpf)
			PartColorSwatch.Fill = new SolidColorBrush(wpf);

		SetField(PartOpacity, Num(MathF.Round((pose ? slot.Opacity : slot.SetupOpacity) * 100f, 1)));

		if (BlendBox.IsDropDownOpen || Equals(BlendBox.SelectedItem, slot.Blend)) return;
		bool wasSyncing = _syncingRig;
		_syncingRig = true;   // so this doesn't count as you choosing something
		BlendBox.SelectedItem = slot.Blend;
		_syncingRig = wasSyncing;
	}

	private void BlendBox_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || _shownSlot is not { } slot || BlendBox.SelectedItem is not BlendMode blend || blend == slot.Blend)
			return;
		_state.EditStructure(() =>
		{
			slot.Blend = blend;
			return $"{slot.Name} blends: {blend}";
		});
	}

	private void PartColorSwatch_Click(object sender, MouseButtonEventArgs e)
	{
		if (_shownSlot is not { } slot) return;
		bool pose = _state.AnimateMode;
		var color = pose ? slot.Color : slot.SetupColor;
		float opacity = pose ? slot.Opacity : slot.SetupOpacity;

		var dialog = new ColorPickerDialog(ToSkColor(color, opacity), $"Colour of {slot.Name}") { Owner = this };
		if (dialog.ShowDialog() != true) return;

		var picked = dialog.Color;
		var newColor = new Vector3(picked.Red / 255f, picked.Green / 255f, picked.Blue / 255f);
		float newOpacity = picked.Alpha / 255f;
		bool colorChanged = Vector3.DistanceSquared(newColor, color) > 1e-6f;
		bool opacityChanged = MathF.Abs(newOpacity - opacity) > 1e-3f;
		if (colorChanged || opacityChanged)
			SetPartColor(slot, colorChanged ? newColor : null, opacityChanged ? newOpacity : null);
	}

	private void PartOpacity_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitPartOpacity();
		Viewport.Focus();
	}

	private void PartOpacity_LostFocus(object sender, RoutedEventArgs e) => CommitPartOpacity();

	private void CommitPartOpacity()
	{
		if (_syncingRig || _shownSlot is not { } slot) return;
		float current = _state.AnimateMode ? slot.Opacity : slot.SetupOpacity;
		string text = PartOpacity.Text.Trim().TrimEnd('%');
		if (text == Num(MathF.Round(current * 100f, 1))) return;

		if (!TryNum(text, out float percent))
		{
			PartHint.Text = $"\"{PartOpacity.Text}\" isn't a number.";
			PartOpacity.Text = Num(MathF.Round(current * 100f, 1));
			return;
		}
		float opacity = Math.Clamp(percent, 0f, 100f) / 100f;
		if (MathF.Abs(opacity - current) > 1e-4f) SetPartColor(slot, null, opacity);
	}

	// Animate mode: colour and / or opacity keys at the playhead. Setup mode: the rest values.
	private void SetPartColor(Slot slot, Vector3? color, float? opacity)
	{
		bool key = _state.AnimateMode;
		int frame = SnapToFrame(key);
		float time = _state.Time;
		string what = color != null && opacity != null ? "colour and opacity" : color != null ? "colour" : "opacity";

		_state.EditStructure(() =>
		{
			if (key)
			{
				if (color is { } c)
					_state.Animation.GetOrCreate(slot.Name, n => new PartColorTimeline(n)).Track.Add(time, c);
				if (opacity is { } o)
					_state.Animation.GetOrCreate(slot.Name, n => new PartOpacityTimeline(n)).Track.Add(time, o);
				return $"Key {slot.Name} {what} @ frame {frame}";
			}

			if (color is { } rest) slot.SetupColor = rest;
			if (opacity is { } restOpacity) slot.SetupOpacity = restOpacity;
			return $"Set {slot.Name} {what}";
		});
	}

	private static SKColor ToSkColor(Vector3 color, float opacity) => new(
		(byte)Math.Clamp(MathF.Round(color.X * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(color.Y * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(color.Z * 255f), 0, 255),
		(byte)Math.Clamp(MathF.Round(opacity * 255f), 0, 255));
}
