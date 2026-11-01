@tool
@icon("res://addons/harlequin/icon.svg")
class_name HarlequinRig
extends Node2D
## Plays a Harlequin engine export (name.hqrig.json and its atlas pages) as Harlequin does:
## bones, parts, skins, meshes, masks, constraints, jiggle, colour layers, events and sounds.
##
## Pick the rig file and an animation in the inspector; it previews in the editor. From code:
## [codeblock]
## $Zombie.play("walk")                 # fades from what was playing, as the rig's mixer says
## $Zombie.play("eat", 0.1)             # or over 0.1 seconds
## $Zombie.set_track(1, "shoot")        # a second animation over the first (arms over legs)
## $Zombie.show_skin("bucket")          # skins of one category: one at a time
## $Zombie.event.connect(func(name, data, volume, pitch): print(name))
## var hand := $Zombie.get_point_transform("hand")   # where a point is, for spawning things
## [/codeblock]

## An event key went by: its name, data (a String or null), volume and pitch
signal event(event_name: String, data: Variant, volume: float, pitch: float)
## A track's animation reached its end (not looping), or went round again (looping)
signal animation_finished(animation_name: String, track: int)
signal animation_looped(animation_name: String, track: int)

## The engine export to show (.hqrig.json)
@export_file("*.json") var rig_file := "":
	set(value):
		rig_file = value
		_load_rig()

## The animation on track 0 (empty: the rest pose)
@export var animation := "":
	set(value):
		animation = value
		if _data != null and not _naming:
			_start(0, value, 0.0, true)

## Whether time goes by (in the editor: whether the preview plays)
@export var playing := true
## How fast time goes by (1 = as made)
@export var speed := 1.0
## The skins on at the start (empty: as the file has them)
@export var skins := PackedStringArray():
	set(value):
		skins = value
		if _pose != null:
			_pose.shown_skins.clear()
			for n in (value if not value.is_empty() else PackedStringArray(_data.shown_skins)):
				_pose.show_skin(n)
## Play the rig's sounds: each event's from the sound map, and an animation's sound track
@export var play_sounds := false
@export var sound_bus := &"Master"
## Draw the hitboxes and points over the rig (for testing)
@export var show_hitboxes := false:
	set(value):
		show_hitboxes = value
		if _overlay != null:
			_overlay.queue_redraw()

var _data: HarlequinRigData
var _pose: HarlequinPose
var _tracks := []          # by track: null or { animation, time, last, alpha, from, from_time, fade, fade_length, ended }
var _clock := 0.0          # time gone by, for the jiggle springs
var _parts_root: Node2D
var _overlay: Node2D
var _parts: Array[Polygon2D] = []
var _shown: Array = []     # what each part's node shows now (its drawing), to set it up only when it changes
var _layout := ""          # the nesting of the part nodes (masks), to change it only when it changes
var _players: Array[AudioStreamPlayer] = []
var _streams := {}
var _sound_track: AudioStreamPlayer
var _sound_track_started := false
var _naming := false       # (play() names the animation in the inspector: that doesn't start it again)

const FLIP := Transform2D(Vector2(1, 0), Vector2(0, -1), Vector2.ZERO)


func _ready() -> void:
	if _data == null:
		_load_rig()


func _validate_property(property: Dictionary) -> void:
	if property.name == "animation" and _data != null:
		property.hint = PROPERTY_HINT_ENUM_SUGGESTION
		property.hint_string = ",".join(_data.animation_names)


func _get_configuration_warnings() -> PackedStringArray:
	if rig_file == "":
		return PackedStringArray(["Pick a Harlequin engine export (.hqrig.json) for Rig File."])
	if _data != null and _data.error != "":
		return PackedStringArray([_data.error])
	return PackedStringArray()


# ======================================================================= loading

func _load_rig() -> void:
	_clear_nodes()
	_data = null
	_pose = null
	_tracks.clear()
	if rig_file == "":
		update_configuration_warnings()
		return
	var data := HarlequinRigData.open(rig_file, Engine.is_editor_hint())
	if data.error != "":
		push_error("Harlequin: " + data.error)
		_data = data
		update_configuration_warnings()
		return
	_data = data
	_pose = HarlequinPose.new(data)
	self.skins = skins   # (the skins on)
	_make_nodes()
	if animation != "":
		_start(0, animation, 0.0, true)
	_refresh()
	notify_property_list_changed()
	update_configuration_warnings()


## Reads the rig file again (after exporting it again from Harlequin)
func reload() -> void:
	HarlequinRigData.forget(rig_file)
	_load_rig()


