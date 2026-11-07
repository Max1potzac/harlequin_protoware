using System.Globalization;
using Animator.Viewer.Export;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// File > Export: what (the scene, or one object's animations), the format, and its options.
// Only the options the chosen format uses are shown. The same as the Windows editor's dialog;
// ShowDialog<bool>: true, export with Settings.
public sealed class ExportWindow : Window
{
	internal sealed record FormatChoice(ExportFormat Format, string Label, string Hint)
	{
		public override string ToString() => Label;
	}

	private sealed record AreaChoice(bool Stage, string Label)
	{
		public override string ToString() => Label;
	}

	private sealed record ObjectChoice(SceneObject Object)
	{
		public override string ToString() => Object.Name;
	}

	internal static readonly FormatChoice[] Formats =
	{
		new(ExportFormat.PngSequence, "PNG pictures (one per frame)",
			"name_0000.png, name_0001.png … in the folder you pick. Keeps see-through parts; video editors and game engines take these."),
		new(ExportFormat.Gif, "Animated GIF",
			"Plays almost anywhere (chats, web pages). GIFs have at most 256 colours and no half see-through pixels."),
		new(ExportFormat.SpriteSheet, "Sprite sheet (PNG + JSON)",
			"Every frame on one picture, in a grid, plus a JSON file listing the frames and animations " +
			"(the Aseprite / TexturePacker layout that game engines and tools read). Harlequin can import it again."),
		new(ExportFormat.Video, "Video (MP4)",
			"An H.264 video, made with FFmpeg (a free program). Videos can't be see-through: the background colour fills the empty space."),
		new(ExportFormat.Engine, "Game engine (rig + atlas)",
			"The object itself for a game to play: name.hqrig.json (bones, parts, skins, meshes and weights, constraints, masks, " +
			"hitboxes, colour layers, every animation, events, fades) and its pictures packed in atlas pages (name.atlas.png). " +
			"The Harlequin plugin for an engine reads it; its format is described in docs/engine-format.md.")
	};

	private static readonly int[] AtlasSizes = { 512, 1024, 2048, 4096, 8192 };
	private const double LabelWidth = 90;

	private readonly Scene _scene;
	private readonly int _fps;
	private SKColor _background = SKColors.White;
	private bool _ready;

	public ExportSettings Settings { get; private set; } = new();
	public string? FfmpegPath { get; private set; }

