using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Animator.Viewer.Dialogs;

public partial class CanvasSizeDialog : Window
{
	public int NewWidth { get; private set; }
	public int NewHeight { get; private set; }
	public int AnchorX { get; private set; } = 1;   // 0 left, 1 center, 2 right
	public int AnchorY { get; private set; } = 1;   // 0 top,  1 center, 2 bottom

	public CanvasSizeDialog(int width, int height)
	{
		InitializeComponent();
		CurrentSize.Text = $"Current size: {width} × {height}";
		WidthBox.Text = width.ToString();
		HeightBox.Text = height.ToString();

		// 3×3 toggle buttons; only one can be pressed at a time
		for (int i = 0; i < 9; i++)
		{
			int index = i;   // copy for the lambda below
			var button = new RadioButton
			{
				GroupName = "Anchor",
				Style = (Style)FindResource(typeof(ToggleButton)),
				Margin = new Thickness(1),
				IsChecked = i == 4   // center
			};
			button.Checked += (_, _) => { AnchorX = index % 3; AnchorY = index / 3; };
			AnchorGrid.Children.Add(button);
		}
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h) ||
			w < 1 || h < 1 || w > 4096 || h > 4096)
		{
			MessageBox.Show(this, "Width and height must be whole numbers from 1 to 4096.", "Canvas Size");
			return;
		}
		NewWidth = w;
		NewHeight = h;
		DialogResult = true;   // closes the dialog; ShowDialog() returns true
	}
}