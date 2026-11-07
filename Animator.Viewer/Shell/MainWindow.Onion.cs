using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using SkiaSharp;

namespace Animator.Viewer;

// Onion skinning: faint ghosts of the neighbouring frames in the viewport, and the part's
// previous and next drawings under the one you paint (the light table)
public partial class MainWindow
{
	private readonly OnionSkin _onion = new();
	private bool _syncingOnion;

	private void InitOnion()
	{
		OnionBefore.ItemsSource = Enumerable.Range(0, 6).ToList();
		OnionAfter.ItemsSource = Enumerable.Range(0, 6).ToList();
		OnionStep.ItemsSource = Enumerable.Range(1, 4).ToList();
		SyncOnionUi();
	}

	private void SyncOnionUi()
	{
		_syncingOnion = true;
		OnionBox.IsChecked = _onion.Enabled;
		OnionBefore.SelectedItem = _onion.Before;
		OnionAfter.SelectedItem = _onion.After;
		OnionStep.SelectedItem = _onion.Step;
		_syncingOnion = false;
	}

	private void ToggleOnion()
	{
		_onion.Enabled = !_onion.Enabled;
		SyncOnionUi();
	}

	private void Onion_Click(object sender, RoutedEventArgs e) => _onion.Enabled = OnionBox.IsChecked == true;

	private void OnionSettings_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingOnion) return;
		if (OnionBefore.SelectedItem is int before) _onion.Before = before;
		if (OnionAfter.SelectedItem is int after) _onion.After = after;
		if (OnionStep.SelectedItem is int step) _onion.Step = step;
	}
}
