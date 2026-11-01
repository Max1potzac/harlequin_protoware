@tool
class_name HarlequinPose
extends RefCounted
## A rig's pose at one moment: its bones, parts, constraint mixes, colour layers and hitboxes,
## worked out the way Harlequin does it (the reference is the editor's own C# code, named in
## docs/engine-format.md). In Harlequin's space: y up, angles counter-clockwise.
##
## Each frame: setup_pose(), then apply() (or crossfade/layer) the animations, then update().

var data: HarlequinRigData

# Bones: the values an animation sets, and where each ends up
var position := PackedVector2Array()
var rotation := PackedFloat32Array()
var scale := PackedVector2Array()
var turn := PackedFloat32Array()
var tilt := PackedFloat32Array()
var space_overridden: Array[bool] = []
var space_parent := PackedInt32Array()   # while overridden: the bone it follows (-1: none, detached)
var world: Array[Transform2D] = []

# Parts
var attachment: Array = []                 # the drawing's name (null: none)
var part_offset := PackedVector2Array()
var part_rotation := PackedFloat32Array()
var part_scale := PackedVector2Array()
var part_color := PackedVector3Array()
var part_opacity := PackedFloat32Array()
var deform: Array = []                     # null, or { drawing, offsets }
var draw_order := PackedInt32Array()       # back to front

var ik_mix := PackedFloat32Array()
var copy_mix := PackedFloat32Array()
var look_mix := PackedFloat32Array()
var jiggle_mix := PackedFloat32Array()
var color_values: Array[PackedFloat32Array] = []
var hitbox_active: Array[bool] = []

## The skins on (names), the default skin aside
var shown_skins := {}

# Jiggle springs: moving, time, tip, velocity
var _jiggle: Array[Dictionary] = []


func _init(rig: HarlequinRigData) -> void:
	data = rig
	var nb := data.bones.size()
	position.resize(nb); rotation.resize(nb); scale.resize(nb); turn.resize(nb); tilt.resize(nb)
	space_overridden.resize(nb); space_parent.resize(nb); world.resize(nb)
	var ns := data.slots.size()
	attachment.resize(ns); part_offset.resize(ns); part_rotation.resize(ns); part_scale.resize(ns)
	part_color.resize(ns); part_opacity.resize(ns); deform.resize(ns)
	ik_mix.resize(data.ik_constraints.size())
	copy_mix.resize(data.copy_constraints.size())
	look_mix.resize(data.look_constraints.size())
	jiggle_mix.resize(data.jiggle_constraints.size())
	color_values.resize(data.color_layers.size())
	hitbox_active.resize(data.hitboxes.size())
	for c in data.jiggle_constraints:
		_jiggle.append({"moving": false, "time": 0.0, "tip": Vector2.ZERO, "velocity": Vector2.ZERO})
	for n in data.shown_skins:
		show_skin(n)
	setup_pose()
	update()


# ======================================================================= rest

func setup_pose() -> void:
	for i in data.bones.size():
		var b: Dictionary = data.bones[i]
		position[i] = b.position
		rotation[i] = b.rotation
		scale[i] = b.scale
		turn[i] = b.turn
		tilt[i] = b.tilt
		space_overridden[i] = false
		space_parent[i] = -1
	for i in data.slots.size():
		var s: Dictionary = data.slots[i]
		attachment[i] = s.attachment
		part_offset[i] = Vector2.ZERO
		part_rotation[i] = 0.0
		part_scale[i] = Vector2.ONE
		part_color[i] = s.color
		part_opacity[i] = s.opacity
		deform[i] = null
	draw_order = PackedInt32Array(range(data.slots.size()))
	for i in ik_mix.size(): ik_mix[i] = data.ik_constraints[i].mix
	for i in copy_mix.size(): copy_mix[i] = data.copy_constraints[i].mix
	for i in look_mix.size(): look_mix[i] = data.look_constraints[i].mix
	for i in jiggle_mix.size(): jiggle_mix[i] = data.jiggle_constraints[i].mix
	for i in color_values.size(): color_values[i] = data.color_layers[i].values
	for i in hitbox_active.size(): hitbox_active[i] = data.hitboxes[i].active


# ======================================================================= animations

