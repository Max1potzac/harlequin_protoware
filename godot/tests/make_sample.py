"""Makes the sample rig (demo/sample.hqrig.json and demo/sample.atlas.png): a small made-up
character using every part of the engine format, for the demo scene and for the tests that check
the Godot runtime against Harlequin's own (Animator.Tests/Export/GodotRuntimeTests.cs).

Run from the godot folder:  python3 tests/make_sample.py
"""
import json, math, struct, zlib, os

# ---------------------------------------------------------------- pictures

def png(path, w, h, pixels):
    raw = b''.join(b'\x00' + bytes(pixels[y * w * 4:(y + 1) * w * 4]) for y in range(h))
    def chunk(kind, data):
        c = struct.pack('>I', len(data)) + kind + data
        return c + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)))
        f.write(chunk(b'IDAT', zlib.compress(raw, 9)))
        f.write(chunk(b'IEND', b''))

# name: (width, height, colour, round?)
PICTURES = {
    'body':    (40, 60, (90, 160, 90), False),
    'head':    (36, 32, (120, 190, 110), True),
    'arm':     (40, 14, (80, 150, 80), False),
    'forearm': (36, 12, (70, 140, 70), False),
    'hat':     (30, 18, (200, 60, 60), False),
    'bucket':  (30, 24, (150, 150, 160), False),
    'cone':    (28, 30, (240, 140, 40), False),
    'eye':     (14, 14, (250, 250, 250), True),
    'pupil':   (6, 6, (20, 20, 30), True),
    'tail':    (40, 10, (100, 170, 90), False),
    'pad':     (18, 14, (180, 170, 60), False),
    'sword':   (60, 8, (210, 210, 230), False),
}
IDS = {name: '00000000-0000-0000-0000-%012d' % (i + 1) for i, name in enumerate(PICTURES)}

GAP = 2
x, y, row, page_w = GAP, GAP, 0, 128
placed = {}
for name, (w, h, _, _) in PICTURES.items():
    if x + w + GAP > page_w:
        x, y, row = GAP, y + row + GAP, 0
    placed[name] = (x, y)
    x += w + GAP
    row = max(row, h)
page_h = y + row + GAP
pixels = [0] * (page_w * page_h * 4)
for name, (w, h, (r, g, b), rounded) in PICTURES.items():
    px, py = placed[name]
    for j in range(h):
        for i in range(w):
            if rounded and ((i + 0.5 - w / 2) / (w / 2)) ** 2 + ((j + 0.5 - h / 2) / (h / 2)) ** 2 > 1:
                continue
            shade = 0.75 if j < 2 or j >= h - 2 or i < 2 or i >= w - 2 else 1.0
            k = ((py + j) * page_w + px + i) * 4
            pixels[k:k + 4] = [int(r * shade), int(g * shade), int(b * shade), 255]

# ---------------------------------------------------------------- 2×3 matrices (row vectors, as Harlequin)

def mul(a, b):   # a, then b
    return [a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
            a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
            a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5]]

def inv(m):
    d = m[0] * m[3] - m[1] * m[2]
    a, b, c, e = m[3] / d, -m[1] / d, -m[2] / d, m[0] / d
    return [a, b, c, e, -(m[4] * a + m[5] * c), -(m[4] * b + m[5] * e)]

def S(x, y): return [x, 0, 0, y, 0, 0]
def R(r): return [math.cos(r), math.sin(r), -math.sin(r), math.cos(r), 0, 0]
def T(x, y): return [1, 0, 0, 1, x, y]

# ---------------------------------------------------------------- the rig

def bone(name, parent, length, x=0, y=0, rot=0.0, sx=1, sy=1, **more):
    b = {'name': name, 'parent': parent, 'length': length, 'position': {'x': x, 'y': y}, 'rotation': rot,
         'scale': {'x': sx, 'y': sy}, 'inheritRotation': True, 'inheritScale': True, 'turn': 0, 'tilt': 0}
    b.update(more)
    return b

