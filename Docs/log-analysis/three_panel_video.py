# -*- coding: utf-8 -*-
"""3 面比較の**動画版**。元動画 / キーポイント可視化 / モデル配置 を横に並べて mp4 にする。

2026-09-10 のユーザー要望。`three_panel.py` は指定フレームの静止画を並べるので、
**動きの追従がずれているのか、その瞬間だけずれているのか**が分からない。
連続再生できるようにしたのがこれ。

静止画版との違いは 2 つだけ:

  1. **切り出し枠を時間方向に平滑化する。**毎フレーム独立に人物へ寄せると
     枠が暴れて見られたものにならない。中心と大きさを移動平均で均し、
     縦横比を固定して全フレーム同じ画素数で出す（動画にするため必須）
  2. キーポイントが落ちたフレームは**直前の枠を使い続ける**（枠が飛ばないように）

必要なもの:
  - `[KP2D]` を含むバッチのログ（`-diagLogs true`）
  - 除去前の動画から切り出した連番（`prerem/fNNNNN.png`、左目 1280x640）
  - モデルのキャプチャ連番（`cap_<tag>/fNNNNN.png`、`-captureFrames 0-300` などで撮る）

使い方:
  python three_panel_video.py <log> <preremDir> <capDir> <out.mp4> <from-to> [fps]

例:
  python three_panel_video.py human.log prerem cap_video 3panel.mp4 0-300
"""
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from three_panel import EDGES, EYE_H, EYE_W, load_kp, model_frame, person_box  # noqa: E402

FONT = "C:/Windows/Fonts/meiryo.ttc"
COL_W = 520          # 1 列の幅
SMOOTH_WIN = 31      # 枠の移動平均の窓（フレーム）。奇数


def draw_skeleton(im, parts, idx, color, radius=4, width=3):
    d = ImageDraw.Draw(im)
    for a, b in EDGES:
        if a in parts and b in parts:
            d.line([(parts[a][idx], parts[a][idx + 1]),
                    (parts[b][idx], parts[b][idx + 1])], fill=color, width=width)
    for v in parts.values():
        x, y = v[idx], v[idx + 1]
        d.ellipse([x - radius, y - radius, x + radius, y + radius],
                  fill=color, outline=(255, 255, 255))
    return im


def smooth_boxes(frames, kp):
    """毎フレームの人物枠を出し、中心と大きさを移動平均で均して縦横比を固定する。

    キーポイントが無いフレームは直前の枠を引き継ぐ（枠が原点へ飛ぶのを防ぐ）。
    """
    raw = []
    last = None
    for f in frames:
        parts = kp.get(f)
        if parts:
            last = person_box(parts)
        raw.append(last if last else (0, 0, EYE_W, EYE_H))
    # 先頭側にキーポイントが無かった分を、最初に取れた枠で埋め直す
    first = next((b for b, f in zip(raw, frames) if kp.get(f)), raw[0])
    for i, f in enumerate(frames):
        if not kp.get(f) and raw[i] == (0, 0, EYE_W, EYE_H):
            raw[i] = first
        else:
            break

    cx = [(b[0] + b[2]) / 2 for b in raw]
    cy = [(b[1] + b[3]) / 2 for b in raw]
    w = [b[2] - b[0] for b in raw]
    h = [b[3] - b[1] for b in raw]

    def mov(v):
        half = SMOOTH_WIN // 2
        out = []
        for i in range(len(v)):
            lo, hi = max(0, i - half), min(len(v), i + half + 1)
            out.append(sum(v[lo:hi]) / (hi - lo))
        return out

    cx, cy, w, h = mov(cx), mov(cy), mov(w), mov(h)

    # **全フレームで同じ画素数にする。**動画にするので必須。
    # 平滑化後の最大の枠に縦横比を合わせ、以後その比を保ったまま拡縮する。
    aspect = max(hh / max(ww, 1) for ww, hh in zip(w, h))
    boxes = []
    for i in range(len(raw)):
        ww = max(w[i], h[i] / aspect)
        hh = ww * aspect
        ww, hh = min(ww, EYE_W), min(hh, EYE_H)
        ww = min(ww, hh / aspect)
        hh = ww * aspect
        x0 = min(max(0, cx[i] - ww / 2), EYE_W - ww)
        y0 = min(max(0, cy[i] - hh / 2), EYE_H - hh)
        boxes.append((int(x0), int(y0), int(x0 + ww), int(y0 + hh)))
    return boxes


def main():
    if len(sys.argv) < 6:
        print(__doc__)
        raise SystemExit(1)
    log, vid_dir, cap_dir, out, span = sys.argv[1:6]
    fps = sys.argv[6] if len(sys.argv) > 6 else "30"
    lo, hi = (int(x) for x in span.split("-"))
    frames = list(range(lo, hi + 1))

    kp = load_kp(log)
    # 素材が無いフレームは黙って落とす（キャプチャが飛ぶことがある）
    frames = [f for f in frames
              if os.path.exists(os.path.join(vid_dir, f"f{f:05d}.png"))
              and os.path.exists(os.path.join(cap_dir, f"f{f:05d}.png"))]
    if not frames:
        raise SystemExit("素材が 1 枚も見つからない。preremDir / capDir を確認して")
    print(f"{len(frames)} フレーム（f{frames[0]}-f{frames[-1]}）")

    boxes = smooth_boxes(frames, kp)
    labels = [("元動画（除去前）", (255, 220, 120)),
              ("meta.bin の keypoint", (60, 220, 120)),
              ("モデル配置", (255, 120, 120))]
    f20 = ImageFont.truetype(FONT, 20)
    f16 = ImageFont.truetype(FONT, 16)

    tmp = tempfile.mkdtemp(prefix="3panel_")
    pad, hdr = 10, 32
    sheet_size = None
    try:
        for i, f in enumerate(frames):
            parts = kp.get(f, {})
            base = Image.open(os.path.join(vid_dir, f"f{f:05d}.png")).convert("RGB")
            panels = [
                base.copy(),
                draw_skeleton(base.copy(), parts, 0, (60, 220, 120)),
                draw_skeleton(model_frame(os.path.join(cap_dir, f"f{f:05d}.png")),
                              parts, 2, (255, 120, 120)),
            ]
            box = boxes[i]
            bw, bh = box[2] - box[0], box[3] - box[1]
            ch = max(1, int(bh * COL_W / bw))
            panels = [p.crop(box).resize((COL_W, ch), Image.LANCZOS) for p in panels]

            if sheet_size is None:
                sheet_size = (COL_W * 3 + pad * 4, ch + hdr + pad)
            sheet = Image.new("RGB", sheet_size, (22, 22, 24))
            d = ImageDraw.Draw(sheet)
            for j, (lab, col) in enumerate(labels):
                d.text((pad + j * (COL_W + pad), 6), lab, font=f20, fill=col)
            d.text((sheet_size[0] - 150, 8), f"f{f}  {f / 30.0:.2f}s",
                   font=f16, fill=(200, 200, 200))
            for j, im in enumerate(panels):
                sheet.paste(im, (pad + j * (COL_W + pad), hdr))
            sheet.save(os.path.join(tmp, f"{i:05d}.png"))

        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        # yuv420p は幅・高さが偶数でないと通らないので pad で丸める
        cmd = ["ffmpeg", "-y", "-framerate", fps, "-i", os.path.join(tmp, "%05d.png"),
               "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
               "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", out]
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print("書き出した:", out, sheet_size)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
