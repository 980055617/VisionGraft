using System.Collections.Generic;
using UnityEngine;

// 車のモデルのライトを、動画の中の車が光る時刻に合わせて点ける（2026-10-22。表は CarLightSchedule.Cues）。
// MeshyModels.CarLights（Assets/MeshyModels/Scripts、作者のスクリプト）を持つモデルだけが対象で、ほかのモデルでは何もしない。
// 呼ぶのは ApplyMetaTarget（その track が今のフレームに居て、モデルが出ているとき）。状態が変わったときだけ書いて [CARLIGHT] を出す。
public partial class StreamingStereoVideoPlayer
{
    [Header("Car Lights (2026-10-22)")]
    // false にすると表を読まず、車のライトはモデルの既定（全部消灯）のまま。
    public bool enableCarLightSchedule = true;

    // モデルのインスタンス → CarLights（無ければ null を覚えて、毎 tick 探さない）。モデルを差し替えるとインスタンスが変わる。
    private readonly Dictionary<int, MeshyModels.CarLights> carLightsByInstance = new Dictionary<int, MeshyModels.CarLights>();
    private List<CarLightCue> carLightCues;

    private void ApplyCarLightSchedule(uint trackId, GameObject instance, int frame)
    {
        if (!enableCarLightSchedule || instance == null)
        {
            return;
        }

        MeshyModels.CarLights lights = ResolveCarLights(instance);
        if (lights == null)
        {
            return;
        }

        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 30f);
        double videoSec = frame / (double)fps;
        string bundleName = System.IO.Path.GetFileName(bundleFileName ?? string.Empty);
        CarLightState wanted = CarLightSchedule.Resolve(GetCarLightCues(), bundleName, trackId, videoSec);
        MeshyModels.CarLights.Blinker blinker = ToCarLightsBlinker(wanted.blinker);
        if (lights.headlights == wanted.headlights && lights.tailLights == wanted.tailLights && lights.blinker == blinker)
        {
            return;
        }

        lights.headlights = wanted.headlights;
        lights.tailLights = wanted.tailLights;
        lights.blinker = blinker;
        Debug.Log($"[CARLIGHT] track={trackId} model={instance.name} frame={frame} t={videoSec:F2}s {wanted}");
    }

    private MeshyModels.CarLights ResolveCarLights(GameObject instance)
    {
        int key = instance.GetInstanceID();
        if (carLightsByInstance.TryGetValue(key, out MeshyModels.CarLights cached))
        {
            return cached;
        }

        MeshyModels.CarLights found = instance.GetComponentInChildren<MeshyModels.CarLights>(true);
        carLightsByInstance[key] = found;
        return found;
    }

    private List<CarLightCue> GetCarLightCues()
    {
        if (carLightCues != null)
        {
            return carLightCues;
        }

        carLightCues = new List<CarLightCue>(CarLightSchedule.Cues);
#if UNITY_EDITOR
        // バッチの確かめ用: -carLightCue "bundle:track:start:end:flags"（何度でも）。表には書き込まない。
        if (Application.isBatchMode)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-carLightCue")
                {
                    continue;
                }

                if (CarLightSchedule.TryParseCue(args[i + 1], out CarLightCue cue))
                {
                    carLightCues.Add(cue);
                    Debug.Log($"[CARLIGHT] batch cue {args[i + 1]}");
                }
                else
                {
                    Debug.LogWarning($"[CARLIGHT] -carLightCue: 読めない '{args[i + 1]}'");
                }
            }
        }
#endif
        return carLightCues;
    }

    private static MeshyModels.CarLights.Blinker ToCarLightsBlinker(CarLightBlinker blinker)
    {
        switch (blinker)
        {
            case CarLightBlinker.Left:
                return MeshyModels.CarLights.Blinker.Left;
            case CarLightBlinker.Right:
                return MeshyModels.CarLights.Blinker.Right;
            case CarLightBlinker.Hazard:
                return MeshyModels.CarLights.Blinker.Hazard;
            default:
                return MeshyModels.CarLights.Blinker.Off;
        }
    }
}