H = math.pi / 2
bones = [
    bone('root', None, 20),
    bone('body', 'root', 60, 0, 40, H),
    bone('head', 'body', 30, 60, 0, -0.1),
    bone('eye', 'head', 6, 14, 6, -H + 0.1),
    bone('pupil', 'eye', 2, 0, 0, 0, minOffset={'x': -3, 'y': -3}, maxOffset={'x': 3, 'y': 3}),
    bone('hat', 'head', 20, 28, 0, 0.2, inheritRotation=False, inheritScale=False),
    bone('arm_up', 'body', 40, 50, -6, H + 0.6),
    bone('forearm', 'arm_up', 36, 40, 0, -0.4),
    bone('pad', 'body', 10, 52, 10, H),
    bone('tail', 'root', 40, -15, 30, math.pi - 0.3),
    bone('sword', 'root', 60, 50, 10, 0.3),
    bone('hand_target', 'root', 5, -55, 70),
    bone('look_at', 'root', 5, 60, 140),
    bone('c1', 'root', 15, 30, 0, 0.2),
    bone('c2', 'c1', 15, 15, 0, 0.1, sx=1.2),
    bone('c3', 'c2', 15, 15, 0, 0.1, tilt=0.3),
    bone('chain_target', 'root', 5, 70, 10),
]

# Rest worlds (Bone.UpdateWorld; only the full-inheritance case is needed for the mesh's binds)
worlds = {}
for b in bones:
    p, s = b['position'], b['scale']
    local = mul(mul(S(s['x'] * math.cos(b['tilt']), s['y'] * math.cos(b['turn'])), R(b['rotation'])), T(p['x'], p['y']))
    worlds[b['name']] = local if b['parent'] is None else mul(local, worlds[b['parent']])

def drawing_matrix(w, h, pivot=(0.5, 0.5), offset=(0, 0), rotation=0.0, scale=(1, 1)):
    return mul(mul(mul(mul(T(-pivot[0] * w, -pivot[1] * h), S(1, -1)), S(*scale)), R(rotation)), T(*offset))

def entry(slot, name, picture, offset=(0, 0), rotation=0.0, scale=(1, 1), pivot=(0.5, 0.5), mesh=None):
    e = {'slot': slot, 'name': name, 'asset': IDS[picture], 'offset': {'x': offset[0], 'y': offset[1]},
         'rotation': rotation, 'scale': {'x': scale[0], 'y': scale[1]}, 'pivot': {'x': pivot[0], 'y': pivot[1]}}
    if mesh:
        e['mesh'] = mesh
    return e

# The forearm: a mesh of 8 points, weighted between the upper arm and the forearm
fw, fh = PICTURES['forearm'][:2]
pts = []
for j in (0, fh):
    for i in (0, 12, 24, fw):
        pts += [i, j]
tris = [0, 1, 4, 1, 5, 4, 1, 2, 5, 2, 6, 5, 2, 3, 6, 3, 7, 6]
weights = []
for k in range(8):
    i = k % 4
    share = [1.0, 0.6, 0.2, 0.0][i]
    weights.append([w for w in ({'bone': 'arm_up', 'w': share}, {'bone': 'forearm', 'w': 1 - share}) if w['w'] > 0])
fore_drawing = drawing_matrix(fw, fh, pivot=(0.0, 0.5), offset=(0, 0))
bind = mul(fore_drawing, worlds['forearm'])
mesh = {'w': fw, 'h': fh, 'points': pts, 'triangles': tris, 'weights': weights, 'bind': bind,
        'boneBind': {'arm_up': inv(worlds['arm_up']), 'forearm': inv(worlds['forearm'])}}

# A flat mesh (no weights) on the tail: drawn where the plain picture would be
tw, th = PICTURES['tail'][:2]
tail_mesh = {'w': tw, 'h': th, 'points': [0, 0, tw, 0, tw, th, 0, th, tw / 2, th / 2],
             'triangles': [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4]}