## An animation at this time (seconds): looping ones wrap, the others hold their ends
func apply(animation: Dictionary, time: float) -> void:
	var duration: float = animation.duration
	if animation.loop and duration > 0.0 and (time > duration or time < 0.0):
		time = fmod(time, duration)
		if time < 0.0:
			time += duration
	else:
		time = clampf(time, 0.0, duration)
	for timeline in animation.timelines:
		_apply_timeline(timeline, time)


func _apply_timeline(tl: Dictionary, time: float) -> void:
	var keys: Array = tl.keys
	if keys.is_empty():
		return
	var i: int = tl.target
	match tl.type:
		"rotate":
			rotation[i] = data.bones[i].rotation + sample(keys, time)
		"translate":
			position[i] = data.bones[i].position + sample(keys, time)
		"scale":
			scale[i] = data.bones[i].scale * sample(keys, time)
		"depth":
			var v: Vector2 = sample(keys, time)
			tilt[i] = data.bones[i].tilt + v.x
			turn[i] = data.bones[i].turn + v.y
		"space":
			if time < keys[0].t:
				return
			var held = sample(keys, time)
			if held == null:
				space_overridden[i] = false
				space_parent[i] = -1
			else:
				space_overridden[i] = true
				space_parent[i] = -1 if held == "" else data.bone_index.get(held, -1)
		"ikMix":
			ik_mix[i] = clampf(sample(keys, time), 0.0, 1.0)
		"attachment":
			if time >= keys[0].t:
				attachment[i] = sample(keys, time)
		"partTranslate":
			part_offset[i] = sample(keys, time)
		"partRotate":
			part_rotation[i] = sample(keys, time)
		"partScale":
			part_scale[i] = sample(keys, time)
		"partColor":
			part_color[i] = sample(keys, time)
		"partOpacity":
			part_opacity[i] = sample(keys, time)
		"deform":
			deform[i] = sample(keys, time)
		"colorLayer":
			color_values[i] = sample(keys, time)
		"hitbox":
			if time >= keys[0].t:
				hitbox_active[i] = sample(keys, time)
		"drawOrder":
			if time >= keys[0].t:
				set_draw_order(sample(keys, time))


## A track's value at this time (Track.TrySample): the first key's before it, the last's after
static func sample(keys: Array, time: float) -> Variant:
	if time <= keys[0].t:
		return keys[0].v
	var last: Dictionary = keys[keys.size() - 1]
	if time >= last.t:
		return last.v
	var lo := 0
	var hi := keys.size() - 1
	while hi - lo > 1:
		var mid := (lo + hi) / 2
		if keys[mid].t <= time:
			lo = mid
		else:
			hi = mid
	var a: Dictionary = keys[lo]
	var b: Dictionary = keys[hi]
	var t: float = (time - a.t) / (b.t - a.t)
	match a.curve:
		HarlequinRigData.Interp.STEPPED:
			return a.v
		HarlequinRigData.Interp.SMOOTH:
			t = t * t * (3.0 - 2.0 * t)
		HarlequinRigData.Interp.BEZIER:
			t = bezier(a.b, t)
	return blend_value(a.v, b.v, t)


static func blend_value(a, b, t: float) -> Variant:
	if a == null or b == null or a is String or a is bool or a is PackedInt32Array:
		return a   # (drawings, spaces, hitboxes, the draw order: held till the next key)
	if a is float or a is Vector2 or a is Vector3:
		return a + (b - a) * t
	if a is PackedFloat32Array:
		var out := PackedFloat32Array(a)
		for n in mini(a.size(), b.size()):
			out[n] = a[n] + (b[n] - a[n]) * t
		return out
	if a is Dictionary and a.has("offsets"):
		if a.drawing != b.drawing or a.offsets.size() != b.offsets.size():
			return a
		var offsets := PackedVector2Array(a.offsets)
		for n in offsets.size():
			offsets[n] = a.offsets[n].lerp(b.offsets[n], t)
		return {"drawing": a.drawing, "offsets": offsets}
	return a   # (events)


## Keyframing/Bezier.cs Ease: the curve's height where it is t across
static func bezier(h: Array, t: float) -> float:
	if t <= 0.0:
		return 0.0
	if t >= 1.0:
		return 1.0
	var x1: float = h[0]
	var y1: float = h[1]
	var x2: float = h[2]
	var y2: float = h[3]
	var u := t
	for i in 8:
		var x := _coord(x1, x2, u) - t
		if absf(x) < 1e-6:
			return _coord(y1, y2, u)
		var dx := _slope(x1, x2, u)
		if absf(dx) < 1e-6:
			break
		u = clampf(u - x / dx, 0.0, 1.0)
	var lo := 0.0
	var hi := 1.0
	for i in 40:
		u = (lo + hi) / 2.0
		if _coord(x1, x2, u) < t:
			lo = u
		else:
			hi = u
	return _coord(y1, y2, u)


