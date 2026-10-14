using System;

[Serializable]
public sealed class ManifestData
{
    public int width;
    public int height;
    public int eye_w;
    public int eye_h;
    public int num_frames;
    public float fps;
    public float fovx_deg;
    public float quant_pos_scale;
    public float quant_joint_scale;
    public string joints_space;
    public string joints_source;
    public string camera_axes;
    public string uv_origin;
    public float fx_norm;
    public float fy_norm;
    public float cx;
    public float cy;
    public float fovy_deg;
    public float fovy;
    // 生成側が深度の規約を明示するために 2026-08-06 に追加したブロック。
    // このキーを持つ bundle は meta.bin の anchor_z が larger=farther に統一されている。
    // 持たない旧 bundle は larger=nearer なので、有無で向きを判定する
    // （IsAnchorDepthLargerMeansFarther）。
    public DepthPolicyData depth_policy;

    // 元動画のファイル名。**動画の同一性キーとして使う**（TrackCustomizationStore）。
    // bundle のファイル名は再生成で変わる（bundle_animal.svb ->
    // bundle_animal_shots_depthdriftfix_shotsfix.svb）が、inputs.video_mp4 は
    // 再生成をまたいで同じことを実測で確認済み（2026-08-28、docs/model-selection-persistence.md）。
    public ManifestInputsData inputs;

    // 片目の画が元の画（推定器が走った画）のどこか（2026-10-14、animalBottomClippedHeadPin の判定 IsAnimalHeadPinCanvasEye だけが読む）。
    // bundle_animal・human・car: left_eye_origin = "full"、crop_fraction の 4 つ = 1.0。bundle.svb・bundle_tutorial は crop_fraction を持たない（null）。
    public string left_eye_origin;
    public ManifestCropFractionData crop_fraction;
}

[Serializable]
public sealed class ManifestCropFractionData
{
    public float crop_w_fraction;
    public float crop_h_fraction;
    public float eye_w_fraction;
    public float eye_h_fraction;
}

[Serializable]
public sealed class ManifestInputsData
{
    public string video_mp4;
}

[System.Serializable]
public sealed class DepthPolicyData
{
    // convention / near_far_direction は depth_npz そのものの規約（0=far, 1=near）を指す。
    // meta.bin の anchor_z は生成側で反転済みの別量なので、この向きを anchor_z に
    // 適用してはいけない（manifest の unrelated_to_anchor_z に明記されている）。
    public string convention;
    public string near_far_direction;
    public string normalization;
    // DepthCrafter が正規化前に出した min/max。DepthCrafter は affine-invariant
    // (disp_raw ≈ a/Z + b, a,b は clip ごとに未知) なので、この 2 値だけでは
    // 絶対距離に較正できない。古い depth_npz から作られた bundle では 0 のまま。
    public float disp_min;
    public float disp_max;
}
