namespace Animator.Desktop;

// The mixer (the animation bar's Mixer…): the open object's animations on tracks over each
// other, and fades from one to another tried out and kept (see MixerWindow). While it plays, the
// viewport shows the mix on its own clock instead of the timeline's.
public sealed partial class MainWindow
{
	private readonly MixPreview _mixPreview = new();
	private MixerWindow? _mixerWindow;
	private SceneObject? _mixerObject;
	private bool _mixing;
	private float _mixClock;

	internal MixerWindow? Mixer => _mixerWindow;
	internal bool Mixing => _mixing;

	internal void OpenMixer()
	{
		if (SceneLevel || _session.Active is not { } obj) return;
		if (_mixerWindow != null)
		{
			_mixerWindow.Activate();
			return;
		}
		_mixerObject = obj;
		_mixerWindow = new MixerWindow(obj.Name, State.Animations, State.Skeleton.Mixes, _mixPreview,
			(name, change) => State.EditStructure(() => change() ? name : null),
			PlayMix);
		_mixerWindow.Closed += (_, _) =>
		{
			_mixerWindow = null;
			_mixerObject = null;
		};
		_mixerWindow.Show(this);
	}

	private void PlayMix(bool on)
	{
		_mixing = on;
		_mixClock = 0f;
		if (on) State.Playing = false;   // (the timeline stands still meanwhile)
		State.Skeleton.ResetPhysics();
		AnimHint(on ? "The mixer is playing: the viewport shows the mix." : "");
	}

	// Every frame: the mixer's clock runs; it closes when its object isn't open any more
	private void AdvanceMixer(float dt)
	{
		if (_mixerWindow != null && (SceneLevel || _mixerObject != _session.Active))
		{
			_mixerWindow.Close();
			return;
		}
		if (_mixing) _mixClock += dt * State.Speed;
	}

	// The open object posed by the mixer (true), or not playing (false)
	private bool PoseMix()
	{
		if (!_mixing || SceneLevel) return false;
		_mixPreview.Pose(State.Skeleton, _mixClock);
		State.Skeleton.StepPhysics(_mixClock);
		return true;
	}
}
