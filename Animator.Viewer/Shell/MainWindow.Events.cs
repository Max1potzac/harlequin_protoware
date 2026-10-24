using System.Windows.Controls;

namespace Animator.Viewer;

// Event markers: adding and editing them, the timeline's right-click menus,
// and the little flash in the viewport when playback passes one
public partial class MainWindow
{
	private double _eventFlashUntil;

	// ---------- playback ----------

	// Called every frame while playing, with the time before and after this frame
	private void FireEvents(float from, float to)
	{
		if (_state.Animation.Events is not { } events) return;

		bool wrapped = to < from;   // a looping animation went past its end and started over
		var fired = wrapped
			? events.Between(from, _state.Animation.Duration).Concat(events.Between(-1f, to))
			: events.Between(from, to);

		var names = fired.Select(e => e.Data == null ? e.Name : $"{e.Name} ({e.Data})").ToList();
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
		var e = new AnimationEvent(dialog.EventName, dialog.EventData);
		_state.EditStructure(() =>
		{
			animation.GetOrCreateEvents().Add(time, e);   // an event already on this frame is replaced
			return $"Event {e.Name} @ frame {frame}";
		});
	}

	private void EditEventAt(EventTimeline events, int frame)
	{
		int Find() => events.Track.Keys.FindIndex(k => (int)MathF.Round(k.Time * _state.Fps) == frame);

		int index = Find();
		if (index < 0) return;
		var current = events.Track.Keys[index].Value;

		var dialog = new EventDialog(frame, current.Name, current.Data) { Owner = this };
		if (dialog.ShowDialog() != true) return;

		var updated = new AnimationEvent(dialog.EventName, dialog.EventData);
		if (updated == current) return;

		_state.EditStructure(() =>
		{
			int i = Find();
			if (i < 0) return null;
			events.Track.Keys[i] = events.Track.Keys[i] with { Value = updated };
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
			foreach (var curve in Enum.GetValues<CurveType>())
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
			menu.Items.Add(new Separator());
		}

		var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
		copy.Click += (_, _) => _timeline.Copy();
		menu.Items.Add(copy);

		var delete = new MenuItem { Header = "Delete", InputGestureText = "Del" };
		delete.Click += (_, _) => _timeline.DeleteSelected();
		menu.Items.Add(delete);

		menu.IsOpen = true;   // opens at the mouse position by default
	}

	// Right-click on an empty spot
	private void ShowTimelineMenu(int frame, (int First, int Last)? picked = null)
	{
		var menu = new ContextMenu();
		if (picked is { } range) AddSequenceItems(menu, range);
		var add = new MenuItem { Header = $"Add event at frame {frame}…" };
		add.Click += (_, _) => AddEventAt(frame);
		menu.Items.Add(add);
		menu.IsOpen = true;
	}
}
