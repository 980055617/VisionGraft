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
        return instance;
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
