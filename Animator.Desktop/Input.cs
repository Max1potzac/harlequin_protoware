using Avalonia.Input;

namespace Animator.Desktop;

// Avalonia's mouse buttons and modifier keys as the engine's tools and panels go by them
public static class Input
{
	public static PointerButton? ButtonOf(PointerUpdateKind kind) => kind switch
	{
		PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => PointerButton.Left,
		PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => PointerButton.Middle,
		PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => PointerButton.Right,
		PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => PointerButton.XButton1,
		PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => PointerButton.XButton2,
		_ => null
	};

	public static KeyMods ModsOf(KeyModifiers m)
	{
		var mods = KeyMods.None;
		if (m.HasFlag(KeyModifiers.Alt)) mods |= KeyMods.Alt;
		if (m.HasFlag(KeyModifiers.Control)) mods |= KeyMods.Control;
		if (m.HasFlag(KeyModifiers.Shift)) mods |= KeyMods.Shift;
		return mods;
	}

	public static Vector2 At(PointerEventArgs e, Avalonia.Visual relativeTo)
	{
		var p = e.GetPosition(relativeTo);
		return new Vector2((float)p.X, (float)p.Y);
	}
}
