using System.Text;
using UnityEditor;
using UnityEngine;

// Else モデルの向きを調べる。どの軸が長いかが分かれば「縦になっている」原因が確定する。
//
//   Unity.exe -batchmode -projectPath . -executeMethod ElseOrientationDiagnostics.Dump -quit
//
// 06_DieselLocomotive は prefab に X 軸 -90 度が入っている（w=0.7071 x=-0.7071）。
// glTF は仕様上 Y-up なので、Blender 流の Z-up 補正を掛けると**機関車が縦に立つ**。
// 実際にどうなっているかを bounds で確かめる。
public static class ElseOrientationDiagnostics
{
    private static readonly string[] Targets =
    {
        "Assets/Resources/Models/Else/06_DieselLocomotive.prefab",
        "Assets/Resources/Models/Else/00_Baseball.prefab",
    };

    // 全モデルの root 回転を出す。配置は root の world 回転を上書きするので、
    // **root に回転を持つモデルはその補正が実行時に消える。** 影響範囲を知るために数える。
    //
    //   Unity.exe -batchmode ... -executeMethod ElseOrientationDiagnostics.DumpAllRootRotations -quit
    public static void DumpAllRootRotations()
    {
        var sb = new StringBuilder();
        int nonIdentity = 0;
        int total = 0;
        foreach (string folder in new[] { "Human", "Animal", "Else" })
        {
            string dir = $"Assets/Resources/Models/{folder}";
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // Sources/ 配下の素材は対象外（LoadPrefabsFromResources と同じ考え）。
                if (path.Contains("/Sources/"))
                {
                    continue;
                }

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                total++;
                Quaternion q = prefab.transform.localRotation;
                float deg = Quaternion.Angle(Quaternion.identity, q);
                if (deg > 0.01f)
                {
                    nonIdentity++;
                    sb.AppendLine($"[RootRot] {folder,-6} {System.IO.Path.GetFileNameWithoutExtension(path),-24} " +
                                  $"euler={q.eulerAngles:F1} angle={deg:F1}");
                }
            }
        }