static func _coord(p1: float, p2: float, u: float) -> float:
	var v := 1.0 - u
	return 3.0 * v * v * u * p1 + 3.0 * v * u * u * p2 + u * u * u


static func _slope(p1: float, p2: float, u: float) -> float:
	var v := 1.0 - u
	return 3.0 * v * v * p1 + 6.0 * v * u * (p2 - p1) + 3.0 * u * u * (1.0 - p2)


## The events an animation passes going from one time to the next: (from, to], wrapping round
## when it loops. [{ name, data, volume, pitch, t }]
static func events_between(animation: Dictionary, from: float, to: float) -> Array:
	var out := []
	for tl in animation.timelines:
		if tl.type != "events":
			continue
		for k in tl.keys:
			if k.t > from and k.t <= to:
				var e: Dictionary = k.v.duplicate()
				e.t = k.t
				out.append(e)
	return out


## The draw order from a key: these parts back to front; ones it doesn't name go in front of the
## part they're in front of at rest (Skeleton.SetDrawOrder)
func set_draw_order(names: PackedInt32Array) -> void:
	var count := data.slots.size()
	var order := PackedInt32Array()
	var placed := {}
	for s in names:
		if s >= 0 and s < count and not placed.has(s):
			order.append(s)
			placed[s] = true
	for i in count:
		if placed.has(i):
			continue
		var at := 0
		for j in range(i - 1, -1, -1):
			if placed.has(j):
				at = order.find(j) + 1
				break
		order.insert(at, i)
		placed[i] = true
	draw_order = order


# ======================================================================= mixing

## Everything an animation can change, now (to blend from: Editing/Mixing.cs)
func capture() -> Dictionary:
	return {
		"position": PackedVector2Array(position), "rotation": PackedFloat32Array(rotation),
		"scale": PackedVector2Array(scale), "turn": PackedFloat32Array(turn), "tilt": PackedFloat32Array(tilt),
		"space_overridden": space_overridden.duplicate(), "space_parent": PackedInt32Array(space_parent),
		"attachment": attachment.duplicate(), "part_offset": PackedVector2Array(part_offset),
		"part_rotation": PackedFloat32Array(part_rotation), "part_scale": PackedVector2Array(part_scale),
		"part_color": PackedVector3Array(part_color), "part_opacity": PackedFloat32Array(part_opacity),
		"deform": deform.duplicate(), "draw_order": PackedInt32Array(draw_order),
		"ik_mix": PackedFloat32Array(ik_mix), "copy_mix": PackedFloat32Array(copy_mix),
		"look_mix": PackedFloat32Array(look_mix), "jiggle_mix": PackedFloat32Array(jiggle_mix),
		"color_values": color_values.duplicate(), "hitbox_active": hitbox_active.duplicate(),
	}


## The animation at this time over what's posed now, by alpha (Mixing.Layer)
func layer(animation: Dictionary, time: float, alpha: float) -> void:
	alpha = clampf(alpha, 0.0, 1.0)
	if alpha <= 0.0:
		return
	if alpha >= 1.0:
		apply(animation, time)
		return
	var before := capture()
	apply(animation, time)
	_blend_from(before, alpha)


## `from` fully, then `to` over it by how far the fade has gone (Mixing.Crossfade)
func crossfade(from: Dictionary, from_time: float, to: Dictionary, to_time: float, progress: float) -> void:
	layer(from, from_time, 1.0)
	layer(to, to_time, progress)


