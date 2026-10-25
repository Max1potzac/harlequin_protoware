using System.Windows;
using System.Windows.Threading;

namespace Animator.Viewer
{
	public partial class App : Application
	{
		// The main window, and once it's up, any unsaved work a run that didn't close properly left
		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);
			var window = new MainWindow();
			MainWindow = window;
			window.Show();
			window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(window.OfferRecovery));
		}
	}
}
