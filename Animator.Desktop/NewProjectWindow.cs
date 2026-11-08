using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// File > New, as the Windows editor asks it: a character (for games) or an animation (a scene for
// a video), the two starting points of the same kind of file. A character can start from the
// demo one; an animation has a stage and a length. Both have a frame rate.
public sealed class NewProjectWindow : Window
{
	internal readonly RadioButton CharacterChoice = new() { Content = "Character", GroupName = "kind", IsChecked = true, FontWeight = FontWeight.SemiBold };
	internal readonly RadioButton AnimationChoice = new() { Content = "Animation", GroupName = "kind", FontWeight = FontWeight.SemiBold };
	internal readonly CheckBox DemoBox = new() { Content = "Start from the demo character", IsChecked = true, Margin = new Thickness(20, 0, 0, 8) };
	internal readonly TextBox StageWidthBox = new() { Text = "1280", Width = 70 };
	internal readonly TextBox StageHeightBox = new() { Text = "720", Width = 70 };
	internal readonly TextBox LengthBox = new() { Text = "120", Width = 70 };
	internal readonly TextBox FpsBox = new() { Width = 56 };
	internal readonly TextBlock ErrorText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	private readonly StackPanel _animationFields;

	public ProjectKind Kind { get; private set; } = ProjectKind.Character;
	public bool UseDemo { get; private set; } = true;
	public int StageWidth { get; private set; } = 1280;
	public int StageHeight { get; private set; } = 720;
	public int LengthFrames { get; private set; } = 120;
	public int Fps { get; private set; }

	public NewProjectWindow(int fps)
	{
		Title = "New project";
		Width = 420;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Fps = fps;
		FpsBox.Text = fps.ToString();
		static TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(20, 0, 0, 6) };
		ToolTip.SetTip(DemoBox, "Off: an empty rig with one bone to build on");
		ToolTip.SetTip(StageWidthBox, "The picture the scene is made for, in pixels (its video size)");

		_animationFields = new StackPanel
		{
			Margin = new Thickness(20, 0, 0, 0),
			Spacing = 6,
			Children =
			{
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Words("Stage"), StageWidthBox, Words("×"), StageHeightBox, Words("pixels") } },
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Words("Length"), LengthBox, Words("frames") } }
			}
		};
		CharacterChoice.IsCheckedChanged += (_, _) => KindChanged();
		AnimationChoice.IsCheckedChanged += (_, _) => KindChanged();

		var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		Content = new StackPanel
		{
			Margin = new Thickness(14),
			Spacing = 4,
			Children =
			{
				CharacterChoice,
				Note("One character (or prop) and its animations, for games. You start inside it, ready to rig and animate."),
				DemoBox,
				AnimationChoice,
				Note("A whole scene played over time on the scene timeline, for a video or GIF. It starts with a blank background the size of the stage."),
				_animationFields,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 12, 0, 0), Children = { Words("Frames per second"), FpsBox } },
				ErrorText,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } }
			}
		};
		KindChanged();
		UiSize.Fit(this);
	}

	// Only the chosen kind's options can be changed
	private void KindChanged()
	{
		bool animation = AnimationChoice.IsChecked == true;
		_animationFields.IsEnabled = animation;
		DemoBox.IsEnabled = !animation;
	}

	internal void TryOk()
	{
		bool animation = AnimationChoice.IsChecked == true;
		if (!int.TryParse(FpsBox.Text?.Trim(), out int fps) || fps < 1 || fps > 240)
		{
			ErrorText.Text = "Frames per second is a whole number from 1 to 240.";
			return;
		}
		if (animation)
		{
			if (!int.TryParse(StageWidthBox.Text?.Trim(), out int w) || !int.TryParse(StageHeightBox.Text?.Trim(), out int h) ||
				w < 16 || h < 16 || w > 4096 || h > 4096)
			{
				ErrorText.Text = "The stage is a whole number of pixels, from 16 to 4096 each way.";
				return;
			}
			if (!int.TryParse(LengthBox.Text?.Trim(), out int frames) || frames < 1 || frames > 100000)
			{
				ErrorText.Text = "The length is a whole number of frames (1 or more).";
				return;
			}
			StageWidth = w;
			StageHeight = h;
			LengthFrames = frames;
		}
		Kind = animation ? ProjectKind.Animation : ProjectKind.Character;
		UseDemo = DemoBox.IsChecked == true;
		Fps = fps;
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
