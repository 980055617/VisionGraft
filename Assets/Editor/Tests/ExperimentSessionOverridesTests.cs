using System.Collections.Generic;
using NUnit.Framework;

// 調整（向き・大きさ・モデル）の書き込み先（2026-10-27、ExperimentSessionOverrides.ResolveWritable）。
//   - 実験者のデバッグのセッションは基準ファイル（TrackCustomizationStore）へ書く
//   - 参加者のセッションは書かない（セッション上書きだけ）。デバッグの後に始めた参加者のセッションでも同じ
//   - 実験の外（通常の視聴）は今までどおり基準へ書く
// 基準はメモリの上だけを見る。ファイルへは書かず、終わりに Reload でメモリの写しを捨てる。
public class ExperimentSessionOverridesTests
{
    private const string Key = "__test_session_overrides__.mp4";

    [TearDown]
    public void TearDown()
    {
        ExperimentSessionOverrides.EndSession();
        TrackCustomizationStore.Reload();
    }

    [Test]
    public void DebugSessionWritesTheBaseline()
    {
        ExperimentSessionOverrides.BeginSession(writesBaseline: true);

        Assert.That(ExperimentSessionOverrides.Active, Is.True);
        Assert.That(ExperimentSessionOverrides.WritesToBaseline, Is.True);
        VideoCustomization target = ExperimentSessionOverrides.ResolveWritable(Key, 720);
        Assert.That(target, Is.SameAs(TrackCustomizationStore.Get(Key)));
        Assert.That(ExperimentSessionOverrides.Get(Key), Is.Null);
    }

    [Test]
    public void ParticipantSessionAfterDebugDoesNotWriteTheBaseline()
    {
        ExperimentSessionOverrides.BeginSession(writesBaseline: true);
        ExperimentSessionOverrides.ResolveWritable(Key, 720);
        ExperimentSessionOverrides.EndSession();
        TrackCustomizationStore.Reload();

        ExperimentSessionOverrides.BeginSession();
        Assert.That(ExperimentSessionOverrides.WritesToBaseline, Is.False);
        VideoCustomization target = ExperimentSessionOverrides.ResolveWritable(Key, 720);
        target.GetOrCreate(0).yawKeyframes = new SortedDictionary<int, float> { { 10, 30f } };

        Assert.That(TrackCustomizationStore.Get(Key), Is.Null);
        Assert.That(ExperimentSessionOverrides.Get(Key), Is.SameAs(target));
    }

    [Test]
    public void DebugMarkDoesNotSurviveIntoTheNextSession()
    {
        // デバッグを閉じずに次のセッションを始めても（BeginSession の決め直し）、印は残らない。
        ExperimentSessionOverrides.BeginSession(writesBaseline: true);
        ExperimentSessionOverrides.BeginSession();
        Assert.That(ExperimentSessionOverrides.DebugWritesBaseline, Is.False);
        Assert.That(ExperimentSessionOverrides.WritesToBaseline, Is.False);

        ExperimentSessionOverrides.BeginSession(writesBaseline: true);
        ExperimentSessionOverrides.EndSession();
        Assert.That(ExperimentSessionOverrides.DebugWritesBaseline, Is.False);
        Assert.That(ExperimentSessionOverrides.Active, Is.False);
    }

    [Test]
    public void OutsideTheExperimentWritesTheBaseline()
    {
        ExperimentSessionOverrides.EndSession();

        Assert.That(ExperimentSessionOverrides.WritesToBaseline, Is.True);
        Assert.That(ExperimentSessionOverrides.ResolveWritable(Key, 720), Is.SameAs(TrackCustomizationStore.Get(Key)));
    }
}