func _clear_nodes() -> void:
	if _parts_root != null:
		_parts_root.queue_free()
	if _overlay != null:
		_overlay.queue_free()
	_parts_root = null
	_overlay = null
	_parts.clear()
	_shown.clear()
	_layout = ""


func _make_nodes() -> void:
	_parts_root = Node2D.new()
	_parts_root.name = "Parts"
	add_child(_parts_root, false, Node.INTERNAL_MODE_FRONT)
	for slot in _data.slots:
		var node := Polygon2D.new()
		node.name = slot.name
		node.antialiased = false
		_parts.append(node)
		_shown.append(null)
		_parts_root.add_child(node)
	_overlay = Node2D.new()
	_overlay.name = "Hitboxes"
	_overlay.draw.connect(_draw_overlay)
	add_child(_overlay, false, Node.INTERNAL_MODE_BACK)


# ======================================================================= playing

## Plays an animation on track 0, fading from what plays there over `fade` seconds (-1: as the
## rig's mixer says; 0: at once)
func play(animation_name: String, fade := -1.0) -> void:
	_start(0, animation_name, fade, false)
	playing = true


## Another animation over the ones below it, by `alpha` (0..1): it changes only what it has keys
## for. Track 0 is play()'s.
func set_track(track: int, animation_name: String, alpha := 1.0, fade := -1.0) -> void:
	_start(track, animation_name, fade, false)
	if track < _tracks.size() and _tracks[track] != null:
		_tracks[track].alpha = alpha


func set_track_alpha(track: int, alpha: float) -> void:
	if track < _tracks.size() and _tracks[track] != null:
		_tracks[track].alpha = clampf(alpha, 0.0, 1.0)


func clear_track(track: int) -> void:
	if track < _tracks.size():
		_tracks[track] = null


## Back to the rest pose, nothing playing
func stop() -> void:
	_tracks.clear()
	_naming = true
	animation = ""
	_naming = false
	_stop_sound_track()
	_refresh()


## Track 0's animation at this time (seconds); the springs start again from rest
func seek(time: float) -> void:
	if not _tracks.is_empty() and _tracks[0] != null:
		_tracks[0].time = time
		_tracks[0].last = time
		_tracks[0].from = null
		_sound_track_started = false
	if _pose != null:
		_pose.reset_physics()
	_refresh()


func get_time(track := 0) -> float:
	return _tracks[track].time if track < _tracks.size() and _tracks[track] != null else 0.0


func get_current_animation(track := 0) -> String:
	return _tracks[track].animation.name if track < _tracks.size() and _tracks[track] != null else ""


func get_animation_names() -> PackedStringArray:
	return PackedStringArray(_data.animation_names) if _data != null else PackedStringArray()


func get_animation_length(animation_name: String) -> float:
	return _data.animations[animation_name].duration if _data != null and _data.animations.has(animation_name) else 0.0


## How long the rig's mixer says one animation takes to fade into another
func get_mix_duration(from: String, to: String) -> float:
	if _data == null:
		return 0.0
	return _data.mix_pairs.get(from + "\n" + to, _data.mix_default)


func _start(track: int, animation_name: String, fade: float, from_inspector: bool) -> void:
	if _data == null:
		return
	while _tracks.size() <= track:
		_tracks.append(null)
	if animation_name == "":
		_tracks[track] = null
		_refresh()
		return
	if not _data.animations.has(animation_name):
		push_warning("Harlequin: %s has no animation \"%s\"" % [rig_file.get_file(), animation_name])
		return
	var old = _tracks[track]
	var next := {"animation": _data.animations[animation_name], "time": 0.0, "last": -1.0, "alpha": 1.0,
				 "from": null, "from_time": 0.0, "fade": 0.0, "fade_length": 0.0, "ended": false}
	if old != null:
		next.alpha = old.alpha
		var length := fade if fade >= 0.0 else get_mix_duration(old.animation.name, animation_name)
		if length > 0.0 and not from_inspector:
			next.from = old.animation
			next.from_time = old.time
			next.fade_length = length
	_tracks[track] = next
	if track == 0:
		_stop_sound_track()
		if not from_inspector:
			_naming = true
			animation = animation_name
			_naming = false
	_refresh()


func _process(delta: float) -> void:
	if _data == null or _pose == null:
		return
	if Engine.is_editor_hint() and not playing:
		return
	if playing:
		advance(delta)


