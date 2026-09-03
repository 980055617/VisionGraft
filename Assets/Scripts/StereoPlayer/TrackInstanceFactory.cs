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
        AddGrabCollider(instance);
        LogSpringBoneStateIfAny(instance);
        return instance;
    }

    // 掴んで回すためのレイ当たり判定を root に足す。
    //
    // 実測では Human 0/16、Animal 0/52、Else は球 6 個だけしか collider を持っていない
    // （2026-09-02）。掴む操作には対象に当たり判定が要るので、bounds から箱を作る。
    //
    // **スクリーンのピックとは干渉しない。** TryPickScreenByRay は Physics.Raycast ではなく
    // 平面との数学的な交差で解いているので、ここで collider を足しても
    // 「指してオブジェクトを選ぶ」既存の挙動は変わらない。
    //
    // isTrigger にするのは物理に参加させないため。Physics.Raycast は既定
    // （queriesHitTriggers = true）でトリガーにも当たる。
    // 掴み判定の最小の厚み。一番長い辺に対する比。
    private const float GrabColliderMinSideRatio = 0.25f;


    private static void AddGrabCollider(GameObject instance)
    {
        if (instance.GetComponent<BoxCollider>() != null)
        {
            return;
        }

        if (!TryComputeLocalBounds(instance.transform, out Bounds bounds))
        {
            Debug.LogWarning($"[GRABBOX] {instance.name}: bounds が取れず箱を作れません。掴めません");
            return;
        }

        BoxCollider box = instance.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = bounds.center;

        // **細い対象は掴めない。** train の信号柱は bbox 22x132px で、モデルもそれに合わせて
        // 細くなる。レイで当てるのが現実的でないので、掴み判定だけ最小の厚みを持たせる。
        // 見た目には影響しない（isTrigger で描画もされない）。
        float minSide = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * GrabColliderMinSideRatio;
        box.size = new Vector3(
            Mathf.Max(bounds.size.x, minSide),
            Mathf.Max(bounds.size.y, minSide),
            Mathf.Max(bounds.size.z, minSide));

        Debug.Log($"[GRABBOX] {instance.name} center={box.center:F3} size={box.size:F3} (bounds={bounds.size:F3})");
    }


    // root から見たメッシュの合成 bounds。renderer.bounds（world AABB）は使わない。
    // 親のスケールや回転が混ざると桁が狂う（モデルピッカーのプレビューで踏んだのと同じ罠）。
    private static bool TryComputeLocalBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool has = false;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = ResolveMesh(r);
            if (mesh == null)
            {
                continue;
            }

            Matrix4x4 toRoot = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Bounds inRoot = TransformBounds(mesh.bounds, toRoot);
            if (!has)
            {
                bounds = inRoot;
                has = true;
            }
            else
            {
                bounds.Encapsulate(inRoot);
            }
        }

        return has;
    }


    private static Mesh ResolveMesh(Renderer renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        if (renderer is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh;
        }

        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }


    private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
    {
        Vector3 center = m.MultiplyPoint3x4(b.center);
        Vector3 e = b.extents;
        Vector3 x = m.MultiplyVector(new Vector3(e.x, 0f, 0f));
        Vector3 y = m.MultiplyVector(new Vector3(0f, e.y, 0f));
        Vector3 z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
        Vector3 extents = new Vector3(
            Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
            Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
            Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(center, extents * 2f);
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
