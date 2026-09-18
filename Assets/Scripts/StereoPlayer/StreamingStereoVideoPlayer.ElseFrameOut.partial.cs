using System.Collections.Generic;
using UnityEngine;

// Else の frame out 継続（2026-09-11）。
//
// 列車が画面の右端から出ていくとき、bundle の bbox は「可視部分だけの外接矩形」、anchor は
// その中心なので、端に近づくほど anchor は減速して 1280 を超えず、track が画面外に出た
// frame で meta から消える（実測: bbox 幅 498 → 94 px、anchor u 1038 → 1239、
// 最後の 25 frame は生成側の hold で anchor が止まる）。そのまま置くとモデルはその場に
// 留まり、最後の数 frame の bbox 高の縮み（15〜25%）で小さくなり、突然消える。
//
// データは画面の外を一切表さないので、ここで外挿する。
//   1. 端に掛かっている間: 画面内側に残っている車両の端（右へ出るなら bbox 左端 = 後端）に
//      モデルの同じ側の端（投影）を合わせ、scale は見切れる直前の値で固定する
//      （Human/Animal の「shot 先頭でロック + keypoint から全高を推定」に相当。
//      Else は keypoint が無いので直前の scale と、最後まで実データである内側の端を使う）。
//   2. track が消えた後: 見えていた最後の frame の位置から、画面内側の端の速度（px/frame を
//      最後の深度で world に直したもの）で画面の横方向へ進め続け、モデル全長の
//      elseFrameOutCoastLengths 倍だけ進んだら非表示にする。慣性中も連結配置の制約に残す
//      （消えた瞬間に後続が手前へ跳ぶのを防ぐ）。
//   端に掛かっている間と慣性中は、連結配置に向き（yaw）を更新させない（elseFrameOutFrozenYawTracks）。
//
// 全 track の配置後、連結配置（ElseChain）の**前**に走る。連結は端合わせ後の位置を見る必要がある
// （可視部分の中心のままだと、出ていく車両の後続が k=2.3 まで押し出され、消えた瞬間に戻って跳ぶ）。
// 端合わせの投影はモデルの向きに依存するので、連結が前 frame までに決めた yaw を先に当てる。
// docs/bundle-placement.md「列車が右へ frame out するとき、モデルがその場に留まって縮む」。
//
// 2026-09-17: 下端（上端）からの frame out にも同じことをする（enableElseVerticalFrameOutContinuation）。
// car clip（D-014）は 5 台とも画面下端から出る。bbox 下端は 720 行に張り付いたまま高さだけが
// 105 → 7 px と縮み、生成側は anchor を hold する（sidecar `continuing_frameout_tail`）。
// Else の scale は毎 frame bboxH 追従なので、何もしないとモデルが下端に向かって縮んで消える。
// 縦は「画面内側に残っている端 = bbox 上端」にモデルの投影上端を合わせ、慣性は画面の縦方向へ進める。
// 左右の見切れが同時にあるときは従来どおり左右を優先する（左右の経路は変えていない）。
public partial class StreamingStereoVideoPlayer
{
    private sealed class ElseFrameOutState
    {
        public bool hasUnclipped;
        public float unclippedBBoxW;
        public Vector3 unclippedLocalScale;
        public bool hasHeldScale;
        public Vector3 heldLocalScale;     // 見切れている間の scale（縮ませない）
        public int lastSeenFrame = -1;
        public Vector3 lastPosition;
        public Quaternion lastRotation;
        public Vector3 lastLocalScale;
        public float lastLength;
        public int clipSide;               // +1 右端、−1 左端、0 なし（最後に見えた frame）
        public int clipVert;               // +1 下端、−1 上端、0 なし（最後に見えた frame。左右が 0 のときだけ使う）
        public bool hasVelocity;
        public Vector3 velocityPerFrame;   // world m / video frame
        public MetaObj lastObj;            // 連結が慣性中の車両を制約として使うときの素性（track / bbox 高）
        public int innerEdgeFrame = -1;    // 画面内側の端（右へ出るなら bbox 左端、下へ出るなら bbox 上端）を最後に測った frame
        public float innerEdgePx;
        public float innerEdgeRatePx;      // その端の速度（px / video frame、EMA）
        public bool hasInnerEdgeRate;
        public bool innerEdgeVertical;     // innerEdge* が縦（v）方向の値か。軸が変わったら測り直す
        public bool coasting;
        public int coastStartFrame;
        public Vector3 coastStartPosition;
        public float coastMaxDistance;
        // 端に掛かる前（見切れの無い frame）に測った動き。位置は world m / video frame、scale は localScale.x の変化 / frame。
        // 端に掛かっている間と慣性で「下に落ちながら近づいて大きくなる」を続けるための外挿の元（2026-09-18）。
        public bool hasMotion;
        public int motionFrame = -1;       // 最後に測った frame（見切れ無し）
        public Vector3 motionPosition;
        public float motionScale;
        public Vector3 velocity3D;
        public float scaleRatePerFrame;
        public float coastScaleRate;       // 慣性中の scale の増分 / frame
    }

