using System.Collections.Generic;
using UnityEngine;

// F2（2026-10-16）: 画面の面の座標（原点 = 画面の中心、x 右・y 上、m）での帯と穴の計算。場面なしで EditMode のテストができるよう static。
internal static class StereoFrameOccluderGeometry
{
    public static void BuildBands(float w, float h, float b, List<Rect> output)
    {
        output.Clear();
        float hw = w * 0.5f;
        float hh = h * 0.5f;
        output.Add(Rect.MinMaxRect(-hw - b, hh, hw + b, hh + b));     // 上
        output.Add(Rect.MinMaxRect(-hw - b, -hh - b, hw + b, -hh));   // 下
        output.Add(Rect.MinMaxRect(-hw - b, -hh, -hw, hh));           // 左
        output.Add(Rect.MinMaxRect(hw, -hh, hw + b, hh));             // 右
    }

    // r から hole を引いた残り（重なりが無ければ r のまま）。上・下の帯と、重なる高さの左・右。
    public static void Subtract(Rect r, Rect hole, List<Rect> output)
    {
        if (!r.Overlaps(hole))
        {
            output.Add(r);
            return;
        }

        float yMin = Mathf.Max(r.yMin, hole.yMin);
        float yMax = Mathf.Min(r.yMax, hole.yMax);
        if (hole.yMax < r.yMax) output.Add(Rect.MinMaxRect(r.xMin, hole.yMax, r.xMax, r.yMax));
        if (hole.yMin > r.yMin) output.Add(Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, hole.yMin));
        if (hole.xMin > r.xMin) output.Add(Rect.MinMaxRect(r.xMin, yMin, hole.xMin, yMax));
        if (hole.xMax < r.xMax) output.Add(Rect.MinMaxRect(hole.xMax, yMin, r.xMax, yMax));
    }

    // 画面の縁から maxGap 未満の穴は縁までつなげる（コントローラのレイが画面と穴のあいだで面に切られないように。R2-8）。
    public static Rect ExtendToScreenEdge(Rect hole, float w, float h, float maxGap)
    {
        float hw = w * 0.5f;
        float hh = h * 0.5f;
        bool overlapsX = hole.xMax > -hw && hole.xMin < hw;
        bool overlapsY = hole.yMax > -hh && hole.yMin < hh;
        float xMin = hole.xMin, xMax = hole.xMax, yMin = hole.yMin, yMax = hole.yMax;
        if (overlapsX && yMin >= hh && yMin - hh < maxGap) yMin = hh;
        if (overlapsX && yMax <= -hh && -hh - yMax < maxGap) yMax = -hh;
        if (overlapsY && xMax <= -hw && -hw - xMax < maxGap) xMax = -hw;
        if (overlapsY && xMin >= hw && xMin - hw < maxGap) xMin = hw;
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    // 画面の中に収まる（板そのものの ISDK の canvas など）。板の ∓1 mm のずれを許す。
    public static bool IsInsideScreen(Rect r, float w, float h)
    {
        const float tol = 0.0025f;
        float hw = w * 0.5f;
        float hh = h * 0.5f;
        return r.xMin >= -hw - tol && r.xMax <= hw + tol && r.yMin >= -hh - tol && r.yMax <= hh + tol;
    }

    public static void BuildMesh(List<Rect> rects, Vector3 center, Vector3 right, Vector3 up, Matrix4x4 worldToLocal,
        List<Vector3> vertices, List<int> triangles)
    {
        vertices.Clear();
        triangles.Clear();
        for (int i = 0; i < rects.Count; i++)
        {
            Rect r = rects[i];
            if (r.width <= 1e-5f || r.height <= 1e-5f) continue;
            int b = vertices.Count;
            vertices.Add(worldToLocal.MultiplyPoint3x4(center + right * r.xMin + up * r.yMin));
            vertices.Add(worldToLocal.MultiplyPoint3x4(center + right * r.xMin + up * r.yMax));
            vertices.Add(worldToLocal.MultiplyPoint3x4(center + right * r.xMax + up * r.yMax));
            vertices.Add(worldToLocal.MultiplyPoint3x4(center + right * r.xMax + up * r.yMin));
            triangles.Add(b); triangles.Add(b + 1); triangles.Add(b + 2);
            triangles.Add(b); triangles.Add(b + 2); triangles.Add(b + 3);
        }
    }
}
