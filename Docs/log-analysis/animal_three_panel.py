# -*- coding: utf-8 -*-
"""animal の 3 面比較（元動画 / キーポイント可視化 / モデル配置）。

`three_panel.py` は human 用で `[KP2D]` ログ（2D 画素）に依存する。
animal にはそのログが無いので、**`meta.bin` の keypoints3d を自分で投影する。**

投影は実装を読んで写した（記憶から再構成していない）:
  StreamingStereoVideoPlayer.Meta.cs
      jointsCam[p] = (xq, yq, zq) * quant_joint_scale  … カメラ空間・root 相対・メートル
  PinholePlacementSpace.ReconstructCamLocalFromEyePixel
      x = xNdc * z / fx,  y = yNdc * z / fy,  z = z
  manifest: camera_axes = x_right_y_up_z_forward, uv_origin = top_left

**root の距離は bundle からは正しく得られない**（anchor_z はメートルではない。D-004）。
最初は「投影の縦スパンが bbox 高に一致する z を二分探索」で合わせたが、
**この動画のように被写体が画面から見切れている場面では bbox が本体より小さく、
全身をそこへ押し込むので形が潰れた**（2026-09-10）。

そこで **弱透視 + 相似変換**にした。遠方（z=50m）で投影して形だけを取り出し、
**拡大率と平行移動を bbox に合わせる**。動物の奥行きは体長に対して小さいので
遠方投影でも形はほとんど変わらない。**これは形を見るための可視化で、
配置深度・スケールの主張ではない。**

使い方:
  python animal_three_panel.py <bundle.svb> <preremDir> <capDir> <out.png|out.mp4> <f1,f2|from-to> [fps]
"""
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFont

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
sys.path.insert(0, os.path.join(os.path.dirname(_HERE), "bundle-shared"))
from smal_read import load  # noqa: E402
from three_panel import model_frame  # noqa: E402

# SMAL rest skeleton（生成側から受領）。body_pose を当てて骨格を組むのに使う。
_REST = json.load(open(os.path.join(os.path.dirname(_HERE), "smal-rest-skeleton.json"),
                       encoding="utf-8"))
SMAL_J = [tuple(j["position"]) for j in _REST["joints"]]
SMAL_PARENT = [(-1 if j["parent"] is None else j["parent"]) for j in _REST["joints"]]
SMAL_EDGES = [(SMAL_PARENT[i], i) for i in range(1, len(SMAL_J)) if SMAL_PARENT[i] >= 0]

FONT = "C:/Windows/Fonts/meiryo.ttc"

# D-007 の対応表（AnimalHeadKeypoints.cs と [ANIMALKP] のペアから写した）。
# **注意**: 2026-08-28 のトポロジー復元では前肢の近位（12-8 / 13-9）と末端（14-3 / 15-4）が
# 復元された木（12-9、13-8）と食い違っている。ここは受領した対応表のほうを採る。
EDGES = [
    (7, 18),                       # 尾の付け根 → 頭（体軸）
    (18, 24), (18, 20), (18, 21),  # 鼻先・左右の耳
    (7, 25), (25, 19),             # しっぽ
    (12, 8), (8, 14), (14, 3),     # 左前肢
    (13, 9), (9, 15), (15, 4),     # 右前肢
    (7, 10), (10, 16), (16, 5),    # 左後肢
    (7, 11), (11, 17), (17, 6),    # 右後肢
    (18, 12), (18, 13),            # 頭 → 左右の肩
]


def intrinsics(m):
    ew, eh = m["eye_w"], m["eye_h"]
    if m.get("fx_norm", 0) > 0 and m.get("fy_norm", 0) > 0:
        fx, fy = m["fx_norm"], m["fy_norm"]
    else:
        fovx = math.radians(m["fovx_deg"])
        fx = 1.0 / math.tan(fovx * 0.5)
        fy = fx * (ew / float(eh))

    def pn(v, dim):
        return 0.5 if not v or v <= 0 else (v / dim if v > 1 else v)

    return ew, eh, fx, fy, pn(m.get("cx", 0), ew), pn(m.get("cy", 0), eh)


def project(o, joints, K, z_root):
    """root 深度 z_root を仮定して 26 点を画素に落とす。"""
    ew, eh, fx, fy, cx, cy = K
    rx = ((o["anchorU"] / ew) - cx) * 2.0 * z_root / fx
    ry = (cy - (o["anchorV"] / eh)) * 2.0 * z_root / fy
    out = []
    for (jx, jy, jz) in joints:
        x, y, z = rx + jx, ry + jy, z_root + jz
        if z <= 1e-6:
            out.append(None)
            continue
        out.append((((x * fx / (2.0 * z)) + cx) * ew,
                    (cy - (y * fy / (2.0 * z))) * eh))
    return out


