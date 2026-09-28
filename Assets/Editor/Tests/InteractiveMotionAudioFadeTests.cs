using NUnit.Framework;

// 動画を止める前後の音量フェード。急に音が消えないこと、止めたときだけ Play が要ること、を押さえる。
public class InteractiveMotionAudioFadeTests
{
    [Test]
    public void FadeOutReachesZeroThenSignalsPauseOnce()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        fade.BeginFadeOut(1f, 0.5f);

        int pauseSignals = 0;
        float lastVolume = 1f;
        for (int i = 0; i < 10; i++)
        {
            InteractiveMotionAudioFade.Step step = fade.Tick(0.1f);
            Assert.That(step.volume, Is.LessThanOrEqualTo(lastVolume), "monotonic");
            lastVolume = step.volume;
            if (step.pauseNow) { pauseSignals++; }
        }

        Assert.That(pauseSignals, Is.EqualTo(1));
        Assert.That(fade.Volume, Is.EqualTo(0f));
        Assert.That(fade.CurrentPhase, Is.EqualTo(InteractiveMotionAudioFade.Phase.Paused));
    }

    [Test]
    public void FadeOutTakesTheConfiguredSeconds()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        fade.BeginFadeOut(1f, 0.5f);
        fade.Tick(0.25f);
        Assert.That(fade.Volume, Is.EqualTo(0.5f).Within(0.001f));
        Assert.That(fade.CurrentPhase, Is.EqualTo(InteractiveMotionAudioFade.Phase.FadingOut));
        InteractiveMotionAudioFade.Step step = fade.Tick(0.25f);
        Assert.That(step.pauseNow, Is.True);
    }

    [Test]
    public void FadeInRestoresBaseVolumeAndReportsPlayOnlyWhenPaused()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        fade.BeginFadeOut(0.8f, 0.2f);   // 基準音量が 1 でない場合も戻り先はその値
        fade.Tick(1f);
        Assert.That(fade.CurrentPhase, Is.EqualTo(InteractiveMotionAudioFade.Phase.Paused));

        bool needsPlay = fade.BeginFadeIn(0.2f);
        Assert.That(needsPlay, Is.True);
        fade.Tick(0.1f);
        Assert.That(fade.Volume, Is.EqualTo(0.4f).Within(0.001f));
        fade.Tick(0.1f);
        Assert.That(fade.Volume, Is.EqualTo(0.8f).Within(0.001f));
        Assert.That(fade.CurrentPhase, Is.EqualTo(InteractiveMotionAudioFade.Phase.Idle));
    }

    // 止める前にイベントが終わった（Motion を OFF にした等）: 動画は止まっていないので Play は不要、音量だけ戻す。
    [Test]
    public void FadeInDuringFadeOutDoesNotRequirePlay()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        fade.BeginFadeOut(1f, 0.5f);
        fade.Tick(0.2f);
        Assert.That(fade.Volume, Is.EqualTo(0.6f).Within(0.001f));

        bool needsPlay = fade.BeginFadeIn(0.5f);
        Assert.That(needsPlay, Is.False);
        InteractiveMotionAudioFade.Step step = fade.Tick(0.1f);
        Assert.That(step.pauseNow, Is.False);
        Assert.That(step.volume, Is.EqualTo(0.8f).Within(0.001f));
    }

    [Test]
    public void ZeroSecondsIsImmediate()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        fade.BeginFadeOut(1f, 0f);
        InteractiveMotionAudioFade.Step step = fade.Tick(0.016f);
        Assert.That(step.pauseNow, Is.True);
        Assert.That(step.volume, Is.EqualTo(0f));

        Assert.That(fade.BeginFadeIn(0f), Is.True);
        step = fade.Tick(0.016f);
        Assert.That(step.volume, Is.EqualTo(1f));
        Assert.That(fade.CurrentPhase, Is.EqualTo(InteractiveMotionAudioFade.Phase.Idle));
    }

    [Test]
    public void IdleTickDoesNothing()
    {
        InteractiveMotionAudioFade fade = new InteractiveMotionAudioFade();
        InteractiveMotionAudioFade.Step step = fade.Tick(0.1f);
        Assert.That(step.applyVolume, Is.False);
        Assert.That(step.pauseNow, Is.False);
        Assert.That(fade.BeginFadeIn(0.5f), Is.False);
    }
}