	internal readonly RadioButton SceneChoice = new() { Content = "The scene, over its timeline (a video, a GIF, pictures)", GroupName = "what" };
	internal readonly RadioButton ObjectChoiceButton = new() { Content = "One object's animations (for a game)", GroupName = "what" };
	internal readonly ComboBox ObjectBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly StackPanel AnimationList = new() { Spacing = 2, Margin = new Thickness(6, 4) };
	private readonly Grid _objectFields = new() { Margin = new Thickness(20, 6, 0, 0) };
	internal readonly ComboBox FormatBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBlock FormatHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };

	private readonly ComboBox _areaBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox FromBox = new() { Width = 56 };
	internal readonly TextBox ToBox = new() { Width = 56 };
	internal readonly TextBox StepBox = new() { Width = 44 };
	private readonly StackPanel _rangeFields = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
	internal readonly TextBox ScaleBox = new() { Width = 56 };
	internal readonly CheckBox PixelBox = new() { Content = "Pixel art (crisp, no smoothing)" };
	internal readonly CheckBox BakeColorBox = new() { Content = "Bake colour correction layers" };
	internal readonly CheckBox EventsBox = new() { Content = "Events file", IsChecked = true };
	internal readonly CheckBox TransparentBox = new() { Content = "See-through" };
	private readonly Border _swatch = new() { Width = 40, Height = 18, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
	private readonly Button _swatchButton = new() { Padding = new Thickness(3) };
	internal readonly CheckBox RowBox = new() { Content = "A row per animation" };
	internal readonly TextBox ColumnsBox = new() { Width = 44 };
	internal readonly TextBox PaddingBox = new() { Width = 44 };
	internal readonly CheckBox LoopBox = new() { Content = "Loop" };
	internal readonly CheckBox DitherBox = new() { Content = "Dither" };
	internal readonly TextBox FfmpegBox = new() { IsReadOnly = true };
	internal readonly ComboBox AtlasSizeBox = new() { Width = 90 };
	internal readonly TextBox AtlasGapBox = new() { Width = 44 };
	internal readonly CheckBox PowerOfTwoBox = new() { Content = "Sizes a power of two" };
	internal readonly CheckBox TrimBox = new() { Content = "Trim", IsChecked = true };
	internal readonly TextBlock ErrorText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };

	// The option rows, shown or hidden with the format
	private Control _areaRow = null!, _sheetRow = null!, _gifRow = null!, _videoRow = null!, _engineRow = null!;

	// last: the previous export's choices (null the first time). current: the object being edited.
	public ExportWindow(Scene scene, SceneObject? current, int fps, ExportSettings? last, string? ffmpegPath, bool pixelArt)
	{
		_scene = scene;
		_fps = Math.Max(1, fps);
		FfmpegPath = ffmpegPath;
		FfmpegBox.Text = ffmpegPath ?? "";
		Title = "Export";
		Width = 520;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		var objects = scene.Objects.Where(o => o.Animations.Count > 0).ToList();
		var objectChoices = objects.Select(o => new ObjectChoice(o)).ToList();
		ObjectBox.ItemsSource = objectChoices;
		ObjectChoiceButton.IsEnabled = objects.Count > 0;
		var pick = last?.Object is { } previous && objects.Contains(previous) ? previous
			: current != null && objects.Contains(current) ? current
			: objects.FirstOrDefault();
		ObjectBox.SelectedItem = objectChoices.FirstOrDefault(c => c.Object == pick);

		FormatBox.ItemsSource = Formats;
		var areas = new List<AreaChoice>();
		if (scene.HasStage) areas.Add(new AreaChoice(true, $"The stage ({scene.StageWidth} × {scene.StageHeight})"));
		areas.Add(new AreaChoice(false, "Everything that shows (fitted around it)"));
		_areaBox.ItemsSource = areas;

		// The last export's choices, or what suits the kind of project
		var s = last ?? new ExportSettings
		{
			Scene = scene.Kind == ProjectKind.Animation || objects.Count == 0,
			Format = scene.Kind == ProjectKind.Animation ? ExportFormat.Gif : ExportFormat.SpriteSheet,
			PixelArt = pixelArt
		};
		(s.Scene || objects.Count == 0 ? SceneChoice : ObjectChoiceButton).IsChecked = true;
		FormatBox.SelectedItem = Formats.First(f => f.Format == s.Format);
		_areaBox.SelectedItem = areas.FirstOrDefault(a => a.Stage == s.UseStage) ?? areas[0];

		int lastFrame = LastSceneFrame;
		FromBox.Text = Math.Clamp(s.FirstFrame, 0, lastFrame).ToString();
		ToBox.Text = (s.LastFrame >= 0 ? Math.Min(s.LastFrame, lastFrame) : lastFrame).ToString();
		StepBox.Text = s.FrameStep.ToString();
		ScaleBox.Text = s.Scale.ToString("0.###", CultureInfo.CurrentCulture);
		PixelBox.IsChecked = s.PixelArt;
		BakeColorBox.IsChecked = s.BakeColorLayers;
		AtlasSizeBox.ItemsSource = AtlasSizes;
		AtlasSizeBox.SelectedItem = AtlasSizes.Contains(s.AtlasSize) ? s.AtlasSize : 2048;
		AtlasGapBox.Text = s.AtlasGap.ToString();
		PowerOfTwoBox.IsChecked = s.PowerOfTwo;
		TrimBox.IsChecked = s.Trim;
		EventsBox.IsChecked = s.WriteEvents;
		TransparentBox.IsChecked = s.Background == null;
		_background = s.Background ?? SKColors.White;
		RowBox.IsChecked = s.RowPerAnimation;
		ColumnsBox.Text = s.Columns.ToString();
		PaddingBox.Text = s.Padding.ToString();
		LoopBox.IsChecked = s.Loop;
		DitherBox.IsChecked = s.Dither;

		Content = BuildLayout();
		ShowAnimations(last?.Animations);

		SceneChoice.IsCheckedChanged += (_, _) => Sync();
		ObjectChoiceButton.IsCheckedChanged += (_, _) => Sync();
		FormatBox.SelectionChanged += (_, _) => Sync();
		TransparentBox.IsCheckedChanged += (_, _) => Sync();
		ObjectBox.SelectionChanged += (_, _) => { if (_ready) ShowAnimations(null); };
		_swatchButton.Click += async (_, _) =>
		{
			if (await ColourDialog.Ask(this, "Background colour", _background) is not { } picked) return;
			_background = picked.WithAlpha(255);   // a background is solid
			Sync();
		};
		KeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
		};

		_ready = true;
		Sync();
	}

	private Control BuildLayout()
	{
		static TextBlock Heading(string text, double top) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, top, 0, 4) };
		static TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		static StackPanel Across(params Control[] controls)
		{
			var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
			foreach (var c in controls) row.Children.Add(c);
			return row;
		}
		// A labelled row of options
		static Control Row(string label, Control content)
		{
			var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
			var name = Words(label);
			name.Width = LabelWidth;
			DockPanel.SetDock(name, Dock.Left);
			row.Children.Add(name);
			row.Children.Add(content);
			return row;
		}

		var panel = new StackPanel { Margin = new Thickness(14) };

		// What
		panel.Children.Add(Heading("What", 0));
		panel.Children.Add(SceneChoice);
		ObjectChoiceButton.Margin = new Thickness(0, 4, 0, 0);
		panel.Children.Add(ObjectChoiceButton);
		_objectFields.ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*");
		_objectFields.RowDefinitions = new RowDefinitions("Auto,Auto");
		var objectLabel = Words("Object");
		_objectFields.Children.Add(objectLabel);
		ObjectBox.Margin = new Thickness(0, 0, 0, 4);
		Grid.SetColumn(ObjectBox, 1);
		_objectFields.Children.Add(ObjectBox);
		var animationsLabel = new TextBlock { Text = "Animations", Margin = new Thickness(0, 4, 0, 0) };
		Grid.SetRow(animationsLabel, 1);
		_objectFields.Children.Add(animationsLabel);
		var list = new Border
		{
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(1),
			Height = 110,
			Child = new ScrollViewer { Content = AnimationList }
		};
		ToolTip.SetTip(list, "Each ticked animation is exported: its own files, or its own row on a sprite sheet");
		Grid.SetRow(list, 1);
		Grid.SetColumn(list, 1);
		_objectFields.Children.Add(list);
		panel.Children.Add(_objectFields);

		// Format
		panel.Children.Add(Heading("Format", 14));
		panel.Children.Add(FormatBox);
		FormatHint.Margin = new Thickness(0, 4, 0, 0);
		panel.Children.Add(FormatHint);

		// Options (only the ones the format uses show)
		var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
		ToolTip.SetTip(_areaBox, "The stage: exactly the scene's frame. Everything: just big enough for what shows in these frames.");
		_areaRow = Row("Area", _areaBox);
		options.Children.Add(_areaRow);

		_rangeFields.Children.Add(Words("from"));
		_rangeFields.Children.Add(FromBox);
		_rangeFields.Children.Add(Words("to"));
		_rangeFields.Children.Add(ToBox);
		ToolTip.SetTip(StepBox, "1 = every frame. 2 = every second frame (half the frame rate, half the files)");
		var every = Words("every");
		every.Margin = new Thickness(6, 0, 0, 0);
		options.Children.Add(Row("Frames", Across(_rangeFields, every, StepBox, Words("frame(s)"))));

		ToolTip.SetTip(ScaleBox, "1 = one pixel per unit, as in the editor. 2 = twice as big, 0.5 = half");
		ToolTip.SetTip(BakeColorBox, "On: the pictures come out with the colour layers (an object's, and those in its drawings) as the editor shows them. Off: without them.");
		ToolTip.SetTip(EventsBox, "An object's animations: also name.events.json beside the pictures, with each animation's events (frame, time, name, data, volume, pitch) and sound track");
		var picture = new WrapPanel { Orientation = Orientation.Horizontal };
		foreach (var c in new Control[] { ScaleBox, PixelBox, BakeColorBox, EventsBox })
		{
			c.Margin = new Thickness(0, 0, 12, 2);
			picture.Children.Add(c);
		}
		options.Children.Add(Row("Scale", picture));

		ToolTip.SetTip(TransparentBox, "No background. (GIFs: pixels less than half see-through become fully see-through.)");
		_swatchButton.Content = _swatch;
		ToolTip.SetTip(_swatchButton, "Click to pick the background colour");
		options.Children.Add(Row("Background", Across(TransparentBox, _swatchButton)));

		ToolTip.SetTip(ColumnsBox, "0 = automatic");
		ToolTip.SetTip(PaddingBox, "Empty pixels between the frames (and around the sheet)");
		_sheetRow = Row("Sheet", Across(RowBox, Words("columns"), ColumnsBox, Words("gap"), PaddingBox));
		options.Children.Add(_sheetRow);

		ToolTip.SetTip(DitherBox, "Smoother gradients with GIF's 256 colours, but grainier. Leave it off for pixel art and flat colours.");
		_gifRow = Row("GIF", Across(LoopBox, DitherBox));
		options.Children.Add(_gifRow);

		var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
		browse.Click += async (_, _) => await BrowseForFfmpeg();
		ToolTip.SetTip(FfmpegBox, "Videos are made with FFmpeg, a free program (ffmpeg.org). Point to it, or leave it empty if it's installed for the whole system.");
		var ffmpeg = new DockPanel();
		DockPanel.SetDock(browse, Dock.Right);
		ffmpeg.Children.Add(browse);
		ffmpeg.Children.Add(FfmpegBox);
		_videoRow = Row("FFmpeg", ffmpeg);
		options.Children.Add(_videoRow);

		ToolTip.SetTip(AtlasSizeBox, "The biggest an atlas page may be; more pages are made when the pictures don't fit one");
		ToolTip.SetTip(AtlasGapBox, "Empty pixels round each picture (keeps neighbours from bleeding in when the engine smooths)");
		ToolTip.SetTip(PowerOfTwoBox, "Pages 256, 512, 1024 … pixels wide and high (some engines and older hardware want this)");
		ToolTip.SetTip(TrimBox, "Leave out each picture's see-through edges (the file says where they were, so nothing moves)");
		var atlas = new WrapPanel { Orientation = Orientation.Horizontal };
		foreach (var c in new Control[] { Across(Words("pages up to"), AtlasSizeBox), Across(Words("gap"), AtlasGapBox), PowerOfTwoBox, TrimBox })
		{
			c.Margin = new Thickness(0, 0, 12, 2);
			atlas.Children.Add(c);
		}
		_engineRow = Row("Atlas", atlas);
		options.Children.Add(_engineRow);
		panel.Children.Add(options);

		ErrorText.Margin = new Thickness(0, 6, 0, 0);
		panel.Children.Add(ErrorText);

		var export = new Button { Content = "Export…", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
		export.Click += (_, _) => TryExport();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		var buttons = Across(export, cancel);
		buttons.HorizontalAlignment = HorizontalAlignment.Right;
		buttons.Margin = new Thickness(0, 12, 0, 0);
		panel.Children.Add(buttons);
		return panel;
	}

	// The scene's last frame: a loop's end is its start again, so it stops one before
	private int LastSceneFrame => Math.Max(0, (int)MathF.Round(_scene.Duration * _fps) - (_scene.Loop ? 1 : 0));

	private ExportFormat Format => (FormatBox.SelectedItem as FormatChoice)?.Format ?? ExportFormat.PngSequence;

	private SceneObject? PickedObject => (ObjectBox.SelectedItem as ObjectChoice)?.Object;

	// The object's animations, each with a tick (all ticked, unless an earlier export picked some)
	private void ShowAnimations(IReadOnlyCollection<Animation>? ticked)
	{
		AnimationList.Children.Clear();
		if (PickedObject is not { } obj) return;
		bool any = ticked != null && obj.Animations.Any(ticked.Contains);
		foreach (var animation in obj.Animations)
			AnimationList.Children.Add(new CheckBox
			{
				Content = $"{animation.Name}   ({(int)MathF.Round(animation.Duration * _fps)} frames{(animation.Loop ? ", loops" : "")})",
				Tag = animation,
				IsChecked = !any || ticked!.Contains(animation)
			});
	}

	internal IReadOnlyList<CheckBox> AnimationTicks => AnimationList.Children.OfType<CheckBox>().ToList();

	// Shows the options that go with the choices
	private void Sync()
	{
		if (!_ready) return;
		var format = Format;
		if (format == ExportFormat.Engine && SceneChoice.IsChecked == true && ObjectChoiceButton.IsEnabled)
		{
			ObjectChoiceButton.IsChecked = true;   // an engine takes an object, not the scene (this comes back here)
			return;
		}
		bool scene = SceneChoice.IsChecked == true;

		_objectFields.IsEnabled = !scene;
		_areaRow.IsVisible = scene;
		_rangeFields.IsVisible = scene;
		_sheetRow.IsVisible = format == ExportFormat.SpriteSheet;
		_gifRow.IsVisible = format == ExportFormat.Gif;
		_videoRow.IsVisible = format == ExportFormat.Video;
		_engineRow.IsVisible = format == ExportFormat.Engine;

		bool video = format == ExportFormat.Video;
		TransparentBox.IsEnabled = !video;
		if (video) TransparentBox.IsChecked = false;   // videos always have a background
		_swatchButton.IsEnabled = TransparentBox.IsChecked != true;
		_swatch.Opacity = _swatchButton.IsEnabled ? 1 : 0.35;
		_swatch.Background = new SolidColorBrush(Color.FromRgb(_background.Red, _background.Green, _background.Blue));

		FormatHint.Text = (FormatBox.SelectedItem as FormatChoice)?.Hint ?? "";
		ErrorText.Text = "";
	}

	private async Task BrowseForFfmpeg()
	{
		var types = OperatingSystem.IsWindows()
			? new[] { new FilePickerFileType("FFmpeg (ffmpeg.exe)") { Patterns = new[] { "ffmpeg.exe" } }, new FilePickerFileType("Programs (*.exe)") { Patterns = new[] { "*.exe" } } }
			: null;
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Where is FFmpeg?",
			AllowMultiple = false,
			FileTypeFilter = types
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
		FfmpegPath = path;
		FfmpegBox.Text = path;
	}

	// Checks the fields; when they'll do, Settings says what to export and the window closes
	internal void TryExport()
	{
		ErrorText.Text = "";
		bool scene = SceneChoice.IsChecked == true;
		var format = Format;

		if (!Whole(StepBox, 1, 60, out int step, "\"Every … frame(s)\" is a whole number from 1 to 60.")) return;
		if (!Number(ScaleBox, 0.05f, 16f, out float scale, "The scale is a number from 0.05 to 16.")) return;

		int from = 0, to = -1;
		if (scene)
		{
			int last = LastSceneFrame;
			if (!Whole(FromBox, 0, last, out from, $"The scene's frames go from 0 to {last}.")) return;
			if (!Whole(ToBox, from, last, out to, $"\"to\" is a frame from {from} to {last}.")) return;
			if (to == last) to = -1;   // to the end, even if the scene gets longer later
		}
		if (scene && format == ExportFormat.Engine)
		{
			ErrorText.Text = "A game engine takes one object: this project has none with animations.";
			return;
		}

		int columns = 0, padding = 2, atlasGap = 2;
		if (format == ExportFormat.Engine && !Whole(AtlasGapBox, 0, 64, out atlasGap, "The atlas gap is a whole number of pixels, from 0 to 64.")) return;
		if (format == ExportFormat.SpriteSheet)
		{
			if (!Whole(ColumnsBox, 0, 1000, out columns, "Columns is a whole number (0 = automatic).")) return;
			if (!Whole(PaddingBox, 0, 64, out padding, "The gap is a whole number of pixels, from 0 to 64.")) return;
		}

		var obj = PickedObject;
		var animations = AnimationTicks.Where(c => c.IsChecked == true).Select(c => (Animation)c.Tag!).ToList();
		if (!scene && (obj == null || animations.Count == 0))
		{
			ErrorText.Text = "Tick at least one animation to export.";
			return;
		}

		Settings = new ExportSettings
		{
			Scene = scene,
			Object = obj,
			Animations = animations,
			Format = format,
			Scale = scale,
			PixelArt = PixelBox.IsChecked == true,
			BakeColorLayers = BakeColorBox.IsChecked == true,
			WriteEvents = EventsBox.IsChecked == true,
			AtlasSize = AtlasSizeBox.SelectedItem as int? ?? 2048,
			AtlasGap = atlasGap,
			PowerOfTwo = PowerOfTwoBox.IsChecked == true,
			Trim = TrimBox.IsChecked == true,
			Background = TransparentBox.IsChecked == true && format != ExportFormat.Video ? null : _background,
			FrameStep = step,
			FirstFrame = from,
			LastFrame = to,
			UseStage = (_areaBox.SelectedItem as AreaChoice)?.Stage ?? false,
			Columns = columns,
			RowPerAnimation = RowBox.IsChecked == true,
			Padding = padding,
			Dither = DitherBox.IsChecked == true,
			Loop = LoopBox.IsChecked == true,
			FfmpegPath = FfmpegPath
		};
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}

	private bool Whole(TextBox box, int min, int max, out int value, string problem)
	{
		if (int.TryParse((box.Text ?? "").Trim(), out value) && value >= min && value <= max) return true;
		ErrorText.Text = problem;
		box.Focus();
		return false;
	}

	private bool Number(TextBox box, float min, float max, out float value, string problem)
	{
		string text = (box.Text ?? "").Trim();
		if ((float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
			 float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && value >= min && value <= max)
			return true;
		ErrorText.Text = problem;
		box.Focus();
		return false;
	}
}

// Shown while an export runs: how far it is, and a Cancel button. Closing it cancels too.
public sealed class ExportProgressWindow : Window
{
	private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 14 };
	private readonly TextBlock _step = new() { Text = "Starting…", TextWrapping = TextWrapping.Wrap };
	private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
	private bool _finished;

	public bool Cancelled { get; private set; }

	public ExportProgressWindow()
	{
		Title = "Exporting";
		Width = 380;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		_cancel.Click += (_, _) => Cancel();
		Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { _step, _bar, _cancel } };
		Closing += (_, e) =>
		{
			if (_finished) return;
			e.Cancel = true;   // the export closes it once it has stopped
			Cancel();
		};
	}

	public void Report(ExportProgress progress)
	{
		_bar.Maximum = Math.Max(1, progress.Total);
		_bar.Value = Math.Min(progress.Done, progress.Total);
		_step.Text = progress.Step;
	}

	// Closes it for good (the export is over)
	public void Finish()
	{
		_finished = true;
		Close();
	}

	internal void Cancel()
	{
		Cancelled = true;
		_cancel.IsEnabled = false;
		_step.Text = "Stopping…";
	}
}
