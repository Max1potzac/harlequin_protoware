# Harlequin engine format (`.hqrig.json` + atlas pages), version 1

The engine export writes one object as a game plays it:

| File | What it is |
|---|---|
| `name.hqrig.json` | The rig, its animations, the atlas layout, the sound map |
| `name.atlas.png`, `name.atlas2.png`, … | Atlas pages: every picture the rig uses, packed |
| `name.events.json` | (Optional) the events alone, in the simple events-file schema (see `Core/Export/EventsFile.cs`) |

The JSON is camelCase. Fields that equal their default are often left out (`null`); the defaults are
given below. Every name (bones, parts, drawings, skins, constraints, hitboxes, animations) is unique
within its kind and is how things refer to each other.

**A Godot 4 runtime** that plays all of this is in the repository's `godot/` folder
(`godot/addons/harlequin`, see its README); its tests are checked against the editor's own results.

**The reference implementation is the editor itself.** Where this document says "see", the C# file
named is the exact behaviour a runtime should match (paths are under `Animator.Core/`).

---

## 1. Top level

```jsonc
{
  "format": "harlequin-rig",   // always this
  "version": 1,                 // this document's version
  "object": "zombie",
  "fps": 30,                    // the project's frames per second (keys are in seconds; fps is for reference)
  "atlas": { … },               // section 2
  "rig": { … },                 // sections 3–9 (the same shape as the editor's own project data:
                                //  Serialization/ProjectDto.cs, class RigDto)
  "sounds": { "footstep": "sfx/step.wav" }   // the sound map (section 10), paths relative to this file
}
```

## 2. Atlas

```jsonc
"atlas": {
  "pages":   [ { "file": "zombie.atlas.png", "width": 1024, "height": 512 } ],
  "regions": { "<asset id>": { "page": 0, "x": 2, "y": 2, "w": 40, "h": 60,
                               "offsetX": 3, "offsetY": 0, "width": 48, "height": 60 } }
}
```

* A **picture** (an *asset*) is identified by a GUID. Skin entries (section 5) name the picture a
  drawing shows by that id.
* Pictures may be **trimmed** of see-through edges (never one a mesh uses: its triangles may reach
  into them). `width × height` is the picture's whole size;
  everything in the rig (placement, pivot, mesh points) counts pixels of the whole picture. Its pixel
  `(px, py)` is on the page at `(x + px − offsetX, y + py − offsetY)`; the rest of the whole picture
  is transparent.
* Page pixels are straight (not premultiplied) RGBA PNG. Pictures keep a gap of empty pixels round
  them.
* Colour layers *inside* drawings are baked into the pictures when the export's "Bake colour
  correction layers" is on, and left out otherwise. The object's own colour layers (section 8) are
  always data.

## 3. Space and units

* Units: one unit is one pixel of art at scale 1. **y points up**; angles are radians,
  counter-clockwise (positive turns from +x toward +y).
* Matrices are 2×3, row vectors (like `System.Numerics.Matrix3x2`): a point `p` maps to
  `p · M`, and `A · B` means "A, then B".
* Pictures have their own pixels: x right, **y down**, origin top-left.

## 4. Bones

`rig.bones`: parents always come before their children.

| Field | Meaning (default) |
|---|---|
| `name`, `parent` | `parent` null = a root bone |
| `length` | from its joint to its tip, along its x axis |
| `position`, `rotation`, `scale` | the rest pose, in the parent's space (`scale` 1,1) |
| `inheritRotation`, `inheritScale` | (true) whether it follows the parent's turn / size |
| `turn`, `tilt` | depth rotation at rest (0): see below |
| `minOffset`, `maxOffset` | (null) limits on its position, relative to the rest position; see `Rigging/Bone.cs` ApplyLimits |
| `connected`, `hidden`, `locked`, `editorColor` | editor-only |

**World transform** (see `Rigging/Bone.cs` UpdateWorld):

```
effectiveScale = (scale.x · cos(tilt), scale.y · cos(turn))
local = Scale(effectiveScale) · Rotate(rotation) · Translate(position)
world = local · parent.world                 (inheritRotation and inheritScale)
```

With either inherit flag off, the bone's joint is `position · parent.world`, and only the parent's
scale (`|row 1|`, `|row 2|` of `parent.world`) and/or angle (`atan2(M12, M11)`) that are inherited
apply before its own scale and rotation. A bone's **tip** is `(length, 0) · world`.

