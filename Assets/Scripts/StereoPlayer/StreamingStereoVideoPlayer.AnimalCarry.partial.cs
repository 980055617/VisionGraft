using System.Collections.Generic;
using UnityEngine;

// 犬にボールをくわえさせる（2026-10-25、ユーザー「犬に関して動画見たいに，ボール加えたいので，ハードコードでできる？口の開け閉め」）。
// 犬の動画（bundle_dog）の AniMer の口（SMAL の関節 32）は、ボールをくわえている間も 1〜7° しか開かない（ボールには 25° 前後要る。
// Docs/tmp/survey_20261025/README.md）ので、追従のデータからは口が開かない。動画のフレームで決め打ちする（AnimalCarryEntries）:
//   openFrom〜grabAt 口を開ける（頭を下げてボールへ向かう所）、grabAt〜holdFrom ボールの大きさまで閉じつつボールを口へ寄せる、holdFrom〜 くわえたまま。
// 口を開けるのは、頭の子に顎の骨（名前に jaw、AnimalRigCache.gestureJaw）があるモデルだけ（骨を作り直した犬 52_MeshyDog_c4c）。
// 顎の骨が無いモデル（今の 52_MeshyDog・狼・キツネ）は何もしない（ボールも追従の置き方のまま）。
// 歯の位置は c4c の犬の bind で測った（scratchpad dogball/jaw/gap.py）。蝶番（顎の骨の付け根）H から鼻の骨 N までの長さ s を 1 とし、
// H→N の向きを α、それに垂直で耳と反対（下）の向きを β とした座標で、上の前歯の下端 U = (0.683, 0.231)、閉じた下の前歯の上端 L = (0.653, 0.308)。
// 顎は「頭の今の world × 蝶番（左右の耳を結ぶ軸）まわりの開き × 顎の bind の局所」を毎 tick ApplyWorldRotation で書く（今の回転に積み重ねない）。
// ボールの大きさは変えない（倍率はショットの中で変えない決まり）。くわえる開きを、ボールの直径が上と下の前歯の間に入る角度にする。
public partial class StreamingStereoVideoPlayer
{
    public bool animalCarriesBallHardcoded = true;
    // くわえている間（holdFrom 以降）にボールの track が映らなくなっても、犬が見えている間は口に付けたままにする（2026-10-29、同じ日に既定 ON。
    // 見る用の版 r4 を Quest で見たユーザー「（犬がまっすぐ・ボールをくわえたまま）いいと思う」）。
    // 犬の動画のボールは f681 で画面の下から出て track が消える（犬は口にくわえたまま f698 まで映る）。今はボールが見えている間だけ口に
    // 付けるので、f681 からボールが消えて口が開いたまま。犬が横を向いて出ていく間は目立たなかったが、端に掛かった所から見る人の方へ来る
    // （bodyFrameOutStartWhenClipped）と正面を向くので空の口が見える。犬が隠れたら、付けていたボールも隠す。
    public bool animalCarryKeepsBallWhileTrackLost = true;
    private readonly HashSet<GameObject> animalCarryKeptBalls = new HashSet<GameObject>();

    private sealed class AnimalCarryEntry
    {
        public string bundle;
        public uint animalTrack;
        public uint ballTrack;
        public int openFrom;
        public int grabAt;
        public int holdFrom;
        // 歯の位置を測ったモデル。prefab の名前にこれを含むモデルだけ（別のモデルには歯の位置が合わない）。
        public string modelNameContains;
        // 上の前歯の下端 U と閉じた下の前歯の上端 L（蝶番 H から鼻 N までの長さ s を 1 とした α・β、下の説明）と、くわえている間のボールの半径 / s。
        public float upperAlpha;
        public float upperBeta;
        public float lowerAlpha;
        public float lowerBeta;
        public float ballRadiusPerS;
    }