slots = [
    {'name': 'tail', 'bone': 'tail', 'attachment': 'tail'},
    {'name': 'body', 'bone': 'body', 'attachment': 'body'},
    {'name': 'arm', 'bone': 'arm_up', 'attachment': 'arm'},
    {'name': 'forearm', 'bone': 'forearm', 'attachment': 'forearm'},
    {'name': 'pad', 'bone': 'pad', 'attachment': 'pad', 'color': {'r': 1, 'g': 0.8, 'b': 0.6}, 'opacity': 0.9},
    {'name': 'head', 'bone': 'head', 'attachment': 'head'},
    {'name': 'eye', 'bone': 'eye', 'attachment': 'eye', 'mask': {'until': 'pupil', 'shows': True}},
    {'name': 'pupil', 'bone': 'pupil', 'attachment': 'pupil'},
    {'name': 'hat', 'bone': 'hat', 'attachment': 'hat', 'blend': 'Add'},
    {'name': 'sword', 'bone': 'sword', 'attachment': 'sword', 'blend': 'Multiply'},
]
default = [
    entry('tail', 'tail', 'tail', offset=(20, 0), mesh=tail_mesh),
    entry('body', 'body', 'body', offset=(30, 0), rotation=-H),
    entry('arm', 'arm', 'arm', offset=(20, 0), pivot=(0.5, 0.5)),
    entry('forearm', 'forearm', 'forearm', pivot=(0.0, 0.5), mesh=mesh),
    entry('pad', 'pad', 'pad', offset=(4, 0)),
    entry('head', 'head', 'head', offset=(14, 0), rotation=-H),
    entry('eye', 'eye', 'eye'),
    entry('pupil', 'pupil', 'pupil'),
    entry('hat', 'hat', 'hat', offset=(4, 0), rotation=-0.2, scale=(1.1, 0.9)),
    entry('hat', 'hat_tilted', 'hat', offset=(4, 2), rotation=0.3),
    entry('sword', 'sword', 'sword', offset=(30, 0)),
]
skins = [
    {'name': 'default', 'overlay': False, 'entries': default},
    {'name': 'bucket', 'category': 'Hat', 'overlay': False, 'entries': [entry('hat', 'hat', 'bucket', offset=(6, 0))]},
    {'name': 'cone', 'category': 'Hat', 'overlay': False, 'entries': [entry('hat', 'hat', 'cone', offset=(8, 0), rotation=-0.1)]},
    {'name': 'glow', 'overlay': True, 'colorLayers': ['Glow'], 'entries': [entry('sword', 'sword', 'sword', offset=(30, 2), scale=(1.2, 1.2))]},
]

def values(**v):
    order = ['mix', 'hue', 'saturation', 'lightness', 'brightness', 'contrast', 'tr', 'tg', 'tb', 'tint',
             'sr', 'sg', 'sb', 'mr', 'mg', 'mb', 'hr', 'hg', 'hb', 'fr', 'fg', 'fb', 'or', 'og', 'ob', 'tol', 'soft']
    d = [1, 0, 0, 0, 0, 0, 1, 0.55, 0.2, 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0.2, 0.8, 0.3, 0.15, 0.15]
    for k, x in v.items():
        d[order.index(k)] = x
    return d

color_layers = [
    {'name': 'Green', 'visible': True, 'adjust': {'kind': 'HueSaturation', 'values': values(hue=40, saturation=0.3, lightness=0.05)},
     'leaveOut': ['pupil', 'eye']},
    {'name': 'Glow', 'visible': True, 'adjust': {'kind': 'Tint', 'keepLight': False, 'values': values(tr=1, tg=0.9, tb=0.3, tint=0.3)}},
    {'name': 'Balance', 'visible': True, 'adjust': {'kind': 'ColorBalance', 'values': values(mix=0.5, sr=0.2, mb=-0.1, hg=0.1)}},
]

def key(t, curve='Linear', b=None, **v):
    k = {'t': t, 'curve': curve}
    if b:
        k['b'] = b
    k.update(v)
    return k

def tl(kind, target, *keys):
    return {'type': kind, 'target': target, 'keys': list(keys)}

