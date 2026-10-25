using System.Windows;

namespace Animator.Viewer.Dialogs;

// One line of text to type: a name, a number
public partial class InputDialog : Window
{
	private readonly Func<string, string?>? _check;

	public string Answer { get; private set; }

	// check: what's wrong with an answer (null: nothing)
	public InputDialog(string title, string prompt, string initial = "", Func<string, string?>? check = null)
	{
		InitializeComponent();
		Title = title;
		Prompt.Text = prompt;
		AnswerBox.Text = initial;
		Answer = initial;
		_check = check;
		Loaded += (_, _) => { AnswerBox.Focus(); AnswerBox.SelectAll(); };
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		string answer = AnswerBox.Text.Trim();
		if (_check?.Invoke(answer) is { } problem)
		{
			Problem.Text = problem;
			Problem.Visibility = Visibility.Visible;
			return;
		}
		Answer = answer;
		DialogResult = true;
	}
}
