using System.Windows;

namespace Animator.Viewer;

// Where each edit was made: the scene, or an object, with the animation, Setup or Animate mode,
// and the paint editor (open on a part, or not). Undo and redo first bring you back there, as a
// step of their own, when you've moved on since (see UndoStack.Place): undoing while you work
// on "walk" can't quietly take back something you did to "idle".
public partial class MainWindow
{
	// The frame is where to look once back, not part of the place: scrubbing isn't moving away
	private sealed record EditPlace(SceneObject? Object, Animation? Animation, bool Animating, bool Painting, Slot? PaintSlot, int Frame)
	{
		public bool Equals(EditPlace? other) =>
			other is not null && Object == other.Object && Animation == other.Animation &&
			Animating == other.Animating && Painting == other.Painting && PaintSlot == other.PaintSlot;

		public override int GetHashCode() => HashCode.Combine(Object, Animation, Animating, Painting, PaintSlot);
	}

	private void InitPlaces()
	{
		_state.Undo.Place = CurrentPlace;
		_state.Undo.GoTo = place => { if (place is EditPlace where) GoToPlace(where); };
	}

	private bool PaintOpen => PaintPanel.Visibility == Visibility.Visible && _paint.Asset != null;

	private object CurrentPlace() => _sceneLevel
		? new EditPlace(null, null, false, false, null, 0)
		: new EditPlace(_active, _state.Animation, _state.AnimateMode, PaintOpen, PaintOpen ? _paintSlot : null, _state.Frame);

	private void GoToPlace(EditPlace place)
	{
		if (place.Object == null)
		{
			LeaveObject();
			return;
		}
		if (!_scene.Objects.Contains(place.Object)) return;   // gone: the edit itself is undone
		if (_sceneLevel || _active != place.Object) EnterObject(place.Object);
		if (place.Animation != null && _state.Animations.Contains(place.Animation) && _state.Animation != place.Animation)
			SwitchAnimation(place.Animation);
		_state.AnimateMode = place.Animating;
		_state.Playing = false;
		_state.SetFrame(place.Frame);

		if (place.Painting && place.PaintSlot is { } slot && _skeleton.Slots.Contains(slot) && PartData(slot).Asset is { } drawing)
		{
			if (!PaintOpen || _paintSlot != slot) OpenPaintEditor(drawing, slot);
		}
		else if (!place.Painting && PaintOpen) ClosePaintEditor();
		RefreshEverything();
	}

	// For the status line: where the next undo was made, when undo would take you back there first
	private string UndoHint()
	{
		if (!_state.Undo.UndoGoesBackFirst || _state.Undo.NextUndoPlace is not EditPlace place) return "";
		string where = place.Object == null ? "the scene"
			: $"{(place.Object != _active ? $"{place.Object.Name}, " : "")}{place.Animation?.Name}, {(place.Animating ? "Animate" : "Setup")} mode{(place.Painting ? ", painting" : "")}";
		return $" (made in {where}: {KeyOf("edit.undo")} goes back there first)";
	}
}