func _blend_from(was: Dictionary, alpha: float) -> void:
	var switch_now := alpha >= 0.5
	for i in position.size():
		position[i] = was.position[i].lerp(position[i], alpha)
		rotation[i] = was.rotation[i] + shortest_angle(rotation[i] - was.rotation[i]) * alpha
		scale[i] = was.scale[i].lerp(scale[i], alpha)
		turn[i] = was.turn[i] + shortest_angle(turn[i] - was.turn[i]) * alpha
		tilt[i] = was.tilt[i] + shortest_angle(tilt[i] - was.tilt[i]) * alpha
	for i in attachment.size():
		part_offset[i] = was.part_offset[i].lerp(part_offset[i], alpha)
		part_rotation[i] = was.part_rotation[i] + shortest_angle(part_rotation[i] - was.part_rotation[i]) * alpha
		part_scale[i] = was.part_scale[i].lerp(part_scale[i], alpha)
		part_color[i] = was.part_color[i].lerp(part_color[i], alpha)
		part_opacity[i] = was.part_opacity[i] + (part_opacity[i] - was.part_opacity[i]) * alpha
		var now_drawing = attachment[i]
		var was_drawing = was.attachment[i]
		deform[i] = _blend_deform(was.deform[i], deform[i], was_drawing, now_drawing, alpha, switch_now)
		attachment[i] = now_drawing if switch_now else was_drawing
	if not switch_now:
		draw_order = was.draw_order
	for i in ik_mix.size(): ik_mix[i] = lerpf(was.ik_mix[i], ik_mix[i], alpha)
	for i in look_mix.size(): look_mix[i] = lerpf(was.look_mix[i], look_mix[i], alpha)
	for i in copy_mix.size(): copy_mix[i] = lerpf(was.copy_mix[i], copy_mix[i], alpha)
	for i in jiggle_mix.size(): jiggle_mix[i] = lerpf(was.jiggle_mix[i], jiggle_mix[i], alpha)
	for i in color_values.size(): color_values[i] = blend_value(was.color_values[i], color_values[i], alpha)
	for i in hitbox_active.size():
		if not switch_now:
			hitbox_active[i] = was.hitbox_active[i]


static func _blend_deform(was, now, was_drawing, now_drawing, alpha: float, switch_now: bool) -> Variant:
	if was_drawing != now_drawing:
		return now if switch_now else was
	if was == null and now == null:
		return null
	var drawing: String = (now if now != null else was).drawing
	var n: int = (now if now != null else was).offsets.size()
	var a := PackedVector2Array()
	a.resize(n)
	var b := PackedVector2Array()
	b.resize(n)
	if was != null and was.drawing == drawing and was.offsets.size() == n:
		a = was.offsets
	if now != null and now.drawing == drawing and now.offsets.size() == n:
		b = now.offsets
	var mixed := PackedVector2Array()
	mixed.resize(n)
	for i in n:
		mixed[i] = a[i].lerp(b[i], alpha)
	return {"drawing": drawing, "offsets": mixed}


# ======================================================================= bones

## Limits, world places, then the constraints (Skeleton.UpdatePose)
func update() -> void:
	_apply_limits()
	update_world()
	for i in data.ik_constraints.size():
		_solve_ik(i)
	for i in data.copy_constraints.size():
		_solve_copy(i)
	for i in data.look_constraints.size():
		_solve_look(i)
	_update_held()


func update_world() -> void:
	for i in data.bones.size():
		if data.bones[i].parent < 0:
			update_bone(i)
	_update_held()


func setup_position(i: int) -> Vector2:
	return data.bones[i].position


func _apply_limits() -> void:
	for i in data.bones.size():
		if space_overridden[i]:
			continue
		var b: Dictionary = data.bones[i]
		if b.min_offset == null and b.max_offset == null:
			continue
		var offset: Vector2 = position[i] - b.position
		if b.min_offset != null:
			offset = offset.max(b.min_offset)
		if b.max_offset != null:
			offset = offset.min(b.max_offset)
		position[i] = b.position + offset


## The bone this one is measured from now: its parent, or what a space key says
func space_of(i: int) -> int:
	return space_parent[i] if space_overridden[i] else data.bones[i].parent


## One bone's world place, then its children's (Bone.UpdateWorld)
func update_bone(i: int) -> void:
	var b: Dictionary = data.bones[i]
	var s := Vector2(scale[i].x * cos(tilt[i]), scale[i].y * cos(turn[i]))
	var local := Transform2D(rotation[i], s, 0.0, position[i])
	var parent := space_of(i)
	if parent < 0:
		world[i] = local
	elif b.inherit_rotation and b.inherit_scale:
		world[i] = world[parent] * local
	else:
		var p: Transform2D = world[parent]
		var at := p * position[i]
		var linear := Transform2D.IDENTITY
		if b.inherit_scale:
			linear = Transform2D(0.0, Vector2(p.x.length(), p.y.length()), 0.0, Vector2.ZERO)
		if b.inherit_rotation:
			linear = Transform2D(atan2(p.x.y, p.x.x), Vector2.ZERO) * linear
		world[i] = Transform2D(0.0, at) * linear * Transform2D(rotation[i], s, 0.0, Vector2.ZERO)
	for c in b.children:
		update_bone(c)


