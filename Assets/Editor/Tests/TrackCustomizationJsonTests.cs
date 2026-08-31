using System.Collections.Generic;
using NUnit.Framework;

// model_selection.json の読み書き。MiniJson に writer が無いので書き出しは手書きで、
// フィールドを足すたびに「書いたのに読めない／読めるのに書いていない」を作りやすい。
// scale を足したときに実際そこが穴になりかけたので、往復を固定する。
public class TrackCustomizationJsonTests
{
    private const string VideoKey = "demo_video.mp4";

    private static Dictionary<string, VideoCustomization> BuildSample()
    {
        var video = new VideoCustomization { numFrames = 1830 };

        TrackCustomization t0 = video.GetOrCreate(0u);
        t0.modelPrefabName = "06_DieselLocomotive";
        t0.yawKeyframes = new SortedDictionary<int, float> { { 0, 15.5f }, { 900, -42.25f } };
        t0.scaleKeyframes = new SortedDictionary<int, float> { { 0, 1f }, { 900, 2.75f } };

        TrackCustomization t1 = video.GetOrCreate(1u);
        t1.scaleKeyframes = new SortedDictionary<int, float> { { 120, 0.5f } };

        return new Dictionary<string, VideoCustomization> { { VideoKey, video } };
    }


    [Test]
    public void ScaleKeyframesSurviveTheRoundTrip()
    {
        string json = TrackCustomizationStore.ToJson(BuildSample());
        Dictionary<string, VideoCustomization> back = TrackCustomizationStore.FromJson(json);

        Assert.That(back.ContainsKey(VideoKey), Is.True, json);
        VideoCustomization video = back[VideoKey];
        Assert.That(video.numFrames, Is.EqualTo(1830));

        TrackCustomization t0 = video.tracks[0u];
        Assert.That(t0.modelPrefabName, Is.EqualTo("06_DieselLocomotive"));
        Assert.That(t0.yawKeyframes[0], Is.EqualTo(15.5f).Within(1e-3f));
        Assert.That(t0.yawKeyframes[900], Is.EqualTo(-42.25f).Within(1e-3f));
        Assert.That(t0.scaleKeyframes[0], Is.EqualTo(1f).Within(1e-3f));
        Assert.That(t0.scaleKeyframes[900], Is.EqualTo(2.75f).Within(1e-3f));

        // モデルも yaw も無く scale だけ、の track が消えないこと（IsEmpty の判定漏れ対策）。
        TrackCustomization t1 = video.tracks[1u];
        Assert.That(t1.modelPrefabName, Is.Null.Or.Empty);
        Assert.That(t1.yawKeyframes, Is.Null);
        Assert.That(t1.scaleKeyframes[120], Is.EqualTo(0.5f).Within(1e-3f));
    }


    [Test]
    public void ScaleOnlyTrackIsNotEmpty()
    {
        var entry = new TrackCustomization
        {
            scaleKeyframes = new SortedDictionary<int, float> { { 0, 1.5f } },
        };

        Assert.That(entry.IsEmpty, Is.False);
        Assert.That(new TrackCustomization().IsEmpty, Is.True);
    }


    [Test]
    public void CloneCopiesScaleKeyframesIndependently()
    {
        var entry = new TrackCustomization
        {
            scaleKeyframes = new SortedDictionary<int, float> { { 0, 1.5f } },
        };

        TrackCustomization copy = entry.Clone();
        copy.scaleKeyframes[0] = 9f;

        Assert.That(entry.scaleKeyframes[0], Is.EqualTo(1.5f));
    }


    // セッション上書き（被験者の調整）を基準ファイルの上に重ねる経路。
    // scale を重ね忘れると、実験中の調整だけが黙って消える。
    [Test]
    public void OverlayCarriesScaleKeyframes()
    {
        var baseline = new VideoCustomization();
        baseline.GetOrCreate(0u).modelPrefabName = "00_Baseball";

        var session = new VideoCustomization();
        session.GetOrCreate(0u).scaleKeyframes = new SortedDictionary<int, float> { { 0, 3f } };

        baseline.OverlayWith(session);

        Assert.That(baseline.tracks[0u].modelPrefabName, Is.EqualTo("00_Baseball"));
        Assert.That(baseline.tracks[0u].scaleKeyframes[0], Is.EqualTo(3f).Within(1e-3f));
    }
}
