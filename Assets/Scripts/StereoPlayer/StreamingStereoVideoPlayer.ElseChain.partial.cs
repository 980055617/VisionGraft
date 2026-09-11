using System.Collections.Generic;
using UnityEngine;

// Else の連結配置（B + C、2026-09-11）。
//
// 遠方で隣り合う Else（bundle_train の車両）は bundle の深度差が数 mm しかなく、
// bbox に合わせた大きさのモデル（全長 80〜140 mm）どうしが 3D で刺さる。
// 深度信号を良くしても届かない（理想でも 10〜24 mm）ので、モデル寸法から前後を作る。
//
//   B: 奥の Else を自分の視線に沿って奥へ。中心間距離が (手前の全長 + 自分の全長)/2 に
//      なる所まで。目を中心にした相似変換（位置と scale を同じ倍率）なので、
//      絵の位置と大きさは変わらず、立体視と遮蔽だけが変わる。
//   C: 隣との中心を結ぶ方向に長軸を向ける。先頭（キャブ）側が手前＝進行方向を向く
//      （どちらの端が先頭かは elseChainHeadingOffsetDeg で吸収）。
//
// 全 track の配置（②〜⑩）が終わった後に走る。⑧⑨ は Else の深度を bundle の anchor_z
// または人の骨格から決めるが、ここはその結果を「手前の Else に対して」だけ動かす。
// docs/bundle-placement.md「bundle_train 遠方で 1 両目と 2 両目の前後が出ない（2026-09-11 調査）」。
public partial class StreamingStereoVideoPlayer
{
    private sealed class ElseChainItem
    {
        public MetaObj obj;
        public GameObject instance;
        public ReplaceableModel model;
        public Vector3 position;
        public float depth;
        public float length;
        public float scaleFactor;
        public bool chained;
    }

    private readonly List<ElseChainItem> elseChainItems = new List<ElseChainItem>();
    // track ペア (小, 大) → +1 なら小さい id が手前、-1 なら大きい id が手前。
    // 配置深度で決められないとき（差が elseChainDepthTieMeters 以内）に使う。
    // 一度決めた順は shot 内では変えない（車両は追い越さない）。
    private readonly Dictionary<ulong, int> elseChainOrderMemory = new Dictionary<ulong, int>();
    // 連結で決めた向き（yaw、度）。連結が解けた後もこれを使い続ける。
    private readonly Dictionary<uint, float> elseChainYawByTrack = new Dictionary<uint, float>();


    private void ResetElseChainStateForShotBoundary()
    {
        elseChainOrderMemory.Clear();
        elseChainYawByTrack.Clear();
    }


    private static ulong ElseChainPairKey(uint a, uint b)
    {
        uint lo = a < b ? a : b;
        uint hi = a < b ? b : a;
        return ((ulong)lo << 32) | hi;
    }


    private void ApplyElseChainPlacementForFrame(int frame)
    {
        if (!enableElseChainPlacement || metaFrameObjects == null)
        {
            return;
        }

        elseChainItems.Clear();
        Transform screen = null;
        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            MetaObj obj = metaFrameObjects[i];
            if (!IsCategoryOther(obj.categoryId))
            {
                continue;
            }

            if (!trackInstances.TryGetValue(obj.trackId, out GameObject instance) ||
                instance == null ||
                !instance.activeInHierarchy)
            {
                continue;
            }

            ReplaceableModel model = instance.GetComponent<ReplaceableModel>();
            if (model == null)
            {
                continue;
            }

            if (screen == null && !ResolveAnchorToScreen(obj.anchorU, out screen, out _, out _))
            {
                continue;
            }

            elseChainItems.Add(new ElseChainItem
            {
                obj = obj,
                instance = instance,
                model = model,
                position = instance.transform.position,
                scaleFactor = 1f,
            });
        }

        if (elseChainItems.Count < 2 || screen == null)
        {
            return;
        }

        if (!TryGetPinholeBasis(screen, out Vector3 camOrigin, out Quaternion camRotation))
        {
            return;
        }