## A bone held by another (a space key) is placed from that one, which may come later: so
## they're placed again, a few times over (Skeleton.UpdateHeldBones)
func _update_held() -> void:
	var held := []
	for i in data.bones.size():
		if space_overridden[i] and space_parent[i] >= 0:
			held.append(i)
	if held.is_empty():
		return
	for pass_number in 3:
		for i in held:
			update_bone(i)


func world_rotation(i: int) -> float:
	return atan2(world[i].x.y, world[i].x.x)


func tip(i: int) -> Vector2:
	return world[i] * Vector2(data.bones[i].length, 0.0)


func _to_space_local(i: int, point: Vector2) -> Vector2:
	var space := space_of(i)
	if space < 0:
		return point
	if absf(world[space].determinant()) < 1e-12:
		return position[i]
	return world[space].affine_inverse() * point


static func shortest_angle(a: float) -> float:
	a = fmod(a, TAU)
	if a > PI:
		a -= TAU
	elif a < -PI:
		a += TAU
	return a


# ======================================================================= constraints

func _solve_ik(n: int) -> void:
	var c: Dictionary = data.ik_constraints[n]
	var mix: float = ik_mix[n]
	var chain: Array = c.bones
	if mix <= 0.0 or chain.is_empty():
		return
	for b in chain:
		if space_overridden[b]:
			return   # a chain that's broken (a bone detached or held): IK lets go
	var target: Vector2 = world[c.target].origin
	var fk := PackedFloat32Array()
	for b in chain:
		fk.append(rotation[b])
	if chain.size() == 2:
		_ik_two(c, target)
	else:
		_ik_chain(c, target)
	for k in chain.size():
		var b: int = chain[k]
		rotation[b] = fk[k] + shortest_angle(rotation[b] - fk[k]) * mix
	update_bone(chain[0])


func _parent_angle(b: int) -> float:
	var parent: int = data.bones[b].parent
	return world_rotation(parent) if parent >= 0 and data.bones[b].inherit_rotation else 0.0


func _ik_two(c: Dictionary, target: Vector2) -> void:
	var b1: int = c.bones[0]
	var b2: int = c.bones[1]
	var root := world[b1].origin
	var joint := world[b2].origin
	var l1 := root.distance_to(joint)
	var l2 := joint.distance_to(tip(b2))
	if l1 < 0.01 or l2 < 0.01:
		return
	var to_target := target - root
	var dist := to_target.length()
	var reach := clampf(dist, absf(l1 - l2) + 0.001, l1 + l2 - 0.001)
	var cos_a := (l1 * l1 + reach * reach - l2 * l2) / (2.0 * l1 * reach)
	var a1 := acos(clampf(cos_a, -1.0, 1.0))
	var aim := atan2(to_target.y, to_target.x)
	var world1: float = aim + c.bend * a1
	var elbow := root + Vector2(cos(world1), sin(world1)) * l1
	var world2 := atan2(target.y - elbow.y, target.x - elbow.x)
	rotation[b1] = world1 - _parent_angle(b1)
	rotation[b2] = world2 - (world1 if data.bones[b2].inherit_rotation else 0.0)


func _ik_chain(c: Dictionary, target: Vector2) -> void:
	var chain: Array = c.bones
	var tip_bone: int = chain[chain.size() - 1]
	for iteration in c.iterations:
		for k in range(chain.size() - 1, -1, -1):
			var b: int = chain[k]
			var origin := world[b].origin
			var to_tip := tip(tip_bone) - origin
			var to_target := target - origin
			if to_tip.length_squared() < 1e-6 or to_target.length_squared() < 1e-6:
				continue
			rotation[b] += shortest_angle(atan2(to_target.y, to_target.x) - atan2(to_tip.y, to_tip.x))
			update_bone(chain[0])
		if tip(tip_bone).distance_to(target) < 0.01:
			return