## Time goes on by delta seconds (times speed): events fire, then the pose is worked out
func advance(delta: float) -> void:
	var dt := delta * speed
	_clock += absf(dt)
	for n in _tracks.size():
		var t = _tracks[n]
		if t == null:
			continue
		var anim: Dictionary = t.animation
		var before: float = t.time
		t.time += dt
		if t.from != null:
			t.from_time += dt
			t.fade += absf(dt)
			if t.fade >= t.fade_length:
				t.from = null
		_fire_events(n, t, anim, before, t.time)
	_refresh()


func _fire_events(track: int, t: Dictionary, anim: Dictionary, from: float, to: float) -> void:
	var d: float = anim.duration
	var fired := []
	if t.last < 0.0:
		from = -1.0   # (just started: a key at 0 fires too)
	t.last = to
	if anim.loop and d > 0.0:
		var wraps := floori(to / d) - floori(maxf(from, 0.0) / d)
		var a := fmod(maxf(from, 0.0), d) if from >= 0.0 else -1.0
		var b := fmod(to, d)
		if wraps <= 0:
			fired = HarlequinPose.events_between(anim, a, b)
		else:
			fired = HarlequinPose.events_between(anim, a, d)
			for w in wraps - 1:
				fired += HarlequinPose.events_between(anim, -1.0, d)
			fired += HarlequinPose.events_between(anim, -1.0, b)
			animation_looped.emit(anim.name, track)
			if track == 0:
				_sound_track_started = false
	else:
		fired = HarlequinPose.events_between(anim, from, minf(to, d))
		if to >= d and not t.ended:
			t.ended = true
			animation_finished.emit(anim.name, track)
	for e in fired:
		event.emit(e.name, e.data, e.volume, e.pitch)
		if play_sounds:
			_play_event_sound(e)


# ======================================================================= the pose, drawn

func _refresh() -> void:
	if _pose == null or _parts_root == null:
		return
	_pose.setup_pose()
	for t in _tracks:
		if t == null:
			continue
		if t.from != null:
			var progress: float = clampf(t.fade / t.fade_length, 0.0, 1.0)
			_pose.layer(t.from, t.from_time, t.alpha)
			_pose.layer(t.animation, t.time, t.alpha * progress)
		else:
			_pose.layer(t.animation, t.time, t.alpha)
	_pose.update()
	_pose.step_physics(_clock)
	_draw_parts()
	if play_sounds:
		_update_sound_track()
	if show_hitboxes and _overlay != null:
		_overlay.queue_redraw()


func _draw_parts() -> void:
	# Masks: the parts each clips go inside it (its picture's alpha cuts them)
	var parent_of := {}
	var mask_mode := {}
	var order := _pose.draw_order
	var i := 0
	while i < order.size():
		var s: int = order[i]
		if _data.slots[s].is_mask and _picture(s) != null:
			var clipped := _pose.clipped_by(s)
			for c in clipped:
				parent_of[c] = s
			mask_mode[s] = CanvasItem.CLIP_CHILDREN_AND_DRAW if _data.slots[s].mask_shows else CanvasItem.CLIP_CHILDREN_ONLY
			i += clipped.size()
		i += 1
	var layout := str(order) + str(parent_of) + str(mask_mode)
	if layout != _layout:
		_layout = layout
		for s in order:
			var node := _parts[s]
			var parent: Node = _parts[parent_of[s]] if parent_of.has(s) else _parts_root
			if node.get_parent() != parent:
				node.get_parent().remove_child(node)
				parent.add_child(node)
			parent.move_child(node, -1)
			node.clip_children = mask_mode.get(s, CanvasItem.CLIP_CHILDREN_DISABLED)
	for s in _parts.size():
		_draw_part(s)


## What a part shows now: its drawing and picture (null: nothing)
func _picture(s: int) -> Variant:
	var drawing = _pose.drawing_of(s)
	if drawing == null or not _data.regions.has(drawing.asset):
		return null
	var region: Dictionary = _data.regions[drawing.asset]
	if region.page >= _data.pages.size() or _data.pages[region.page] == null:
		return null
	return [drawing, region]


