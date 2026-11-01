@tool
class_name HarlequinRigData
extends RefCounted
## A Harlequin engine export (name.hqrig.json and its atlas pages), read once and shared by every
## HarlequinRig showing it. The format: docs/engine-format.md in the Harlequin repository.
##
## Everything is kept in Harlequin's own space (y up, angles counter-clockwise): HarlequinRig
## turns it the right way up for Godot when it draws.

const FORMAT := "harlequin-rig"
const SUPPORTED_VERSION := 1

enum Interp { LINEAR, STEPPED, SMOOTH, BEZIER }
enum Blend { NORMAL, ADD, MULTIPLY, SCREEN }

var path := ""
var folder := ""
var object_name := ""
var fps := 30
var version := 1

## Atlas pages (Texture2D) and, by picture (asset) id, where each picture is on them:
## { page, x, y, w, h, offset_x, offset_y, width, height }
var pages: Array[Texture2D] = []
var regions := {}

## Bones, parents before children: { name, parent (index, -1 = none), children (indices), length,
## position, rotation, scale, inherit_rotation, inherit_scale, turn, tilt, min_offset, max_offset }
var bones: Array[Dictionary] = []
var bone_index := {}

## Parts, back to front: { name, bone, attachment (String or null), blend, color (Vector3),
## opacity, is_mask, mask_until (index, -1 = all the rest), mask_shows }
var slots: Array[Dictionary] = []
var slot_index := {}

## Skins: [0] is "default". { name, category, overlay, color_layers (names),
## entries: { part name: { drawing name: drawing } } }; a drawing: { name, asset, offset, rotation,
## scale, pivot, mesh }; a mesh: { w, h, points, triangles, weights, bind, bone_bind }
var skins: Array[Dictionary] = []
var skin_index := {}
var shown_skins: Array[String] = []

var ik_constraints: Array[Dictionary] = []
var copy_constraints: Array[Dictionary] = []
var look_constraints: Array[Dictionary] = []
var jiggle_constraints: Array[Dictionary] = []
var hitboxes: Array[Dictionary] = []
var hitbox_index := {}
var points: Array[Dictionary] = []
var point_index := {}
var color_layers: Array[Dictionary] = []
var color_layer_index := {}

## Animations by name: { name, duration, loop, timelines, sound }; a timeline: { type, target
## (an index, or -1 when it names nothing there), keys }; a key: { t, curve, b (Bezier handles), v }
var animations := {}
var animation_names: Array[String] = []

var mix_default := 0.2
var mix_pairs := {}   # "from\nto" -> seconds
var sounds := {}      # event name -> path (relative to the rig file)

var error := ""       # why it couldn't be read ("" = it could)

static var _cache := {}


## The rig at this path (read once; `reload` reads it again)
static func open(file: String, reload := false) -> HarlequinRigData:
	if not reload and _cache.has(file):
		return _cache[file]
	var data := HarlequinRigData.new()
	data._read(file)
	if data.error == "":
		_cache[file] = data
	return data


static func forget(file: String) -> void:
	_cache.erase(file)


func _read(file: String) -> void:
	path = file
	folder = file.get_base_dir()
	var text := FileAccess.get_file_as_string(file)
	if text == "":
		error = "Can't read %s (%s)" % [file, error_string(FileAccess.get_open_error())]
		return
	var json = JSON.parse_string(text)
	if typeof(json) != TYPE_DICTIONARY:
		error = "%s isn't JSON" % file
		return
	if json.get("format", "") != FORMAT:
		error = "%s isn't a Harlequin rig (format \"%s\")" % [file, json.get("format", "")]
		return
	version = int(json.get("version", 1))
	if version > SUPPORTED_VERSION:
		push_warning("Harlequin: %s is version %d; this plugin knows version %d. It may not play right." % [file, version, SUPPORTED_VERSION])
	object_name = str(json.get("object", ""))
	fps = int(json.get("fps", 30))
	sounds = _dict(json.get("sounds"))
	_read_atlas(_dict(json.get("atlas")))
	_read_rig(_dict(json.get("rig")))


# ---------------------------------------------------------------- atlas

