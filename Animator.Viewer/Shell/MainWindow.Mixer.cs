using System.Windows;

namespace Animator.Viewer;

// The mixer (the timeline bar's Mixer… button): the open object's animations on tracks over each
// other, and fades from one to another tried out and kept (see MixerWindow). While it plays, the
// viewport shows the mix on its own clock instead of the timeline's.
public partial class MainWindow
{
	private readonly MixPreview _mixPreview = new();
	private MixerWindow? _mixer;
	private SceneObject? _mixerObject;
	private bool _mixing;
	private float _mixClock;

	private void Mixer_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel || _active is not { } obj) return;
		if (_mixer != null)
		{
			_mixer.Activate();
			return;
		}
		_mixerObject = obj;
		_mixer = new MixerWindow(obj.Name, _state.Animations, _skeleton.Mixes, _mixPreview,
			(name, change) => _state.EditStructure(() => change() ? name : null),
			PlayMix) { Owner = this };
		_mixer.Closed += (_, _) =>
		{
			_mixer = null;
			_mixerObject = null;
		};
		_mixer.Show();
	}

	private void PlayMix(bool on)
	{
		_mixing = on;
		_mixClock = 0f;
		if (on) _state.Playing = false;   // (the timeline stands still meanwhile)
		_skeleton.ResetPhysics();
		AnimHint.Text = on ? "The mixer is playing: the viewport shows the mix." : "";
	}

	// Every frame: the mixer's clock runs; it closes when its object isn't open any more
	private void AdvanceMixer(float dt)
	{
		if (_mixer != null && (_sceneLevel || _mixerObject != _active))
		{
			_mixer.Close();
			return;
		}
		if (_mixing) _mixClock += dt * _state.Speed;
	}

	// The open object posed by the mixer (true), or not playing (false)
	private bool PoseMix()
	{
		if (!_mixing || _sceneLevel) return false;
		_mixPreview.Pose(_skeleton, _mixClock);
		_skeleton.StepPhysics(_mixClock);
		return true;
	}
}
