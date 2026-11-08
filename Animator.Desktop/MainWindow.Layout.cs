using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace Animator.Desktop;

// What the window looked like last time, kept in the settings (the same ones the Windows editor
// keeps): where it was and how big, the panels' widths, the timeline's height, the Rig tab's last
// view (Bones, Layers, Colour, Boxes) or the Skins and Poses tabs. And the View menu's check marks
// and grid sizes.
public sealed partial class MainWindow
{
	private ColumnDefinitions _columns = null!;   // the rig panel, the work, the properties
	private RowDefinitions _rows = null!;         // the viewport, the animation bar, the timeline

	// (the tests leave the settings as they were)
	internal static bool RememberLayoutOnClose { get; set; } = true;

	internal MenuItem MenuItemFor(string id) => _menuItems[id];
	internal double LeftPanelWidth => _columns[0].Width.Value;
	internal double RightPanelWidth => _columns[4].Width.Value;
	internal double TimelineHeight => _rows[3].Height.Value;

	internal void RestoreLayout()
	{
		var s = _settings;
		if (s.WindowWidth is { } w && w > 400 && s.WindowHeight is { } h && h > 300)
		{
			Width = w;
			Height = h;
			// (only where it's still on a screen: a monitor may have gone)
			if (s.WindowLeft is { } left && s.WindowTop is { } top && Screens?.All is { Count: > 0 } screens)
			{
				var at = new PixelPoint((int)left, (int)top);
				if (screens.Any(x => x.Bounds.Contains(at + new PixelPoint(40, 10))))
				{
					WindowStartupLocation = WindowStartupLocation.Manual;
					Position = at;
				}
			}
		}
		if (s.WindowMaximized) WindowState = WindowState.Maximized;
		if (s.LeftPanelWidth is { } lw && lw is >= 200 and <= 600) _columns[0].Width = new GridLength(lw);
		if (s.RightPanelWidth is { } rw && rw is >= 220 and <= 700) _columns[4].Width = new GridLength(rw);
		if (s.TimelineHeight is { } th && th is >= 80 and <= 900) _rows[3].Height = new GridLength(th);
		switch (s.RigTab)
		{
			case "layers": _layersView = true; break;
			case "colour": _colourViewOn = true; break;
			case "boxes": _boxesViewOn = true; break;
			case "skins": LeftTabs.SelectedIndex = 1; break;
			case "poses": LeftTabs.SelectedIndex = 2; break;
		}
	}

	internal void RememberLayout()
	{
		var s = _settings;
		if (WindowState == WindowState.Normal && Bounds.Width > 0)
		{
			s.WindowLeft = Position.X;
			s.WindowTop = Position.Y;
			s.WindowWidth = Bounds.Width;
			s.WindowHeight = Bounds.Height;
		}
		s.WindowMaximized = WindowState == WindowState.Maximized;
		s.LeftPanelWidth = _columns[0].ActualWidth > 0 ? _columns[0].ActualWidth : _columns[0].Width.Value;
		s.RightPanelWidth = _columns[4].ActualWidth > 0 ? _columns[4].ActualWidth : _columns[4].Width.Value;
		s.TimelineHeight = _rows[3].ActualHeight > 0 ? _rows[3].ActualHeight : _rows[3].Height.Value;
		s.RigTab = LeftTabs.SelectedIndex switch
		{
			1 => "skins",
			2 => "poses",
			_ => _colourViewOn ? "colour" : _boxesViewOn ? "boxes" : _layersView ? "layers" : "bones"
		};
		SaveSettingsQuietly();
	}

	// ======================= the View menu =======================

	private static readonly float[] GridSizes = { 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 50, 64, 100 };
	private readonly MenuItem _gridSizeMenu = new() { Header = "Grid si_ze" };

	// The toggles get check marks (set as the menu opens), and the grid's sizes go under them
	private void AddViewChecks(MenuItem view)
	{
		foreach (string id in ViewChecks.Keys)
			if (_menuItems.TryGetValue(id, out var item)) item.ToggleType = MenuItemToggleType.CheckBox;
		view.Items.Insert(view.Items.IndexOf(_menuItems["view.guides"]) + 1, _gridSizeMenu);
		_gridSizeMenu.Items.Add(new MenuItem { Header = "1" });   // (so it opens as a submenu)
		view.SubmenuOpened += (_, _) => SyncViewMenu();
	}

	private Dictionary<string, Func<bool>> ViewChecks => new()
	{
		["tool.select"] = () => _tool.Tool == ViewTool.Select,
		["tool.move"] = () => _tool.Tool == ViewTool.Move,
		["tool.rotate"] = () => _tool.Tool == ViewTool.Rotate,
		["tool.paint"] = () => _tool.Tool == ViewTool.Paint,
		["view.images"] = () => _renderer.ShowImages,
		["view.bones"] = () => _settings.ShowBones,
		["view.pixels"] = () => _renderer.PixelArt,
		["view.onion"] = () => _painter.Onion.Enabled,
		["view.hitboxes"] = () => _showHitboxes,
		["view.grid"] = () => _settings.ShowGrid,
		["view.rulers"] = () => _settings.ShowRulers,
		["view.guides"] = () => _settings.ShowGuides,
		["view.snapGrid"] = () => _settings.SnapToGrid,
		["view.snapGuides"] = () => _settings.SnapToGuides,
		["view.lockGuides"] = () => _settings.LockGuides
	};

	internal void SyncViewMenu()
	{
		foreach (var (id, on) in ViewChecks)
			if (_menuItems.TryGetValue(id, out var item)) item.IsChecked = on();
		_menuItems["view.clearGuides"].IsEnabled = Scene.Guides.Count > 0;
		_gridSizeMenu.Items.Clear();
		foreach (float size in GridSizes)
		{
			var item = new MenuItem
			{
				Header = size == 1 ? "1 (a pixel)" : size.ToString(CultureInfo.CurrentCulture),
				ToggleType = MenuItemToggleType.Radio,
				IsChecked = MathF.Abs(size - Scene.GridSize) < 1e-6f
			};
			float chosen = size;
			item.Click += (_, _) => SetGridSize(chosen);
			_gridSizeMenu.Items.Add(item);
		}
	}

	internal void SetGridSize(float size)
	{
		if (!_session.SetGridSize(size, $"Grid size {size.ToString("0.##", CultureInfo.CurrentCulture)}")) return;
		if (!_settings.ShowGrid) ToggleSetting(s => s.ShowGrid = true);   // (so you see what changed)
	}
}
