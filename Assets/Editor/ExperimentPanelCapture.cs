using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 実験パネル（ExperimentPanel）の見た目を Editor でレンダリングして PNG に落とす。
//
// **なぜ要るか**: このパネルは ExperimentScene の実行時にしか生成されず、実機（Quest）でしか見られない。
// バッチ撮影（BatchPlaybackLogger）はプレイヤーの画面しか撮れないので、ボタンの色や文言の収まりを
// 「ユーザーに絵を見せて決めてもらう」（CLAUDE.md の作業方針）ことが今までできなかった。
//
// 使い方（Editor を閉じてから）:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ExperimentPanelCapture.CaptureStates
//             -outDir <出力先> [-bg black|gray|video]
//
// 注意: Editor では `Selectable.interactable` を変えても色は反映されない（OnSetProperty が
// プレイ中でないときはスプライト交換しかしない）。実際の描画と同じ色にするため、uGUI の
// 状態遷移を `OnPointerExit`（IPointerExitHandler、public）で明示的に走らせてから撮る。
public static class ExperimentPanelCapture
{
    private const int CaptureWidth = 1280;
    private const int CaptureHeight = 960;

    public static void CaptureStates()
    {
        string outDir = ReadArg("-outDir", "Docs/tmp/panel_colors");
        string bgName = ReadArg("-bg", "gray");
        Directory.CreateDirectory(outDir);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Camera cam = new GameObject("CaptureCamera").AddComponent<Camera>();
        cam.transform.position = Vector3.zero;
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = ResolveBackground(bgName);
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.05f;

        // 試行中のバー（2026-09-29 からボタン 2 個）。
        CaptureTrialBar(cam, outDir);
        // 待機画面（Default レイアウト、押せる 2 個）。
        CaptureWaitingPanel(cam, outDir);

        Debug.Log($"[PanelCapture] 出力: {Path.GetFullPath(outDir)}");
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void CaptureTrialBar(Camera cam, string outDir)
    {
        ExperimentPanel panel = new ExperimentPanel(null, () => cam)
        {
            DistanceMeters = 1.2f,
            SizeMeters = new Vector2(0.6f, 0.19f),
            CurrentLayout = ExperimentPanel.TrialBarLayout,
        };

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            // 70 秒が過ぎるまで押せない（interactable = false）。
            ExperimentPanel.ButtonSpec.Create("視聴を終了", null, false),
            ExperimentPanel.ButtonSpec.CreateConfirm("実験者用: 中止", null),
        };
        panel.Show("3 / 9 本目の動画", string.Empty, specs);
        Place(panel, cam);

        // (1) 70 秒前: 「視聴を終了」が押せない / 中止は押せる
        ApplyStates(new[] { false, true }, armConfirmIndex: -1);
        Render(cam, Path.Combine(outDir, "trialbar_1_before70s.png"));

        // (2) 70 秒後: 両方押せる
        ApplyStates(new[] { true, true }, armConfirmIndex: -1);
        Render(cam, Path.Combine(outDir, "trialbar_2_after70s.png"));

        // (3) 「実験者用: 中止」を 1 回押した後（2 度押しの 1 度目。色だけ変わる）
        ApplyStates(new[] { true, true }, armConfirmIndex: 1);
        Render(cam, Path.Combine(outDir, "trialbar_3_confirm_armed.png"));

        panel.Destroy();
    }

    private static void CaptureWaitingPanel(Camera cam, string outDir)
    {
        ExperimentPanel panel = new ExperimentPanel(null, () => cam)
        {
            DistanceMeters = 1.2f,
            SizeMeters = new Vector2(1.05f, 0.82f),
            CurrentLayout = ExperimentPanel.DefaultLayout,
        };

        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("この動画を開始", null),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", null),
        };
        panel.Show(
            "待機中",
            "参加者 ID: P01\n次: 3 / 9 本目の動画\n\n" +
            "アンケートの記入が終わったら、実験者の合図で\n「この動画を開始」を押してください。",
            specs);
        Place(panel, cam);

        // (4) 作り直した直後の 0.4 秒（全部押せない）
        ApplyStates(new[] { false, false }, armConfirmIndex: -1);
        Render(cam, Path.Combine(outDir, "waiting_1_armdelay.png"));

        // (5) 通常
        ApplyStates(new[] { true, true }, armConfirmIndex: -1);
        Render(cam, Path.Combine(outDir, "waiting_2_normal.png"));

        // (6) 「中止して Home へ」の 1 度目
        ApplyStates(new[] { true, true }, armConfirmIndex: 1);
        Render(cam, Path.Combine(outDir, "waiting_3_confirm_armed.png"));

        panel.Destroy();
    }

    private static void Place(ExperimentPanel panel, Camera cam)
    {
        panel.UpdatePlacement(cam.transform, ExperimentPanel.ResolveFrontForward(cam.transform));
    }

    // interactable と 2 度押しの色を、実際のコード経路を通して反映させる。
    private static void ApplyStates(bool[] interactable, int armConfirmIndex)
    {
        GameObject root = ExperimentPanel.ActiveRoot;
        if (root == null)
        {
            return;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null)
            {
                continue;
            }

            if (i < interactable.Length)
            {
                button.interactable = interactable[i];
                Text label = button.GetComponentInChildren<Text>(true);
                if (label != null)
                {
                    // ApplyButtonInteractable と同じ扱い（押せないときは文字も灰色）。
                    label.color = interactable[i] ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                }
            }

            if (i == armConfirmIndex)
            {
                // 実際のハンドラ（HandleConfirmClick）を通して 1 度目の状態にする。
                button.onClick.Invoke();
            }

            // Editor では interactable の変更が色に反映されないので、状態遷移を明示的に走らせる。
            button.OnPointerExit(null);
        }

        Canvas.ForceUpdateCanvases();
    }

    private static void Render(Camera cam, string path)
    {
        RenderTexture rt = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
        RenderTexture previousTarget = cam.targetTexture;
        RenderTexture previousActive = RenderTexture.active;

        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        Texture2D texture = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0);
        texture.Apply();

        File.WriteAllBytes(path, texture.EncodeToPNG());
        Debug.Log($"[PanelCapture] {path}");

        cam.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        Object.DestroyImmediate(texture);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    private static Color ResolveBackground(string name)
    {
        switch (name)
        {
            case "black":
                return Color.black;
            case "video":
                // 動画の明るめの場面に重なったとき。
                return new Color(0.55f, 0.55f, 0.58f, 1f);
            default:
                return new Color(0.18f, 0.18f, 0.2f, 1f);
        }
    }

    private static string ReadArg(string name, string fallback)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, System.StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return fallback;
    }
}
