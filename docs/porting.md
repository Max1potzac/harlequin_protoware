# Harlequin on Linux: the plan

Harlequin's editor is WPF, which only runs on Windows. The goal is a Linux build, by way of
Avalonia (a WPF-like UI framework that runs on Windows, Linux and macOS and draws with Skia, as
Harlequin already does). Each step is its own pull request, and the Windows editor keeps working
throughout.

| Step | What | Status |
|---|---|---|
| 1 | **Animator.Engine**: everything that isn't a window (drawing, painting, imports, exports, the project file, the editing state) moved out of Animator.Viewer into a plain .NET library, built on Linux by CI | done |
| 2 | **A command-line Harlequin** (`harlequin`, see [cli.md](cli.md)) on the engine: open and save projects, import Animate and Photoshop files, bring heads (or other parts) in from other Animate projects as skins, render a frame or a sheet of skins to a picture, export. CI builds it for Linux and Windows and publishes it as a release download. | done |
| 3 | **Editing logic out of the window code** (next): the work done by MainWindow's partial files (file, scene, rig, skins, timeline, poses…) into engine classes the window calls, a part at a time, each with tests | |
| 4 | **An Avalonia editor** (Animator.Desktop) on the engine, built panel by panel next to the WPF one, with headless UI tests | |
| 5 | **Parity**, then the Avalonia editor becomes the one released (for Windows too), and the WPF one is retired | |

## Why this order

- Steps 1 and 2 make Harlequin usable on Linux without a window: batch work like building a
  project of many characters' heads can run in scripts (and be checked from an automated
  workspace, where only Linux is available).
- Step 3 makes the window code thin, so step 4 has much less to rewrite, and the logic gets tested
  without windows.
- The WPF editor stays until the Avalonia one matches it.

## Notes

- The moved files keep their `Animator.Viewer.*` namespaces for now (so nothing using them had to
  change); they can be renamed `Animator.Engine.*` once the WPF editor is retired.
- On Linux, SkiaSharp needs its native library: `SkiaSharp.NativeAssets.Linux` in the programs
  (the command-line tool, the Avalonia editor), not in the engine itself.
- Sound in the editor uses Windows' MediaPlayer today; the Avalonia editor needs a cross-platform
  player (OpenAL, LibVLC or similar).
