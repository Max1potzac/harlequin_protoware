"""Tests for the PvZ2 converter, with a small soundbank and animation made here (no game files).
Run: python3 -m unittest discover tools/pvz2"""
import json
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wwise  # noqa: E402
import pvz2_events  # noqa: E402


def node_base(parent):
    # NodeBaseParams as bank version 140 writes them: no effects, no metadata, a parent
    return bytes([0, 0, 0, 0, 0]) + struct.pack('<II', 0, parent) + b'\x00' * 12


def obj(kind, body):
    return bytes([kind]) + struct.pack('<I', len(body)) + body


def sound(oid, media, parent):
    return obj(2, struct.pack('<IIBIIB', oid, 0x00040001, 0, media, 1000, 0) + node_base(parent))


def container(oid, kind, parent):
    return obj(kind, struct.pack('<I', oid) + node_base(parent))


def action(oid, target, kind=wwise.PLAY):
    return obj(3, struct.pack('<IHIB', oid, kind, target, 0) + b'\x00' * 8)


def event(oid, actions):
    return obj(4, struct.pack('<I', oid) + bytes([len(actions)]) + b''.join(struct.pack('<I', a) for a in actions))


def bank(path, objects=(), media=()):
    def chunk(tag, data):
        return tag + struct.pack('<I', len(data)) + data
    data = chunk(b'BKHD', struct.pack('<II', 140, 1234) + b'\x00' * 8)
    if media:
        data += chunk(b'DIDX', b''.join(struct.pack('<III', m, 0, 10) for m in media))
        data += chunk(b'DATA', b'\x00' * 10 * len(media))
    if objects:
        data += chunk(b'HIRC', struct.pack('<I', len(objects)) + b''.join(objects))
    with open(path, 'wb') as f:
        f.write(data)


class ConverterTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        p = lambda *n: os.path.join(self.dir, *n)  # noqa: E731
        self.p = p
        spawn, groan, mixer, takes = wwise.event_id('Play_Spawn_Thing'), wwise.event_id('Play_Groan'), 900, 901
        bank(p('Global_Data.bnk'), [
            container(mixer, 7, 0),
            container(takes, 5, mixer),            # a random container of two takes
            sound(902, 111, takes), sound(903, 222, takes),
            sound(904, 333, mixer),
            action(905, takes), event(spawn, [905]),
            action(906, 904), event(groan, [906]),
            action(907, 904, kind=0x0102), event(wwise.event_id('Stop_Groan'), [907]),   # (a stop: plays nothing)
            event(wwise.event_id('Play_Nameless'), [905]),
        ])
        bank(p('VO_Thing.bnk'), media=[111, 222, 333])
        os.makedirs(p('audio'))
        for m in (111, 222, 333):
            open(p('audio', '%d.wav' % m), 'wb').close()
        with open(p('names.json'), 'w') as f:
            json.dump({'objects': [{'objclass': 'ThingProps', 'objdata': {
                'PlantFoodActivationSound': 'Play_Groan', 'ProjectileLaunchSound': 'Play_Spawn_Thing', 'DeathSound': 'Play_Other'}}]}, f)
        frames = [{} for _ in range(40)]
        frames[0] = {'label': 'idle'}
        frames[9] = {'stop': True}
        frames[10] = {'label': 'attack'}
        frames[14] = {'command': [{'command': 'use_action', 'argument': ''}]}
        frames[20] = {'label': 'plantfood_on'}
        frames[25] = {'command': [{'command': 'PlaySample', 'argument': 'sfx_bonk'}]}
        with open(p('thing.pam.json'), 'w') as f:
            json.dump({'frame_rate': 30, 'main_sprite': {'frame': frames}}, f)

    def test_event_ids_are_wwise_fnv1(self):
        self.assertEqual(wwise.event_id('Play_Spawn_Sun'), 1745606728)   # (as in PvZ2's own banks)
        self.assertEqual(wwise.event_id('play_spawn_sun'), wwise.event_id('PLAY_SPAWN_SUN'))

    def test_the_graph_finds_what_events_play(self):
        graph = wwise.SoundGraph([wwise.Bank(self.p('Global_Data.bnk')), wwise.Bank(self.p('VO_Thing.bnk'))])
        self.assertEqual(graph.event_media(wwise.event_id('Play_Spawn_Thing')), {111, 222})
        self.assertEqual(graph.event_media(wwise.event_id('Play_Groan')), {333})
        self.assertEqual(graph.event_media(wwise.event_id('Stop_Groan')), set())

    def test_suggesting_a_mapping(self):
        _, _, labels, _ = pvz2_events.read_pam(self.p('thing.pam.json'))
        m = pvz2_events.suggest([self.p('names.json')], 'Thing', labels)
        self.assertEqual(m['labels'], {'plantfood_on': 'Play_Groan'})
        self.assertEqual(m['commands'], {'use_action': 'Play_Spawn_Thing'})
        self.assertEqual(m['unplaced'], {'DeathSound': 'Play_Other'})

    def test_converting(self):
        mapping = {'commands': {'use_action': 'Play_Spawn_Thing'}, 'labels': {'plantfood_on': 'Play_Groan'}}
        events, sounds, variants, report, events_path, sounds_path = pvz2_events.convert(
            self.p('thing.pam.json'), [self.dir], [self.p('names.json')], [self.p('audio')], mapping,
            self.p('out'), 'thing', log=lambda *_: None)
        anims = {a['name']: a['events'] for a in events['animations']}
        self.assertEqual(list(anims), ['attack', 'plantfood_on'])
        self.assertEqual([(e['frame'], e['name']) for e in anims['attack']], [(4, 'use_action'), (4, 'Play_Spawn_Thing')])
        self.assertNotIn('data', anims['attack'][0])   # (an empty argument)
        self.assertEqual([(e['frame'], e['name'], e.get('data')) for e in anims['plantfood_on']],
                         [(0, 'Play_Groan', None), (5, 'PlaySample', 'sfx_bonk')])
        self.assertAlmostEqual(anims['plantfood_on'][1]['time'], 5 / 30, places=3)
        self.assertEqual(sounds, {'Play_Spawn_Thing': '../audio/111.wav', 'Play_Groan': '../audio/333.wav'})
        self.assertEqual(variants['Play_Spawn_Thing'], ['../audio/111.wav', '../audio/222.wav'])
        self.assertEqual(len(report['unnamed_with_audio']), 1)   # Play_Nameless: no file names it
        with open(sounds_path) as f:
            self.assertIn('Play_Groan', json.load(f)['sounds'])
        with open(events_path) as f:
            self.assertEqual(json.load(f)['fps'], 30)


if __name__ == '__main__':
    unittest.main()