        Vector3 camForward = camRotation * Vector3.forward;
        Vector3 up = screen.up.sqrMagnitude > 0.000001f ? screen.up.normalized : Vector3.up;

        for (int i = 0; i < elseChainItems.Count; i++)
        {
            ElseChainItem item = elseChainItems[i];
            item.depth = Vector3.Dot(item.position - camOrigin, camForward);
            // 水平方向の最長辺（車体なら全長）。scale は一様なので x 成分で代表させる。
            Vector3 size = item.model.baseBoundsSize3;
            float longest = Mathf.Max(size.x, size.z);
            if (longest <= 0f)
            {
                longest = item.model.baseHeightMeters;
            }
            item.length = longest * Mathf.Abs(item.instance.transform.lossyScale.x);
        }

        UpdateElseChainOrderMemory();
        SortElseChainItemsNearestFirst();

        // 手前から順に、既に置いた全部より「奥」で、かつ重ならない所まで奥へ。
        //
        // **順序の条件を別に持つこと。** 「中心間距離 ≥ 接触距離」だけだと、押し出された
        // 2 両目の**手前側**で距離条件を満たした 3 両目がそこに留まる（2026-09-11 初回実装の欠陥。
        // 3 両目が z 1.027 で 2 両目 1.053 の手前に出て、向きも 126° と後ろを向いた）。
        for (int i = 1; i < elseChainItems.Count; i++)
        {
            ElseChainItem item = elseChainItems[i];
            Vector3 v = item.position - camOrigin;
            float kMax = 1f;
            for (int j = 0; j < i; j++)
            {
                ElseChainItem front = elseChainItems[j];
                Vector3 w = front.position - camOrigin;
                float li = item.length;
                float lj = front.length;

                // 順序: 自分の深度（k 倍）が手前の深度以上。
                float kReq = item.depth > 0.0001f ? front.depth / item.depth : 1f;

                // 重なり: |k v − w| ≥ (lj + k li)/2 の**大きい方の根**（球の奥側で接する k）。
                //   k²(|v|² − li²/4) − k(2 v·w + lj li/2) + (|w|² − lj²/4) ≥ 0
                // 視線が球をかすめない（判別式 < 0）なら順序の条件だけで足りる。
                float a = v.sqrMagnitude - li * li * 0.25f;
                float b = 2f * Vector3.Dot(v, w) + lj * li * 0.5f;
                float c = w.sqrMagnitude - lj * lj * 0.25f;
                if (a > 0.000001f)
                {
                    float disc = b * b - 4f * a * c;
                    if (disc >= 0f)
                    {
                        float kPlus = (b + Mathf.Sqrt(disc)) / (2f * a);
                        if (kPlus > kReq)
                        {
                            kReq = kPlus;
                        }
                    }
                }

                if (kReq > kMax)
                {
                    kMax = kReq;
                }
            }

            if (kMax > 1.0001f)
            {
                item.position = camOrigin + v * kMax;
                item.depth *= kMax;
                item.length *= kMax;
                item.scaleFactor = kMax;
            }
        }

        // 向き: 隣（手前側の 1 つ前）との中心を結ぶ水平方向。接触距離の elseChainNeighborFactor 倍
        // より離れていれば別物と見なして触らない。
        Vector3 camForwardH = Vector3.ProjectOnPlane(camForward, up);
        for (int i = 1; i < elseChainItems.Count; i++)
        {
            ElseChainItem item = elseChainItems[i];
            ElseChainItem front = elseChainItems[i - 1];
            float contact = 0.5f * (item.length + front.length);
            Vector3 d = item.position - front.position;
            if (d.magnitude > Mathf.Max(1f, elseChainNeighborFactor) * contact)
            {
                continue;
            }

            Vector3 dh = Vector3.ProjectOnPlane(d, up);
            if (dh.sqrMagnitude < 0.000001f || camForwardH.sqrMagnitude < 0.000001f)
            {
                continue;
            }

            // yaw 0 で長軸は視線方向。SignedAngle だけだと +Z 端が dh（奥の隣）を向く。
            // 06_DieselLocomotive は +Z 端がキャブ（先頭）なので、そのままだと先頭が進行方向の
            // 反対を向く（2026-09-11 実機指摘「反対側が先頭」）。elseChainHeadingOffsetDeg（既定 180）で
            // 先頭側を手前（進行方向）へ向ける。
            float yaw = Mathf.DeltaAngle(0f, Vector3.SignedAngle(camForwardH, dh, up) + elseChainHeadingOffsetDeg);
            elseChainYawByTrack[item.obj.trackId] = yaw;
            item.chained = true;
            if (i == 1)
            {
                elseChainYawByTrack[front.obj.trackId] = yaw;
                front.chained = true;
            }
        }