func _read_atlas(atlas: Dictionary) -> void:
	for page in _list(atlas.get("pages")):
		pages.append(load_texture(folder.path_join(str(page.get("file", "")))))
	var found := _dict(atlas.get("regions"))
	for id in found:
		var r: Dictionary = found[id]
		regions[str(id).to_lower()] = {
			"page": int(r.get("page", 0)),
			"x": int(r.get("x", 0)), "y": int(r.get("y", 0)),
			"w": int(r.get("w", 0)), "h": int(r.get("h", 0)),
			"offset_x": int(r.get("offsetX", 0)), "offset_y": int(r.get("offsetY", 0)),
			"width": int(r.get("width", 0)), "height": int(r.get("height", 0)),
		}


## A picture file: imported by Godot when it is (res://), else read as it is (user://, a folder)
static func load_texture(file: String) -> Texture2D:
	if ResourceLoader.exists(file):
		var res = load(file)
		if res is Texture2D:
			return res
	var image := Image.load_from_file(file)
	if image == null or image.is_empty():
		push_error("Harlequin: can't load the atlas page %s" % file)
		return null
	return ImageTexture.create_from_image(image)


# ---------------------------------------------------------------- the rig

func _read_rig(rig: Dictionary) -> void:
	for b in _list(rig.get("bones")):
		var bone := {
			"name": str(b.get("name", "")),
			"parent": -1,
			"children": [],
			"length": _f(b.get("length"), 0.0),
			"position": _vec(b.get("position"), Vector2.ZERO),
			"rotation": _f(b.get("rotation"), 0.0),
			"scale": _vec(b.get("scale"), Vector2.ONE),
			"inherit_rotation": _bool(b.get("inheritRotation"), true),
			"inherit_scale": _bool(b.get("inheritScale"), true),
			"turn": _f(b.get("turn"), 0.0),
			"tilt": _f(b.get("tilt"), 0.0),
			"min_offset": _vec_or_null(b.get("minOffset")),
			"max_offset": _vec_or_null(b.get("maxOffset")),
		}
		var parent = b.get("parent")
		if parent != null:
			bone.parent = bone_index.get(str(parent), -1)
			if bone.parent >= 0:
				bones[bone.parent].children.append(bones.size())
		bone_index[bone.name] = bones.size()
		bones.append(bone)

	for s in _list(rig.get("slots")):
		var slot := {
			"name": str(s.get("name", "")),
			"bone": bone_index.get(str(s.get("bone", "")), -1),
			"attachment": s.get("attachment"),
			"blend": _blend(s.get("blend")),
			"color": _rgb(s.get("color"), Vector3.ONE),
			"opacity": _f(s.get("opacity"), 1.0),
			"is_mask": false, "mask_until": -1, "mask_shows": false,
		}
		slot_index[slot.name] = slots.size()
		slots.append(slot)
	var i := 0
	for s in _list(rig.get("slots")):   # (masks name other parts: once they're all there)
		var mask = s.get("mask")
		if mask is Dictionary:
			slots[i].is_mask = true
			var until = mask.get("until")
			slots[i].mask_until = -1 if until == null else slot_index.get(str(until), i)
			slots[i].mask_shows = _bool(mask.get("shows"), false)
		i += 1

	for k in _list(rig.get("skins")):
		var skin := {
			"name": str(k.get("name", "")),
			"category": str(k.get("category", "") if k.get("category") != null else "").strip_edges(),
			"overlay": _bool(k.get("overlay"), false),
			"color_layers": _strings(k.get("colorLayers")),
			"entries": {},
		}
		for e in _list(k.get("entries")):
			var drawing := {
				"name": str(e.get("name", "")),
				"asset": str(e.get("asset", "")).to_lower(),
				"offset": _vec(e.get("offset"), Vector2.ZERO),
				"rotation": _f(e.get("rotation"), 0.0),
				"scale": _vec(e.get("scale"), Vector2.ONE),
				"pivot": _vec(e.get("pivot"), Vector2(0.5, 0.5)),
				"mesh": _mesh(e.get("mesh")),
			}
			var part := str(e.get("slot", ""))
			if not skin.entries.has(part):
				skin.entries[part] = {}
			skin.entries[part][drawing.name] = drawing
		skin_index[skin.name] = skins.size()
		skins.append(skin)
	if skins.is_empty() or skins[0].name != "default":
		var own := {"name": "default", "category": "", "overlay": false, "color_layers": [], "entries": {}}
		if skin_index.has("default"):
			own = skins[skin_index["default"]]
			skins.erase(own)
		skins.insert(0, own)
		skin_index.clear()
		for n in skins.size():
			skin_index[skins[n].name] = n
	var shown = rig.get("shownSkins")
	if shown == null and rig.get("activeSkin") != null:
		shown = [rig.get("activeSkin")]
	shown_skins.assign(_strings(shown))

	for c in _list(rig.get("ikConstraints")):
		var chain: Array[int] = []
		for n in _list(c.get("bones")):
			chain.append(bone_index.get(str(n), -1))
		if chain.has(-1) or chain.is_empty():
			continue
		ik_constraints.append({
			"name": str(c.get("name", "")), "bones": chain,
			"target": bone_index.get(str(c.get("target", "")), -1),
			"mix": _f(c.get("mix"), 1.0), "bend": int(_f(c.get("bend"), 1.0)),
			"iterations": int(_f(c.get("iterations"), 15.0)),
		})
	for c in _list(rig.get("copyConstraints")):
		copy_constraints.append({
			"name": str(c.get("name", "")),
			"bone": bone_index.get(str(c.get("bone", "")), -1),
			"source": bone_index.get(str(c.get("source", "")), -1),
			"local": _bool(c.get("local"), false),
			"rotate": _f(c.get("rotate"), 1.0), "move": _f(c.get("move"), 0.0), "scale": _f(c.get("scale"), 0.0),
			"rotation_offset": _f(c.get("rotationOffset"), 0.0),
			"position_offset": _vec(c.get("positionOffset"), Vector2.ZERO),
			"scale_offset": _vec(c.get("scaleOffset"), Vector2.ONE),
			"mix": _f(c.get("mix"), 1.0),
		})
	for c in _list(rig.get("lookConstraints")):
		look_constraints.append({
			"name": str(c.get("name", "")),
			"bone": bone_index.get(str(c.get("bone", "")), -1),
			"target": bone_index.get(str(c.get("target", "")), -1),
			"rectangle": str(c.get("shape", "Ellipse")) == "Rectangle",
			"radius": _vec(c.get("radius"), Vector2(10, 6)),
			"follow": _f(c.get("follow"), 0.25), "mix": _f(c.get("mix"), 1.0),
		})
	for c in _list(rig.get("jiggleConstraints")):
		jiggle_constraints.append({
			"name": str(c.get("name", "")),
			"bone": bone_index.get(str(c.get("bone", "")), -1),
			"stiffness": _f(c.get("stiffness"), 80.0), "damping": _f(c.get("damping"), 6.0),
			"gravity": _f(c.get("gravity"), 0.0), "mix": _f(c.get("mix"), 1.0),
		})
	# (constraints on bones that aren't there are left out)
	copy_constraints = copy_constraints.filter(func(c): return c.bone >= 0 and c.source >= 0)
	look_constraints = look_constraints.filter(func(c): return c.bone >= 0 and c.target >= 0)
	jiggle_constraints = jiggle_constraints.filter(func(c): return c.bone >= 0)
	ik_constraints = ik_constraints.filter(func(c): return c.target >= 0)

	for h in _list(rig.get("hitboxes")):
		var box := {
			"name": str(h.get("name", "")), "bone": bone_index.get(str(h.get("bone", "")), -1),
			"circle": str(h.get("shape", "box")) == "circle",
			"offset": _vec(h.get("offset"), Vector2.ZERO), "rotation": _f(h.get("rotation"), 0.0),
			"size": _vec(h.get("size"), Vector2(40, 40)), "kind": str(h.get("kind", "hurt")),
			"active": _bool(h.get("active"), true),
		}
		if box.bone >= 0:
			hitbox_index[box.name] = hitboxes.size()
			hitboxes.append(box)
	for p in _list(rig.get("points")):
		var point := {
			"name": str(p.get("name", "")), "bone": bone_index.get(str(p.get("bone", "")), -1),
			"offset": _vec(p.get("offset"), Vector2.ZERO), "rotation": _f(p.get("rotation"), 0.0),
		}
		if point.bone >= 0:
			point_index[point.name] = points.size()
			points.append(point)

	for l in _list(rig.get("colorLayers")):
		var adjust := _dict(l.get("adjust"))
		var leave := {}
		for n in _strings(l.get("leaveOut")):
			if slot_index.has(n):
				leave[slot_index[n]] = true
		var layer := {
			"name": str(l.get("name", "")), "visible": _bool(l.get("visible"), true),
			"kind": _adjust_kind(adjust.get("kind")),
			"colorize": _bool(adjust.get("colorize"), false),
			"keep_light": _bool(adjust.get("keepLight"), true),
			"values": adjust_values(adjust.get("values")),
			"leave_out": leave,
		}
		color_layer_index[layer.name] = color_layers.size()
		color_layers.append(layer)

	var mixes := _dict(rig.get("mixes"))
	mix_default = _f(mixes.get("default"), 0.2)
	for p in _list(mixes.get("pairs")):
		mix_pairs[str(p.get("from", "")) + "\n" + str(p.get("to", ""))] = _f(p.get("duration"), mix_default)

	for a in _list(rig.get("animations")):
		var animation := {
			"name": str(a.get("name", "")),
			"duration": _f(a.get("duration"), 0.0),
			"loop": _bool(a.get("loop"), false),
			"timelines": [],
			"sound": null,
		}
		var sound = a.get("sound")
		if sound is Dictionary and str(sound.get("path", "")) != "":
			animation.sound = {"path": str(sound.path), "offset": _f(sound.get("offset"), 0.0), "volume": _f(sound.get("volume"), 1.0)}
		for t in _list(a.get("timelines")):
			var timeline := _timeline(t)
			if not timeline.is_empty():
				animation.timelines.append(timeline)
		animations[animation.name] = animation
		animation_names.append(animation.name)


