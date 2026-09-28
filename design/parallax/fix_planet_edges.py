import math
import sys

import numpy as np
from PIL import Image

REFERENCE = "src/Ksp2Redux.Tools.Launcher/Assets/background.png"
REF_CX, REF_CY, REF_R = 1417.2, 1089.8, 521.4
LIT_ANGLES = range(150, 236, 5)

GLOW_COLOR = np.array([230, 145, 90], np.float32) / 255
BAND_START, BAND_FULL = -20.0, -8.0
PROFILE_RANGE = (-24.0, 48.0, 0.25)
GLOW_BASELINE_FROM = 40.0
GLOW_FADE_FROM, GLOW_FADE_TO = 18.0, 34.0


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float32) / 255


def bilinear(img, x, y):
    h, w, _ = img.shape
    x = np.clip(x, 0, w - 1.001)
    y = np.clip(y, 0, h - 1.001)
    x0, y0 = x.astype(int), y.astype(int)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    return (img[y0, x0] * (1 - fx) * (1 - fy) + img[y0, x0 + 1] * fx * (1 - fy)
            + img[y0 + 1, x0] * (1 - fx) * fy + img[y0 + 1, x0 + 1] * fx * fy)


def reference_profile():
    ref = load(REFERENCE)
    d = np.arange(*PROFILE_RANGE)
    samples = []
    for angle in LIT_ANGLES:
        t = math.radians(angle)
        samples.append(bilinear(ref, REF_CX + (REF_R + d) * math.cos(t), REF_CY + (REF_R + d) * math.sin(t)))
    return d, np.mean(samples, axis=0)


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
    Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA").save(target, optimize=True)
    print(f"circle centre=({cx:.1f},{cy:.1f}) radius={surface_r:.1f} brightened rows {dark_start}-{flat_start} and refilled from {flat_start}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
