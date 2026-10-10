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
            // 3 本目の前（ブロックの途中）なのでアンケートの案内も実験者の合図も出ない。
            ExperimentController.ResolveTrialWaitingInstruction(false, 2),
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

    // 置換ありの練習（ExperimentTutorial）のパネルを段階ごとに撮る。文面の収まり（1 行 24 文字・5 行）を絵で確かめる。
    //   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ExperimentPanelCapture.CaptureTutorialModelReplaced
    //             -outDir <出力先> [-bg black|gray|video]
    public static void CaptureTutorialModelReplaced()
    {
        string outDir = ReadArg("-outDir", "Docs/tmp/tutorial_panels");
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

        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        // 練習の最初に出るモデル。これが無いと「全部表示しない」の案内と区別が付かない。
        tutorial.RecordOperation("model_assigned", "track=0 category=person prefab=00_Female_A_01");
        tutorial.RecordOperation("model_assigned", "track=1 category=animal prefab=36_LabradorDog");

        for (int i = 0; i < 10; i++)
        {
            bool done = tutorial.IsDone;
            ExperimentPanel panel = new ExperimentPanel(null, () => cam)
            {
                DistanceMeters = 1.2f,
                SizeMeters = new Vector2(0.6f, 0.33f),
                CurrentLayout = ExperimentPanel.CompactLargeTextLayout,
            };

            // ExperimentController.BuildTutorialPanelButtons と同じ並び。
            List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>();
            if (done)
            {
                specs.Add(ExperimentPanel.ButtonSpec.Create("視聴を終了", null));
            }
            specs.Add(ExperimentPanel.ButtonSpec.CreateConfirm("実験者用: 終了", null));

            panel.Show(tutorial.Title, tutorial.Body, specs);
            Place(panel, cam);
            bool[] interactable = new bool[specs.Count];
            for (int b = 0; b < interactable.Length; b++)
            {
                interactable[b] = true;
            }
            ApplyStates(interactable, armConfirmIndex: -1);
            Render(cam, Path.Combine(outDir, $"tutorialC_{i + 1}_{tutorial.CurrentStep}.png"));
            panel.Destroy();

            if (done)
            {
                break;
            }
            AdvanceTutorialForCapture(tutorial);
        }

        Debug.Log($"[PanelCapture] 出力: {Path.GetFullPath(outDir)}");
        EditorSceneManager.MarkSceneDirty(scene);
    }

    // 待機画面の指示（ブロックの途中 / 頭）と、置換ありの前の練習の待機画面（内容の一覧）を、
    // 2026-10-01 に変える前の文面と並べて撮る。変える前の文面はここに写してある（比較用）。
    //   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ExperimentPanelCapture.CaptureWaitingPanels
    //             -outDir <出力先> [-bg black|gray|video]
    public static void CaptureWaitingPanels()
    {
        string outDir = ReadArg("-outDir", "Docs/tmp/waiting_panels");
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

        const string oldInstruction = "実験者の合図があったら\n「この動画を開始」を押してください。";
        RenderTrialWaiting(cam, Path.Combine(outDir, "waiting_3of9_before.png"), "次: 3 / 9 本目の動画", oldInstruction);
        RenderTrialWaiting(cam, Path.Combine(outDir, "waiting_3of9_after.png"), "次: 3 / 9 本目の動画",
            ExperimentController.ResolveTrialWaitingInstruction(false, 2));
        RenderTrialWaiting(cam, Path.Combine(outDir, "waiting_4of9_after.png"), "次: 4 / 9 本目の動画",
            ExperimentController.ResolveTrialWaitingInstruction(false, 0));

        const string oldContent = "モデルが自分から動く例を見る / Model でモデルを替える / Settings で動きの切り替え";
        RenderTutorialWaiting(cam, Path.Combine(outDir, "tutorial_waiting_C_before.png"), oldContent);
        RenderTutorialWaiting(cam, Path.Combine(outDir, "tutorial_waiting_C_after.png"),
            ExperimentController.DescribeTutorialContent(ExperimentDisplayMode.ModelReplaced));

        Debug.Log($"[PanelCapture] 出力: {Path.GetFullPath(outDir)}");
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void RenderTrialWaiting(Camera cam, string path, string nextLine, string instruction)
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
        panel.Show("待機中", $"参加者 ID: P01\n{nextLine}\n\n{instruction}", specs);
        Place(panel, cam);
        ApplyStates(new[] { true, true }, armConfirmIndex: -1);
        Render(cam, path);
        panel.Destroy();
    }

    // ExperimentController.ShowTutorialWaitingPanel と同じ組み立て（2・3 ブロック目の前なのでアンケートの案内つき）。
    private static void RenderTutorialWaiting(Camera cam, string path, string content)
    {
        ExperimentPanel panel = new ExperimentPanel(null, () => cam)
        {
            DistanceMeters = 1.2f,
            SizeMeters = new Vector2(1.05f, 0.82f),
            CurrentLayout = ExperimentPanel.DefaultLayout,
        };
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.CreateConfirm("練習を飛ばす", null),
            ExperimentPanel.ButtonSpec.Create("練習を開始", null),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", null),
        };
        string body =
            "参加者 ID: P01\n" +
            "次: 操作の練習 ― 次の 3 本（立体の動画 + 3D モデル）の前\n" +
            $"内容: {content}\n\n" +
            "実験者の合図でヘッドセットを外し、\nアンケートに答えてください。\n" +
            "戻ったら真ん中の「練習を開始」を押してください。" +
            "\n（光線を合わせて人差し指のトリガーを引くと押せます）";
        panel.Show("練習", body, specs);
        Place(panel, cam);
        ApplyStates(new[] { true, true, true }, armConfirmIndex: -1);
        Render(cam, path);
        panel.Destroy();
    }

    // 今の段階を済ませる操作を 1 つ流す（実機で出るのと同じ形の操作ログ）。
    private static void AdvanceTutorialForCapture(ExperimentTutorial tutorial)
    {
        switch (tutorial.CurrentStep)
        {
            case ExperimentTutorial.Step.WatchMotion:
                tutorial.RecordInteraction(1, "random_Static", "subject=animal");
                tutorial.RecordInteraction(1, "motion_end", "reason=completed source=random");
                break;
            case ExperimentTutorial.Step.ChangeModel:
                tutorial.RecordOperation("change_model", "track=0 category=human index=15 prefab=16_Male_Eric");
                break;
            case ExperimentTutorial.Step.ToggleMotion:
                tutorial.RecordOperation("motion_toggle", "value=0");
                break;
            default:
                // 掴んで回す。列挙子を名指ししないのは、段階を足す前のコードでも同じ撮り方ができるようにするため。
                tutorial.RecordOperation("change_rotation", "track=1 op=grab yaw=35 pitch=0 roll=0 frame=120");
                break;
        }
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
