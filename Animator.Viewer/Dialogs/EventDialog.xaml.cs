using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Animator.Viewer.Dialogs;

public partial class EventDialog : Window
{
	public string EventName { get; private set; }
	public string? EventData { get; private set; }
	public float Volume { get; private set; }
	public float Pitch { get; private set; }

	// A sound file picked for the event's name (null: the sound map left as it is)
	public string? PickedSound { get; private set; }

	public EventDialog(int frame, string name = "event", string? data = null, float volume = 1f, float pitch = 1f, string? sound = null)
	{
		InitializeComponent();
		Heading.Text = $"Event at frame {frame}";
		EventName = name;
		EventData = data;
		Volume = volume;
		Pitch = pitch;
		NameBox.Text = name;
		DataBox.Text = data ?? "";
		VolumeBox.Text = (volume * 100).ToString("0.#", CultureInfo.CurrentCulture);
		PitchBox.Text = pitch.ToString("0.###", CultureInfo.CurrentCulture);
		SoundText.Text = sound ?? "(none: silent here)";

		Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
	}

	private static bool TryNumber(string text, out float value) =>
		float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
		|| float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	private void PickSound_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog
		{
			Title = "What this event sounds like",
			Filter = "Sounds (*.wav;*.mp3;*.wma;*.m4a;*.aac)|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true) return;
		PickedSound = dialog.FileName;
		SoundText.Text = dialog.FileName;
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		string name = NameBox.Text.Trim();
		if (name.Length == 0)
		{
			MessageBox.Show(this, "An event needs a name.", "Event");
			return;
		}
		if (!TryNumber(VolumeBox.Text, out float volume) || volume < 0 || volume > 400)
		{
			MessageBox.Show(this, "Volume: a percentage, 0 to 400.", "Event");
			return;
		}
		if (!TryNumber(PitchBox.Text, out float pitch) || pitch < 0.25f || pitch > 4f)
		{
			MessageBox.Show(this, "Pitch: 0.25 to 4 (1 = as recorded).", "Event");
			return;
		}
		EventName = name;
		EventData = string.IsNullOrWhiteSpace(DataBox.Text) ? null : DataBox.Text.Trim();
		Volume = volume / 100f;
		Pitch = pitch;
		DialogResult = true;
	}
}
