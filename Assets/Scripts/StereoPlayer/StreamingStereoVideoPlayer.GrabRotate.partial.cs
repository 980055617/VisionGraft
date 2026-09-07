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

    // 掴み判定が届く距離。
    private const float GrabCastDistanceMeters = 20f;

    // 掴み判定の最小の厚み。一番長い辺に対する比。
    // train の信号柱のような薄いモデルも狙えるようにするため。
    // TrackInstanceFactory の同名の定数と同じ値にしておく。
    private const float GrabColliderMinSideRatio = 0.25f;
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

        // bundle ピッカーは入口の画面で、そもそも掴む対象が出ていない。
        if (bundlePickerActive)
        {
            EndGrabRotate("bundle ピッカーが開いた");
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

        // **パネルを指している間だけ掴まない。**
        // 以前はパネルが開いていたら一律で掴めなくしていたが、それだと
        // 編集タブで値やキーを見ながら向きを合わせられない（2026-09-04 の要望）。
        // 掴めない理由は「パネルが開いていること」ではなく「そのトリガーが
        // パネル操作のものだから」なので、パネルを指しているかどうかで分ければよい。
        if ((runtimeSettingsOpen || runtimeModelPickerOpen) &&
            IsPointerOnRuntimePanel(origin, rotation * Vector3.forward))
        {
            EndGrabRotate("パネルを指している");
            SetPointerRayVisible(false);
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
        LogRayInventoryOnce();

        // **自前の線は既定で描かない。**ISDK の白い線と 2 本出て、しかも始点が違うので
        // 紛らわしい（2026-09-07 実機指摘）。判定にはこの origin / direction を
        // 使い続けるので、掴む動作は変わらない。
        //
        // 前提は「白い線と同じ場所を指していること」。**ISDK の線は LineRenderer では
        // 描かれていない**ので（`[RAY] 線を描く候補 0 個`）、線どうしを数値で
        // 比べる手は使えなかった。2026-09-07 実機で「掴める」ことを確認して前提を通した。
        if (!showPointerRay)
        {
            SetPointerRayVisible(false);
            return;
        }

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


    // **実機で「レイが 2 本出ている」と言われたので、何が線を描いているかを列挙する。**
    // 自前の LineRenderer はプロジェクトに 1 つしか無いので、もう 1 本は
    // ISDK 側（`ControllerRayInteractor` / `HandRayInteractor`）のはず。
    // 推測で消すと必要な方を消しかねないので、まず名前を出す。
    private bool loggedRayInventory;

    private void LogRayInventoryOnce()
    {
        if (loggedRayInventory)
        {
            return;
        }

        loggedRayInventory = true;
        LineRenderer[] lines = FindObjectsByType<LineRenderer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[RAY] 線を描く候補 {lines.Length} 個");
        for (int i = 0; i < lines.Length; i++)
        {
            LineRenderer line = lines[i];
            if (line == null)
            {
                continue;
            }

            System.Text.StringBuilder path = new System.Text.StringBuilder(line.name);
            for (Transform t = line.transform.parent; t != null; t = t.parent)
            {
                path.Insert(0, "/").Insert(0, t.name);
            }

            Debug.Log(
                $"[RAY]   {path} enabled={line.enabled} " +
                $"active={line.gameObject.activeInHierarchy} pts={line.positionCount}");
        }
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

        if (!TryResolveTrackFromRay(origin, direction, out uint trackId, out string how))
        {
            Debug.Log(
                $"[GRAB] 対象が見つかりません origin={origin:F3} dir={direction:F3} " +
                $"instances={trackInstances.Count}");
            return;
        }

        Debug.Log($"[GRAB] 対象は track={trackId}（{how}）");

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
        // **合成側と必ず同じ規則を使う。** ManualRotationMath にまとめてある。
        // 別々に書くと、分解と合成が逆でなくなってもコンパイルは通り、
        // 掴んでいる間にずれが積み上がるという形で静かに壊れる。
        Quaternion applied = Quaternion.AngleAxis(angle, axis) *
                             ManualRotationMath.ComposeOffset(
                                 grabRotateStartYaw, grabRotateStartPitch, grabRotateStartRoll);
        ManualRotationMath.DecomposeOffset(applied, out float yaw, out float pitch, out float roll);

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


    // ポインタが開いているパネルの面を指しているか。
    //
    // world space canvas は板なので、面との交点が枠の中かどうかで判定できる。
    // ISDK のレイ判定に相乗りしないのは、あちらが押下のタイミングでしか結果を
    // 返さず、毎フレームの「いま指しているか」には使えないため。
    private bool IsPointerOnRuntimePanel(Vector3 origin, Vector3 direction)
    {
        return IsPointerOnPanelRect(origin, direction, runtimeModelPickerRoot, runtimeModelPickerOpen) ||
               IsPointerOnPanelRect(origin, direction, runtimeSettingsRoot, runtimeSettingsOpen);
    }


    private static bool IsPointerOnPanelRect(Vector3 origin, Vector3 direction, GameObject root, bool open)
    {
        if (!open || root == null)
        {
            return false;
        }

        // パネルを作るときと同じ順で canvas を探す。
        // prefab 経路だと Canvas が子にいることがある。
        Canvas canvas = root.GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = root.GetComponentInChildren<Canvas>(true);
        }

        RectTransform rect = canvas != null
            ? canvas.transform as RectTransform
            : root.GetComponent<RectTransform>();
        if (rect == null)
        {
            return false;
        }

        // Plane.Raycast は裏から当てると false を返すので、自分で解く。
        // パネルの裏側から指していても「パネルを指している」で正しい。
        Vector3 normal = rect.forward;
        float denominator = Vector3.Dot(normal, direction);
        if (Mathf.Abs(denominator) < 0.000001f)
        {
            return false;
        }

        float t = Vector3.Dot(normal, rect.position - origin) / denominator;
        if (t <= 0f)
        {
            return false;
        }

        Vector3 local = rect.InverseTransformPoint(origin + direction * t);
        Rect bounds = rect.rect;

        // 枠のすぐ外を狙ったつもりが中に入っていた、を避けるための余白。
        // canvas 座標なので 40 は板の 4% 程度。
        const float margin = 40f;
        return local.x >= bounds.xMin - margin && local.x <= bounds.xMax + margin &&
               local.y >= bounds.yMin - margin && local.y <= bounds.yMax + margin;
    }


    // レイが指している track を返す。**掴みと選択で同じ規則を使う。**
    // 別々の規則にすると「掴めた対象と選ばれた対象が違う」が起きる。
    //
    // 順に、細いレイ → 太いレイ → 角度の最近傍。手前で緩くしていくので、
    // まっすぐ刺さっていればそれが必ず勝つ。
    private bool TryResolveTrackFromRay(Vector3 origin, Vector3 direction, out uint trackId, out string how)
    {
        trackId = 0u;
        how = "なし";

        // **Unity の物理には頼らない。自分で交差を解く。**
        //
        // 実機で collider の world bounds を見たら、実体が 0.95m 先にいるのに
        // 中心 (0,0,0)・大きさ (1,1,1) という「設定前の既定値」のままだった
        // （2026-09-04 実測）。このプロジェクトは m_AutoSyncTransforms: 0 で、
        // モデルは毎フレーム script で置き直しているため、物理側の表現が実体に
        // 追いついていない。Physics.SyncTransforms() を挟んでも変わらなかった。
        //
        // レイと箱の交差は Bounds.IntersectRay で解ける。transform から直接
        // 引くので、物理の同期状態に一切左右されない。対象は数個しかないので
        // 全部見ても安い。
        Vector3 dir = direction.normalized;
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            GameObject instance = kv.Value;
            if (instance == null || !instance.activeInHierarchy)
            {
                continue;
            }

            if (!TryRayHitTrackInstance(instance, origin, dir, out float distance))
            {
                continue;
            }

            // **手前を採る。** ここは実際の交差なので、奥行きで正しく決まる。
            // 近い 2 体でどちらを指しているかが、これで初めて正しく解ける。
            if (distance < bestDistance)
            {
                bestDistance = distance;
                trackId = kv.Key;
                found = true;
            }
        }

        if (found)
        {
            how = $"交差 {bestDistance:F2}m";
            return true;
        }

        // 外れたら、向いている方向に最も近い対象。狙いが少しずれても掴めるようにする保険。
        // **奥行きを見ないので、これに頼り切ってはいけない。**
        if (TryFindNearestTrackByAngle(origin, direction, out trackId, out float angleDeg))
        {
            how = $"角度 {angleDeg:F1}度";
            return true;
        }

        return false;
    }


    // レイが対象の箱に刺さるか。**physics を通さない。**
    // 箱は描画の bounds をそのモデルのローカル空間で取ったもの。
    private static bool TryRayHitTrackInstance(
        GameObject instance, Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0f;
        if (!TryCalculateLocalRendererBounds(instance.transform, out Bounds local))
        {
            return false;
        }

        // **細い対象は狙えない。** train の信号柱のように薄いモデルがあるので、
        // 掴み判定だけ最小の厚みを持たせる（collider に入れていたのと同じ考え方）。
        Vector3 size = local.size;
        float minSide = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * GrabColliderMinSideRatio;
        local.size = new Vector3(
            Mathf.Max(size.x, minSide),
            Mathf.Max(size.y, minSide),
            Mathf.Max(size.z, minSide));

        Matrix4x4 worldToLocal = instance.transform.worldToLocalMatrix;
        Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(origin);
        Vector3 localDirection = worldToLocal.MultiplyVector(direction);

        // direction は正規化済みなので、返る t はそのまま world のメートル。
        // 変換は線形なので、局所空間で解いた t が world でもそのまま使える。
        if (!local.IntersectRay(new Ray(localOrigin, localDirection), out float enter))
        {
            return false;
        }

        distance = Mathf.Max(0f, enter);
        return distance <= GrabCastDistanceMeters;
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


}