    private const float ElseFrameOutVelocitySmoothing = 0.3f;
    private const int ElseFrameOutMaxGapFrames = 3;
    private const int ElseFrameOutMaxCoastFrames = 180;

    private readonly Dictionary<uint, ElseFrameOutState> elseFrameOutByTrack = new Dictionary<uint, ElseFrameOutState>();
    private readonly HashSet<uint> elseFrameOutSeenThisFrame = new HashSet<uint>();
    private readonly List<uint> elseFrameOutToRemove = new List<uint>();
    // 連結配置が向き（yaw）を更新してはいけない track（端に掛かっている・慣性中）。
    // 通過中に向きが視聴者側へ暴走するのを、出ていく車両だけは見切れる前の向きで止める。
    private readonly HashSet<uint> elseFrameOutFrozenYawTracks = new HashSet<uint>();


    private void ResetElseFrameOutStateForShotBoundary()
    {
        elseFrameOutByTrack.Clear();
    }


    private void ApplyElseFrameOutForFrame(int frame)
    {
        elseFrameOutFrozenYawTracks.Clear();
        if (!enableElseFrameOutContinuation || metaFrameObjects == null ||
            manifest == null || manifest.eye_w <= 0)
        {
            return;
        }

        elseFrameOutSeenThisFrame.Clear();
        Transform screen = null;
        Vector3 camOrigin = Vector3.zero;
        Quaternion camRotation = Quaternion.identity;
        float fx = 0f;
        float fy = 0f;
        bool hasBasis = false;

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

            if (!hasBasis)
            {
                if (!TryResolveElseFrameOutBasis(obj.anchorU, out screen, out camOrigin, out camRotation, out fx, out fy))
                {
                    return;
                }

                hasBasis = true;
            }

            elseFrameOutSeenThisFrame.Add(obj.trackId);
            if (!elseFrameOutByTrack.TryGetValue(obj.trackId, out ElseFrameOutState st))
            {
                st = new ElseFrameOutState();
                elseFrameOutByTrack[obj.trackId] = st;
            }

            // 巻き戻し・ループ・シークで frame が戻ったら覚え直す。
            if (st.lastSeenFrame >= 0 && frame < st.lastSeenFrame)
            {
                st.hasUnclipped = false;
                st.hasVelocity = false;
                st.hasInnerEdgeRate = false;
                st.innerEdgeFrame = -1;
                st.coasting = false;
                st.lastSeenFrame = -1;
                st.hasMotion = false;
                st.motionFrame = -1;
            }

            Transform root = instance.transform;
            Vector3 up = screen.up.sqrMagnitude > 0.000001f ? screen.up.normalized : Vector3.up;
            bool clipL = obj.bboxX <= 0;
            bool clipR = obj.bboxX + obj.bboxW >= manifest.eye_w - 1;
            int clipSide = clipR && !clipL ? 1 : (clipL && !clipR ? -1 : 0);
            // 縦の見切れ。左右の見切れがあるときは左右を優先する（clipVert は使わない）。
            // スイッチ OFF のときは 0 のままなので、下端に掛かった frame は従来どおり
            // 「見切れなし」として扱われる（= 縮んだ scale を基準として覚え直してしまう）。
            bool clipT = enableElseVerticalFrameOutContinuation && manifest.eye_h > 0 && obj.bboxY <= 0;
            bool clipB = enableElseVerticalFrameOutContinuation && manifest.eye_h > 0 &&
                obj.bboxY + obj.bboxH >= manifest.eye_h - 1;
            int clipVert = clipSide != 0 ? 0 : (clipB && !clipT ? 1 : (clipT && !clipB ? -1 : 0));

            if (clipSide == 0 && clipVert == 0)
            {
                if (!clipL && !clipR && !clipT && !clipB && obj.bboxW > 0)
                {
                    st.hasUnclipped = true;
                    st.unclippedBBoxW = obj.bboxW;
                    st.unclippedLocalScale = root.localScale;
                    st.hasHeldScale = false;
                    st.hasInnerEdgeRate = false;
                    st.innerEdgeFrame = -1;

                    // 端に掛かる前の 3D の動き（データどおりに置かれた位置と scale の 1 frame あたりの変化、EMA）。
                    // 連続した frame でだけ測る（バッチの同 frame 反復・シークでは更新しない）。
                    if (st.motionFrame >= 0 && frame == st.motionFrame + 1)
                    {
                        Vector3 dp = root.position - st.motionPosition;
                        float ds = root.localScale.x - st.motionScale;
                        st.velocity3D = st.hasMotion ? Vector3.Lerp(st.velocity3D, dp, ElseFrameOutVelocitySmoothing) : dp;
                        st.scaleRatePerFrame = st.hasMotion ? Mathf.Lerp(st.scaleRatePerFrame, ds, ElseFrameOutVelocitySmoothing) : ds;
                        st.hasMotion = true;
                    }
                    st.motionFrame = frame;
                    st.motionPosition = root.position;
                    st.motionScale = root.localScale.x;
                }
            }
            else if (clipVert != 0 && st.hasUnclipped)
            {
                // 縦の見切れ（下端から出るのが car の 5 台）。左右と同じ 3 手:
                // scale の単調保持 → 画面内側の端（下へ出るなら bbox 上端）の速度 → その端にモデルの投影上端を合わせる。
                Vector3 live = root.localScale;
                Vector3 held = st.hasHeldScale && st.heldLocalScale.x > live.x ? st.heldLocalScale : live;
                st.heldLocalScale = held;
                st.hasHeldScale = true;
                float offsetLocal = model.baseBottomOffsetLocal;
                Vector3 bottom = root.position - up * (offsetLocal * root.lossyScale.y);
                TrackPlacementWriter.ApplyLocalScale(root, held);
                TrackPlacementWriter.ApplyPosition(root, bottom + up * (offsetLocal * root.lossyScale.y));

                float innerEdge = clipVert > 0 ? obj.bboxY : (obj.bboxY + obj.bboxH);
                if (!st.innerEdgeVertical)
                {
                    // 左右の端を測っていた値は使えない。
                    st.innerEdgeVertical = true;
                    st.hasInnerEdgeRate = false;
                    st.innerEdgeFrame = -1;
                }
                if (st.innerEdgeFrame >= 0 && frame > st.innerEdgeFrame && frame - st.innerEdgeFrame <= ElseFrameOutMaxGapFrames)
                {
                    float r = (innerEdge - st.innerEdgePx) / (frame - st.innerEdgeFrame);
                    st.innerEdgeRatePx = st.hasInnerEdgeRate ? Mathf.Lerp(st.innerEdgeRatePx, r, ElseFrameOutVelocitySmoothing) : r;
                    st.hasInnerEdgeRate = true;
                }
                if (frame != st.innerEdgeFrame)
                {
                    st.innerEdgeFrame = frame;
                    st.innerEdgePx = innerEdge;
                }

                elseFrameOutFrozenYawTracks.Add(obj.trackId);

                if (elseChainYawByTrack.TryGetValue(obj.trackId, out float chainYaw))
                {
                    TrackPlacementWriter.ApplyRotation(
                        root, Quaternion.AngleAxis(chainYaw, up) * GetPinholeBasisRotation(screen) * model.baseLocalRotation);
                }

                // 端に掛かっている間、bundle 側は anchor（奥行き）を止める（sidecar の hold）が、bbox の幅は伸び続ける =
                // 車はまだ近づいている。端に掛かる前の動きで奥行きと scale を外挿し、近づく方向・大きくなる方向だけ通す。
                // 奥行きは視線（camOrigin → モデル）に沿って動かし、投影の u / v を変えない。縦は次の上端合わせが決める。
                if (elseFrameOutFollowMotion && st.hasMotion && st.motionFrame >= 0 && frame > st.motionFrame)
                {
                    int k = frame - st.motionFrame;
                    Vector3 camForwardK = camRotation * Vector3.forward;
                    float zNow = Vector3.Dot(root.position - camOrigin, camForwardK);
                    float zExtrap = Vector3.Dot(st.motionPosition + st.velocity3D * k - camOrigin, camForwardK);
                    if (zExtrap > 0.05f && zExtrap < zNow)
                    {
                        TrackPlacementWriter.ApplyPosition(root, camOrigin + (root.position - camOrigin) * (zExtrap / zNow));
                    }
                    float sExtrap = st.motionScale + st.scaleRatePerFrame * k;
                    if (sExtrap > root.localScale.x)
                    {
                        TrackPlacementWriter.ApplyLocalScale(root, Vector3.one * sExtrap);
                        st.heldLocalScale = root.localScale;
                    }
                }

                // 画面内側に残っている端にモデルの同じ側の端を合わせる。eye の v は下向き正なので、
                // dv > 0（モデルの端が bbox の端より上にある）なら画面の下方向へ動かす。
                // px → world は投影の逆（PinholePlacementSpace.ReconstructCamLocalFromEyePixel の y）。
                if (TryProjectRendererBoundsToEye(instance, camOrigin, camRotation, fx, fy, out _, out _, out float minV, out float maxV))
                {
                    float dv = clipVert > 0 ? (obj.bboxY - minV) : ((obj.bboxY + obj.bboxH) - maxV);
                    Vector3 camForward = camRotation * Vector3.forward;
                    float z = Vector3.Dot(root.position - camOrigin, camForward);
                    float dy = (dv / manifest.eye_h) * 2f * z / fy;
                    TrackPlacementWriter.ApplyPosition(root, root.position - (camRotation * Vector3.up) * dy);
                }
            }
            else if (clipSide != 0 && st.hasUnclipped)
            {
                // 見切れている間は scale を縮ませない（単調保持）。見切れ始めの値で固定すると、
                // その後 80 frame 近づいて太るぶん（track 1: 0.025 → 0.037）が出ない。bbox 高は
                // 最後の数 frame 以外は信頼できるので、伸びる方向だけ通す。
                // モデルの下端は動かさない（root は下端から baseBottomOffsetLocal × scale だけ上にある）。
                Vector3 live = root.localScale;
                Vector3 held = st.hasHeldScale && st.heldLocalScale.x > live.x ? st.heldLocalScale : live;
                st.heldLocalScale = held;
                st.hasHeldScale = true;
                float offsetLocal = model.baseBottomOffsetLocal;
                Vector3 bottom = root.position - up * (offsetLocal * root.lossyScale.y);
                TrackPlacementWriter.ApplyLocalScale(root, held);
                TrackPlacementWriter.ApplyPosition(root, bottom + up * (offsetLocal * root.lossyScale.y));

                // 画面内側の端の速度（px / frame）。慣性の速さはこれから作る（world 速度は向きの揺れで
                // 符号すら安定しなかった: f935 で alongRight = −6 mm/f）。
                float innerEdge = clipSide > 0 ? obj.bboxX : (obj.bboxX + obj.bboxW);
                if (st.innerEdgeVertical)
                {
                    // 縦の端を測っていた値は使えない。
                    st.innerEdgeVertical = false;
                    st.hasInnerEdgeRate = false;
                    st.innerEdgeFrame = -1;
                }
                if (st.innerEdgeFrame >= 0 && frame > st.innerEdgeFrame && frame - st.innerEdgeFrame <= ElseFrameOutMaxGapFrames)
                {
                    float r = (innerEdge - st.innerEdgePx) / (frame - st.innerEdgeFrame);
                    st.innerEdgeRatePx = st.hasInnerEdgeRate ? Mathf.Lerp(st.innerEdgeRatePx, r, ElseFrameOutVelocitySmoothing) : r;
                    st.hasInnerEdgeRate = true;
                }
                if (frame != st.innerEdgeFrame)
                {
                    st.innerEdgeFrame = frame;
                    st.innerEdgePx = innerEdge;
                }

                elseFrameOutFrozenYawTracks.Add(obj.trackId);

                // 投影はモデルの向きに依存する。連結配置はこの後に走るので、前 frame までに
                // 連結が決めた yaw があればここで先に当てておく（連結は同じ式で上書きするだけ）。
                if (elseChainYawByTrack.TryGetValue(obj.trackId, out float chainYaw))
                {
                    TrackPlacementWriter.ApplyRotation(
                        root, Quaternion.AngleAxis(chainYaw, up) * GetPinholeBasisRotation(screen) * model.baseLocalRotation);
                }

                // 画面内側に残っている車両の端（右へ出るなら bbox 左端 = 後端）にモデルの
                // 同じ側の端を合わせる。「見切れる直前の幅の半分」で中心を推定する案は、
                // 通過中に車両が近づいて太るぶんを表せなかった（track 2: 記憶 532 px に対し
                // 可視幅 896 px で補正量 0）。内側の端は最後の frame まで実データ。
                if (TryProjectRendererBoundsToEye(instance, camOrigin, camRotation, fx, fy, out float minU, out float maxU, out _, out _))
                {
                    float du = clipSide > 0 ? (obj.bboxX - minU) : ((obj.bboxX + obj.bboxW) - maxU);
                    Vector3 camForward = camRotation * Vector3.forward;
                    float z = Vector3.Dot(root.position - camOrigin, camForward);
                    float dx = (du / manifest.eye_w) * 2f * z / fx;
                    TrackPlacementWriter.ApplyPosition(root, root.position + (camRotation * Vector3.right) * dx);
                }
            }

            // 速度（補正後の位置で測る）。
            if (st.lastSeenFrame >= 0 && frame > st.lastSeenFrame && frame - st.lastSeenFrame <= ElseFrameOutMaxGapFrames)
            {
                Vector3 v = (root.position - st.lastPosition) / (frame - st.lastSeenFrame);
                st.velocityPerFrame = st.hasVelocity
                    ? Vector3.Lerp(st.velocityPerFrame, v, ElseFrameOutVelocitySmoothing)
                    : v;
                st.hasVelocity = true;
            }

            if (frame != st.lastSeenFrame)
            {
                st.lastSeenFrame = frame;
                st.lastPosition = root.position;
                st.lastRotation = root.rotation;
                st.lastLocalScale = root.localScale;
                st.lastObj = obj;
                Vector3 size = model.baseBoundsSize3;
                st.lastLength = Mathf.Max(size.x, size.z) * Mathf.Abs(root.lossyScale.x);
                st.clipSide = clipSide;
                st.clipVert = clipVert;
                st.coasting = false;
            }

            if (logElseChainPlacement && (clipSide != 0 || clipVert != 0))
            {
                Debug.Log(
                    $"[FRAMEOUT] f={frame} track={obj.trackId} side={clipSide} vside={clipVert} bboxL={obj.bboxX} bboxW={obj.bboxW} " +
                    $"bboxT={obj.bboxY} bboxH={obj.bboxH} u={obj.anchorU} v={obj.anchorV} " +
                    $"edgeRate={st.innerEdgeRatePx:F1}px/f scale={root.localScale.x:F4}");
            }
        }

