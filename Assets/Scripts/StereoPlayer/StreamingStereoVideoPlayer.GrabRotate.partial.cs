using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // 対象を掴んで、手首をひねった通りに回す。
    //
    // 三方向の動きに yaw / pitch / roll を割り当てる案もあったが、
    // 「前後に動かすと roll」は現実の動作と対応しない。コントローラの**姿勢の差分**を
    // そのまま渡せば、1 つの動作で 3 軸すべてが自然に決まる（2026-09-02 ユーザー合意）。
    //
    // 掴む対象は TrackInstanceFactory.AddGrabCollider が付けた箱。
    // スクリーンのピック（TryPickScreenByRay）は平面との数学的な交差で解いているので、
    // ここで Physics.Raycast を使っても互いに干渉しない。

    private bool grabRotateActive;
    private uint grabRotateTrackId;
    private Quaternion grabRotateStartPointerRotation;
    private float grabRotateStartYaw;
    private float grabRotateStartPitch;
    private float grabRotateStartRoll;
    private bool prevGrabTriggerPressed;
    private float grabRotateLoggedAt;
    private float grabRotateStartedAt;
    private string grabRotateEndReason = "";
    private GameObject pointerRayRoot;
    private LineRenderer pointerRayLine;

    // 掴み判定の太さと距離。モデルは world では 0.1〜0.3m しかないので、
    // 細いレイだと狙いを外す。少し太くして当てやすくする。
    private const float GrabCastRadiusMeters = 0.05f;
    private const float GrabCastDistanceMeters = 20f;
    // 当たり判定を外したときに「これを狙っていた」と見なす角度。
    private const float GrabAngleToleranceDeg = 25f;
    private const float PointerRayLengthMeters = 2.5f;


    private void UpdateGrabRotate()
    {
        if (!enableGrabRotate || isNormalMode)
        {
            EndGrabRotate("機能が無効 / normal mode");
            SetPointerRayVisible(false);
            return;
        }

        // **掴む対象が無いときは線を出さない。**
        // bundle を選んだあと再生が始まるまでの間は、bundlePickerActive が false で
        // パネルも開いていないので、ここを抜けて線だけが出ていた。
        // その段階では見るカメラもリグもまだ落ち着いておらず、
        // 線が古い姿勢のまま空中に残る（2026-09-04 実機報告）。
        if (!metaLoaded || trackInstances.Count == 0)
        {
            EndGrabRotate("掴む対象がまだ無い");
            SetPointerRayVisible(false);
            prevGrabTriggerPressed = false;
            return;
        }

        // パネルを開いている間は掴まない。パネル操作のトリガーで対象が回ってしまう。
        // 線もそちらの邪魔になるので消す（パネルには ISDK 側のレイが出る）。
        if (runtimeSettingsOpen || runtimeModelPickerOpen || bundlePickerActive)
        {
            EndGrabRotate("パネルが開いた");
            SetPointerRayVisible(false);
            prevGrabTriggerPressed = false;
            return;
        }

        bool hasPointer = RuntimeXrRayPickReader.TryReadPointerPose(
            xrInputDevices, out Vector3 pointerLocal, out Quaternion pointerLocalRotation, out bool triggerPressed);

        // **1 フレームの読み取り失敗で離さない。** コントローラの列挙は時々空を返す。
        // 掴んでいる最中に落とすと、実機では「掴んだ瞬間に離れる」ように見える。
        if (!hasPointer)
        {
            if (!grabRotateActive)
            {
                SetPointerRayVisible(false);
                prevGrabTriggerPressed = false;
            }

            return;
        }

        if (!TryResolvePointerWorldPose(pointerLocal, pointerLocalRotation, out Vector3 origin, out Quaternion rotation))
        {
            // 同上。頭の姿勢が 1 フレーム取れなかっただけで掴みを解かない。
            if (!grabRotateActive)
            {
                SetPointerRayVisible(false);
            }

            prevGrabTriggerPressed = triggerPressed;
            return;
        }

        // どこを指しているかを見せる。これが無いと 0.2m のモデルは狙えない。
        UpdatePointerRay(origin, rotation * Vector3.forward, triggerPressed);

        bool pressedThisFrame = triggerPressed && !prevGrabTriggerPressed;
        prevGrabTriggerPressed = triggerPressed;

        if (!triggerPressed)
        {
            EndGrabRotate();
            return;
        }

        if (pressedThisFrame && !grabRotateActive)
        {
            TryBeginGrabRotate(origin, rotation);
            return;
        }

        if (grabRotateActive)
        {
            ApplyGrabRotate(rotation);
        }
    }


    private void UpdatePointerRay(Vector3 origin, Vector3 direction, bool pressed)
    {
        if (pointerRayRoot == null)
        {
            RuntimePointerRayFactory.Ray created = RuntimePointerRayFactory.Create();
            pointerRayRoot = created.root;
            pointerRayLine = created.line;
        }

        SetPointerRayVisible(true);
        if (pointerRayLine == null)
        {
            return;
        }

        // 掴んでいる間は短く濃くして、掴んでいることが分かるようにする。
        float length = grabRotateActive ? 0.35f : PointerRayLengthMeters;
        pointerRayLine.SetPosition(0, origin);
        pointerRayLine.SetPosition(1, origin + direction.normalized * length);
        pointerRayLine.widthMultiplier = pressed ? 0.007f : 0.004f;
    }


    private void SetPointerRayVisible(bool visible)
    {
        if (pointerRayRoot == null)
        {
            return;
        }

        SceneObjectWriter.ApplyActive(pointerRayRoot, visible);
    }


    // コントローラの姿勢はトラッキング空間なので、頭の姿勢差からリグの回転を復元して world に直す。
    private bool TryResolvePointerWorldPose(
        Vector3 pointerLocal, Quaternion pointerLocalRotation, out Vector3 position, out Quaternion rotation)
    {
        position = pointerLocal;
        rotation = pointerLocalRotation;

        Transform head = GetViewOrHeadTransform();
        if (head == null)
        {
            return false;
        }

        if (!RuntimeXrRayPickReader.TryReadHeadPose(xrInputDevices, out Vector3 headLocal, out Quaternion headLocalRot))
        {
            return false;
        }

        Quaternion rigRotation = head.rotation * Quaternion.Inverse(headLocalRot);
        position = head.position + rigRotation * (pointerLocal - headLocal);
        rotation = rigRotation * pointerLocalRotation;
        return true;
    }


    private void TryBeginGrabRotate(Vector3 origin, Quaternion rotation)
    {
        Vector3 direction = rotation * Vector3.forward;

        // **細い線ではなく太さを持たせて当てる。**
        // モデルは bbox 合わせで小さく、world では 0.1〜0.3m しかない。
        // 細いレイでは狙いを外しやすく、実機で一度も当たらなかった（2026-09-03）。
        //
        // **最初に当たったものだけを見てはいけない。** 動画スクリーンにも当たり判定があり、
        // パネルを閉じている間（＝掴みたいとき）は有効になっている。
        // 手前をかすめてスクリーンに吸われるので、当たった全部から track を探す。
        RaycastHit[] hits = Physics.SphereCastAll(origin, GrabCastRadiusMeters, direction, GrabCastDistanceMeters);
        uint trackId = 0u;
        bool found = false;

        if (hits != null && hits.Length > 0)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length && !found; i++)
            {
                found = TryResolveTrackIdFromCollider(hits[i].collider, out trackId);
            }
        }

        // **当たり判定に頼り切らない。**
        // ポインタの線が見えない状態で 0.2m のモデルを狙うのは無理で、実機では
        // 26 度もずれていた（2026-09-03 実測: dir=(-0.436, 0.183, 0.881)）。
        // 外れたら「向いている方向に一番近い対象」を拾う。多少ずれても掴める。
        if (!found)
        {
            found = TryFindNearestTrackByAngle(origin, direction, out trackId, out float angleDeg);
            if (found)
            {
                Debug.Log($"[GRAB] 当たり判定は外れたので角度で拾いました track={trackId} 角度={angleDeg:F1}度");
            }
            else
            {
                Debug.Log(
                    $"[GRAB] 対象が見つかりません origin={origin:F3} dir={direction:F3} " +
                    $"instances={trackInstances.Count} 最小角={angleDeg:F1}度");
                return;
            }
        }

        PauseForManualRotationEdit();

        grabRotateActive = true;
        grabRotateStartedAt = Time.unscaledTime;
        grabRotateTrackId = trackId;
        grabRotateStartPointerRotation = rotation;
        GetManualRotationForTrack(trackId, out grabRotateStartYaw, out grabRotateStartPitch, out grabRotateStartRoll);

        // 掴んだ対象を、回転・モデル変更の対象にも合わせる。別々だと混乱する。
        selectedManualRotationTrackId = (int)trackId;
        runtimeModelPickerTrackId = (int)trackId;
        runtimeModelPickerPreferredTrackId = (int)trackId;

        Debug.Log($"[GRAB] 掴んだ track={trackId} yaw={grabRotateStartYaw:F1} pitch={grabRotateStartPitch:F1} roll={grabRotateStartRoll:F1}");
    }


    private void ApplyGrabRotate(Quaternion rotation)
    {
        // 掴んだ瞬間からの姿勢差。これをそのまま対象の回転に足す。
        Quaternion delta = rotation * Quaternion.Inverse(grabRotateStartPointerRotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 0.000001f)
        {
            return;
        }

        if (angle > 180f)
        {
            angle -= 360f;
        }

        // 掴んだ状態の回転を、yaw / pitch / roll の 3 つに落として保存する。
        // キーフレームは軸ごとの float で持っているので、ここで euler に直す。
        Quaternion applied = Quaternion.AngleAxis(angle, axis) *
                             Quaternion.Euler(grabRotateStartPitch, grabRotateStartYaw, grabRotateStartRoll);
        Vector3 euler = applied.eulerAngles;

        float yaw = Mathf.DeltaAngle(0f, euler.y);
        float pitch = Mathf.DeltaAngle(0f, euler.x);
        float roll = Mathf.DeltaAngle(0f, euler.z);

        SetManualRotationForTrack(grabRotateTrackId, yaw, pitch, roll);

        if (Time.unscaledTime - grabRotateLoggedAt >= 0.5f)
        {
            grabRotateLoggedAt = Time.unscaledTime;
            Debug.Log($"[GRAB] track={grabRotateTrackId} yaw={yaw:F1} pitch={pitch:F1} roll={roll:F1}");
        }
    }


    private void EndGrabRotate(string reason = "トリガーを離した")
    {
        if (!grabRotateActive)
        {
            return;
        }

        grabRotateActive = false;
        grabRotateEndReason = reason;
        PersistManualYaw(grabRotateTrackId);
        UpdateRuntimeTrackRotationUiState();

        GetManualRotationForTrack(grabRotateTrackId, out float yaw, out float pitch, out float roll);
        Debug.Log(
            $"[GRAB] 離した track={grabRotateTrackId} yaw={yaw:F1} pitch={pitch:F1} roll={roll:F1} " +
            $"理由={grabRotateEndReason} 掴んでいた時間={(Time.unscaledTime - grabRotateStartedAt):F2}秒");
        ExperimentLog.Operation(
            "change_rotation",
            $"track={grabRotateTrackId} op=grab yaw={ExperimentCsv.Format(yaw)} " +
            $"pitch={ExperimentCsv.Format(pitch)} roll={ExperimentCsv.Format(roll)} " +
            $"frame={GetCurrentPlaybackFrame()}");
    }


    // 向いている方向に最も近い track を返す。しきい値を超えていたら false。
    // angleDeg には（見つからなくても）最小角を入れるので、ログで「どれくらい外していたか」が分かる。
    private bool TryFindNearestTrackByAngle(Vector3 origin, Vector3 direction, out uint trackId, out float angleDeg)
    {
        trackId = 0u;
        angleDeg = 999f;
        if (direction.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        Vector3 dir = direction.normalized;
        bool found = false;
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            GameObject instance = kv.Value;
            if (instance == null || !instance.activeInHierarchy)
            {
                continue;
            }

            Vector3 toTarget = ResolveGrabTargetCenter(instance) - origin;
            if (toTarget.sqrMagnitude < 0.000001f)
            {
                continue;
            }

            float angle = Vector3.Angle(dir, toTarget.normalized);
            if (angle < angleDeg)
            {
                angleDeg = angle;
                trackId = kv.Key;
                found = true;
            }
        }

        return found && angleDeg <= GrabAngleToleranceDeg;
    }


    // 対象の中心。ルート原点は足元などにあることが多いので、描画の中心を使う。
    private static Vector3 ResolveGrabTargetCenter(GameObject instance)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        bool has = false;
        Bounds b = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
            {
                continue;
            }

            if (!has)
            {
                b = renderers[i].bounds;
                has = true;
            }
            else
            {
                b.Encapsulate(renderers[i].bounds);
            }
        }

        return has ? b.center : instance.transform.position;
    }


    private bool TryResolveTrackIdFromCollider(Collider collider, out uint trackId)
    {
        trackId = 0u;
        if (collider == null)
        {
            return false;
        }

        Transform root = collider.transform;
        while (root != null)
        {
            foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
            {
                if (kv.Value != null && kv.Value.transform == root)
                {
                    trackId = kv.Key;
                    return true;
                }
            }

            root = root.parent;
        }

        return false;
    }
}