func _solve_copy(n: int) -> void:
	var c: Dictionary = data.copy_constraints[n]
	var mix: float = copy_mix[n]
	var b: int = c.bone
	var src: int = c.source
	if mix <= 0.0 or src == b:
		return
	var turn_mix := clampf(c.rotate, 0.0, 1.0) * mix
	var move_mix := clampf(c.move, 0.0, 1.0) * mix
	var size_mix := clampf(c.scale, 0.0, 1.0) * mix
	var sb: Dictionary = data.bones[src]
	if c.local:
		rotation[b] += (rotation[src] - sb.rotation) * turn_mix
		position[b] += (position[src] - sb.position) * move_mix
		var ratio := Vector2(_ratio(scale[src].x, sb.scale.x), _ratio(scale[src].y, sb.scale.y))
		scale[b] *= Vector2.ONE.lerp(ratio, size_mix)
	else:
		if move_mix > 0.0:
			var at := _to_space_local(b, world[src] * c.position_offset)
			position[b] = position[b].lerp(at, move_mix)
			update_bone(b)
		if turn_mix > 0.0:
			rotation[b] += shortest_angle(world_rotation(src) + c.rotation_offset - world_rotation(b)) * turn_mix
		if size_mix > 0.0:
			scale[b] = scale[b].lerp(scale[src] * c.scale_offset, size_mix)
	update_bone(b)


static func _ratio(a: float, b: float) -> float:
	return a / b if absf(b) > 1e-6 else 1.0


func _solve_look(n: int) -> void:
	var c: Dictionary = data.look_constraints[n]
	var b: int = c.bone
	var center := position[b]
	var mix: float = look_mix[n]
	if mix <= 0.0:
		return
	var target: Vector2 = world[c.target].origin
	var space := space_of(b)
	if space >= 0:
		if absf(world[space].determinant()) < 1e-12:
			return
		target = world[space].affine_inverse() * target
	var v: Vector2 = (target - center) * c.follow
	var rx := maxf(c.radius.x, 0.001)
	var ry := maxf(c.radius.y, 0.001)
	if c.rectangle:
		v = Vector2(clampf(v.x, -rx, rx), clampf(v.y, -ry, ry))
	else:
		var d := v.x * v.x / (rx * rx) + v.y * v.y / (ry * ry)
		if d > 1.0:
			v /= sqrt(d)
	position[b] = center + v * mix
	update_bone(b)


# ======================================================================= jiggle

## The jiggle springs, a step on to this time (after update): parents first
func step_physics(time: float) -> void:
	if data.jiggle_constraints.is_empty():
		return
	var order := range(data.jiggle_constraints.size())
	order.sort_custom(func(a, b): return data.jiggle_constraints[a].bone < data.jiggle_constraints[b].bone)
	for n in order:
		_step_jiggle(n, time)
	_update_held()


## The springs still again: the next step starts them from rest
func reset_physics() -> void:
	for j in _jiggle:
		j.moving = false


func _step_jiggle(n: int, time: float) -> void:
	var c: Dictionary = data.jiggle_constraints[n]
	var j: Dictionary = _jiggle[n]
	var b: int = c.bone
	var head := world[b].origin
	var wanted := tip(b)
	var length := head.distance_to(wanted)
	if length < 1e-4:
		return
	if not j.moving or time < j.time - 1e-5 or time - j.time > 1.0:
		j.moving = true
		j.time = time
		j.tip = wanted
		j.velocity = Vector2.ZERO
		return
	var dt: float = time - j.time
	j.time = time
	if dt > 0.0:
		var steps := maxi(1, ceili(dt * 240.0))
		var h := dt / steps
		var tip_now: Vector2 = j.tip
		var velocity: Vector2 = j.velocity
		for s in steps:
			var pull: Vector2 = (wanted - tip_now) * c.stiffness - velocity * c.damping + Vector2(0.0, -c.gravity)
			velocity += pull * h
			tip_now += velocity * h
			var along := tip_now - head
			if along.length_squared() <= 1e-8:
				continue
			var direction := along.normalized()
			tip_now = head + direction * length
			velocity -= direction * velocity.dot(direction)
		j.tip = tip_now
		j.velocity = velocity
	var mix: float = jiggle_mix[n]
	if mix <= 0.0:
		return
	var angle := atan2((j.tip - head).y, (j.tip - head).x) - atan2((wanted - head).y, (wanted - head).x)
	rotation[b] += shortest_angle(angle) * clampf(mix, 0.0, 1.0)
	update_bone(b)


# ======================================================================= skins and drawings

