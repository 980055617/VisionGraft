using UnityEngine;

public class ReplaceableModel : MonoBehaviour
{
    // 人体計測: 頭の中心は頭頂から身長の 6.5%、足首は 95.5% の位置にある。
    // よって Humanoid の Head ボーン〜足首の距離は身長の約 0.89 倍。
    private const float SkeletonSpanToHeightRatio = 0.89f;

    public Transform anchor;
    public float referenceHeightMeters = 0f;
    public float userScale = 1f;
    public bool alignToGround = true;
    public Vector3 baseLocalScale;

    // prefab が持っている向きの補正。**配置は root の world 回転を上書きする**ので、
    // ここに控えておかないと作者が付けた補正が実行時に消える。
    //
    // 2026-08-28: 06_DieselLocomotive が縦に立っていたのがこれ。prefab root に
    // X 軸 -90 度（長軸のローカル Y を -Z へ倒す）が入っているのに、
    // TrackPlacementWriter の SetPositionAndRotation で消えていた。
    // 全 75 モデル中、root に回転を持つのは 06_DieselLocomotive と 21_Donkey1.0 の 2 つだけ
    // （実測）。残りは恒等なので合成しても影響しない。
    public Quaternion baseLocalRotation = Quaternion.identity;
    public float baseHeightMeters;
    // Humanoid の骨格から推定した身長。Renderer の AABB には髪・靴・広げた腕が含まれ、
    // 骨格に対して相対的に大きくなる。AABB を bbox に合わせると骨格が bbox の 73% まで
    // 縮み、頭が本来より下がる（2026-08-07 実測。頭上のボールが浮く原因になっていた）。
    // 関節位置を映像に合わせるにはこちらを使う。Humanoid でない場合は 0。
    public float baseSkeletonHeightMeters;
    // AABB の (幅, 高さ)。配置スケールに使うのは高さ（.y）だけで、幅（.x）は使わない。
    // 幅は bind pose 固定の X 幅であり、prefab によって X 軸が体長だったり体幅だったりする
    // うえ、映像の bbox 幅は被写体の yaw で体長〜体幅の間を動くので、両者を比べても
    // 意味のある比率にならない（TrackModelPlacement.ResolveDesiredLocalScale のコメント参照）。
    public Vector2 baseBoundsSize;
    public float baseBottomOffsetLocal;

    private void Awake()
    {
        baseLocalScale = transform.localScale;
        baseLocalRotation = transform.localRotation;
        if (Quaternion.Angle(Quaternion.identity, baseLocalRotation) > 0.01f)
        {
            Debug.Log($"[Model] {name}: prefab の向き補正を保持します euler={baseLocalRotation.eulerAngles:F1}");
        }
        CaptureSkeletonHeight();
        if (referenceHeightMeters > 0f)
        {
            baseHeightMeters = referenceHeightMeters;
            return;
        }

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            baseHeightMeters = 0f;
            return;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 lossy = transform.lossyScale;
        float lossyY = lossy.y;
        baseHeightMeters = lossyY > 0f ? bounds.size.y / lossyY : bounds.size.y;
        float lossyX = lossy.x;
        float baseW = lossyX > 0f ? bounds.size.x / lossyX : bounds.size.x;
        baseBoundsSize = new Vector2(baseW, baseHeightMeters);
        baseBottomOffsetLocal = lossyY > 0f ? (transform.position.y - bounds.min.y) / lossyY : 0f;
    }

    // Humanoid の bind pose で Head ボーン〜足首を測り、身長に換算して保持する。
    // Awake の時点ではまだ姿勢が適用されていないので prefab 本来の比率が取れる。
    private void CaptureSkeletonHeight()
    {
        baseSkeletonHeightMeters = 0f;
        Animator animator = GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman)
        {
            return;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        Transform foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        if (foot == null)
        {
            foot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        }

        if (head == null || foot == null)
        {
            return;
        }

        float lossyY = transform.lossyScale.y;
        float span = Vector3.Distance(head.position, foot.position);
        if (lossyY > 0f)
        {
            span /= lossyY;
        }

        if (span > 0.0001f)
        {
            baseSkeletonHeightMeters = span / SkeletonSpanToHeightRatio;
        }
    }

    // 配置スケールの基準となる高さ。Humanoid では骨格由来を優先する。
    // bbox（映像の人物のシルエット）に合わせる対象を「メッシュの外形」ではなく
    // 「骨格」にすることで、関節位置が映像と一致する。髪や靴のぶんシルエットは
    // わずかにはみ出すが、接触の再現にはこちらが正しい。
    public float GetModelHeightMeters()
    {
        return baseSkeletonHeightMeters > 0f ? baseSkeletonHeightMeters : baseHeightMeters;
    }
}
