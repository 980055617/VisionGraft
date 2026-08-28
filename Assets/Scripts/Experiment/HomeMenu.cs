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
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
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
