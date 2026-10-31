using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Animator.Viewer.Dialogs;

// The mixer (an object's animations together): tracks played over each other, a fade from one
// animation to another tried out, and the fade times kept for the game. It stays open beside the
// main window; while it plays, the viewport shows the mix instead of the timeline.
public partial class MixerWindow : Window
{
	private sealed record Choice(string Name, Animation? Animation);

	private readonly IReadOnlyList<Animation> _animations;
	private readonly MixPlan _plan;
	private readonly MixPreview _preview;
	private readonly Func<string, Func<bool>, bool> _edit;   // an undoable change to the plan
	private readonly Action<bool> _play;                     // the viewport showing the preview (or not)
	private bool _syncing;

	public MixerWindow(string objectName, IReadOnlyList<Animation> animations, MixPlan plan, MixPreview preview,
		Func<string, Func<bool>, bool> edit, Action<bool> play)
	{
		InitializeComponent();
		_animations = animations;
		_plan = plan;
		_preview = preview;
		_edit = edit;
		_play = play;
		Heading.Text = $"Mixing {objectName}'s animations";
		BuildTracks();
		FromBox.ItemsSource = animations;
		ToBox.ItemsSource = animations;
		if (animations.Count > 0) FromBox.SelectedIndex = 0;
		if (animations.Count > 1) ToBox.SelectedIndex = 1;
		DefaultBox.Text = Seconds(plan.Default);
		RefreshPairs();
		Closed += (_, _) => _play(false);
	}

	private static string Seconds(float s) => s.ToString("0.###", CultureInfo.CurrentCulture);

	private static bool TrySeconds(string text, out float seconds) =>
		(float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)
		 || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) && seconds >= 0 && seconds < 1000;

	// ======================= tracks =======================

	private void BuildTracks()
	{
		var choices = new[] { new Choice("(none)", null) }.Concat(_animations.Select(a => new Choice(a.Name, a))).ToList();
		for (int i = 0; i < _preview.Tracks.Count && i < 3; i++)
		{
			var track = _preview.Tracks[i];
			int row = i + 1;
			var label = new TextBlock { Text = $"{row}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
			var pick = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", Margin = new Thickness(0, 4, 0, 0) };
			pick.SelectedItem = choices.FirstOrDefault(c => c.Animation == track.Animation) ?? choices[0];
			if (i == 0 && track.Animation == null && _animations.Count > 0) pick.SelectedIndex = 1;   // (track 1 starts with something)
			track.Animation = (pick.SelectedItem as Choice)?.Animation;
			pick.SelectionChanged += (_, _) => track.Animation = (pick.SelectedItem as Choice)?.Animation;
			var weight = new Slider { Minimum = 0, Maximum = 1, Value = track.Weight, Margin = new Thickness(6, 6, 0, 0), ToolTip = "How much of this track: 0 = none, all the way = all of it" };
			weight.ValueChanged += (_, e) => track.Weight = (float)e.NewValue;
			var speed = new TextBox { Text = Seconds(track.Speed), Margin = new Thickness(6, 4, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "1 = as made; 0.5 = half as fast" };
			speed.LostFocus += (_, _) =>
			{
				if (TrySeconds(speed.Text, out float s) && s > 0) track.Speed = s;
				speed.Text = Seconds(track.Speed);
			};
			var loop = new CheckBox { Content = "Loop", IsChecked = track.Loop, Margin = new Thickness(6, 6, 0, 0) };
			loop.Click += (_, _) => track.Loop = loop.IsChecked == true;
			foreach (var (element, column) in new (UIElement, int)[] { (label, 0), (pick, 1), (weight, 2), (speed, 3), (loop, 4) })
			{
				Grid.SetRow(element, row);
				Grid.SetColumn(element, column);
				TrackGrid.Children.Add(element);
			}
		}
	}

	private void Preview_Click(object sender, RoutedEventArgs e)
	{
		bool on = PreviewButton.IsChecked == true;
		if (on)
		{
			TryButton.IsChecked = false;
			_preview.Transition = null;
		}
		_play(on);
	}

	// ======================= fades =======================

	private (Animation From, Animation To)? Pair() =>
		FromBox.SelectedItem is Animation from && ToBox.SelectedItem is Animation to ? (from, to) : null;

	private void Pair_Changed(object sender, SelectionChangedEventArgs e)
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

	private void Try_Click(object sender, RoutedEventArgs e)
	{
		if (TryButton.IsChecked == true) StartTrying();
		else
		{
			_preview.Transition = null;
			_play(false);
		}
	}

	private void StartTrying()
	{
		Hint.Text = "";
		if (Pair() is not { } pair || !ReadDuration(out float seconds))
		{
			TryButton.IsChecked = false;
			return;
		}
		PreviewButton.IsChecked = false;
		_preview.Transition = (pair.From, pair.To, seconds);
		_play(true);
	}

	private void KeepPair_Click(object sender, RoutedEventArgs e)
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

	private void ForgetPair_Click(object sender, RoutedEventArgs e)
	{
		if (Pair() is not { } pair) return;
		_edit($"Fade {pair.From.Name} → {pair.To.Name}: the default", () => _plan.Remove(pair.From.Name, pair.To.Name));
		RefreshPairs();
	}

	private void Default_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) CommitDefault();
	}

	private void Default_LostFocus(object sender, RoutedEventArgs e) => CommitDefault();

	private void CommitDefault()
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

	private void RefreshPairs()
	{
		_syncing = true;
		PairList.Items.Clear();
		foreach (var (from, to, d) in _plan.Pairs)
			PairList.Items.Add(new ListBoxItem { Content = $"{from} → {to}: {Seconds(d)} s", Tag = (from, to) });
		DefaultBox.Text = Seconds(_plan.Default);
		_syncing = false;
		if (Pair() is { } pair)
		{
			DurationBox.Text = Seconds(_plan.Duration(pair.From.Name, pair.To.Name));
			ForgetButton.IsEnabled = _plan.Pairs.Any(p => p.From == pair.From.Name && p.To == pair.To.Name);
		}
	}

	private void PairList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing || PairList.SelectedItem is not ListBoxItem { Tag: (string from, string to) }) return;
		var a = _animations.FirstOrDefault(x => x.Name == from);
		var b = _animations.FirstOrDefault(x => x.Name == to);
		if (a == null || b == null)
		{
			Hint.Text = "One of those animations isn't there any more.";
			return;
		}
		FromBox.SelectedItem = a;
		ToBox.SelectedItem = b;
	}

	private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
