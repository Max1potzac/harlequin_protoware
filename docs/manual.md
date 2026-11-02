# Harlequin user manual

Harlequin is a 2D animation tool for game assets. You build characters and props from bones,
parts and drawings, animate them, and export them for a game engine: sprite sheets, pictures,
videos, or Harlequin's own engine format with a ready-made Godot runtime. You can also lay
several objects out on a scene timeline and render the scene as a video or GIF.

This manual goes through the program the way you'll meet it, from first launch to export. The
last sections are reference: [every shortcut](#22-keyboard-reference), [where files
live](#23-files-and-folders) and [troubleshooting](#24-troubleshooting).

Two habits save a lot of reading:

- **Hover anything.** Almost every button, field and tick box has a tooltip that says exactly
  what it does.
- **Press F1.** Help → Keyboard and mouse opens a window listing what every click, drag and key
  does where you are now, with your own key settings. It updates as you move around.

---

## Contents

1. [Getting started](#1-getting-started)
2. [The window](#2-the-window)
3. [Scenes and objects](#3-scenes-and-objects)
4. [Building a rig: bones and parts](#4-building-a-rig-bones-and-parts)
5. [Drawing: the paint editor](#5-drawing-the-paint-editor)
6. [Frame-by-frame drawings](#6-frame-by-frame-drawings)
7. [Skins](#7-skins)
8. [Meshes](#8-meshes)
9. [Constraints: IK, look, follow and jiggle](#9-constraints-ik-look-follow-and-jiggle)
10. [Hitboxes and points](#10-hitboxes-and-points)
11. [Colour, blend modes and effects](#11-colour-blend-modes-and-effects)
12. [Animating](#12-animating)
13. [Poses and mirroring](#13-poses-and-mirroring)
14. [Events](#14-events)
15. [Sound](#15-sound)
16. [The mixer: tracks and fades](#16-the-mixer-tracks-and-fades)
17. [The scene timeline](#17-the-scene-timeline)
18. [Importing](#18-importing)
19. [Exporting](#19-exporting)
20. [Using exports in Godot](#20-using-exports-in-godot)
21. [Settings, themes and saving](#21-settings-themes-and-saving)
22. [Keyboard reference](#22-keyboard-reference)
23. [Files and folders](#23-files-and-folders)
24. [Troubleshooting](#24-troubleshooting)

---

## 1. Getting started

### Running it

Harlequin runs on 64-bit Windows 10 or 11.

To build a standalone copy from the source, open a terminal in the folder with `Harlequin.sln`
(you need the .NET 8 SDK) and run:

```
dotnet publish Animator.Viewer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`publish\Animator.Viewer.exe` is then the whole program in one file; rename it if you like.
For a smaller build, use `--self-contained false`; the computer running it then needs the
.NET 8 Desktop Runtime installed.

### A new project

**File → New…** (Ctrl+N) asks what the project is for:

- **Character** is one character or prop and its animations, for games. You start inside it,
  ready to rig and animate. **Start from the demo character** gives you a small rigged example to
  try things on. Without it, you get an empty rig with one bone.
- **Animation** is a whole scene played over time on the scene timeline, for a video or GIF. You
  set the **Stage** (the video size in pixels), the **Length** in frames and the **frames per
  second**. It starts with a blank background object the size of the stage.

Both kinds are the same file type, `.hqnap` (a Harlequin animation project). The difference is
only where you start, and you can add objects or a scene timeline to either later.

You can also start from art you already have; see [Importing](#18-importing).

### Words used in this manual

| Word | Meaning |
|---|---|
| **Object** | One thing in the scene: a character, a prop, a background. Each has its own rig, drawings and animations. |
| **Bone** | A line with a joint at one end. Bones hang from each other in a tree; moving a bone moves everything below it. |
| **Part** | A piece of the object that's drawn, such as an arm or a hat. It sits on a bone and shows one **drawing** at a time. |
| **Drawing** | A picture shown by a part. A part can have several, to swap between (frame by frame, or skins). |
| **Layer** | A layer inside a drawing, in the paint editor, as in Photoshop. Also the layers of an imported PSD. |
| **Colour layer** | A layer that changes colours instead of holding pixels: on a drawing, or on the whole object. |
| **Rest pose** | How the object is when nothing animates it, which is what you edit in **Build**. |
| **Key** | A value at a frame on the timeline. Between keys, values blend. |
| **Pick** | To select bones and parts, in the viewport or the rig panel. |

---

## 2. The window

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ File  Edit  View  Animation  Help                                           │
├────────────┬──────────────────────────────────────────────┬─────────────────┤
│ Objects    │            [ Build | Animate ]               │ Properties      │
│ and rig    │                                              │ of what's       │
│ panel      │                 viewport                     │ picked          │
│            │                                              │                 │
├────────────┴──────────────────────────────────────────────┴─────────────────┤
│ ⏮ ◀ ▶ ▶|  Keys ▾ │ animation, length, loop, auto key, key, sound, mixer…    │
│ timeline                                                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│ status bar                                          Keyboard and mouse (F1) │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Left panel.** At scene level it lists the objects in the scene. Inside an object it's the
  rig panel, with six views: Bones, Order, Colour, Poses, Skins and Boxes.
- **Viewport.** Where you see and drag things. The tool buttons sit at its left edge, and the
  Build / Animate switch at its top.
- **Properties** (right). The numbers and settings of the picked bone or part, as fields you can
  type in or drag (see [Number fields](#number-fields)). This panel is hidden at scene level.
- **Timeline** (bottom). The current animation's keys, or the scene's clips. It starts with the
  play buttons and **Keys ▾**, then the animation's settings. It's hidden while you're in Build.
- **Status bar.** The current mode, the object, what's picked, and the last edit.

Drag the borders between panels to resize them. Harlequin remembers the panel sizes, the window's
size and place, and the rig panel's last view for next time.

### Build and Animate

Inside an object you're always in one of two workspaces. Switch with the button over the
viewport or **K**.

- **Build** (blue frame round the viewport) is for making the object itself: bones, parts,
  drawings, skins, meshes, hitboxes. Everything is at its rest pose, there's no timeline, and
  nothing plays.
- **Animate** (red frame) is for animating it: the timeline, keys and poses. The rig itself can't
  change here, so the buttons that build it are put away.

A typical loop: draw a part in Build, switch to Animate, animate it, switch back, add the next
part.

### Menus

- **File**: New, Open, Open recent (your last 10 projects), Reopen (back to the last save),
  Save, Save As, Import, Export, Settings, Exit.
- **Edit**: Undo, Redo, History, picking everything, selecting the keys of what's picked, and
  hiding and locking.
- **View**: rulers, grid, guides and snapping, pictures on or off, pixel filtering, onion skin,
  hitboxes, and resetting the view.
- **Animation**: Build / Animate, play, key the pose, auto key, curves, the mixer, the sound map,
  and importing and exporting events.
- **Help**: Keyboard and mouse (F1), the engine format, this manual, and About.

### The viewport

Out of the box, the left mouse button **picks**, the right button **moves things**, and the
middle button **moves the view**. You can reassign all three in
[Settings → Mouse](#mouse).

| Do this | To |
|---|---|
| Click | Pick a bone or part. Ctrl+click toggles it in the selection; Shift+click adds it. |
| Drag on empty space | Pick everything in a box. Shift adds to the selection; Ctrl lets go of what's in the box. |
| Right-drag a joint | Move it. |
| Right-drag a bone | Turn it. Shift turns in 15° steps. |
| Right-drag a part | Move it. Drag its corners to scale it, its round handle to turn it, and its pivot (in Build) to move the pivot. |
| Right-drag anything picked | Move everything that's picked. |
| Ctrl+right-drag | Draw a new bone (Build). |
| Alt+right-click a part | Put it on the selected bone (Build). |
| Double-click a part | Open it in the paint editor. |
| Middle-drag, or right-drag on empty space | Move the view. |
| Wheel | Zoom where the mouse is. |
| F | Reset the view. |

**The tool buttons** at the viewport's left change what the pick button does when you drag:

| Tool | Keys | What dragging does |
|---|---|---|
| **Select** | 1 or Q | Picks (a box over several). The move button still moves things. |
| **Move** | 2 or W | Moves the bone or part you drag. On empty space, draws a box. |
| **Rotate** | 3 | Turns the bone or part you drag. |
| **Paint** | 4 | One click on a part opens it in the paint editor. |

**View aids** (View menu):

- **Rulers** run along the top and left. Drag a guide out of a ruler; drag it back onto the
  ruler to remove it.
- **Grid**. View → Grid size sets its spacing for the project.
- **Snapping** to the grid or to guides. Joints, bone ends, part pivots or box corners, and
  objects all snap.
- **Lock the guides** so you can't move them by accident; **Clear the guides** removes them all.

**Pictures** (G) hides the art so you can see just the bones. **Pixel filtering** (P) switches
between smooth and crisp pixels when zoomed in.

### Number fields

Every number in the properties panel, a clip's fields and the scene bar can be **dragged left or
right** to change it. Hold Shift to go ten times faster, or Ctrl for ten times finer steps. A
plain click lets you type in it instead. A whole drag is one undo step.

### Undo and History

**Undo** (Ctrl+Z) and **Redo** (Ctrl+Y or Ctrl+Shift+Z) cover everything, including painting.

If the step to undo was made somewhere else (another animation, the other workspace, the paint
editor), the first undo takes you back there, and the next one undoes it. That way nothing is
undone out of sight.

**Edit → History…** (Ctrl+Alt+Z) lists every step. Click one to jump straight back (or forward)
to just after it.

### Hiding and locking

These only affect editing; exports always show everything.

- **Hide** (H) hides the picked bones and parts while you work. A hidden bone hides everything on
  it and below it. Shift+H hides everything else; Alt+H shows everything again.
- **Lock** (L) stops the picked from being clicked or dragged in the viewport. You can still pick
  them in the rig panel and type their numbers. Shift+L locks everything else; Alt+L unlocks
  everything.
- In the rig panel, each row's **●** hides or shows it, and its padlock locks or unlocks it.

---

## 3. Scenes and objects

A project is a **scene** holding one or more **objects**. At scene level, the left panel lists
the objects; the top of the list is in front.

- **+ New ▾** adds an object:
  - a rigged object (with bones),
  - a drawn object (blank, or from an image),
  - an object from a Photoshop file, from an Animate project, or from a sprite sheet.
- **Copy** makes a copy with its own rig, animations and drawings. **Delete** removes the object
  (a scene always keeps at least one).
- **▲ / ▼** moves the object in front of or behind others.
- **Edit this object ▸**, a double-click or Enter opens the object. **Esc**, or the back
  button, returns to the scene.

Each object's settings:

- **Name**.
- **X / Y, Rotation, Scale**: where it is in the scene. A negative scale mirrors it.
- **Visible**: whether it's drawn at scene level.
- **Locked**: can't be clicked or dragged in the viewport, which is handy for backgrounds.
- **Export**: whether scene exports include it.
- **Onion skin**: whether it shows onion ghosts at scene level.
- **Preview**: the animation it plays at scene level, and while you edit other objects.
- **Attached to**: carries the object on another object's bone, such as a sword in a hand or a
  hat on a head. It then moves, turns and scales with that bone, and stays where it is at the
  moment you attach or free it.

In the viewport at scene level, click an object to select it and drag it to move it. Drag a
corner to scale it (Shift keeps its proportions) and the round handle to turn it (Shift turns in
15° steps). Double-click an object to open it.

### Rigged and drawn objects

A **rigged** object has bones; its parts hang on them.

A **drawn** object has no bones, just parts you paint, frame by frame if you like. It's good for
effects and simple props. **Give it bones ▾** turns a drawn object into a rigged one, either with
just a root bone you draw the rest from, or with bones suggested from where its parts overlap.

---

## 4. Building a rig: bones and parts

Everything in this section happens in **Build**.

### Bones

- **+ Bone** adds a child of the selected bone (or a root bone if nothing is selected). Faster:
  **Ctrl+right-drag** in the viewport draws a bone from where you press to where you let go.
- **Snap to ends**: a new bone started near the selected bone's end, or a joint dragged near its
  parent's end, snaps exactly onto that end and stays there. In the bone's properties this shows
  as **On parent's end**. Pull it further away to free it.
- **Parts stay put when bones move**: while this is on, dragging a joint or turning a bone leaves
  every part (and every other joint) where it is. Use it to fit bones to finished art. Turn it
  off when you want parts to follow their bones.
- In the **Bones** view, drag a bone onto another bone to hang it there.

**Bone properties:**

- **Name**, and **Parent**.
- **X / Y**: the joint's place, in the parent's space.
- **Rot°**: its angle.
- **Len**: its length.
- **Scale**.
- **Turn° / Tilt°**: depth rotation, which fakes turning the bone in 3D.
  - **Turn** spins it round its own length: the parts on it narrow, then mirror across the bone
    at 180°.
  - **Tilt** tips it towards or away from you: it foreshortens, then mirrors along itself at
    180°.
- **Inherit rotation / Inherit scale**: untick these so the bone ignores its parent's angle or
  size. Use it for a hat that stays level, for example.
- **The square** sets the bone's line colour in the viewport (for every picked bone at once).

### Parts

- **+ Part ▾** adds a part to the selected bone: a blank one to paint, or one from an image
  (PNG, JPG, BMP or WebP).
- **Bind ▾** puts parts on bones while keeping them where they are: every part onto its nearest
  bone, or by hand.
- To move one part to another bone, drag it onto the bone in the Bones view, change its **Bone**
  in its properties, or Alt+right-click it in the viewport.
- **Delete ▾** deletes the selected part. For a bone, you choose whether what's on it moves up to
  the bone above, or goes with it.

**Part properties:**

- **Name** and **Bone**.
- **X / Y, Rotation°, Scale**: the drawing's placement on its bone.
- **Pivot (px)**: the pixel that rotation and scaling turn around. Changing it doesn't move the
  picture.
- **Edit pixels**: opens the paint editor.
- **▲ Front / ▼ Back**: draw order (see below).
- **Drawings**: see [Frame-by-frame drawings](#6-frame-by-frame-drawings).
- **Effects**: blend mode, tint and opacity (see [Colour](#11-colour-blend-modes-and-effects)).
- **Mesh**: see [Meshes](#8-meshes).

### Draw order

The **Order** view lists every part in the order it's drawn, with the front one at the top,
whatever bone each part is on.

- Drag a part up or down to change the order.
- **Ctrl+]** and **Ctrl+[** move the picked parts a step forward or back; add Shift to send them
  all the way to the front or back.
- **Order and mask ▾**, over a part's properties, has the same options as right-clicking the
  part in the list.

In Animate, changing the order sets a **draw order key** at the playhead. Use it when a hand
goes behind the body for part of a swing, for example.

### Clipping masks

A part can be a **clipping mask**: the parts drawn just above it only show where it has pixels.
Use it for an eye's pupil inside the eye white, or a mouth inside a head.

Right-click the mask part in the Order view, or use **Order and mask ▾**, to:

- make it clip all the parts above it, or only up to a chosen one,
- choose whether the mask itself shows too, or only cuts the others,
- stop clipping.

Masks are set in Build. In the list, a mask shows ◐, and each clipped part shows "clipped by…".

### Sets

**Sets**, under the rig list, are named groups of bones and parts that you can pick again in one
click. Use them for "left arm", "face" and so on.

- **+ Set** makes a set of what's picked.
- Click a set to pick it again. Ctrl picks or unpicks it; Shift adds it to the selection.
- Right-click a set to rename, change or delete it.

### Picking in the rig panel

- Ctrl+click picks several rows. Shift+click picks every row from the selected one to this one.
- **Ctrl+A** picks everything that's shown and not locked.
- **Only the picked's rows** (in the timeline bar) shows just the picked bones' and parts' rows
  in the timeline.

---

## 5. Drawing: the paint editor

Double-click a part (or use **Edit pixels**, or the Paint tool) to paint it in the paint panel
beside the viewport. **Close** or Esc goes back.

The paint editor's keys only work while the mouse is over the paint canvas.

### Tools

| Tool | Key | Notes |
|---|---|---|
| **Select** | M | Selects by shape: rectangle, ellipse, polygon, polyline, curve or freehand. Shift adds to the selection, Alt subtracts, Shift+Alt intersects. |
| **Wand** | W | Selects by colour, using **Tolerance**. **Contiguous** keeps it to the area connected to where you click; **Sample all layers** looks at the merged image. |
| **Move** | V | Moves the selected pixels, or the whole layer if nothing is selected. |
| **Pencil** | N | Hard pixels. Good for pixel art. |
| **Brush** | B | Soft brush. **Size** sets how big it is; [ and ] change it too. |
| **Mixer** | X | Paints by picking up colour from the canvas. **Wet** sets how much it picks up; **Load** sets how quickly it refills with your colour (0 smudges). |
| **Eraser** | E | Rubs pixels out. |
| **Picker** | I | Picks a colour from the canvas. |
| **Fill** | G | Fills an area, with Tolerance and Contiguous as for the Wand. |
| **Gradient** | D | From the primary colour to the secondary one, or with **To transparent**, to see-through. |
| **Figure** | U | Draws a line, rectangle, ellipse, polygon, polyline or curve: as an outline, filled, or both. |

Shared tricks:

- **Shift+click**, or Shift+drag, draws a straight line from the last point.
- **Alt+click** picks a colour.
- For polygons, polylines and curves: click the points, then double-click or press Enter to
  finish. Backspace takes back the last point; Esc cancels.
- **Ctrl+A** selects all, **Ctrl+D** deselects, **Ctrl+Shift+I** inverts the selection, and
  **Del** deletes the selected pixels.

**Colours.** There's a primary colour (front) and a secondary one (back). Click either to pick a
colour, transparency included. **S** or **⇄** swaps them. Right-click a palette colour to set it
as the secondary. The hex field takes `#RRGGBB` or `#AARRGGBB`.

### Layers

A drawing has layers, like a Photoshop file.

- **+** adds a layer, **−** deletes it, and **▲ / ▼** moves it up or down.
- Each layer has a name and an opacity.
- **+ Colour layer ▾** adds a colour layer: it changes the colours of the layers under it, in this
  drawing only. You can key its numbers in Animate.

**Canvas size…** changes the drawing's size.

**Onion skin** in the paint editor shows the part's previous and next drawings faintly under the
one you're painting.

---

## 6. Frame-by-frame drawings

A part can hold several drawings and swap between them, like traditional frame-by-frame
animation. You can mix this freely with bones.

In the part's properties, under **Drawings**:

- **Drawing** picks the drawing shown. In Animate, picking one keys it at the playhead, so it
  shows from this frame on. In Build, it sets the part's rest drawing. **(hidden)** shows nothing.
- **New (F7)** adds an empty drawing with the same size and layers.
- **Copy (F6)** adds a copy of the current drawing to change a little. This is the usual way to
  make the next frame.
- **Delete** removes the drawing, along with its keys in every animation.
- **Rename** names the current drawing.
- **◂ Shorter (Shift+F5)** and **Longer ▸ (F5)** hold this drawing for one frame less or more;
  the part's later drawings move along with it.

**Onion skin** (O) shows the neighbouring frames faintly: red for before, blue for after. Set
how many frames to show **before** and **after**, and **every** how many frames. Use 2 for
animating on twos.

---

## 7. Skins

Skins are alternative looks for some of the parts. Examples: a cone, a bucket or no hat; armour
that dents as it takes damage; a bandage shown over everything.

- **Skin 0 · Default** is the object as it is by default.
- Each other skin has drawings for some parts. While the skin is on, those parts show the skin's
  drawings; everything else shows its own.

Open the **Skins** view. Tick a skin to turn it on.

**Categories:**

- Skins in the same category are alternatives: only one is on at a time. For example, a "Bucket"
  category holds the bucket and its damaged versions.
- Skins with no category are outfits, and also one at a time.
- An **Overlay** turns nothing off. Any number of overlays can show at once, over the other skins
  (dirt, a glow, a bandage).

When two skins that are on both have a drawing for the same part, the overlay wins, then the one
later in the list. **▲ / ▼** reorders the list.

**Making skins (+ Skin ▾):**

- the picked parts as one skin,
- each picked part as a skin of one category,
- a part's drawings as a category, one skin per drawing,
- a colour layer as a skin,
- an empty skin, to edit.

**Editing a skin.** Click ✎ on a skin's row to edit it. A banner over the viewport says so, and
any change to a part (painting, placement) goes into that skin. The skin gets its own copy of
the drawing, so the default look stays as it was. Skins can come from edits, not only from
separate layers.

In the skin's settings:

- **Add the picked** puts the picked parts' own drawings into the skin; **Take out** sends the
  picked parts back to the object's own drawings.
- **Pick its parts** picks the parts the skin changes.
- **Colour layers** lists the object's colour layers that belong to this skin: they apply only
  while it's on.

**Deleting a skin** either takes its drawings with it, or gives them back to the object.

### Bringing in another object (one zombie's head on another's body)

**Edit → Bring in from another object…** (also **+ Skin ▾ → From another object…**) takes
another object in the same project and brings its animations and drawings into the one that's
open. Use it to build one shared rig for several similar characters: one body, every head as a
skin, every character's animations.

1. Import each character (File → Import… its Animate project), then open the one to keep as the
   shared rig.
2. **Bring in from another object…** and pick the other one. Bones, parts and drawings are
   matched by name. What each object's names start with is set aside, so `zombie_egypt_jaw`
   matches `zombie_pirate_basic_jaw`; a number at the end may differ too (`toe_01` ↔ `toe`).
   Change any match in the lists.
3. **Parts**: tick the ones whose drawings go into the skin. For a head: pick the head bone in the
   rig panel first, then **Tick those on the picked bones**. A part with no match can be **added
   as a new part**, shown only while the skin is on (a flag, a bandage flap).
4. **Skin**: its name and category. Give every head the same category (`Head`), so only one is on
   at a time.
5. **Animations**: tick the ones to bring, and what their names end with here (`walk_egypt`).
   **Poses as they look** works each key out again from this object's rest pose, so poses look
   as they did even where the two rigs rest differently. **Keys as they are** copies the changes
   from the rest pose.

**Many at once: Edit → Bring in from Animate projects…** Pick the head bone (or whichever bones
the parts come from) in the rig panel, then add any number of Animate projects. Each is imported
aside, not into the project. The parts on the picked bones become a skin named after the file
(`zombie_egypt_basic` → `egypt`), in one category, along with what rides on them (hats, hair).
Their animations can come too. **Line them up as they show** places each drawing exactly where it
shows in its own animation, lined up at the start of an animation both have (idle first). That's
the right choice for characters that share their animations.

The new skin is turned on at the end. Click ✎ on it in the Skins view to line its drawings up on
the bones; they come in placed on their bones as they were placed in the other object. Animations
key drawing names, not pictures, so a drawing swap (the head's hurt drawing) shows whichever
head's version is on. Meshes and their deform keys don't come along. The other object stays as it
was: delete it once everything's in.

---

## 8. Meshes

A mesh lets a picture bend instead of staying a flat rectangle. It can bend with its bones (a
sleeve along an arm) or be pushed about by deform keys (a squash, a cape fluttering).

In the part's **Mesh** box:

1. **Make a mesh ▾**: a grid over the picture's visible pixels, from Fine to Coarse or just the
   corners. Finer meshes bend more smoothly but have more points.
2. **Edit points**: drag the mesh's points in the viewport. In Build this moves where they sit on
   the picture; in Animate it sets **deform keys** at the playhead.
3. **Weights ▾**: choose which bones bend the mesh. The usual start is "from the nearest bones".
4. **Paint weights** (Build): pick a **Bone**, then drag over the points to give that bone more
   of them, or Alt-drag to give it less. **Strength** and **Brush** size set how much and how
   far. Red means a point follows that bone fully; blue means not at all.

**Remove it** turns the part back into a flat picture.

---

## 9. Constraints: IK, look, follow and jiggle

All four are set in the bone's properties.

### IK (inverse kinematics)

Use IK to move a hand or foot and have the arm or leg bend to reach it.

- **IK chain**: make this bone the end of a chain. 2 bones makes an arm or leg; 1 makes an aim
  (the bone points at the target).
- **IK target**: the bone it reaches for. Usually that's a free bone you animate.
- **IK on**: turn it off (or press I) and the chain plays its own keys (FK) instead.
- **Flip bend** (B): which way the elbow or knee bends.
- In Animate:
  - **Shift+I** switches between IK and FK at this frame.
  - **T** snaps the target to the hand.
  - **Shift+T** bakes IK into ordinary keys.

### Look at

The bone (an iris, a pupil, a highlight) moves towards another bone, but never outside a shape
round where it rests. The shape turns and scales with its parent, such as the head.

- **Shape**: ellipse or rectangle.
- **Follow**: how strongly it follows. 0.1 is subtle; 1 goes straight to the edge.
- **Range X / Y**: the shape's half-width and half-height.

### Follow another bone

Under **Follow and jiggle**: this bone takes another bone's turn, place and/or size, each by its
own amount. Use it for shoulder pads riding an arm, a gear turning with another, or a prop
copying a hand.

- **Turn / Place / Size** set how much of each it takes.
- **How it moves (local), not where it is**:
  - On: it copies how the other bone moves from its own rest. Good for gears and mirrored limbs.
  - Off: it takes the other bone's actual angle, place and size, keeping the offsets it had.
- **Keep it where it is now** resets those offsets to what they are at this moment.

### Jiggle

**Jiggle (lags behind and springs back)**: the bone trails behind as it moves, swings past and
settles. Use it for hair, tails, ears and antennae.

- **Stiffness**: how hard it springs back. Higher means quicker, smaller swings.
- **Damping**: how fast the swinging dies down.
- **Gravity**: pulls its tip down (or up, if below 0).
- **Mix**: how much of the follow and the jiggle it takes (0 means none).

Jiggle needs time going by, so you see it while playing and in exports, not while scrubbing.

### Detaching, attaching and throwing

In Animate, **Follows** in the bone's properties sets what the bone follows from the current
frame on:

- its parent (normal),
- nothing (detached: it stays where it was and moves on its own),
- or another bone that holds it (a sword handed to the other hand).

**Throw / fall from this frame…** detaches the bone here and works out a thrown or falling
flight as keys: launch speed, spin, gravity, ground height, bounce and friction. Everything
attached to the bone falls with it. Undo removes the whole thing.

---

## 10. Hitboxes and points

Open the **Boxes** view to add data for the game. This view shows the boxes and points in the
viewport; elsewhere, **J** shows or hides them.

- **+ Hitbox ▾**: a box or circle on the selected bone. If a part is selected, it goes on the
  part's bone, around the part. **Fit round the selected part** fits it to the part's visible
  pixels.
- **Kind**: what it's for, as the game knows it: `hurt` (can be hit), `hit` (hits others),
  `block`, `trigger`, or any word.
- **On at rest**: whether it's on when no animation keys it. For an attack hitbox, leave it off,
  then key it on where the attack strikes with **On from here** and **Off from here**.
- **+ Point**: a point at the tip of the selected bone, with a direction. Use it to mark where
  the game spawns or holds things (a muzzle, a hand).

Drag boxes and points in the viewport to place them. Sprite sheet and engine exports carry them
frame by frame.

---

## 11. Colour, blend modes and effects

### On a part (Effects, in its properties)

- **Blend**:
  - Normal covers what's behind.
  - Add glows (sparks, flashes, magic).
  - Multiply darkens (shadows).
  - Screen lightens softly.
- **Colour**: a tint that multiplies the drawing's colours. White means as drawn. Click the
  swatch to pick a colour. In Animate, this sets a colour key.
- **%**: opacity. 100 is solid, 0 is invisible. In Animate, typing sets a key, for fades.

### On the whole object (the Colour view)

**Colour layers** change the colours of every part except the ones you leave out, such as the
eyes of a body that's been turned green. They apply first to last.

- **+ Colour layer ▾** adds one:
  - **Hue / saturation**: round the colour wheel, richer or greyer, lighter or darker; or, with
    Colorize, everything one hue.
  - **Brightness / contrast**.
  - **Tint**: towards a colour, either as a coloured filter that keeps the light, or as a flash.
  - **Colour balance**: shadows, midtones and highlights each pushed towards a colour.
- **Leave out** lists the parts it doesn't change.
- **▲ / ▼** sets the order.
- Its numbers can be keyed in Animate.
- A colour layer can belong to a skin, so it applies only while that skin is on.

Drawings can have their own colour layers as well (see [Layers](#layers)). That's also where
**Replace colour** lives: one colour, within a tolerance, turned into another.

**Exports**: tick **Bake colour correction layers** to have pictures come out coloured exactly
as the editor shows them.

---

## 12. Animating

Switch to **Animate** (K). The timeline appears at the bottom.

### Animations

The timeline bar shows the current **Animation**:

- **+ New**, **Duplicate** and **Delete**.
- **Name**, **Length (frames)** and **Loop**.
- **FPS**: frames per second for the whole project. Timing is kept in seconds, so changing the
  frame rate doesn't change how long anything takes.

### Posing and keys

- **Auto key** on (the default): any change you make to the pose sets keys at the playhead.
- **Auto key** off (**Shift+K**): changes only pose the rig, and an empty orange key marks where.
  **Key** (S) keys the pose. Moving to another frame drops whatever wasn't keyed.
- **S** with auto key on keys the picked bones' and parts' whole pose here.

### Playing

| Key | Does |
|---|---|
| Space | Play / pause |
| R | Back to the first frame |
| ← / → | A frame back / on |
| Shift+← / Shift+→ | To the key before / after the playhead |

The same are the buttons at the start of the timeline bar.

- **Speed** changes how fast it plays (not the animation itself).
- **Playback ▾** has three options: back and forth (ping-pong), just the picked frames over and
  over, and whether stopping goes back to where play started.

### The timeline (dopesheet)

| Do this | To |
|---|---|
| Click a key | Select it. Shift+click adds to the selection; Ctrl+drag draws a box of keys. Alt+click selects every key at that frame. |
| Drag keys | Move them. |
| Alt+← / Alt+→ | Move the selected keys a frame earlier or later. |
| Ctrl+C / Ctrl+V | Copy the selected keys, and paste them at the playhead. |
| Del | Delete the selected keys. |
| Ctrl+Shift+A | Select every key of the picked bones and parts. Then dragging those in the viewport moves all their keys alike. |
| Alt+= / Alt+− | Insert a frame after the playhead, or remove the frame at it. |
| Drag across empty frames | Pick a range of frames. Play then loops them, and right-click → **Copy into a new animation sequence…** makes a new animation from them. |
| Wheel / Shift+wheel / Ctrl+wheel | Scroll / scroll in time / zoom. |

**Right-click** a key for its curve and timing options:

- the curve (see below),
- a frame earlier or later,
- **Stretch or squeeze…**: further apart or closer together, as a percentage,
- **Reverse**,
- copy and delete.

Right-click an empty spot for frame options:

- select every key at this frame, or from it on,
- insert or remove frames,
- add an event,
- select the keys of what's picked.

**Keys ▾** in the timeline bar opens the same menus without right-clicking.

### Curves

Each key has a curve for the stretch to the next key:

- **Linear**: steady.
- **Smooth**: eases in and out.
- **Stepped**: holds the value, then jumps.
- **Bézier**: your own easing, with presets (ease in, ease out, overshoot, anticipate).

**Curves** (Ctrl+G) opens the graph editor, which shows keys' numbers as lines over time.

- Click a key, and Shift+click to add more.
- Drag a key to move it; Shift keeps it to one direction.
- Drag a key's square handles to shape the Bézier easing.
- Wheel zooms, middle-drag moves, double-click fits.
- Click a name in the list to hide its curve.

### Frame-by-frame and other keyed things

Several other things become keys in Animate:

- which drawing a part shows (see [Frame-by-frame drawings](#6-frame-by-frame-drawings)),
- the draw order,
- part placement, tint and opacity,
- colour layers,
- IK on/off,
- hitboxes on/off,
- mesh deforms,
- detach / attach.

---

## 13. Poses and mirroring

Open the **Poses** view.

- **+ Keep this pose** saves the picked bones and parts as they're posed now, by name. With
  nothing picked, it saves the whole rig. **Update** saves the selected pose again, from the
  current pose.
- **Put it on**, or a double-click, puts the pose back on the rig.
  - **Amount** puts on part of it: 50% is halfway from the current pose.
  - **Only on the picked** limits it to the picked bones and parts.
  - **Mirrored** puts it on the other side.
  - In Animate, this sets keys; in Build, it changes the rest pose.
- **Mirror the pose** (Ctrl+M) swaps left and right on the picked bones (or on everything).
- **Copy pose / Paste pose / Paste mirrored** are Ctrl+Shift+C, Ctrl+Shift+V and Ctrl+Alt+V.
- **Symmetrize the rig** (Build) makes the other side of the picked bones, or of all bones, the
  mirror image at rest. It creates the other side's bones if they don't exist yet.

Harlequin finds a bone's other side from names that say a side: `arm_L` / `arm_R`,
`Left Arm` / `Right Arm`, and so on.

---

## 14. Events

Events are named moments the game reacts to: `footstep`, `shoot`, `arm_off`, or a sound to play.
They show as flags in the timeline's top row.

- **E** adds an event at the playhead. You can also right-click the timeline → **Add event at
  frame…**.
- Double-click a flag to edit it, and drag it to move it.
- Each event has:
  - **Name**: what the game sees.
  - **Data**: optional extra information, such as which bone.
  - **Volume %** and **Pitch**: how its sound plays.
  - **Sounds like…**: what this name plays in the editor (see [Sound](#15-sound)).

Events can also go to and from files, outside the project: Animation → **Export events…** and
**Import events…** (both are also under **Sound ▾**).

- An **events file** (`name.events.json`) lists each animation's events by frame and time.
- Importing merges the file's events into the animations with the same names. You can replace
  what's there or add to it.

The standalone **PvZ2 Sound Events** tool writes these files from PvZ2 animations; it has its
own README.

---

## 15. Sound

**Sound ▾**, in the timeline bar, has these options:

- **Sound track…**: a sound that plays along this animation, such as a voice line or music, to
  time the animation to. WAV files show their waveform under the keys.
  - **Starts at frame…** offsets it.
  - **Volume…** sets how loud it plays.
  - **Remove the sound track** takes it off.
- **Sound map…**: what each event name sounds like in the editor. It's one file per name.
  - Names the animations use that have no sound are listed too, shown as silent.
  - Paths are kept relative to the project's folder when possible.
- **Import / Export events…** (see [Events](#14-events)).
- **Import a sound map… / Export the sound map…** (`name.sounds.json`).
- **Mute**.

Sounds can be WAV, MP3, WMA, M4A or AAC. They play while the animation plays, in the editor
only; exports don't carry audio, except as data in the engine format.

---

## 16. The mixer: tracks and fades

**Mixer…** (timeline bar, or the Animation menu) works with the open object's animations
together. It has two parts.

**Tracks** lay animations over each other, such as walking legs on track 1 and shooting arms on
track 2.

- Each track changes only what it has keys for.
- **Weight** sets how much of a track shows; **Speed** sets how fast it plays.
- **▶ Play the tracks** shows the mix in the viewport. Closing the mixer goes back to the normal
  timeline.

**Fades** set how one animation fades into another.

- Pick two animations and a time in seconds, then **▶ Try the fade**: the first animation plays
  once, then fades into the second.
- **Keep for this pair** saves the time. The game uses it, and so does a scene clip's
  **As mixed**.
- **Default fade** covers every pair you haven't saved a time for.

---

## 17. The scene timeline

At scene level, the timeline shows one row per object, holding **clips** of that object's
animations.

- Add a clip in either of two ways:
  - Right-click an object's row → **Add clip at frame…**.
  - Use **+ Clip at playhead ▾** in the object's settings.
- Drag a clip to move it, and drag its edges to change its length or trim its start.
- Double-click a clip, or use **Edit in place ▸**, to open the object at that clip's animation
  and frame, with the rest of the scene playing along.
- An object with no clips loops its **Preview** animation.

Clip settings:

- **Animation**: which of the object's animations it plays.
- **Start / Length**: when it starts and how long it lasts, in frames.
  - If it's longer than its animation, **Loop** plays it again; without Loop, it holds the last
    frame.
- **From frame**: where in the animation the clip starts; the frames before it are cut off.
- **Fade in**: frames to fade from the clip before it, instead of cutting. **As mixed** uses the
  mixer's time for that pair.
- **Delete clip** (Del).

The scene bar has these settings:

- **Length**, and **Fit to clips** to end the scene where its last clip ends.
- **Loop**, to play the scene again from the start when it ends.
- **Stage**: the picture size the scene is made for (0 × 0 means none).

Right-click the scene timeline → **End the scene at frame…** sets the length from there.

---

## 18. Importing

**File → Import…** (Ctrl+I) takes the file, and each kind opens its own window with a live
preview. **+ New ▾** at scene level offers the same imports.

### Photoshop (.psd, .psb)

Each ticked layer comes in as a part, in the file's order.

- **Tick them all** or **Only the visible ones**.
- **Suggest a rig: bones where the layers overlap**:
  - Each layer gets a bone that turns where it overlaps the layer it hangs from.
  - Details lying on another layer (eyes, a belt) ride on that layer's bone instead.
  - Choose the **Root** bone, and change any layer's role or what it **Hangs from**. The preview
    shows the result.
  - **Suggest the roles again** forgets your changes and starts over.
- **Size**: bring big files in smaller (50% means half size).

Without a rig, the file comes in as a drawn object.

### Animate (.fla, .xfl, the DOMDocument.xml in an XFL folder, or a zipped XFL folder)

- **Timeline**: the scene, or a symbol with its own animation.
- **An animation for each frame label**: each label (idle, walk, attack…) starts an animation
  that runs to the next label.
- **Parts follow their drawings from layer to layer**: for projects where a part moves between
  layers, which is how converters write PvZ2 animations. It starts ticked when the project looks
  like that.
- **Each part** has three ticks:
  - **In**: bring it in.
  - **Rig**: use it to work out the rig.
  - **FX**: make it an effect. It's left out of the rig, moves on its own and is hidden at rest.
    Use this for crumbs, splashes and sparks; parts that only show for a moment start out as
    effects.
- **Suggest a rig from the animation**:
  - Parts that turn around a shared point get bones jointed there, keyed to move as they did.
  - Pick the **Rest pose** frame; **Use the frame shown** takes the one in the preview.
  - Untick animations where parts come apart (a head falling off): they'd hide the joints the
    other animations show.
- **Show left-out parts**: untick it to see only what will come in.
- **Size**: as for Photoshop.

### Sprite sheets (.png, .webp, .gif, .bmp, .jpg)

The import works in three steps.

**1. Cut the sheet into pieces**, in one of four ways:

- **A grid**: frame size, the first frame's position and the gaps; or "a grid of N × M".
- **Find the separate sprites**, on see-through sheets: bits closer than a set distance join
  into one sprite, and specks below a set size are skipped.
- **Draw them by hand**: drag on the sheet. Ctrl+drag picks pieces, and Del removes them.
- **As its JSON file says**, if there's an Aseprite or TexturePacker JSON next to the sheet.

A flat background colour (magenta, green…) can be made see-through.

**2. Make animations.**

- Pick pieces, then **Make an animation**, or use **An animation per row**.
- Set each animation's name, **Each frame shows** (in frames), **Line up by** (where frames of
  different sizes line up; bottom middle keeps feet on the ground) and **Loop**.
- **Leave out / put back** skips pieces you don't want.

**3. Bring them in**, as new drawn objects or all in one object. Pieces in no animation can come
in as stills. **Show pixels sharp** is for pixel art.

---

## 19. Exporting

**File → Export…** (Ctrl+E). First choose **What**:

- **The scene, over its timeline**: for a video, a GIF or pictures. **Area** is the stage or
  everything that shows.
- **One object's animations**: for a game. Tick which animations to export.

**Formats:**

| Format | You get |
|---|---|
| **PNG pictures** | `name_0000.png`, `name_0001.png`, … with see-through backgrounds. Video editors and game engines take these. |
| **GIF** | An animated GIF. **Loop** makes it repeat; **Dither** smooths gradients but makes them grainier, so leave it off for pixel art. |
| **Sprite sheet** | One PNG with every frame, plus a JSON next to it in Aseprite / TexturePacker layout. **A row per animation**, columns and gap are set here. The JSON includes each frame's hitboxes and points. |
| **Video** | MP4, made with FFmpeg, a free program from ffmpeg.org. Point Harlequin to `ffmpeg.exe`, or leave it empty if it's installed for the whole system. |
| **Engine** | `name.hqrig.json` plus atlas pages: the whole rig and every animation, for a game runtime to play (see below). |

Common options:

- **Frames from / to / every**: every 2 means half the frame rate and half the files.
- **Scale**: 1 is one pixel per unit; 2 is twice as big.
- **Pixel art (crisp, no smoothing)**.
- **Bake colour correction layers**.
- **Events file**: for one object's animations, this also writes `name.events.json`.
- **Background**: see-through, or a colour.

### The engine format

The engine export writes one object exactly as a game would play it: bones, parts, skins,
meshes and weights, masks, every constraint, colour layers, every animation and curve, events,
fades from the mixer, hitboxes and points. Pictures go into atlas pages.

- **Pages up to**: the biggest an atlas page may be. More pages are made if the pictures don't
  fit on one.
- **Gap**: empty pixels around each picture, so neighbours don't bleed in when the engine
  smooths them.
- **Sizes a power of two**: pages of 256, 512, 1024… pixels, for engines and older hardware
  that need it.
- **Trim**: leaves out each picture's see-through edges. The file records where they were, so
  nothing moves. Pictures under a mesh are never trimmed.

`docs/engine-format.md` (Help → The engine format) describes the file field by field, for
anyone writing a runtime for another engine.

---

## 20. Using exports in Godot

The repository's `godot/` folder has a Godot 4.3+ plugin that plays engine exports.

1. Copy `godot/addons/harlequin` into your game's `addons/` folder, and turn the plugin on in
   Project Settings → Plugins.
2. Put the `.hqrig.json`, its atlas pages, and any sounds anywhere in the project.
3. Add a **HarlequinRig** node, pick the rig file, and pick an animation. It previews in the
   editor.

From code:

- `play("walk")` fades in using your mixer times.
- `set_track(1, "shoot")` layers a second animation on top.
- `show_skin("bucket")` turns a skin on, following your skin rules.
- The `event` signal fires on every event.
- `get_point_transform("hand")` and `get_hitboxes()` read points and hitboxes.

`godot/README.md` has the full list.

---

## 21. Settings, themes and saving

**File → Settings…** (Ctrl+,). **Everything back to the defaults** resets the keys, mouse
buttons and saving choices. Nothing changes until you press OK.

### Keyboard

Every command and paint tool, with its keys.

- **Find** searches by name, group or key.
- Pick a command, click in **New key** and press the key you want, with any Ctrl, Shift or Alt.
  Then:
  - **Add** gives the command another key,
  - **Set** replaces its keys with this one,
  - **No key** removes its keys,
  - **Its default** puts its original key back.
- Paint keys work while the mouse is over the paint canvas; the others work everywhere else.
  Giving a key to one command takes it away from any other command that had it.

### Mouse

Choose which button **picks**, which **moves** things, and which moves the **view**. Each needs a
different button.

### Appearance

- **Theme**: Dark, Light or High contrast, or your own. It shows straight away; Cancel goes back.
- **Size**: 90% to 200%. It scales the panels, menus and dialogs; the viewport and timeline stay
  as they are.
- **Your own themes** are JSON files in the themes folder (**Open the themes folder**). Each
  gives a name, the built-in theme it starts from, and the colours it changes, as `#RRGGBB` or
  `#AARRGGBB`.
  - The folder has an `example.json` that lists every colour and what it's for. Copy it, change
    it, then press **Look again**.
  - If a theme file can't be read, the dialog says why.

### Saving

- **Autosave**: while you have unsaved work, a copy is kept aside every so many minutes, in the
  background. If Harlequin closes any other way than normally (a crash, or the computer turning
  off), the next start offers the copy back.
- **Backups**: saving over a project first keeps the old version in a `Backups` folder next to
  the project, named with the time it was saved. Only the newest few are kept.
- **Open the folder of autosaved copies**.

### Files

- **Open recent**: the last 10 projects you opened or saved. A file that has gone missing or
  can't be read is taken off the list, with a message saying so.
- **Reopen**: reloads the project as it was last saved. It asks first, because undo can't bring
  back the changes it throws away.

---

## 22. Keyboard reference

These are the defaults. Change any of them in Settings → Keyboard; F1 always shows the current
ones.

### File and edit

| Keys | Command |
|---|---|
| Ctrl+N | New project |
| Ctrl+O | Open |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+I / Ctrl+E | Import / Export |
| Ctrl+, | Settings |
| Ctrl+Z | Undo |
| Ctrl+Y, Ctrl+Shift+Z | Redo |
| Ctrl+Alt+Z | History |
| Ctrl+C / Ctrl+V | Copy keys / paste keys at the playhead |
| Del | Delete: the selected keys, the picked one's key here, or a clip or object at scene level |
| Esc | Back: close the paint editor, let go of picked frames, leave the object |
| F1 | Keyboard and mouse help |
| (no key) | Reopen |

### Picking, hiding, locking

| Keys | Command |
|---|---|
| Ctrl+A | Pick every bone and part shown |
| Ctrl+Shift+A | Select every key of the picked |
| H / Shift+H / Alt+H | Hide the picked / hide everything else / show everything |
| L / Shift+L / Alt+L | Lock the picked / lock everything else / unlock everything |

### Playing and animating

| Keys | Command |
|---|---|
| Space | Play / pause |
| R | Back to the first frame |
| ← / → | A frame back / on |
| Shift+← / Shift+→ | Previous / next key |
| K | Build / Animate |
| Shift+K | Auto key on / off |
| S | Key the pose |
| E | Add an event at the playhead |
| Ctrl+G | Curves / dopesheet |
| Alt+← / Alt+→ | Selected keys a frame earlier / later |
| Alt+= / Alt+− | Insert / remove a frame |
| (no key) | Reverse the selected keys |
| Ctrl+] / Ctrl+[ | Picked parts a step to the front / back |
| Ctrl+Shift+] / Ctrl+Shift+[ | Picked parts to the very front / back |

### Poses and IK

| Keys | Command |
|---|---|
| Ctrl+M | Mirror the pose |
| Ctrl+Shift+C / Ctrl+Shift+V | Copy / paste the pose |
| Ctrl+Alt+V | Paste the pose mirrored |
| I | IK / FK |
| Shift+I | Switch IK / FK at this frame |
| T | Snap the IK target to the hand |
| Shift+T | Bake IK into FK |
| B | Flip the elbow |

### Frame by frame

| Keys | Command |
|---|---|
| F7 | New drawing |
| F6 | Copy of the drawing |
| F5 / Shift+F5 | Drawing a frame longer / shorter |

### View

| Keys | Command |
|---|---|
| F | Reset the view |
| G | Pictures on / off |
| P | Pixel filtering on / off |
| O | Onion skin on / off |
| J | Hitboxes and points on / off |
| Ctrl+R | Rulers |
| Ctrl+' | Grid |
| Ctrl+; | Guides |
| Ctrl+Shift+' | Snap to the grid |
| Ctrl+Shift+; | Snap to the guides |
| Ctrl+Alt+; | Lock the guides |
| Enter | Open the selected object (scene level) |
| 1 or Q / 2 or W / 3 / 4 | Select / Move / Rotate / Paint tool |

### Paint editor (mouse over the canvas)

| Keys | Command |
|---|---|
| M, W, V | Select, Wand, Move |
| N, B, X, E | Pencil, Brush, Mixer, Eraser |
| I, G, D, U | Picker, Fill, Gradient, Figure |
| S | Swap the colours |
| [ / ] | Smaller / bigger brush |
| Ctrl+A / Ctrl+D / Ctrl+Shift+I | Select all / deselect / invert |
| Del | Delete the selected pixels |
| Enter / Backspace / Esc | Finish the shape / take back its last point / cancel |

---

## 23. Files and folders

| What | Where |
|---|---|
| Projects | `.hqnap` files, wherever you save them |
| Backups of saved-over projects | a `Backups` folder next to the project |
| Settings (keys, mouse, theme, layout, recent files…) | `%AppData%\Harlequin\settings.json` |
| Your themes | `%AppData%\Harlequin\themes\` |
| Autosaved copies of unsaved work | `%LocalAppData%\Harlequin\Recovery\` |
| Events files | `name.events.json` |
| Sound maps | `name.sounds.json` |
| Engine exports | `name.hqrig.json` + `name.atlas.png` (`name.atlas2.png`, …) |
| Sprite sheets | `name.png` + `name.json` |

To move Harlequin to another computer with all your preferences, copy `settings.json` and the
`themes` folder.

Batch work (importing many files, bringing every variant's head in as a skin, rendering a sheet
of skins, exporting) can also be done without the window, with the command-line `harlequin` from
the Releases page, on Linux or Windows: see [cli.md](cli.md).

---

## 24. Troubleshooting

**A button I expected isn't there.**
Check the workspace. Rig-building buttons (+ Bone, + Part, Bind, Delete, meshes, masks,
symmetrize) only show in **Build**; the timeline, keys and poses only in **Animate**. Press K to
switch.

**Dragging moves the view instead of the bone.**
You probably dragged on empty space with the move button. Grab the joint or the bone's line
itself, or check Settings → Mouse to see which button does what.

**I can't click a part or bone in the viewport.**
It may be locked (the padlock in the rig panel, or Alt+L to unlock everything) or hidden (Alt+H
shows everything). In a mesh's Edit points mode, clicks go to the mesh's points.

**My changes vanish when I move to another frame.**
Auto key is off (Shift+K toggles it). Changes you don't key are dropped when the playhead moves.
Press S to key them first, or turn auto key back on.

**Undo jumped somewhere instead of undoing.**
The step was made somewhere else: another animation, the other workspace, or the paint editor.
The first undo takes you there; press it again to undo the step itself.

**Jiggle doesn't move.**
It needs time going by. Press play; scrubbing doesn't move it.

**The IK arm points the wrong way.**
Press B (Flip bend). If the chain has a detached or held bone, IK lets go on purpose.

**A part shows the wrong drawing.**
A skin that's on may have its own drawing for that part. Check the Skins view, where overlays
win, then the skins later in the list. In Animate, a drawing key may also be setting it.

**Imported events landed on nothing.**
Events are matched to animations by name. Compare the events file's animation names with yours;
Animate imports name animations after their frame labels.

**A sound doesn't play.**
Check the sound map (Sound ▾ → Sound map…). The event's name needs a file, the file has to still
be where the map says, and **Mute** must be off.

**Video export fails.**
Harlequin needs FFmpeg. Point the Export window to `ffmpeg.exe`.

**A theme of mine doesn't show up.**
Press **Look again** in Settings → Appearance. If the file has a mistake, the dialog says what's
wrong with it.

**Harlequin crashed.**
Start it again: it offers the autosaved copy of what you hadn't saved. If you also saved over a
good version by mistake, the older versions are in the `Backups` folder next to the project.

**Something else.**
Press F1. It explains what every click and key does where you are right now.
