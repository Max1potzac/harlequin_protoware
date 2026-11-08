using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The shell: Open recent and Reopen, undo going back to where an edit was made first (Places),
// the history window, the help window (F1) and the status line
public class ShellTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static void ShotOf(Window window, string name)
	{
		Dispatcher.UIThread.RunJobs();
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		Directory.CreateDirectory(Windows.ShotsFolder);
		frame!.Save(Path.Combine(Windows.ShotsFolder, name + ".png"));
	}

	[AvaloniaFact]
	public void Saved_projects_are_recent_and_reopen_goes_back_to_the_save()
	{
		var window = Windows.Show();
		var root = window.State.Skeleton.Roots[0];
		string folder = Path.Combine(Path.GetTempPath(), "hq-shell-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, "hero" + ProjectFile.Extension);

		Assert.True(window.TrySave(path, out var problem), problem);
		Assert.Equal(Path.GetFullPath(path), window.Recent[0]);
		window.FillRecentMenu();

		// Changed, then back to the save
		window.Session.RenameBone(root, "spine");
		window.AnswerReopen = () => Task.FromResult(true);
		Finish(window.Reopen());
		Assert.Equal("root", window.State.Skeleton.Roots[0].Name);

		// A recent project that's gone is taken off the list
		string gone = Path.Combine(folder, "gone" + ProjectFile.Extension);
		window.Recent.Insert(0, gone);
		Finish(window.OpenRecent(gone));
		Assert.DoesNotContain(window.Recent, p => p.EndsWith("gone" + ProjectFile.Extension));
		Assert.Contains(window.Recent, p => p.EndsWith("hero" + ProjectFile.Extension));
	}

	[AvaloniaFact]
	public void Undo_goes_back_to_where_an_edit_was_made_first()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		window.Session.RenameBone(root, "spine");
		state.AnimateMode = true;
		window.Frame(0);
		Assert.Contains("goes back there first", window.UndoHint());
		Assert.Contains("Last edit:", window.StatusText);

		state.Undo.Undo();   // back to Build, the name kept
		Assert.False(state.AnimateMode);
		Assert.Equal("spine", root.Name);
		state.Undo.Undo();   // and now undone
		Assert.Equal("root", root.Name);
	}

	[AvaloniaFact]
	public void The_history_goes_back_to_any_step_and_help_follows_along()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		window.Session.RenameBone(root, "spine");
		window.Session.RenameBone(root, "back");

		window.ShowHistory();
		var history = window.History!;
		Assert.Equal(3, history.Steps.ItemCount);
		Assert.Equal(2, history.Steps.SelectedIndex);
		history.Steps.SelectedIndex = 0;   // the start
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("root", root.Name);
		Assert.Equal(3, history.Steps.ItemCount);   // (the two can be redone)
		history.Steps.SelectedIndex = 1;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("spine", root.Name);
		ShotOf(history, "shell-history");

		// F1: the help for inside an object, then the scene's
		Assert.True(window.HandleKey(Avalonia.Input.Key.F1, Avalonia.Input.KeyModifiers.None));
		var help = window.HelpShown!;
		window.Frame(0);
		Assert.True(help.Lines.Children.Count > 10);
		Assert.Contains(help.Lines.Children.OfType<TextBlock>(), t => (t.Inlines?.Count > 0 ? string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "").StartsWith("Rig tab:"));
		ShotOf(help, "shell-help");
		window.LeaveObject();
		window.Frame(0);
		Assert.Contains("Scene timeline", window.HelpText());
		help.Close();
		history.Close();
		Assert.Null(window.HelpShown);
		Assert.Null(window.History);
	}
}
