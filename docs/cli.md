# Harlequin on the command line

`harlequin` does Harlequin's batch work without a window: on Linux (or Windows, or in a script). It
opens and saves the same `.hqnap` projects as the editor, through the same code (Animator.Engine),
so a project made here opens in the editor as if made there, and the other way round.

## Getting it

From the [Releases page](https://github.com/Max1potzac/harlequin_protoware/releases):
**cli-latest** (built from master) or **cli-preview** (the latest build of any other branch).

- Linux: `harlequin-linux-x64.tar.gz`, then `tar -xzf harlequin-linux-x64.tar.gz` and `./harlequin help`
- Windows: `harlequin-win-x64.zip`, then `harlequin.exe help` in a terminal

Nothing else needs installing (.NET is inside). Or build it: `dotnet publish Animator.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true`.

## Commands

Every command takes the project first. Lists are comma-separated (`--bones head,jaw`). Commands
that change a project save it in place (written beside it first, so a failed save leaves the old one).

| Command | What it does |
|---|---|
| `info <project> [--object NAME] [--tree]` | The objects, their animations (length, frames, which loop) and skins by category (`(on)` = shown). `--tree`: the bones, with the parts on each. |
| `import <project> <file>... [--no-rig] [--no-split] [--scale S] [--rig FILE]` | Animate projects (an XFL folder, `.xfl`, `DOMDocument.xml`, `.fla`, a zipped XFL folder) and Photoshop files as new objects, with a suggested rig (as Import with *Suggest a rig* ticked). `--no-split`: one animation, not one per frame label. A project that isn't there yet is made. `--rig FILE` (Animate only): a bone for every part at its symbol's origin (where a game turns it), hung as the file's rules say: a JSON list of `[part, parent]` name pairs, `*` standing for anything, the first rule a part matches deciding (`["*hand_inner*", "*arm_inner_lower"]`; a parent of `""` is the object's own bone). |
| `bring-in <project> --object NAME --bones B,... <animate file>... [--category Head] [--animations] [--loose] [--no-accessories] [--except W,...] [--map A=B,...] [--include P,...] [--by-bones] [--imported-from FILE] [--rig FILE] [--copy-missing W,...] [--fit W,...] [--by-pose W,...] [--own W,...] [--verbose]` | Edit → Bring in from Animate projects: for each file, the parts on those bones (and the bones under them) as a skin of the object, named after the file (`zombie_egypt_basic` → `egypt`), in the category. Parts the object hasn't (a hat, a pharaoh's headdress) come too, shown only with their skin, unless `--no-accessories`. They're lined up as they show in their own animations, unless `--loose`. `--animations`: their animations too, as `idle_egypt` and so on, copied by where their parts show, frame by frame (keys on every frame), so they play right on a rig that doesn't match theirs bone for bone — two imports of similar characters never do; with no `--bones` or `--include`, just the animations come. `--by-bones`: bone for bone instead (rigs that match). `--except armor,ink`: parts with those words in their names stay out (`=flag`: the part called just that). `--map theirs=ours,...`: a part of theirs goes on that part here, for names that don't match (an auto-named `zombie_summernights_105x74=zombie_skull`); files without that part ignore it. `--include P,...`: those parts of theirs come too, wherever their bones are (a hairflap hung off the torso); without `--bones` they're the only ones (a body as a skin: `--category Body --include zombie_torso,...`). `--imported-from FILE`: the Animate project the object was imported from; with it, everything is placed exactly, through the symbols (a head goes where the game draws it in that symbol, whatever pose the two are in), and the two projects' stages line up. `--rig FILE`: the files brought in get that rig (as import). `--copy-missing hand`: with `--animations`, their parts and drawings with those words the object has nothing like are copied over for them (hidden at rest). `--fit arm,leg,hand,foot`: those parts fitted by their joints where their art is drawn longer or shorter (only joints they really turn about). `--by-pose arm,leg,foot`: those parts (in the skin, and in `--animations`) placed as the two show at the start of idle rather than through the symbols — for a character's own art drawn elsewhere in its symbols (a pirate's boots, a cowboy's sleeves); a drawing either one swaps to is placed from that through the two drawings' symbols. `--own hand`: with `--animations`, those parts show the source's own drawings in its animations (copied, named `…_pirate`), even where the object has drawings by the same names — hands, whose poses differ from character to character. `--verbose`: which part of theirs went on which here. Where a part would swing round or fly off between frames as it plays (a part written another way on the next frame), the keys there are held until the next frame. |
| `show <project> --object NAME [--skins A,B] [--hide A,B] [--none]` | Which skins are on, as the project opens (and exports). |
| `skin <project> --object NAME --name SKIN --parts P,... [--category C] [--overlay] [--on] [--from SKIN]` | A skin of those parts' own drawings: the parts then show only while it's on (a cone, a bucket). Made off unless `--on`. `--from`: their drawings taken out of that skin first (a hat out of a head skin, into a skin of its own). |
| `attach <project> --object NAME --parts P,... --bone B` | Those parts put on that bone, staying where they are at rest; the animations still show them where they did (as with *Animations stay as they were* in the editor). |
| `hides <project> --object NAME --skin SKIN --parts P,... [--not]` | Those parts show nothing while the skin is on: what an outfit covers (legs under a long skirt). `--not`: they show again. |
| `order <project> --object NAME --parts P,... (--behind Q \| --in-front-of Q) [--animation A]` | Parts moved in the draw order: at rest, and in every animation's order keys that drew them as the rest order did (keys that draw them otherwise on purpose stay as they are). `--animation A`: in that animation instead, all through it (every key of its order; one at its start if it keys none). |
| `rename <project> --object NAME [--skin SKIN \| --animation A] --to NEW` | The object, or one of its skins or animations. |
| `remove <project> --object NAME [--animations A,B] [--skins A,B] [--parts P,Q]` | Takes out animations, skins or parts; with none of those, the object. |
| `render <project> --out FILE.png [--object NAME] [--animation A] [--frame N \| --time T] [--skins A,B] [--scale S] [--background RRGGBB]` | A picture of an object: at rest, or a frame of an animation, with those skins on. |
| `render ... --sheet CATEGORY [--columns N]` | One picture of every skin in the category (or `all`) side by side, posed alike, each named: a quick look at every head. |
| `pose <project> [--object NAME] [--animation A] [--frame N \| --time T] [--skins A,B] [--sweep K]` | Every part shown then, front last: its drawing, where its picture's middle is, its turn and size (tab-separated). `--sweep K`: the whole animation, K times a frame (as it plays between keys), each line led by its time. For checking one animation against another. |
| `export <project> --format engine\|png\|gif\|sheet --out PATH [--object NAME] [--animations A,B] [--scale S] [--bake] [--pixel-art] [--no-events] [--background RRGGBB]` | File → Export. With `--object`, that object's animations (all, or those named); without, the scene timeline. `engine` is the Godot plugin's format (needs `--object`). `--bake`: the colour layers in the pictures. |

Exit codes: 0 done, 1 it couldn't be done (a file that can't be read, nothing to draw), 2 the
command line itself was wrong.

## Example: one zombie, every variant's head

```
harlequin import zombies.hqnap zombie_tutorial.pam.xfl
harlequin info zombies.hqnap --tree                      # find the head bone's name
harlequin bring-in zombies.hqnap --object zombie_tutorial.pam.xfl --bones head \
    zombie_egypt_basic.pam.xfl zombie_pirate_basic.pam.xfl zombie_beach_basic.pam.xfl
harlequin render zombies.hqnap --sheet Head --animation idle --out heads.png
harlequin show zombies.hqnap --object zombie_tutorial.pam.xfl --none
```
