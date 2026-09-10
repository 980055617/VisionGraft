# -*- coding: utf-8 -*-
"""姿勢の比較を「元動画 / キーポイント可視化 / モデル配置」の 3 枚で出す。

2026-09-08 のユーザー要望。2 枚（元動画とモデル）だけだと、ずれの原因が
**利用側（Unity）なのか供給側（bundle の keypoints）なのか**切り分けられない。
真ん中にデータそのものを置く。

必要なもの:
  - `[KP2D]` を含むバッチのログ（`-diagLogs true` で出る）
  - 除去前の動画から切り出したフレーム（`prerem/fNNNNN.png`）
  - モデルのキャプチャ（`cap_<tag>/fNNNNN.png`）

使い方:
  python three_panel.py <log> <preremDir> <capDir> <outPng> <f1,f2,...> [<capDir2> <名前1> <名前2>]

capDir2 を渡すと A/B になり、モデルの列が 2 つになる（キーポイントは真ん中のまま）。
"""
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

FONT = "C:/Windows/Fonts/meiryo.ttc"
EYE_W, EYE_H = 1280, 640

# 骨格の辺。[KP2D] が出す部位名でつなぐ。
EDGES = [
    ("Hips", "Neck"),
    ("Neck", "RUpArm"), ("RUpArm", "RLowArm"), ("RLowArm", "RHand"),
    ("Neck", "LUpArm"), ("LUpArm", "LLowArm"), ("LLowArm", "LHand"),
    ("Hips", "RUpLeg"), ("RUpLeg", "RLowLeg"), ("RLowLeg", "RFoot"),
    ("Hips", "LUpLeg"), ("LUpLeg", "LLowLeg"), ("LLowLeg", "LFoot"),
]

RX_HEAD = re.compile(r"\[KP2D\] f=(\d+) track=(\d+) bboxH=(\d+)")
RX_PART = re.compile(r"(\w+)=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)")


def load_kp(path):
    """フレームごとに最後の 1 行を採る。値は (srcU, srcV, mdlU, mdlV)。"""
    last = {}
    with open(path, "rb") as fh:
        for raw in fh:
            line = raw.decode("utf-8", "replace")
            m = RX_HEAD.search(line)
            if not m:
                continue
            parts = {}
            for name, a, b, c, d in RX_PART.findall(line):
                if name == "f":
                    continue
                parts[name] = (float(a), float(b), float(c), float(d))
            last[int(m.group(1))] = parts
    return last


def has_model_side(name):
    """`[KP2D]` の末尾に付く `*Src`（RHeelSrc / LHeelSrc）は**元映像だけ**の値で、
    モデル側は `0.0,0.0` の詰め物（C# 側のコメントに明記されている）。

    これを座標として扱うと、骨も枠も必ず原点 (0,0) を巻き込む。
    2026-09-10、枠が毎フレームほぼ全画面になっていたのがこれ。"""
    return not name.endswith("Src")


def draw_skeleton(im, parts, idx, color, radius=5, width=4):
    """idx=0 なら元映像の keypoint、idx=2 ならモデルの投影を描く。"""
    d = ImageDraw.Draw(im)
    for a, b in EDGES:
        if a in parts and b in parts:
            d.line([(parts[a][idx], parts[a][idx + 1]),
                    (parts[b][idx], parts[b][idx + 1])], fill=color, width=width)
    for name, v in parts.items():
        if idx != 0 and not has_model_side(name):
            continue
        x, y = v[idx], v[idx + 1]
        d.ellipse([x - radius, y - radius, x + radius, y + radius],
                  fill=color, outline=(255, 255, 255))
    return im


