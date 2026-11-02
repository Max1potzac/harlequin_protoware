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
| `import <project> <file>... [--no-rig] [--no-split] [--scale S]` | Animate projects (an XFL folder, `.xfl`, `DOMDocument.xml`, `.fla`, a zipped XFL folder) and Photoshop files as new objects, with a suggested rig (as Import with *Suggest a rig* ticked). `--no-split`: one animation, not one per frame label. A project that isn't there yet is made. |
| `bring-in <project> --object NAME --bones B,... <animate file>... [--category Head] [--animations] [--loose] [--no-accessories] [--except W,...] [--map A=B,...]` | Edit → Bring in from Animate projects: for each file, the parts on those bones (and the bones under them) as a skin of the object, named after the file (`zombie_egypt_basic` → `egypt`), in the category. Parts the object hasn't (a hat, a pharaoh's headdress) come too, shown only with their skin, unless `--no-accessories`. They're lined up as they show in their own animations, unless `--loose`. `--animations`: their animations too, as `idle_egypt` and so on. `--except armor,ink`: parts with those words in their names stay out. `--map theirs=ours,...`: a part of theirs goes on that part here, for names that don't match (an auto-named `zombie_summernights_105x74=zombie_skull`); files without that part ignore it. |
| `show <project> --object NAME [--skins A,B] [--hide A,B] [--none]` | Which skins are on, as the project opens (and exports). |
| `skin <project> --object NAME --name SKIN --parts P,... [--category C] [--overlay] [--on]` | A skin of those parts' own drawings: the parts then show only while it's on (a cone, a bucket). Made off unless `--on`. |
| `rename <project> --object NAME [--skin SKIN] --to NEW` | The object, or (with `--skin`) one of its skins. |
| `remove <project> --object NAME [--animations A,B] [--skins A,B] [--parts P,Q]` | Takes out animations, skins or parts; with none of those, the object. |
| `render <project> --out FILE.png [--object NAME] [--animation A] [--frame N \| --time T] [--skins A,B] [--scale S] [--background RRGGBB]` | A picture of an object: at rest, or a frame of an animation, with those skins on. |
| `render ... --sheet CATEGORY [--columns N]` | One picture of every skin in the category (or `all`) side by side, posed alike, each named: a quick look at every head. |
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
