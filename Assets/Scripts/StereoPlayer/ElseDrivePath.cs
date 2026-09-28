using System.Collections.Generic;
using UnityEngine;

// Else（車などの剛体）のインタラクティブモーション「走って近づく → U ターン → 走って戻る」の経路。
// 純粋な幾何だけ（Unity のシーンに触らない）なので EditMode テストで検証できる。
//
// 車はその場で回転できないので、向きの変化はすべて「走りながら曲がる」で作る:
//   - 出発時の向き h0 と、視聴者へ向かう方向 dIn の差は 3 次エルミート曲線（両端の接線を指定）で吸収する
//   - U ターンは半径 R の半円（常に進行方向の右へ曲がる）
//   - 到着時は必ず位置 O・向き h0 に戻る。追従へのハンドオフで向きが跳ばないようにするため
//
// 経路は「主線」（O を通り dIn 方向）と「戻り線」（主線を右に 2R ずらした線）の 2 本の上を走る:
//   出発: h0 が dIn 寄りなら主線へ滑らかに入る。h0 が反対向きなら、いったん遠ざかってから U ターンして主線へ入る
//   往路: 主線を S（視聴者の手前の停止点）まで
//   遠端: S で右へ U ターン → 戻り線（−dIn 向き）
//   復路: 戻り線を O の近くまで
//   到着: h0 が −dIn 寄りなら戻り線から O へ滑らかに入る。h0 が dIn 寄りなら O の手前で U ターンして主線から O へ入る
// どちらの場合も U ターンは 2 回で、その場回転は無い。
public static class ElseDrivePath
{
    public const int SegmentDriveIn = 0;
    public const int SegmentUTurn = 1;
    public const int SegmentDriveBack = 2;

    public struct Sample
    {
        public Vector3 position;
        public Vector3 tangent;
        public int segment;
    }

    public sealed class Path
    {
        public readonly List<Vector3> points = new List<Vector3>(256);
        public readonly List<Vector3> tangents = new List<Vector3>(256);
        public readonly List<int> segments = new List<int>(256);
        public readonly List<float> cumulative = new List<float>(256);
        public readonly float[] segmentLengths = new float[3];
        public float TotalLength => cumulative.Count > 0 ? cumulative[cumulative.Count - 1] : 0f;

