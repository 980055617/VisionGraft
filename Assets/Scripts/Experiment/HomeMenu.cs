using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// 起動直後に出す入口。HomeScene に 1 つだけ置く。
//
// Build And Run はビルド設定の先頭シーンから始まるので、HomeScene を index 0 に置いて
// ここから「ピッカーで自由に見る」か「被験者実験を始める」かを選ぶ。
//
// 構成は ExperimentController に合わせてある。XR リグ・カメラは HomeScene が持ち、
// パネルは ExperimentPanel を使い回す（同じ ISDK レイ操作 prefab）。
[DisallowMultipleComponent]
public sealed class HomeMenu : MonoBehaviour
{
    [Header("Scenes")]
    public string viewerSceneName = "TestScene";
    public string experimentSceneName = "ExperimentScene";

    [Header("UI")]
    // StreamingStereoVideoPlayer の bundlePickerCanvasWithInteractionRayPrefab と同じものを割り当てる。
    // 未設定でも素の Canvas で動く（ExperimentPanel の挙動に準じる）。
    public GameObject panelCanvasWithInteractionRayPrefab;
    public float panelDistanceMeters = 1.2f;

    private ExperimentPanel panel;
    private Camera cachedCamera;
    private bool loading;

    private void Start()
    {
        panel = new ExperimentPanel(panelCanvasWithInteractionRayPrefab, ResolveCamera)
        {
            DistanceMeters = panelDistanceMeters,
        };

        ShowMenu();
    }


    private void OnDestroy()
    {
        if (panel != null)
        {
            panel.Destroy();
            panel = null;
        }
    }


    private void Update()
    {
        Camera cam = ResolveCamera();
        if (panel != null && cam != null)
        {
            panel.UpdatePlacement(cam.transform);
        }
    }


    private void ShowMenu()
    {
        panel.Show(
            "VisionGraft",
            "どちらを開きますか。\n\n" +
            "・自由に見る … bundle を選んで再生します\n" +
            "・被験者実験 … 参加者 ID と群を設定して開始します",
            new List<ExperimentPanel.ButtonSpec>
            {
                ExperimentPanel.ButtonSpec.Create("自由に見る", () => Load(viewerSceneName)),
                ExperimentPanel.ButtonSpec.Create("被験者実験", () => Load(experimentSceneName)),
            });
    }


    private void Load(string sceneName)
    {
        if (loading || string.IsNullOrEmpty(sceneName))
        {
            return;
        }

        // 二重ロードを防ぐ。VR のレイは 1 フレームに複数回クリックを飛ばすことがある。
        loading = true;

        // 前の実験の指示が残っていると、ピッカー経路で開いたのに実験の bundle が
        // 読み込まれる。入口に戻ってきた時点で必ず捨てる。
        ExperimentTrialHandoff.Clear();
        HomeLaunchHandoff.Clear();

        // ビューア経路はピッカーから始める。シーンに焼き込まれた
        // showBundlePickerOnStart は 0 なので、ここで実行時に要求する。
        if (sceneName == viewerSceneName)
        {
            HomeLaunchHandoff.RequestBundlePicker();
        }

        Debug.Log($"[Home] load scene: {sceneName}");
        StartCoroutine(LoadSceneRoutine(sceneName));
    }


    // **同期 LoadScene は使わない。** 押した瞬間にフレームが止まり、画面が固まったまま
    // 数秒待たされる（実機で「押しても反応しない」と報告された 2026-08-31）。
    // 先に「読み込み中」を出して 1 フレーム描かせてから、非同期で読み込む。
    private IEnumerator LoadSceneRoutine(string sceneName)
    {
        panel.Show(
            "読み込み中",
            sceneName == viewerSceneName
                ? "bundle ピッカーを準備しています…"
                : "実験シーンを準備しています…",
            new List<ExperimentPanel.ButtonSpec>());

        // Show した内容が実際に 1 枚描かれるまで待つ。1 フレームだと
        // Canvas の再構築が間に合わないことがあるので 2 フレーム置く。
        yield return null;
        yield return null;

        // どれだけ止まっているのかを測る。
        //
        // VR ではメインスレッドが止まっても頭の向きだけコンポジタが
        // 再投影するので、見回せるのにコントローラーだけが空中で固まる。
        // 「何秒固まったか」はフレーム間隔の最大値でしか分からない。
        var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        float worstFrameSeconds = 0f;
        int frames = 0;

        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        while (op != null && !op.isDone)
        {
            yield return null;
            frames++;
            if (Time.unscaledDeltaTime > worstFrameSeconds)
            {
                worstFrameSeconds = Time.unscaledDeltaTime;
            }
        }

        Debug.Log(
            $"[LOADTIME] {sceneName} 合計 {loadStopwatch.ElapsedMilliseconds}ms " +
            $"回ったフレーム {frames} " +
            $"最悪のフレーム {(worstFrameSeconds * 1000f):F0}ms");
    }


    private Camera ResolveCamera()
    {
        if (cachedCamera != null)
        {
            return cachedCamera;
        }

        cachedCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        return cachedCamera;
    }
}
