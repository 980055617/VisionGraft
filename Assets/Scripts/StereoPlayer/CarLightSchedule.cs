using System;
using System.Collections.Generic;
using System.Globalization;

// 車のモデル（Meshy の車 3 台。MeshyModels.CarLights を持つもの）のライトを、動画の中の車が光っているのと同じ時刻に
// 点ける・消すための表（2026-10-22 ユーザー「実際動画内で光っていると同じタイミングでフラッシュとかつけ，閉じしたい，
// そこだけハードコードでいい．あとで動画を見て，合わせるようにすればいい」）。
//   - 表は「動画（bundle のファイル名）× track × 再生位置の区間」。どの区間にも入らない時刻は全部消灯
//   - 区間が重なったら、ヘッドライト・テールは「どれかが点けていれば点く」、ウィンカーは表の後ろに書いた区間が勝つ
//   - 時刻は動画の再生位置（秒、meta のフレーム番号 / fps）。一時停止中・モーション中は進まない
//   - ウィンカーの点滅の位相は CarLights 自身の時計（実時間）。置換ありの条件では元の車は消してあるので、位相は合わせない
// 車のモデルが出るのは置換ありの条件だけなので、ほかの条件には効かない。
public enum CarLightBlinker
{
    Off,
    Left,
    Right,
    Hazard,
}

public readonly struct CarLightCue
{
    // track をこの値にすると、その動画の車すべて。
    public const int AllTracks = -1;

    // bundle のファイル名（例 "bundle_video3.svb"）。大文字小文字は区別しない。
    public readonly string bundle;
    public readonly int trackId;
    // [startSec, endSec) の区間で点ける。
    public readonly float startSec;
    public readonly float endSec;
    public readonly bool headlights;
    public readonly bool tailLights;
    public readonly CarLightBlinker blinker;

    public CarLightCue(string bundle, int trackId, float startSec, float endSec,
        bool headlights = false, bool tailLights = false, CarLightBlinker blinker = CarLightBlinker.Off)
    {
        this.bundle = bundle;
        this.trackId = trackId;
        this.startSec = startSec;
        this.endSec = endSec;
        this.headlights = headlights;
        this.tailLights = tailLights;
        this.blinker = blinker;
    }

    public bool Covers(string bundleName, uint track, double videoSec)
    {
        // 区間の端は float で書くので、比べる側も float にそろえる（double のままだと 5.3 秒ちょうどが 5.3f = 5.30000019 未満と
        // 判定され、[5.0, 5.3) の区間に 1 フレーム余計に入る）。
        float t = (float)videoSec;
        return string.Equals(bundle, bundleName, StringComparison.OrdinalIgnoreCase) &&
               (trackId == AllTracks || trackId == track) &&
               t >= startSec && t < endSec;
    }
}

public struct CarLightState : IEquatable<CarLightState>
{
    public bool headlights;
    public bool tailLights;
    public CarLightBlinker blinker;

    public bool Equals(CarLightState other)
    {
        return headlights == other.headlights && tailLights == other.tailLights && blinker == other.blinker;
    }

    public override bool Equals(object obj)
    {
        return obj is CarLightState other && Equals(other);
    }

    public override int GetHashCode()
    {
        return (headlights ? 1 : 0) | (tailLights ? 2 : 0) | ((int)blinker << 2);
    }

    public override string ToString()
    {
        return $"head={(headlights ? 1 : 0)} tail={(tailLights ? 1 : 0)} blinker={blinker}";
    }
}

public static class CarLightSchedule
{
    // ★ 新しい動画（30 秒 × 6 本）が届いたら、動画を見て光っている区間をここに書く（2026-10-22 時点では空 = 全部消灯）。
    //   書き方の例:
    //     new CarLightCue("bundle_video3.svb", 2, 4.0f, 6.5f, blinker: CarLightBlinker.Left),        // track 2 が 4.0〜6.5 秒に左ウィンカー
    //     new CarLightCue("bundle_video3.svb", 2, 10.0f, 10.3f, headlights: true),                   // 10.0 秒に一瞬ヘッドライト（パッシング）
    //     new CarLightCue("bundle_video3.svb", CarLightCue.AllTracks, 0f, 30f, tailLights: true),    // 全部の車のテールを通して点ける
    //   track の番号は Settings の Track 表示か、ログの [CARLIGHT] / [PLACE] の track= で分かる。
    //   バッチでは -carLightCue "bundle:track:start:end:flags"（flags は H=ヘッド T=テール L/R=ウィンカー Z=ハザード）で足せる。
    public static readonly CarLightCue[] Cues =
    {
    };

    public static CarLightState Resolve(IReadOnlyList<CarLightCue> cues, string bundleName, uint trackId, double videoSec)
    {
        CarLightState state = default;
        if (cues == null)
        {
            return state;
        }

        for (int i = 0; i < cues.Count; i++)
        {
            CarLightCue cue = cues[i];
            if (!cue.Covers(bundleName, trackId, videoSec))
            {
                continue;
            }

            state.headlights |= cue.headlights;
            state.tailLights |= cue.tailLights;
            if (cue.blinker != CarLightBlinker.Off)
            {
                state.blinker = cue.blinker;
            }
        }

        return state;
    }

    // "bundle:track:start:end:flags"。track は数字か "all"。flags は H / T / L / R / Z の組み合わせ（空なら消灯の区間）。
    public static bool TryParseCue(string spec, out CarLightCue cue)
    {
        cue = default;
        if (string.IsNullOrEmpty(spec))
        {
            return false;
        }

        string[] parts = spec.Split(':');
        if (parts.Length < 4)
        {
            return false;
        }

        int track;
        if (string.Equals(parts[1], "all", StringComparison.OrdinalIgnoreCase))
        {
            track = CarLightCue.AllTracks;
        }
        else if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out track))
        {
            return false;
        }

        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float start) ||
            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float end) ||
            end <= start)
        {
            return false;
        }

        string flags = parts.Length >= 5 ? parts[4].ToUpperInvariant() : string.Empty;
        CarLightBlinker blinker = CarLightBlinker.Off;
        if (flags.Contains("Z"))
        {
            blinker = CarLightBlinker.Hazard;
        }
        else if (flags.Contains("L"))
        {
            blinker = CarLightBlinker.Left;
        }
        else if (flags.Contains("R"))
        {
            blinker = CarLightBlinker.Right;
        }

        cue = new CarLightCue(parts[0], track, start, end, flags.Contains("H"), flags.Contains("T"), blinker);
        return true;
    }
}