func _mesh(m) -> Variant:
	if not (m is Dictionary):
		return null
	var raw := _floats(m.get("points"))
	var pts := PackedVector2Array()
	for i in range(0, raw.size() - 1, 2):
		pts.append(Vector2(raw[i], raw[i + 1]))
	var tris := PackedInt32Array()
	for v in _list(m.get("triangles")):
		tris.append(int(v))
	var weights = null
	if m.get("weights") is Array:
		weights = []
		for point_weights in m.weights:
			var list := []
			for w in _list(point_weights):
				var bone: int = bone_index.get(str(w.get("bone", "")), -1)
				if bone >= 0:
					list.append([bone, _f(w.get("w"), 0.0)])
			weights.append(list)
		if weights.size() != pts.size():
			weights = null
	var bone_bind := {}
	var binds := _dict(m.get("boneBind"))
	for n in binds:
		if bone_index.has(str(n)):
			bone_bind[bone_index[str(n)]] = matrix(binds[n])
	return {
		"w": int(_f(m.get("w"), 0.0)), "h": int(_f(m.get("h"), 0.0)),
		"points": pts, "triangles": tris, "weights": weights,
		"bind": matrix(m.get("bind")), "bone_bind": bone_bind,
	}


func _timeline(t: Dictionary) -> Dictionary:
	var type := str(t.get("type", ""))
	var target_name := str(t.get("target", ""))
	var target := -1
	match type:
		"rotate", "translate", "scale", "depth", "space":
			target = bone_index.get(target_name, -1)
		"attachment", "partTranslate", "partRotate", "partScale", "partColor", "partOpacity", "deform":
			target = slot_index.get(target_name, -1)
		"ikMix":
			for n in ik_constraints.size():
				if ik_constraints[n].name == target_name:
					target = n
		"colorLayer":
			target = color_layer_index.get(target_name, -1)
		"hitbox":
			target = hitbox_index.get(target_name, -1)
		"events", "drawOrder":
			target = 0
		_:
			return {}   # (layerAdjust: baked into the pictures, or left out; anything newer: skipped)
	if target < 0:
		return {}
	var keys := []
	for k in _list(t.get("keys")):
		var key := {"t": _f(k.get("t"), 0.0), "curve": _curve(k.get("curve")), "b": null, "v": null}
		var b := _floats(k.get("b"))
		if key.curve == Interp.BEZIER and b.size() >= 4:
			key.b = [clampf(b[0], 0.0, 1.0), b[1], clampf(b[2], 0.0, 1.0), b[3]]
		elif key.curve == Interp.BEZIER:
			key.curve = Interp.LINEAR
		var x := _f(k.get("x"), 0.0)
		var y := _f(k.get("y"), 0.0)
		match type:
			"rotate", "ikMix", "partRotate", "partOpacity":
				key.v = x
			"translate", "scale", "depth", "partTranslate", "partScale":
				key.v = Vector2(x, y)
			"partColor":
				key.v = Vector3(x, y, _f(k.get("z"), 0.0))
			"attachment", "space":
				key.v = k.get("name")
			"events":
				key.v = {"name": str(k.get("name", "")), "data": k.get("data"),
						 "volume": _f(k.get("x"), 1.0), "pitch": _f(k.get("y"), 1.0)}
			"drawOrder":
				var order := PackedInt32Array()
				for n in _strings(k.get("order")):
					if slot_index.has(n):
						order.append(slot_index[n])
				key.v = order
			"colorLayer":
				key.v = adjust_values(k.get("v"))
			"deform":
				var raw := _floats(k.get("v"))
				var offsets := PackedVector2Array()
				for i in range(0, raw.size() - 1, 2):
					offsets.append(Vector2(raw[i], raw[i + 1]))
				key.v = {"drawing": str(k.get("name", "")), "offsets": offsets}
			"hitbox":
				key.v = x > 0.5
				key.curve = Interp.STEPPED
		keys.append(key)
	keys.sort_custom(func(a, b): return a.t < b.t)
	return {"type": type, "target": target, "keys": keys}


