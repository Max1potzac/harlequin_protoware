extends Node2D
# The sample rig, to try the runtime: Space plays walk / idle (fading), 1–3 switch the hat skin,
# G the glow overlay, H the hitboxes, T a second track (the walk over the idle at half strength).

@onready var rig: HarlequinRig = $Sample
@onready var label: Label = $Help


func _ready() -> void:
	rig.event.connect(_on_event)
	_show("")


func _unhandled_input(e: InputEvent) -> void:
	if not (e is InputEventKey and e.pressed and not e.echo):
		return
	match e.keycode:
		KEY_SPACE:
			rig.play("idle" if rig.get_current_animation() == "walk" else "walk")
		KEY_1:
			rig.show_skin("cone")
		KEY_2:
			rig.show_skin("bucket")
		KEY_3:
			for s in ["cone", "bucket"]:
				rig.show_skin(s, false)
		KEY_G:
			rig.show_skin("glow", not rig.get_shown_skins().has("glow"))
		KEY_H:
			rig.show_hitboxes = not rig.show_hitboxes
		KEY_T:
			if rig.get_current_animation(1) == "":
				rig.set_track(1, "walk", 0.5)
			else:
				rig.clear_track(1)
	_show("")


func _on_event(event_name: String, data: Variant, volume: float, pitch: float) -> void:
	_show("event: %s %s" % [event_name, data if data != null else ""])


func _show(last: String) -> void:
	label.text = "Space: walk / idle   1 cone   2 bucket   3 no hat   G glow   H hitboxes   T second track\n" + \
		"playing: %s   skins: %s\n%s" % [rig.get_current_animation(), ", ".join(rig.get_shown_skins()), last]
