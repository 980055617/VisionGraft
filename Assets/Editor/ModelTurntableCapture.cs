using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// 実験の Model ボタンで替わるモデルを 1 体ずつ 360° 回して撮る（2026-10-30、ゼミの発表の素材。
// ユーザー「切り替え用に作ったモデルそれぞれの全容が見れる動画をつくほしい」）。
// 空のシーンに 1 体ずつ置き、カメラと光は止めたままモデルを鉛直軸のまわりに 1 周回す（回転台と同じで、光の当たり方は前から見たまま）。
// 連番の jpg を書くだけで、mp4 にするのは外の ffmpeg。
//   Unity.exe -batchmode -projectPath ... -executeMethod ModelTurntableCapture.Run
//     -turntableModels "Human/19_Female_Meshy_Soccer;Animal/54_MeshyWolf;Else/22_Car_Meshy_Classic"（Assets/Resources/Models/ からの名前）
//     -turntableOut C:/.../turntable [-turntableFrames 180] [-turntableSize 1600] [-turntableElevation 12]
// 出力: <out>/<prefab 名>/f0000.jpg …（1 周を frames 枚）。最初の 1 枚はモデルの正面（−Z の側、実験で視聴者に向く側）から。
// 車はヘッドライトと尾灯を点ける（実験の car2 の動画は 3 台とも映っている間ずっと点灯、CarLightSchedule）。
public static class ModelTurntableCapture
{
    private const string LogTag = "[TURNTABLE]";

    public static void Run()
    {
        string models = null;
        string outDir = null;
        int frames = 180;
        int size = 1600;
        float elevation = 12f;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-turntableModels") models = args[i + 1];
            if (args[i] == "-turntableOut") outDir = args[i + 1];
            if (args[i] == "-turntableFrames") int.TryParse(args[i + 1], out frames);
            if (args[i] == "-turntableSize") int.TryParse(args[i + 1], out size);
            if (args[i] == "-turntableElevation")
            {
                float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out elevation);
            }
        }

        if (string.IsNullOrEmpty(models) || string.IsNullOrEmpty(outDir))
        {
            Debug.LogError($"{LogTag} -turntableModels と -turntableOut が要る");
            EditorApplication.Exit(2);
            return;
        }

        int failures;
        bool async = ShaderUtil.allowAsyncCompilation;
        try
        {
            // シェーダの準備の前に描くと 1 枚目が白い影絵になる（2026-10-13 の毛の撮影）。撮る間だけ同期にする（設定には書かない）。
            ShaderUtil.allowAsyncCompilation = false;
            failures = CaptureAll(models.Split(';'), outDir, Mathf.Max(1, frames), Mathf.Clamp(size, 64, 4096), elevation);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogTag} {ex}");
            failures = 1;
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = async;
        }

        Debug.Log($"{LogTag} done failures={failures}");
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    private static int CaptureAll(IEnumerable<string> entries, string outDir, int frames, int size, float elevation)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.64f, 0.68f);
        RenderSettings.ambientEquatorColor = new Color(0.50f, 0.50f, 0.52f);
        RenderSettings.ambientGroundColor = new Color(0.30f, 0.29f, 0.28f);

        Camera cam = new GameObject("TurntableCamera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.82f, 0.84f, 0.88f);
        cam.fieldOfView = 30f;

        // 光はカメラに付けて止めておく（主光は左上の前、弱い補助光は右から）。
        Light key = new GameObject("TurntableKeyLight").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.2f;
        key.shadows = LightShadows.Soft;
        Light fill = new GameObject("TurntableFillLight").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.35f;
        fill.shadows = LightShadows.None;

        int failures = 0;
        try
        {
            foreach (string raw in entries)
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                string path = $"Assets/Resources/Models/{entry}.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    Debug.LogError($"{LogTag} 読めない {path}");
                    failures++;
                    continue;
                }

                GameObject pivot = new GameObject("TurntablePivot");
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    instance.transform.position = Vector3.zero;
                    MeshyModels.CarLights carLights = instance.GetComponentInChildren<MeshyModels.CarLights>(true);
                    if (carLights != null)
                    {
                        carLights.headlights = true;
                        carLights.tailLights = true;
                        carLights.blinker = MeshyModels.CarLights.Blinker.Off;
                        carLights.Apply(0.1f);
                    }

                    // 回す軸はモデルの bounds の中心を通る鉛直線。prefab の root の向き（車の Y 180° など）はそのまま。
                    Bounds b = ComputeRendererBounds(instance);
                    pivot.transform.position = b.center;
                    instance.transform.SetParent(pivot.transform, true);

                    // どの向きでも枠に入るよう、bounds を包む球が画角に収まる距離にする。
                    float radius = Mathf.Max(0.001f, b.extents.magnitude);
                    float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.04f;
                    float el = elevation * Mathf.Deg2Rad;
                    cam.transform.position = b.center + new Vector3(0f, Mathf.Sin(el), -Mathf.Cos(el)) * distance;
                    cam.transform.LookAt(b.center);
                    cam.nearClipPlane = Mathf.Max(0.001f, distance * 0.02f);
                    cam.farClipPlane = distance * 4f;
                    key.transform.rotation = cam.transform.rotation * Quaternion.Euler(28f, 32f, 0f);
                    fill.transform.rotation = cam.transform.rotation * Quaternion.Euler(8f, -55f, 0f);

                    string dir = Path.Combine(outDir, prefab.name);
                    Directory.CreateDirectory(dir);
                    RenderFrame(cam, size, null);
                    for (int f = 0; f < frames; f++)
                    {
                        // 上から見て時計回りにモデルを回す（正面 → 右の側面 → 背中 → 左の側面）。
                        pivot.transform.rotation = Quaternion.Euler(0f, -360f * f / frames, 0f);
                        if (carLights != null)
                        {
                            carLights.Apply(0.1f);
                        }

                        RenderFrame(cam, size, Path.Combine(dir, $"f{f:0000}.jpg"));
                    }

                    Debug.Log($"{LogTag} {prefab.name} frames={frames} size=({b.size.x:F3},{b.size.y:F3},{b.size.z:F3}) " +
                              $"distance={distance:F3} lights={(carLights != null)} out={dir}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    UnityEngine.Object.DestroyImmediate(pivot);
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            UnityEngine.Object.DestroyImmediate(key.gameObject);
            UnityEngine.Object.DestroyImmediate(fill.gameObject);
        }

        return failures;
    }

    private static void RenderFrame(Camera cam, int size, string path)
    {
        RenderTexture rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
        RenderTexture previousActive = RenderTexture.active;
        Texture2D texture = null;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            if (path == null)
            {
                return;
            }

            RenderTexture.active = rt;
            texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToJPG(95));
        }
        finally
        {
            cam.targetTexture = null;
            RenderTexture.active = previousActive;
            if (texture != null)
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static Bounds ComputeRendererBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(go.transform.position, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }
}
