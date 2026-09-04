using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// モデル編集タブ。「Change」パネルの 2 面目。
//
// **なぜ Settings から出したか。**
// Settings には系統の違う 2 種類が同居していた:
//   - 系全体の設定 … Motion / Screen Dist
//   - いま選んでいる対象の編集 … Track 選択 / Rot 0 / Scl 1 / Del / Scale / Keys
// 後者はモデルを選ぶ操作と地続きなので、モデルピッカーと同じパネルに置く
// （2026-09-04 ユーザー提案「設定に入れずにモデル編集という別のに入れる」）。
//
// **なぜ別ウィンドウにしなかったか。**
//   - 操作バーに 7 個目のボタンが入らない。canvas 440 に 76px のボタンが 3 行、
//     3 行目の下端が -213 で canvas の下端が -220。
//   - どちらの面も「どの track を触るか」を必要とする。対象の行を共有すれば
//     選択がずれない。**別ウィンドウにすると 2 つの選択状態が生まれる**
//     （実際 Settings 側は selectedManualRotationTrackId、ピッカー側は
//     runtimeModelPickerTrackId と別々に持っていた）。
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private const int ModelPickerTabModels = 0;
    private const int ModelPickerTabEdit = 1;

    private int runtimeModelPickerTab = ModelPickerTabModels;
    private Button runtimeModelPickerModelTabButton;
    private Button runtimeModelPickerEditTabButton;
    private readonly List<GameObject> runtimeModelPickerModelTabObjects = new List<GameObject>();
    private readonly List<GameObject> runtimeModelPickerEditTabObjects = new List<GameObject>();

    private Text runtimeTrackRotationValueText;
    private Button runtimeTrackRotationResetButton;
    private Button runtimeTrackScaleResetButtonRef;
    private Button runtimeTrackKeyDeleteButtonRef;
    private Button runtimeTrackKeyPrevButton;
    private Button runtimeTrackKeyNextButton;

    // 前後送りで飛べるキーのフレーム。無ければ -1。ラベルにそのまま出す。
    private int runtimeTrackPrevKeyFrame = -1;
    private int runtimeTrackNextKeyFrame = -1;
    private readonly List<SortedDictionary<int, float>> keyNavigationCurves =
        new List<SortedDictionary<int, float>>();

    // 編集タブの行。canvas は 980x660 で原点は中心（上端 330 / 下端 -330）。
    private const float ModelEditRotationRowY = 110f;
    private const float ModelEditResetRowY = 48f;
    private const float ModelEditScaleRowY = -30f;
    private const float ModelEditKeyInfoRowY = -104f;
    private const float ModelEditKeyNavRowY = -176f;
    private const float ModelEditHintRowY = -246f;


    private void RegisterModelPickerTabObject(int tab, GameObject obj)
    {
        if (obj == null)
        {
            return;
        }

        if (tab == ModelPickerTabEdit)
        {
            runtimeModelPickerEditTabObjects.Add(obj);
        }
        else
        {
            runtimeModelPickerModelTabObjects.Add(obj);
        }
    }


    private void SetRuntimeModelPickerTab(int tab)
    {
        if (runtimeModelPickerTab == tab)
        {
            return;
        }

        runtimeModelPickerTab = tab;
        ApplyRuntimeModelPickerTabVisibility();

        // 編集タブに来た時点で、いま選んでいる対象の値を出す。
        // 開いた直後に「x1.00 / Keys 0」と出てから正しい値に化けるのを避ける。
        if (tab == ModelPickerTabEdit)
        {
            UpdateRuntimeTrackRotationUiState();
        }
        else
        {
            InvalidateRuntimeModelPickerPreviews();
        }

        ExperimentLog.Operation("model_panel_tab", tab == ModelPickerTabEdit ? "edit" : "models");
    }


    private void ApplyRuntimeModelPickerTabVisibility()
    {
        bool edit = runtimeModelPickerTab == ModelPickerTabEdit;

        for (int i = 0; i < runtimeModelPickerModelTabObjects.Count; i++)
        {
            GameObject obj = runtimeModelPickerModelTabObjects[i];
            if (obj != null)
            {
                SceneObjectWriter.ApplyActive(obj, !edit);
            }
        }

        for (int i = 0; i < runtimeModelPickerEditTabObjects.Count; i++)
        {
            GameObject obj = runtimeModelPickerEditTabObjects[i];
            if (obj != null)
            {
                SceneObjectWriter.ApplyActive(obj, edit);
            }
        }

        // モデルタブのプレビューは 3D の実体なので、隠すときは消す。
        // 残したままだとパネルの手前に浮いたまま編集タブに被る。
        if (edit)
        {
            ClearRuntimeModelPickerPreviews();
        }

        ApplyModelPickerTabHighlight(runtimeModelPickerModelTabButton, !edit);
        ApplyModelPickerTabHighlight(runtimeModelPickerEditTabButton, edit);
    }


    private static void ApplyModelPickerTabHighlight(Button button, bool active)
    {
        if (button == null || !(button.targetGraphic is Image image))
        {
            return;
        }

        UiComponentWriter.ApplyGraphicColor(
            image,
            active
                ? new Color(0.16f, 0.42f, 0.66f, 0.96f)
                : new Color(0.13f, 0.14f, 0.15f, 0.92f));
    }


    private void BuildRuntimeModelEditTab(Transform parent)
    {
        runtimeModelPickerEditTabObjects.Clear();

        runtimeTrackRotationValueText = CreateModelPickerText(
            parent,
            "TrackRotationValue",
            "回転  yaw 0.0  pitch 0.0  roll 0.0",
            new Vector2(0f, ModelEditRotationRowY),
            new Vector2(900f, 48f),
            30,
            TextAnchor.MiddleCenter,
            Color.white);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackRotationValueText.gameObject);

        runtimeTrackRotationResetButton = CreateModelPickerButton(
            parent,
            "TrackYawResetButton",
            "回転をリセット",
            new Vector2(-160f, ModelEditResetRowY),
            new Vector2(300f, 56f),
            OnRuntimeTrackYawResetClicked,
            TextAnchor.MiddleCenter);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackRotationResetButton.gameObject);

        runtimeTrackScaleResetButtonRef = CreateModelPickerButton(
            parent,
            "TrackScaleResetButton",
            "大きさをリセット",
            new Vector2(160f, ModelEditResetRowY),
            new Vector2(300f, 56f),
            OnRuntimeTrackScaleResetClicked,
            TextAnchor.MiddleCenter);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackScaleResetButtonRef.gameObject);

        // Scale は自動フィット（bbox 高さ合わせ）に対する**倍率**。1.0 が「自動のまま」。
        Text scaleLabel = CreateModelPickerText(
            parent,
            "ScaleLabel",
            "大きさ",
            new Vector2(-380f, ModelEditScaleRowY),
            new Vector2(180f, 48f),
            30,
            TextAnchor.MiddleLeft,
            Color.white);
        RegisterModelPickerTabObject(ModelPickerTabEdit, scaleLabel.gameObject);

        runtimeTrackScaleValueText = CreateModelPickerText(
            parent,
            "ScaleValue",
            "x1.00",
            new Vector2(380f, ModelEditScaleRowY),
            new Vector2(160f, 48f),
            30,
            TextAnchor.MiddleRight,
            Color.white);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackScaleValueText.gameObject);

        // 操作列は Settings と同じ (-180..240)。canvas が 80px 広いだけで列は共通に収まる。
        runtimeTrackScaleSlider = CreateSlider(parent, "TrackScaleSlider", ModelEditScaleRowY);
        if (runtimeTrackScaleSlider != null)
        {
            UiComponentWriter.ApplySliderRange(runtimeTrackScaleSlider, ManualScaleMin, ManualScaleMax);
            UiComponentWriter.ApplySliderValueWithoutNotify(runtimeTrackScaleSlider, ManualScaleDefault);
            BindRuntimeSlider(runtimeTrackScaleSlider, OnRuntimeTrackScaleSliderChanged);
            RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackScaleSlider.gameObject);
        }

        runtimeTrackKeyInfoText = CreateModelPickerText(
            parent,
            "TrackKeyInfo",
            "キー 回転:0 大きさ:0",
            new Vector2(0f, ModelEditKeyInfoRowY),
            new Vector2(900f, 44f),
            28,
            TextAnchor.MiddleCenter,
            new Color(0.9f, 0.95f, 1f, 1f));
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackKeyInfoText.gameObject);

        // **キーの前後送り。**
        // Del だけだと「現在フレームちょうどに止めないと消せない」。bundle_train は
        // 1830 フレーム、bundle_human は 2167 フレームで、シークバーは数百 px しかない。
        // 目的のフレームに手で合わせるのは実質不可能だという指摘（2026-09-04）。
        //
        // 一覧ではなく前後送りにしたのは、VR で小さい的を並べる設計が破綻するため。
        // 飛び先のフレーム番号をボタンに出せば、一覧の「どこにキーがあるか分かる」も概ね賄える。
        runtimeTrackKeyPrevButton = CreateModelPickerButton(
            parent,
            "TrackKeyPrevButton",
            "< ―",
            new Vector2(-330f, ModelEditKeyNavRowY),
            new Vector2(240f, 60f),
            OnRuntimeTrackKeyPrevClicked,
            TextAnchor.MiddleCenter);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackKeyPrevButton.gameObject);

        runtimeTrackKeyDeleteButtonRef = CreateModelPickerButton(
            parent,
            "TrackKeyDeleteButton",
            "この位置のキーを消す",
            new Vector2(0f, ModelEditKeyNavRowY),
            new Vector2(380f, 60f),
            OnRuntimeTrackKeyDeleteClicked,
            TextAnchor.MiddleCenter);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackKeyDeleteButtonRef.gameObject);

        runtimeTrackKeyNextButton = CreateModelPickerButton(
            parent,
            "TrackKeyNextButton",
            "― >",
            new Vector2(330f, ModelEditKeyNavRowY),
            new Vector2(240f, 60f),
            OnRuntimeTrackKeyNextClicked,
            TextAnchor.MiddleCenter);
        RegisterModelPickerTabObject(ModelPickerTabEdit, runtimeTrackKeyNextButton.gameObject);

        Text keyHint = CreateModelPickerText(
            parent,
            "TrackKeyHint",
            "対象を掴んで回すと現在フレームにキーが入ります。キーとキーの間は自動で補間されます。",
            new Vector2(0f, ModelEditHintRowY),
            new Vector2(900f, 44f),
            22,
            TextAnchor.MiddleCenter,
            new Color(0.75f, 0.8f, 0.85f, 1f));
        RegisterModelPickerTabObject(ModelPickerTabEdit, keyHint.gameObject);
    }


    private void OnRuntimeTrackKeyPrevClicked()
    {
        SeekToTrackKeyFrame(runtimeTrackPrevKeyFrame);
    }


    private void OnRuntimeTrackKeyNextClicked()
    {
        SeekToTrackKeyFrame(runtimeTrackNextKeyFrame);
    }


    private bool batchSeekTestDone;
    private float batchSeekTestVerifyAtRealtime = -1f;
    private float batchSeekTestStartRealtime = -1f;
    private int batchSeekTestVerifyCount;

    // フレーム直指定シークが実際に効くかを実動画で確かめる。
    private void RunBatchSeekTestIfRequested()
    {
        // **batchmode でしか走らせない。**
        // batchSeekTestFrame は public なのでシーンの serialize 値が優先され、
        // 何かの拍子で 0 になると 0 >= 0 で成立してしまう。
        // そのとき実機で「開始直後にフレーム 0 へ飛んで止まる」。
        // 検証用の仕掛けは、検証の場でしか動かないようにする。
        if (!Application.isBatchMode)
        {
            return;
        }

        if (batchSeekTestFrame < 0 || batchSeekTestDone || vp == null || !vp.isPrepared || !metaLoaded)
        {
            return;
        }

        // **vp.frame で待たない。**
        // ピッカーを開けば再生は止まるので、frame は 0 付近で固まる。
        // 「vp.frame >= 30 になったら」で待つと永遠に発火しない。
        // 実時間で待って、**実際の操作と同じ「既に止まっている」状態**で試す。
        if (batchSeekTestStartRealtime < 0f)
        {
            batchSeekTestStartRealtime = Time.realtimeSinceStartup;
            return;
        }

        float elapsed = Time.realtimeSinceStartup - batchSeekTestStartRealtime;
        if (elapsed < 3f)
        {
            return;
        }

        // **先に確実に止めてから試す。**
        // 実際の操作ではパネルを開いている間は再生が止まっており、
        // 「既に止まっているプレーヤーに time を書く」のが毎回の条件になる。
        // 再生中に書いて効いても、それは本番の条件を試していない。
        if (vp.isPlaying)
        {
            vp.Pause();
            Debug.Log($"[SEEKTEST] 先に停止して 1 秒待ちます frame={vp.frame}");
            batchSeekTestStartRealtime = Time.realtimeSinceStartup - 2f;
            return;
        }

        batchSeekTestDone = true;
        int before = GetCurrentPlaybackFrame();
        Debug.Log($"[SEEKTEST] 移動前の track: {DescribeAvailableTracksForLog()}");
        Debug.Log(
            $"[SEEKTEST] 開始 目標={batchSeekTestFrame} 直前={before} " +
            $"isPlaying={vp.isPlaying} ピッカー開={runtimeModelPickerOpen} " +
            $"タブ={(runtimeModelPickerTab == ModelPickerTabEdit ? "編集" : "モデル")}");

        SeekToTrackKeyFrame(batchSeekTestFrame);

        // **シークは非同期。** 書いた直後の vp.frame はまだ古い値を返すので、
        // ここだけを見て「効かない」と判断してはいけない。少し後でもう一度見る。
        Debug.Log($"[SEEKTEST] 目標={batchSeekTestFrame} 直前={before} 直後={GetCurrentPlaybackFrame()} vp.frame={vp.frame}");
        batchSeekTestVerifyAtRealtime = Time.realtimeSinceStartup + 1f;
    }


    private string DescribeAvailableTracksForLog()
    {
        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        var sb = new System.Text.StringBuilder();
        sb.Append("一覧=[");
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(ids[i]);
        }

        sb.Append("] ボタン数=").Append(runtimeModelPickerTargetButtons.Count);
        sb.Append(" picker=").Append(runtimeModelPickerTrackId);
        sb.Append(" 編集=").Append(selectedManualRotationTrackId);
        sb.Append(" 選択済=").Append(runtimeModelPickerPreferredTrackId);
        return sb.ToString();
    }


    private void VerifyBatchSeekTestIfDue()
    {
        if (batchSeekTestVerifyAtRealtime < 0f || Time.realtimeSinceStartup < batchSeekTestVerifyAtRealtime)
        {
            return;
        }

        batchSeekTestVerifyCount++;
        batchSeekTestVerifyAtRealtime = batchSeekTestVerifyCount < 5
            ? Time.realtimeSinceStartup + 1f
            : -1f;
        int landed = GetCurrentPlaybackFrame();
        Debug.Log($"[SEEKTEST] 移動後の track: {DescribeAvailableTracksForLog()}");
        bool ok = Mathf.Abs(landed - batchSeekTestFrame) <= 2;
        Debug.Log(
            $"[SEEKTEST] #{batchSeekTestVerifyCount}: 現在={landed} vp.frame={(vp != null ? vp.frame : -1L)} " +
            $"vp.time={(vp != null ? vp.time : 0d):F3} isPlaying={(vp != null && vp.isPlaying)} " +
            $"目標={batchSeekTestFrame} 判定={(ok ? "到達" : "未到達")}");
    }


    // キーのフレーム番号を秒に直すための fps。
    // 進捗バーと同じ ResolveSeekFps を使う。ここだけ別の fps を使うと、
    // バーの位置とキーの位置がじわじわずれる。
    private float ResolveSeekFpsForKeyNavigation()
    {
        float manifestFps = manifest != null ? manifest.fps : 0f;
        return RuntimePlaybackTimeline.ResolveSeekFps(
            metaHeader.fps, manifestFps, vp != null ? vp.frameRate : 0d);
    }


    private void SeekToTrackKeyFrame(int frame)
    {
        if (isNormalMode || frame < 0 || vp == null)
        {
            return;
        }

        bool wasPlaying = vp.isPlaying;

        // **秒で飛ばす。** 進捗バーが使っているのと同じ経路。
        //
        // シークは**非同期**で、完了には実時間で 1 秒弱かかる。
        // 書いた直後の vp.time / vp.frame は古い値のままなので、
        // それを見て「効かない」と判断してはいけない
        // （batchmode の Update は実時間を待たないので、Update 回数で待っても進まない）。
        //
        // **半フレーム分足す。** 秒 → frame は floor なので、frame/fps ちょうどだと
        // 丸め誤差で 1 手前に落ち得る。キーの上に乗らないと Del が押せなくなり、
        // 前後送りを足した意味が消える。フレームの真ん中を狙う。
        float fps = ResolveSeekFpsForKeyNavigation();
        if (fps <= 0.001f)
        {
            Debug.Log($"[KEYNAV] fps が分からないので移動できません frame={frame}");
            return;
        }

        double seconds = (frame + 0.5d) / fps;
        RuntimePlaybackController.ApplySeekTarget(
            vp, new RuntimePlaybackTimeline.SeekTarget(true, seconds, false, 0L));

        // 止めるのはシークの後。この順序自体は必須ではない——
        // 停止済みのプレーヤーに書いてもシークは効く（実測 2026-09-04:
        // isPlaying=False で目標 900 に対し 900 へ到達）。先に飛ばす方が
        // 「飛んでから止まる」と順番が素直なのでこちらにしてある。
        PauseForManualRotationEdit();

        UpdateRuntimeProgressUi();
        UpdateRuntimeTrackRotationUiState();
        Debug.Log(
            $"[KEYNAV] frame={frame} へ移動 秒={seconds:F3} fps={fps:F3} " +
            $"canSetTime={vp.canSetTime} 再生中だった={wasPlaying} " +
            $"vp.frame={vp.frame} 現在={GetCurrentPlaybackFrame()}");
        ExperimentLog.Operation("seek_key", $"frame={frame}");
    }


    // 探索自体は TrackKeyNavigation。ここは track の 4 本を集めて渡すだけ。
    private void RefreshTrackKeyNavigationTargets(uint trackId, int currentFrame)
    {
        keyNavigationCurves.Clear();
        AddKeyNavigationCurve(manualYawKeyframesByTrack, trackId);
        AddKeyNavigationCurve(manualPitchKeyframesByTrack, trackId);
        AddKeyNavigationCurve(manualRollKeyframesByTrack, trackId);
        AddKeyNavigationCurve(manualScaleKeyframesByTrack, trackId);

        TrackKeyNavigation.FindNeighbors(
            keyNavigationCurves,
            currentFrame,
            out runtimeTrackPrevKeyFrame,
            out runtimeTrackNextKeyFrame);
    }


    private void AddKeyNavigationCurve(
        Dictionary<uint, SortedDictionary<int, float>> source,
        uint trackId)
    {
        if (source != null && source.TryGetValue(trackId, out SortedDictionary<int, float> keys) && keys != null)
        {
            keyNavigationCurves.Add(keys);
        }
    }


    // 前後送りのボタンに飛び先のフレーム番号を出す。無ければ押せなくする。
    private void ApplyTrackKeyNavigationButtons()
    {
        ApplyKeyNavButton(runtimeTrackKeyPrevButton, runtimeTrackPrevKeyFrame, true);
        ApplyKeyNavButton(runtimeTrackKeyNextButton, runtimeTrackNextKeyFrame, false);
    }


    private void ApplyKeyNavButton(Button button, int frame, bool isPrev)
    {
        if (button == null)
        {
            return;
        }

        bool enabled = !isNormalMode && frame >= 0;
        UiComponentWriter.ApplyInteractable(button, enabled);

        Text label = button.GetComponentInChildren<Text>(true);
        if (label == null)
        {
            return;
        }

        // ◀ ▶ は端末のフォントに無いと豆腐になる。既存の "< Prev" と同じ ASCII にする。
        string body = enabled ? frame.ToString() : "―";
        UiComponentWriter.ApplyTextContent(label, isPrev ? "< " + body : body + " >");
    }
}
