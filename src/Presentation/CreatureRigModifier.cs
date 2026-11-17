using Godot;

namespace PaleoSphere.Presentation;

/// <summary>
/// 재생 중인 애니메이션 위에 자세를 덧입힌다. 원본 팩에는 먹기·마시기·쉬기 동작이 없어서
/// 대기 동작을 재생하면서 목·머리를 숙이거나(Graze) 다리를 접고 몸을 낮춘다(Rest).
/// 회전축은 모델 좌표(앞 = +Z, 위 = +Y)로 정하고 뼈마다 지역 좌표로 바꿔 적용하므로
/// 뼈의 축 방향이 제각각이어도 같은 코드로 동작한다. 해당 뼈가 없는 모델에서는 그 부분만 건너뛴다.
/// </summary>
public partial class CreatureRigModifier : SkeletonModifier3D
{
    /// <summary>0이면 그대로, 1이면 머리를 땅 가까이 숙인다.</summary>
    public float Graze { get; set; }

    /// <summary>0이면 서 있고, 1이면 다리를 접고 엎드린다.</summary>
    public float Rest { get; set; }

    /// <summary>스켈레톤 좌표 → 모델 좌표 변환(크기 포함). 회전축과 이동량을 바꾸는 데 쓴다.</summary>
    public Transform3D SkeletonToModel { get; set; } = Transform3D.Identity;

    /// <summary>엎드릴 때 몸을 낮추는 거리(모델 단위).</summary>
    public float RestDrop { get; set; }

    private int _body = -1, _shoulders = -1, _neck = -1, _head = -1, _torso = -1;
    private readonly int[] _upper = { -1, -1, -1, -1 };
    private readonly int[] _lower = { -1, -1, -1, -1 };
    private readonly int[] _legEnd = { -1, -1, -1, -1 };
    private readonly int[] _foot = { -1, -1, -1, -1 };
    private bool _resolved;

    // 다리 순서: 앞왼쪽, 앞오른쪽, 뒤왼쪽, 뒤오른쪽.
    private static readonly string[] LegPrefix = { "Front", "Front", "Back", "Back" };
    private static readonly string[] LegSide = { ".L", ".R", ".L", ".R" };

    private void Resolve(Skeleton3D skel)
    {
        _resolved = true;
        _body = skel.FindBone("Body");
        _torso = skel.FindBone("Torso");
        _shoulders = skel.FindBone("Shoulders");
        _neck = skel.FindBone("Neck");
        _head = skel.FindBone("Head");
        for (int i = 0; i < 4; i++)
        {
            _upper[i] = skel.FindBone(LegPrefix[i] + "UpLeg" + LegSide[i]);
            _lower[i] = skel.FindBone(LegPrefix[i] + "LowLeg" + LegSide[i]);
            _legEnd[i] = skel.FindBone(LegPrefix[i] + "LowLeg" + LegSide[i] + "_end");
            _foot[i] = skel.FindBone(LegPrefix[i] + "Foot" + LegSide[i]);
        }
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        Skeleton3D skel = GetSkeleton();
        if (skel == null) return;
        if (!_resolved) Resolve(skel);
        if (Graze < 0.001f && Rest < 0.001f) return;

        // 모델의 옆 방향(+X)을 스켈레톤 좌표로. +X 축으로 양의 각만큼 돌리면 앞(+Z)이 아래로 숙여진다.
        Basis modelToSkel = SkeletonToModel.Basis.Orthonormalized().Inverse();
        Vector3 lateral = (modelToSkel * Vector3.Right).Normalized();

        if (Rest > 0.001f)
        {
            float r = Rest;
            // 몸 낮추기: Body 뼈를 모델의 아래쪽으로 옮긴다.
            if (_body >= 0)
            {
                Vector3 downSkel = SkeletonToModel.Basis.Inverse() * new Vector3(0f, -RestDrop * r, 0f);
                int parent = skel.GetBoneParent(_body);
                Basis parentBasis = parent >= 0 ? skel.GetBoneGlobalPose(parent).Basis : Basis.Identity;
                skel.SetBonePosePosition(_body, skel.GetBonePosePosition(_body) + parentBasis.Inverse() * downSkel);
            }
            // 다리 접기: 앞다리는 아래팔을 뒤로 접고, 뒷다리는 넓적다리를 앞으로 내밀고 정강이를 뒤로 접는다.
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                Rotate(skel, _upper[i], lateral, (front ? 0.35f : -1.15f) * r);
                Rotate(skel, _lower[i], lateral, (front ? 2.05f : 2.25f) * r);
            }
            Rotate(skel, _torso, lateral, 0.05f * r);
            Rotate(skel, _neck, lateral, 0.25f * r);
        }

        if (Graze > 0.001f)
        {
            float g = Graze;
            Rotate(skel, _shoulders, lateral, 0.18f * g);
            Rotate(skel, _neck, lateral, 0.55f * g);
            Rotate(skel, _head, lateral, 0.40f * g);
        }

        // 발 뼈는 루트에 붙어 있어 다리를 접어도 제자리에 남는다. 접힌 다리 끝으로 옮겨 늘어나지 않게 한다.
        if (Rest > 0.001f)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_foot[i] < 0 || _legEnd[i] < 0) continue;
                Vector3 endGlobal = skel.GetBoneGlobalPose(_legEnd[i]).Origin;
                int parent = skel.GetBoneParent(_foot[i]);
                Transform3D parentGlobal = parent >= 0 ? skel.GetBoneGlobalPose(parent) : Transform3D.Identity;
                skel.SetBonePosePosition(_foot[i], parentGlobal.AffineInverse() * endGlobal);
            }
        }
    }

    /// <summary>뼈를 스켈레톤 좌표의 축을 중심으로 angle만큼 더 돌린다(현재 애니메이션 자세 기준).</summary>
    private static void Rotate(Skeleton3D skel, int bone, Vector3 axisSkel, float angle)
    {
        if (bone < 0 || Mathf.Abs(angle) < 1e-5f) return;
        int parent = skel.GetBoneParent(bone);
        Basis parentBasis = parent >= 0 ? skel.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        var local = new Basis(skel.GetBonePoseRotation(bone));
        var spin = new Basis(axisSkel, angle);
        Basis rotated = parentBasis.Inverse() * spin * parentBasis * local;
        skel.SetBonePoseRotation(bone, rotated.Orthonormalized().GetRotationQuaternion());
    }
}
