using System.Collections.Generic;
using UnityEngine;

// 画面の左右の端から出た物・人・動物の続き（frame out 継続）で進む向き（2026-10-26、StreamingStereoVideoPlayer.frameOutPassByViewer、既定 OFF）。
// 純粋な幾何だけ（シーンに触らない）なので EditMode テストで確かめられる（Assets/Editor/Tests/FrameOutPassByTests.cs）。
//
// 決まり（物・人・動物で同じ）:
//   1. 端に掛かる前に測った進む向き h0 から始める（呼ぶ側が渡す。物 = 映像と同じ絵になる「大きさの変わらない物」の動き、人・動物 = 体の前）
//   2. 通る距離 = その向きにまっすぐ進んだとき、目に近づく点（骨・箱の角）が目のいちばん近くを通る距離のうち最小のもの
//      （地面の上の 2 次元。出ていく側を +、反対側を通る・目を突き抜けるなら −。目に近づく点が無ければ +∞ = 目の横を通らない）
//   3. 通る距離が [passMin, passMax] に入っていれば h0 のまま。入っていなければ up まわりに回して入る最小の角を探す
//      （ScanStepDegrees 刻みで両回り、h0 が要る向き（近すぎる・反対側 → 出ていく側、遠すぎる・近づかない → 目の方）を先に。
//      見つけた刻みの中を RefineSteps 回の二分で詰める）。maxTurnDegrees 以内に無ければ使わない（呼ぶ側は今の向きのまま）
//   4. どこかの点がもう passMin より目に近い（内側）なら使わない
// pointsTurnWithHeading: 向きも同じ角だけ回す物（ホイールのある車）は、点を pivot（root）まわりに一緒に回して測る。
//
// 2 次元の座標: x = 出ていく側 exitSide、z = 目の前 sceneForward（exitSide に直交させたもの）、角 phi = atan2(x, z)
// （0 = 目から遠ざかる、180° = 目へ向かう）。点 p について、along = p·h（負なら目に近づく）、miss = p.z sin phi − p.x cos phi
// （いちばん近づいたときの、進む線から目までの符号付きの距離。+ は出ていく側）。phi を減らすと miss が増える（出ていく側へ回る）。
public static class FrameOutPassBy
{
    public const float ScanStepDegrees = 0.5f;
    public const int RefineSteps = 16;

    public struct Result
    {
        public bool ok;
        public Vector3 heading;              // 地面の上の単位ベクトル（world）
        public float bendDegrees;            // h0 → heading の角（Vector3.SignedAngle(h0, heading, up)）
        public float measuredPassMeters;     // h0 のままの通る距離（+∞ = 近づかない）
        public float passMeters;             // heading の通る距離
        public float nearestStartMeters;     // 始めの、目にいちばん近い点の距離（地面の上）
        public string note;
    }

    // pointsFromEye / pivotFromEye: 目からの位置（world、地面へ射影していなくてよい）。measuredHeading・exitSide・sceneForward も world。
    public static Result Resolve(IList<Vector3> pointsFromEye, Vector3 pivotFromEye, Vector3 measuredHeading, Vector3 exitSide,
        Vector3 sceneForward, Vector3 up, float passMin, float passMax, float maxTurnDegrees, bool pointsTurnWithHeading)
    {
        Result result = default(Result);
        result.measuredPassMeters = float.PositiveInfinity;
        result.passMeters = float.PositiveInfinity;
        result.nearestStartMeters = float.PositiveInfinity;
        up = up.sqrMagnitude > 1e-12f ? up.normalized : Vector3.up;
        Vector3 e = Vector3.ProjectOnPlane(exitSide, up);
        Vector3 h0 = Vector3.ProjectOnPlane(measuredHeading, up);
        if (pointsFromEye == null || pointsFromEye.Count == 0 || e.sqrMagnitude <= 1e-12f || h0.sqrMagnitude <= 1e-12f)
        {
            result.note = "no input";
            return result;
        }

        e.Normalize();
        h0.Normalize();
        // z の向き（目の前）を取り違えると左右が逆になる（miss の符号が反転する）。
        Vector3 f = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(sceneForward, up), e);
        if (f.sqrMagnitude <= 1e-12f)
        {
            result.note = "no forward";
            return result;
        }

