using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Animator.Viewer;
using Animator.Viewer.Dialogs;

namespace Animator.Tests;

// The windows themselves: they open, run a few frames and commands, and close, without an error.
// A XAML mistake, or a handler that runs before the window is ready, shows up here first.
public class WindowSmokeTests
{
	[Fact]
	public void The_main_window_and_the_settings_open_work_and_close()
	{
		Exception? failure = null;
		var thread = new Thread(() =>
		{
			try
			{
				if (Application.Current == null) new App().InitializeComponent();   // its resources (the checkerboard)

				var window = new MainWindow();
				window.Show();
				Pump();

				// Picking, hiding and the order, in Setup and Animate mode, and undoing them
				foreach (var command in new[]
				{
					"pick.all", "pick.keys", "pick.hide", "pick.showAll", "edit.undo", "edit.undo",
					"pick.all", "pick.hideOthers", "pick.showAll", "anim.mode", "pick.all", "order.forward",
					"edit.undo", "anim.mode", "view.onion", "view.onion"
				})
				{
					Run(window, command);
					Pump(40);
				}

				var settings = new SettingsDialog(new KeyMap(), MouseButton.Left, MouseButton.Right, MouseButton.Middle) { Owner = window };
				settings.Show();
				Pump();
				settings.Close();

				// As if just saved, so closing doesn't stop to ask
				var state = (EditorState)typeof(MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
				state.Undo.MarkSaved();
				window.Close();
				Pump(40);
			}
			catch (Exception e)
			{
				failure = e;
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();

		Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The windows stopped responding");
		if (failure != null) Assert.Fail(failure.ToString());
	}

	// What a key or a menu item would do (see MainWindow.Commands.cs)
	private static void Run(MainWindow window, string command)
	{
		var run = typeof(MainWindow).GetMethod("RunCommand", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("MainWindow.RunCommand is gone");
		run.Invoke(window, new object[] { command });
	}

	// Lets the window get on with things for a while: layout, rendering, a few frames
	private static void Pump(int milliseconds = 300)
	{
		var frame = new DispatcherFrame();
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
			(_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
		timer.Start();
		Dispatcher.PushFrame(frame);
		timer.Stop();
	}
}