    // 動画のフレーム（24 fps）。犬の動画: 3 秒ごろ投げられたボールは犬の前で弾んで左の地面へ転がり、犬は一度離れて戻り、
    // f150〜160 に頭を下げた口がボールに届き、f162〜170 にくわえて頭を上げ、最後（f688 ごろカメラの横を通って出る）までくわえている
    // （元の動画のコマ送りと meta.bin のボールの位置で確かめた。犬の bbox の中のボールの高さが f150〜162 は下端、f164〜170 で頭の高さへ上がる）。
    // 歯の位置（α・β）とボールの半径 / s はモデルごと:
    // - 犬（52_MeshyDog_c4c）: U = (0.683, 0.231)、L = (0.653, 0.308)。ボールの半径 0.212 × s（実物のテニスボール 半径 3.35 cm をこの犬
    //   （H→N 0.158、実物大の Labrador）に合わせた値で、くわえている間の配置の大きさの揺れ（s の 0.17〜0.28 倍、10/25 の carry の撮影）の中央値 0.21 とも合う）
    // - 狼（54_MeshyWolf_mouth、2026-10-25 の Workflow wolf-mouth-build の wA）: 唇は閉じた形で上下が同じ点なので U = L = (0.7817, 0.2144)
    //   （前の唇の中心線の点を 1 cm 奥へ。唇の先だとボールが唇の先に載った）。ボールは野球（半径 3.65 cm）で 0.2001 × s（s 0.1824 m）。
    //   作る人の見積もりのくわえる開き 26.3°（押しつぶし 0.92）。今の 54_MeshyWolf・54_MeshyWolf_c4c は顎の骨が無いので何もしない
    //   2026-10-29: その口を実験の狼（c4k3）へ移した 54_MeshyWolf_c4k_mouth（試作、Pilot/）の値へ替えた: U = L = (0.7819, 0.2365)、
    //   ボールの半径 0.2003 × s（s 0.1822 m）、くわえる開き 26.07°（scratchpad rig/r5/wolf_c4k3_mouth3/landmarks.json）。c4c の口の値
    //   (0.7817, 0.2144)・0.2001 だと 4 mm 上にずれる。2026-10-29 から Resources の 54_MeshyWolf もこの FBX（wolf_c4k_mouth.fbx、見る用の版 r5 に入れた。
    //   r5 を見たユーザーは「いいと思う」、ただし端末の記録では狼を出していない）
    private static readonly AnimalCarryEntry[] AnimalCarryEntries =
    {
        new AnimalCarryEntry
        {
            bundle = "bundle_dog.svb", animalTrack = 0, ballTrack = 1, openFrom = 136, grabAt = 150, holdFrom = 160, modelNameContains = "MeshyDog",
            upperAlpha = 0.683f, upperBeta = 0.231f, lowerAlpha = 0.653f, lowerBeta = 0.308f, ballRadiusPerS = 0.212f,
        },
        new AnimalCarryEntry
        {
            bundle = "bundle_dog.svb", animalTrack = 0, ballTrack = 1, openFrom = 136, grabAt = 150, holdFrom = 160, modelNameContains = "MeshyWolf",
            upperAlpha = 0.7819f, upperBeta = 0.2365f, lowerAlpha = 0.7819f, lowerBeta = 0.2365f, ballRadiusPerS = 0.2003f,
        },
    };

    // 毛のボールを歯で少し押しつぶす（直径のこの割合まで閉じる）。
    private const float CarryBallSqueeze = 0.92f;
    // くわえている間のボールの大きさは決まった値（entry.ballRadiusPerS × s）。ボール（Else）の倍率はショットで固定されず毎フレーム bbox から決まり、
    // くわえている間に半径が ±25% 揺れた（f168〜672 で s の 0.17〜0.28 倍、口の開きが 18〜36° で動く。10/25 の carry の撮影）。
    private const float CarryMaxOpenDeg = 40f;
    private const float CarryGrabExtraDeg = 10f;

    private sealed class AnimalCarryRig
    {
        public bool resolved;
        public bool valid;
        public Transform head;
        public Transform jaw;
        public Transform nose;
        public Transform earA;
        public Transform earB;
        public Quaternion jawBindLocal;
        public float openSign;
        public bool jawWritten;
        public bool loggedHold;
        public int lastLoggedFrame = -1;
    }

    private sealed class AnimalCarryBall
    {
        public Vector3 centerLocal;
        public Renderer[] renderers;
    }

