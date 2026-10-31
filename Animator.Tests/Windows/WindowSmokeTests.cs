using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Animator.Viewer;
using Animator.Viewer.Dialogs;
using Animator.Viewer.Files;

namespace Animator.Tests;

// The windows themselves: they open, run a few frames and commands, and close, without an error.
// A XAML mistake, or a handler that runs before the window is ready, shows up here first.
public class WindowSmokeTests
{
	[Fact]
	public void The_main_window_and_the_settings_open_work_and_close()
	{
		Exception? failure = null;
		// Settings of its own: what the test turns on and off mustn't reach anyone's real ones
		string settingsFolder = Path.Combine(Path.GetTempPath(), $"harlequin-smoke-{Guid.NewGuid():N}");
		AppSettings.FilePath = Path.Combine(settingsFolder, "settings.json");
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
				var children = ((StackPanel)row.Header).Children;
				Click(children[children.Count - 1]);               // the name
				Assert.Equal(new object[] { bone }, state.Picked);

				// The padlock beside the eye locks it; L locks the picked and lets them go; Alt+L unlocks all
				Pump(40);
				row = tree.Items.OfType<TreeViewItem>().First();
				window.UpdateLayout();
				Click(((StackPanel)row.Header).Children[1]);       // the lock
				Pump(40);
				Assert.True(bone.Locked, $"The padlock didn't lock {bone.Name}");
				Run(window, "edit.undo");
				Assert.False(bone.Locked);
				Run(window, "pick.all");
				Run(window, "pick.lock");
				Assert.True(bone.Locked);
				Assert.Empty(state.Picked);
				Run(window, "pick.all");                           // (nothing: everything's locked)
				Assert.Empty(state.Picked);
				Run(window, "pick.unlockAll");
				Assert.False(bone.Locked);
				Pump(40);

				// A selection set: its chip under the list picks what's in it
				var skeleton = (Skeleton)Field(window, "_skeleton");
				var set = new SelectionSet { Name = "test set" };
				set.Bones.Add(bone);
				skeleton.Sets.Add(set);
				Call(window, "RefreshRigPanel");
				Pump(40);
				var chip = ((Panel)window.FindName("SetChips")).Children.OfType<Button>().First(b => ReferenceEquals(b.Tag, set));
				state.ClearPicks();
				chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
				Assert.Equal(new object[] { bone }, state.Picked);
				skeleton.Sets.Remove(set);
				Call(window, "RefreshRigPanel");

				// Rulers, the grid and guides: a guide dragged out of the top ruler, then back onto the left one
				var scene = (Scene)Field(window, "_scene");
				var viewport = (FrameworkElement)window.FindName("Viewport");
				float w = (float)viewport.ActualWidth, h = (float)viewport.ActualHeight;
				Call(window, "GuideMouseDown", new Vector2(w / 2, 5), MouseButton.Left);
				Call(window, "GuideMouseMove", new Vector2(w / 2, h / 2));
				Call(window, "GuideMouseUp", new Vector2(w / 2, h / 2));
				Assert.Single(scene.Guides);
				Assert.False(scene.Guides[0].Vertical);   // across
				Assert.Equal("Add a guide", state.Undo.NextUndoName);
				Pump(40);
				Call(window, "GuideMouseDown", new Vector2(5, h / 2), MouseButton.Left);   // its mark on the left ruler
				Call(window, "GuideMouseUp", new Vector2(5, h / 2));
				Assert.Empty(scene.Guides);
				Run(window, "edit.undo");
				Assert.Single(scene.Guides);
				foreach (var command in new[] { "view.grid", "view.snapGrid", "view.rulers", "view.guides" })
				{
					Run(window, command);
					Pump(40);
					Run(window, command);
				}
				Call(window, "SetGridSize", 16f);
				Assert.Equal(16f, scene.GridSize);
				Pump(40);
				Run(window, "edit.undo");
				Assert.Equal(10f, scene.GridSize);
				Run(window, "edit.undo");
				Assert.Empty(scene.Guides);

				// The Colour view: a colour layer on the whole object, its settings in both modes, undone
				((RadioButton)window.FindName("ColourViewChoice")).IsChecked = true;
				Pump(40);
				Call(window, "AddObjectColour", ColorAdjustKind.HueSaturation);
				Pump(40);
				Assert.Single(skeleton.ColorLayers);
				Assert.NotEmpty(((Panel)window.FindName("ColourSettings")).Children);
				foreach (var command in new[] { "anim.mode", "anim.autoKey", "anim.autoKey", "anim.mode" })
				{
					Run(window, command);
					Pump(40);
				}
				Run(window, "edit.undo");
				Pump(40);
				Assert.Empty(skeleton.ColorLayers);

				// The layers view, with everything picked
				((RadioButton)window.FindName("LayersViewChoice")).IsChecked = true;
				Pump(40);
				Run(window, "pick.all");
				Run(window, "order.backward");
				Pump(40);
				((RadioButton)window.FindName("BonesViewChoice")).IsChecked = true;
				Pump(40);

				var settings = new SettingsDialog(new KeyMap(), MouseButton.Left, MouseButton.Right, MouseButton.Middle, 5, 3) { Owner = window };
				settings.Show();
				Pump();
				((TabItem)settings.FindName("SavingTab")).IsSelected = true;
				Pump(100);
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
		try { Directory.Delete(settingsFolder, recursive: true); }
		catch (Exception) { }   // (nothing was saved, or it's still in use: it's in the temp folder anyway)
		if (failure != null) Assert.Fail(failure.ToString());
	}

	private static object Field(MainWindow window, string name) =>
		(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"MainWindow.{name} is gone")).GetValue(window)!;

	private static void Call(MainWindow window, string method, params object[] args)
	{
		var info = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"MainWindow.{method} is gone");
		info.Invoke(window, args);
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
