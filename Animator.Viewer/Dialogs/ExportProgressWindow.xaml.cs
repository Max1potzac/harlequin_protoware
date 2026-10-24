using System.ComponentModel;
using System.Windows;

namespace Animator.Viewer.Dialogs;

// Shown while an export runs: how far it is, and a Cancel button. Closing it cancels too.
public partial class ExportProgressWindow : Window
{
	private bool _finished;

	public bool Cancelled { get; private set; }

	public ExportProgressWindow() => InitializeComponent();

	public void Report(ExportProgress progress)
	{
		Bar.Maximum = Math.Max(1, progress.Total);
		Bar.Value = Math.Min(progress.Done, progress.Total);
		StepText.Text = progress.Step;
	}

	// Closes it for good (the export is over)
	public void Finish()
	{
		_finished = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		Cancelled = true;
		CancelButton.IsEnabled = false;
		StepText.Text = "Stopping…";
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		if (_finished) return;
		e.Cancel = true;   // the export closes it once it has stopped
		Cancel_Click(this, new RoutedEventArgs());
	}
}