    private readonly Dictionary<GameObject, AnimalCarryRig> animalCarryRigs = new Dictionary<GameObject, AnimalCarryRig>();
    private readonly Dictionary<GameObject, AnimalCarryBall> animalCarryBalls = new Dictionary<GameObject, AnimalCarryBall>();

    // DisplayModelTick の最後（全 track の配置・姿勢・ボールの補正の後）に呼ぶ。
    private void ApplyAnimalCarryForFrame(int frame)
    {
        if (!animalCarriesBallHardcoded || animalPoseApplier == null)
        {
            return;
        }

        string bundle = System.IO.Path.GetFileName(bundleFileName);
        for (int i = 0; i < AnimalCarryEntries.Length; i++)
        {
            AnimalCarryEntry entry = AnimalCarryEntries[i];
            if (entry.bundle == bundle)
            {
                ApplyAnimalCarry(entry, frame);
            }
        }
    }

    private void ApplyAnimalCarry(AnimalCarryEntry entry, int frame)
    {
        GameObject animal = GetTrackInstanceOrNull(entry.animalTrack);
        if (animal == null || !animal.activeInHierarchy)
        {
            HideAnimalCarryKeptBall(entry);
            return;
        }

        ReplaceableModel model = animal.GetComponent<ReplaceableModel>();
        if (model == null || string.IsNullOrEmpty(model.sourcePrefabName) || !model.sourcePrefabName.Contains(entry.modelNameContains))
        {
            return;
        }

        AnimalCarryRig rig = GetAnimalCarryRig(animal);
        if (rig == null || !rig.valid || rig.head == null || rig.jaw == null)
        {
            return;
        }

        Vector3 hinge = rig.jaw.position;
        Vector3 toNose = rig.nose.position - hinge;
        float s = toNose.magnitude;
        Vector3 lateral = rig.earA.position - rig.earB.position;
        if (s < 1e-5f || lateral.sqrMagnitude < 1e-10f)
        {
            return;
        }

        lateral.Normalize();
        Vector3 e1 = toNose / s;
        Vector3 e2 = Vector3.Cross(lateral, e1).normalized;
        Vector3 earMid = 0.5f * (rig.earA.position + rig.earB.position);
        if (Vector3.Dot(e2, earMid - hinge) > 0f)
        {
            e2 = -e2;
        }

        Vector3 upper = hinge + s * (entry.upperAlpha * e1 + entry.upperBeta * e2);
        Vector3 lowerClosed = hinge + s * (entry.lowerAlpha * e1 + entry.lowerBeta * e2);

        GameObject ball = GetTrackInstanceOrNull(entry.ballTrack);
        if (animalCarryKeepsBallWhileTrackLost && ball != null)
        {
            if (frame >= entry.holdFrom && !IsTrackInFrame(entry.ballTrack, frame))
            {
                // くわえている間にボールの track が映らなくなった（画面の下から出た）。犬が見えている間は出して口に付ける。
                SceneObjectWriter.ApplyActive(ball, true);
                animalCarryKeptBalls.Add(ball);
            }
            else
            {
                animalCarryKeptBalls.Remove(ball);
            }
        }

        bool ballVisible = ball != null && ball.activeInHierarchy;
        AnimalCarryBall ballInfo = ballVisible ? GetAnimalCarryBall(ball) : null;
        float ballRadius = ballInfo != null ? AnimalCarryBallRadius(ballInfo) : 0f;

        // くわえる開きは、くわえている間の決まった大きさのボールに合わせる（ボールの今の大きさには付いていかない）。
        float holdRadius = entry.ballRadiusPerS * s;
        float holdDeg = SolveAnimalCarryOpen(hinge, lateral, rig.openSign, upper, lowerClosed, 2f * holdRadius * CarryBallSqueeze);

        float grabDeg = Mathf.Min(CarryMaxOpenDeg, holdDeg + CarryGrabExtraDeg);
        float openDeg;
        float attach;
        if (frame < entry.openFrom)
        {
            openDeg = 0f;
            attach = 0f;
        }
        else if (frame < entry.grabAt)
        {
            openDeg = grabDeg * Mathf.SmoothStep(0f, 1f, (frame - entry.openFrom) / (float)Mathf.Max(1, entry.grabAt - entry.openFrom));
            attach = 0f;
        }
        else if (frame < entry.holdFrom)
        {
            float t = Mathf.SmoothStep(0f, 1f, (frame - entry.grabAt) / (float)Mathf.Max(1, entry.holdFrom - entry.grabAt));
            openDeg = Mathf.Lerp(grabDeg, holdDeg, t);
            attach = t;
        }
        else
        {
            openDeg = holdDeg;
            attach = 1f;
        }

        if (openDeg > 0.01f)
        {
            Quaternion open = Quaternion.AngleAxis(rig.openSign * openDeg, lateral);
            TransformWriter.ApplyWorldRotation(rig.jaw, open * rig.head.rotation * rig.jawBindLocal);
            rig.jawWritten = true;
        }
        else if (rig.jawWritten)
        {
            // 周の頭へ戻ったら一度だけ bind（頭 × bind の局所）へ戻す。
            TransformWriter.ApplyWorldRotation(rig.jaw, rig.head.rotation * rig.jawBindLocal);
            rig.jawWritten = false;
            rig.loggedHold = false;
        }

        if (ballInfo == null || attach <= 0f || ballRadius < 1e-6f)
        {
            return;
        }

        // ボールの大きさを、配置の大きさから決まった大きさへ（寄せるのと同じ重みで）。配置が毎 tick 倍率を書き直すので積み重ならない。
        float radius = Mathf.Lerp(ballRadius, holdRadius, attach);
        ball.transform.localScale *= radius / ballRadius;
        Vector3 lowerOpen = hinge + Quaternion.AngleAxis(rig.openSign * openDeg, lateral) * (lowerClosed - hinge);
        Vector3 mid = 0.5f * (upper + lowerOpen);
        Vector3 forward = (mid - hinge).normalized;
        Vector3 target = mid + forward * (0.3f * radius);
        Vector3 current = ball.transform.TransformPoint(ballInfo.centerLocal);
        Vector3 placed = Vector3.Lerp(current, target, attach);
        ball.transform.position += placed - current;

        if (attach >= 1f && !rig.loggedHold)
        {
            rig.loggedHold = true;
            Debug.Log($"[CARRY] hold f={frame} animal={animal.name} ball={ball.name} open={openDeg:F1}deg ballR={radius:F4}m（配置の大きさ {ballRadius:F4}m） " +
                      $"gap={(upper - lowerOpen).magnitude:F4}m s={s:F4}m");
        }

        // くわえている間の開きとボールの大きさ（1 秒ごと。配置の大きさは毎フレーム bbox から決まり揺れる）。
        if (attach >= 1f && frame % 24 == 0 && frame != rig.lastLoggedFrame)
        {
            rig.lastLoggedFrame = frame;
            Debug.Log($"[CARRY] f={frame} open={openDeg:F1}deg ballR={radius:F4}m（配置の大きさ {ballRadius:F4}m） s={s:F4}m");
        }
    }

