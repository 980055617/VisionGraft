// 車のライト: ヘッドライト・テールランプ・ウィンカー（左・右・ハザード）を点ける
//  - ランプの形の光（Emission）: lamp_color と lamp_mask を、ランプごとの明るさで合成して Emission に入れる
//    （材質そのものは変えず、MaterialPropertyBlock で渡すので、シーンや材質のファイルは書き換わらない）
//  - まわりを照らす光: ヘッドライトはスポットライト。オレンジは車の外側の地面をうすく照らす小さなスポットライト
//  再生していないときも点いて見える。点滅は再生（▶）中だけ。Inspector の値を変えるとすぐ反映される
using UnityEngine;

namespace MeshyModels
{
    [ExecuteAlways]
    public class CarLights : MonoBehaviour
    {
        public enum Blinker { Off, Left, Right, Hazard }

        [Header("点け方")]
        public bool headlights = true;
        public bool tailLights = true;
        public Blinker blinker = Blinker.Off;
        [Tooltip("点滅の1回の長さ（秒）。法律では 1分に 60〜120 回 = 0.5〜1.0 秒")]
        [Min(0.1f)] public float blinkPeriod = 0.8f;
        [Range(0.1f, 0.9f)] public float blinkOnRatio = 0.5f;

        [Header("ランプの形の光（Emission）の明るさ")]
        [Min(0f)] public float headGlow = 1.6f;
        [Min(0f)] public float amberGlow = 1.3f;
        [Min(0f)] public float tailGlow = 1.5f;

        [Header("まわりを照らす光")]
        [Min(0f)] public float headBeam = 14f;
        [Min(0f)] public float amberLight = 0.3f;

        [Header("つなぎ（シーンを作るときに自動で入る）")]
        public Renderer target;
        public Texture lampColor;
        public Texture lampMask;
        public Shader composeShader;
        public Light[] headBeams = new Light[0];
        public Light[] amberLeft = new Light[0];
        public Light[] amberRight = new Light[0];

        Material compose;
        RenderTexture rt;
        MaterialPropertyBlock mpb;
        Vector4 last = new Vector4(-1, -1, -1, -1);
        bool warned;
        Renderer lastTarget;

