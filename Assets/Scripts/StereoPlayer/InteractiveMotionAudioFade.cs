using UnityEngine;

// Random のインタラクティブモーションで動画を止める前後の音量フェード（2026-09-28、ユーザー指示）。
// 音が急に消えるのが驚かれるので、止める前に fadeSeconds かけて 0 へ落とし、再開後に同じ時間で元へ戻す。
// 純粋な状態機械（Unity のシーンに触らない）。VideoPlayer への反映は呼び出し側（Tick の戻り値で指示する）。
//
//   Idle ──BeginFadeOut──▶ FadingOut ──(音量 0)──▶ Paused ──BeginFadeIn──▶ FadingIn ──(元の音量)──▶ Idle
//                              └──BeginFadeIn（止める前に終わった）──▶ FadingIn
public sealed class InteractiveMotionAudioFade
{
    public enum Phase { Idle, FadingOut, Paused, FadingIn }

    public struct Step
    {
        public float volume;      // 今フレームの音量（0〜baseVolume）
        public bool applyVolume;  // 音量を VideoPlayer に書く必要があるか
        public bool pauseNow;     // フェードアウトが終わった。この tick で動画を止める
    }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public float Volume { get; private set; } = 1f;
    public float BaseVolume { get; private set; } = 1f;
    private float fadeSeconds;

    public void Reset(float baseVolume = 1f)
    {
        CurrentPhase = Phase.Idle;
        BaseVolume = Mathf.Clamp01(baseVolume);
        Volume = BaseVolume;
    }

    // 動画を止めたい: 今の音量から 0 へ落とし始める。既にフェード中なら基準音量は据え置く。
    public void BeginFadeOut(float currentVolume, float seconds)
    {
        if (CurrentPhase == Phase.Idle)
        {
            BaseVolume = Mathf.Clamp01(currentVolume);
            Volume = BaseVolume;
        }
        fadeSeconds = Mathf.Max(0f, seconds);
        CurrentPhase = Phase.FadingOut;
    }

    // 動画を戻したい。戻り値 true なら動画は止まっていたので呼び出し側が Play する。
    public bool BeginFadeIn(float seconds)
    {
        fadeSeconds = Mathf.Max(0f, seconds);
        bool wasPaused = CurrentPhase == Phase.Paused;
        if (CurrentPhase == Phase.Idle)
        {
            return false;
        }
        CurrentPhase = Phase.FadingIn;
        return wasPaused;
    }

    public Step Tick(float deltaSeconds)
    {
        Step step = new Step { volume = Volume };
        float dt = Mathf.Max(0f, deltaSeconds);
        float rate = fadeSeconds > 0.0001f ? BaseVolume * dt / fadeSeconds : float.MaxValue;
        switch (CurrentPhase)
        {
            case Phase.FadingOut:
                Volume = Mathf.Max(0f, Volume - rate);
                step.volume = Volume;
                step.applyVolume = true;
                if (Volume <= 0f)
                {
                    CurrentPhase = Phase.Paused;
                    step.pauseNow = true;
                }
                break;
            case Phase.FadingIn:
                Volume = Mathf.Min(BaseVolume, Volume + rate);
                step.volume = Volume;
                step.applyVolume = true;
                if (Volume >= BaseVolume)
                {
                    CurrentPhase = Phase.Idle;
                }
                break;
        }
        return step;
    }
}
