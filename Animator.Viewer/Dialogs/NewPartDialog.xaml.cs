using System.Windows;

namespace Animator.Viewer.Dialogs;

public partial class NewPartDialog : Window
{
	public string PartName { get; private set; } = "part";
	public int PartWidth { get; private set; } = 64;
	public int PartHeight { get; private set; } = 64;

	public NewPartDialog() : this(null, null, null) { }

	// The same dialog for other blank canvases (a new drawn object): its own title, name and hint
	public NewPartDialog(string? title, string? name, string? hint)
	{
		InitializeComponent();
		if (title != null) Title = title;
		if (name != null) PartName = name;
		if (hint != null) HintText.Text = hint;
		NameBox.Text = PartName;
		WidthBox.Text = PartWidth.ToString();
		HeightBox.Text = PartHeight.ToString();

		// Once the window is shown, put the cursor in the name box with the text selected
		Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h) ||
			w < 1 || h < 1 || w > 4096 || h > 4096)
		{
			MessageBox.Show(this, "Width and height must be whole numbers from 1 to 4096.", Title);
			return;
		}
		PartName = string.IsNullOrWhiteSpace(NameBox.Text) ? "part" : NameBox.Text.Trim();
		PartWidth = w;
		PartHeight = h;
		DialogResult = true;
	}
}