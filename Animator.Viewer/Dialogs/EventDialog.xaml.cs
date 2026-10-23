using System.Windows;

namespace Animator.Viewer.Dialogs;

public partial class EventDialog : Window
{
	public string EventName { get; private set; }
	public string? EventData { get; private set; }

	public EventDialog(int frame, string name = "event", string? data = null)
	{
		InitializeComponent();
		Heading.Text = $"Event at frame {frame}";
		EventName = name;
		EventData = data;
		NameBox.Text = name;
		DataBox.Text = data ?? "";

		Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		string name = NameBox.Text.Trim();
		if (name.Length == 0)
		{
			MessageBox.Show(this, "An event needs a name.", "Event");
			return;
		}
		EventName = name;
		EventData = string.IsNullOrWhiteSpace(DataBox.Text) ? null : DataBox.Text.Trim();
		DialogResult = true;
	}
}