        f.Normalize();
        int n = pointsFromEye.Count;
        Vector2[] pts = new Vector2[n];
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vector2(Vector3.Dot(pointsFromEye[i], e), Vector3.Dot(pointsFromEye[i], f));
            nearest = Mathf.Min(nearest, pts[i].magnitude);
        }

        result.nearestStartMeters = nearest;
        Vector2 pivot = new Vector2(Vector3.Dot(pivotFromEye, e), Vector3.Dot(pivotFromEye, f));
        float phi0 = Mathf.Atan2(Vector3.Dot(h0, e), Vector3.Dot(h0, f));
        float m0 = PassDistance(pts, pivot, phi0, 0f, pointsTurnWithHeading);
        result.measuredPassMeters = m0;
        if (nearest <= passMin)
        {
            result.note = "inside";
            return result;
        }

        float turn;
        string note;
        if (InBand(m0, passMin, passMax))
        {
            turn = 0f;
            note = "in band";
        }
        else if (!TryFindTurn(pts, pivot, phi0, m0 < passMin ? -1f : 1f, passMin, passMax, maxTurnDegrees, pointsTurnWithHeading, out turn))
        {
            result.note = "no heading within " + maxTurnDegrees.ToString("F0") + "deg";
            return result;
        }
        else
        {
            note = turn < 0f ? "toward exit" : "toward viewer";
        }

        float phi = phi0 + turn;
        Vector3 heading = (e * Mathf.Sin(phi) + f * Mathf.Cos(phi)).normalized;
        result.ok = true;
        result.heading = heading;
        result.bendDegrees = Vector3.SignedAngle(h0, heading, up);
        result.passMeters = PassDistance(pts, pivot, phi, turn, pointsTurnWithHeading);
        result.note = note;
        return result;
    }

    // 回す角（2 次元の phi の向き、ラジアン）。first: 先に試す回り（−1 = phi を減らす = 出ていく側、+1 = 目の方）。
    private static bool TryFindTurn(Vector2[] pts, Vector2 pivot, float phi0, float first, float passMin, float passMax,
        float maxTurnDegrees, bool turnPoints, out float turn)
    {
        turn = 0f;
        float maxTurn = Mathf.Max(0f, maxTurnDegrees) * Mathf.Deg2Rad;
        float step = ScanStepDegrees * Mathf.Deg2Rad;
        int steps = Mathf.CeilToInt(Mathf.Max(0f, maxTurnDegrees) / ScanStepDegrees);
        for (int k = 1; k <= steps; k++)
        {
            float g = Mathf.Min(k * step, maxTurn);
            for (int w = 0; w < 2; w++)
            {
                float sgn = w == 0 ? first : -first;
                if (!InBand(PassDistance(pts, pivot, phi0 + sgn * g, sgn * g, turnPoints), passMin, passMax))
                {
                    continue;
                }

                // (k − 1) 刻み目は帯の外（k = 1 なら h0）、k 刻み目は帯の中。境目まで詰める。
                float lo = Mathf.Min((k - 1) * step, g);
                float hi = g;
                for (int r = 0; r < RefineSteps; r++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (InBand(PassDistance(pts, pivot, phi0 + sgn * mid, sgn * mid, turnPoints), passMin, passMax))
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }

                turn = sgn * hi;
                return true;
            }
        }

        return false;
    }

    // 向き phi の通る距離。turn = h0 からの回し角（turnPoints なら点を pivot まわりに同じだけ回してから測る）。
    private static float PassDistance(Vector2[] pts, Vector2 pivot, float phi, float turn, bool turnPoints)
    {
        float s = Mathf.Sin(phi);
        float c = Mathf.Cos(phi);
        float ct = Mathf.Cos(turn);
        float st = Mathf.Sin(turn);
        bool rotate = turnPoints && turn != 0f;
        float m = float.PositiveInfinity;
        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 p = pts[i];
            if (rotate)
            {
                // phi の向き（z から x へ）に turn だけ回す。
                Vector2 v = p - pivot;
                p = pivot + new Vector2(v.x * ct + v.y * st, v.y * ct - v.x * st);
            }

            float along = p.x * s + p.y * c;
            if (along < 0f)
            {
                m = Mathf.Min(m, p.y * s - p.x * c);
            }
        }

        return m;
    }

    private static bool InBand(float m, float passMin, float passMax)
    {
        return m >= passMin && m <= passMax;
    }
}