Space keys (section 7) can make a bone follow another bone than its parent (held), or nothing
(detached, its last world place kept); see `Rigging/Bone.cs` SetSpace and `Editing/SpaceSwitching.cs`.

## 5. Parts, drawings and skins

`rig.slots` (parts), **back to front** (the rest draw order):

| Field | Meaning (default) |
|---|---|
| `name`, `bone` | the part, and the bone it's on |
| `attachment` | the drawing it shows at rest (null = none) |
| `blend` | (Normal) Normal, Add, Multiply, Screen, … |
| `color` | (1,1,1) tint: multiplies the picture's red, green, blue |
| `opacity` | (1) |
| `mask` | (null) a clipping mask, section 6 |
| `hidden`, `locked` | editor-only |

`rig.skins`: the first is `"default"` (the object's own drawings); others are optional looks.

| Field | Meaning |
|---|---|
| `name`, `category`, `overlay` | see **Skins** below |
| `colorLayers` | names of the object's colour layers that apply only while this skin is on |
| `entries` | drawings: `{ slot, name, asset, offset, rotation, scale, pivot, mesh? }` |

A **drawing** is a picture placed on its part's bone: `pivot` is normalised (0..1) in the picture,
`(0.5, 0.5)` its middle. Picture pixels → the bone's space (see `Rigging/Attachment.cs` LocalMatrix):

```
Translate(−pivot.x · width, −pivot.y · height) · Scale(1, −1) · Scale(scale · keyedScale)
  · Rotate(rotation + keyedRotation) · Translate(offset + keyedOffset)
```

then `· bone.world`. The keyed values are the part's placement keys (0, 0, (1,1) when none).

**Which drawing a part shows:** its current drawing *name* (rest `attachment`, or attachment keys),
looked up first in the skins that are on (an overlay before a non-overlay, then the later in the list
first), then in `default`. Not found anywhere = the part shows nothing.

**Skins** (see `Rigging/Skin.cs`, `Editing/SkinOps.cs`): `rig.shownSkins` lists the ones on (null =
none; older files: `activeSkin`). Skins in the same `category` are alternatives, one on at a time;
skins with no category are outfits, one at a time too; `overlay` skins stack over anything. A runtime
should let the game turn skins on and off by these rules.

## 6. Clipping masks, meshes

**Masks** (see `Editing/MaskOps.cs`, `Viewer/Rendering/SkeletonRenderer.cs` DrawSlots): a part with
`mask: { until, shows }` clips the parts drawn right above it in the *current* draw order, up to and
including `until` (null = all the rest): they show only where the mask's picture has alpha.
The mask itself isn't drawn unless `shows`. Masks don't nest (a mask inside another's range is an
ordinary part). Draw the clipped parts into a layer, then keep it only where the mask is (DstIn).
In Godot: the mask as a `CanvasItem` with `clip_children`, the clipped parts as its children.

**Meshes** (an entry's `mesh`, see `Rigging/Mesh.cs`, `Editing/MeshOps.cs`):

```jsonc
"mesh": { "w": 48, "h": 60,               // the picture size it was made for
          "points": [x0, y0, x1, y1, …],  // in the picture's pixels: also its UVs
          "triangles": [0, 1, 2, …],
          "weights": [[{"bone": "arm", "w": 0.7}, …], …],   // per point (null: none)
          "bind": [m11, m12, m21, m22, m31, m32],          // pixels → object, when weighted
          "boneBind": { "arm": [ …6… ] } }                 // object → each bone, then
```

* No weights: points go where the flat picture's pixels would (the drawing matrix above).
* Weights: point `i` goes to `(points[i] + deform[i]) · Σ w · (bind · boneBind[bone] · bone.world)`.
* `deform` is the part's current deform key (section 7), in picture pixels, for this drawing only.
* A mesh whose `w × h` isn't the picture's size is drawn flat.

## 7. Animations

`rig.animations`: `{ name, duration, loop, timelines, sound? }`. Every frame:

1. Set everything to its rest values (bones, parts, constraint mixes, colour layers, hitboxes, draw order).
2. Apply each timeline at the time (keys below).
3. Limits, world transforms, then constraints (section 9), then jiggles.

A timeline is `{ type, target, keys }`, each key `{ t, curve, b?, x?, y?, z?, name?, data?, order?, v? }`.
Between a key and the next, `curve` says how: `Linear`, `Stepped` (hold), `Smooth` (`3t² − 2t³`), or
`Bezier` with handles `b = [x1, y1, x2, y2]` (a CSS-style cubic from (0,0) to (1,1); solve x for t,
use y; see `Keyframing/Bezier.cs`). Before the first key the first key's value holds, after the last
the last's (except where noted: those only act from their first key).

| type | target | value → effect |
|---|---|---|
| `rotate` | bone | `x`: rotation = rest + x |
| `translate` | bone | `x, y`: position = rest + (x, y) |
| `scale` | bone | `x, y`: scale = rest × (x, y) |
| `depth` | bone | `x` tilt, `y` turn: added to rest |
| `space` | bone | `name`: null = its parent, `""` = detached, else the holding bone (from the first key) |
| `ikMix` | IK constraint | `x`: its mix |
| `attachment` | part | `name`: the drawing shown (null = none) (from the first key) |
| `partTranslate` / `partRotate` / `partScale` | part | placement on top of the drawing's (see section 5) |
| `partColor` | part | `x, y, z`: tint |
| `partOpacity` | part | `x` |
| `deform` | part | `name`: the drawing, `v`: [dx0, dy0, dx1, dy1, …] mesh point pushes (blend only between keys of the same drawing) |
| `drawOrder` | — | `order`: the parts' names, back to front (from the first key) |
| `colorLayer` | colour layer | `v`: its numbers (section 8) |
| `layerAdjust` | picture layer | a colour layer inside a drawing (editor; baked or left out on export) |
| `hitbox` | hitbox | `x`: 1 on, 0 off (stepped, from the first key) |
| `events` | — | `name`, `data`, `x` volume (1), `y` pitch (1): fire when playback passes the key |

Angles in keys may go past ±π (several turns): blend them as plain numbers. When **mixing** two
animations (section 11) angles blend the short way round.

`sound`: `{ path, offset, volume }`: a sound playing along the animation from `offset` seconds.

## 8. Colour layers

`rig.colorLayers`, applied first to last to every part except those in `leaveOut`; one in a skin's
`colorLayers` applies only while that skin is on. `adjust = { kind, colorize, keepLight, values }`;
`values` is the 27 numbers of `Assets/ColorAdjust.cs` AdjustValues, the filter for each kind is in
`Viewer/Rendering/ColorFilters.cs` (hue/saturation as a colour matrix, the rest per channel). A shader
runtime applies them per part after the part's tint.

## 9. Constraints, hitboxes, points

Solved in this order every frame, after the animation (see `Rigging/Skeleton.cs` UpdatePose):

1. **IK** `rig.ikConstraints`: `{ name, bones (1 or 2), target, mix, bend, iterations }` (see `Rigging/IkConstraint.cs`).
2. **Follow** `rig.copyConstraints`: `{ bone, source, local, rotate, move, scale, rotationOffset, positionOffset, scaleOffset, mix }` (see `Rigging/Constraints.cs` CopyConstraint).
3. **Look** `rig.lookConstraints`: `{ bone, target, shape, radius, follow, mix }` (see `Rigging/LookConstraint.cs`).
4. **Jiggle** `rig.jiggleConstraints`: `{ bone, stiffness, damping, gravity, mix }`, stepped with time, parents first (see `Rigging/Constraints.cs` JiggleConstraint): restart from rest when time jumps back.

**Hitboxes** `rig.hitboxes`: `{ name, bone, shape ("box" | "circle"), offset, rotation, size, kind, active }`:
in the bone's space, centre `offset`, a box `size.x × size.y` turned by `rotation`, a circle of
diameter `size.x`. `active`: on at rest (hitbox keys switch it). **Points** `rig.points`: `{ name, bone,
offset, rotation }`: `Rotate(rotation) · Translate(offset) · bone.world`.

## 10. Sound

Events (section 7) carry a name; the game decides what it plays. `sounds` maps names to files for a
runtime that wants the editor's preview sounds. Sound tracks (`animation.sound`) are paths too.

## 11. Mixing

`rig.mixes`: `{ default, pairs: [ { from, to, duration } ] }` (seconds; `default` 0.2): how long one
animation takes to fade into another. Layering and fading (see `Editing/Mixing.cs`): apply the lower
track, remember the pose, apply the upper track, blend between them by its weight; an animation only
changes what it has keys for. Values blend linearly (angles the short way round); drawings, the draw
order and hitboxes switch at half way.

## 12. Changes

* Version 1: first version (Harlequin project format 17).
