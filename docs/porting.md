# Harlequin on Linux: the plan

Harlequin's editor is WPF, which only runs on Windows. The goal is a Linux build, by way of
Avalonia (a WPF-like UI framework that runs on Windows, Linux and macOS and draws with Skia, as
Harlequin already does). Each step is its own pull request, and the Windows editor keeps working
throughout.

| Step | What | Status |
|---|---|---|
| 1 | **Animator.Engine**: everything that isn't a window (drawing, painting, imports, exports, the project file, the editing state) moved out of Animator.Viewer into a plain .NET library, built on Linux by CI | done |
| 2 | **A command-line Harlequin** (`harlequin`, see [cli.md](cli.md)) on the engine: open and save projects, import Animate and Photoshop files, bring heads (or other parts) in from other Animate projects as skins, render a frame or a sheet of skins to a picture, export. CI builds it for Linux and Windows and publishes it as a release download. | done |
| 3 | **Editing logic out of the window code** (in progress: the session, see below): the work done by MainWindow's partial files (file, scene, rig, skins, timeline, poses…) into engine classes the window calls, a part at a time, each with tests | |
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

## Step 3, piece by piece

1. **EditorSession** (Animator.Engine/Editor): the project as it's being edited — the scene and
   its pictures, the open object or scene level, both playheads, posing everything at a moment,
   and the file (new, open, save). MainWindow keeps one and shows it.
2. **Scene editing** (EditorSession.Scene.cs): objects added (rigged, drawn), copied, deleted and
   reordered; their name, placement, settings, what they hang on and the animation they preview;
   the scene's length, loop and stage; clips added, timed, changed, fitted and deleted. Each edit
   is one undo step and says whether it changed anything. The Objects panel, the object
   properties and the scene timeline call these.
3. **The open object's animations** (EditorSession.Animations.cs): switching, new, duplicated,
   deleted (one always stays), renamed (the mix plan's fades and the preview follow), looping,
   length, frame rate, and picked frames made an animation sequence. The Animations panel calls these.
4. **Skins** (EditorSession.Skins.cs): made from parts, a part's drawings or colour layers; filled,
   emptied and hiding parts; shown, ordered, renamed, put in categories (and categories renamed);
   dissolved or deleted; the skin being edited, and painting in it (its own pixels). The Skins
   panel calls these.
5. **The rig** (EditorSession.Rig.cs): bones added, renamed, set at rest (parts staying put or
   not) and keyed by value, connected, inheriting, hung from others, dissolved or deleted (objects
   they carried let go); look and IK constraints; parts put on bones (by hand or the nearest); a
   drawn object given bones. The Rig panel, Bind and the bone list's drags call these.
6. **Keys** (KeyEditor): the selected keys' curve and easing, deleted, copied and pasted, nudged,
   stretched and reversed; keys selected by frame or by what they move; frames inserted and
   removed; the playhead to the next key; the pose keyed (S) or held (auto key off), and the
   picked's keys at the playhead deleted. The timeline panel keeps the drawing and the mouse.
7. **Poses and events** (EditorSession.Poses.cs): saved poses put on (partly, mirrored, on the
   picked), the pose mirrored, copied and pasted, a rig made symmetric; the list of poses kept,
   updated, renamed and deleted; events added and changed, with their sounds.
8. **Parts, drawings and colour** (EditorSession.Parts.cs, .Colour.cs): parts made, renamed,
   placed at rest or keyed, put on another bone, deleted; drawings shown, made (blank or copied),
   renamed, deleted, held longer or shorter; colour layers added, deleted, moved, renamed, shown,
   leaving parts out, their numbers set at rest or keyed (a drawing's colour layers too).
9. **Hitboxes, points and meshes** (EditorSession.Boxes.cs): hitboxes and points added (round the
   selected part), deleted, renamed, fitted, typed, put on bones, switched on and off at rest or
   keyed; what's under the mouse and how a drag moves or resizes one; meshes made over a drawing's
   visible pixels, weighted (nearest bones, one bone, none) and removed.
10. **Draw order, masks and constraints** (EditorSession.Order.cs): parts stepped or dragged in
   front of or behind others (the rest order in Setup, keyed at the playhead in Animate); clipping
   masks made, shown and stopped; follow (copy) and jiggle constraints set up and their numbers set.
11. **Editing aids and sound** (EditorSession.Aids.cs): bones and parts hidden, shown, locked and
   unlocked; the bones' own colours; selection sets made, changed, renamed and deleted; guides and
   the grid size; an animation's sound track, the scene's sound map and events brought in from files.
12. **Imports and bring-ins** (EditorSession.Import.cs, .Parts.cs): Photoshop files, Animate
   projects and sprite sheets read (saying what's wrong with a file rather than throwing) and
   brought in as objects or as parts of the open one; another object, or Animate projects' parts,
   merged into the open one; a part's blend mode, colour and opacity at rest or keyed.