        // 2. 消えた track を進ませる。
        elseFrameOutToRemove.Clear();
        foreach (KeyValuePair<uint, ElseFrameOutState> kv in elseFrameOutByTrack)
        {
            uint trackId = kv.Key;
            ElseFrameOutState st = kv.Value;
            if (elseFrameOutSeenThisFrame.Contains(trackId) || st.lastSeenFrame < 0)
            {
                continue;
            }

            if (frame < st.lastSeenFrame)
            {
                elseFrameOutToRemove.Add(trackId);
                continue;
            }

            if (!trackInstances.TryGetValue(trackId, out GameObject instance) || instance == null)
            {
                elseFrameOutToRemove.Add(trackId);
                continue;
            }

            if (!st.coasting)
            {
                // 端に掛かったまま消えた track だけ。内側の端がその端へ動いていなければ別の消え方
                // （遮蔽・検出落ち）なので従来どおり非表示のまま。
                // 左右に掛かっていればその向き、そうでなく縦に掛かっていれば縦の向きで判定する
                // （測っていた端の軸と一致しているときだけ）。
                //
                // 2026-09-18: この frame に Else が 1 体も見えていないと基準（板・焦点距離）が未解決で
                // 慣性が始まらなかった（car の track 3 / 4 は消えた瞬間に他の車がいないので置き去りになった）。
                // 最後に見えたときの anchor から同じ手順で基準を取る。
                if (!hasBasis)
                {
                    hasBasis = TryResolveElseFrameOutBasis(st.lastObj.anchorU, out screen, out camOrigin, out camRotation, out fx, out fy);
                }

                bool horizontal = st.clipSide != 0;
                int exitSign = horizontal ? st.clipSide : st.clipVert;
                bool leftByEdge = exitSign != 0 && st.hasInnerEdgeRate && st.innerEdgeVertical == !horizontal &&
                    frame - st.lastSeenFrame <= ElseFrameOutMaxGapFrames &&
                    hasBasis && st.innerEdgeRatePx * exitSign > 0.5f;
                if (!leftByEdge)
                {
                    if (logElseChainPlacement)
                    {
                        Debug.Log(
                            $"[FRAMEOUT] f={frame} track={trackId} no-coast side={st.clipSide} vside={st.clipVert} hasRate={st.hasInnerEdgeRate} " +
                            $"gap={frame - st.lastSeenFrame} basis={hasBasis} edgeRate={st.innerEdgeRatePx:F1}px/f");
                    }

                    elseFrameOutToRemove.Add(trackId);
                    continue;
                }

                st.coasting = true;
                st.coastStartFrame = st.lastSeenFrame;
                st.coastStartPosition = st.lastPosition;
                // 慣性の速さ: 内側の端の px 速度を、最後の深度で world に直し、画面の横（縦）方向へ。
                // 縦は eye の v が下向き正なので、正の速度は画面の下方向。
                Vector3 camForward = camRotation * Vector3.forward;
                float zLast = Mathf.Max(0.05f, Vector3.Dot(st.lastPosition - camOrigin, camForward));
                st.velocityPerFrame = horizontal
                    ? (camRotation * Vector3.right) * ((st.innerEdgeRatePx / manifest.eye_w) * 2f * zLast / fx)
                    : -(camRotation * Vector3.up) * ((st.innerEdgeRatePx / manifest.eye_h) * 2f * zLast / fy);
                st.coastScaleRate = 0f;
                if (elseFrameOutFollowMotion && !horizontal && st.hasMotion)
                {
                    // 縦の見切れ: 端に掛かる前の 3D の動き（横・奥行き）を続ける。縦成分は端で測った速度に置き換えて、
                    // 端に掛かっている間の動きと連続にする。scale も同じ変化率で大きくなり続ける（近づく分）。
                    Vector3 camUp = camRotation * Vector3.up;
                    Vector3 lateralAndDepth = st.velocity3D - camUp * Vector3.Dot(st.velocity3D, camUp);
                    st.velocityPerFrame += lateralAndDepth;
                    st.coastScaleRate = Mathf.Max(0f, st.scaleRatePerFrame);
                }
                st.coastMaxDistance = Mathf.Max(0.01f, st.lastLength) * Mathf.Max(0.1f, elseFrameOutCoastLengths);
            }

            int elapsed = frame - st.coastStartFrame;
            Vector3 delta = st.velocityPerFrame * elapsed;
            if (elapsed > ElseFrameOutMaxCoastFrames || delta.magnitude > st.coastMaxDistance)
            {
                SceneObjectWriter.ApplyActive(instance, false);
                elseFrameOutToRemove.Add(trackId);
                continue;
            }

            elseFrameOutFrozenYawTracks.Add(trackId);
            SceneObjectWriter.ApplyActive(instance, true);
            Vector3 coastScale = st.lastLocalScale + Vector3.one * (st.coastScaleRate * elapsed);
            TrackPlacementWriter.Apply(
                instance.transform,
                new TrackPlacementCommand(st.coastStartPosition + delta, st.lastRotation, coastScale));

            // 2026-09-18 ユーザー要望: 「全長 × 1.2 進んだら」ではなく、**視界から映らなくなったら**消す。
            // 置いた後の箱が見ているカメラの視錐台から完全に外れたら（または顔の前 5 cm より近づいたら）非表示。
            // 距離（elseFrameOutCoastLengths、既定 6 倍）と frame 数（180）は安全弁として残す。
            // 頭を動かして追いかければその分だけ長く残る（視界に入っている限り消さない）。
            if (elapsed >= 2 && !IsCoastingInstanceInView(instance, camOrigin, camRotation))
            {
                if (logElseChainPlacement)
                {
                    Debug.Log($"[FRAMEOUT] f={frame} track={trackId} coast end: out of view after {elapsed} frames dist={delta.magnitude * 1000f:F0}mm");
                }
                SceneObjectWriter.ApplyActive(instance, false);
                elseFrameOutToRemove.Add(trackId);
                continue;
            }

            if (logElseChainPlacement)
            {
                Debug.Log(
                    $"[FRAMEOUT] f={frame} track={trackId} coasting elapsed={elapsed} " +
                    $"dist={delta.magnitude * 1000f:F0}mm/{st.coastMaxDistance * 1000f:F0}mm " +
                    $"v=({st.velocityPerFrame.x * 1000f:F1},{st.velocityPerFrame.y * 1000f:F1},{st.velocityPerFrame.z * 1000f:F1})mm/f scale={coastScale.x:F4}");
            }
        }