func _draw_part(s: int) -> void:
	var node := _parts[s]
	var picture = _picture(s)
	if picture == null:
		node.visible = false
		_shown[s] = null
		return
	var drawing: Dictionary = picture[0]
	var region: Dictionary = picture[1]
	var mesh = drawing.mesh
	if mesh != null and (mesh.w != region.width or mesh.h != region.height or mesh.triangles.is_empty()):
		mesh = null   # (made for another picture size: drawn flat)
	node.visible = true
	if not is_same(_shown[s], drawing):
		_shown[s] = drawing
		node.texture = _data.pages[region.page]
		var uv := PackedVector2Array()
		var polys := []
		var origin := Vector2(region.x - region.offset_x, region.y - region.offset_y)
		if mesh != null:
			for p in mesh.points:
				uv.append(origin + p)
			for t in range(0, mesh.triangles.size() - 2, 3):
				polys.append(PackedInt32Array([mesh.triangles[t], mesh.triangles[t + 1], mesh.triangles[t + 2]]))
		else:
			uv = PackedVector2Array([Vector2(region.x, region.y), Vector2(region.x + region.w, region.y),
				Vector2(region.x + region.w, region.y + region.h), Vector2(region.x, region.y + region.h)])
		node.uv = uv
		node.polygons = polys
		node.polygon = PackedVector2Array()
	var points: PackedVector2Array
	if mesh != null:
		points = _pose.posed_mesh(s, drawing, mesh)
	else:
		var m := _pose.picture_to_object(s, drawing, region.width, region.height)
		var x0: float = region.offset_x
		var y0: float = region.offset_y
		var x1: float = x0 + region.w
		var y1: float = y0 + region.h
		points = PackedVector2Array([m * Vector2(x0, y0), m * Vector2(x1, y0), m * Vector2(x1, y1), m * Vector2(x0, y1)])
	for k in points.size():
		points[k].y = -points[k].y   # (Harlequin's y is up)
	node.polygon = points
	var c: Vector3 = _pose.part_color[s]
	node.color = Color(c.x, c.y, c.z, clampf(_pose.part_opacity[s], 0.0, 1.0))
	_set_material(s, node)


func _set_material(s: int, node: Polygon2D) -> void:
	var blend: int = _data.slots[s].blend
	var layers := []
	for n in _data.color_layers.size():
		var layer: Dictionary = _data.color_layers[n]
		if not layer.visible or layer.leave_out.has(s) or layer.kind == 4 or not _layer_applies(layer.name):
			continue
		layers.append({"kind": layer.kind, "colorize": layer.colorize, "keep_light": layer.keep_light, "values": _pose.color_values[n]})
	if blend == HarlequinRigData.Blend.NORMAL and layers.is_empty():
		node.material = null
		return
	var material := node.material as ShaderMaterial
	var shader := HarlequinShader.for_blend(blend)
	if material == null or material.shader != shader:
		material = ShaderMaterial.new()
		material.shader = shader
		node.material = material
	HarlequinShader.set_layers(material, layers)


## A colour layer in a skin applies only while that skin is on
func _layer_applies(layer_name: String) -> bool:
	for k in range(1, _data.skins.size()):
		if layer_name in _data.skins[k].color_layers:
			return _pose.shown_skins.has(_data.skins[k].name)
	return true


# ======================================================================= skins

## A skin on or off (skins of one category, or of none, are one at a time; overlays stack)
func show_skin(skin_name: String, on := true) -> void:
	if _pose == null:
		return
	_pose.show_skin(skin_name, on)
	_refresh()


func get_skin_names() -> PackedStringArray:
	var out := PackedStringArray()
	if _data != null:
		for k in range(1, _data.skins.size()):
			out.append(_data.skins[k].name)
	return out


func get_shown_skins() -> PackedStringArray:
	return PackedStringArray(_pose.shown_skins.keys()) if _pose != null else PackedStringArray()


# ======================================================================= where things are

## A Harlequin transform (y up) as one in this node's space (y down)
static func to_godot(t: Transform2D) -> Transform2D:
	return FLIP * t * FLIP


## A point's place and direction, in this node's space (IDENTITY when there's no such point)
func get_point_transform(point_name: String) -> Transform2D:
	if _pose == null or not _data.point_index.has(point_name):
		return Transform2D.IDENTITY
	var p: Dictionary = _data.points[_data.point_index[point_name]]
	return to_godot(_pose.world[p.bone] * Transform2D(p.rotation, p.offset))


func get_point_global_transform(point_name: String) -> Transform2D:
	return global_transform * get_point_transform(point_name)


## A bone's world place (its joint, its x axis along it), in this node's space
func get_bone_transform(bone_name: String) -> Transform2D:
	if _pose == null or not _data.bone_index.has(bone_name):
		return Transform2D.IDENTITY
	return to_godot(_pose.world[_data.bone_index[bone_name]])


