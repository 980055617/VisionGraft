using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// モデル prefab を「要るものから」読む（2026-11-02）。
//
// それまでは試行・視聴のたびに model_index.txt の 104 個を全部読み終えてから bundle を開いていた。被験者実験では
// 試行ごとに 7.5〜10.6 秒（10/29 の実機ログ）。1 試行で使うのは 1〜3 個で、単眼・立体の条件は 0 個
// （Docs/model-selection-persistence.md「被験者実験では毎回 7.5〜10.6 秒待っている」）。
//   - 一覧（humanPrefabs など）の長さと並びは従来と同じ。名前は model_index.txt から先に全部埋め、中身（prefab）は空の枠で始める。
//     **index の意味（trackModelIndices・selectedHumanIndex・保存した選択・ログの index=）を変えないため**
//   - 名前で探すところ（保存した選択・実験の既定1/既定2・練習の犬・Model の窓の名前）は名前の一覧を見る。枠が空でも名前は引ける
//   - bundle を開いたら、この動画の track が使うモデル（＋実験の既定1・既定2）だけを読んでから再生する（LoadModelPrefabsNeededForBundle）。
//     normal mode（実験の単眼・立体）はモデルを出さないので何も読まない
//   - 自由に見るときは、再生が始まってから残りを 1 個ずつ裏で読む（Model の窓のため。ユーザー「通常の視聴における model windows は
//     再生中にゆっくり読み込みたい」）。実験の試行は窓を使わない（置換ありは既定1 ⇄ 既定2 の切り替え）ので読まない
//   - 枠が空のまま要るとき（Model の窓でまだ読んでいないモデルを押した等）は、その 1 個を同期で読む（GetOrLoadModelPrefab）。
//     実験の試行でこれが出たら、先に読む一覧の漏れ（[LOADTIME] 同期で読んだ … が警告で出る）
//   - バッチは従来どおり全部読んでから始める（ほかの検証の撮影を変えないため）。新しい経路は batchLazyModelPrefabLoad で試す
public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // バッチ検証専用（batchmode のときだけ効く）。true でバッチも実機と同じ「要るものから読む」経路にする（-setFields で入れる）。
    public bool batchLazyModelPrefabLoad = false;

    // 再生の準備ができてから裏の読み込みを始めるまでの秒数。動画のデコーダの立ち上がりと重ねない。
    private const float ModelBackgroundLoadStartDelaySeconds = 2f;

    private string[] humanPrefabNames;
    private string[] animalPrefabNames;
    private string[] elsePrefabNames;
    // Resources.Load に渡すパス（model_index.txt の行）。一覧が無く LoadAll に落ちたときは null（全部読み済み）。
    private string[] humanPrefabPaths;
    private string[] animalPrefabPaths;
    private string[] elsePrefabPaths;
    // 読めなかったパス。毎 tick 読み直さない。
    private readonly HashSet<string> modelPrefabLoadFailedPaths = new HashSet<string>();

    private readonly HashSet<Material> cutoutAlphaDoneMaterials = new HashSet<Material>();
    private int cutoutAlphaChangedCount;

    // LoadModelPrefabsNeededForBundle の最中。DisplayModelTick はこの間モデルを置かない。
    private bool modelPrefabsNeededLoading;
    private bool modelBackgroundLoadStarted;
    private bool modelBackgroundLoadRunning;
    private ThreadPriority modelBackgroundLoadSavedPriority;

    private static readonly string[] ModelPrefabCategories = { "Human", "Animal", "Else" };

    private bool LoadsAllModelPrefabsUpFront
    {
        get { return Application.isBatchMode && !batchLazyModelPrefabLoad; }
    }


    // 名前と空の枠を作る。並びは従来の Resources.LoadAll と同じ（名前の順、animal は AnimalModelPriorityOrder が先）。
    // TextAsset 1 個を読むだけなので Start で同期で呼んでよい。
    private void BuildModelPrefabSlots()
    {
        TextAsset index = Resources.Load<TextAsset>(ModelIndexResourcePath);
        if (index == null || string.IsNullOrEmpty(index.text))
        {
            // 一覧が無いときは従来どおり同期で全部読む。遅いが壊れはしない。
            Debug.LogWarning(
                $"[Model] {ModelIndexResourcePath} が無いので同期読み込みに落ちます。" +
                "Tools/VisionGraft/モデル一覧を作り直す で作れます。");
            humanPrefabs  = LoadPrefabsFromResources("Models/Human");
            animalPrefabs = SortByPriority(LoadPrefabsFromResources("Models/Animal"), AnimalModelPriorityOrder);
            elsePrefabs   = LoadPrefabsFromResources("Models/Else");
            humanPrefabNames = NamesOfPrefabs(humanPrefabs);
            animalPrefabNames = NamesOfPrefabs(animalPrefabs);
            elsePrefabNames = NamesOfPrefabs(elsePrefabs);
            humanPrefabPaths = null;
            animalPrefabPaths = null;
            elsePrefabPaths = null;
            ApplyCutoutAlphaToModelPrefabs("Human", humanPrefabs);
            ApplyCutoutAlphaToModelPrefabs("Animal", animalPrefabs);
            ApplyCutoutAlphaToModelPrefabs("Else", elsePrefabs);
            return;
        }

        var human = new List<string>();
        var animal = new List<string>();
        var other = new List<string>();
        string[] lines = index.text.Split(ModelIndexLineSeparators, System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string path = lines[i].Trim();
            if (path.Length == 0)
            {
                continue;
            }

            if (path.StartsWith("Models/Human", System.StringComparison.Ordinal))
            {
                human.Add(path);
            }
            else if (path.StartsWith("Models/Animal", System.StringComparison.Ordinal))
            {
                animal.Add(path);
            }
            else
            {
                other.Add(path);
            }
        }

        // 並びは従来と同じにする。ずれると selectedHumanIndex や trackModelIndices が指すモデルが変わってしまう。
        // prefab の名前 = ファイル名（パスの最後）。読んだときに StoreModelPrefab が突き合わせる。
        human.Sort((a, b) => string.CompareOrdinal(ModelNameOfPath(a), ModelNameOfPath(b)));
        other.Sort((a, b) => string.CompareOrdinal(ModelNameOfPath(a), ModelNameOfPath(b)));
        animal.Sort((a, b) => CompareByPriority(ModelNameOfPath(a), ModelNameOfPath(b), AnimalModelPriorityOrder));

        humanPrefabPaths = human.ToArray();
        animalPrefabPaths = animal.ToArray();
        elsePrefabPaths = other.ToArray();
        humanPrefabNames = NamesOfPaths(humanPrefabPaths);
        animalPrefabNames = NamesOfPaths(animalPrefabPaths);
        elsePrefabNames = NamesOfPaths(elsePrefabPaths);
        humanPrefabs = new GameObject[humanPrefabPaths.Length];
        animalPrefabs = new GameObject[animalPrefabPaths.Length];
        elsePrefabs = new GameObject[elsePrefabPaths.Length];
        Debug.Log(
            $"[Model] 一覧 Human: {humanPrefabs.Length} / Animal: {animalPrefabs.Length} / Else: {elsePrefabs.Length}" +
            $"（{(LoadsAllModelPrefabsUpFront ? "全部読んでから始める" : "中身は要るものから読む")}）");
    }


    // 要るものから読む経路の Start 側の仕上げ。全部読む経路は LoadModelPrefabsAsync の終わりで同じことをする。
    private void FinishLazyModelPrefabSlots()
    {
        AppendBatchExtraPrefabs();
        modelPrefabsReady = true;
    }


    private static string ModelNameOfPath(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }


    private static string[] NamesOfPaths(string[] paths)
    {
        var names = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            names[i] = ModelNameOfPath(paths[i]);
        }

        return names;
    }


    private static string[] NamesOfPrefabs(GameObject[] prefabs)
    {
        var names = new string[prefabs.Length];
        for (int i = 0; i < prefabs.Length; i++)
        {
            names[i] = prefabs[i] != null ? prefabs[i].name : null;
        }

        return names;
    }


    private GameObject[] ResolvePrefabsForModelCategory(string category)
    {
        return category == "Animal" ? animalPrefabs : category == "Else" ? elsePrefabs : humanPrefabs;
    }


    private string[] ResolvePrefabNamesForModelCategory(string category)
    {
        return category == "Animal" ? animalPrefabNames : category == "Else" ? elsePrefabNames : humanPrefabNames;
    }


    private string[] ResolvePrefabPathsForModelCategory(string category)
    {
        return category == "Animal" ? animalPrefabPaths : category == "Else" ? elsePrefabPaths : humanPrefabPaths;
    }


    private string[] ResolvePrefabNamesForCategory(byte categoryId)
    {
        return ResolvePrefabNamesForModelCategory(ResolveRuntimeModelPickerCategoryLabel(categoryId));
    }


    private static int IndexOfModelName(string[] names, string name, bool ignoreCase)
    {
        if (names == null || string.IsNullOrEmpty(name))
        {
            return -1;
        }

        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, ignoreCase ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }


    // 一覧の名前（枠が空でも引ける）。範囲外は null。
    private static string ModelNameAt(string[] names, int index)
    {
        return names != null && index >= 0 && index < names.Length ? names[index] : null;
    }


    private bool IsModelSlotPending(string category, int index)
    {
        GameObject[] prefabs = ResolvePrefabsForModelCategory(category);
        string[] paths = ResolvePrefabPathsForModelCategory(category);
        return prefabs != null && paths != null && index >= 0 && index < prefabs.Length && index < paths.Length &&
               prefabs[index] == null && !modelPrefabLoadFailedPaths.Contains(paths[index]);
    }


    // 読んだ prefab を枠に入れる。名前の突き合わせと、実機の cutout の透けの直し（PassthroughCutoutAlpha）もここで 1 個ずつ。
    private void StoreModelPrefab(string category, int index, string path, Object asset)
    {
        if (!(asset is GameObject prefab))
        {
            modelPrefabLoadFailedPaths.Add(path);
            Debug.LogWarning($"[Model] 読めません: {path}");
            return;
        }

        GameObject[] prefabs = ResolvePrefabsForModelCategory(category);
        if (prefabs == null || index < 0 || index >= prefabs.Length)
        {
            return;
        }

        string expected = ModelNameAt(ResolvePrefabNamesForModelCategory(category), index);
        if (expected != prefab.name)
        {
            // 名前で引く（保存した選択・既定1/既定2）ところは名前の一覧を見るので、ずれると別のモデルが出る。
            Debug.LogError($"[Model] 一覧の名前 {expected} と prefab の名前 {prefab.name} が違う（{path}）");
        }

        if (prefabs[index] == null)
        {
            prefabs[index] = prefab;
            ApplyCutoutAlphaToModelPrefabs(category, new[] { prefab });
        }
    }


    // 実機の MSAA 4x で、cutout（毛・ヒゲのカード、人の髪・服）からパススルーの部屋が透けるのを防ぐ（PassthroughCutoutAlpha）。
    // 書き換えるのは実機だけ（Editor では 0 件）。失敗しても読み込みは止めない。
    private void ApplyCutoutAlphaToModelPrefabs(string category, GameObject[] prefabs)
    {
        if (!cutoutAlphaForPassthrough)
        {
            return;
        }

        try
        {
            cutoutAlphaChangedCount += PassthroughCutoutAlpha.ApplyToPrefabs(prefabs, category, cutoutAlphaDoneMaterials);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
        }
    }


    // 枠の中身を返す。空なら同期で 1 個読む（先に読む一覧に無かったモデル）。範囲外・読めないときは null。
    private GameObject GetOrLoadModelPrefab(string category, int index, string reason)
    {
        GameObject[] prefabs = ResolvePrefabsForModelCategory(category);
        if (prefabs == null || index < 0 || index >= prefabs.Length)
        {
            return null;
        }

        if (prefabs[index] != null || !IsModelSlotPending(category, index))
        {
            return prefabs[index];
        }

        string path = ResolvePrefabPathsForModelCategory(category)[index];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        StoreModelPrefab(category, index, path, Resources.Load<GameObject>(path));
        string message = $"[LOADTIME] 同期で読んだ {ModelNameOfPath(path)} {stopwatch.ElapsedMilliseconds}ms（{reason}）";
        if (startedAsExperimentTrial)
        {
            // 実験の試行は先に全部読んでいるはず。出たら CollectModelPrefabSlotsForBundle の漏れで、試行の途中でコントローラーが固まる。
            Debug.LogWarning(message + " ← 実験で先に読む一覧から漏れた");
        }
        else
        {
            Debug.Log(message);
        }

        return prefabs[index];
    }


    // 1 個ずつフレームを跨いで読む。読み済み・読めないものは飛ばす。読んだ名前を loadedNames に足す（null 可）。
    private IEnumerator LoadModelPrefabSlotsAsync(List<(string category, int index)> slots, List<string> loadedNames)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            (string category, int index) = slots[i];
            if (!IsModelSlotPending(category, index))
            {
                continue;
            }

            string path = ResolvePrefabPathsForModelCategory(category)[index];
            ResourceRequest request = Resources.LoadAsync<GameObject>(path);
            while (!request.isDone)
            {
                yield return null;
            }

            StoreModelPrefab(category, index, path, request.asset);
            loadedNames?.Add(ModelNameOfPath(path));
        }
    }


    private List<(string category, int index)> CollectAllModelPrefabSlots()
    {
        var slots = new List<(string category, int index)>();
        foreach (string category in ModelPrefabCategories)
        {
            GameObject[] prefabs = ResolvePrefabsForModelCategory(category);
            for (int i = 0; prefabs != null && i < prefabs.Length; i++)
            {
                slots.Add((category, i));
            }
        }

        return slots;
    }


    // bundle を開いた後、再生の準備（Prepare）より前に呼ぶ。この動画で最初に要るモデルだけを読む。
    // RestoreTrackCustomization・SeedExperimentModelPresets・ResolveInitialNormalMode の後でないと、どのモデルが要るか決まらない。
    private IEnumerator LoadModelPrefabsNeededForBundle()
    {
        if (LoadsAllModelPrefabsUpFront || isNormalMode)
        {
            // 全部読み済み / モデルを出さない（実験の単眼・立体）。
            yield break;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var slots = new List<(string category, int index)>();
        int tracks = CollectModelPrefabSlotsForBundle(slots);
        long collectMs = stopwatch.ElapsedMilliseconds;
        var loadedNames = new List<string>();
        modelPrefabsNeededLoading = true;
        try
        {
            yield return LoadModelPrefabSlotsAsync(slots, loadedNames);
        }
        finally
        {
            modelPrefabsNeededLoading = false;
        }

        Debug.Log(
            $"[LOADTIME] この動画のモデルを先に読む {stopwatch.ElapsedMilliseconds}ms " +
            $"（track {tracks} 個、読んだ {loadedNames.Count} 個: {(loadedNames.Count > 0 ? string.Join(",", loadedNames) : "-")}、" +
            $"track を数えるのに {collectMs}ms）");
    }


    // meta.bin の全 track について、ResolveTrackPrefab が選ぶはずのモデルを集める。実験の Model ボタンの既定2 も足す。
    // **ResolveTrackPrefab と同じ順で決める**（預けた名前 → selectedModelIndexByTrack → trackModelIndices → 種類ごとの既定）。
    // 預けた名前（pendingModelNameByTrack）と練習の犬（experimentPreferredAnimalModelName）は消費しない。
    // 戻り値は track の数。
    private int CollectModelPrefabSlotsForBundle(List<(string category, int index)> slots)
    {
        var categoryByTrack = new Dictionary<uint, byte>();
        var objs = new List<MetaObj>(16);
        int frames = manifest != null ? manifest.num_frames : 0;
        for (int f = 0; f < frames; f++)
        {
            if (!TryReadFrameObjects(f, objs))
            {
                continue;
            }

            for (int i = 0; i < objs.Count; i++)
            {
                if (!categoryByTrack.ContainsKey(objs[i].trackId))
                {
                    categoryByTrack[objs[i].trackId] = objs[i].categoryId;
                }
            }
        }

        foreach (KeyValuePair<uint, byte> kv in categoryByTrack)
        {
            string category = ResolveRuntimeModelPickerCategoryLabel(kv.Value);
            int index = PredictTrackModelIndex(kv.Key, kv.Value);
            if (!IsHiddenModelIndex(index))
            {
                AddModelPrefabSlot(slots, category, index);
            }
        }

        // 押したときに待たないよう、もう一方の組も先に読む（ユーザー「二組でいい」）。
        foreach (ExperimentModelPreset entry in activeModelPresets)
        {
            string[] names = ResolvePrefabNamesForModelCategory(entry.category);
            AddModelPrefabSlot(slots, entry.category, IndexOfModelName(names, entry.preset1, false));
            AddModelPrefabSlot(slots, entry.category, IndexOfModelName(names, entry.preset2, false));
        }

        return categoryByTrack.Count;
    }


    private void AddModelPrefabSlot(List<(string category, int index)> slots, string category, int index)
    {
        GameObject[] prefabs = ResolvePrefabsForModelCategory(category);
        if (prefabs == null || prefabs.Length == 0 || index < 0)
        {
            return;
        }

        // ResolvePrefabFromSelection と同じく範囲外は端に丸める。
        index = Mathf.Clamp(index, 0, prefabs.Length - 1);
        if (!slots.Contains((category, index)))
        {
            slots.Add((category, index));
        }
    }


    // ResolveTrackPrefab（ResolvePendingModelSelection・ApplyExperimentPreferredAnimalModelOnce・ResolvePrefabFromSelection）と同じ決め方。
    private int PredictTrackModelIndex(uint trackId, byte categoryId)
    {
        string[] names = ResolvePrefabNamesForCategory(categoryId);
        if (pendingModelNameByTrack.TryGetValue(trackId, out string pendingName) && !string.IsNullOrEmpty(pendingName))
        {
            if (pendingName == HiddenModelName)
            {
                return HiddenModelIndex;
            }

            int pendingIndex = IndexOfModelName(names, pendingName, false);
            if (pendingIndex >= 0)
            {
                return pendingIndex;
            }
        }

        int defaultIndex = selectedHumanIndex;
        if (IsCategoryAnimal(categoryId))
        {
            int preferred = IndexOfModelName(names, experimentPreferredAnimalModelName, true);
            defaultIndex = preferred >= 0 ? preferred : selectedAnimalIndex;
        }
        else if (IsCategoryOther(categoryId))
        {
            defaultIndex = selectedElseIndex;
        }

        return ResolveSelectedModelIndex(trackId, defaultIndex);
    }


    // 自由に見るときだけ、再生が始まってから残りのモデルを裏で読む（Model の窓のため）。EnsureBundleAndPrepareVideo の Prepare の後に呼ぶ。
    private void StartModelPrefabBackgroundLoadIfNeeded()
    {
        if (modelBackgroundLoadStarted || startedAsExperimentTrial || LoadsAllModelPrefabsUpFront)
        {
            return;
        }

        modelBackgroundLoadStarted = true;
        StartCoroutine(LoadRemainingModelPrefabsInBackground());
    }


    private IEnumerator LoadRemainingModelPrefabsInBackground()
    {
        while (vp == null || !vp.isPrepared)
        {
            yield return null;
        }

        float wait = ModelBackgroundLoadStartDelaySeconds;
        while (wait > 0f)
        {
            wait -= Time.unscaledDeltaTime;
            yield return null;
        }

        modelBackgroundLoadSavedPriority = Application.backgroundLoadingPriority;
        modelBackgroundLoadRunning = true;
        var total = System.Diagnostics.Stopwatch.StartNew();
        float worstFrameSeconds = 0f;
        int count = 0;
        while (TryPickNextBackgroundModelSlot(out string category, out int index))
        {
            // 窓を開いている間は今のページを従来の速さで読む。閉じている間は 1 フレームに使う時間を絞る（Low = 2ms）。
            // backgroundLoadingPriority は全体の設定なので、終わったら・OnDisable で戻す。
            Application.backgroundLoadingPriority = runtimeModelPickerOpen ? modelBackgroundLoadSavedPriority : ThreadPriority.Low;
            string path = ResolvePrefabPathsForModelCategory(category)[index];
            var one = System.Diagnostics.Stopwatch.StartNew();
            ResourceRequest request = Resources.LoadAsync<GameObject>(path);
            while (!request.isDone)
            {
                yield return null;
                worstFrameSeconds = Mathf.Max(worstFrameSeconds, Time.unscaledDeltaTime);
            }

            StoreModelPrefab(category, index, path, request.asset);
            count++;
            Debug.Log($"[LOADTIME] 裏で読んだ {ModelNameOfPath(path)} {one.ElapsedMilliseconds}ms");
            // 1 個ごとに 1 フレーム空ける。
            yield return null;
            worstFrameSeconds = Mathf.Max(worstFrameSeconds, Time.unscaledDeltaTime);
        }

        RestoreModelBackgroundLoadPriority();
        Debug.Log($"[LOADTIME] 裏の読み込み終わり {count} 個 {total.ElapsedMilliseconds}ms 最悪のフレーム {(worstFrameSeconds * 1000f):F0}ms");
    }


    // 次に裏で読む枠。窓を開いていれば今のページを先に、そうでなければ Human → Animal → Else の順。
    private bool TryPickNextBackgroundModelSlot(out string category, out int index)
    {
        // 窓のページ（UpdateRuntimeModelPickerEntryButtons が最後に作った種類）。metaFrameObjects を触らないよう target は解き直さない。
        if (runtimeModelPickerOpen && previewBuiltCategoryId != 255)
        {
            category = ResolveRuntimeModelPickerCategoryLabel(previewBuiltCategoryId);
            int start = runtimeModelPickerPageIndex * RuntimeModelPickerEntriesPerPage;
            for (index = start; index < start + RuntimeModelPickerEntriesPerPage; index++)
            {
                if (IsModelSlotPending(category, index))
                {
                    return true;
                }
            }
        }

        foreach (string candidate in ModelPrefabCategories)
        {
            GameObject[] prefabs = ResolvePrefabsForModelCategory(candidate);
            for (index = 0; prefabs != null && index < prefabs.Length; index++)
            {
                if (IsModelSlotPending(candidate, index))
                {
                    category = candidate;
                    return true;
                }
            }
        }

        category = null;
        index = -1;
        return false;
    }


    private void RestoreModelBackgroundLoadPriority()
    {
        if (!modelBackgroundLoadRunning)
        {
            return;
        }

        Application.backgroundLoadingPriority = modelBackgroundLoadSavedPriority;
        modelBackgroundLoadRunning = false;
    }


    // Model の窓の 1 ページのうち、中身を読み終えた枠の数。増えたらプレビューを作り直す（UpdateRuntimeModelPickerEntryButtons）。
    private static int CountLoadedModelPrefabsOnPage(GameObject[] prefabs, int page, int perPage)
    {
        int loaded = 0;
        int start = page * perPage;
        for (int i = start; prefabs != null && i < start + perPage && i < prefabs.Length; i++)
        {
            if (prefabs[i] != null)
            {
                loaded++;
            }
        }

        return loaded;
    }
}
