extends SceneTree
# A picture of the demo (needs a display: xvfb-run works):
#   godot --path godot --rendering-method gl_compatibility -s tests/screenshot.gd -- out.png [frames] [skin…]

var _frames := 0
var _wait := 20
var _out := "user://demo.png"


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() > 0:
		_out = args[0]
	if args.size() > 1:
		_wait = int(args[1])
	var demo: Node = load("res://demo/demo.tscn").instantiate()
	root.add_child(demo)
	for i in range(2, args.size()):
		demo.get_node("Sample").show_skin(args[i])


func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < _wait:
		return false
	root.get_texture().get_image().save_png(_out)
	print("saved " + _out)
	return true
