using System.Collections.Generic;
using UnityEngine;

// 男性（17）の口パク（2026-10-27、ユーザー「サッカーについては音声に合わせて口パクさせれる？」
// 「男性の口パクについては，今回の被験者実験だけの合わせなのでbundleデータ追加で増やさないようにして，また，モデルに関してはblenderでうまく調整して動かせるようにして」）。
// サッカーの動画（bundle_soccer）の声は 2 か所の笑いだけ（9.2〜10.0 秒と 24.0〜25.0 秒。ほかの音はボールに触る 0.1 秒の音）。
// 声の大きさ（300〜3400 Hz の RMS、10 ms ごと）から「は・は・は」の区切りを 5 つずつ読み、この表に決め打ちで持つ（bundle には足さない。
// scratchpad mouth17/tools/envelope.py）。元の動画では笑いの間ずっと口が開いている（9.2〜9.9 秒は歯が見える、24.1〜24.9 秒は大きく開く）ので、
// 笑いの区間は土台の角度で開けたままにし、区切りごとに山の角度を足す。
// 口を開けるのは、頭の子にあごの骨 Jaw（その子に JawTip）があるモデルだけ（Blender で口を開けられるように作った 17、
// scratchpad mouth17/tools/build_mouth4.py）。骨が無いモデル（今の 17・女性 19 など）は何もしない。
// あごの骨は Jaw（開く角度そのまま）と、同じ付け根で開く角度の NN% だけ回る Jaw_NN（頭の子、2.5% 刻み）。皮膚の頂点の頭の分を、その頂点を
// あごと一緒に動かしたい割合に一番近い骨へ移してある（Quest の 1 頂点 2 本のまま、首の分を変えずに口の両わき・あごの角をなめらかに動かすため。
// 口を閉じていればどの骨も頭と同じなので、閉じた形は元の 17 と同じ）。
// あごは「頭の今の world × 蝶番まわりの開き × あごの bind の局所」を毎 tick ApplyWorldRotation で書く（今の回転に積み重ねない、AnimalCarry と同じ形）。
// 蝶番の軸は頭の上（head_end − Head）とあごの向き（JawTip − Jaw）の外積で、+ に回すとあごの先が頭の上から離れる（開く）向きにそろえる。
// 動画のフレームは 1/24 秒刻みなので、フレームが変わってからの時間を足して 72 Hz でなめらかにする（1 フレーム分まで）。
// 音が無い間は閉じる: 動画が止まっている（被験者の A・身ぶりで止めた間）・身ぶりの前後の音のフェード（音量の割合を掛ける）。
public partial class StreamingStereoVideoPlayer
{
    public bool humanLipSyncHardcoded = true;
    public bool logHumanLipSync = false;

    internal sealed class HumanLipSyncLaugh
    {
        // 口を開け始める・閉じ終わる（動画の秒）。始まりと終わりは LipSyncRampSeconds でなめらかに。
        public float from;
        public float to;
        // 笑いの間ずっと開けておく角度と、声の区切りの山で足す角度（度）。
        public float baseDeg;
        public float peakDeg;
        // 声の区切りの始まり・終わり（秒）の組を並べたもの。
        public float[] syllables;
    }

    private sealed class HumanLipSyncEntry
    {
        public string bundle;
        public uint humanTrack;
        public HumanLipSyncLaugh[] laughs;
    }

