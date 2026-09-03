using System.Collections.Generic;

// 現在フレームから見て前後にあるキーのフレームを探す。
//
// **4 本の曲線（yaw / pitch / roll / scale）の和を見る。**
// 片方だけを辿ると、たとえば scale だけのキーには前後送りで到達できないのに
// `Del` は効く、という食い違いが起きる。`OnRuntimeTrackKeyDeleteClicked` は
// 4 本すべてから消すので、送りも和で揃える。
//
// `TrackKeyframeCurve` と同じ理由でここに切り出してある。MonoBehaviour の
// 中に置いたままだと EditMode テストから触れず、実機まで一度も実行されない。
public static class TrackKeyNavigation
{
    // prevFrame / nextFrame は見つからなければ -1。
    // 現在フレームちょうどのキーはどちらにも含めない（そこには既にいる）。
    public static void FindNeighbors(
        IReadOnlyList<SortedDictionary<int, float>> curves,
        int currentFrame,
        out int prevFrame,
        out int nextFrame)
    {
        prevFrame = -1;
        nextFrame = -1;
        if (curves == null)
        {
            return;
        }

        for (int i = 0; i < curves.Count; i++)
        {
            SortedDictionary<int, float> keys = curves[i];
            if (keys == null)
            {
                continue;
            }

            foreach (KeyValuePair<int, float> kv in keys)
            {
                int frame = kv.Key;
                if (frame < currentFrame)
                {
                    if (frame > prevFrame)
                    {
                        prevFrame = frame;
                    }
                }
                else if (frame > currentFrame)
                {
                    if (nextFrame < 0 || frame < nextFrame)
                    {
                        nextFrame = frame;
                    }
                }
            }
        }
    }
}