        sb.AppendLine($"[RootRot] **root に回転を持つ prefab: {nonIdentity} / {total}**");
        Debug.Log(sb.ToString());
    }


    // root 回転を「作者の意図」と見なしてよいかを、寸法で判定する。
    //
    // ReplaceableModel.Awake は renderer.bounds（**world** AABB）の .y を身長基準にするので、
    // root 回転の有無で基準がまるごと入れ替わる。どちらが実物の「高さ」に近いかを見れば、
    // その回転が意図的な補正か import 由来のゴミかが分かる。
    //   機関車 … 全高 4.2〜4.7m / 全長 18〜20m
    //   ロバ   … 体高 1.2〜1.4m / 体長 1.9〜2.1m
    //
    // **prefab の localRotation を直接読むこと。** ReplaceableModel.baseLocalRotation は
    // 後から足した Quaternion フィールドなので、既存 prefab では初期化子ではなく
    // (0,0,0,0) で復元される。
    //
    //   Unity.exe -batchmode ... -executeMethod ElseOrientationDiagnostics.DumpRotatedBounds -quit
    public static void DumpRotatedBounds()
    {
        string[] targets =
        {
            "Assets/Resources/Models/Else/06_DieselLocomotive.prefab",
            "Assets/Resources/Models/Animal/21_Donkey1.0.prefab",
        };

        var sb = new StringBuilder();
        foreach (string path in targets)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                sb.AppendLine($"[RotBounds] not found: {path}");
                continue;
            }

            Quaternion rot = prefab.transform.localRotation;
            if (!TryCombineBounds(prefab, Matrix4x4.identity, out Bounds flat) ||
                !TryCombineBounds(prefab, Matrix4x4.Rotate(rot), out Bounds rotated))
            {
                sb.AppendLine($"[RotBounds] no mesh: {path}");
                continue;
            }

            sb.AppendLine($"[RotBounds] ==== {System.IO.Path.GetFileNameWithoutExtension(path)} rootRot={rot.eulerAngles:F1}");
            sb.AppendLine($"[RotBounds]   回転なし size={flat.size:F3} **height={flat.size.y:F3}**");
            sb.AppendLine($"[RotBounds]   回転あり size={rotated.size:F3} **height={rotated.size.y:F3}**");
        }

        Debug.Log(sb.ToString());
    }


    // prefab ルートから見た合成 bounds に、さらに任意の行列を掛けたもの。
    private static bool TryCombineBounds(GameObject prefab, Matrix4x4 extra, out Bounds combined)
    {
        combined = default;
        bool has = false;
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = ResolveMesh(r);
            if (mesh == null)
            {
                continue;
            }

            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Bounds b = TransformBounds(mesh.bounds, extra * toRoot);
            if (!has)
            {
                combined = b;
                has = true;
            }
            else
            {
                combined.Encapsulate(b);
            }
        }

        return has;
    }


    // 四足のモデルは「頭が足より上」で up 軸が決まる。AABB の縦横比より確実。
    // 21_Donkey1.0 の root 回転が意図的な補正か import 由来のゴミかを、骨の位置で判定する。
    //
    //   Unity.exe -batchmode ... -executeMethod ElseOrientationDiagnostics.DumpAnimalUpAxis -quit
    public static void DumpAnimalUpAxis()
    {
        const string path = "Assets/Resources/Models/Animal/21_Donkey1.0.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.Log($"[UpAxis] not found: {path}");
            return;
        }

        var sb = new StringBuilder();
        Quaternion rot = prefab.transform.localRotation;
        sb.AppendLine($"[UpAxis] {System.IO.Path.GetFileNameWithoutExtension(path)} rootRot={rot.eulerAngles:F1}");

        Transform root = prefab.transform;
        foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            bool interesting =
                n.Contains("head") || n.Contains("neck") || n.Contains("nose") ||
                n.Contains("foot") || n.Contains("hoof") || n.Contains("toe") ||
                n.Contains("paw") || n.Contains("tail") || n.Contains("hip") ||
                n.Contains("pelvis") || n.Contains("spine") || n.Contains("root");
            if (!interesting)
            {
                continue;
            }

            // prefab ルートから見たローカル座標（＝root 回転を掛ける前）と、掛けた後。
            Vector3 local = root.InverseTransformPoint(t.position);
            Vector3 corrected = rot * local;
            sb.AppendLine($"[UpAxis]   {t.name,-28} local={local:F3} rotated={corrected:F3}");
        }

        sb.AppendLine("[UpAxis] **頭・首が足より上にある側が正しい基準（.y が大きい方）**");
        Debug.Log(sb.ToString());
    }


    public static void Dump()
    {
        var sb = new StringBuilder();
        foreach (string path in Targets)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                sb.AppendLine($"[ElseDiag] not found: {path}");
                continue;
            }

            sb.AppendLine($"[ElseDiag] ==== {path}");
            sb.AppendLine($"[ElseDiag]   root localRotation={prefab.transform.localRotation.eulerAngles:F1} " +
                          $"localScale={prefab.transform.localScale:F3}");

            // ルート基準の合成 bounds。実際に「どの向きに長いか」はこれで決まる。
            Bounds combined = default;
            bool has = false;
            foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = ResolveMesh(r);
                if (mesh == null)
                {
                    continue;
                }

                // メッシュのローカル bounds を、prefab ルートから見た空間へ移す。
                Bounds local = mesh.bounds;
                Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                Bounds inRoot = TransformBounds(local, toRoot);
                if (!has)
                {
                    combined = inRoot;
                    has = true;
                }
                else
                {
                    combined.Encapsulate(inRoot);
                }

                sb.AppendLine($"[ElseDiag]   renderer={r.name} meshBoundsSize={local.size:F3} " +
                              $"localRot={r.transform.localRotation.eulerAngles:F1}");
            }

            if (!has)
            {
                sb.AppendLine("[ElseDiag]   (no mesh)");
                continue;
            }

            Vector3 s = combined.size;
            string longest = s.x >= s.y && s.x >= s.z ? "X" : (s.y >= s.z ? "Y" : "Z");
            sb.AppendLine($"[ElseDiag]   **rootBoundsSize={s:F3} longestAxis={longest}**");
            sb.AppendLine($"[ElseDiag]   (縦長に見えるなら longestAxis=Y。機関車なら X か Z が正しい)");
        }

        Debug.Log(sb.ToString());
    }


    private static Mesh ResolveMesh(Renderer r)
    {
        if (r is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh;
        }

        MeshFilter filter = r.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }


    private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
    {
        Vector3 c = m.MultiplyPoint3x4(b.center);
        Vector3 e = b.extents;
        Vector3 x = m.MultiplyVector(new Vector3(e.x, 0f, 0f));
        Vector3 y = m.MultiplyVector(new Vector3(0f, e.y, 0f));
        Vector3 z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
        Vector3 ext = new Vector3(
            Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
            Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
            Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(c, ext * 2f);
    }
}