# ---------------------------------------------------------------- helpers

## A 2×3 matrix as Harlequin writes it (M11 M12 M21 M22 M31 M32: row vectors) as a Transform2D
## (the same numbers: x = (M11, M12), y = (M21, M22), origin = (M31, M32))
static func matrix(v) -> Transform2D:
	var m := _floats(v)
	if m.size() < 6:
		return Transform2D.IDENTITY
	return Transform2D(Vector2(m[0], m[1]), Vector2(m[2], m[3]), Vector2(m[4], m[5]))


## A colour layer's 27 numbers (missing ones: Harlequin's defaults)
static func adjust_values(v) -> PackedFloat32Array:
	var d := PackedFloat32Array([1, 0, 0, 0, 0, 0, 1, 0.55, 0.2, 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0.2, 0.8, 0.3, 0.15, 0.15])
	var given := _floats(v)
	for i in mini(given.size(), d.size()):
		if is_finite(given[i]):
			d[i] = given[i]
	return d


static func _dict(v) -> Dictionary:
	return v if v is Dictionary else {}


static func _list(v) -> Array:
	return v if v is Array else []


static func _floats(v) -> PackedFloat32Array:
	var out := PackedFloat32Array()
	if v is Array:
		for x in v:
			out.append(float(x) if (x is float or x is int) else 0.0)
	return out


