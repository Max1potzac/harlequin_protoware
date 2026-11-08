using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The mixer (the animation bar's Mixer…, as the Windows editor has it): an object's animations
// together. Tracks played over each other, a fade from one animation to another tried out, and
// the fade times kept for the game (and a scene clip's Fade as mixed). It stays open beside the
// main window; while it plays, the viewport shows the mix instead of the timeline.
public sealed class MixerWindow : Window
{
	private sealed record Choice(string Name, Animation? Animation)
	{
		public override string ToString() => Name;
	}

	private sealed record AnimationItem(Animation Animation)
	{
		public override string ToString() => Animation.Name;
	}

	private readonly IReadOnlyList<Animation> _animations;
	private readonly MixPlan _plan;
	private readonly MixPreview _preview;
	private readonly Func<string, Func<bool>, bool> _edit;   // an undoable change to the plan
	private readonly Action<bool> _play;                     // the viewport showing the preview (or not)
	private bool _syncing;

	internal readonly List<(ComboBox Pick, Slider Weight, TextBox Speed, CheckBox Loop)> TrackRows = new();
	internal readonly ComboBox FromBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly ComboBox ToBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox DurationBox = new() { Width = 64 };
	internal readonly ToggleButton TryButton = new() { Content = "▶ Try the fade" };
	internal readonly Button KeepButton = new() { Content = "Keep for this pair" };
	internal readonly Button ForgetButton = new() { Content = "Back to the default" };
	internal readonly TextBox DefaultBox = new() { Width = 64 };
	internal readonly ListBox PairList = new() { Height = 90 };
	internal readonly ToggleButton PreviewButton = new() { Content = "▶ Play the tracks" };
	internal readonly TextBlock Hint = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };

	public MixerWindow(string objectName, IReadOnlyList<Animation> animations, MixPlan plan, MixPreview preview,
		Func<string, Func<bool>, bool> edit, Action<bool> play)
	{
		_animations = animations;
		_plan = plan;
		_preview = preview;
		_edit = edit;
		_play = play;
		Title = "Mixer";
		Width = 520;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
		static Control Spaced(Control c, double right = 6)
		{
			c.Margin = new Thickness(0, 0, right, 0);
			return c;
		}

		var tracks = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,170,*,60,Auto"), Margin = new Thickness(0, 0, 0, 10) };
		BuildTracks(tracks);

		var items = animations.Select(a => new AnimationItem(a)).ToList();
		FromBox.ItemsSource = items;
		ToBox.ItemsSource = items;
		if (items.Count > 0) FromBox.SelectedIndex = 0;
		if (items.Count > 1) ToBox.SelectedIndex = 1;
		FromBox.SelectionChanged += (_, _) => PairChanged();
		ToBox.SelectionChanged += (_, _) => PairChanged();
		var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
		var arrow = new TextBlock { Text = " → ", VerticalAlignment = VerticalAlignment.Center };
		var seconds = new TextBlock { Text = "  seconds ", VerticalAlignment = VerticalAlignment.Center };
		Grid.SetColumn(arrow, 1);
		Grid.SetColumn(ToBox, 2);
		Grid.SetColumn(seconds, 3);
		Grid.SetColumn(DurationBox, 4);
		pair.Children.Add(FromBox);
		pair.Children.Add(arrow);
		pair.Children.Add(ToBox);
		pair.Children.Add(seconds);
		pair.Children.Add(DurationBox);

		TryButton.Click += (_, _) =>
		{
			if (TryButton.IsChecked == true) StartTrying();
			else
			{
				_preview.Transition = null;
				_play(false);
			}
		};
		KeepButton.Click += (_, _) => KeepPair();
		ForgetButton.Click += (_, _) => ForgetPair();
		ToolTip.SetTip(DefaultBox, "For pairs not kept below");
		DefaultBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) CommitDefault(); };
		DefaultBox.LostFocus += (_, _) => CommitDefault();
		ToolTip.SetTip(PairList, "The pairs with a fade of their own");
		PairList.SelectionChanged += (_, _) => PairPicked();
		ToolTip.SetTip(PreviewButton, "The viewport shows the tracks mixed, playing (closing the mixer goes back to the timeline)");
		PreviewButton.Click += (_, _) =>
		{
			bool on = PreviewButton.IsChecked == true;
			if (on)
			{
				TryButton.IsChecked = false;
				_preview.Transition = null;
			}
			_play(on);
		};
		var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		close.Click += (_, _) => Close();
		var bottom = new DockPanel();
		DockPanel.SetDock(close, Dock.Right);
		bottom.Children.Add(close);
		bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { PreviewButton } });

		Content = new StackPanel
		{
			Margin = new Thickness(14),
			Children =
			{
				new TextBlock { Text = $"Mixing {objectName}'s animations", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
				Heading("Tracks"),
				Note("Each track plays an animation over the ones above it in this list: it changes only what it has keys for (legs walking on track 1, arms shooting on track 2). Weight: how much of it."),
				tracks,
				Heading("Fade from one animation to another"),
				Note("Try it: the first plays once, then fades into the second. Keep the time for the pair: the game (and a scene clip's Fade as mixed) uses it."),
				pair,
				new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { Spaced(TryButton), Spaced(KeepButton), ForgetButton } },
				new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), Children = { Spaced(new TextBlock { Text = "Default fade (seconds)", VerticalAlignment = VerticalAlignment.Center }, 8), DefaultBox } },
				PairList,
				new Border { Height = 10 },
				bottom,
				Hint
			}
		};
		RefreshPairs();
		Closed += (_, _) => _play(false);
		UiSize.Fit(this);
	}

	private static string Seconds(float s) => s.ToString("0.###", CultureInfo.CurrentCulture);

	private static bool TrySeconds(string? text, out float seconds) =>
		(float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)
		 || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) && seconds >= 0 && seconds < 1000;

	// ======================= tracks =======================

	private void BuildTracks(Grid grid)
	{
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		void Header(string text, int column)
		{
			var t = new TextBlock { Text = text, Foreground = Brushes.Gray, Margin = new Thickness(column > 1 ? 6 : 0, 0, 0, 0) };
			Grid.SetColumn(t, column);
			grid.Children.Add(t);
		}
		Header("Animation", 1);
		Header("Weight", 2);
		Header("Speed", 3);
		var choices = new[] { new Choice("(none)", null) }.Concat(_animations.Select(a => new Choice(a.Name, a))).ToList();
		for (int i = 0; i < _preview.Tracks.Count && i < 3; i++)
		{
			var track = _preview.Tracks[i];
			int row = i + 1;
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			var label = new TextBlock { Text = $"{row}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
			var pick = new ComboBox { ItemsSource = choices, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
			pick.SelectedItem = choices.FirstOrDefault(c => c.Animation == track.Animation) ?? choices[0];
			if (i == 0 && track.Animation == null && _animations.Count > 0) pick.SelectedIndex = 1;   // (track 1 starts with something)
			track.Animation = (pick.SelectedItem as Choice)?.Animation;
			pick.SelectionChanged += (_, _) => track.Animation = (pick.SelectedItem as Choice)?.Animation;
			var weight = new Slider { Minimum = 0, Maximum = 1, Value = track.Weight, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
			ToolTip.SetTip(weight, "How much of this track: 0 = none, all the way = all of it");
			weight.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) track.Weight = (float)weight.Value; };
			var speed = new TextBox { Text = Seconds(track.Speed), Margin = new Thickness(6, 4, 0, 0) };
			ToolTip.SetTip(speed, "1 = as made; 0.5 = half as fast");
			void CommitSpeed()
			{
				if (TrySeconds(speed.Text, out float s) && s > 0) track.Speed = s;
				speed.Text = Seconds(track.Speed);
			}
			speed.LostFocus += (_, _) => CommitSpeed();
			speed.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) CommitSpeed(); };
			var loop = new CheckBox { Content = "Loop", IsChecked = track.Loop, Margin = new Thickness(6, 4, 0, 0) };
			loop.IsCheckedChanged += (_, _) => track.Loop = loop.IsChecked == true;
			foreach (var (element, column) in new (Control, int)[] { (label, 0), (pick, 1), (weight, 2), (speed, 3), (loop, 4) })
			{
				Grid.SetRow(element, row);
				Grid.SetColumn(element, column);
				grid.Children.Add(element);
			}
			TrackRows.Add((pick, weight, speed, loop));
		}
	}

	// ======================= fades =======================

	private (Animation From, Animation To)? Pair() =>
		FromBox.SelectedItem is AnimationItem from && ToBox.SelectedItem is AnimationItem to ? (from.Animation, to.Animation) : null;

	private void PairChanged()
	{
		if (_syncing || Pair() is not { } pair) return;
		DurationBox.Text = Seconds(_plan.Duration(pair.From.Name, pair.To.Name));
		ForgetButton.IsEnabled = _plan.Pairs.Any(p => p.From == pair.From.Name && p.To == pair.To.Name);
		if (TryButton.IsChecked == true) StartTrying();
	}

	private bool ReadDuration(out float seconds)
	{
		if (TrySeconds(DurationBox.Text, out seconds)) return true;
		Hint.Text = $"\"{DurationBox.Text}\" isn't a number of seconds.";
		return false;
	}

	internal void StartTrying()
	{
		Hint.Text = "";
		if (Pair() is not { } pair || !ReadDuration(out float seconds))
		{
			TryButton.IsChecked = false;
			return;
		}
		TryButton.IsChecked = true;
		PreviewButton.IsChecked = false;
		_preview.Transition = (pair.From, pair.To, seconds);
		_play(true);
	}

	internal void KeepPair()
	{
		if (Pair() is not { } pair || !ReadDuration(out float seconds)) return;
		_edit($"Fade {pair.From.Name} → {pair.To.Name}: {Seconds(seconds)} s", () =>
		{
			_plan.Set(pair.From.Name, pair.To.Name, seconds);
			return true;
		});
		RefreshPairs();
		if (TryButton.IsChecked == true) StartTrying();
	}

	internal void ForgetPair()
	{
		if (Pair() is not { } pair) return;
		_edit($"Fade {pair.From.Name} → {pair.To.Name}: the default", () => _plan.Remove(pair.From.Name, pair.To.Name));
		RefreshPairs();
	}

	internal void CommitDefault()
	{
		if (!TrySeconds(DefaultBox.Text, out float seconds))
		{
			DefaultBox.Text = Seconds(_plan.Default);
			return;
		}
		if (MathF.Abs(seconds - _plan.Default) < 1e-6f) return;
		_edit($"Default fade: {Seconds(seconds)} s", () => { _plan.Default = seconds; return true; });
		RefreshPairs();
	}

	// After an edit, or an undo in the main window
	internal void RefreshPairs()
	{
		_syncing = true;
		PairList.ItemsSource = _plan.Pairs.Select(p => new ListBoxItem { Content = $"{p.From} → {p.To}: {Seconds(p.Duration)} s", Tag = (p.From, p.To) }).ToList();
		if (!DefaultBox.IsFocused) DefaultBox.Text = Seconds(_plan.Default);
		_syncing = false;
		if (Pair() is { } pair)
		{
			DurationBox.Text = Seconds(_plan.Duration(pair.From.Name, pair.To.Name));
			ForgetButton.IsEnabled = _plan.Pairs.Any(p => p.From == pair.From.Name && p.To == pair.To.Name);
		}
	}

	private void PairPicked()
	{
		if (_syncing || PairList.SelectedItem is not ListBoxItem { Tag: (string from, string to) }) return;
		var items = (IReadOnlyList<AnimationItem>)FromBox.ItemsSource!;
		var a = items.FirstOrDefault(x => x.Animation.Name == from);
		var b = items.FirstOrDefault(x => x.Animation.Name == to);
		if (a == null || b == null)
		{
			Hint.Text = "One of those animations isn't there any more.";
			return;
		}
		FromBox.SelectedItem = a;
		ToBox.SelectedItem = b;
	}
}
