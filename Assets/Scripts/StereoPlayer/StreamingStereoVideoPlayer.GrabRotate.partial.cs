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

    // 掴んだまま手首を回しても対象が飛ばないよう、1 フレームで許す角度に上限を置く。
    private const float GrabRotateMaxDegPerFrame = 45f;


    private void UpdateGrabRotate()
    {
        if (!enableGrabRotate || isNormalMode)
        {
            EndGrabRotate();
            return;
        }

        // パネルを開いている間は掴まない。パネル操作のトリガーで対象が回ってしまう。
        if (runtimeSettingsOpen || runtimeModelPickerOpen || bundlePickerActive)
        {
            EndGrabRotate();
            prevGrabTriggerPressed = false;
            return;
        }

        bool hasPointer = RuntimeXrRayPickReader.TryReadPointerPose(
            xrInputDevices, out Vector3 pointerLocal, out Quaternion pointerLocalRotation, out bool triggerPressed);

        if (!hasPointer)
        {
            EndGrabRotate();
            prevGrabTriggerPressed = false;
            return;
        }

        if (!TryResolvePointerWorldPose(pointerLocal, pointerLocalRotation, out Vector3 origin, out Quaternion rotation))
        {
            EndGrabRotate();
            prevGrabTriggerPressed = triggerPressed;
            return;
        }

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
        if (!Physics.Raycast(new Ray(origin, rotation * Vector3.forward), out RaycastHit hit, 20f))
        {
            return;
        }

        if (!TryResolveTrackIdFromCollider(hit.collider, out uint trackId))
        {
            return;
        }

        PauseForManualRotationEdit();

        grabRotateActive = true;
        grabRotateTrackId = trackId;
        grabRotateStartPointerRotation = rotation;
        GetManualRotationForTrack(trackId, out grabRotateStartYaw, out grabRotateStartPitch, out grabRotateStartRoll);

        // 掴んだ対象を、回転・モデル変更の対象にも合わせる。別々だと混乱する。
        selectedManualRotationTrackId = (int)trackId;
        runtimeModelPickerTrackId = (int)trackId;

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


    private void EndGrabRotate()
    {
        if (!grabRotateActive)
        {
            return;
        }

        grabRotateActive = false;
        PersistManualYaw(grabRotateTrackId);
        UpdateRuntimeTrackRotationUiState();

        GetManualRotationForTrack(grabRotateTrackId, out float yaw, out float pitch, out float roll);
        Debug.Log($"[GRAB] 離した track={grabRotateTrackId} yaw={yaw:F1} pitch={pitch:F1} roll={roll:F1}");
        ExperimentLog.Operation(
            "change_rotation",
            $"track={grabRotateTrackId} op=grab yaw={ExperimentCsv.Format(yaw)} " +
            $"pitch={ExperimentCsv.Format(pitch)} roll={ExperimentCsv.Format(roll)} " +
            $"frame={GetCurrentPlaybackFrame()}");
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
