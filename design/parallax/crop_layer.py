import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ASSETS = Path(__file__).resolve().parents[2] / "src" / "Ksp2Redux.Tools.Launcher" / "Assets" / "Parallax"
LAYOUT = ASSETS / "layers.json"
CANVAS = (2000, 1400)
PAD = 2
ALIGN = 32
WEBP_QUALITY = 95


def install_layer(image, name, lossless=False):
    rgba = image.convert("RGBA")
    if rgba.size != CANVAS:
        raise SystemExit(f"{name} must be the full {CANVAS[0]}x{CANVAS[1]} canvas, got {rgba.size[0]}x{rgba.size[1]}.")

    ys, xs = np.nonzero(np.asarray(rgba)[..., 3])
    if len(xs) == 0:
        raise SystemExit(f"{name} is fully transparent.")
    left = max(0, xs.min() - PAD) // ALIGN * ALIGN
    top = max(0, ys.min() - PAD) // ALIGN * ALIGN
    right = min(CANVAS[0], -(-(xs.max() + 1 + PAD) // ALIGN) * ALIGN)
    bottom = min(CANVAS[1], -(-(ys.max() + 1 + PAD) // ALIGN) * ALIGN)

    options = {"lossless": True} if lossless else {"quality": WEBP_QUALITY}
    rgba.crop((left, top, right, bottom)).save(ASSETS / f"{name}.webp", "WEBP", method=6, exact=True, **options)

    layout = json.loads(LAYOUT.read_text()) if LAYOUT.exists() else {"canvasWidth": CANVAS[0], "canvasHeight": CANVAS[1]}
    layout[name] = {"x": int(left), "y": int(top)}
    LAYOUT.write_text(json.dumps(layout, indent=2) + "\n")
    print(f"{name}: {right - left}x{bottom - top} at ({left}, {top})")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        raise SystemExit("usage: crop_layer.py <full canvas image> <planet|station> [--lossless]")
    install_layer(Image.open(sys.argv[1]), sys.argv[2], "--lossless" in sys.argv[3:])