    // 上の前歯と、開いた下の前歯の間がボールの直径（押しつぶし込み）になる開き（二分法、0〜CarryMaxOpenDeg）。
    private static float SolveAnimalCarryOpen(Vector3 hinge, Vector3 lateral, float openSign, Vector3 upper, Vector3 lowerClosed, float targetGap)
    {
        float lo = 0f;
        float hi = CarryMaxOpenDeg;
        if (Gap(hi) < targetGap)
        {
            return hi;
        }

        if (Gap(lo) >= targetGap)
        {
            return lo;
        }

        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Gap(mid) < targetGap)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return hi;

        float Gap(float deg)
        {
            Vector3 lower = hinge + Quaternion.AngleAxis(openSign * deg, lateral) * (lowerClosed - hinge);
            return (upper - lower).magnitude;
        }
    }

    private AnimalCarryRig GetAnimalCarryRig(GameObject animal)
    {
        if (animalCarryRigs.TryGetValue(animal, out AnimalCarryRig rig) && rig.resolved &&
            (!rig.valid || (rig.head != null && rig.jaw != null && rig.nose != null && rig.earA != null && rig.earB != null)))
        {
            return rig;
        }

        PruneDestroyedCarryEntries();

        if (!animalPoseApplier.TryGetJawRig(animal.transform, out Transform head, out Transform jaw, out Quaternion jawBindLocal,
                out Transform earLeft, out Transform earRight, out bool cacheReady))
        {
            if (!cacheReady)
            {
                return null;
            }

            rig = new AnimalCarryRig { resolved = true, valid = false };
            animalCarryRigs[animal] = rig;
            Debug.Log($"[CARRY] model={animal.name} は頭の子に顎の骨が無い（か耳が無い） → 口を開けずボールも付けない");
            return rig;
        }

        Transform nose = null;
        for (int i = 0; i < head.childCount && nose == null; i++)
        {
            Transform child = head.GetChild(i);
            if (child.name.ToLowerInvariant().Contains("nose"))
            {
                nose = child;
            }
        }

        rig = new AnimalCarryRig
        {
            resolved = true,
            valid = nose != null,
            head = head,
            jaw = jaw,
            nose = nose,
            earA = earLeft,
            earB = earRight,
            jawBindLocal = jawBindLocal,
            openSign = 1f,
        };
        animalCarryRigs[animal] = rig;
        if (!rig.valid)
        {
            Debug.Log($"[CARRY] model={animal.name} は頭の子に鼻の骨が無い → 口を開けずボールも付けない");
            return rig;
        }

        // + の向きに回して下の前歯が耳から離れる（下がる）なら +1。bind の形から 1 回だけ決める（耳・顎・鼻の付け根は頭に対して動かない）。
        Vector3 hinge = jaw.position;
        Vector3 lateral = (earLeft.position - earRight.position).normalized;
        Vector3 earMid = 0.5f * (earLeft.position + earRight.position);
        Vector3 toNose = nose.position - hinge;
        Vector3 turned = Quaternion.AngleAxis(10f, lateral) * toNose;
        rig.openSign = Vector3.Dot(turned - toNose, earMid - hinge) < 0f ? 1f : -1f;
        Debug.Log($"[CARRY] rig model={animal.name} jaw={jaw.name} nose={nose.name} ears={earLeft.name}/{earRight.name} " +
                  $"s={toNose.magnitude:F4}m openSign={rig.openSign:+0;-0}");
        return rig;
    }

    // animalCarryKeepsBallWhileTrackLost で出していたボールを、犬が隠れたら一緒に隠す。
    private void HideAnimalCarryKeptBall(AnimalCarryEntry entry)
    {
        GameObject ball = GetTrackInstanceOrNull(entry.ballTrack);
        if (ball != null && animalCarryKeptBalls.Remove(ball))
        {
            SceneObjectWriter.ApplyActive(ball, false);
        }
    }

    private AnimalCarryBall GetAnimalCarryBall(GameObject ball)
    {
        if (animalCarryBalls.TryGetValue(ball, out AnimalCarryBall info) && info != null && info.renderers.Length > 0 && info.renderers[0] != null)
        {
            return info;
        }

        Renderer[] renderers = ball.GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0)
        {
            return null;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        info = new AnimalCarryBall { centerLocal = ball.transform.InverseTransformPoint(bounds.center), renderers = renderers };
        animalCarryBalls[ball] = info;
        return info;
    }

    // モデルを替えるとインスタンスは壊して作り直される（ModelPicker）。壊れたインスタンスの控えを捨てる。
    private void PruneDestroyedCarryEntries()
    {
        var dead = new List<GameObject>();
        foreach (GameObject key in animalCarryRigs.Keys)
        {
            if (key == null)
            {
                dead.Add(key);
            }
        }

        foreach (GameObject key in animalCarryBalls.Keys)
        {
            if (key == null)
            {
                dead.Add(key);
            }
        }

        foreach (GameObject key in dead)
        {
            animalCarryRigs.Remove(key);
            animalCarryBalls.Remove(key);
        }
    }

    // ボールの半径（world）。球の AABB は回っても同じなので、3 辺の半分の平均。
    private static float AnimalCarryBallRadius(AnimalCarryBall info)
    {
        Bounds bounds = info.renderers[0].bounds;
        for (int i = 1; i < info.renderers.Length; i++)
        {
            bounds.Encapsulate(info.renderers[i].bounds);
        }

        return (bounds.extents.x + bounds.extents.y + bounds.extents.z) / 3f;
    }
}
