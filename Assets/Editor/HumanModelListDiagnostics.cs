using System.Text;
using UnityEditor;
using UnityEngine;

// Resources から実際に読まれる Human モデル一覧を、runtime と同じ手順で出す。
//
// 06_Female_C だけ .vrm で、他は .prefab。Resources.LoadAll<GameObject> は
// vrm の import 結果も GameObject として拾うので一覧には入るが、
// **Humanoid の Animator を持っているか**は別問題。姿勢適用は Animator.isHuman が
// 前提なので、そこが違えば「選べるのに動かないモデル」になる。
//
//   Unity.exe -batchmode -projectPath . -executeMethod HumanModelListDiagnostics.Dump -quit
public static class HumanModelListDiagnostics
{
    public static void Dump()
    {
        var sb = new StringBuilder();
        foreach (string folder in new[] { "Human", "Animal", "Else" })
        {
            GameObject[] all = Resources.LoadAll<GameObject>($"Models/{folder}");
            var kept = new System.Collections.Generic.List<GameObject>();
            foreach (GameObject go in all)
            {
                if (go != null && IsIndexedPrefabName(go.name))
                {
                    kept.Add(go);
                }
            }

            kept.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            sb.AppendLine($"[HumanList] ==== {folder}: {kept.Count} 件");
            for (int i = 0; i < kept.Count; i++)
            {
                GameObject go = kept[i];
                string path = AssetDatabase.GetAssetPath(go);
                string ext = System.IO.Path.GetExtension(path);
                Animator animator = go.GetComponentInChildren<Animator>(true);
                string rig = animator == null
                    ? "Animator なし"
                    : (animator.isHuman ? "Humanoid" : "Generic");
                string avatar = animator != null && animator.avatar != null
                    ? (animator.avatar.isValid ? "avatar OK" : "avatar 無効")
                    : "avatar なし";
                int renderers = go.GetComponentsInChildren<Renderer>(true).Length;
                sb.AppendLine($"[HumanList] {i,2} {go.name,-24} {ext,-8} {rig,-12} {avatar,-11} renderers={renderers}");
            }

            sb.AppendLine();
        }

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
