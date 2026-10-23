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
		_paintCanvas.LightTable = LightTableImages;
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

	// ======================= viewport =======================

	// Draws the ghosts behind the current frame, farthest first. Each one poses the rig at
	// its own frame; afterwards the rig goes back to the frame you're on. Not while playing.
	private void DrawOnionGhosts(SKCanvas canvas)
	{
		if (!_onion.Enabled || _state.Playing || !_renderer.ShowImages) return;

		bool drew = false;
		for (int k = _onion.Before; k >= 1; k--)
			drew |= DrawGhostAt(canvas, _state.Frame - k * _onion.Step, before: true, k, _onion.Before);
		for (int k = _onion.After; k >= 1; k--)
			drew |= DrawGhostAt(canvas, _state.Frame + k * _onion.Step, before: false, k, _onion.After);

		if (drew) PoseAt(_state.Time);
	}

	private bool DrawGhostAt(SKCanvas canvas, int frame, bool before, int distance, int count)
	{
		int frames = _state.FrameCount;
		if (frames <= 0) return false;
		if (_state.Animation.Loop) frame = (frame % frames + frames) % frames;   // a loop wraps around
		else if (frame < 0 || frame > frames) return false;
		if (frame == _state.Frame) return false;

		PoseAt(frame / (float)_state.Fps);
		_renderer.DrawGhost(canvas, _skeleton, before, OnionSkin.Opacity(distance, count));
		return true;
	}

	// The whole pose pipeline at one time: rest pose, the animation's keys, then constraints
	private void PoseAt(float time)
	{
		_skeleton.SetToSetupPose();
		_state.Animation.Apply(_skeleton, time);
		_skeleton.UpdatePose();
	}

	// ======================= paint editor (light table) =======================

	// The part's previous and next drawings, placed relative to the drawing being painted
	// (all drawings of a part sit on the same bone, so only their own placements matter)
	private IReadOnlyList<LightTableImage> LightTableImages()
	{
		var images = new List<LightTableImage>();
		if (!_onion.Enabled || _state.Playing || _paintSlot is not { } slot || _paint.Asset is not { } open)
			return images;

		var painted = _skeleton.AllSkins().SelectMany(s => s.AttachmentsFor(slot.Name)).FirstOrDefault(a => a.AssetId == open.Id);
		if (painted == null || !Matrix3x2.Invert(painted.LocalMatrix(open.Width, open.Height), out var toPainted))
			return images;

		var (earlier, later) = DrawingOps.Neighbors(_state.Animation, slot, _state.Time, _onion.Before, _onion.After);
		AddGhosts(earlier, before: true, _onion.Before);
		AddGhosts(later, before: false, _onion.After);
		return images;

		void AddGhosts(List<string> names, bool before, int count)
		{
			for (int k = names.Count; k >= 1; k--)   // farthest first, so the nearest ends up on top
			{
				if (DrawingOps.Find(_skeleton, slot, names[k - 1]) is not { } drawing
					|| drawing.AssetId == open.Id
					|| !_skeleton.Assets.TryGetValue(drawing.AssetId, out var asset)
					|| _assets.Get(asset) is not { } bitmap)
					continue;

				var placement = drawing.LocalMatrix(asset.Width, asset.Height) * toPainted;
				images.Add(new LightTableImage(bitmap, placement, before, OnionSkin.Opacity(k, count)));
			}
		}
	}
}
