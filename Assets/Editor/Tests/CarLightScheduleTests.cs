using NUnit.Framework;

// 車のライトの表（2026-10-22）。区間の外は消灯、重なりはヘッド・テールが OR、ウィンカーは後ろの区間が勝つ。
public class CarLightScheduleTests
{
    private static readonly CarLightCue[] Cues =
    {
        new CarLightCue("bundle_video3.svb", 2, 4.0f, 6.5f, blinker: CarLightBlinker.Left),
        new CarLightCue("bundle_video3.svb", 2, 5.0f, 5.3f, headlights: true),
        new CarLightCue("bundle_video3.svb", CarLightCue.AllTracks, 0f, 30f, tailLights: true),
        new CarLightCue("bundle_video3.svb", 2, 6.0f, 7.0f, blinker: CarLightBlinker.Hazard),
    };

    [Test]
    public void OutsideEveryCue_IsAllOff()
    {
        CarLightState state = CarLightSchedule.Resolve(Cues, "bundle_video1.svb", 2, 5.0);

        Assert.That(state, Is.EqualTo(default(CarLightState)));
    }

    [Test]
    public void IntervalIsHalfOpen()
    {
        Assert.That(CarLightSchedule.Resolve(Cues, "bundle_video3.svb", 2, 3.999).blinker, Is.EqualTo(CarLightBlinker.Off));
        Assert.That(CarLightSchedule.Resolve(Cues, "bundle_video3.svb", 2, 4.0).blinker, Is.EqualTo(CarLightBlinker.Left));
        Assert.That(CarLightSchedule.Resolve(Cues, "bundle_video3.svb", 2, 5.3).headlights, Is.False);
    }

    [Test]
    public void OverlappingCues_CombineLampsAndLaterBlinkerWins()
    {
        CarLightState flash = CarLightSchedule.Resolve(Cues, "bundle_video3.svb", 2, 5.1);
        Assert.That(flash.headlights, Is.True);
        Assert.That(flash.tailLights, Is.True, "全 track の区間も効く");
        Assert.That(flash.blinker, Is.EqualTo(CarLightBlinker.Left));

        CarLightState both = CarLightSchedule.Resolve(Cues, "bundle_video3.svb", 2, 6.2);
        Assert.That(both.blinker, Is.EqualTo(CarLightBlinker.Hazard), "後ろに書いた区間が勝つ");
    }

    [Test]
    public void OtherTrack_GetsOnlyTheAllTracksCue()
    {
        CarLightState state = CarLightSchedule.Resolve(Cues, "BUNDLE_VIDEO3.SVB", 5, 5.1);

        Assert.That(state.headlights, Is.False);
        Assert.That(state.tailLights, Is.True, "ファイル名の大文字小文字は区別しない");
        Assert.That(state.blinker, Is.EqualTo(CarLightBlinker.Off));
    }

    [Test]
    public void ShippedTableIsWellFormed()
    {
        foreach (CarLightCue cue in CarLightSchedule.Cues)
        {
            Assert.That(cue.endSec, Is.GreaterThan(cue.startSec), $"{cue.bundle} track {cue.trackId}");
            Assert.That(string.IsNullOrEmpty(cue.bundle), Is.False);
        }
    }

    [Test]
    public void TryParseCue_ReadsTrackTimesAndFlags()
    {
        Assert.That(CarLightSchedule.TryParseCue("bundle_car.svb:3:1.5:4:HT", out CarLightCue a), Is.True);
        Assert.That(a.bundle, Is.EqualTo("bundle_car.svb"));
        Assert.That(a.trackId, Is.EqualTo(3));
        Assert.That(a.startSec, Is.EqualTo(1.5f));
        Assert.That(a.endSec, Is.EqualTo(4f));
        Assert.That(a.headlights && a.tailLights, Is.True);
        Assert.That(a.blinker, Is.EqualTo(CarLightBlinker.Off));

        Assert.That(CarLightSchedule.TryParseCue("bundle_car.svb:all:6:9:z", out CarLightCue b), Is.True);
        Assert.That(b.trackId, Is.EqualTo(CarLightCue.AllTracks));
        Assert.That(b.blinker, Is.EqualTo(CarLightBlinker.Hazard));

        Assert.That(CarLightSchedule.TryParseCue("bundle_car.svb:1:5:4:H", out _), Is.False, "終わりが始まりより前");
        Assert.That(CarLightSchedule.TryParseCue("bundle_car.svb:x:1:2", out _), Is.False);
        Assert.That(CarLightSchedule.TryParseCue("bundle_car.svb:1:2", out _), Is.False);
    }
}
