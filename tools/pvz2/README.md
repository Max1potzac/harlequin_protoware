# PvZ2 sound events → Harlequin

`pvz2_events.py` turns a PvZ2 animation's events and sounds into Harlequin's two files:

- **`NAME.events.json`** is for Harlequin's **Import events…**.
  - Every command in the animation's frames becomes an event, in the animation its frame label
    starts. Commands include `use_action`, `use_special` and `PlaySample`.
  - Sound events from your mapping go in alongside them.
- **`NAME.sounds.json`** is for **Import a sound map…**.
  - It gives each named sound event and the audio file it plays.
  - Random containers list every take under `"variants"`. Harlequin plays the first one.

It needs Python 3.8 or later and nothing else.

## What it needs

| Option | What to give it |
|---|---|
| (the first argument) | The animation as `.pam.json`, as Sen / PvZ2 toolkits convert `.pam`. |
| `--banks` | Soundbanks, or folders of them. Include the one holding the events (**`Global_Data.bnk`**) and the ones whose audio you dumped (e.g. `VO_Sunflower.bnk`). `RESOURCES.json` says which bank goes with what (e.g. group `AudioSunflower` → `VO_Sunflower.bnk`). |
| `--names` | Files to find event names in. Soundbanks only keep a hash of each name, so the names come from game data that writes them out. That includes `PLANTPROPERTIES.json` (`PlantFoodActivationSound`, `ProjectileLaunchSound`…), `COLLECTABLETYPES.json` (`CollectSound`…), zombie properties, or the game's library (`libPVZ2.so`). Any file works: every name-like string in it is tried. |
| `--audio` | Folders of the dumped audio, named by Wwise media id (`224518217.wav`, `.ogg`, `.wem`). |
| `--map` | The mapping file. It says which sound plays on which command or label. |

PvZ2 animations mostly don't name their sounds. Their frames say `use_action` or `use_special`,
and the game decides what plays then. That's what the mapping file is for:

```json
{
  "commands": { "use_action": "Play_Spawn_Sun" },
  "labels":   { "plantfood_on": "Play_Plant_Sunflower_Nitro" },
  "frames":   [ { "frame": 89, "event": "Play_Something" } ],
  "rename":   { "use_action": "produce" }
}
```

- `commands` adds a sound event on every frame with that command.
- `labels` adds one at the label's first frame.
- `frames` adds one at a frame of the whole timeline.
- `rename` writes a command's event under another name.

`--suggest PLANT` writes a mapping to start from, using the plant's or zombie's properties:

- `PlantFoodActivationSound` goes on the plant food label.
- A launch sound goes on `use_action`.
- Any other sound field goes under `"unplaced"`, for you to put where it belongs.

## Example: the sunflower

```
python3 pvz2_events.py sunflower.pam.json --names PLANTPROPERTIES.json --suggest Sunflower --map sunflower.map.json
python3 pvz2_events.py sunflower.pam.json --banks banks/ --names PLANTPROPERTIES.json COLLECTABLETYPES.json \
    --audio Converted/ --map sunflower.map.json --out out/
```

```
  special        use_action@29
  plantfood_on   Play_Plant_Sunflower_Nitro@0
  plantfood      use_special@3, use_special@7, use_special@11
out/sunflower.sounds.json: 2 named sounds, 1 with several takes (the first is used)
  Play_Spawn_Sun -> ../Converted/22603879.wav
```

The converter says when an event's audio isn't in the audio folder. If it knows the bank, it says
that too. `Play_Plant_Sunflower_Nitro`'s audio is in `Audio_Always_Loaded.bnk`.

Sound paths are written relative to the output folder. Keep the audio where it is, or fix the
paths, before importing the sound map into Harlequin.

## Tests

`python3 -m unittest discover tools/pvz2` runs them. They build a small soundbank in the test
itself, so the repository holds no game files.
