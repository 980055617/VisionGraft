using UnityEngine;

// 視線の向きの補正（2026-10-04、既定 OFF: alignSmplToViewRay、調査役 HM の F4）。
//
// HMR2 の globalOrient は人の crop（bbox）を正面から見たカメラ基準のまま bundle に入っている
// （反論役 C-HM の確認: meta の globalOrient は HMR2 の出力と完全に一致し、keypoints2d は keypoints3d を f=25000 px
//  でほぼ正射影したものと一致する。生成側は視線の向きを補正していない）。runtime はそれを fovx 70° のピンホールで、
// 画面中央から外れた位置（斜めの視線）に置くので、人が画面の端にいるほど向きがずれて見える（画像上の胴の線で約 3°、C-HM）。
// crop の中心への視線 d = ((u − cx)/fx, (cy − v)/fy, 1) へカメラの前方を回す R_ray = FromToRotation(forward, d) を、
// SMPL の camRotation と keypoints（jointsCam）の両方に右から掛ける（全身を剛体のまま回すだけで、スキンには影響しない）。
// 大きさは runtime の FOV（70°）に依る。FOV を変えたら同じ式で追従する（TryGetProjectionIntrinsics を毎フレーム使う）。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private bool TryResolveViewRayRotation(MetaObj obj, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0 ||
            !TryGetProjectionIntrinsics(out float fx, out float fy, out _, out _))
        {
            return false;
        }

        // bbox は eye 画素（右目の物体は eye_w ぶん右にある）。crop の中心 ≒ bbox の中心（HMR2 の crop 中心との差は最大 4.3 px、HM）。
        float u = obj.bboxX + obj.bboxW * 0.5f;
        if (u >= manifest.eye_w)
        {
            u -= manifest.eye_w;
        }

        float v = obj.bboxY + obj.bboxH * 0.5f;
        Vector3 dir = ReconstructCamLocalFromEyePixel(u, v, 1f, fx, fy, manifest.eye_w, manifest.eye_h);
        if (dir.sqrMagnitude < 1e-10f)
        {
            return false;
        }

        rotation = Quaternion.FromToRotation(Vector3.forward, dir.normalized);
        return IsFinite(rotation);
    }

    // keypoints（anchor 基準のカメラ座標）にも同じ R_ray を掛け直す。SMPL 目標の AimAt（E）は camRotation から来るので不要だが、
    // keypoint の AimAt と足の高さ合わせ（AlignHumanoidFeetYToSmplAnkles）が SMPL と食い違わないように揃える。
    private void ApplyViewRayToPersonJoints(ref PersonPoseWorldData pose, Transform screen, Quaternion viewRay)
    {
        if (pose.jointsWorld == null || pose.jointsCam == null || !TryGetPinholeBasis(screen, out _, out Quaternion camRotation))
        {
            return;
        }

        int n = Mathf.Min(pose.jointCount, Mathf.Min(pose.jointsWorld.Length, pose.jointsCam.Length));
        for (int i = 0; i < n; i++)
        {
            pose.jointsWorld[i] = pose.rootWorld + camRotation * (viewRay * pose.jointsCam[i]);
        }
    }
}
