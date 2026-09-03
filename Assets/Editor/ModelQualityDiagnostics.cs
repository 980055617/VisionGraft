using System.Text;
using UnityEditor;
using UnityEngine;

// 「遠いとポリゴンが減る」「VRoid のスカート・髪が重力に従わない」を切り分けるための実測。
//
// 前者は LODGroup の有無で決まる。モデルは bbox 合わせで 0.26 や 0.0065 まで縮むので、
// 画面占有率で LOD を選ぶ仕組みだと常に最下位が選ばれてもおかしくない。
// 後者は VRM の SpringBone（揺れもの）が入っているかどうか。
//
//   Unity.exe -batchmode -projectPath . -executeMethod ModelQualityDiagnostics.Dump -quit
public static class ModelQualityDiagnostics
{
    public static void Dump()
    {
        var sb = new StringBuilder();
        foreach (string folder in new[] { "Human", "Animal", "Else" })
        {
            GameObject[] all = Resources.LoadAll<GameObject>($"Models/{folder}");
            int withLod = 0;
            int total = 0;
            sb.AppendLine($"[Quality] ==== {folder}");
            foreach (GameObject go in all)
            {
                if (go == null || !IsIndexedPrefabName(go.name))
                {
                    continue;
                }

                total++;
                LODGroup[] groups = go.GetComponentsInChildren<LODGroup>(true);
                int springComponents = CountSpringLikeComponents(go);
                if (groups.Length > 0 || springComponents > 0)
                {
                    if (groups.Length > 0)
                    {
                        withLod++;
                    }

                    var lodInfo = new StringBuilder();
                    for (int i = 0; i < groups.Length; i++)
                    {
                        lodInfo.Append(groups[i] != null ? groups[i].lodCount : 0).Append(' ');
                    }

                    sb.AppendLine(
                        $"[Quality] {go.name,-24} LODGroup={groups.Length} (段数: {lodInfo}) " +
                        $"揺れもの={springComponents}");
                }
            }

            sb.AppendLine($"[Quality] {folder}: LODGroup を持つ prefab {withLod} / {total}");
            sb.AppendLine();
        }

        Debug.Log(sb.ToString());
    }


    // VRM の揺れもの（SpringBone 系）は型名で拾う。パッケージのバージョンで名前空間が
    // 変わるので、型名に "SpringBone" を含むものを数える。
    private static int CountSpringLikeComponents(GameObject go)
    {
        int count = 0;
        foreach (Component c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null)
            {
                continue;
            }

            string name = c.GetType().Name;
            if (name.Contains("SpringBone") || name.Contains("Spring") || name.Contains("Cloth"))
            {
                count++;
            }
        }

        return count;
    }


    // 06_Female_C（VRoid 由来の .vrm）に何が入っているかを型名で列挙する。
    // 「揺れものが無い」のか「あるのに動いていない」のかで対処がまったく変わる。
    //
    //   Unity.exe -batchmode ... -executeMethod ModelQualityDiagnostics.DumpVrmComponents -quit
    public static void DumpVrmComponents()
    {
        GameObject go = Resources.Load<GameObject>("Models/Human/06_Female_C");
        if (go == null)
        {
            Debug.Log("[VRM] 06_Female_C が見つかりません");
            return;
        }

        var counts = new System.Collections.Generic.SortedDictionary<string, int>();
        foreach (Component c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null)
            {
                continue;
            }

            string key = c.GetType().FullName;
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[VRM] 06_Female_C のコンポーネント一覧");
        foreach (var kv in counts)
        {
            // Transform / Renderer だけで数百行になるので、素の Unity 型は省く。
            if (kv.Key.StartsWith("UnityEngine.") &&
                !kv.Key.Contains("Cloth") && !kv.Key.Contains("Rigidbody") && !kv.Key.Contains("Joint"))
            {
                continue;
            }

            sb.AppendLine($"[VRM]   {kv.Value,4} x {kv.Key}");
        }

        Debug.Log(sb.ToString());
    }


    // モデルに当たり判定があるかを数える。掴んで回すにはレイが当たる必要がある。
    // 無ければ生成時に bounds から箱を足すことになるので、設計の前提が変わる。
    //
    //   Unity.exe -batchmode ... -executeMethod ModelQualityDiagnostics.DumpColliders -quit
    public static void DumpColliders()
    {
        var sb = new StringBuilder();
        foreach (string folder in new[] { "Human", "Animal", "Else" })
        {
            int total = 0;
            int withCollider = 0;
            var sample = new StringBuilder();
            foreach (GameObject go in Resources.LoadAll<GameObject>($"Models/{folder}"))
            {
                if (go == null || !IsIndexedPrefabName(go.name))
                {
                    continue;
                }

                total++;
                Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
                if (colliders.Length > 0)
                {
                    withCollider++;
                    if (sample.Length < 200)
                    {
                        sample.Append(go.name).Append('(').Append(colliders.Length).Append(") ");
                    }
                }
            }

            sb.AppendLine($"[Collider] {folder}: 当たり判定を持つ prefab {withCollider} / {total}  {sample}");
        }

        // レイが当たるレイヤーの確認材料として、track インスタンスが置かれる既定レイヤーも出す。
        sb.AppendLine($"[Collider] Default レイヤー番号 = {LayerMask.NameToLayer("Default")}");
        Debug.Log(sb.ToString());
    }


    private static bool IsIndexedPrefabName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               name.Length >= 3 &&
               char.IsDigit(name[0]) &&
               char.IsDigit(name[1]) &&
               name[2] == '_';
    }
}