        bool EnsureInit()
        {
            if (target == null || lampColor == null || lampMask == null || composeShader == null)
            {
                bool fresh = target == null && lampColor == null && lampMask == null && composeShader == null;   // 付けたばかり（まだつないでいない）
                if (!warned && !fresh) { Debug.LogWarning("[CarLights] " + name + ": つなぎ（target / lampColor / lampMask / composeShader）が足りないので、ライトを点けられません", this); warned = true; }
                return false;
            }
            // つなぐ先が変わったら、前の車の光を外し、絵を作り直す
            if (lastTarget != null && lastTarget != target) lastTarget.SetPropertyBlock(null);
            lastTarget = target;
            if (rt != null && (rt.width != lampColor.width || rt.height != lampColor.height)) { rt.Release(); DestroyImmediate(rt); rt = null; }
            if (rt == null)
            {
                rt = new RenderTexture(lampColor.width, lampColor.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { useMipMap = true, autoGenerateMips = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = name + " lamps", hideFlags = HideFlags.DontSave };
                rt.Create();
                last = new Vector4(-1, -1, -1, -1);
            }
            if (compose == null) compose = new Material(composeShader) { hideFlags = HideFlags.DontSave };
            if (mpb == null) mpb = new MaterialPropertyBlock();
            // 材質の「光る」スイッチ（_EMISSION）が切れていたら入れる（URP は GI の設定が「光らない」だと切ってしまう）
            var m = target.sharedMaterial;
            if (m != null && !m.IsKeywordEnabled("_EMISSION")) { m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; m.EnableKeyword("_EMISSION"); }
            return true;
        }

        // t 秒のときの点き方にする（毎フレーム呼ばれる。確認用に外からも呼べる）
        public void Apply(float t)
        {
            if (!isActiveAndEnabled || !EnsureInit()) return;
            float period = Mathf.Max(0.1f, blinkPeriod);
            bool phase = blinker != Blinker.Off && Mathf.Repeat(t, period) < period * blinkOnRatio;
            bool l = phase && (blinker == Blinker.Left || blinker == Blinker.Hazard);
            bool r = phase && (blinker == Blinker.Right || blinker == Blinker.Hazard);
            var I = new Vector4(headlights ? headGlow : 0, l ? amberGlow : 0, r ? amberGlow : 0, tailLights ? tailGlow : 0);
            // 再生していないときは毎回作り直す（シーンを開いた直後は、まだ絵が読み込まれていないことがあるため）
            if (I != last || !rt.IsCreated() || !Application.isPlaying)
            {
                if (!rt.IsCreated()) rt.Create();
                compose.SetTexture("_Mask", lampMask);
                compose.SetVector("_I", I);
                var prev = RenderTexture.active;
    #if UNITY_EDITOR
                // シェーダーの準備が終わる前に描くと真っ黒になるので、この1回はシェーダーの準備を待つ
                bool async = UnityEditor.ShaderUtil.allowAsyncCompilation;
                UnityEditor.ShaderUtil.allowAsyncCompilation = false;
                Graphics.Blit(lampColor, rt, compose);
                UnityEditor.ShaderUtil.allowAsyncCompilation = async;
    #else
                Graphics.Blit(lampColor, rt, compose);
    #endif
                RenderTexture.active = prev;
                last = I;
            }
            target.GetPropertyBlock(mpb);
            mpb.SetTexture("_EmissionMap", rt);
            mpb.SetColor("_EmissionColor", Color.white);
            target.SetPropertyBlock(mpb);
            Set(headBeams, headlights, headBeam);
            Set(amberLeft, l, amberLight);
            Set(amberRight, r, amberLight);
        }

        // 確認用: 光の絵の明るさと、材質の設定を返す
        public string DebugInfo()
        {
            if (rt == null || target == null || target.sharedMaterial == null || lampColor == null) return "not ready";
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false); t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); t.Apply();
            RenderTexture.active = prev;
            var px = t.GetPixels(); float mx = 0, sum = 0; int lit = 0;
            foreach (var c in px) { float v = Mathf.Max(c.r, Mathf.Max(c.g, c.b)); mx = Mathf.Max(mx, v); sum += v; if (v > 0.05f) lit++; }
            DestroyImmediate(t);
            var m = target.sharedMaterial;
            return string.Format("rt {0}x{1} {2} created {3} | max {4:F2} lit texels {5} | mat {6} shader {7} _EMISSION {8} emisColor {9} | hasMPB {10} | lampColor {11} {12}",
                rt.width, rt.height, rt.format, rt.IsCreated(), mx, lit, m.name, m.shader.name, m.IsKeywordEnabled("_EMISSION"), m.GetColor("_EmissionColor"), target.HasPropertyBlock(), lampColor.name, lampColor.width);
        }

        static void Set(Light[] ls, bool on, float intensity)
        {
            foreach (var x in ls) if (x != null) { x.enabled = on; x.intensity = intensity; }
        }

        void OnEnable()
        {
            last = new Vector4(-1, -1, -1, -1);
            Apply(0f);
        }

        void OnValidate()
        {
    #if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null && isActiveAndEnabled) { last = new Vector4(-1, -1, -1, -1); Apply(Application.isPlaying ? Time.time : 0f); }
            };
    #endif
        }

        void Update()
        {
            Apply(Application.isPlaying ? Time.time : 0f);
        }

        void OnDisable()
        {
            if (target != null) target.SetPropertyBlock(null);
            Set(amberLeft, false, amberLight);
            Set(amberRight, false, amberLight);
            Set(headBeams, headlights, headBeam);
            if (rt != null)
            {
                if (RenderTexture.active == rt) RenderTexture.active = null;
                rt.Release(); DestroyImmediate(rt); rt = null;
            }
            if (compose != null) { DestroyImmediate(compose); compose = null; }
        }
    }
}
