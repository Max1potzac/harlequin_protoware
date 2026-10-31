using System.Windows.Controls;

namespace Animator.Viewer;

// Event markers: adding and editing them, the timeline's right-click menus,
// and the little flash in the viewport when playback passes one
public partial class MainWindow
{
	private double _eventFlashUntil;

	// ---------- playback ----------

	// Called every frame while playing, with the time before and after this frame
	// The stretches of time the playhead went past (see EditorState.Advance)
	private void FireEvents(IReadOnlyList<(float From, float To)> passed)
	{
		if (_state.Animation.Events is not { } events) return;

		var fired = passed.SelectMany(span => events.Between(span.From, span.To));

		var list = fired.ToList();
		foreach (var e in list) PlayEventSound(e);
		var names = list.Select(e => e.Data == null ? e.Name : $"{e.Name} ({e.Data})").ToList();
		if (names.Count == 0) return;

		EventFlash.Text = "⚑ " + string.Join("    ⚑ ", names);
		_eventFlashUntil = _clock.Elapsed.TotalSeconds + 0.8;
	}

	private void UpdateEventFlash()
	{
		if (EventFlash.Text.Length > 0 && _clock.Elapsed.TotalSeconds > _eventFlashUntil)
			EventFlash.Text = "";
	}

	// ---------- adding & editing ----------

	private void AddEventAt(int frame)
	{
		_state.Playing = false;
		var dialog = new EventDialog(frame) { Owner = this };
		if (dialog.ShowDialog() != true) return;

		var animation = _state.Animation;
		float time = frame / (float)_state.Fps;
		var e = new AnimationEvent(dialog.EventName, dialog.EventData, dialog.Volume, dialog.Pitch);
		EditStructureAndScene(() =>
		{
			animation.GetOrCreateEvents().Add(time, e);   // an event already on this frame is replaced
			if (dialog.PickedSound is { } sound) _scene.Sounds[e.Name] = StoredSoundPath(sound);
			return $"Event {e.Name} @ frame {frame}";
		});
	}

	private void EditEventAt(EventTimeline events, int frame)
	{
		int Find() => events.Track.Keys.FindIndex(k => (int)MathF.Round(k.Time * _state.Fps) == frame);

		int index = Find();
		if (index < 0) return;
		var current = events.Track.Keys[index].Value;

		var dialog = new EventDialog(frame, current.Name, current.Data, current.Volume, current.Pitch,
			_scene.Sounds.TryGetValue(current.Name, out var mapped) ? mapped : null) { Owner = this };
		if (dialog.ShowDialog() != true) return;

		var updated = new AnimationEvent(dialog.EventName, dialog.EventData, dialog.Volume, dialog.Pitch);
		if (updated == current && dialog.PickedSound == null) return;

		EditStructureAndScene(() =>
		{
			int i = Find();
			if (i < 0) return null;
			events.Track.Keys[i] = events.Track.Keys[i] with { Value = updated };
			if (dialog.PickedSound is { } sound) _scene.Sounds[updated.Name] = StoredSoundPath(sound);
			return $"Edit event {updated.Name}";
		});
	}

	// ---------- timeline right-click menus ----------

	// Right-click on keys
	private void ShowKeyMenu((int First, int Last)? picked = null)
	{
		var menu = new ContextMenu();
		if (picked is { } range) AddSequenceItems(menu, range);

		if (_timeline.SelectionIsEvents)
		{
			if (_timeline.SingleSelectedEvent is { } ev)
			{
				var edit = new MenuItem { Header = "Edit event…" };
				edit.Click += (_, _) => EditEventAt(ev.Events, ev.Frame);
				menu.Items.Add(edit);
				menu.Items.Add(new Separator());
			}
		}
		else
		{
			var current = _timeline.SelectedCurve();
			foreach (var curve in new[] { CurveType.Linear, CurveType.Smooth, CurveType.Stepped })
			{
				var item = new MenuItem
				{
					Header = curve.ToString(),
					IsCheckable = true,
					IsChecked = current == curve   // tick mark shows the current curve
				};
				item.Click += (_, _) => _timeline.SetCurve(curve);
				menu.Items.Add(item);
			}
			AddEasingItems(menu, _timeline.SetBezier);
			menu.Items.Add(new Separator());
		}
		AddKeyTimingItems(menu);

		var copy = new MenuItem { Header = "Copy", InputGestureText = _keys.Gesture("edit.copy") };
		copy.Click += (_, _) => _timeline.Copy();
		menu.Items.Add(copy);

		var delete = new MenuItem { Header = "Delete", InputGestureText = _keys.Gesture("edit.delete") };
		delete.Click += (_, _) => _timeline.DeleteSelected();
		menu.Items.Add(delete);

		menu.IsOpen = true;   // opens at the mouse position by default
	}

	// Right-click on an empty spot
	private void ShowTimelineMenu(int frame, (int First, int Last)? picked = null)
	{
		var menu = new ContextMenu();
		if (picked is { } range) AddSequenceItems(menu, range);
		AddFrameItems(menu, frame);
		var add = new MenuItem { Header = $"Add event at frame {frame}…" };
		add.Click += (_, _) => AddEventAt(frame);
		menu.Items.Add(add);

		// Every key of what's picked: then dragging it in the viewport moves them all alike
		int picks = _state.Picked.Count;
		var keys = new MenuItem
		{
			Header = picks > 1 ? $"Select every key of the {picks} picked" : "Select every key of the picked one",
			InputGestureText = _keys.Gesture("pick.keys"),
			IsEnabled = picks > 0
		};
		keys.Click += (_, _) => SelectKeysOfPicked();
		menu.Items.Add(keys);
		menu.IsOpen = true;
	}
}
