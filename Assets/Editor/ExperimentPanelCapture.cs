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
        // 音の確認（2026-11-03、セッション開始の直後）。
        CaptureAudioCheck(cam, outDir);
        // 確認の窓を閉じた後（いいえ・はい・画面が替わる・同じ画面の書き換え）の状態を、実際のハンドラで確かめてログに出す。
        VerifyConfirmDialogFlow(cam, outDir);

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
        ApplyStates(new[] { false, true });
        Render(cam, Path.Combine(outDir, "trialbar_1_before70s.png"));

        // (2) 70 秒後: 両方押せる
        ApplyStates(new[] { true, true });
        Render(cam, Path.Combine(outDir, "trialbar_2_after70s.png"));

        // (3) 「実験者用: 中止」を押した後の確認の窓（2026-11-03 まではここで色が変わるだけの 2 度押しだった）
        OpenConfirmDialogForCapture(panel, cam, 1);
        Render(cam, Path.Combine(outDir, "trialbar_3_confirm_dialog.png"));

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
        ApplyStates(new[] { false, false });
        Render(cam, Path.Combine(outDir, "waiting_1_armdelay.png"));

        // (5) 通常
        ApplyStates(new[] { true, true });
        Render(cam, Path.Combine(outDir, "waiting_2_normal.png"));

        // (6) 「中止して Home へ」を押した後の確認の窓
        OpenConfirmDialogForCapture(panel, cam, 1);
        Render(cam, Path.Combine(outDir, "waiting_3_confirm_dialog.png"));

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
            ApplyStates(interactable);
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
        ApplyStates(new[] { true, true });
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
        ApplyStates(new[] { true, true, true });
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

    // ExperimentController の音の確認（AudioCheck.cs）と同じ組み立てで、段階ごとに撮る。
    private static void CaptureAudioCheck(Camera cam, string outDir)
    {
        ExperimentPanel.ButtonSpec abort = ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", null);
        RenderAudioCheck(cam, Path.Combine(outDir, "audio_1_ready.png"), ExperimentController.AudioCheckStage.Ready,
            new List<ExperimentPanel.ButtonSpec> { ExperimentPanel.ButtonSpec.Create(ExperimentController.AudioCheckPlayLabel, null), abort },
            new[] { true, true });
        // 鳴り始めて 1 秒たつまで「聞こえた」は押せない。
        RenderAudioCheck(cam, Path.Combine(outDir, "audio_2_playing_wait.png"), ExperimentController.AudioCheckStage.Playing,
            new List<ExperimentPanel.ButtonSpec> { ExperimentPanel.ButtonSpec.Create(ExperimentController.AudioCheckHeardLabel, null, false), abort },
            new[] { false, true });
        RenderAudioCheck(cam, Path.Combine(outDir, "audio_3_playing.png"), ExperimentController.AudioCheckStage.Playing,
            new List<ExperimentPanel.ButtonSpec> { ExperimentPanel.ButtonSpec.Create(ExperimentController.AudioCheckHeardLabel, null), abort },
            new[] { true, true });
        RenderAudioCheck(cam, Path.Combine(outDir, "audio_4_failed.png"), ExperimentController.AudioCheckStage.Failed,
            new List<ExperimentPanel.ButtonSpec>
            {
                ExperimentPanel.ButtonSpec.Create(ExperimentController.AudioCheckRetryLabel, null),
                ExperimentPanel.ButtonSpec.CreateConfirm(ExperimentController.AudioCheckSkipLabel, null),
                abort,
            },
            new[] { true, true, true });
    }

    private static void RenderAudioCheck(Camera cam, string path, ExperimentController.AudioCheckStage stage,
        List<ExperimentPanel.ButtonSpec> specs, bool[] interactable)
    {
        ExperimentPanel panel = new ExperimentPanel(null, () => cam)
        {
            DistanceMeters = 1.2f,
            SizeMeters = new Vector2(1.05f, 0.82f),
            CurrentLayout = ExperimentPanel.DefaultLayout,
        };
        panel.Show(ExperimentController.AudioCheckTitle, ExperimentController.BuildAudioCheckBody(stage), specs);
        Place(panel, cam);
        ApplyStates(interactable);
        Render(cam, path);
        panel.Destroy();
    }

    // 確認の窓の 4 つの閉じ方。結果は [PanelCapture] confirm ... の行（OK / NG）に出す。
    private static void VerifyConfirmDialogFlow(Camera cam, string outDir)
    {
        int actionRuns = 0;
        List<string> logged = new List<string>();
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create("この動画を開始", null),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", () => actionRuns++),
        };
        ExperimentPanel panel = new ExperimentPanel(null, () => cam)
        {
            DistanceMeters = 1.2f,
            SizeMeters = new Vector2(1.05f, 0.82f),
            CurrentLayout = ExperimentPanel.DefaultLayout,
            OperationLogger = (action, detail) => logged.Add($"{action} {detail}"),
        };
        panel.Show("待機中", "確かめ用", specs);
        Place(panel, cam);
        GameObject mainRoot = ExperimentPanel.ActiveRoot;

        // (a) いいえ: 実行しない・元のパネルが戻る
        OpenConfirmDialogForCapture(panel, cam, 1);
        bool openedHidesMain = mainRoot != null && !mainRoot.activeSelf && ExperimentPanel.ActiveRoot != mainRoot;
        ClickDialogButton(0);
        Place(panel, cam);
        bool noOk = openedHidesMain && actionRuns == 0 && mainRoot.activeSelf && ExperimentPanel.ActiveRoot == mainRoot;
        Debug.Log($"[PanelCapture] confirm no: {(noOk ? "OK" : "NG")} hidMain={openedHidesMain} runs={actionRuns} mainActive={mainRoot.activeSelf} activeIsMain={ExperimentPanel.ActiveRoot == mainRoot}");
        ApplyStates(new[] { true, true });
        Render(cam, Path.Combine(outDir, "waiting_4_after_no.png"));

        // (b) はい: 1 回だけ実行・元のパネルが戻る
        OpenConfirmDialogForCapture(panel, cam, 1);
        ClickDialogButton(1);
        bool yesOk = actionRuns == 1 && mainRoot.activeSelf && ExperimentPanel.ActiveRoot == mainRoot;
        Debug.Log($"[PanelCapture] confirm yes: {(yesOk ? "OK" : "NG")} runs={actionRuns} mainActive={mainRoot.activeSelf}");

        // (c) 同じ画面の書き換え（keepPlacement）: 窓は残り、元のパネルは隠れたまま。その後のいいえで戻る
        OpenConfirmDialogForCapture(panel, cam, 1);
        GameObject dialogRoot = ExperimentPanel.ActiveRoot;
        panel.Show("待機中", "書き換えた本文", specs, true);
        bool keepOk = ExperimentPanel.ActiveRoot == dialogRoot && dialogRoot != null && dialogRoot.activeSelf && !mainRoot.activeSelf;
        ClickDialogButton(0);
        keepOk = keepOk && mainRoot.activeSelf && actionRuns == 1;
        Debug.Log($"[PanelCapture] confirm keepPlacement: {(keepOk ? "OK" : "NG")} runs={actionRuns} mainActive={mainRoot.activeSelf}");

        // (d) 画面が替わった（keepPlacement でない Show）: 窓は実行せずに閉じ、新しい中身が出る
        OpenConfirmDialogForCapture(panel, cam, 1);
        dialogRoot = ExperimentPanel.ActiveRoot;
        panel.Show("次の画面", "替わった", specs);
        bool dismissOk = actionRuns == 1 && mainRoot.activeSelf && ExperimentPanel.ActiveRoot == mainRoot && dialogRoot != null && !dialogRoot.activeSelf;
        Debug.Log($"[PanelCapture] confirm dismiss: {(dismissOk ? "OK" : "NG")} runs={actionRuns} mainActive={mainRoot.activeSelf} dialogActive={(dialogRoot != null && dialogRoot.activeSelf)}");

        Debug.Log($"[PanelCapture] confirm log: {string.Join(" | ", logged)}");
        panel.Destroy();
    }

    // 表示中の確認の窓のボタンを実際のハンドラで押す（0 = いいえ、1 = はい）。
    private static void ClickDialogButton(int index)
    {
        GameObject root = ExperimentPanel.ActiveRoot;
        Button[] buttons = root != null ? root.GetComponentsInChildren<Button>(true) : new Button[0];
        if (index >= 0 && index < buttons.Length)
        {
            buttons[index].onClick.Invoke();
        }
    }

    // 実験者用のボタンを実際のハンドラで押し、出た確認の窓（ExperimentPanel.OpenConfirmDialog）を置いて押せる状態にする。
    private static void OpenConfirmDialogForCapture(ExperimentPanel panel, Camera cam, int buttonIndex)
    {
        GameObject root = ExperimentPanel.ActiveRoot;
        Button[] buttons = root != null ? root.GetComponentsInChildren<Button>(true) : new Button[0];
        if (buttonIndex >= 0 && buttonIndex < buttons.Length)
        {
            buttons[buttonIndex].onClick.Invoke();
        }

        // 窓は元のパネルの UpdatePlacement から置かれる。「いいえ」「はい」は作った直後は押せないので、押せる状態で撮る。
        Place(panel, cam);
        ApplyStates(new[] { true, true });
    }

    // interactable を、実際のコード経路を通して反映させる。
    private static void ApplyStates(bool[] interactable)
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