        Quaternion basis = GetPinholeBasisRotation(screen);
        for (int i = 0; i < elseChainItems.Count; i++)
        {
            ElseChainItem item = elseChainItems[i];
            Transform root = item.instance.transform;
            Quaternion rotation = root.rotation;
            bool hasYaw = elseChainYawByTrack.TryGetValue(item.obj.trackId, out float yawDeg);
            if (hasYaw)
            {
                // 連結中・連結後は手動回転（キー）を使わず、連結で決めた yaw だけを掛ける。
                rotation = Quaternion.AngleAxis(yawDeg, up) * basis * item.model.baseLocalRotation;
            }

            Vector3 scale = root.localScale * item.scaleFactor;
            if (item.scaleFactor != 1f || hasYaw)
            {
                TrackPlacementWriter.Apply(root, new TrackPlacementCommand(item.position, rotation, scale));
            }

            if (logElseChainPlacement)
            {
                Debug.Log(
                    $"[CHAIN] f={frame} order={i} track={item.obj.trackId} bboxH={item.obj.bboxH} " +
                    $"depth={item.depth:F3} k={item.scaleFactor:F3} len={item.length * 1000f:F0}mm " +
                    $"chained={item.chained} yaw={(hasYaw ? yawDeg.ToString("F1") : "manual")}");
            }
        }
    }


    // 配置深度で決まるペアは毎フレーム更新し、決まらないペアは初回だけ bbox 高で決めて記憶する。
    private void UpdateElseChainOrderMemory()
    {
        float tie = Mathf.Max(0f, elseChainDepthTieMeters);
        for (int i = 0; i < elseChainItems.Count; i++)
        {
            for (int j = i + 1; j < elseChainItems.Count; j++)
            {
                ElseChainItem a = elseChainItems[i];
                ElseChainItem b = elseChainItems[j];
                ulong key = ElseChainPairKey(a.obj.trackId, b.obj.trackId);
                bool aIsLower = a.obj.trackId < b.obj.trackId;
                float dz = b.depth - a.depth;   // 正なら a が手前
                if (Mathf.Abs(dz) > tie)
                {
                    bool aFront = dz > 0f;
                    elseChainOrderMemory[key] = (aFront == aIsLower) ? 1 : -1;
                    continue;
                }

                if (elseChainOrderMemory.ContainsKey(key))
                {
                    continue;
                }

                bool aFrontByBBox = a.obj.bboxH >= b.obj.bboxH;
                elseChainOrderMemory[key] = (aFrontByBBox == aIsLower) ? 1 : -1;
            }
        }
    }


    private bool ElseChainIsInFront(ElseChainItem a, ElseChainItem b)
    {
        ulong key = ElseChainPairKey(a.obj.trackId, b.obj.trackId);
        if (!elseChainOrderMemory.TryGetValue(key, out int sign))
        {
            return a.depth <= b.depth;
        }

        bool aIsLower = a.obj.trackId < b.obj.trackId;
        return (sign > 0) == aIsLower;
    }


    // 要素数は多くて 8 程度なので挿入ソート。比較はペア記憶に基づく。
    private void SortElseChainItemsNearestFirst()
    {
        for (int i = 1; i < elseChainItems.Count; i++)
        {
            ElseChainItem item = elseChainItems[i];
            int j = i - 1;
            while (j >= 0 && ElseChainIsInFront(item, elseChainItems[j]))
            {
                elseChainItems[j + 1] = elseChainItems[j];
                j--;
            }

            elseChainItems[j + 1] = item;
        }
    }
}
