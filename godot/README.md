# Harlequin for Godot

Plays Harlequin's **engine export** (`name.hqrig.json` + `name.atlas.png` pages) in Godot 4.3 or
later. It handles bones, parts, skins, meshes, clipping masks, all four constraints, jiggle, colour
layers, blend modes, draw-order keys, events, sounds, hitboxes and points, all worked out the way the
editor does it.

This folder is a small Godot project:

- `addons/harlequin/` is the plugin. It's the part you copy into your game.
- `demo/` holds a sample rig and a scene to try it in.
- `tests/` holds the plugin's tests.

## Installing

1. Copy `addons/harlequin` into your project's `addons/` folder.
2. Turn it on in **Project → Project Settings → Plugins**. The node also works without this step.
3. In Harlequin, use **File → Export… → Engine**. Put the `.hqrig.json`, its atlas page(s) and any
   sounds anywhere inside your project.

## Using it

Add a **HarlequinRig** node, pick the `.hqrig.json` for **Rig File**, and choose an **Animation**.
It previews in the editor; untick **Playing** to stop the preview. **Skins** lists the skins that
are on at the start; leave it empty to use the ones the file has on.

The rig is drawn in the node's own space, with Godot's y down. Move, scale (a negative x scale flips
it) and rotate the node as usual.

```gdscript
@onready var zombie: HarlequinRig = $Zombie

func _ready():
    zombie.event.connect(_on_event)
    zombie.animation_finished.connect(func(anim, track): if anim == "die": queue_free())
    zombie.play("walk")

func eat():
    zombie.play("eat")            # fades over the time the rig's mixer gives for walk → eat
    zombie.play("eat", 0.0)       # or at once, or over any number of seconds

func _on_event(event_name, data, volume, pitch):
    if event_name == "footstep":
        $Steps.play()
```

| Method | What it does |
|---|---|
| `play(name, fade := -1)` | Plays on track 0. `fade` is in seconds; -1 uses the rig's mixer. |
| `set_track(track, name, alpha := 1, fade := -1)` | Plays an animation over the tracks below it, e.g. arms shooting while legs walk. It changes only what it has keys for. |
| `set_track_alpha(track, alpha)`, `clear_track(track)` | Changes how strongly a track shows, or removes it. |
| `stop()`, `seek(time)`, `advance(delta)` | Rest pose; jump in time; step time yourself (with `playing` off). |
| `get_current_animation(track := 0)`, `get_time(track := 0)` | What is playing, and where. |
| `get_animation_names()`, `get_animation_length(name)`, `get_mix_duration(from, to)` | What the rig has. |
| `show_skin(name, on := true)`, `get_skin_names()`, `get_shown_skins()` | Skins. Within one category, only one is on at a time; overlays stack on top. |
| `get_point_transform(name)`, `get_point_global_transform(name)` | Where a point is and which way it faces, e.g. a muzzle or a hand. |
| `get_bone_transform(name)` | A bone's joint, with its x axis pointing along the bone. |
| `get_hitboxes(only_active := true)`, `get_hitbox_polygon(hitbox)` | Hitboxes: name, kind, centre transform and size, or as an outline. |
| `reload()` | Reads the file again after you re-export it. |
| `get_pose()`, `get_rig_data()` | The pose and the data themselves, in Harlequin's space (y up). |

Signals:
- `event(name, data, volume, pitch)` fires when playback passes an event key.
- `animation_finished(name, track)` fires when an animation that doesn't loop reaches its end.
- `animation_looped(name, track)` fires each time a looping animation goes round.

**Sounds.** Turn on **Play Sounds** to play the rig's own sounds: the sound map (event name to file)
and each animation's sound track. Paths are relative to the `.hqrig.json`, so keep the files there.
Godot imports `.wav` and `.ogg`. Without Play Sounds, you play whatever you like from the `event`
signal.

**Hitboxes.** Turn on **Show Hitboxes** to see them drawn: red for hurt, yellow for hit, blue for
the rest. Points show as green dots. Inactive boxes are drawn faded.

## How it's drawn

- **Parts.** Each part is a `Polygon2D`: four corners, or a mesh's triangles. The parts are kept in
  the current draw order.
- **Clipping masks.** A mask part is the parent of the parts it clips, using `clip_children`.
- **Tint and opacity.** These are the polygon's colour.
- **Blend modes and colour layers.** A part with a blend mode other than Normal, or with any of the
  object's colour layers on it, gets the plugin's shader. Notes:
  - Godot has no Screen blend, so Screen is a close match: lighter by the part's colour.
  - Up to 8 colour layers apply to a part.
  - Replace-colour layers only exist inside drawings, so the export bakes them into the pictures.

## Exporting your game

The rig file is JSON. If it's missing from an exported game, add `*.json` under
**Project → Export → Resources → Filters to export non-resource files/folders**.

## Tests

```
godot --headless --path godot --import
godot --headless --path godot -s tests/run_tests.gd
```

The tests check the plugin against `tests/expected.json`, which holds poses of the sample rig. The
repository's C# tests (`Animator.Tests/Export/GodotRuntimeTests.cs`) work out the same poses with
Harlequin's own code and check they match. That keeps the plugin and the editor in step.

If the runtime changes on purpose, write `expected.json` again:

```
godot --headless --path godot -s tests/run_tests.gd -- --update
```

`tests/make_sample.py` makes the sample rig. `tests/screenshot.gd` saves a picture of the demo; it
needs a display, so run it under xvfb-run on Linux.
