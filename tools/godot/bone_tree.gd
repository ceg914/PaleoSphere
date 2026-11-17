# 사용법: godot --headless --path . --script res://tools/godot/bone_tree.gd -- res://경로/모델.fbx
# 뼈 계층과 쉬는 자세(rest)에서 각 뼈의 위치(모델 기준)를 출력한다.
extends SceneTree

var model_root: Node3D

func _initialize() -> void:
	model_root = (load(OS.get_cmdline_user_args()[0]) as PackedScene).instantiate()
	get_root().add_child(model_root)

func _process(_delta: float) -> bool:
	var skel: Skeleton3D = model_root.find_children("*", "Skeleton3D", true, false)[0]
	var to_root := Transform3D.IDENTITY
	var n: Node = skel
	while n != model_root and n != null:
		if n is Node3D:
			to_root = (n as Node3D).transform * to_root
		n = n.get_parent()
	print("skeleton→모델 변환 basis ", to_root.basis, " origin ", to_root.origin)
	for i in skel.get_bone_count():
		var depth := 0
		var p := skel.get_bone_parent(i)
		while p >= 0:
			depth += 1
			p = skel.get_bone_parent(p)
		var g: Transform3D = skel.get_bone_global_rest(i)
		print("  ".repeat(depth), skel.get_bone_name(i), "  위치 ", (to_root * g.origin).snapped(Vector3(0.01, 0.01, 0.01)), "  뼈 y축(모델) ", (to_root.basis * g.basis.y).normalized().snapped(Vector3(0.01, 0.01, 0.01)), "  x축 ", (to_root.basis * g.basis.x).normalized().snapped(Vector3(0.01, 0.01, 0.01)))
	return true