def model_frame(path):
    """キャプチャから映像面を取り出して 1280x640 に正規化する。

    映像部は 2:1（manifest の eye_w:eye_h）。明るい領域の外接矩形の幅から
    高さを決めて、下の UI バーを落とす。
    """
    im = Image.open(path).convert("RGB")
    g = np.asarray(im.convert("L"))
    ys, xs = np.where(g > 30)
    x0, x1, y0 = xs.min(), xs.max(), ys.min()
    w = x1 - x0 + 1
    return im.crop((x0, y0, x0 + w, y0 + w // 2)).resize((EYE_W, EYE_H), Image.LANCZOS)


def person_box(parts, margin=0.22):
    """src と mdl の両方の keypoint を囲む枠を返す。余白は枠の長辺に対する比。"""
    if not parts:
        return (0, 0, EYE_W, EYE_H)
    xs, ys = [], []
    for name, v in parts.items():
        xs.append(v[0])
        ys.append(v[1])
        if has_model_side(name):
            xs.append(v[2])
            ys.append(v[3])
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    m = max(x1 - x0, y1 - y0) * margin
    x0, x1, y0, y1 = x0 - m, x1 + m, y0 - m, y1 + m
    # 画面の外へ出ない範囲に収める（比率は崩さず平行移動で戻す）
    w, h = min(x1 - x0, EYE_W), min(y1 - y0, EYE_H)
    x0 = min(max(0, x0), EYE_W - w)
    y0 = min(max(0, y0), EYE_H - h)
    return (int(x0), int(y0), int(x0 + w), int(y0 + h))


def main():
    if len(sys.argv) < 6:
        print(__doc__)
        raise SystemExit(1)
    log, vid_dir, cap_dir, out, frames = sys.argv[1:6]
    cap_dir2 = sys.argv[6] if len(sys.argv) > 6 else None
    name1 = sys.argv[7] if len(sys.argv) > 7 else "モデル配置"
    name2 = sys.argv[8] if len(sys.argv) > 8 else "モデル配置（別）"
    frames = [int(x) for x in frames.split(",")]
    kp = load_kp(log)

    CW = 400
    cells = []
    for f in frames:
        parts = kp.get(f, {})
        base = Image.open(os.path.join(vid_dir, f"f{f:05d}.png")).convert("RGB")
        panels = [
            base.copy(),
            draw_skeleton(base.copy(), parts, 0, (60, 220, 120)),
            draw_skeleton(model_frame(os.path.join(cap_dir, f"f{f:05d}.png")),
                          parts, 2, (255, 120, 120)),
        ]
        if cap_dir2:
            # **2 本目には骨格を描かない。**ログは片方の実行のものなので、
            # もう片方に重ねると別の実行の骨を描くことになる。
            panels.append(model_frame(os.path.join(cap_dir2, f"f{f:05d}.png")))
        # **人物に寄せる。**1280x640 全体だと骨格が小さすぎて見えない
        # （2026-09-08、最初に作った版の失敗）。src と mdl の両方を囲む枠に余白を足す。
        box = person_box(parts)
        panels = [p.crop(box) for p in panels]
        bw = box[2] - box[0]
        bh = box[3] - box[1]
        cells.append([p.resize((CW, max(1, int(bh * CW / bw))), Image.LANCZOS) for p in panels])

    rh = cells[0][0].size[1]
    ncol = len(cells[0])
    pad, hdr, cap_h = 12, 46, 30
    sheet = Image.new("RGB", (CW * ncol + pad * (ncol + 1),
                              (rh + cap_h) * len(frames) + hdr + pad),
                      (22, 22, 24))
    d = ImageDraw.Draw(sheet)
    f20 = ImageFont.truetype(FONT, 20)
    f24 = ImageFont.truetype(FONT, 24)
    d.text((pad, 8), "元動画 / キーポイント可視化 / モデル配置", font=f24, fill=(240, 240, 240))

    labels = [("元動画（除去前）", (255, 220, 120)),
              ("meta.bin の keypoint", (60, 220, 120)),
              (name1, (255, 120, 120))]
    if cap_dir2:
        labels.append((name2, (140, 210, 255)))
    y = hdr
    for i, f in enumerate(frames):
        for j, (lab, col) in enumerate(labels):
            d.text((pad + j * (CW + pad), y + 4), f"f{f}  {lab}", font=f20, fill=col)
        y += cap_h
        for j, im in enumerate(cells[i]):
            sheet.paste(im, (pad + j * (CW + pad), y))
        y += rh

    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print("書き出した:", out, sheet.size)


if __name__ == "__main__":
    main()
