# Harlequin on Linux: the plan

Harlequin's editor is WPF, which only runs on Windows. The goal is a Linux build, by way of
Avalonia (a WPF-like UI framework that runs on Windows, Linux and macOS and draws with Skia, as
Harlequin already does). Each step is its own pull request, and the Windows editor keeps working
throughout.

| Step | What | Status |
|---|---|---|
| 1 | **Animator.Engine**: everything that isn't a window (drawing, painting, imports, exports, the project file, the editing state) moved out of Animator.Viewer into a plain .NET library, built on Linux by CI | done |
| 2 | **A command-line Harlequin** (`harlequin`, see [cli.md](cli.md)) on the engine: open and save projects, import Animate and Photoshop files, bring heads (or other parts) in from other Animate projects as skins, render a frame or a sheet of skins to a picture, export. CI builds it for Linux and Windows and publishes it as a release download. | done |
| 3 | **Editing logic out of the window code** (the session, the tools, the panels and the viewport, see below): the work done by MainWindow's partial files (file, scene, rig, skins, timeline, poses…) into engine classes the window calls, a part at a time, each with tests | done |
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
13. **The paint editor** (EditorSession.Paint.cs, Painting/PaintToolbox.cs): the paint session and
   its tools (picked by name, and which options each uses); a part's drawing opened (a skin's own
   pixels when editing one) and closed; the editor following the part's drawings along the
   timeline. The canvas (zoom, the mouse) and the panel stay with the window.
14. **The viewport tools** (Animator.Engine/Tools): BoneTool (picking, posing and rigging bones and
   parts), ObjectTool (whole objects), Snapping and KeyShift, with the engine's own mouse buttons
   and modifier keys (PointerButton, KeyMods: the same names and numbers as WPF's, so the Windows
   editor casts, and settings saved by name still read).
15. **The viewport's other drags** (GuideTool, MeshEditTool, EditorSession box drags) and **IK,
   space switches and throws** (EditorSession.Ik.cs): guides out of the rulers, moved and taken
   away; mesh points dragged and weights painted; hitboxes and points moved and resized; IK
   matched, baked, switched, toggled and flipped; a bone made to follow another, or thrown.
16. **The timelines, the graph, the paint canvas and the settings** (Animator.Engine/Panels,
   PaintCanvas, AppSettings): drawn with Skia already, so moved whole; the theme's colours handed
   in by the editor (CanvasTheme).
17. **What the viewport shows** (Rendering/ViewportPainter.cs): the scene, the onion ghosts, the open
   object's bones, handles, hitboxes and mesh, the stage, grid, guides and rulers; the paint
   editor's light table. And **autosave's bookkeeping** (Files/Autosaver.cs): when a copy is kept,
   written in the background, forgotten on save, adopted after a crash.