walk = {'name': 'walk', 'duration': 1.0, 'loop': True, 'timelines': [
    tl('events', 'events', key(0, 'Stepped', name='step', data='left'), key(0.5, 'Stepped', name='step', data='right', x=0.8, y=1.2),
       key(0.75, 'Stepped', name='swing')),
    tl('drawOrder', 'draw order', key(0.4, 'Stepped', order=['tail', 'body', 'forearm', 'arm', 'pad', 'head', 'eye', 'pupil', 'hat', 'sword']),
       key(0.8, 'Stepped', order=['sword', 'tail', 'body', 'arm', 'forearm', 'pad', 'head', 'eye', 'pupil', 'hat'])),
    tl('rotate', 'body', key(0, 'Smooth', x=0), key(0.5, 'Bezier', b=[0.34, 1.56, 0.64, 1], x=0.15), key(1, x=0)),
    tl('translate', 'root', key(0, x=0, y=0), key(0.25, 'Smooth', x=4, y=6), key(0.5, x=0, y=0), key(0.75, 'Bezier', b=[0.42, 0, 0.58, 1], x=-4, y=6), key(1, x=0, y=0)),
    tl('scale', 'head', key(0, x=1, y=1), key(0.5, 'Stepped', x=1.1, y=0.95), key(0.7, x=1, y=1)),
    tl('depth', 'head', key(0, x=0, y=0), key(0.5, x=0.4, y=0.6), key(1, x=0, y=0)),
    tl('rotate', 'arm_up', key(0, x=0.3), key(0.5, x=-0.4), key(1, x=0.3)),
    tl('translate', 'hand_target', key(0, x=0, y=0), key(0.5, x=20, y=-15), key(1, x=0, y=0)),
    tl('ikMix', 'arm', key(0, x=1), key(0.6, x=0.3), key(0.9, x=1)),
    tl('translate', 'look_at', key(0, x=0, y=0), key(0.5, x=-120, y=-60), key(1, x=0, y=0)),
    tl('translate', 'chain_target', key(0, x=0, y=0), key(0.5, x=-10, y=25)),
    tl('rotate', 'tail', key(0, x=0), key(0.3, x=0.5), key(0.6, x=-0.3)),
    tl('space', 'sword', key(0.25, 'Stepped', name='forearm'), key(0.75, 'Stepped', name=''), key(0.9, 'Stepped', name=None)),
    tl('attachment', 'hat', key(0.3, 'Stepped', name='hat_tilted'), key(0.6, 'Stepped', name='hat'), key(0.85, 'Stepped', name=None)),
    tl('partTranslate', 'pad', key(0, x=0, y=0), key(0.5, x=3, y=-2)),
    tl('partRotate', 'pad', key(0, x=0), key(0.5, 'Smooth', x=0.5)),
    tl('partScale', 'pad', key(0, x=1, y=1), key(0.5, x=1.3, y=0.8)),
    tl('partColor', 'pad', key(0, x=1, y=0.8, z=0.6), key(0.5, x=0.4, y=1, z=1)),
    tl('partOpacity', 'pad', key(0, x=0.9), key(0.5, x=0.3)),
    tl('deform', 'forearm', key(0.1, name='forearm', v=[0] * 16), key(0.6, name='forearm', v=[0, 0, 0, -3, 0, -4, 2, -2, 0, 0, 0, 3, 0, 4, 2, 2])),
    tl('colorLayer', 'Green', key(0, v=values(hue=40, saturation=0.3, lightness=0.05)), key(0.5, v=values(hue=-30, saturation=-0.2, lightness=-0.1))),
    tl('hitbox', 'swipe', key(0.4, 'Stepped', x=1), key(0.6, 'Stepped', x=0)),
]}
idle = {'name': 'idle', 'duration': 1.5, 'loop': False, 'timelines': [
    tl('rotate', 'body', key(0, x=0), key(1.5, 'Smooth', x=-0.1)),
    tl('rotate', 'head', key(0, x=0), key(0.75, x=0.2), key(1.5, x=0)),
    tl('translate', 'look_at', key(0, x=-80, y=-100)),
    tl('attachment', 'hat', key(0, 'Stepped', name='hat_tilted')),
    tl('events', 'events', key(1.0, 'Stepped', name='blink')),
], 'sound': {'path': 'sounds/idle.wav', 'offset': 0.2, 'volume': 0.7}}

