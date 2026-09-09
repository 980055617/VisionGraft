# -*- coding: utf-8 -*-
"""元映像の keypoint（緑）とモデルの骨（赤）を **同じ 1 枚に重ねる**。

2026-09-08: 「骨格の比は 1.039 なのに写真ではモデルが大きい」という食い違いを
切り分けるため。骨が重なるなら、大きく見える原因は骨格ではなく
**モデルの体つき（肩幅・頭の大きさ・手足の太さ）**にある。

使い方:
  python skeleton_overlay.py <log> <preremDir> <outPng> <f1,f2,...>
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from three_panel import EDGES, load_kp, person_box, EYE_W, EYE_H  # noqa: E402

FONT = "C:/Windows/Fonts/meiryo.ttc"


def draw(im, parts, idx, color, width=4, radius=5):
    d = ImageDraw.Draw(im)
    for a, b in EDGES:
        if a in parts and b in parts:
            d.line([(parts[a][idx], parts[a][idx + 1]),
                    (parts[b][idx], parts[b][idx + 1])], fill=color, width=width)
    for v in parts.values():
        x, y = v[idx], v[idx + 1]
        d.ellipse([x - radius, y - radius, x + radius, y + radius], fill=color)
    return im


def main():
    if len(sys.argv) < 5:
        print(__doc__)
        raise SystemExit(1)
    log, vid_dir, out, frames = sys.argv[1:5]
    frames = [int(x) for x in frames.split(",")]
    kp = load_kp(log)

    CW = 460
    cells = []
    for f in frames:
        parts = kp.get(f, {})
        im = Image.open(os.path.join(vid_dir, f"f{f:05d}.png")).convert("RGB")
        im = draw(im, parts, 0, (60, 230, 120))     # 元映像の keypoint
        im = draw(im, parts, 2, (255, 90, 90))      # モデルの骨
        box = person_box(parts, margin=0.14)
        im = im.crop(box)
        bw, bh = box[2] - box[0], box[3] - box[1]
        cells.append(im.resize((CW, max(1, int(bh * CW / bw))), Image.LANCZOS))

    rh = max(c.size[1] for c in cells)
    pad, hdr = 12, 78
    sheet = Image.new("RGB", (CW * len(cells) + pad * (len(cells) + 1), rh + hdr + pad),
                      (22, 22, 24))
    d = ImageDraw.Draw(sheet)
    d.text((pad, 8), "緑 = 元映像の keypoint / 赤 = モデルの骨（同じ画面座標に重ねた）",
           font=ImageFont.truetype(FONT, 24), fill=(240, 240, 240))
    f20 = ImageFont.truetype(FONT, 20)
    for i, c in enumerate(cells):
        d.text((pad + i * (CW + pad), 46), f"f{frames[i]}", font=f20, fill=(230, 230, 230))
        sheet.paste(c, (pad + i * (CW + pad), hdr))

    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print("書き出した:", out, sheet.size)


if __name__ == "__main__":
    main()
