namespace Animator.Viewer.Tools;

// The mouse buttons and modifier keys the viewport tools go by, the editor's own (whichever UI
// toolkit reads the mouse translates into these). The names and numbers match WPF's MouseButton
// and ModifierKeys, so the Windows editor converts them with a cast and settings saved by name
// still read.
public enum PointerButton { Left, Middle, Right, XButton1, XButton2 }

[Flags]
public enum KeyMods { None = 0, Alt = 1, Control = 2, Shift = 4 }
