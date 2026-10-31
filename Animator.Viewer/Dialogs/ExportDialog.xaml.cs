using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkiaSharp;

namespace Animator.Viewer.Dialogs;

// File > Export: what (the scene, or one object's animations), the format, and its options.
// Only the options the chosen format uses are shown.
public partial class ExportDialog : Window
{
	private sealed record FormatChoice(ExportFormat Format, string Label, string Hint)
	{
		public override string ToString() => Label;
	}

	private sealed record AreaChoice(bool Stage, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly FormatChoice[] Formats =
	{
		new(ExportFormat.PngSequence, "PNG pictures (one per frame)",
			"name_0000.png, name_0001.png … in the folder you pick. Keeps see-through parts; video editors and game engines take these."),
		new(ExportFormat.Gif, "Animated GIF",
			"Plays almost anywhere (chats, web pages). GIFs have at most 256 colours and no half see-through pixels."),
		new(ExportFormat.SpriteSheet, "Sprite sheet (PNG + JSON)",
			"Every frame on one picture, in a grid, plus a JSON file listing the frames and animations " +
			"(the Aseprite / TexturePacker layout that game engines and tools read). Harlequin can import it again."),
		new(ExportFormat.Video, "Video (MP4)",
			"An H.264 video, made with FFmpeg (a free program). Videos can't be see-through: the background colour fills the empty space.")
	};

	private readonly Scene _scene;
	private readonly int _fps;
	private SKColor _background = SKColors.White;
	private bool _ready;

	public ExportSettings Settings { get; private set; } = new();
	public string? FfmpegPath { get; private set; }

	// last: the previous export's choices (null the first time). current: the object being edited.
	public ExportDialog(Scene scene, SceneObject? current, int fps, ExportSettings? last, string? ffmpegPath, bool pixelArt)
	{
		InitializeComponent();
		_scene = scene;
		_fps = Math.Max(1, fps);
		FfmpegPath = ffmpegPath;
		FfmpegBox.Text = ffmpegPath ?? "";

		var objects = scene.Objects.Where(o => o.Animations.Count > 0).ToList();
		ObjectBox.ItemsSource = objects;
		ObjectChoice.IsEnabled = objects.Count > 0;
		ObjectBox.SelectedItem =
			last?.Object is { } previous && objects.Contains(previous) ? previous
			: current != null && objects.Contains(current) ? current
			: objects.FirstOrDefault();

		FormatBox.ItemsSource = Formats;
		var areas = new List<AreaChoice>();
		if (scene.HasStage) areas.Add(new AreaChoice(true, $"The stage ({scene.StageWidth} × {scene.StageHeight})"));
		areas.Add(new AreaChoice(false, "Everything that shows (fitted around it)"));
		AreaBox.ItemsSource = areas;

		// The last export's choices, or what suits the kind of project
		var s = last ?? new ExportSettings
		{
			Scene = scene.Kind == ProjectKind.Animation || objects.Count == 0,
			Format = scene.Kind == ProjectKind.Animation ? ExportFormat.Gif : ExportFormat.SpriteSheet,
			PixelArt = pixelArt
		};
		(s.Scene || objects.Count == 0 ? SceneChoice : ObjectChoice).IsChecked = true;
		FormatBox.SelectedItem = Formats.First(f => f.Format == s.Format);
		AreaBox.SelectedItem = areas.FirstOrDefault(a => a.Stage == s.UseStage) ?? areas[0];

		int lastFrame = LastSceneFrame;
		FromBox.Text = Math.Clamp(s.FirstFrame, 0, lastFrame).ToString();
		ToBox.Text = (s.LastFrame >= 0 ? Math.Min(s.LastFrame, lastFrame) : lastFrame).ToString();
		StepBox.Text = s.FrameStep.ToString();
		ScaleBox.Text = s.Scale.ToString("0.###", CultureInfo.CurrentCulture);
		PixelBox.IsChecked = s.PixelArt;
		BakeColorBox.IsChecked = s.BakeColorLayers;
		EventsBox.IsChecked = s.WriteEvents;
		TransparentBox.IsChecked = s.Background == null;
		_background = s.Background ?? SKColors.White;
		RowBox.IsChecked = s.RowPerAnimation;
		ColumnsBox.Text = s.Columns.ToString();
		PaddingBox.Text = s.Padding.ToString();
		LoopBox.IsChecked = s.Loop;
		DitherBox.IsChecked = s.Dither;
		ShowAnimations(last?.Animations);

		_ready = true;
		Sync();
	}

	// The scene's last frame: a loop's end is its start again, so it stops one before
	private int LastSceneFrame => Math.Max(0, (int)MathF.Round(_scene.Duration * _fps) - (_scene.Loop ? 1 : 0));

	private ExportFormat Format => (FormatBox.SelectedItem as FormatChoice)?.Format ?? ExportFormat.PngSequence;

	// The object's animations, each with a tick (all ticked, unless an earlier export picked some)
	private void ShowAnimations(IReadOnlyCollection<Animation>? ticked)
	{
		AnimationList.Items.Clear();
		if (ObjectBox.SelectedItem is not SceneObject obj) return;
		bool any = ticked != null && obj.Animations.Any(ticked.Contains);
		foreach (var animation in obj.Animations)
			AnimationList.Items.Add(new CheckBox
			{
				Content = $"{animation.Name}   ({(int)MathF.Round(animation.Duration * _fps)} frames{(animation.Loop ? ", loops" : "")})",
				Tag = animation,
				IsChecked = !any || ticked!.Contains(animation)
			});
	}

	// Shows the options that go with the choices
	private void Sync()
	{
		if (!_ready) return;
		bool scene = SceneChoice.IsChecked == true;
		var format = Format;

		ObjectFields.IsEnabled = !scene;
		Show(scene, AreaLabel, AreaBox, RangeFields);
		Show(format == ExportFormat.SpriteSheet, SheetLabel, SheetFields);
		Show(format == ExportFormat.Gif, GifLabel, GifFields);
		Show(format == ExportFormat.Video, VideoLabel, VideoFields);

		bool video = format == ExportFormat.Video;
		TransparentBox.IsEnabled = !video;
		if (video) TransparentBox.IsChecked = false;   // videos always have a background
		BackgroundSwatchBorder.IsEnabled = TransparentBox.IsChecked != true;
		BackgroundSwatchBorder.Opacity = BackgroundSwatchBorder.IsEnabled ? 1 : 0.35;
		BackgroundSwatch.Fill = new SolidColorBrush(Color.FromArgb(255, _background.Red, _background.Green, _background.Blue));

		FormatHint.Text = (FormatBox.SelectedItem as FormatChoice)?.Hint ?? "";
		ErrorText.Text = "";

		static void Show(bool visible, params UIElement[] elements)
		{
			foreach (var e in elements) e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void Choice_Changed(object sender, RoutedEventArgs e) => Sync();
	private void Format_Changed(object sender, SelectionChangedEventArgs e) => Sync();
	private void Transparent_Click(object sender, RoutedEventArgs e) => Sync();

	private void ObjectBox_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready) return;
		ShowAnimations(null);
	}

	private void BackgroundSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!BackgroundSwatchBorder.IsEnabled) return;
		var picker = new ColorPickerDialog(_background, "Background colour") { Owner = this };
		if (picker.ShowDialog() != true) return;
		_background = picker.Color.WithAlpha(255);   // a background is solid
		Sync();
	}

	private void FfmpegBrowse_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog { Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|Programs (*.exe)|*.exe", Title = "Where is ffmpeg.exe?" };
		if (dialog.ShowDialog(this) != true) return;
		FfmpegPath = dialog.FileName;
		FfmpegBox.Text = dialog.FileName;
	}

	private void Export_Click(object sender, RoutedEventArgs e)
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

		int columns = 0, padding = 2;
		if (format == ExportFormat.SpriteSheet)
		{
			if (!Whole(ColumnsBox, 0, 1000, out columns, "Columns is a whole number (0 = automatic).")) return;
			if (!Whole(PaddingBox, 0, 64, out padding, "The gap is a whole number of pixels, from 0 to 64.")) return;
		}

		var obj = ObjectBox.SelectedItem as SceneObject;
		var animations = AnimationList.Items.OfType<CheckBox>()
			.Where(c => c.IsChecked == true).Select(c => (Animation)c.Tag).ToList();
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
			Background = TransparentBox.IsChecked == true && format != ExportFormat.Video ? null : _background,
			FrameStep = step,
			FirstFrame = from,
			LastFrame = to,
			UseStage = (AreaBox.SelectedItem as AreaChoice)?.Stage ?? false,
			Columns = columns,
			RowPerAnimation = RowBox.IsChecked == true,
			Padding = padding,
			Dither = DitherBox.IsChecked == true,
			Loop = LoopBox.IsChecked == true,
			FfmpegPath = FfmpegPath
		};
		DialogResult = true;
	}

	private bool Whole(TextBox box, int min, int max, out int value, string problem)
	{
		if (int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max) return true;
		ErrorText.Text = problem;
		box.Focus();
		return false;
	}

	private bool Number(TextBox box, float min, float max, out float value, string problem)
	{
		string text = box.Text.Trim();
		if ((float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
			 float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && value >= min && value <= max)
			return true;
		ErrorText.Text = problem;
		box.Focus();
		return false;
	}
}