## A skin on or off: skins of one category (or none) are one at a time; overlays stack (SkinOps.Show)
func show_skin(skin_name: String, on := true) -> void:
	if not data.skin_index.has(skin_name) or skin_name == "default":
		return
	if not on:
		shown_skins.erase(skin_name)
		return
	var skin: Dictionary = data.skins[data.skin_index[skin_name]]
	if not skin.overlay:
		for other in shown_skins.keys():
			var o: Dictionary = data.skins[data.skin_index[other]]
			if other != skin_name and not o.overlay and o.category.to_lower() == skin.category.to_lower():
				shown_skins.erase(other)
	shown_skins[skin_name] = true


## The drawing a part shows under this name: from the skins on (overlays first, then the later
## in the list), else the object's own (Skeleton.FindAttachment). null: none.
func find_drawing(slot: int, drawing_name) -> Variant:
	if drawing_name == null:
		return null
	var part: String = data.slots[slot].name
	var on_top := []
	for k in range(data.skins.size() - 1, 0, -1):
		if shown_skins.has(data.skins[k].name):
			on_top.append(data.skins[k])
	var overlays := on_top.filter(func(s): return s.overlay)
	var others := on_top.filter(func(s): return not s.overlay)
	for skin in overlays + others:
		if skin.entries.has(part) and skin.entries[part].has(drawing_name):
			return skin.entries[part][drawing_name]
	var own: Dictionary = data.skins[0]
	if own.entries.has(part) and own.entries[part].has(drawing_name):
		return own.entries[part][drawing_name]
	return null


func drawing_of(slot: int) -> Variant:
	return find_drawing(slot, attachment[slot])


## Picture pixels (y down) → the bone's space, the part's placement keys on top (Attachment.LocalMatrix)
func picture_to_bone(slot: int, drawing: Dictionary, width: int, height: int) -> Transform2D:
	var pivot: Vector2 = drawing.pivot
	var m := Transform2D(0.0, Vector2(-pivot.x * width, -pivot.y * height))
	m = Transform2D(0.0, Vector2(1, -1), 0.0, Vector2.ZERO) * m
	m = Transform2D(0.0, drawing.scale * part_scale[slot], 0.0, Vector2.ZERO) * m
	m = Transform2D(drawing.rotation + part_rotation[slot], Vector2.ZERO) * m
	return Transform2D(0.0, drawing.offset + part_offset[slot]) * m


## Picture pixels → the object (Harlequin's space)
func picture_to_object(slot: int, drawing: Dictionary, width: int, height: int) -> Transform2D:
	return world[data.slots[slot].bone] * picture_to_bone(slot, drawing, width, height)


## A mesh's points where they are now (MeshOps.Posed), deform keys included
func posed_mesh(slot: int, drawing: Dictionary, mesh: Dictionary) -> PackedVector2Array:
	var pts: PackedVector2Array = mesh.points
	var pushes = null
	var d = deform[slot]
	if d != null and d.drawing == drawing.name and d.offsets.size() == pts.size():
		pushes = d.offsets
	var out := PackedVector2Array()
	out.resize(pts.size())
	if mesh.weights == null:
		var rigid := picture_to_object(slot, drawing, mesh.w, mesh.h)
		for i in pts.size():
			out[i] = rigid * (pts[i] + (pushes[i] if pushes != null else Vector2.ZERO))
		return out
	for i in pts.size():
		var sum := Transform2D(Vector2.ZERO, Vector2.ZERO, Vector2.ZERO)
		for w in mesh.weights[i]:
			var bone: int = w[0]
			var bind: Transform2D = mesh.bone_bind[bone] if mesh.bone_bind.has(bone) else (world[bone].affine_inverse() if absf(world[bone].determinant()) > 1e-12 else Transform2D.IDENTITY)
			var m: Transform2D = world[bone] * bind * mesh.bind
			sum.x += m.x * w[1]
			sum.y += m.y * w[1]
			sum.origin += m.origin * w[1]
		out[i] = sum * (pts[i] + (pushes[i] if pushes != null else Vector2.ZERO))
	return out


## The parts a mask clips now (MaskOps.Clipped): the ones right above it up to its last
func clipped_by(mask: int) -> PackedInt32Array:
	var out := PackedInt32Array()
	var at := draw_order.find(mask)
	if at < 0 or not data.slots[mask].is_mask:
		return out
	var until: int = data.slots[mask].mask_until
	var end := draw_order.size() - 1 if until < 0 else draw_order.find(until)
	for i in range(at + 1, mini(end, draw_order.size() - 1) + 1):
		out.append(draw_order[i])
	return out