static func _strings(v) -> Array[String]:
	var out: Array[String] = []
	if v is Array:
		for x in v:
			if x != null:
				out.append(str(x))
	return out


static func _f(v, default: float) -> float:
	return float(v) if (v is float or v is int) else default


static func _bool(v, default: bool) -> bool:
	return bool(v) if v is bool else default


static func _vec(v, default: Vector2) -> Vector2:
	if v is Dictionary:
		return Vector2(_f(v.get("x"), default.x), _f(v.get("y"), default.y))
	return default


static func _vec_or_null(v) -> Variant:
	return _vec(v, Vector2.ZERO) if v is Dictionary else null


static func _rgb(v, default: Vector3) -> Vector3:
	if v is Dictionary:
		return Vector3(_f(v.get("r"), 1.0), _f(v.get("g"), 1.0), _f(v.get("b"), 1.0))
	return default


static func _curve(v) -> int:
	match str(v):
		"Stepped", "1": return Interp.STEPPED
		"Smooth", "2": return Interp.SMOOTH
		"Bezier", "3": return Interp.BEZIER
	return Interp.LINEAR


static func _blend(v) -> int:
	match str(v):
		"Add", "1": return Blend.ADD
		"Multiply", "2": return Blend.MULTIPLY
		"Screen", "3": return Blend.SCREEN
	return Blend.NORMAL


static func _adjust_kind(v) -> int:
	match str(v):
		"BrightnessContrast", "1": return 1
		"Tint", "2": return 2
		"ColorBalance", "3": return 3
		"ReplaceColor", "4": return 4
	return 0