What stays in Animator.Viewer is the Windows UI itself, for step 4 to build again in Avalonia:
the window's layout, menus and panels of controls (MainWindow.xaml and its partial files, which
now mostly fill in controls and call the session), the dialogs, the two WPF controls of the
property panels (NumberScrub, AdjustEditor), the theme brushes (Themes), the shortcut map (KeyMap,
on WPF's keys), the sound player (MediaPlayer), and the timers. The Avalonia editor hosts the
engine's panels, tools and ViewportPainter on its own Skia surfaces, and translates its mouse
and keys into PointerButton and KeyMods.


## Step 4: the Avalonia editor

Animator.Desktop (the program `harlequin-editor`) is the editor on Avalonia 12, built a piece at
a time. It references SkiaSharp 4 by name (Avalonia 12.1 takes it so; its own default is 3), and
draws the engine's panels into bitmaps of their own (SkiaView), so it looks the same everywhere,
the headless tests included. Animator.Desktop.Tests shows the window on Avalonia's headless
platform and saves what it shows as pictures; CI (the `desktop` job) runs them on Linux, keeps the
pictures with the run and on the branch `shots/<branch>`, and publishes the editor for Linux
(`editor-linux-x64`, kept with the run).

1. **The first window**: the viewport (ViewportPainter, the bone and object tools, the guides),
   the timeline (the animation's, or the scene's at scene level), the rig list (bones and parts,
   or the scene's objects), New / Open / Save / Save as, undo, playback, Build / Animate, keying
   the pose, the Select / Move / Rotate tools, and the main shortcuts.
2. **The properties panel**: the picked bone's numbers (where, turned, long, scaled: the rest pose
   in Build, the pose at the playhead in Animate, keyed as typed), its name, parent and links,
   a bone added on it or deleted; the picked part's placement, pivot, opacity, blend, bone and
   place in the order; the picked object's place in the scene. Numbers are typed or dragged by
   their labels (a drag is one undo step). The shown and typed numbers are the engine's
   (EditorSession.Fields.cs), which the Windows editor's panels use too.
3. **The animations bar** over the timeline: the open object's animations (picked, new, a copy,
   deleted), the edited one's name, length and looping, the project's frame rate; picked frames
   split or copied into an animation sequence of their own (the timeline's right-click).
4. **Skins and poses**, as tabs next to the rig list. Skins: made from the picked parts (or each
   picked part, or one part's drawings, a skin of a category; or the colour layers; or empty, to
   edit), shown and hidden, ✎ edited, renamed, given a category or made an overlay, parts added,
   taken out or hidden by them, moved, deleted (their drawings back to the object, or with
   them), a category renamed. Poses: kept, renamed, put on again (mirrored, partly, on just the
   picked), kept again, deleted; the pose mirrored, copied and pasted; the rig symmetrized.
5. **The paint editor**, beside the viewport (a double-click on a part, or Paint on the part
   panel): the engine's PaintCanvas, the eleven tools with the options each uses, the colours
   (a colour dialog, a palette, typed as hex), the drawing's layers (shown, named, faded, added,
   deleted, moved), the canvas size; it follows the part's drawings along the timeline, and has
   the Windows editor's keys while the mouse is over it.
6. **Import** (File > Import, Ctrl+I), through the same dialogs as the Windows editor's: a
   Photoshop file (the layers ticked, a suggested rig with every choice changeable and shown on
   the picture, the size); an Animate project (the timeline, an animation per label, the parts
   in, in the rig or effects, the rig worked out from the motion and played on the picture, the
   rest pose); a sprite sheet (cut as a grid, into its separate sprites, by hand or as its JSON
   says, the background made see-through, animations made of the pieces and played, brought in
   as objects or as parts of the open one).
7. **Export** (File > Export, Ctrl+E), the Windows editor's dialog: the scene or one object's
   animations, as PNG pictures, a GIF, a sprite sheet, a video or a game engine's rig and atlas,
   with the options each format uses; a progress window with Cancel; then where it went, and
   Show it.
8. **Settings** (File > Settings, Ctrl+,): any command's keys (a port of the Windows editor's key
   map with Avalonia's keys, so both keep the same shortcuts in settings.json), the mouse buttons,
   the recovery copy's interval and the backups, the theme (Fluent's dark or light) and the size
   of the panels and dialogs. Autosave runs as in the Windows editor, and a copy a crashed run
   left is offered back at the start.
9. **Sound** (Sound ▾ on the animation bar): event sounds and the sound track heard while
   playing, the track's waveform under the keys, the sound map, events and sound map files in and
   out, mute. Heard through OpenAL Soft (Silk.NET, with its library for each system); WAV is read
   here, MP3 by NLayer, Ogg Vorbis by NVorbis. (WMA and AAC, which the Windows editor plays
   through Windows' media player, aren't.)
10. **Building the rig** (the Rig tab): Bones or Layers (every part in drawing order, front
    first, with its bone, mask, clipping and skins; a drawn object always shows these), + Part ▾
    (blank, or from a picture), Bind ▾ (parts onto their nearest bones, a bone following a part),
    Give it bones ▾ for a drawn object, and whether parts stay put and animations stay as they
    were. Rows dragged: parts onto bones, bones onto bones, parts up or down the layers. The order
    keys (Ctrl+] / Ctrl+[, Shift: all the way) and Order and mask ▾ (also a part row's
    right-click): the draw order and clipping masks.