## The hitboxes, each { name, kind, active, circle, transform (its centre, turned, in this node's
## space), size (a box's width and height; a circle's diameter in x) }
func get_hitboxes(only_active := true) -> Array:
	var out := []
	if _pose == null:
		return out
	for n in _data.hitboxes.size():
		var h: Dictionary = _data.hitboxes[n]
		if only_active and not _pose.hitbox_active[n]:
			continue
		out.append({"name": h.name, "kind": h.kind, "active": _pose.hitbox_active[n], "circle": h.circle,
					"transform": to_godot(_pose.world[h.bone] * Transform2D(h.rotation, h.offset)), "size": h.size})
	return out


## A hitbox's outline, in this node's space (a circle: 24 points round it)
func get_hitbox_polygon(hitbox: Dictionary) -> PackedVector2Array:
	var t: Transform2D = hitbox.transform
	var out := PackedVector2Array()
	if hitbox.circle:
		for k in 24:
			out.append(t * (Vector2.from_angle(TAU * k / 24.0) * hitbox.size.x / 2.0))
	else:
		var hw: float = hitbox.size.x / 2.0
		var hh: float = hitbox.size.y / 2.0
		for c in [Vector2(-hw, -hh), Vector2(hw, -hh), Vector2(hw, hh), Vector2(-hw, hh)]:
			out.append(t * c)
	return out


## The pose itself (bones, parts…), in Harlequin's space: for anything the methods above don't cover
func get_pose() -> HarlequinPose:
	return _pose


func get_rig_data() -> HarlequinRigData:
	return _data


func _draw_overlay() -> void:
	if not show_hitboxes or _pose == null:
		return
	for h in get_hitboxes(false):
		var color := Color(1, 0.3, 0.3) if h.kind == "hurt" else Color(1, 0.8, 0.2) if h.kind == "hit" else Color(0.3, 0.8, 1)
		if not h.active:
			color.a = 0.3
		var outline := get_hitbox_polygon(h)
		outline.append(outline[0])
		_overlay.draw_polyline(outline, color, 1.5)
	for point_name in _data.point_index:
		var t := get_point_transform(point_name)
		_overlay.draw_circle(t.origin, 3.0, Color(0.4, 1, 0.5))
		_overlay.draw_line(t.origin, t.origin + t.x.normalized() * 14.0, Color(0.4, 1, 0.5), 1.5)


# ======================================================================= sound

func _play_event_sound(e: Dictionary) -> void:
	if not _data.sounds.has(e.name):
		return
	var stream := _stream(_data.sounds[e.name])
	if stream == null:
		return
	var player: AudioStreamPlayer = null
	for p in _players:
		if not p.playing:
			player = p
			break
	if player == null:
		if _players.size() >= 8:
			player = _players[0]
		else:
			player = AudioStreamPlayer.new()
			add_child(player, false, Node.INTERNAL_MODE_BACK)
			_players.append(player)
	player.bus = sound_bus
	player.stream = stream
	player.volume_db = linear_to_db(maxf(e.volume, 0.0001))
	player.pitch_scale = clampf(e.pitch, 0.01, 4.0)
	player.play()


func _update_sound_track() -> void:
	if _tracks.is_empty() or _tracks[0] == null or _tracks[0].animation.sound == null or Engine.is_editor_hint():
		return
	var t: Dictionary = _tracks[0]
	var sound: Dictionary = t.animation.sound
	var at: float = t.time - sound.offset
	if t.animation.loop and t.animation.duration > 0.0:
		at = fmod(t.time, t.animation.duration) - sound.offset
	if _sound_track_started or at < 0.0:
		return
	var stream := _stream(sound.path)
	if stream == null:
		return
	if _sound_track == null:
		_sound_track = AudioStreamPlayer.new()
		add_child(_sound_track, false, Node.INTERNAL_MODE_BACK)
	_sound_track.bus = sound_bus
	_sound_track.stream = stream
	_sound_track.volume_db = linear_to_db(maxf(sound.volume, 0.0001))
	_sound_track.play(at)
	_sound_track_started = true


func _stop_sound_track() -> void:
	_sound_track_started = false
	if _sound_track != null:
		_sound_track.stop()


func _stream(relative: String) -> AudioStream:
	if _streams.has(relative):
		return _streams[relative]
	var file := relative if relative.is_absolute_path() else _data.folder.path_join(relative)
	var stream: AudioStream = null
	if ResourceLoader.exists(file):
		stream = load(file) as AudioStream
	elif file.get_extension().to_lower() == "ogg":
		stream = AudioStreamOggVorbis.load_from_file(file)
	if stream == null:
		push_warning("Harlequin: can't load the sound %s" % file)
	_streams[relative] = stream
	return stream
