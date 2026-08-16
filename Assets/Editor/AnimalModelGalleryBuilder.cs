using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Resources/Models/Animal の prefab を全部同じ見た目サイズ・同じ向きに揃えて、
// グリッド状に並べた一覧シーン (All_Animal_model.unity) を生成する。
//
// サイズ: 動物モデルは import 元のスケールがまちまち（マンモスもネズミも prefab スケール 1.0）
//         なので、素のまま並べても一覧にならない。各モデルの描画境界 (Renderer.bounds) の
//         最大辺を ModelSize に揃え、足元を y=0 に落としてセル中心に置く。
//
// 向き:   import 元パッケージごとに forward がバラバラなので、canonical rig のボーン
//         （head / tail_base 等、docs/adr/0001-canonical-animal-rig-bone-names.md）から
//         体軸を実測し、Y 軸回転だけで TargetFacingYaw の向きへ揃える。
//         Y 軸回転に限定するのは、prefab はどれも y-up で立っており、他軸を触ると倒れるため。
public static class AnimalModelGalleryBuilder
{
    private const string ScenePath = "Assets/Scenes/All_Animal_model.unity";
    private const string PrefabDir = "Assets/Resources/Models/Animal";
    private const string RootName = "AnimalGallery";

    // 並べ方の調整はこの 3 つで行う。52 体なら 8 列 x 7 行。
    private const int Columns = 8;
    private const float ModelSize = 1.0f;   // 正規化後のモデル最大辺 (m)
    private const float CellSpacing = 1.8f; // 隣り合うセルの中心間距離 (m)

    // カメラは +Z 側の斜め上から原点を見る。0 = カメラ正面向き、90 = 真横。
    // 45 は顔と体側面が同時に見える「斜め前」。
    private const float TargetFacingYaw = 45f;

    // 自動推定が外れたモデルはここに prefab 名 -> 追加 yaw(度) を足して個別補正する。
    // 前後が裏返っているだけなら 180 を入れればよい。
    private static readonly Dictionary<string, float> ManualYawOverrides = new Dictionary<string, float>
    {
    };

    private const float LabelFontSize = 100f;
    private const float LabelCharacterSize = 0.016f;

    // 体軸をどう決めたか。ログに出して、怪しいものを ManualYawOverrides で直すための情報。
    private enum FacingSource
    {
        CanonicalBones, // head/neck と tail_base/spine（最も信頼できる）
        LooseBones,     // 名前にマッチするボーンを緩く検索
        FrontLegs,      // 前脚の左右から法線として算出
        BoundsOnly,     // 描画境界の長辺のみ。前後の符号は不定
    }

    [MenuItem("Tools/VisionGraft/Build Animal Model Gallery Scene")]
    public static void Build()
    {
        string[] prefabPaths = CollectPrefabPaths();
        if (prefabPaths.Length == 0)
        {
            Debug.LogError($"[AnimalGallery] {PrefabDir} に prefab が見つかりません。");
            return;
        }

        bool sceneExists = File.Exists(ScenePath);
        string message = sceneExists
            ? $"{ScenePath} を作り直します。\n現在のシーン内容は破棄されます。\n\n配置するモデル: {prefabPaths.Length} 体"
            : $"{ScenePath} を新規作成します。\n\n配置するモデル: {prefabPaths.Length} 体";
        if (!EditorUtility.DisplayDialog("Animal Model Gallery", message, "実行", "キャンセル"))
        {
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var root = new GameObject(RootName);
        int rows = Mathf.CeilToInt(prefabPaths.Length / (float)Columns);
        var boundsFailed = new List<string>();
        var facingUncertain = new List<string>();
        var log = new System.Text.StringBuilder();

        for (int i = 0; i < prefabPaths.Length; i++)
        {
            string path = prefabPaths[i];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                boundsFailed.Add(Path.GetFileNameWithoutExtension(path));
                continue;
            }

            var cell = new GameObject(prefab.name);
            cell.transform.SetParent(root.transform, false);
            cell.transform.localPosition = CellPosition(i, rows);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, cell.transform);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            FacingSource source = ResolveForward(instance, out Vector3 forward);

            // 順序が重要。体軸を +Z に揃えてから寸法を測る。斜めに構えたモデルの AABB は
            // 対角方向に膨らむため、揃える前に測ると胴の長い動物ほど小さく正規化されてしまう。
            AlignYaw(instance, forward, 0f);
            bool scaled = TryNormalizeScale(instance);

            float yaw = TargetFacingYaw;
            if (ManualYawOverrides.TryGetValue(prefab.name, out float extraYaw))
            {
                yaw += extraYaw;
            }

            RotateYaw(instance, yaw);
            bool snapped = TrySnapToCell(instance);

            if (!scaled || !snapped)
            {
                boundsFailed.Add(prefab.name);
            }
            if (source == FacingSource.BoundsOnly)
            {
                facingUncertain.Add(prefab.name);
            }

            log.AppendLine($"  {prefab.name}\t{source}");
            CreateLabel(cell.transform, prefab.name);
        }

