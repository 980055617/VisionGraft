using System.Collections.Generic;

// manifest.json の "shots" ([[start, end), ...]) を runtime で扱える形に正規化する。
//
// bundle の shot_boundary_policy によれば、shots の各レンジは「内部にハードカットのない
// 連続テイク」であり、同じ trackId でも shot をまたげばカメラ距離・見かけサイズが正当に
// 変わる（被写体が動いたのではなくカットが切り替わっただけ）。したがって shot 境界は、
// track ごとにロックした表示スケールや位置の平滑化を持ち越してはいけない点になる。
//   unity_guidance: "Do not interpolate or spring position/scale across a shot boundary for
//   the same trackId; snap to the new shot's first-frame anchor instead."
//
// shots を持たない旧 bundle では Empty になり、ResolveShotIndex が常に 0 を返すので
// 全編が 1 shot 扱い = 従来どおりロックしっぱなしの挙動になる。
public sealed class ShotBoundaries
{
    public static readonly ShotBoundaries Empty = new ShotBoundaries(new int[0]);

    // 各 shot の開始フレーム。昇順・重複なし。end は保持しない: リセット判定に必要なのは
    // 「フレームがどの shot に属すか」だけで、レンジ間に隙間がある bundle でも隙間フレームを
    // 直前の shot の続きとして扱ったほうが余計なリセットが起きず安全なため。
    private readonly int[] startFrames;

    private ShotBoundaries(int[] startFrames)
    {
        this.startFrames = startFrames;
    }

    public bool HasShots
    {
        get { return startFrames.Length > 0; }
    }

    public int Count
    {
        get { return startFrames.Length; }
    }

    public int GetStartFrame(int shotIndex)
    {
        if (shotIndex < 0 || shotIndex >= startFrames.Length)
        {
            return 0;
        }

        return startFrames[shotIndex];
    }

    // JsonUtility は [[0, 258], [258, 338], ...] のような入れ子配列を扱えないため、
    // manifest の生 JSON から MiniJson で shots だけを読む。
    public static ShotBoundaries FromManifestJson(string manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return Empty;
        }

        Dictionary<string, object> root = MiniJson.Parse(manifestJson) as Dictionary<string, object>;
        if (root == null || !root.TryGetValue("shots", out object shotsValue))
        {
            return Empty;
        }

        return FromShotsValue(shotsValue as List<object>);
    }

    private static ShotBoundaries FromShotsValue(List<object> shots)
    {
        if (shots == null || shots.Count == 0)
        {
            return Empty;
        }

        SortedSet<int> uniqueStarts = new SortedSet<int>();
        for (int i = 0; i < shots.Count; i++)
        {
            if (TryReadShotStartFrame(shots[i] as List<object>, out int startFrame))
            {
                uniqueStarts.Add(startFrame);
            }
        }

        if (uniqueStarts.Count == 0)
        {
            return Empty;
        }

        int[] startFrames = new int[uniqueStarts.Count];
        uniqueStarts.CopyTo(startFrames);
        return new ShotBoundaries(startFrames);
    }

    private static bool TryReadShotStartFrame(List<object> range, out int startFrame)
    {
        startFrame = 0;
        if (range == null || range.Count < 2)
        {
            return false;
        }

        if (!TryReadFrameIndex(range[0], out int start) || !TryReadFrameIndex(range[1], out int end))
        {
            return false;
        }

        // 空レンジ・逆順レンジは壊れたデータなので採用しない。
        if (start < 0 || end <= start)
        {
            return false;
        }

        startFrame = start;
        return true;
    }

    private static bool TryReadFrameIndex(object value, out int frameIndex)
    {
        // MiniJson は整数を long、小数を double で返す。
        if (value is long longValue)
        {
            frameIndex = (int)longValue;
            return true;
        }

        if (value is double doubleValue)
        {
            frameIndex = (int)doubleValue;
            return true;
        }

        frameIndex = 0;
        return false;
    }

    // frame が属する shot の index。shots を持たない場合は常に 0。
    // 先頭 shot の開始より前のフレームも 0 に丸める（先頭 shot の続き扱い）。
    public int ResolveShotIndex(int frame)
    {
        if (startFrames.Length == 0)
        {
            return 0;
        }

        int found = System.Array.BinarySearch(startFrames, frame);
        if (found >= 0)
        {
            return found;
        }

        // ~found は frame を超える最初の要素の位置。その 1 つ手前が frame の属する shot。
        int insertion = ~found;
        return insertion > 0 ? insertion - 1 : 0;
    }
}
