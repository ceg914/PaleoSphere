# 사용법: godot --headless --path . --script res://tools/godot/measure_gait.gd -- res://경로/모델.fbx
# 걷기·달리기 애니메이션에서 발끝이 몸에 대해 앞뒤로 움직이는 폭을 재서,
# 발이 미끄러지지 않으려면 몸이 얼마나 빨리 가야 하는지(모델 단위/초) 추정한다.
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
	var path := OS.get_cmdline_user_args()[0]
	model_root = (load(path) as PackedScene).instantiate()
	get_root().add_child(model_root)

func _run() -> void:
	var player: AnimationPlayer = model_root.find_children("*", "AnimationPlayer", true, false)[0]
	var skel: Skeleton3D = model_root.find_children("*", "Skeleton3D", true, false)[0]
	var to_root := Transform3D.IDENTITY
	var n: Node = skel
	while n != model_root and n != null:
		if n is Node3D:
			to_root = (n as Node3D).transform * to_root
		n = n.get_parent()
	var feet := []
	for i in skel.get_bone_count():
		var bone := skel.get_bone_name(i)
		if bone.ends_with("Foot.L_end") or bone.ends_with("Foot.R_end"):
			feet.append(i)
	for anim_name in player.get_animation_list():
		if not ("Walk" in anim_name or "Run" in anim_name or "Idle" in anim_name):
			continue
		var anim := player.get_animation(anim_name)
		player.play(anim_name)
		var samples := 240
		var dt := anim.length / samples
		var best_range := 0.0
		var ground_speed := 0.0
		for f in feet:
			var zs := []
			var ys := []
			for s in samples + 1:
				player.seek(minf(anim.length * s / samples, anim.length - 0.0001), true)
				var p: Vector3 = to_root * skel.get_bone_global_pose(f).origin
				zs.append(p.z)
				ys.append(p.y)
			var zmin: float = zs.min()
			var zmax: float = zs.max()
			var ymin: float = ys.min()
			var yrange: float = maxf(ys.max() - ymin, 0.001)
			# 발이 땅에 닿아 있는 동안(높이 하위 15%) 발이 뒤로 가는 속도 = 몸이 앞으로 가는 속도.
			var speeds := []
			for s in samples:
				if ys[s] < ymin + yrange * 0.15 and ys[s + 1] < ymin + yrange * 0.15:
					var v: float = -(zs[s + 1] - zs[s]) / dt
					if v > 0.0:
						speeds.append(v)
			speeds.sort()
			var median: float = speeds[speeds.size() / 2] if speeds.size() > 0 else 0.0
			if zmax - zmin > best_range:
				best_range = zmax - zmin
				ground_speed = median
		var root_start := Vector3.ZERO
		var root_end := Vector3.ZERO
		var speed := ground_speed
		player.seek(0.0, true)
		root_start = to_root * skel.get_bone_global_pose(0).origin
		player.seek(anim.length * 0.999, true)
		root_end = to_root * skel.get_bone_global_pose(0).origin
		print(anim_name, " 길이 ", snappedf(anim.length, 0.01), "초, 발끝 앞뒤 폭 ", snappedf(best_range, 0.01),
			", 추정 이동 속도 ", snappedf(speed, 0.01), " 단위/초",
			", 루트 이동 ", root_end - root_start)
	quit(0)
