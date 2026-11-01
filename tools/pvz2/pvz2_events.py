#!/usr/bin/env python3
"""PvZ2 animation sound events → Harlequin's events file and sound map.

Takes a PvZ2 animation (a .pam.json, as Sen / PvZ2 toolkits convert .pam files) and writes:

  NAME.events.json   every command in its frames (use_action, use_special, PlaySample…) as a
                     Harlequin event, in the animation its frame label starts, plus the sound
                     events a mapping file puts on commands or labels. File → Import events… in
                     Harlequin merges it into the animations of the same names.
  NAME.sounds.json   the sound map: each event name → the audio file it plays (File → Import a
                     sound map…), for every named event whose audio is in the given audio folder.

Event names come from strings in the game's files (--names: property sheets, collectable types,
even the game library), matched against the soundbanks' hashed event ids (--banks: the bank with
the events, Global_Data.bnk, and the ones with the audio). Audio files are named by their Wwise
media id (123456789.wav / .ogg / .wem), as dumping tools write them (--audio).

  python3 pvz2_events.py sunflower.pam.json --banks banks/ --names PLANTPROPERTIES.json \\
      COLLECTABLETYPES.json --audio Converted/ --map sunflower.map.json --out out/

  python3 pvz2_events.py sunflower.pam.json --names PLANTPROPERTIES.json --suggest Sunflower \\
      --map sunflower.map.json     (writes a mapping file to start from, from the plant's properties)

The mapping file (all of it optional):
  { "commands": { "use_action": "Play_Spawn_Sun" },          a sound event with each such command
    "labels": { "plantfood_on": "Play_Plant_Sunflower_Nitro" },   at that label's first frame
    "frames": [ { "frame": 89, "event": "Play_Something" } ],    at a frame of the whole timeline
    "rename": { "use_action": "produce" } }                    commands under other names
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wwise  # noqa: E402

AUDIO_EXTENSIONS = ('.wav', '.ogg', '.wem', '.mp3', '.flac')


# ----------------------------------------------------------------------------- the animation

def read_pam(path):
    """The main timeline's frames: frame rate, labels (where each animation starts and stops) and
    the commands on each frame"""
    with open(path, encoding='utf-8-sig') as f:
        pam = json.load(f)
    main = pam.get('main_sprite') or {}
    frames = main.get('frame') or []
    fps = pam.get('frame_rate') or 30
    labels, commands = [], []
    for i, frame in enumerate(frames):
        if frame.get('label'):
            labels.append((i, frame['label']))
        for c in frame.get('command') or []:
            name = c.get('command') if isinstance(c, dict) else (c[0] if isinstance(c, list) and c else None)
            arg = c.get('argument') if isinstance(c, dict) else (c[1] if isinstance(c, list) and len(c) > 1 else None)
            if name:
                commands.append((i, name, arg or None))
    return fps, len(frames), labels, commands


def animation_at(labels, frame):
    """The animation (frame label) a frame is in, and the frame within it"""
    current = None
    for start, name in labels:
        if start <= frame:
            current = (start, name)
    if current is None:
        return None, frame
    return current[1], frame - current[0]


# ----------------------------------------------------------------------------- suggesting a mapping

SOUND_FIELDS_ON_COMMAND = ('ProjectileLaunchSound', 'AudioEventName', 'LaunchSound', 'AttackSound', 'FireSound')


def suggest(properties_paths, plant, labels):
    """A mapping to start from: the sound fields of the plant's (or zombie's) properties"""
    want = plant.lower()
    found = None
    for path in properties_paths:
        try:
            with open(path, encoding='utf-8-sig') as f:
                data = json.load(f)
        except (ValueError, OSError):
            continue
        for obj in data.get('objects', []) if isinstance(data, dict) else []:
            names = [str(obj.get('objclass', ''))] + [str(a) for a in obj.get('aliases', [])]
            lowered = [n.lower() for n in names]
            if want + 'props' in lowered or want + 'default' in lowered or want in lowered:
                found = obj
                break
        if found:
            break
    mapping = {'commands': {}, 'labels': {}, 'frames': [], 'unplaced': {}}
    if not found:
        mapping['note'] = 'No properties for "%s" in the given files' % plant
        return mapping
    mapping['from'] = found.get('objclass')
    label_names = [n for _, n in labels]

    def walk(o, path=''):
        if isinstance(o, dict):
            for k, v in o.items():
                yield from walk(v, path + '/' + k)
        elif isinstance(o, list):
            for i, v in enumerate(o):
                yield from walk(v, '%s[%d]' % (path, i))
        elif isinstance(o, str) and path.split('/')[-1].lower().endswith(('sound', 'eventname', 'audio')):
            yield path, o

    for path, event in walk(found.get('objdata', {})):
        field = path.split('/')[-1]
        if field == 'PlantFoodActivationSound':
            label = next((n for n in ('plantfood_on', 'plantfood', 'powerup') if n in label_names), None)
            if label:
                mapping['labels'][label] = event
                continue
        if field in SOUND_FIELDS_ON_COMMAND and 'use_action' not in mapping['commands']:
            mapping['commands']['use_action'] = event
            continue
        mapping['unplaced'][path.lstrip('/')] = event
    return mapping


# ----------------------------------------------------------------------------- audio files

def audio_files(folders):
    """Media id -> file, from audio named by id"""
    files = {}
    for folder in folders:
        for root, _, names in os.walk(folder):
            for name in names:
                stem, ext = os.path.splitext(name)
                if ext.lower() in AUDIO_EXTENSIONS and stem.isdigit():
                    files.setdefault(int(stem), os.path.join(root, name))
    return files


def bank_files(paths):
    out = []
    for p in paths:
        if os.path.isdir(p):
            for root, _, names in os.walk(p):
                out += [os.path.join(root, n) for n in sorted(names) if n.lower().endswith('.bnk')]
        else:
            out.append(p)
    return out


# ----------------------------------------------------------------------------- the whole job

def convert(pam_path, banks=(), names=(), audio=(), mapping=None, out_dir='.', object_name=None,
            log=print):
    fps, frame_count, labels, commands = read_pam(pam_path)
    stem = object_name or os.path.basename(pam_path).split('.')[0]
    mapping = mapping or {}
    rename = mapping.get('rename', {})

    animations = {}   # label -> [event]

    def add(frame, name, data=None, origin=''):
        anim, at = animation_at(labels, frame)
        if anim is None:
            log('  frame %d is before the first label: left out (%s)' % (frame, name))
            return
        animations.setdefault(anim, []).append(
            {'frame': at, 'time': round(at / fps, 4), 'name': name, 'data': data, '_from': origin})

    for frame, name, arg in commands:
        add(frame, rename.get(name, name), arg, 'command')
        if name in mapping.get('commands', {}):
            add(frame, mapping['commands'][name], None, 'mapped to %s' % name)
    starts = {n: f for f, n in labels}
    for label, event in mapping.get('labels', {}).items():
        if label in starts:
            add(starts[label], event, None, 'label %s' % label)
        else:
            log('  the mapping names a label the animation hasn\'t: %s' % label)
    for item in mapping.get('frames', []):
        add(int(item['frame']), item['event'], item.get('data'), 'frame')

    events_file = {'object': stem, 'fps': fps, 'animations': []}
    for _, label in labels:   # (in timeline order; one entry per label, even without events)
        events = sorted(animations.get(label, []), key=lambda e: e['frame'])
        if not events or any(a['name'] == label for a in events_file['animations']):
            continue
        events_file['animations'].append({
            'name': label,
            'events': [{k: v for k, v in e.items() if not k.startswith('_') and v is not None} for e in events]})

    # Sounds: name the events, find what each plays, keep those whose audio is here
    sound_map, variants, report = {}, {}, {'unnamed_with_audio': []}
    files = audio_files(audio)
    graph = None
    if banks:
        graph = wwise.SoundGraph([wwise.Bank(p) for p in bank_files(banks)])
        for w in graph.warnings:
            log('  ' + w)
        used = {e['name'] for a in events_file['animations'] for e in a['events']}
        candidates = wwise.harvest_names(names, extra=used | set(mapping.get('commands', {}).values())
                                         | set(mapping.get('labels', {}).values()))
        named = wwise.name_events(graph, candidates)
        log('  %d events in the banks, %d named from %d strings' % (len(graph.events), len(named), len(candidates)))
        out_abs = os.path.abspath(out_dir)
        for eid in graph.events:
            media = sorted(graph.event_media(eid))
            here = [m for m in media if m in files]
            if not here:
                continue
            paths = [os.path.relpath(os.path.abspath(files[m]), out_abs).replace(os.sep, '/') for m in here]
            if eid in named:
                sound_map[named[eid]] = paths[0]
                if len(paths) > 1:
                    variants[named[eid]] = paths
            else:
                report['unnamed_with_audio'].append({'id': eid, 'media': here})
        for name in sorted(used):
            if name in sound_map:
                continue
            eid = wwise.event_id(name)
            if eid in graph.events:
                media = sorted(graph.event_media(eid))
                where = sorted({os.path.basename(graph.media_bank[m]) for m in media if m in graph.media_bank})
                log('  %s is an event, playing %s, but none of it is in the audio folder%s'
                    % (name, ', '.join(map(str, media)) or 'nothing', ' (it\'s in %s)' % ', '.join(where) if where else ''))

    os.makedirs(out_dir, exist_ok=True)
    events_path = os.path.join(out_dir, stem + '.events.json')
    with open(events_path, 'w', encoding='utf-8') as f:
        json.dump(events_file, f, indent=2)
    sounds_path = None
    if graph is not None:
        sounds_path = os.path.join(out_dir, stem + '.sounds.json')
        doc = {'sounds': dict(sorted(sound_map.items()))}
        if variants:
            doc['variants'] = variants   # (Harlequin plays the first; the game picks one of them)
        with open(sounds_path, 'w', encoding='utf-8') as f:
            json.dump(doc, f, indent=2)
    return events_file, sound_map, variants, report, events_path, sounds_path


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('pam', help='the animation (.pam.json)')
    p.add_argument('--banks', nargs='*', default=[], help='soundbanks (.bnk) or folders of them: the events (Global_Data.bnk) and the audio')
    p.add_argument('--names', nargs='*', default=[], help='files to find event names in (property sheets, collectable types, the game library…)')
    p.add_argument('--audio', nargs='*', default=[], help='folders of audio files named by media id')
    p.add_argument('--map', help='the mapping file (sound events on commands and labels)')
    p.add_argument('--suggest', metavar='PLANT', help='write a mapping file for this plant or zombie (from --names) to --map, and stop')
    p.add_argument('--object', help='the name for the output files (default: from the animation file)')
    p.add_argument('--out', default='.', help='where to write (default: here)')
    a = p.parse_args(argv)

    if a.suggest:
        if not a.map:
            p.error('--suggest writes to --map: give it a file name')
        _, _, labels, _ = read_pam(a.pam)
        mapping = suggest(a.names, a.suggest, labels)
        with open(a.map, 'w', encoding='utf-8') as f:
            json.dump(mapping, f, indent=2)
        print('wrote %s from %s: check it, and move what\'s under "unplaced" where it belongs'
              % (a.map, mapping.get('from', 'nothing')))
        return 0

    mapping = None
    if a.map:
        with open(a.map, encoding='utf-8-sig') as f:
            mapping = json.load(f)
    print('Converting %s' % a.pam)
    events_file, sound_map, variants, report, events_path, sounds_path = convert(
        a.pam, a.banks, a.names, a.audio, mapping, a.out, a.object)
    count = sum(len(x['events']) for x in events_file['animations'])
    print('%s: %d events in %d animations' % (events_path, count, len(events_file['animations'])))
    for anim in events_file['animations']:
        print('  %-14s %s' % (anim['name'], ', '.join('%s@%d' % (e['name'], e['frame']) for e in anim['events'])))
    if sounds_path:
        print('%s: %d named sounds%s' % (sounds_path, len(sound_map),
              (', %d with several takes (the first is used)' % len(variants)) if variants else ''))
        for name, path in sorted(sound_map.items()):
            print('  %s -> %s' % (name, path))
        if report['unnamed_with_audio']:
            print('  %d more events play this audio but have no name in the files given: give --names the file that names them'
                  % len(report['unnamed_with_audio']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
