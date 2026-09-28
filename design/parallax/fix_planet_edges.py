import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

LIMB_PROFILE = Path(__file__).with_name("limb_profile.csv")

GLOW_COLOR = np.array([230, 145, 90], np.float32) / 255
BAND_START, BAND_FULL = -20.0, -8.0
GLOW_BASELINE_FROM = 40.0
GLOW_FADE_FROM, GLOW_FADE_TO = 18.0, 34.0
WEBP_QUALITY = 95


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float32) / 255


def reference_profile():
    table = np.loadtxt(LIMB_PROFILE, delimiter=",", skiprows=1, dtype=np.float32)
    return table[:, 0], table[:, 1:4]


def fit_surface_circle(img):
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    surface = (r > 120 / 255) & (r > g * 1.3) & (r > b * 1.7)
    h, w = surface.shape
    points = []
    for y in range(0, h, 4):
        xs = np.nonzero(surface[y])[0]
        if len(xs) and 5 < xs[0] and y < h - 30:
            points.append((xs[0], y))
    for x in range(0, w, 4):
        ys = np.nonzero(surface[:, x])[0]
        if len(ys) and 5 < ys[0] and x < w - 30:
            points.append((x, ys[0]))
    p = np.array(points, float)
    a = np.c_[2 * p[:, 0], 2 * p[:, 1], np.ones(len(p))]
    cx, cy, c = np.linalg.lstsq(a, (p ** 2).sum(1), rcond=None)[0]
    radius = math.sqrt(c + cx ** 2 + cy ** 2)
    keep = np.abs(np.hypot(p[:, 0] - cx, p[:, 1] - cy) - radius) < 4
    cx, cy, c = np.linalg.lstsq(a[keep], (p[keep] ** 2).sum(1), rcond=None)[0]
    return cx, cy, math.sqrt(c + cx ** 2 + cy ** 2)


def repair_bottom(img, inside):
    h = img.shape[0]
    rows = np.array([img[y, inside[y]].mean(axis=0) if inside[y].any() else np.zeros(3) for y in range(h)])
    reference = np.median(rows[h - 200:h - 100], axis=0)
    brightness = rows.mean(axis=1)
    dark = np.nonzero(brightness[h - 100:] < reference.mean() * 0.97)[0]
    if len(dark) == 0:
        return img, None, None
    dark_start = h - 100 + int(dark[0])
    jumps = np.diff(brightness[h - 30:])
    flat_start = h - 30 + int(np.argmax(jumps)) + 1 if jumps.max() > 0.02 else h

    out = img.copy()
    for y in range(dark_start, flat_start):
        out[y, inside[y]] = np.clip(img[y, inside[y]] * (reference / np.maximum(rows[y], 1e-3)), 0, 1)

    strip = h - flat_start
    for y in range(flat_start, h):
        copy = out[y - strip, inside[y]]
        blend = min(1.0, (y - flat_start + 1) / 4)
        out[y, inside[y]] = copy * blend + out[flat_start - 1, inside[y]] * (1 - blend) if y - flat_start < 3 else copy
    return out, dark_start, flat_start


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def main(source, target):
    img = load(source)
    h, w, _ = img.shape
    cx, cy, surface_r = fit_surface_circle(img)

    d_prof, profile = reference_profile()
    profile = np.clip(profile - profile[d_prof >= GLOW_BASELINE_FROM].mean(axis=0), 0, 1)
    yy, xx = np.mgrid[0:h, 0:w]
    d = np.hypot(xx - cx, yy - cy) - surface_r

    img, dark_start, flat_start = repair_bottom(img, d < 0)

    band = np.stack([np.interp(d, d_prof, profile[:, ch]) for ch in range(3)], axis=-1)
    weight = smoothstep(BAND_START, BAND_FULL, d)[..., None]
    color = img * (1 - weight) + band * weight

    glow_alpha = np.clip(band[..., 0] / GLOW_COLOR[0], 0, 1) * (1 - smoothstep(GLOW_FADE_FROM, GLOW_FADE_TO, d))
    alpha = np.where(d < -2, 1.0, glow_alpha)
    outside = d >= -2
    color[outside] = np.clip(band[outside] / np.maximum(alpha[outside], 1e-3)[..., None], 0, 1)
    color[alpha == 0] = 0

    rgba = np.dstack([color, alpha])
    image = Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")
    if target.lower().endswith(".webp"):
        image.save(target, quality=WEBP_QUALITY, method=6, exact=True)
    else:
        image.save(target, optimize=True)
    print(f"circle centre=({cx:.1f},{cy:.1f}) radius={surface_r:.1f} brightened rows {dark_start}-{flat_start} and refilled from {flat_start}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