    // サッカーの動画（24 fps、30 秒）。声の区切り（声の帯域の RMS が −30 dB を超える区間）:
    //   9.23〜9.31 / 9.36〜9.44 / 9.48〜9.55 / 9.57〜9.65 / 9.70〜9.90（区切りの間も −38〜−56 dB で途切れない）
    //   24.16〜24.22 / 24.32〜24.39 / 24.50〜24.57 / 24.68〜24.77 / 24.88〜24.95（24.02〜24.14 は −40〜−50 dB の吸う音、区切りの間は −50 dB）
    // 開ける区間は元の動画の顔に合わせて声の前後へ広げた（元の顔は 10.2 秒・25.1 秒も笑って歯が見え、23.8 秒は閉じている。
    // 声が終わってすぐ閉じると、元の顔がまだ笑っている所で閉じた。10/27 の撮影 mouth17/compose/sheet_572.jpg の 25.08 秒）。
    private static readonly HumanLipSyncEntry[] HumanLipSyncEntries =
    {
        new HumanLipSyncEntry
        {
            bundle = "bundle_soccer.svb",
            humanTrack = 0,
            laughs = new[]
            {
                new HumanLipSyncLaugh
                {
                    from = 9.00f, to = 10.30f, baseDeg = 4.5f, peakDeg = 5.5f,
                    syllables = new[] { 9.23f, 9.31f, 9.36f, 9.44f, 9.48f, 9.55f, 9.57f, 9.65f, 9.70f, 9.90f },
                },
                new HumanLipSyncLaugh
                {
                    from = 23.95f, to = 25.35f, baseDeg = 8f, peakDeg = 7f,
                    syllables = new[] { 24.16f, 24.22f, 24.32f, 24.39f, 24.50f, 24.57f, 24.68f, 24.77f, 24.88f, 24.95f },
                },
            },
        },
    };

    private const float LipSyncRampSeconds = 0.15f;
    // 区切りの山: 声の始まりの少し前から開き始め、終わりの少し後に土台へ戻る（区切りの間の 30〜60 ms の切れ目で少し閉じる速さ）。
    private const float LipSyncLeadSeconds = 0.02f;
    private const float LipSyncAttackSeconds = 0.04f;
    private const float LipSyncReleaseSeconds = 0.06f;
    private const float LipSyncMaxDeg = 16f;
    // 動画が止まった・動き出したときに口を閉じる・開ける速さ（秒）。
    private const float LipSyncPauseSeconds = 0.15f;

    private sealed class HumanLipSyncRig
    {
        public bool resolved;
        public bool valid;
        public Transform head;
        public Transform jaw;
        // Jaw と Jaw_NN（開く角度に掛ける割合と bind の局所の回転）。
        public readonly List<Transform> jawBones = new List<Transform>();
        public readonly List<float> jawFractions = new List<float>();
        public readonly List<Quaternion> jawBindLocals = new List<Quaternion>();
        public Vector3 hingeAxisHeadLocal;
        public bool jawWritten;
        public float playingFactor = 1f;
        public int lastFrame = -1;
        public float frameStartTime;
        public int lastLoggedFrame = -1;
    }

    private readonly Dictionary<GameObject, HumanLipSyncRig> humanLipSyncRigs = new Dictionary<GameObject, HumanLipSyncRig>();

    // 純粋な計算（テスト HumanLipSyncTests）: 動画の時刻 t（秒）の口の開き（度）。笑いの区間の外は 0。
    internal static float ResolveHumanLipSyncDeg(HumanLipSyncLaugh[] laughs, float t)
    {
        if (laughs == null)
        {
            return 0f;
        }

        float deg = 0f;
        for (int i = 0; i < laughs.Length; i++)
        {
            HumanLipSyncLaugh laugh = laughs[i];
            if (laugh == null || t <= laugh.from || t >= laugh.to)
            {
                continue;
            }

            float ramp = Mathf.SmoothStep(0f, 1f, (t - laugh.from) / LipSyncRampSeconds) *
                         (1f - Mathf.SmoothStep(0f, 1f, (t - (laugh.to - LipSyncRampSeconds)) / LipSyncRampSeconds));
            float bump = 0f;
            if (laugh.syllables != null)
            {
                for (int k = 0; k + 1 < laugh.syllables.Length; k += 2)
                {
                    float rise = Mathf.SmoothStep(0f, 1f, (t - (laugh.syllables[k] - LipSyncLeadSeconds)) / LipSyncAttackSeconds);
                    float fall = 1f - Mathf.SmoothStep(0f, 1f, (t - laugh.syllables[k + 1]) / LipSyncReleaseSeconds);
                    bump = Mathf.Max(bump, rise * fall);
                }
            }

            deg = Mathf.Max(deg, ramp * (laugh.baseDeg + laugh.peakDeg * bump));
        }

        return Mathf.Min(LipSyncMaxDeg, deg);
    }

