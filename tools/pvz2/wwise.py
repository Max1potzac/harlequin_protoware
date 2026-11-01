"""Reading Wwise soundbanks (.bnk), as PvZ2 ships them (bank version 140; others are tried).

What's kept: each event, the Play actions it runs, and every audio file (media id) that those can
end up playing, through random, sequence, switch and blend containers. Event names aren't in the
banks: only their ids, the FNV-1 hash of the name in lower case (see event_id). Names are found by
hashing candidate strings (see harvest_names) and keeping the ones that come out as real events.
"""
import re
import struct

PLAY = 0x0403   # an action that plays its target

SOUND, ACTION, EVENT = 2, 3, 4
CONTAINERS = (5, 6, 7, 9)   # random/sequence, switch, actor-mixer, blend (layer) containers


def event_id(name):
    """Wwise's id for a name: 32-bit FNV-1 of its lower-case bytes"""
    h = 2166136261
    for c in name.lower().encode('utf-8'):
        h = (h * 16777619) & 0xFFFFFFFF
        h ^= c
    return h


def _chunks(data):
    pos = 0
    while pos + 8 <= len(data):
        tag = data[pos:pos + 4].decode('latin-1')
        size = struct.unpack_from('<I', data, pos + 4)[0]
        yield tag, data[pos + 8:pos + 8 + size]
        pos += 8 + size


def _var_uint(data, pos):
    """A count written 7 bits at a time (banks from version 134 on)"""
    value, shift = 0, 0
    while True:
        c = data[pos]
        pos += 1
        value |= (c & 0x7F) << shift
        shift += 7
        if c < 0x80:
            return value, pos


class Bank:
    """One .bnk: its version, its own audio (DIDX: media ids) and its objects (HIRC)"""

    def __init__(self, path):
        self.path = path
        self.version = 0
        self.bank_id = 0
        self.media = []
        self.objects = {}   # id -> (type, body)
        with open(path, 'rb') as f:
            data = f.read()
        for tag, chunk in _chunks(data):
            if tag == 'BKHD' and len(chunk) >= 8:
                self.version, self.bank_id = struct.unpack_from('<II', chunk, 0)
            elif tag == 'DIDX':
                self.media = [struct.unpack_from('<I', chunk, i)[0] for i in range(0, len(chunk) - 11, 12)]
            elif tag == 'HIRC':
                count = struct.unpack_from('<I', chunk, 0)[0]
                pos = 4
                for _ in range(count):
                    if pos + 5 > len(chunk):
                        break
                    kind = chunk[pos]
                    size = struct.unpack_from('<I', chunk, pos + 1)[0]
                    body = chunk[pos + 5:pos + 5 + size]
                    if len(body) >= 4:
                        self.objects[struct.unpack_from('<I', body, 0)[0]] = (kind, body)
                    pos += 5 + size


class SoundGraph:
    """Every bank's objects together: which events play which media"""

    def __init__(self, banks):
        self.objects = {}
        self.media_bank = {}   # media id -> the bank file holding its audio
        self.warnings = []
        for bank in banks:
            if bank.version and bank.version != 140:
                self.warnings.append('%s is bank version %d (this was made for 140): containers may not resolve'
                                     % (bank.path, bank.version))
            self.objects.update(bank.objects)
            for m in bank.media:
                self.media_bank.setdefault(m, bank.path)
        self.children = {}
        for oid, (kind, body) in self.objects.items():
            if kind == SOUND or kind in CONTAINERS:
                parent = self._parent(kind, body)
                if parent:
                    self.children.setdefault(parent, []).append(oid)
        self.events = {}
        for oid, (kind, body) in self.objects.items():
            if kind == EVENT:
                try:
                    count, pos = _var_uint(body, 4)
                    self.events[oid] = [struct.unpack_from('<I', body, pos + 4 * k)[0] for k in range(count)]
                except (IndexError, struct.error):
                    continue
        self._media_cache = {}

    @staticmethod
    def _parent(kind, body):
        """The object's direct parent (NodeBaseParams, bank version 140); 0: none, or unreadable"""
        try:
            pos = 4
            if kind == SOUND:
                plugin = struct.unpack_from('<I', body, pos)[0]
                pos += 14   # plugin, stream type, source id, in-memory size, source bits
                if plugin & 0xF == 2:   # (a source plugin with parameters)
                    pos += 4 + struct.unpack_from('<I', body, pos)[0]
            pos += 1
            fx = body[pos]
            pos += 1
            if fx:
                pos += 1 + fx * 6
            pos += 1
            meta = body[pos]
            pos += 1 + meta * 6
            pos += 1
            return struct.unpack_from('<I', body, pos + 4)[0]
        except (IndexError, struct.error):
            return 0

    def media_under(self, oid, depth=0):
        """Every audio file an object can play (all of a container's children, every switch state)"""
        if oid in self._media_cache:
            return self._media_cache[oid]
        found = set()
        entry = self.objects.get(oid)
        if entry is not None and depth < 32:
            kind, body = entry
            if kind == SOUND and len(body) >= 13:
                found.add(struct.unpack_from('<I', body, 9)[0])
            else:
                for child in self.children.get(oid, []):
                    found |= self.media_under(child, depth + 1)
        self._media_cache[oid] = found
        return found

    def event_media(self, eid):
        """The audio files an event can play (its Play actions' targets)"""
        found = set()
        for action in self.events.get(eid, []):
            entry = self.objects.get(action)
            if entry is None or entry[0] != ACTION or len(entry[1]) < 10:
                continue
            kind, target = struct.unpack_from('<HI', entry[1], 4)
            if kind == PLAY:
                found |= self.media_under(target)
        return found


NAME = re.compile(rb'[A-Za-z_][A-Za-z0-9_]{2,127}')


def harvest_names(paths, extra=()):
    """Every name-like string in these files (JSON, text or binary: a game library works too)"""
    names = set(extra)
    for path in paths:
        with open(path, 'rb') as f:
            data = f.read()
        for m in NAME.finditer(data):
            names.add(m.group().decode('ascii'))
    return names


def name_events(graph, candidates):
    """Event id -> name, for the candidates that hash to real events"""
    named = {}
    for name in candidates:
        h = event_id(name)
        if h in graph.events:
            named.setdefault(h, name)
    return named