11. **Picking, hiding, locking** (the rig list): Ctrl+click, Shift+click (with Ctrl: added), a
    band on the picked rows; the eye and the lock on each row; the Pick menu and its keys (Ctrl+A,
    Ctrl+Shift+A, H / Shift+H / Alt+H, L / Shift+L / Alt+L); selection sets (a row of them over
    the list, + Set, each one's right-click menu) and bone colours (Colour ▾ in the bone panel).
12. **The timeline's tools and events**: auto key (Shift+K), only the picked's rows, playback
    speed and Playback ▾ (back and forth, the picked frames over and over, stopping goes back);
    the right-click menus and Keys ▾ (a key's curve and easing, keys a frame earlier or later,
    stretched, reversed; every key at a frame; frames inserted or removed; the picked frames'
    sequence items); E, Alt+Left / Alt+Right, Alt+= / Alt+-. Event markers added and edited (the
    event dialog, a double-click), flashed over the viewport while playing.
13. **The graph editor**: Curves on the animation bar, or Ctrl+G: the picked's keys as curves
    (GraphPanel), keys dragged in time and value, Bézier handles, zoom and move, a right-click on
    a key (and Keys ▾) for the key menu.
14. **Frame by frame**: the part panel's Drawings rows (the drawing at the playhead or at rest,
    picked, hidden, renamed; New, Copy, Shorter, Longer, Delete), F7 / F6 / F5 / Shift+F5, and the
    onion skin's box and settings on the animation bar (the paint editor's light table follows it).
15. **Meshes**: the part panel's Mesh box (made over the picture, removed; Edit points and Paint
    weights, which the viewport's MeshEditTool now gets the mouse for; Weights ▾; the brush's
    reach and strength).
16. **IK and following** (the bone panel): Turn / Tilt; look at (target, shape, follow, range);
    the IK chain (length, target, on, the elbow) and the IK keys and menu (I, Shift+I, T, Shift+T,
    B); Follows (parent, detached, held by another bone, from the playhead on) and the throw /
    fall dialog; Follow and jiggle (copy and jiggle constraints, a slider drag one undo step).
17. **Effects and colour**: a part's tint (its swatch, keyed in Animate like the opacity); the Rig
    tab's Colour view (an object's colour layers: + Colour layer, on / off, order, name, the
    sliders and switches, parts left out); colour layers inside a drawing in the paint editor
    (+ ◐, Replace colour too).
18. **Hitboxes and points**: the Rig tab's Boxes view (+ Hitbox of a kind, box or circle, round the
    picked part; + Point; name, bone, kind, shape, on at rest, on / off from the playhead in
    Animate, fit round the picked part, their numbers); picked, moved and resized in the viewport
    (a drag one undo step); J shows them without the view.
19. **The scene**: at scene level the Rig tab lists the objects (front first) under + Object (rigged,
    drawn blank or from a picture, from a file), Copy, Delete, ▲ / ▼ and Edit; the picked object's
    flags (visible, locked, export, onion), preview animation and what carries it; the picked
    clip (animation, start, length, first frame, loop, fade in, as mixed, edit in place, delete);
    the scene's bar (length, fit to clips, loop, stage, FPS); the scene timeline's right-click
    menu; Delete and Enter at scene level. File > New asks what kind (a character, from the demo
    one or not; an animation with its stage and length) and the frame rate. Edit > Bring in from
    another object (also + Skin ▾) and from Animate projects.
20. **The mixer** (Mixer… on the animation bar): three tracks of animations over each other (weight,
    speed, loop) played in the viewport on the mixer's own clock; a fade from one animation to
    another tried out, kept for the pair or back to the default (undoable); the default fade.
21. **The shell, part 1**: File > Open recent (the last 10; one that's gone is taken off) and
    Reopen; Places (undo and redo first go back to where an edit was made: the object, the
    animation, Build or Animate, painting); Edit > History (Ctrl+Alt+Z: every step, click one to
    go back or on to it); Help: Keyboard and mouse (F1, following where you are), the user
    manual, the engine format, About; the status line's mode and last edit.

### Still to come in step 4

What the Windows editor has that this one doesn't yet, panel by panel (Animator.Viewer/Shell,
checked against Animator.Desktop after the sound). The logic is mostly the engine's already
(step 3); what's missing is the window side. In the order that would make the editor usable for
whole jobs soonest:

1. **The rest of the shell**: the window's size, panel widths and timeline height remembered
   (Layout); the Paint tool on the tool bar (4); the view toggles still missing (pixel filtering,
   snap to grid / guides, lock the guides). The Windows editor's high-contrast theme and theme
   files.

Step 5 can start once these are in: using this editor for real work alongside the Windows one,
fixing what's different, and then retiring Animator.Viewer.
