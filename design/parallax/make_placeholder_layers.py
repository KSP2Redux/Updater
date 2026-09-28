import sys
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

SRC = "src/Ksp2Redux.Tools.Launcher/Assets/background.png"
OUT = "src/Ksp2Redux.Tools.Launcher/Assets/Parallax"
PREVIEW = sys.argv[1] if len(sys.argv) > 1 else None

CX, CY, R = 1417.2, 1089.8, 521.4
GLOW = 70
BBOX = (1025, 435, 1945, 935)

rgb = np.asarray(Image.open(SRC).convert("RGB")).astype(np.float32) / 255.0
H, W, _ = rgb.shape
yy, xx = np.mgrid[0:H, 0:W]
dist = np.hypot(xx - CX, yy - CY)
in_disc = dist <= R - 1

mx = rgb.max(-1)
mn = rgb.min(-1)
sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0)
r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
hue = np.degrees(np.arctan2(np.sqrt(3) * (g - b), 2 * r - g - b)) % 360
orange = (hue > 4) & (hue < 40) & (sat > 0.38) & (mx > 0.36)
yellow = (hue >= 40) & (hue < 75) & (mx > 0.3)

x0, y0, x1, y1 = BBOX
box = np.zeros((H, W), bool)
box[y0:y1, x0:x1] = True

station = box & (
    (in_disc & ~orange) |
    (in_disc & yellow) |
    (~in_disc & (((sat < 0.42) & (mx > 0.075)) | yellow))
)


def keep_large_components(mask, min_size):
    out = np.zeros_like(mask)
    seen = np.zeros_like(mask)
    ys, xs = np.nonzero(mask)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        comp = []
        q = deque([(sy, sx)])
        seen[sy, sx] = True
        while q:
            cy, cx = q.popleft()
            comp.append((cy, cx))
            for ny in (cy - 1, cy, cy + 1):
                for nx in (cx - 1, cx, cx + 1):
                    if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
        if len(comp) >= min_size:
            cy_, cx_ = zip(*comp)
            out[list(cy_), list(cx_)] = True
    return out


def morph(mask, size, op):
    img = Image.fromarray((mask * 255).astype(np.uint8))
    img = img.filter(ImageFilter.MaxFilter(size) if op == "dilate" else ImageFilter.MinFilter(size))
    return np.asarray(img) > 127


limb = (dist > R - 12) & (dist < R + GLOW)
station &= ~limb | (sat < 0.2) | yellow
station = morph(morph(station, 3, "dilate"), 3, "erode")
station = keep_large_components(station, 60)

alpha_station = np.asarray(
    Image.fromarray((station * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8))
).astype(np.float32) / 255.0
station_rgba = np.dstack([rgb, alpha_station])
station_rgba[alpha_station == 0, :3] = 0

hole = morph(station, 7, "dilate")


def box1d(a, r, axis):
    pad = [(0, 0)] * a.ndim
    pad[axis] = (r + 1, r)
    c = np.cumsum(np.pad(a, pad, mode="edge"), axis=axis, dtype=np.float64)
    hi = np.take(c, range(2 * r + 1, c.shape[axis]), axis=axis)
    lo = np.take(c, range(0, c.shape[axis] - 2 * r - 1), axis=axis)
    return ((hi - lo) / (2 * r + 1)).astype(np.float32)


def blur(a, s):
    r = max(1, int(s))
    for _ in range(3):
        a = box1d(box1d(a, r, 0), r, 1)
    return a


def smooth_fill(img, hole_mask, region):
    known = (~hole_mask & region).astype(np.float32)
    out = img.copy()
    todo = hole_mask.copy()
    for s in (3, 6, 12, 24, 48, 96):
        den = blur(known, s)
        ok = todo & (den > 0.08)
        for ch in range(3):
            num = blur(img[..., ch] * known, s)
            out[..., ch][ok] = (num / np.maximum(den, 1e-6))[ok]
        todo &= ~ok
    return out


filled_disc = smooth_fill(rgb, hole & in_disc, in_disc)
filled_out = smooth_fill(rgb, hole & ~in_disc, ~in_disc)
filled = np.where(in_disc[..., None], filled_disc, filled_out)
planet_rgb = np.where(hole[..., None], filled, rgb)

rng = np.random.default_rng(7)
grain = rng.normal(0, 0.012, planet_rgb.shape).astype(np.float32)
planet_rgb = np.where((hole & in_disc)[..., None], np.clip(planet_rgb + grain, 0, 1), planet_rgb)

core = dist <= R - 6
glow_band = ~core & (dist < R + GLOW)
glow_alpha = np.clip(planet_rgb.max(-1) * 1.6, 0, 1) * np.clip((R + GLOW - dist) / (GLOW + 6), 0, 1) ** 0.6
alpha_planet = np.where(core, 1.0, np.where(glow_band, glow_alpha, 0.0))
color = np.where(alpha_planet[..., None] > 0, planet_rgb / np.maximum(alpha_planet, 1e-3)[..., None], 0)
color = np.where(core[..., None], planet_rgb, np.clip(color, 0, 1))
planet_rgba = np.dstack([color, alpha_planet])


def save(arr, name):
    Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA").save(f"{OUT}/{name}", optimize=True)


import os
os.makedirs(OUT, exist_ok=True)
save(planet_rgba, "planet.png")
save(station_rgba, "station.png")


def over(dst, src):
    a = src[..., 3:4]
    return src[..., :3] * a + dst * (1 - a)


if PREVIEW is None:
    sys.exit()

bg = np.zeros((H, W, 3), np.float32)
for dx, dy, tag in [(0, 0, "aligned"), (-14, -8, "shifted")]:
    comp = over(bg, planet_rgba)
    shifted = np.roll(np.roll(station_rgba, dy, 0), dx, 1)
    comp = over(comp, shifted)
    Image.fromarray((comp * 255).astype(np.uint8)).save(f"{PREVIEW}/preview_{tag}.png")
Image.fromarray((planet_rgba[..., :3] * planet_rgba[..., 3:4] * 255).astype(np.uint8)).save(f"{PREVIEW}/preview_planet_only.png")
print("station px:", int(station.sum()), "hole px:", int(hole.sum()))