    // DisplayModelTick の最後（全 track の姿勢の後、犬のボールの後）に呼ぶ。
    private void ApplyHumanLipSyncForFrame(int frame)
    {
        if (!humanLipSyncHardcoded)
        {
            return;
        }

        string bundle = System.IO.Path.GetFileName(bundleFileName);
        for (int i = 0; i < HumanLipSyncEntries.Length; i++)
        {
            if (HumanLipSyncEntries[i].bundle == bundle)
            {
                ApplyHumanLipSync(HumanLipSyncEntries[i], frame);
            }
        }
    }

    private void ApplyHumanLipSync(HumanLipSyncEntry entry, int frame)
    {
        GameObject human = GetTrackInstanceOrNull(entry.humanTrack);
        if (human == null || !human.activeInHierarchy)
        {
            return;
        }

        HumanLipSyncRig rig = GetHumanLipSyncRig(human);
        if (rig == null || !rig.valid)
        {
            return;
        }

        float fps = metaHeader.fps > 0f ? metaHeader.fps : (manifest != null && manifest.fps > 0f ? manifest.fps : 24f);
        if (frame != rig.lastFrame)
        {
            rig.lastFrame = frame;
            rig.frameStartTime = Time.time;
        }

        float t = frame / fps + Mathf.Clamp(Time.time - rig.frameStartTime, 0f, 1f / fps);
        float deg = ResolveHumanLipSyncDeg(entry.laughs, t);
        float audio = motionAudioFade.CurrentPhase == InteractiveMotionAudioFade.Phase.Idle || motionAudioFade.BaseVolume <= 1e-4f
            ? 1f
            : Mathf.Clamp01(motionAudioFade.Volume / motionAudioFade.BaseVolume);
        float playing = vp != null && vp.isPlaying ? 1f : 0f;
        rig.playingFactor = Mathf.MoveTowards(rig.playingFactor, playing, Time.deltaTime / LipSyncPauseSeconds);
        deg *= audio * rig.playingFactor;

        if (deg > 0.01f)
        {
            Vector3 axis = rig.head.rotation * rig.hingeAxisHeadLocal;
            for (int i = 0; i < rig.jawBones.Count; i++)
            {
                TransformWriter.ApplyWorldRotation(rig.jawBones[i],
                    Quaternion.AngleAxis(deg * rig.jawFractions[i], axis) * rig.head.rotation * rig.jawBindLocals[i]);
            }

            rig.jawWritten = true;
        }
        else if (rig.jawWritten)
        {
            // 笑いの外・周の頭へ戻ったら一度だけ bind の局所の回転そのものへ戻す（頭 × bind の局所を world で書くと丸めが残り、
            // 口を閉じていても首の襟元の小さな日なたが今の 17 と違った。10/27 の撮影 mouth17/unity/runs/diag88_566.jpg）。
            // 姿勢の FK の流れ（ApplyWorldRotation だけを使う決まり）の外で、頭の子の重みの無い分の骨を元へ戻すだけ。
            for (int i = 0; i < rig.jawBones.Count; i++)
            {
                TransformWriter.ApplyLocalRotation(rig.jawBones[i], rig.jawBindLocals[i]);
            }

            rig.jawWritten = false;
        }

        if (logHumanLipSync && deg > 0.01f && frame != rig.lastLoggedFrame && frame % 3 == 0)
        {
            rig.lastLoggedFrame = frame;
            Debug.Log($"[LIPSYNC] f={frame} t={t:F3}s open={deg:F2}deg audio={audio:F2} playing={rig.playingFactor:F2} model={human.name}");
        }
    }