        for (int i = 0; i < elseFrameOutToRemove.Count; i++)
        {
            elseFrameOutByTrack.Remove(elseFrameOutToRemove[i]);
        }
    }


    // Renderer の world AABB の 8 頂点を eye 画素へ投影し、U と V の範囲を返す（[HPOS] と同じ計算）。
    // frame out の基準: anchor の u が属する板と、その pinhole 基底・焦点距離。
    private bool TryResolveElseFrameOutBasis(
        ushort anchorU, out Transform screen, out Vector3 camOrigin, out Quaternion camRotation, out float fx, out float fy)
    {
        camOrigin = Vector3.zero;
        camRotation = Quaternion.identity;
        fx = 0f;
        fy = 0f;
        return ResolveAnchorToScreen(anchorU, out screen, out _, out _) &&
            TryGetPinholeBasis(screen, out camOrigin, out camRotation) &&
            TryGetFocalLengths(out fx, out fy) && fx > 0f && fy > 0f;
    }

    private bool TryProjectRendererBoundsToEye(
        GameObject instance, Vector3 camOrigin, Quaternion camRotation, float fx, float fy,
        out float minU, out float maxU, out float minV, out float maxV)
    {
        minU = float.MaxValue;
        maxU = float.MinValue;
        minV = float.MaxValue;
        maxV = float.MinValue;
        if (fx <= 0f || fy <= 0f || instance == null)
        {
            return false;
        }

        // 2026-09-18: 以前は world AABB（軸に沿った箱）の 8 角を投影していた。手動 yaw で車を −18° 回すと
        // 箱が車より前後 0.4 m ずつ太り、その幻の角を bbox 上端に合わせて車体が 250 px 下に押し出された。
        // 板の向きが世界の軸からずれている実機では yaw 無しでも同じ。renderer ごとのローカル箱（mesh の bounds）を
        // その renderer の行列で world に出す（= 回転込みの箱）ので、向きを変えても箱は太らない。
        Quaternion worldToCam = Quaternion.Inverse(camRotation);
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null || !renderer.enabled || !TryGetRendererLocalBounds(renderer, out Bounds local))
            {
                continue;
            }

            Matrix4x4 toWorld = renderer.localToWorldMatrix;
            Vector3 c = local.center;
            Vector3 e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = toWorld.MultiplyPoint3x4(c + new Vector3(
                    ((i & 1) == 0 ? -e.x : e.x),
                    ((i & 2) == 0 ? -e.y : e.y),
                    ((i & 4) == 0 ? -e.z : e.z)));
                Vector3 cam = worldToCam * (corner - camOrigin);
                if (!PinholePlacementSpace.TryProjectCamLocalToEyePixel(manifest, cam, fx, fy, out Vector2 px))
                {
                    continue;
                }

                if (px.x < minU) { minU = px.x; }
                if (px.x > maxU) { maxU = px.x; }
                if (px.y < minV) { minV = px.y; }
                if (px.y > maxV) { maxV = px.y; }
            }
        }

        return minU <= maxU && minV <= maxV;
    }

    // 慣性中のモデルがまだ見えているか。見ているカメラ（実機は HMD の中央目、バッチは撮影カメラ）の視錐台と
    // renderer の world AABB で判定する（AABB は OBB より大きいので「まだ見えている」側に倒れる）。
    // カメラが取れないときは pinhole の基準（板の向き）で代用し、板の下端より 1 画面ぶん下に出たら見えないとみなす。
    private bool IsCoastingInstanceInView(GameObject instance, Vector3 camOrigin, Quaternion camRotation)
    {
        if (!TryGetRendererWorldBounds(instance, out Bounds bounds))
        {
            return false;
        }

        Camera cam = GetViewCamera();
        if (cam != null)
        {
            Vector3 toCenter = bounds.center - cam.transform.position;
            if (Vector3.Dot(toCenter, cam.transform.forward) < 0.05f)
            {
                return false; // 顔より手前・後ろ
            }
            // world AABB だと慣性で大きくなった車の箱が視野の端に引っ掛かり続けるので、renderer ごとの
            // ローカル箱（回転込み）の 8 角と中心のどれかが視錐台の中にあるかで見る。
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            if (IsPointInsidePlanes(planes, bounds.center))
            {
                return true;
            }
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null || !renderer.enabled || !TryGetRendererLocalBounds(renderer, out Bounds local))
                {
                    continue;
                }
                Matrix4x4 toWorld = renderer.localToWorldMatrix;
                Vector3 c = local.center;
                Vector3 e = local.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = toWorld.MultiplyPoint3x4(c + new Vector3(
                        ((i & 1) == 0 ? -e.x : e.x),
                        ((i & 2) == 0 ? -e.y : e.y),
                        ((i & 4) == 0 ? -e.z : e.z)));
                    if (IsPointInsidePlanes(planes, corner))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        Vector3 camForward = camRotation * Vector3.forward;
        Vector3 camUp = camRotation * Vector3.up;
        Vector3 rel = bounds.center - camOrigin;
        float z = Vector3.Dot(rel, camForward);
        if (z < 0.05f)
        {
            return false;
        }
        float y = Vector3.Dot(rel, camUp) + bounds.extents.magnitude;
        return y > -z; // 45° より下（おおよそ HMD の下側の視野の端）に出るまでは見えている扱い
    }

    private static bool IsPointInsidePlanes(Plane[] planes, Vector3 point)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            if (planes[i].GetDistanceToPoint(point) < 0f)
            {
                return false;
            }
        }
        return true;
    }

    // renderer のローカル空間の箱。MeshRenderer は mesh の bounds、SkinnedMeshRenderer は localBounds。
    private static bool TryGetRendererLocalBounds(Renderer renderer, out Bounds local)
    {
        local = default(Bounds);
        if (renderer is SkinnedMeshRenderer smr)
        {
            local = smr.localBounds;
            return true;
        }
        MeshFilter mf = renderer.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            local = mf.sharedMesh.bounds;
            return true;
        }
        return false;
    }
}