WEAK_PERSPECTIVE_Z = 50.0


def shape_pts(o, joints, K, vis):
    """形だけを取り出して bbox に合わせる（弱透視 + 相似変換）。

    可視な点の外接矩形を bbox の外接矩形へ、**縦横比を保ったまま**写す。
    bbox が見切れている場面では本体より小さいので、絶対スケールは信用しないこと。
    """
    pts = project(o, joints, K, WEAK_PERSPECTIVE_Z)
    idx = [i for i, p in enumerate(pts) if p and vis[i]]
    if len(idx) < 4:
        return pts
    xs = [pts[i][0] for i in idx]
    ys = [pts[i][1] for i in idx]
    w = max(max(xs) - min(xs), 1e-6)
    h = max(max(ys) - min(ys), 1e-6)
    s = min(o["bboxW"] / w, o["bboxH"] / h)
    cx0, cy0 = (min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0
    cx1 = o["bboxX"] + o["bboxW"] / 2.0
    cy1 = o["bboxY"] + o["bboxH"] / 2.0
    return [None if p is None else ((p[0] - cx0) * s + cx1, (p[1] - cy0) * s + cy1)
            for p in pts]


def smal_fk_points(rots):
    """body_pose を rest skeleton に当てて 35 関節の 3D 位置を出す（global_orient 込み）。

    G[j] = G[parent] * R[j]、位置は親から rest のオフセットを回して積む。
    rots[0] = global_orient、rots[j] = body_pose[j-1]（AnimalSmalFkApplier と同じ対応）。
    """
    def mm(a, b):
        return [sum(a[i * 3 + k] * b[k * 3 + j] for k in range(3))
                for i in range(3) for j in range(3)]

    def ap(m, v):
        return tuple(sum(m[i * 3 + k] * v[k] for k in range(3)) for i in range(3))

    n = len(SMAL_J)
    R = [None] * n
    T = [None] * n
    # **global_orient は Y 反転を通す。**meta.bin は Y 下向きのカメラ規約で入っており、
    # StreamingStereoVideoPlayer の TryReadRotationMatrixFromBin が
    # flipCameraY:true（行列の 2 行目の符号反転 = 左から diag(1,-1,1)）を掛けている。
    # body_pose 側は flipCameraY:false なのでそのまま。
    # 2026-09-10: これを写し忘れて骨格の向きが上下反転していた（ユーザー指摘）。
    go = list(rots[0])
    go[3] = -go[3]
    go[4] = -go[4]
    go[5] = -go[5]
    R[0] = go
    T[0] = (0.0, 0.0, 0.0)
    for j in range(1, n):
        p = SMAL_PARENT[j]
        R[j] = mm(R[p], rots[j] if j < len(rots) else [1, 0, 0, 0, 1, 0, 0, 0, 1])
        off = tuple(SMAL_J[j][k] - SMAL_J[p][k] for k in range(3))
        dv = ap(R[p], off)
        T[j] = tuple(T[p][k] + dv[k] for k in range(3))
    return T


def fit_to_bbox(pts2d, o):
    """縦横比を保ったまま bbox に合わせる（拡大率と平行移動のみ）。"""
    idx = [i for i, p in enumerate(pts2d) if p]
    if len(idx) < 4:
        return pts2d
    xs = [pts2d[i][0] for i in idx]
    ys = [pts2d[i][1] for i in idx]
    w = max(max(xs) - min(xs), 1e-6)
    h = max(max(ys) - min(ys), 1e-6)
    sc = min(o["bboxW"] / w, o["bboxH"] / h)
    cx0, cy0 = (min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0
    cx1 = o["bboxX"] + o["bboxW"] / 2.0
    cy1 = o["bboxY"] + o["bboxH"] / 2.0
    return [None if p is None else ((p[0] - cx0) * sc + cx1, (p[1] - cy0) * sc + cy1)
            for p in pts2d]


def draw_edges(im, pts, edges, color, vis=None):
    d = ImageDraw.Draw(im)
    for a, b in edges:
        if 0 <= a < len(pts) and 0 <= b < len(pts) and pts[a] and pts[b]                 and (vis is None or (vis[a] and vis[b])):
            d.line([pts[a], pts[b]], fill=color, width=3)
    for i, p in enumerate(pts):
        if p and (vis is None or vis[i]):
            d.ellipse([p[0] - 4, p[1] - 4, p[0] + 4, p[1] + 4],
                      fill=color, outline=(255, 255, 255))
    return im


def draw(im, pts, vis, color=(60, 220, 120)):
    d = ImageDraw.Draw(im)
    for a, b in EDGES:
        if a < len(pts) and b < len(pts) and pts[a] and pts[b] and vis[a] and vis[b]:
            d.line([pts[a], pts[b]], fill=color, width=3)
    for i, p in enumerate(pts):
        if p and vis[i]:
            d.ellipse([p[0] - 4, p[1] - 4, p[0] + 4, p[1] + 4],
                      fill=color, outline=(255, 255, 255))
    return im


def build_sheet(f, o, pts, vid_dir, cap_dir, cw, smal_pts=None):
    base = Image.open(os.path.join(vid_dir, "f%05d.png" % f)).convert("RGB")
    panels = [base.copy(), draw(base.copy(), pts, o["vis"])]
    if smal_pts is not None:
        panels.append(draw_edges(base.copy(), smal_pts, SMAL_EDGES, (255, 200, 60)))
    panels.append(model_frame(os.path.join(cap_dir, "f%05d.png" % f)))
    h = cw // 2
    panels = [p.resize((cw, h), Image.LANCZOS) for p in panels]
    n = len(panels)
    pad, hdr, cap = 10, 38, 24
    sh = Image.new("RGB", (cw * n + pad * (n + 1), hdr + cap + h + pad), (22, 22, 24))
    d = ImageDraw.Draw(sh)
    d.text((pad, 8), "animal  元動画 / キーポイント可視化 / モデル配置",
           font=ImageFont.truetype(FONT, 20), fill=(240, 240, 240))
    f15 = ImageFont.truetype(FONT, 15)
    labels = [("元動画（除去前）", (255, 220, 120)),
              ("keypoints3d を投影", (60, 220, 120))]
    if n == 4:
        labels.append(("body_pose から組んだ骨格", (255, 200, 60)))
    labels.append(("モデル配置", (255, 120, 120)))
    for i, (lab, col) in enumerate(labels):
        d.text((pad + i * (cw + pad), hdr), "f%d %.1fs  %s" % (f, f / 30.0, lab),
               font=f15, fill=col)
        sh.paste(panels[i], (pad + i * (cw + pad), hdr + cap))
    return sh


def main():
    if len(sys.argv) < 6:
        print(__doc__)
        raise SystemExit(1)
    bundle, vid_dir, cap_dir, out, spec = sys.argv[1:6]
    fps = sys.argv[6] if len(sys.argv) > 6 else "15"
    b = load(bundle)
    K = intrinsics(b["manifest"])
    qj = b["qjoint"]

    if "-" in spec:
        lo, hi = (int(x) for x in spec.split("-"))
        frames = list(range(lo, hi + 1))
    else:
        frames = [int(x) for x in spec.split(",")]
    frames = [f for f in frames
              if os.path.exists(os.path.join(vid_dir, "f%05d.png" % f))
              and os.path.exists(os.path.join(cap_dir, "f%05d.png" % f))]
    if not frames:
        raise SystemExit("素材が無い。preremDir / capDir を確認して")

    video = out.lower().endswith(".mp4")
    cw = 480 if video else 430
    sheets = []
    for f in frames:
        objs = [o for o in b["frames"][f] if "joints" in o]
        if not objs:
            continue
        o = max(objs, key=lambda x: x["bboxW"] * x["bboxH"])
        joints = [(j[0] * qj, j[1] * qj, j[2] * qj) for j in o["joints"]]
        pts = shape_pts(o, joints, K, o["vis"])
        smal_pts = None
        if "smal" in o:
            T = smal_fk_points(o["smal"]["rots"])
            smal_pts = fit_to_bbox(project(o, T, K, WEAK_PERSPECTIVE_Z), o)
        sheets.append(build_sheet(f, o, pts, vid_dir, cap_dir, cw, smal_pts))

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    if video:
        tmp = tempfile.mkdtemp(prefix="a3p_")
        try:
            for i, sh in enumerate(sheets):
                sh.save(os.path.join(tmp, "%05d.png" % i))
            subprocess.run(["ffmpeg", "-y", "-framerate", fps,
                            "-i", os.path.join(tmp, "%05d.png"),
                            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", out],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
    else:
        w, h = sheets[0].size
        sheet = Image.new("RGB", (w, h * len(sheets)), (22, 22, 24))
        for i, sh in enumerate(sheets):
            sheet.paste(sh, (0, i * h))
        sheet.save(out)
    print("書き出した:", out, len(sheets), "フレーム")


if __name__ == "__main__":
    main()
