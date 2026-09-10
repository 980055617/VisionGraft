# -*- coding: utf-8 -*-
"""`anchor_z` を Unity の**配置深度（m）**へ直す。実装を読んで移植したもの。

移植元（記憶から再構成していない。読んで写した）:
  Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.Meta.cs
      CalibrateAnchorDepthRange        … 2〜98 パーセンタイル、120 点サンプリング
      anchorZ01 = anchorZq * quant_pos_scale
  Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.Manifest.partial.cs
      NormalizeAnchorZ01 / Z01ToNearness / TryResolveNearnessRange
      ResolvePopoutFraction / DecodeAnchorDepthMetersFromBundle

シーンの serialize 値（TrialScene.unity / TestScene.unity）を既定にしてある。
既定値ではなくシーンの値を使うこと（CLAUDE.md）。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from train_depth_order import _load_full  # noqa: E402

EPSILON_M = 0.02          # EpsilonMeters
MIN_FROM_HEAD_M = 0.25    # MinDistanceFromHeadMeters
DISPARITY_MIN = 0.01      # AnchorDisparityMinimum
RANGE_MIN_SPAN = 0.02     # AnchorDepthRangeMinimumSpan
CALIB_SAMPLES = 120       # AnchorDepthCalibrationSampleCount


def clamp01(v):
    return 0.0 if v < 0.0 else (1.0 if v > 1.0 else v)


class PlacementDepth:
    """1 つの bundle について、配置深度への変換を再現する。"""

    def __init__(self, path, screen_dist=1.0, popout_range=0.35, normalize=False):
        d = _load_full(path)
        self.frames = d["frames"]
        self.qpos = d["qpos"]
        self.manifest = d["manifest"]
        self.screen_dist = max(0.001, screen_dist)
        self.popout_range = max(0.0, popout_range)
        self.normalize = normalize
        # depth_policy.convention があれば larger=farther（IsAnchorDepthLargerMeansFarther）
        dp = self.manifest.get("depth_policy") or {}
        self.larger_is_farther = bool(dp.get("convention"))
        self._calibrate()

    def _calibrate(self):
        n = len(self.frames)
        step = max(1, n // CALIB_SAMPLES)
        samples = []
        for fi in range(0, n, step):
            for o in self.frames[fi]:
                samples.append(o["anchorZq"] * self.qpos)
        self.has_range = False
        self.range_min, self.range_max = 0.0, 1.0
        if len(samples) < 8:
            return
        samples.sort()
        lo = samples[min(max(round(len(samples) * 0.02), 0), len(samples) - 1)]
        hi = samples[min(max(round(len(samples) * 0.98), 0), len(samples) - 1)]
        if hi - lo < RANGE_MIN_SPAN:
            return
        self.range_min, self.range_max, self.has_range = lo, hi, True

    def normalize_z01(self, z01):
        if not self.normalize or not self.has_range:
            return z01
        span = self.range_max - self.range_min
        if span < 0.0001:
            return z01
        return clamp01((z01 - self.range_min) / span)

    def nearness(self, z01):
        return (1.0 - z01) if self.larger_is_farther else z01

    def _nearness_range(self):
        if not self.has_range:
            return None
        a = self.nearness(self.normalize_z01(self.range_min))
        b = self.nearness(self.normalize_z01(self.range_max))
        lo, hi = min(a, b), max(a, b)
        return (lo, hi) if hi - lo >= 0.0001 else None

    def popout_fraction(self, near):
        r = self._nearness_range()
        if r is None:
            return clamp01(near)
        d_min, d_max = r
        if d_min <= DISPARITY_MIN:
            return clamp01(near)
        d = min(max(near, d_min), d_max)
        inv_near, inv_far = 1.0 / d_max, 1.0 / d_min
        farness = (1.0 / d - inv_near) / (inv_far - inv_near)
        return 1.0 - clamp01(farness)

    def depth_m(self, z01):
        z = self.normalize_z01(clamp01(z01))
        popout = self.popout_range * self.popout_fraction(self.nearness(z))
        zp = self.screen_dist - EPSILON_M - popout
        zp = max(zp, max(0.001, MIN_FROM_HEAD_M))
        zp = min(zp, self.screen_dist - 0.0001)
        return max(0.001, zp)

    def depth_of(self, obj):
        return self.depth_m(obj["anchorZq"] * self.qpos)
