using UnityEngine;

public static class TrackInstanceFactory
{
    public static GameObject Create(GameObject prefab, uint trackId)
    {
        if (prefab == null)
        {
            return null;
        }

        // **prefab の root 回転を潰さないこと。** ここで Quaternion.identity を渡すと、
        // 直後に走る ReplaceableModel.Awake が「補正前の姿勢」で world AABB を測ってしまう。
        // 06_DieselLocomotive は root に X 軸 -90 度が入っており、これを潰すと
        // baseHeightMeters が屋根高 5.26m ではなく車体長 18.51m になる。
        // 縦向きになるだけでなく、その車体長を bbox 高さに合わせるせいで大きさも狂う。
        // 位置は配置側が毎フレーム上書きするので原点のままでよい。
        GameObject instance = Object.Instantiate(prefab, Vector3.zero, prefab.transform.localRotation);
        instance.name = $"Track_{trackId}";
        if (instance.GetComponent<ReplaceableModel>() == null)
        {
            instance.AddComponent<ReplaceableModel>();
        }

        EnableSkinnedBoundsPoseTracking(instance);
        ForceHighestLod(instance);
        LogSpringBoneStateIfAny(instance);
        return instance;
    }

    // VRM の揺れもの（髪・スカート）が本当に動く状態で生成されたかを 1 行残す。
    //
    // 06_Female_C（VRoid 由来）は VRM10SpringBoneJoint を 131 個持っている（実測）。
    // 「入っていない」のか「入っているのに動いていない」のかで対処がまったく変わるので、
    // 生成時の状態を記録する。型名で拾うのはパッケージのバージョンで名前空間が変わるため。
    private static void LogSpringBoneStateIfAny(GameObject instance)
    {
        int joints = 0;
        MonoBehaviour instanceComponent = null;
        foreach (MonoBehaviour c in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null)
            {
                continue;
            }

            string name = c.GetType().Name;
            if (name == "VRM10SpringBoneJoint")
            {
                joints++;
            }
            else if (name == "Vrm10Instance")
            {
                instanceComponent = c;
            }
        }

        if (joints == 0 && instanceComponent == null)
        {
            return;
        }

        // UpdateType が None だと Vrm10Instance が有効でも揺れものは 1 度も計算されない。
        // 「enabled なのに動かない」の最有力候補なので、そこまで見る。
        string updateType = "?";
        if (instanceComponent != null)
        {
            System.Type t = instanceComponent.GetType();
            System.Reflection.PropertyInfo prop = t.GetProperty("UpdateType");
            if (prop != null)
            {
                updateType = prop.GetValue(instanceComponent)?.ToString() ?? "null";
            }
            else
            {
                System.Reflection.FieldInfo field = t.GetField(
                    "m_updateType",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                updateType = field != null ? (field.GetValue(instanceComponent)?.ToString() ?? "null") : "プロパティ無し";
            }
        }

        Debug.Log(
            $"[SPRING] {instance.name} joints={joints} " +
            $"vrm10Instance={(instanceComponent != null ? (instanceComponent.enabled ? "enabled" : "disabled") : "なし")} " +
            $"updateType={updateType} lossyScale={instance.transform.lossyScale:F4}");
    }


    // LOD を最上位に固定する。
    //
    // Human の prefab 17 個中 12 個が 5 段の LODGroup を持つ（実測 2026-09-02）。
    // LOD は画面占有率で段を選ぶが、**このプロジェクトのモデルは bbox 合わせで
    // 0.26 倍などに縮む**ので、実質いつも最下位が選ばれて「遠いとポリゴンが減る」ように見える。
    //
    // 同時に置くオブジェクトは多くて 5 体程度なので、常に最上位で問題ない
    // （2026-09-02 ユーザー確認: 全部ポリゴン最大にしたい）。
    // Animal / Else に LODGroup は無いので、この処理は Human にだけ効く。
    private static void ForceHighestLod(GameObject instance)
    {
        LODGroup[] groups = instance.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < groups.Length; i++)
        {
            if (groups[i] != null)
            {
                groups[i].ForceLOD(0);
            }
        }
    }


    // SkinnedMeshRenderer.bounds は updateWhenOffscreen=false（prefab の既定）だと固定の m_AABB を
    // root bone の transform で変換した値になり、スキニング変形を反映しない。
    // FitDisplayedModelToBBox は renderer.bounds の下端を bbox 下端へ合わせ直す補正なので、
    // これが rest pose 相当のままだと補正量がほぼ 0 になり、rest pose から大きく外れたポーズ
    // （座位など）でモデルが浮いたままになる。bounds をポーズに追従させて補正を機能させる。
    // baseBoundsSize / baseBottomOffsetLocal は ReplaceableModel.Awake で既に確定しているため、
    // スケール基準は従来どおり変わらない。
    private static void EnableSkinnedBoundsPoseTracking(GameObject instance)
    {
        SkinnedMeshRenderer[] renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                renderers[i].updateWhenOffscreen = true;
            }
        }
    }
}