        public Sample Evaluate(float distance)
        {
            Sample s = default;
            if (points.Count == 0)
            {
                return s;
            }
            if (points.Count == 1 || distance <= 0f)
            {
                s.position = points[0];
                s.tangent = tangents[0];
                s.segment = segments[0];
                return s;
            }
            if (distance >= TotalLength)
            {
                int last = points.Count - 1;
                s.position = points[last];
                s.tangent = tangents[last];
                s.segment = segments[last];
                return s;
            }

            int lo = 0;
            int hi = cumulative.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] <= distance)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            float span = cumulative[hi] - cumulative[lo];
            float t = span > 0.000001f ? (distance - cumulative[lo]) / span : 0f;
            s.position = Vector3.LerpUnclamped(points[lo], points[hi], t);
            Vector3 tangent = Vector3.Slerp(tangents[lo], tangents[hi], t);
            s.tangent = tangent.sqrMagnitude > 0.000001f ? tangent.normalized : tangents[lo];
            s.segment = segments[lo];
            return s;
        }

        internal void Add(Vector3 position, Vector3 tangent, int segment)
        {
            Vector3 safeTangent = tangent.sqrMagnitude > 0.000001f ? tangent.normalized : (tangents.Count > 0 ? tangents[tangents.Count - 1] : Vector3.forward);
            float length = points.Count > 0 ? Vector3.Distance(points[points.Count - 1], position) : 0f;
            if (points.Count > 0 && length <= 0.0000001f)
            {
                // 同じ点は積まない（区間の継ぎ目で端点が重なる）。接線だけ最新にする。
                tangents[tangents.Count - 1] = safeTangent;
                segments[segments.Count - 1] = segment;
                return;
            }
            points.Add(position);
            tangents.Add(safeTangent);
            segments.Add(segment);
            cumulative.Add(points.Count == 1 ? 0f : cumulative[cumulative.Count - 1] + length);
            if (points.Count > 1)
            {
                segmentLengths[Mathf.Clamp(segment, 0, 2)] += length;
            }
        }
    }

    // 停止点までの距離がこれより短いと経路が組めない（主線に入る助走 2R のぶん直線が残らない）。
    public static float MinimumDriveDistance(float turnRadius)
    {
        return Mathf.Max(0.05f, turnRadius * 2f + 0.02f);
    }

    // origin: 出発位置 O、heading: 出発時の向き h0（進行面内、正規化不要）、stopPoint: 視聴者の手前の停止点 S、
    // up: 進行面の法線、turnRadius: U ターンの半径 R。
    public static Path Build(Vector3 origin, Vector3 heading, Vector3 stopPoint, Vector3 up, float turnRadius, int curveSamples = 24)
    {
        Vector3 safeUp = up.sqrMagnitude > 0.000001f ? up.normalized : Vector3.up;
        float radius = Mathf.Max(0.005f, turnRadius);
        Vector3 toStop = Vector3.ProjectOnPlane(stopPoint - origin, safeUp);
        Vector3 dIn = toStop.sqrMagnitude > 0.000001f ? toStop.normalized : Vector3.ProjectOnPlane(heading, safeUp).normalized;
        if (dIn.sqrMagnitude <= 0.000001f)
        {
            dIn = Vector3.ProjectOnPlane(Vector3.forward, safeUp).normalized;
        }
        Vector3 h0 = Vector3.ProjectOnPlane(heading, safeUp);
        h0 = h0.sqrMagnitude > 0.000001f ? h0.normalized : dIn;
        Vector3 side = Vector3.Cross(safeUp, dIn).normalized;       // 進行方向（+dIn）の右
        Vector3 stop = origin + dIn * toStop.magnitude;               // 高さは origin に揃える
        float lead = Mathf.Max(radius * 2f, 0.05f);                   // 主線に入る／主線から出るための助走
        bool loopOut = Vector3.Dot(h0, dIn) < 0f;                     // 出発時に反対を向いているなら遠ざかってから U ターン
        bool loopIn = !loopOut;                                       // 到着時の向きが −dIn と反対なら O の手前で U ターン

        Path path = new Path();
        int n = Mathf.Max(8, curveSamples);

        // --- 出発（drive_in） ---
        Vector3 lineEntry;
        if (loopOut)
        {
            Vector3 g = origin - dIn * lead + side * (2f * radius);
            AddHermite(path, origin, h0, g, -dIn, n, SegmentDriveIn);
            AddSemicircle(path, g, -dIn, -side, radius, n, SegmentDriveIn);   // 右折: −dIn 向きの右は −side
            lineEntry = origin - dIn * lead;
        }
        else
        {
            lineEntry = origin + dIn * lead;
            AddHermite(path, origin, h0, lineEntry, dIn, n, SegmentDriveIn);
        }
        path.Add(stop, dIn, SegmentDriveIn);

        // --- 遠端の U ターン（u_turn）: S から右へ半円、戻り線の S' へ ---
        AddSemicircle(path, stop, dIn, side, radius, n, SegmentUTurn);
        Vector3 stopPrime = stop + side * (2f * radius);

        // --- 復路（drive_back） ---
        if (loopIn)
        {
            Vector3 f = origin + side * (2f * radius) - dIn * lead;
            path.Add(f, -dIn, SegmentDriveBack);
            AddSemicircle(path, f, -dIn, -side, radius, n, SegmentDriveBack);
            AddHermite(path, origin - dIn * lead, dIn, origin, h0, n, SegmentDriveBack);
        }
        else
        {
            Vector3 e = origin + side * (2f * radius) + dIn * lead;
            path.Add(e, -dIn, SegmentDriveBack);
            AddHermite(path, e, -dIn, origin, h0, n, SegmentDriveBack);
        }
        // 数値誤差を吸って、終点を厳密に origin / h0 にする。
        path.Add(origin, h0, SegmentDriveBack);
        return path;
    }

    // 3 次エルミート曲線。接線の大きさは弦の長さ（曲率が自然になる）。
    private static void AddHermite(Path path, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, int samples, int segment)
    {
        float m = Mathf.Max(0.001f, Vector3.Distance(p0, p1));
        Vector3 m0 = t0.normalized * m;
        Vector3 m1 = t1.normalized * m;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float t2 = t * t;
            float t3 = t2 * t;
            Vector3 p = (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
            Vector3 d = (6f * t2 - 6f * t) * p0 + (3f * t2 - 4f * t + 1f) * m0 + (-6f * t2 + 6f * t) * p1 + (3f * t2 - 2f * t) * m1;
            if (d.sqrMagnitude <= 0.000001f)
            {
                d = i == 0 ? m0 : m1;
            }
            path.Add(p, d, segment);
        }
    }

    // 半円。start から heading 方向へ走り出し、turnSide（進行方向の右）へ曲がって反対向きになる。
    private static void AddSemicircle(Path path, Vector3 start, Vector3 heading, Vector3 turnSide, float radius, int samples, int segment)
    {
        Vector3 center = start + turnSide * radius;
        for (int i = 0; i <= samples; i++)
        {
            float a = Mathf.PI * i / samples;
            Vector3 p = center - turnSide * (radius * Mathf.Cos(a)) + heading * (radius * Mathf.Sin(a));
            Vector3 d = turnSide * Mathf.Sin(a) + heading * Mathf.Cos(a);
            path.Add(p, d, segment);
        }
    }
}
