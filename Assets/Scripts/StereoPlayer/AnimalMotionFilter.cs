using System.Collections.Generic;
using UnityEngine;

public sealed class AnimalMotionFilter
{
    private readonly AnimalFilterConfig config;
    private readonly Dictionary<Transform, Vector3> rootYawForward = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, int> rootYawLastSeenFrame = new Dictionary<Transform, int>();
    private readonly Dictionary<Transform, RuntimeOneEuroVector3Filter> rootForwardFilters = new Dictionary<Transform, RuntimeOneEuroVector3Filter>();
    private readonly Dictionary<Transform, RuntimeOneEuroVector3Filter> rootUpFilters = new Dictionary<Transform, RuntimeOneEuroVector3Filter>();
    private readonly Dictionary<Transform, RuntimeOneEuroVector3Filter> rootPositionFilters = new Dictionary<Transform, RuntimeOneEuroVector3Filter>();
    private readonly Dictionary<Transform, RuntimeOneEuroVector3Filter> limbTargetFilters = new Dictionary<Transform, RuntimeOneEuroVector3Filter>();

    public AnimalMotionFilter(AnimalFilterConfig config)
    {
        this.config = config;
    }

    // shot 境界で呼ぶ。フィルタ本体を破棄することで、次フレームの観測値が初期値として
    // そのまま採用される（各 Filter* は未登録キーに対し Reset(target) して target を返す）。
    // shot をまたいだ位置・向きは前 shot からの連続運動ではないため、平滑化で追従させると
    // 新しいカットの先頭で数フレームかけて滑り込む挙動になる。
    public void Reset()
    {
        rootYawForward.Clear();
        rootYawLastSeenFrame.Clear();
        rootForwardFilters.Clear();
        rootUpFilters.Clear();
        rootPositionFilters.Clear();
        limbTargetFilters.Clear();
    }

    public void MarkRootSeen(Transform root, int frame)
    {
        rootYawLastSeenFrame[root] = frame;
    }

    public bool WasSeenRecently(Transform root, int currentFrame)
    {
        return rootYawLastSeenFrame.TryGetValue(root, out int lastFrame) &&
               RuntimeClock.WasSeenRecently(currentFrame, lastFrame, 1);
    }

    public bool TryGetPrevRootForward(Transform root, out Vector3 prevForward)
    {
        return rootYawForward.TryGetValue(root, out prevForward) && prevForward.sqrMagnitude > 0.000001f;
    }

    public void SetRootForward(Transform root, Vector3 forward)
    {
        rootYawForward[root] = forward;
    }

    public void ResetRootBasis(Transform root, Vector3 planarForward, Vector3 bodyUp, Vector3 worldUp)
    {
        rootYawForward[root] = planarForward;
        GetOrCreateRootFilter(rootForwardFilters, root).Reset(planarForward);

        Vector3 resetUp = Vector3.ProjectOnPlane(bodyUp, planarForward);
        if (resetUp.sqrMagnitude <= 0.000001f)
        {
            resetUp = Vector3.ProjectOnPlane(worldUp, planarForward);
        }
        if (resetUp.sqrMagnitude > 0.000001f)
        {
            resetUp.Normalize();
            GetOrCreateRootFilter(rootUpFilters, root).Reset(resetUp);
        }
    }

    public Vector3 FilterRootForward(Transform root, Vector3 forward, float deltaTime)
    {
        return GetOrCreateRootFilter(rootForwardFilters, root).Filter(forward, deltaTime);
    }

    public Vector3 FilterRootUp(Transform root, Vector3 up, float deltaTime)
    {
        return GetOrCreateRootFilter(rootUpFilters, root).Filter(up, deltaTime);
    }

    public Vector3 FilterRootPosition(Transform root, Vector3 target, float deltaTime)
    {
        if (!rootPositionFilters.TryGetValue(root, out RuntimeOneEuroVector3Filter filter) || filter == null)
        {
            filter = new RuntimeOneEuroVector3Filter(1.6f, 0.08f, 1.0f);
            rootPositionFilters[root] = filter;
            filter.Reset(target);
            return target;
        }
        return filter.Filter(target, deltaTime);
    }

    public Vector3 FilterLimbTarget(Transform key, Vector3 target, float minCutoffHz, float beta, float deltaTime)
    {
        if (!limbTargetFilters.TryGetValue(key, out RuntimeOneEuroVector3Filter filter) || filter == null)
        {
            filter = new RuntimeOneEuroVector3Filter(minCutoffHz, beta, 1.0f);
            limbTargetFilters[key] = filter;
            filter.Reset(target);
            return target;
        }
        return filter.Filter(target, deltaTime);
    }

    private RuntimeOneEuroVector3Filter GetOrCreateRootFilter(Dictionary<Transform, RuntimeOneEuroVector3Filter> filters, Transform key)
    {
        if (filters.TryGetValue(key, out RuntimeOneEuroVector3Filter existing) && existing != null)
        {
            return existing;
        }
        var created = new RuntimeOneEuroVector3Filter(config.minCutoffHz, config.beta, config.derivativeCutoffHz);
        filters[key] = created;
        return created;
    }
}
