using UnityEngine;

// コントローラから伸びるポインタの線。掴む対象を狙うために要る。
//
// パネルを閉じている間、ISDK のレイ表示は出ない（あれは canvas 用）。
// 線が見えないまま world では 0.2m しかないモデルを狙うのは無理で、
// 実機では 26 度ずれていた（2026-09-03 実測）。
public static class RuntimePointerRayFactory
{
    public readonly struct Ray
    {
        public Ray(GameObject root, LineRenderer line)
        {
            this.root = root;
            this.line = line;
        }

        public readonly GameObject root;
        public readonly LineRenderer line;
    }


    public static Ray Create()
    {
        GameObject root = new GameObject("RuntimePointerRay");
        LineRenderer line = root.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.widthMultiplier = 0.004f;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        // 動画スクリーンは Background キューで ZWrite Off なので、その手前に出すために
        // 深度に依存しない不透明マテリアルを使う（docs の「スクリーンは背景」参照）。
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader != null)
        {
            Material material = new Material(shader);
            material.color = new Color(0.35f, 0.8f, 1f, 1f);
            line.material = material;
        }

        line.startColor = new Color(0.35f, 0.8f, 1f, 1f);
        line.endColor = new Color(0.35f, 0.8f, 1f, 0.15f);
        return new Ray(root, line);
    }
}