    private HumanLipSyncRig GetHumanLipSyncRig(GameObject human)
    {
        if (humanLipSyncRigs.TryGetValue(human, out HumanLipSyncRig rig) && rig.resolved && (!rig.valid || (rig.head != null && rig.jaw != null)))
        {
            return rig;
        }

        PruneDestroyedLipSyncEntries();
        Animator animator = human.GetComponentInChildren<Animator>(true);
        Transform head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
        Transform jaw = head != null ? head.Find("Jaw") : null;
        Transform tip = jaw != null ? jaw.Find("JawTip") : null;
        Transform top = head != null ? head.Find("head_end") : null;
        rig = new HumanLipSyncRig { resolved = true, valid = head != null && jaw != null && tip != null && top != null, head = head, jaw = jaw };
        humanLipSyncRigs[human] = rig;
        if (!rig.valid)
        {
            Debug.Log($"[LIPSYNC] model={human.name} は頭の子にあごの骨（Jaw・JawTip・head_end）が無い → 口を動かさない " +
                      $"head={(head != null ? head.name : "-")} jaw={(jaw != null ? jaw.name : "-")} tip={(tip != null ? tip.name : "-")} top={(top != null ? top.name : "-")}");
            return rig;
        }

        // あごの骨は誰も書かないので、最初に見たときの局所の回転が bind。軸は頭の局所で持つ（頭・あご・頭の上・あごの先は頭に対して動かない）。
        for (int i = 0; i < head.childCount; i++)
        {
            Transform child = head.GetChild(i);
            if (TryParseJawFraction(child.name, out float fraction))
            {
                rig.jawBones.Add(child);
                rig.jawFractions.Add(fraction);
                rig.jawBindLocals.Add(child.localRotation);
            }
        }

        Vector3 up = top.position - head.position;
        Vector3 jawDir = tip.position - jaw.position;
        Vector3 axis = Vector3.Cross(up, jawDir).normalized;
        Vector3 turned = Quaternion.AngleAxis(10f, axis) * jawDir;
        if (Vector3.Dot(turned - jawDir, up) > 0f)
        {
            axis = -axis;
        }

        rig.hingeAxisHeadLocal = Quaternion.Inverse(head.rotation) * axis;
        int innerSlots = DisableMouthInnerShadows(human, jaw);
        Debug.Log($"[LIPSYNC] rig model={human.name} head={head.name} jaw={jaw.name} tip={tip.name} top={top.name} jawBones={rig.jawBones.Count} " +
                  $"jawLen={jawDir.magnitude:F4}m axisHeadLocal={rig.hingeAxisHeadLocal.ToString("F3")} innerShadowOff={innerSlots}");
        return rig;
    }

    // 口の中（皮膚の内側の歯・袋・舌・唇の内側、メッシュの材質の枠 1 以降）は影を落とさない。外からは見えないが、影の計算に入ると
    // 口を閉じていても首の襟元の日なたが今の 17 と違った（口の中を外した版は差 0、10/27）。URP の Lit は「影を落とすか」の項目を持たず、
    // 不透明の材質は Editor が読み込むたびに影のパスを入れ直すので、材質のファイルでは切れない。実行時にその枠だけ材質を複製してパスを切る。
    // 返り値: 切った枠の数。
    private static int DisableMouthInnerShadows(GameObject human, Transform jaw)
    {
        int count = 0;
        foreach (SkinnedMeshRenderer r in human.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r == null || r.sharedMesh == null || r.sharedMesh.subMeshCount < 2 || System.Array.IndexOf(r.bones, jaw) < 0)
            {
                continue;
            }

            Material[] mats = r.sharedMaterials;
            for (int i = 1; i < mats.Length; i++)
            {
                if (mats[i] == null)
                {
                    continue;
                }

                var inner = new Material(mats[i]) { name = mats[i].name + " (no shadow)" };
                inner.SetShaderPassEnabled("ShadowCaster", false);
                mats[i] = inner;
                count++;
            }

            r.sharedMaterials = mats;
        }

        return count;
    }

    // あごの骨の名前から開く角度に掛ける割合: "Jaw" = 1、"Jaw_NN" = NN / 100（純粋な計算、テスト HumanLipSyncTests）。
    internal static bool TryParseJawFraction(string name, out float fraction)
    {
        fraction = 0f;
        if (name == "Jaw")
        {
            fraction = 1f;
            return true;
        }

        if (name != null && name.Length == 6 && name.StartsWith("Jaw_", System.StringComparison.Ordinal) &&
            int.TryParse(name.Substring(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int percent) &&
            percent > 0 && percent < 100)
        {
            fraction = percent / 100f;
            return true;
        }

        return false;
    }

    // モデルを替えるとインスタンスは壊して作り直される（ModelPicker）。壊れたインスタンスの控えを捨てる。
    private void PruneDestroyedLipSyncEntries()
    {
        var dead = new List<GameObject>();
        foreach (GameObject key in humanLipSyncRigs.Keys)
        {
            if (key == null)
            {
                dead.Add(key);
            }
        }

        foreach (GameObject key in dead)
        {
            humanLipSyncRigs.Remove(key);
        }
    }
}
