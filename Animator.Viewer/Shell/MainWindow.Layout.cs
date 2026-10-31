using System.Windows;

namespace Animator.Viewer;

// What the window looked like last time, kept in the settings: where it was and how big, the
// panels' widths, the timeline's height, the rig panel's last view (Bones, Layers, Colour …).
// The properties panel (on the right) is put away at scene level: there's nothing picked to show.
public partial class MainWindow
{
	private GridLength _propertiesWidth = new(300);

	private void RestoreLayout()
	{
		var s = _appSettings;
		if (s.WindowWidth is { } w && w > 400 && s.WindowHeight is { } h && h > 300)
		{
			// (only where it's still on a screen: a monitor may have gone)
			var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
				SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
			if (s.WindowLeft is { } left && s.WindowTop is { } top && screen.IntersectsWith(new Rect(left + 40, top + 10, 100, 30)))
			{
				WindowStartupLocation = WindowStartupLocation.Manual;
				Left = left;
				Top = top;
			}
			Width = Math.Min(w, screen.Width);
			Height = Math.Min(h, screen.Height);
		}
		if (s.WindowMaximized) WindowState = WindowState.Maximized;
		if (s.LeftPanelWidth is { } lw && lw is >= 200 and <= 600) LeftColumn.Width = new GridLength(lw);
		if (s.RightPanelWidth is { } rw && rw is >= 220 and <= 700) RightColumn.Width = _propertiesWidth = new GridLength(rw);
		if (s.TimelineHeight is { } th && th is >= 80 and <= 900) TimelineRow.Height = _timelineHeight = new GridLength(th);
		switch (s.RigTab)
		{
			case "layers": _layersView = true; break;
			case "colour": _colourView = true; break;
			case "poses": _posesView = true; break;
			case "skins": _skinsView = true; break;
			case "boxes": _boxesView = true; break;
		}
	}

	private void RememberLayout()
	{
		var s = _appSettings;
		var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
		if (!bounds.IsEmpty && bounds.Width > 0)
		{
			s.WindowLeft = bounds.Left;
			s.WindowTop = bounds.Top;
			s.WindowWidth = bounds.Width;
			s.WindowHeight = bounds.Height;
		}
		s.WindowMaximized = WindowState == WindowState.Maximized;
		s.LeftPanelWidth = LeftColumn.ActualWidth;
		s.RightPanelWidth = RightColumn.ActualWidth > 0 ? RightColumn.ActualWidth : _propertiesWidth.Value;
		s.TimelineHeight = TimelineRow.ActualHeight > 0 ? TimelineRow.ActualHeight : _timelineHeight.Value;
		s.RigTab = RigTab();
		s.Save();
	}

	private string RigTab() => _colourView ? "colour" : _posesView ? "poses" : _skinsView ? "skins" : _boxesView ? "boxes"
		: _layersView ? "layers" : "bones";

	// The properties panel and the timeline's splitter follow the workspace (SyncWorkspace)
	private void SyncPanels(bool scene, bool timeline)
	{
		if (RightColumn.Width.Value > 0) _propertiesWidth = RightColumn.Width;
		RightColumn.MinWidth = scene ? 0 : 220;
		RightColumn.Width = scene ? new GridLength(0) : _propertiesWidth;
		var properties = scene ? Visibility.Collapsed : Visibility.Visible;
		PropertiesPanel.Visibility = properties;
		PropertiesSplitter.Visibility = properties;
		TimelineSplitter.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
	}
}
