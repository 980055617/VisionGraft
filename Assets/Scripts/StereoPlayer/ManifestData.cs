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
}
