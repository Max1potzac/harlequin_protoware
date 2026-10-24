using System.Windows;

namespace Animator.Viewer.Dialogs;

// File > New: a character (for games) or an animation (a scene for a video), the two starting
// points of the same kind of file
public partial class NewProjectDialog : Window
{
	public ProjectKind Kind { get; private set; } = ProjectKind.Character;
	public bool UseDemo { get; private set; } = true;
	public int StageWidth { get; private set; } = 1280;
	public int StageHeight { get; private set; } = 720;
	public int LengthFrames { get; private set; } = 120;
	public int Fps { get; private set; }

	public NewProjectDialog(int fps)
	{
		InitializeComponent();
		Fps = fps;
		StageWidthBox.Text = StageWidth.ToString();
		StageHeightBox.Text = StageHeight.ToString();
		LengthBox.Text = LengthFrames.ToString();
		FpsBox.Text = Fps.ToString();
		Kind_Changed(this, new RoutedEventArgs());
	}

	// Only the chosen kind's options can be changed
	private void Kind_Changed(object sender, RoutedEventArgs e)
	{
		if (AnimationFields == null || DemoBox == null) return;   // still being built
		bool animation = AnimationChoice.IsChecked == true;
		AnimationFields.IsEnabled = animation;
		DemoBox.IsEnabled = !animation;
	}

	private void Create_Click(object sender, RoutedEventArgs e)
	{
		bool animation = AnimationChoice.IsChecked == true;
		if (!int.TryParse(FpsBox.Text.Trim(), out int fps) || fps < 1 || fps > 240)
		{
			MessageBox.Show(this, "Frames per second must be a whole number from 1 to 240.", Title);
			return;
		}
		if (animation)
		{
			if (!int.TryParse(StageWidthBox.Text.Trim(), out int w) || !int.TryParse(StageHeightBox.Text.Trim(), out int h) ||
				w < 16 || h < 16 || w > 4096 || h > 4096)
			{
				MessageBox.Show(this, "The stage is a whole number of pixels, from 16 to 4096 each way.", Title);
				return;
			}
			if (!int.TryParse(LengthBox.Text.Trim(), out int frames) || frames < 1 || frames > 100000)
			{
				MessageBox.Show(this, "The length is a whole number of frames (1 or more).", Title);
				return;
			}
			StageWidth = w;
			StageHeight = h;
			LengthFrames = frames;
		}

		Kind = animation ? ProjectKind.Animation : ProjectKind.Character;
		UseDemo = DemoBox.IsChecked == true;
		Fps = fps;
		DialogResult = true;
	}
}