rig = {
    'bones': bones, 'slots': slots, 'assets': [], 'skins': skins, 'shownSkins': ['cone'],
    'ikConstraints': [
        {'name': 'arm', 'bones': ['arm_up', 'forearm'], 'target': 'hand_target', 'mix': 1, 'bend': -1, 'iterations': 15},
        {'name': 'chain', 'bones': ['c1', 'c2', 'c3'], 'target': 'chain_target', 'mix': 0.8, 'bend': 1, 'iterations': 10},
    ],
    'lookConstraints': [{'name': 'look', 'bone': 'pupil', 'target': 'look_at', 'shape': 'Ellipse', 'radius': {'x': 3, 'y': 2}, 'follow': 0.05, 'mix': 1}],
    'copyConstraints': [{'name': 'pad', 'bone': 'pad', 'source': 'arm_up', 'local': False, 'rotate': 0.7, 'move': 0.5, 'scale': 0,
                         'rotationOffset': 0.4, 'positionOffset': {'x': 4, 'y': 6}, 'scaleOffset': {'x': 1, 'y': 1}, 'mix': 1},
                        {'name': 'tail_copy', 'bone': 'c3', 'source': 'tail', 'local': True, 'rotate': 0.5, 'move': 1, 'scale': 1, 'mix': 0.5}],
    'jiggleConstraints': [{'name': 'tail', 'bone': 'tail', 'stiffness': 60, 'damping': 5, 'gravity': 200, 'mix': 1}],
    'mixes': {'default': 0.25, 'pairs': [{'from': 'walk', 'to': 'idle', 'duration': 0.1}]},
    'colorLayers': color_layers,
    'hitboxes': [{'name': 'body', 'bone': 'body', 'shape': 'box', 'offset': {'x': 30, 'y': 0}, 'rotation': 0.1, 'size': {'x': 60, 'y': 36}, 'kind': 'hurt', 'active': True},
                 {'name': 'swipe', 'bone': 'forearm', 'shape': 'circle', 'offset': {'x': 34, 'y': 0}, 'rotation': 0, 'size': {'x': 24, 'y': 24}, 'kind': 'hit', 'active': False}],
    'points': [{'name': 'hand', 'bone': 'forearm', 'offset': {'x': 36, 'y': 0}, 'rotation': 0.2}],
    'animations': [walk, idle],
}

regions = {IDS[n]: {'page': 0, 'x': placed[n][0], 'y': placed[n][1], 'w': PICTURES[n][0], 'h': PICTURES[n][1],
                    'offsetX': 0, 'offsetY': 0, 'width': PICTURES[n][0], 'height': PICTURES[n][1]} for n in PICTURES}
# One picture trimmed (the sword's see-through edges): only its middle is on the page
sw = regions[IDS['sword']]
sw.update({'x': sw['x'] + 2, 'y': sw['y'] + 1, 'w': sw['w'] - 4, 'h': sw['h'] - 2, 'offsetX': 2, 'offsetY': 1})

engine = {'format': 'harlequin-rig', 'version': 1, 'object': 'sample', 'fps': 30,
          'atlas': {'pages': [{'file': 'sample.atlas.png', 'width': page_w, 'height': page_h}], 'regions': regions},
          'rig': rig, 'sounds': {'step': 'sounds/step.wav'}}

os.makedirs('demo', exist_ok=True)
png('demo/sample.atlas.png', page_w, page_h, pixels)
with open('demo/sample.hqrig.json', 'w') as f:
    json.dump(engine, f, indent=1)
print('demo/sample.hqrig.json, demo/sample.atlas.png (%d × %d)' % (page_w, page_h))
