using System.Collections.Generic;
using UnityEngine;

// 出口で近づいてくる人・動物が遅く小さい件の直し 2 つ（2026-10-29、新しい振る舞い・どちらも既定 OFF）。全体の洗い出しで「テレビの女性は 22〜25 秒に
// カメラの目の前まで来るが、モデルは 0.88 倍 → 半分の大きさで約 1 秒遅れる」「犬は 28〜30 秒にカメラの左を抜けるが、モデルは小さいまま 1 秒遅れる」。
// 調べ（scratchpad validate/combined/approach/、Docs/tmp/survey_20261025/README.md「近づき方の調査の結果」、予測 approach/predictions.txt）:
//   A placementFollowVideoDepthEdgeFollowsSmplDepth: 女性でいちばん崩れるのは続きが始まる前の端の Hold（f528〜542、大きさの比 0.87 → 0.63）。Hold は大きさ f を
//     止め、奥行きを動画の奥行き Zv に置くが、この人の Zv は近づきを表していない（z01 0.44 → 0.47 → 0.41）のでモデルは逆に 0.920 → 1.013 m へ下がる。
//     → Hold の端の間、SMPL のある人は合わせる先を「端の前の最後の合わせる点の深さ × tz ÷ 端の前の tz」と Zv の手前の方にする（tz = SMPL の transl z、
//     このロックの端の間の最小値 = 近づく向きだけ）。f は止めたまま、姿勢の追従も今のまま、合わせる点の光線に沿った奥行きだけが変わる。
//     tz を使う理由: 女性の切れたフレーム f528〜563 で、箱の幅（左右は切れていない）と 1/tz が平均 +3%・ばらつき 4% で合う。動物には使わない
//     （AniMer の tz は犬の最後の 0.25 秒を ×1.31 しか読まず、箱の幅は ×1.94）。
//     予測: z f528/536/542 0.920/1.011/0.991 → 0.899/0.844/0.793、比 0.85/0.70/0.63 → 0.87/0.83/0.79、下がる動きが無くなる。危険: 足が画面の下から
//     はみ出すのが早まる。実験の動画で効くのはテレビの出口だけ（サッカー・猫は端に触れない、犬は動物）。
//   C2 bodyFrameOutLeaveViewWithTrack: 続きの速さ（BodyFrameOut の ObserveBodyFrameOutTracked）は Hold の間の root の動き（ほぼ横すべり）の EMA で、
//     近づく速さではない（女性 0.232 m/s・犬 0.196 m/s、モデルが大きくなる速さは元の約半分）。→ 続きの始めに、先読みで分かる元の track が消える
//     フレームで、投影に使う骨が全部、動画の枠（manifest の fovx と片目の縦横、上下左右と目の面の 5 つ）を抜ける速さに上げる（遅くはしない、上限は今の
//     2.5 × 高さ）。向き・始めの位置・終わり方・大きさは今のまま。動画の枠で決めるので実機とバッチで同じ（実機では枠を越えた後も画面の横で見える時間が残る、車と同じ）。
//     予測: 女性（A と組み）0.257 → 0.330 m/s、比 f552〜568 0.82〜0.90・f576 1.0〜1.1、f581 に消える（元 f580、今 f612〜623）。犬 0.196 → 0.630 m/s、
//     f681 の比 0.36 → 0.43、f700 に消える（元 f699）。危険: 犬の歩きのクリップは速さに合わせないので足が滑る。
//     結果（撮影 stage2・stage2b）: 女性は A だけで f579 に消え C2 の上乗せなし。犬 0.192 → 0.745 m/s・狼 0.174 → 0.783 m/s（どちらも上限）で犬 f702・
//     狼 f701 に消える。頭は目の横 0.202 m を通る（近さの下限 0.25 m より近い）。
// どちらも OFF なら今と全く同じ。
public partial class StreamingStereoVideoPlayer
{
    public bool placementFollowVideoDepthEdgeFollowsSmplDepth = false;
    public bool bodyFrameOutLeaveViewWithTrack = false;

    private sealed class VideoDepthEdgeApproachRef
    {
        public object state;        // 控えたときの VideoDepthFollowState（ロックが替わったら使わない）
        public float refMatch;      // 端に掛かっていない最後に測ったフレームの合わせる点の深さ（m）
        public float refTz;         // そのフレームの SMPL の transl z
        public float minTz;         // それから後の端の間の transl z の最小値（近づく向きだけ）
        public int refFrame = -1;
        public int loggedFrame = -1;
    }