        SetupCamera(rows);
        SetupLight();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log(
            $"[AnimalGallery] {ScenePath} を生成しました。{prefabPaths.Length} 体 / {Columns} 列 x {rows} 行\n" +
            $"体軸の決定方法:\n{log}" +
            (boundsFailed.Count > 0 ? $"\n描画境界を取得できず未調整: {string.Join(", ", boundsFailed)}" : string.Empty) +
            (facingUncertain.Count > 0
                ? $"\n向きが不確実（前後が裏返っている可能性あり。ManualYawOverrides に 180 を入れて調整）: " +
                  $"{string.Join(", ", facingUncertain)}"
                : string.Empty));

        EditorUtility.DisplayDialog(
            "Animal Model Gallery",
            $"完了\n配置: {prefabPaths.Length} 体（{Columns} 列 x {rows} 行）\n" +
            $"向きが不確実: {facingUncertain.Count} 体\n" +
            $"サイズ未調整: {boundsFailed.Count} 体\n\n詳細は Console を参照してください。",
            "OK");
    }

    // PrefabDir 直下のみを対象にする。Sources/ 以下の素材 prefab は一覧に含めない。
    private static string[] CollectPrefabPaths()
    {
        if (!Directory.Exists(PrefabDir))
        {
            return new string[0];
        }

        return AssetDatabase.FindAssets("t:Prefab", new[] { PrefabDir })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => (Path.GetDirectoryName(p) ?? string.Empty).Replace('\\', '/') == PrefabDir)
            .Distinct()
            .OrderBy(Path.GetFileNameWithoutExtension, System.StringComparer.Ordinal)
            .ToArray();
    }

    // グリッド全体が原点中心になるように、行は手前 (z-) 方向へ進める。
    private static Vector3 CellPosition(int index, int rows)
    {
        int column = index % Columns;
        int row = index / Columns;
        float width = (Columns - 1) * CellSpacing;
        float depth = (rows - 1) * CellSpacing;
        return new Vector3(
            column * CellSpacing - width * 0.5f,
            0f,
            depth * 0.5f - row * CellSpacing);
    }

    // 最大辺を ModelSize に揃える。
    private static bool TryNormalizeScale(GameObject instance)
    {
        if (!TryGetWorldBounds(instance, out Bounds bounds))
        {
            return false;
        }

        float longestSide = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (longestSide <= 1e-5f)
        {
            return false;
        }

        instance.transform.localScale *= ModelSize / longestSide;
        return true;
    }

    // currentForward が targetYaw の方角を向くよう、world Y 軸回りに回す。
    private static void AlignYaw(GameObject instance, Vector3 currentForward, float targetYaw)
    {
        Vector3 target = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward;
        RotateYaw(instance, Vector3.SignedAngle(currentForward, target, Vector3.up));
    }

    private static void RotateYaw(GameObject instance, float degrees)
    {
        instance.transform.rotation = Quaternion.AngleAxis(degrees, Vector3.up) * instance.transform.rotation;
    }

    private static FacingSource ResolveForward(GameObject instance, out Vector3 forward)
    {
        Transform[] bones = instance.GetComponentsInChildren<Transform>(true);

        // 1. canonical rig。head と tail_base が揃えば前後の符号まで確定する。
        Transform front = FindExact(bones, "head", "neck");
        Transform back = FindExact(bones, "tail_base", "spine");
        if (front != null && back != null && TryHorizontalAxis(front, back, out forward))
        {
            return FacingSource.CanonicalBones;
        }

        // 2. リネーム前のモデル向けに緩く探す。
        Transform looseFront = front ?? FindShortestContaining(bones, "head", "neck", "skull");
        Transform looseBack = back ?? FindShortestContaining(bones, "tail", "spine", "pelvis", "hip");
        if (looseFront != null && looseBack != null && TryHorizontalAxis(looseFront, looseBack, out forward))
        {
            return FacingSource.LooseBones;
        }

        // 3. 前脚の左右から。Unity は左手系なので forward = right x up。
        Transform leftLeg = FindExact(bones, "front_l_upper");
        Transform rightLeg = FindExact(bones, "front_r_upper");
        if (leftLeg != null && rightLeg != null && TryHorizontalAxis(rightLeg, leftLeg, out Vector3 rightAxis))
        {
            forward = Vector3.Cross(rightAxis, Vector3.up).normalized;
            if (forward.sqrMagnitude > 1e-6f)
            {
                return FacingSource.FrontLegs;
            }
        }

        // 4. 最後の手段。胴が伸びている水平方向を前後軸とみなす。符号は決まらない。
        forward = Vector3.forward;
        if (TryGetWorldBounds(instance, out Bounds bounds))
        {
            forward = bounds.size.x >= bounds.size.z ? Vector3.right : Vector3.forward;
        }
        return FacingSource.BoundsOnly;
    }

    // 閾値はボーン間距離との比で見る。ここはスケール正規化より前に呼ばれるので、
    // 絶対長で判定するとマンモスとネズミで基準が変わってしまう。
    private static bool TryHorizontalAxis(Transform from, Transform to, out Vector3 axis)
    {
        Vector3 delta = from.position - to.position;
        float length = delta.magnitude;
        delta.y = 0f;

        // 頭が体の真上にあるモデル（鳥・カンガルー等）では前後方向の手掛かりにならない。
        if (length <= 1e-6f || delta.magnitude < length * 0.3f)
        {
            axis = Vector3.forward;
            return false;
        }

        axis = delta.normalized;
        return true;
    }

    // names の並び順が優先順位。先に見つかった名前を返す。
    private static Transform FindExact(Transform[] bones, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (Transform bone in bones)
            {
                if (string.Equals(bone.name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return bone;
                }
            }
        }

        return null;
    }

    // token を含むボーンのうち最も名前が短いものを返す。"head" を狙って "headTop_End" を
    // 拾わないようにするための単純な優先付け。
    private static Transform FindShortestContaining(Transform[] bones, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            Transform best = null;
            foreach (Transform bone in bones)
            {
                if (bone.name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (best == null || bone.name.Length < best.name.Length)
                {
                    best = bone;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    // 水平方向の中心をセル中心に、足元を y=0 に合わせる。
    private static bool TrySnapToCell(GameObject instance)
    {
        if (!TryGetWorldBounds(instance, out Bounds bounds))
        {
            return false;
        }

        Vector3 cellOrigin = instance.transform.parent != null
            ? instance.transform.parent.position
            : Vector3.zero;
        instance.transform.position += new Vector3(
            cellOrigin.x - bounds.center.x,
            cellOrigin.y - bounds.min.y,
            cellOrigin.z - bounds.center.z);
        return true;
    }

    private static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer)
            {
                continue;
            }

            Bounds rendererBounds = renderer.bounds;

            // 非再生モードの SkinnedMeshRenderer は bounds が潰れて返ることがあるので、
            // bind pose のメッシュ境界から作り直す。
            if (rendererBounds.size.sqrMagnitude < 1e-8f
                && renderer is SkinnedMeshRenderer skinned
                && skinned.sharedMesh != null)
            {
                rendererBounds = TransformBounds(skinned.sharedMesh.bounds, skinned.transform.localToWorldMatrix);
            }

            if (rendererBounds.size.sqrMagnitude < 1e-8f)
            {
                continue;
            }

            if (!found)
            {
                bounds = rendererBounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(rendererBounds);
            }
        }

        return found;
    }

    // ローカル AABB を行列で変換し、変換後を包む軸平行 AABB を返す。
    private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
    {
        Vector3 center = matrix.MultiplyPoint3x4(local.center);
        Vector3 e = local.extents;
        var extents = new Vector3(
            Mathf.Abs(matrix.m00) * e.x + Mathf.Abs(matrix.m01) * e.y + Mathf.Abs(matrix.m02) * e.z,
            Mathf.Abs(matrix.m10) * e.x + Mathf.Abs(matrix.m11) * e.y + Mathf.Abs(matrix.m12) * e.z,
            Mathf.Abs(matrix.m20) * e.x + Mathf.Abs(matrix.m21) * e.y + Mathf.Abs(matrix.m22) * e.z);
        return new Bounds(center, extents * 2f);
    }

    // 名前は床に寝かせて置く。見下ろし視点でも斜め視点でも読める。
    private static void CreateLabel(Transform cell, string text)
    {
        var label = new GameObject("Label");
        label.transform.SetParent(cell, false);
        label.transform.localPosition = new Vector3(0f, 0.001f, -CellSpacing * 0.48f);
        label.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var mesh = label.AddComponent<TextMesh>();
        mesh.text = text;
        mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        mesh.fontSize = Mathf.RoundToInt(LabelFontSize);
        mesh.characterSize = LabelCharacterSize;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = Color.white;

        var renderer = label.GetComponent<MeshRenderer>();
        if (renderer != null && mesh.font != null)
        {
            renderer.sharedMaterial = mesh.font.material;
        }
    }

    // グリッド全体が収まる位置へ斜め上から見下ろす。
    private static void SetupCamera(int rows)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            return;
        }

        float width = Columns * CellSpacing;
        float depth = rows * CellSpacing;
        float radius = Mathf.Max(width, depth) * 0.5f;
        float distance = radius / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;

        const float pitch = 55f;
        float pitchRad = pitch * Mathf.Deg2Rad;
        camera.transform.position = new Vector3(
            0f,
            distance * Mathf.Sin(pitchRad),
            distance * Mathf.Cos(pitchRad));
        camera.transform.LookAt(Vector3.zero, Vector3.up);
        camera.farClipPlane = Mathf.Max(camera.farClipPlane, distance * 3f);
    }

    private static void SetupLight()
    {
        foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light.type == LightType.Directional)
            {
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                light.intensity = 1.2f;
            }
        }
    }
}
