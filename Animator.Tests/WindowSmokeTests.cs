using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

				// The rig panel: a click on a row's eye hides that bone, a click on the row picks just it
				var state = (EditorState)typeof(MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
				var tree = (TreeView)window.FindName("BoneTree");
				var row = tree.Items.OfType<TreeViewItem>().First();
				var bone = (Bone)row.Tag;
				var header = (StackPanel)row.Header;
				window.UpdateLayout();
				Click(header.Children[0]);   // the eye
				Pump(40);
				Assert.True(bone.Hidden, $"The eye didn't hide {bone.Name} (the list shown: {tree.IsVisible}, the row laid out: {System.Windows.Media.VisualTreeHelper.GetParent(header) != null})");
				Run(window, "edit.undo");
				Assert.False(bone.Hidden);
				Pump(40);
				row = tree.Items.OfType<TreeViewItem>().First();   // (the list was made again)
				window.UpdateLayout();
				Click(((StackPanel)row.Header).Children[1]);       // the name
				Assert.Equal(new object[] { bone }, state.Picked);

				// The layers view, with everything picked
				((RadioButton)window.FindName("LayersViewChoice")).IsChecked = true;
				Pump(40);
				Run(window, "pick.all");
				Run(window, "order.backward");
				Pump(40);
				((RadioButton)window.FindName("BonesViewChoice")).IsChecked = true;
				Pump(40);

				var settings = new SettingsDialog(new KeyMap(), MouseButton.Left, MouseButton.Right, MouseButton.Middle) { Owner = window };
				settings.Show();
				Pump();
				settings.Close();

				// As if just saved, so closing doesn't stop to ask
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

	// A left-button click on an element, the way the mouse sends it: the press and the release
	// tunnel down to it, and each element on the way turns them into its left-button events
	private static void Click(UIElement element)
	{
		element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
		{
			RoutedEvent = Mouse.PreviewMouseDownEvent
		});
		element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
		{
			RoutedEvent = Mouse.PreviewMouseUpEvent
		});
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