    private readonly Dictionary<uint, VideoDepthEdgeApproachRef> videoDepthEdgeApproachByTrack = new Dictionary<uint, VideoDepthEdgeApproachRef>();

    // 合わせる先の深さ（純粋な計算、テスト ExitApproachTests）。minTz が refTz より小さい（近づいた）ときだけ、refMatch × minTz ÷ refTz と targetDepth の手前の方。
    // minDepth より手前にはしない。近づいていない・値が使えないときは targetDepth のまま。
    internal static float ResolveVideoDepthEdgeApproachDepth(float targetDepth, float refMatch, float refTz, float minTz, float minDepth)
    {
        if (!(refMatch > 0f) || !(refTz > 0.0001f) || !(minTz > 0.0001f) || !(minTz < refTz) || !(targetDepth > 0f))
        {
            return targetDepth;
        }

        float approach = refMatch * minTz / refTz;
        return Mathf.Max(minDepth, Mathf.Min(targetDepth, approach));
    }

    // MeasurePlacementFollowVideoDepth の控えから（hold でない、このフレームで初めて測った tick）。端に掛かっていない人のフレームを控え、端の間の最小値を始め直す。
    private void NoteVideoDepthEdgeApproachRef(object st, MetaObj target, int frame, bool atEdge, float match)
    {
        if (!placementFollowVideoDepthEdgeFollowsSmplDepth || atEdge || !(match > 0f) || !IsCategoryPerson(target.categoryId) ||
            !TryGetHumanSmplPose(frame, target.trackId, out HumanSmplPose pose) || !pose.hasTransl || !(pose.transl.z > 0.0001f))
        {
            return;
        }

        if (!videoDepthEdgeApproachByTrack.TryGetValue(target.trackId, out VideoDepthEdgeApproachRef r) || r == null)
        {
            r = new VideoDepthEdgeApproachRef();
            videoDepthEdgeApproachByTrack[target.trackId] = r;
        }

        r.state = st;
        r.refMatch = match;
        r.refTz = pose.transl.z;
        r.minTz = pose.transl.z;
        r.refFrame = frame;
    }

    // TryResolveVideoDepthFollowEdgeHoldRatio から（Hold の端の間、その場・ゆっくり変える形でない tick）。
    private float ApplyVideoDepthEdgeApproach(object st, MetaObj obj, float targetDepth, bool edgeHoldOnly)
    {
        if (!placementFollowVideoDepthEdgeFollowsSmplDepth || !edgeHoldOnly || !IsCategoryPerson(obj.categoryId) ||
            !videoDepthEdgeApproachByTrack.TryGetValue(obj.trackId, out VideoDepthEdgeApproachRef r) || r == null || !ReferenceEquals(r.state, st))
        {
            return targetDepth;
        }

        int frame = GetCurrentPlaybackFrame();
        if (!TryGetHumanSmplPose(frame, obj.trackId, out HumanSmplPose pose) || !pose.hasTransl || !(pose.transl.z > 0.0001f))
        {
            return targetDepth;
        }

        // 端の間の最小値（女性は画面から出ていくとき tz が 31 → 38 → 79 → 98 と跳ぶので、最小値で奥へ戻さない）。
        r.minTz = Mathf.Min(r.minTz, pose.transl.z);
        float depth = ResolveVideoDepthEdgeApproachDepth(targetDepth, r.refMatch, r.refTz, r.minTz, MinDistanceFromHeadMeters + 0.1f);
        if (logPlacementFollowVideoDepth && depth != targetDepth && r.loggedFrame != frame)
        {
            r.loggedFrame = frame;
            Debug.Log($"[VFOLLOW-EDGE] f={frame} track={obj.trackId} target {targetDepth:F4} -> {depth:F4}m (ref match {r.refMatch:F4} tz {r.refTz:F4} @f{r.refFrame}, " +
                      $"min tz {r.minTz:F4}, tz {pose.transl.z:F4})");
        }

        return depth;
    }

    // 続きの速さ（純粋な計算、テスト ExitApproachTests）: 消えるまでの秒で leaveDistance を進む速さ（0.3〜2.5 × 高さ）が今より速ければそれ、遅ければ今のまま。
    internal static float ResolveBodyFrameOutLeaveViewSpeed(float speed, float leaveDistance, float seconds, float height)
    {
        if (!(leaveDistance > 0f) || !(height > 0f))
        {
            return speed;
        }

        float leave = Mathf.Clamp(leaveDistance / Mathf.Max(0.05f, seconds), 0.3f * height, 2.5f * height);
        return leave > speed ? leave : speed;
    }

