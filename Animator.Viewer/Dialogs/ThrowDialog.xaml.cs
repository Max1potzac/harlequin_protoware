using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer.Dialogs;

public partial class ThrowDialog : Window
{
	public ThrowSettings Settings { get; private set; }

	public ThrowDialog(string boneName, int frame, ThrowSettings initial)
	{
		InitializeComponent();
		Heading.Text = $"Throw \"{boneName}\" from frame {frame}";
		Settings = initial;

		SpeedXBox.Text = Num(initial.Velocity.X);
		SpeedYBox.Text = Num(initial.Velocity.Y);
		SpinBox.Text = Num(initial.SpinDegrees);
		GravityBox.Text = Num(initial.Gravity);
		GroundBox.Text = Num(initial.GroundY);
		BounceBox.Text = Num(initial.Bounce);
		FrictionBox.Text = Num(initial.Friction);
		KeyEveryBox.Text = initial.KeyEvery.ToString();

		Loaded += (_, _) => { SpeedXBox.Focus(); SpeedXBox.SelectAll(); };
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		if (!Read(SpeedXBox, out float speedX) || !Read(SpeedYBox, out float speedY) ||
			!Read(SpinBox, out float spin) || !Read(GravityBox, out float gravity) ||
			!Read(GroundBox, out float ground) || !Read(BounceBox, out float bounce) ||
			!Read(FrictionBox, out float friction))
			return;

		if (!int.TryParse(KeyEveryBox.Text.Trim(), out int keyEvery) || keyEvery < 1 || keyEvery > 30)
		{
			MessageBox.Show(this, "\"Key every\" must be a whole number of frames from 1 to 30.", "Throw / Fall");
			return;
		}

		Settings = new ThrowSettings
		{
			Velocity = new Vector2(speedX, speedY),
			SpinDegrees = spin,
			Gravity = gravity,
			GroundY = ground,
			Bounce = Math.Clamp(bounce, 0f, 1f),
			Friction = Math.Clamp(friction, 0f, 1f),
			KeyEvery = keyEvery
		};
		DialogResult = true;
	}

	// Accepts numbers in your regional format ("12,5") or with a dot ("12.5")
	private bool Read(TextBox box, out float value)
	{
		string text = box.Text.Trim();
		if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
			float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
			return true;

		MessageBox.Show(this, $"\"{text}\" isn't a number.", "Throw / Fall");
		box.Focus();
		box.SelectAll();
		return false;
	}

	private static string Num(float v) => v.ToString("0.##", CultureInfo.CurrentCulture);
}
