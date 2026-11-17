# 사용법: godot --headless --path . --script res://tools/godot/measure_model.gd -- res://경로/모델.fbx [애니메이션 이름 일부]
# 모델을 장면에 올려 첫 애니메이션(또는 지정한 것)의 0초 자세에서 뼈 끝점의 범위를 잰다.
# 머리 끝(Head_end)과 꼬리 끝(Tail5_end)으로 몸길이와 앞 방향을 구한다.
extends SceneTree

var _done := false

func _process(_delta: float) -> bool:
	if _done:
		return true
	_done = true
	_run()
	return true

var model_root: Node3D

func _initialize() -> void:
	model_root = (load(OS.get_cmdline_user_args()[0]) as PackedScene).instantiate()
	get_root().add_child(model_root)

func _run() -> void:
	var args := OS.get_cmdline_user_args()
	var player: AnimationPlayer = model_root.find_children("*", "AnimationPlayer", true, false)[0]
	var wanted := args[1] if args.size() > 1 else "Idle"
	for anim_name in player.get_animation_list():
		if wanted in anim_name:
			player.play(anim_name)
			player.seek(0.0, true)
			print("자세: ", anim_name)
			break
	player.advance(0.0)
	var skel: Skeleton3D = model_root.find_children("*", "Skeleton3D", true, false)[0]
	# 아직 트리에 들어가기 전이라 global_transform을 못 쓰므로 부모 변환을 직접 곱한다.
	var to_root := Transform3D.IDENTITY
	var n: Node = skel
	while n != model_root and n != null:
		if n is Node3D:
			to_root = (n as Node3D).transform * to_root
		n = n.get_parent()
	var lo := Vector3(INF, INF, INF)
	var hi := Vector3(-INF, -INF, -INF)
	var named := {}
	for i in skel.get_bone_count():
		var p: Vector3 = to_root * skel.get_bone_global_pose(i).origin
		lo = lo.min(p)
		hi = hi.max(p)
		named[skel.get_bone_name(i)] = p
	print("뼈 범위 최소 ", lo, " 최대 ", hi, " 크기 ", hi - lo)
	for key in ["model_root", "Hips", "Head", "Head_end", "Tail5_end", "FrontFoot.L_end", "BackFoot.L_end", "BackLowLeg.L_end", "FrontLowLeg.L_end", "Shoulders"]:
		if named.has(key):
			print("  ", key, " ", named[key])
	if named.has("Head_end") and named.has("Tail5_end"):
		var d: Vector3 = named["Head_end"] - named["Tail5_end"]
		print("머리끝-꼬리끝 거리 ", d.length(), " 방향(꼬리→머리) ", d.normalized())
	quit(0)