    // 点 p（ピンホールの座標、x 右・y 上・z 前）が向き h に s 進むとき、動画の枠（|x| ≤ tanH·z、|y| ≤ tanV·z、z ≥ 0.05）の中を通る区間 [sIn, sOut] の
    // 出口までの距離（純粋な計算、テスト ExitApproachTests）。区間が無い（枠を通らない）・もう出た後なら 0、出ない（ずっと中）なら +∞。
    // まっすぐ進む点は凸な枠を 1 区間だけ通るので、どの面から出てもよい（犬は左下・下の面から抜ける）。外から入って後で抜ける点もこれで数える。
    internal static float ResolveLeaveViewDistanceForPoint(Vector3 p, Vector3 h, float tanH, float tanV)
    {
        float sIn = float.NegativeInfinity;
        float sOut = float.PositiveInfinity;
        bool empty = false;
        Clip(tanH * p.z - p.x, tanH * h.z - h.x);
        Clip(tanH * p.z + p.x, tanH * h.z + h.x);
        Clip(tanV * p.z - p.y, tanV * h.z - h.y);
        Clip(tanV * p.z + p.y, tanV * h.z + h.y);
        Clip(p.z - 0.05f, h.z);
        if (empty || sIn > sOut)
        {
            return 0f;
        }

        return sOut > 0f ? sOut : 0f;

        // 中の条件 f0 + s·f1 ≥ 0 で区間を切る。
        void Clip(float f0, float f1)
        {
            if (Mathf.Abs(f1) < 1e-9f)
            {
                if (f0 < 0f)
                {
                    empty = true;
                }

                return;
            }

            float s = -f0 / f1;
            if (f1 > 0f)
            {
                sIn = Mathf.Max(sIn, s);
            }
            else
            {
                sOut = Mathf.Min(sOut, s);
            }
        }
    }

    // 投影に使う骨（ResolveProjectionBones、横を通る決まりと同じ骨。無ければ描画の箱の角）が、進む向き heading に進んで、動画の枠（ピンホールの camOrigin・
    // camRotation、manifest の fovx と片目の縦横）を全部抜けるまでの距離（点ごとの出口の最大）。ずっと枠の中に残る点があれば false（速さは上げない）。
    // 描画の箱の角だと、見えるメッシュが消えてからも 3〜12 フレーム以上枠に残る（1 段目の撮影、approach/）。
    private bool TryResolveBodyFrameOutLeaveViewDistance(GameObject instance, Vector3 heading, Vector3 camOrigin, Quaternion camRotation,
        out float distance)
    {
        distance = 0f;
        if (instance == null || heading.sqrMagnitude < 1e-10f)
        {
            return false;
        }

        Quaternion inv = Quaternion.Inverse(camRotation);
        Vector3 h = inv * heading.normalized;
        float fovx = manifest != null && manifest.fovx_deg > 1f ? manifest.fovx_deg : 70f;
        float tanH = Mathf.Tan(0.5f * Mathf.Deg2Rad * fovx);
        float aspect = manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0 ? (float)manifest.eye_h / manifest.eye_w : 720f / 1280f;
        float tanV = tanH * aspect;
        bool any = false;
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        List<KeyValuePair<string, Transform>> bones = ResolveProjectionBones(instance, animator);
        for (int i = 0; i < bones.Count; i++)
        {
            Transform t = bones[i].Value;
            if (t == null)
            {
                continue;
            }

            float s = ResolveLeaveViewDistanceForPoint(inv * (t.position - camOrigin), h, tanH, tanV);
            if (float.IsInfinity(s))
            {
                return false;
            }

            distance = Mathf.Max(distance, s);
            any = true;
        }

        if (any)
        {
            return true;
        }

        foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !r.enabled || !TryGetRendererLocalBounds(r, out Bounds local))
            {
                continue;
            }

            Matrix4x4 m = r.localToWorldMatrix;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                float s = ResolveLeaveViewDistanceForPoint(inv * (m.MultiplyPoint3x4(corner) - camOrigin), h, tanH, tanV);
                if (float.IsInfinity(s))
                {
                    return false;
                }

                distance = Mathf.Max(distance, s);
                any = true;
            }
        }

        return any;
    }
}
