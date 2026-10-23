using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer;

// Detach / attach ("Follows" in the bone panel) and the throw / fall helper
public partial class MainWindow
{
	// An entry in the "Follows" dropdown; ToString is what the ComboBox displays
	private sealed record SpaceChoice(string? Space, string Label)
	{
		public override string ToString() => Label;
	}

	private ThrowSettings _lastThrow = new();   // the throw dialog remembers your last settings

	// Rebuilds the dropdown's choices (when the selected bone or the rig changes)
	private void ShowSpaceChoices(Bone? bone)
	{
		var choices = new List<SpaceChoice>();
		if (bone != null)
		{
			if (bone.Parent is { } parent)
			{
				choices.Add(new SpaceChoice(null, $"its parent ({parent.Name})"));
				choices.Add(new SpaceChoice(SpaceTimeline.World, "nothing (detached)"));
			}
			else
			{
				choices.Add(new SpaceChoice(null, "nothing (root bone)"));
			}

			// Any other bone can hold it, except itself and the bones below it (that would be a loop)
			choices.AddRange(_skeleton.AllBones()
				.Where(b => b != bone && b != bone.Parent && !b.IsDescendantOf(bone))
				.Select(b => new SpaceChoice(b.Name, $"held by {b.Name}")));
		}

		SpaceBox.ItemsSource = choices;
		SyncSpaceChoice();
	}

	// Shows what the bone follows at the current frame (called every frame)
	private void SyncSpaceChoice()
	{
		var bone = _shownBone;
		bool animate = _state.AnimateMode && bone != null;
		SpaceBox.IsEnabled = animate;
		ThrowButton.IsEnabled = animate;

		if (bone == null || SpaceBox.IsDropDownOpen || SpaceBox.ItemsSource is not List<SpaceChoice> choices) return;

		string? current = SpaceSwitching.SpaceAt(_state.Animation, bone, _state.Time);
		var match = choices.FirstOrDefault(c => c.Space == current);
		if (Equals(SpaceBox.SelectedItem, match)) return;

		bool wasSyncing = _syncingRig;
		_syncingRig = true;   // so this doesn't count as you choosing something
		SpaceBox.SelectedItem = match;
		_syncingRig = wasSyncing;
	}

	private void SpaceBox_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingRig || !_state.AnimateMode) return;
		if (_shownBone is not { } bone || SpaceBox.SelectedItem is not SpaceChoice choice) return;

		_state.Playing = false;
		_state.SetFrame(_state.Frame);   // switches always happen exactly on a frame
		int frame = _state.Frame;

		bool changed = _state.EditStructure(() =>
			SpaceSwitching.Switch(_skeleton, _state.Animation, bone, frame, _state.Fps, choice.Space)
				? $"{bone.Name} follows {choice.Label} @ frame {frame}"
				: null);

		if (!changed) SyncSpaceChoice();   // put the dropdown back to what's really there
	}

	private void Throw_Click(object sender, RoutedEventArgs e)
	{
		if (_shownBone is not { } bone || !_state.AnimateMode) return;

		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		int frame = _state.Frame;
		if (frame >= _state.FrameCount)
		{
			RigHint.Text = "Move the playhead back a bit: the flight needs frames after this one to happen in.";
			return;
		}

		// The floor starts at the character's lowest point, as you see it right now
		var dialog = new ThrowDialog(bone.Name, frame, _lastThrow with { GroundY = LowestPoint() }) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		_lastThrow = dialog.Settings;

		bool thrown = _state.EditStructure(() =>
			ThrowBake.Bake(_skeleton, _state.Animation, bone, frame, _state.Fps, dialog.Settings)
				? $"Throw {bone.Name} @ frame {frame}"
				: null);

		if (!thrown) RigHint.Text = "Nothing to bake: there are no frames between here and the end (or the next attach).";
	}

	// The lowest point of every visible part in the current pose (bones, if there are no parts)
	private float LowestPoint()
	{
		float lowest = float.MaxValue;
		foreach (var slot in _skeleton.Slots)
			if (PartGizmo.For(_skeleton, slot, _camera.Zoom) is { } handles)
				foreach (var corner in handles.Corners)
					lowest = MathF.Min(lowest, corner.Y);

		if (lowest == float.MaxValue)
			foreach (var b in _skeleton.AllBones())
				lowest = MathF.Min(lowest, MathF.Min(b.World.Translation.Y, b.TipWorld.Y));

		return lowest == float.MaxValue ? 0f : lowest;
	}
}
