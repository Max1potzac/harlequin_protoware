using System.Windows;

namespace Animator.Viewer.Dialogs;

// Names the animation sequence a run of frames becomes (split off from its animation, or copied)
public partial class SequenceDialog : Window
{
	private readonly IReadOnlySet<string> _taken;

	public string SequenceName { get; private set; }

	public SequenceDialog(string animation, int first, int last, bool split, string suggested, IReadOnlySet<string> taken)
	{
		InitializeComponent();
		_taken = taken;
		SequenceName = suggested;
		int count = last - first + 1;
		Heading.Text = split
			? $"Split frames {first}–{last} of \"{animation}\" ({count} frames) into an animation sequence of their own."
			: $"Copy frames {first}–{last} of \"{animation}\" ({count} frames) into an animation sequence of their own.";
		Explanation.Text = split
			? $"They start at frame 0 there. In \"{animation}\", they're taken out and the frames after them move up. Undo puts them back."
			: $"They start at frame 0 there. \"{animation}\" stays as it is.";
		OkButton.Content = split ? "Split" : "Copy";
		NameBox.Text = suggested;
		Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		string name = NameBox.Text.Trim();
		if (name.Length == 0) ErrorText.Text = "It needs a name.";
		else if (_taken.Contains(name)) ErrorText.Text = $"There's an animation called \"{name}\" already.";
		else
		{
			SequenceName = name;
			DialogResult = true;
		}
	}
}
