"""Builds the fonts the launcher swaps in for languages its own fonts do not cover.

Run from the repository root:

    python -m pip install fonttools
    python scripts/fonts/build-language-fonts.py

It downloads pinned upstream fonts, checks their SHA-256, and writes the results to
src/Ksp2Redux.Tools.Launcher/Assets/LanguageFonts/<culture>/ together with each font's license.

- Body text (fallback for JetBrains Mono): static Regular and Bold instances of Noto Sans JP, KR, SC
  and TC. Avalonia does not apply the weight axis of variable fonts, so the instances are cut here.
- LED labels (fallback for LED Counter 7): ZLabs RoundPix MD for Simplified Chinese as published, and
  round-dot versions of three pixel fonts for Japanese, Korean and Traditional Chinese, drawn the way
  ZLabs RoundPix MD draws its dots. Polish uses Doto and Russian uses DotMatrix for the whole label,
  because LED Counter 7 lacks their letters and a word would otherwise mix two dot grids.
"""

import hashlib
import io
import sys
import urllib.request
import zipfile
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph, GlyphComponent, GlyphCoordinates
from fontTools.ttLib.tables.ttProgram import Program
from fontTools.varLib import instancer

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "src" / "Ksp2Redux.Tools.Launcher" / "Assets" / "LanguageFonts"
CACHE = Path(__file__).resolve().parent / ".cache"

GOOGLE_FONTS = "https://raw.githubusercontent.com/google/fonts"

# Pinned to a commit or release. The SHA-256 of each download is recorded in sources.sha256 on the
# first run and checked on every later one.
SOURCES = {
    "noto-jp": f"{GOOGLE_FONTS}/66a36c8c94b1a5d992ee4e7f392fccfe4945767c/ofl/notosansjp/NotoSansJP%5Bwght%5D.ttf",
    "noto-jp-ofl": f"{GOOGLE_FONTS}/66a36c8c94b1a5d992ee4e7f392fccfe4945767c/ofl/notosansjp/OFL.txt",
    "noto-kr": f"{GOOGLE_FONTS}/b38c5c93af322c45f633e17ac440ec1e6c94d489/ofl/notosanskr/NotoSansKR%5Bwght%5D.ttf",
    "noto-kr-ofl": f"{GOOGLE_FONTS}/b38c5c93af322c45f633e17ac440ec1e6c94d489/ofl/notosanskr/OFL.txt",
    "noto-sc": f"{GOOGLE_FONTS}/a85815a42757630ce188fdad368c2dfc444d4773/ofl/notosanssc/NotoSansSC%5Bwght%5D.ttf",
    "noto-sc-ofl": f"{GOOGLE_FONTS}/a85815a42757630ce188fdad368c2dfc444d4773/ofl/notosanssc/OFL.txt",
    "noto-tc": f"{GOOGLE_FONTS}/3be1884c48c3e45b52ecc725676a08f87776373e/ofl/notosanstc/NotoSansTC%5Bwght%5D.ttf",
    "noto-tc-ofl": f"{GOOGLE_FONTS}/3be1884c48c3e45b52ecc725676a08f87776373e/ofl/notosanstc/OFL.txt",
    "doto": f"{GOOGLE_FONTS}/7380f7cebbf2250460c23fd301b51bcbab162edf/ofl/doto/Doto%5BROND,wght%5D.ttf",
    "doto-ofl": f"{GOOGLE_FONTS}/7380f7cebbf2250460c23fd301b51bcbab162edf/ofl/doto/OFL.txt",
    "dotmatrix": "https://raw.githubusercontent.com/Gissio/font_DotMatrix/df67d223c806b28d638039e0609484191593e208/fonts/ttf/DotMatrix-Regular.ttf",
    "dotmatrix-ofl": "https://raw.githubusercontent.com/Gissio/font_DotMatrix/df67d223c806b28d638039e0609484191593e208/OFL.txt",
    "zlabs": "https://github.com/Astro-2539/ZLabs-RoundPix-12px/releases/download/Build_20260813/ZLabsRoundPix_12px_MD_CN.ttf",
    "zlabs-ofl": "https://github.com/Astro-2539/ZLabs-RoundPix-12px/releases/download/Build_20260813/LICENSE-OFL",
    "maruminya": "https://raw.githubusercontent.com/hicchicc/x12y12pxMaruMinya/ad836b68da9ccb3c51063ca164335db556413969/fonts/ttf/x12y12pxMaruMinya.ttf",
    "maruminya-ofl": "https://raw.githubusercontent.com/hicchicc/x12y12pxMaruMinya/ad836b68da9ccb3c51063ca164335db556413969/OFL.txt",
    "galmuri": "https://github.com/quiple/galmuri/releases/download/v2.40.4/Galmuri-v2.40.4.zip",
    "fusion": "https://github.com/TakWolf/fusion-pixel-font/releases/download/2026.11.10/fusion-pixel-font-12px-proportional-ttf-v2026.11.10.zip",
}

HASHES_FILE = Path(__file__).resolve().parent / "sources.sha256"


def load_hashes():
    hashes = {}
    if HASHES_FILE.exists():
        for line in HASHES_FILE.read_text(encoding="utf-8").splitlines():
            if line.strip():
                digest, name = line.split(maxsplit=1)
                hashes[name.strip()] = digest
    return hashes


def fetch(name, hashes, record):
    url = SOURCES[name]
    CACHE.mkdir(exist_ok=True)
    path = CACHE / name
    if not path.exists():
        print(f"Downloading {name}")
        with urllib.request.urlopen(url) as response:
            path.write_bytes(response.read())
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    expected = hashes.get(name)
    if expected is None:
        record[name] = digest
    elif expected != digest:
        sys.exit(f"{name}: SHA-256 {digest} does not match the pinned {expected}")
    return data


def zip_member(data, file_name):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        matches = [n for n in archive.namelist() if n.replace("\\", "/").rsplit("/", 1)[-1] == file_name]
        if len(matches) != 1:
            sys.exit(f"Expected one zip member named {file_name}, found {matches}")
        return archive.read(matches[0])


def zip_members(data, predicate):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        return {n: archive.read(n) for n in sorted(archive.namelist()) if predicate(n)}


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    print(f"Wrote {path.relative_to(ROOT)} ({len(data) / 1048576:.1f} MB)")


def save_font(font, path):
    buffer = io.BytesIO()
    font.save(buffer)
    write(path, buffer.getvalue())


def build_noto(culture, region, data, license_text):
    for weight, style in ((400, "Regular"), (700, "Bold")):
        font = instancer.instantiateVariableFont(TTFont(io.BytesIO(data)), {"wght": weight}, updateFontNames=True)
        save_font(font, OUT / culture / f"NotoSans{region}-{style}.ttf")
    write(OUT / culture / f"NotoSans{region}-OFL.txt", license_text)


# Dot geometry of ZLabs RoundPix MD: a dot is 80% of the 100-unit pixel cell, drawn as a 12-point
# TrueType contour with on-curve points at the four extremes.
DOT_DIAMETER = 0.8


def dot_glyph(cell):
    r = cell * DOT_DIAMETER / 2
    k = r * 0.41421356  # tan(22.5 degrees), so each implied on-curve point lies on the circle
    c = cell / 2
    points = [
        (c + r, c, True), (c + r, c - k, False), (c + k, c - r, False),
        (c, c - r, True), (c - k, c - r, False), (c - r, c - k, False),
        (c - r, c, True), (c - r, c + k, False), (c - k, c + r, False),
        (c, c + r, True), (c + k, c + r, False), (c + r, c + k, False),
    ]
    glyph = Glyph()
    glyph.numberOfContours = 1
    glyph.coordinates = GlyphCoordinates([(round(x), round(y)) for x, y, _ in points])
    glyph.flags = bytearray(1 if on else 0 for _, _, on in points)
    glyph.endPtsOfContours = [len(points) - 1]
    glyph.program = Program()
    glyph.program.fromBytecode(b"")
    return glyph


def filled_cells(source_glyf, glyph_name, cell):
    """Pixel cells of a pixel font glyph, found by testing each cell centre against its outline."""
    glyph = source_glyf[glyph_name]
    if glyph.numberOfContours == 0:
        return []
    coords, ends, flags = glyph.getCoordinates(source_glyf)
    if any(not (f & 1) for f in flags):
        raise ValueError(f"{glyph_name} has curves; only pixel outlines are supported")
    edges = []
    start = 0
    for end in ends:
        contour = coords[start:end + 1]
        for i in range(len(contour)):
            x0, y0 = contour[i]
            x1, y1 = contour[(i + 1) % len(contour)]
            if y0 != y1:
                edges.append((x0, y0, x1, y1))
        start = end + 1
    xs = [p[0] for p in coords]
    ys = [p[1] for p in coords]
    cells = []
    for row in range(int(min(ys) // cell), int(-(-max(ys) // cell))):
        yc = (row + 0.5) * cell
        crossings = []
        for x0, y0, x1, y1 in edges:
            if (y0 <= yc < y1) or (y1 <= yc < y0):
                crossings.append((x0 + (yc - y0) * (x1 - x0) / (y1 - y0), 1 if y1 > y0 else -1))
        if not crossings:
            continue
        for column in range(int(min(xs) // cell), int(-(-max(xs) // cell))):
            xc = (column + 0.5) * cell
            if sum(d for x, d in crossings if x > xc) != 0:
                cells.append((column, row))
    return cells


def build_dotted(source_data, family, culture, file_name, license_text, extra_copyright):
    source = TTFont(io.BytesIO(source_data))
    upm = source["head"].unitsPerEm
    cell = upm // 12
    glyf = source["glyf"]
    cmap = source.getBestCmap()

    order = [".notdef", "dot"]
    glyphs = {".notdef": Glyph(), "dot": dot_glyph(cell)}
    metrics = {".notdef": (source["hmtx"][".notdef"][0], 0), "dot": (cell, 0)}
    new_cmap = {}
    seen = {}
    for codepoint, source_name in sorted(cmap.items()):
        if source_name in seen:
            new_cmap[codepoint] = seen[source_name]
            continue
        name = f"g{len(order)}"
        seen[source_name] = name
        new_cmap[codepoint] = name
        order.append(name)
        glyph = Glyph()
        cells = filled_cells(glyf, source_name, cell)
        if cells:
            glyph.numberOfContours = -1
            glyph.components = []
            for column, row in cells:
                component = GlyphComponent()
                component.glyphName = "dot"
                component.x, component.y = column * cell, row * cell
                component.flags = 0x4  # ROUND_XY_TO_GRID
                glyph.components.append(component)
        glyphs[name] = glyph
        metrics[name] = (source["hmtx"][source_name][0], 0)

    builder = FontBuilder(upm, isTTF=True)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap(new_cmap)
    builder.setupGlyf(glyphs)
    glyf_out = builder.font["glyf"]
    for name in order:
        glyf_out[name].recalcBounds(glyf_out)
        advance = metrics[name][0]
        metrics[name] = (advance, getattr(glyf_out[name], "xMin", 0))
    builder.setupHorizontalMetrics(metrics)
    hhea = source["hhea"]
    builder.setupHorizontalHeader(ascent=hhea.ascent, descent=hhea.descent, lineGap=hhea.lineGap)
    source_os2 = source["OS/2"]
    builder.setupOS2(
        usWeightClass=400,
        sTypoAscender=source_os2.sTypoAscender, sTypoDescender=source_os2.sTypoDescender,
        sTypoLineGap=source_os2.sTypoLineGap,
        usWinAscent=source_os2.usWinAscent, usWinDescent=source_os2.usWinDescent,
        ulUnicodeRange1=source_os2.ulUnicodeRange1, ulUnicodeRange2=source_os2.ulUnicodeRange2,
        ulUnicodeRange3=source_os2.ulUnicodeRange3, ulUnicodeRange4=source_os2.ulUnicodeRange4,
        ulCodePageRange1=getattr(source_os2, "ulCodePageRange1", 0),
        ulCodePageRange2=getattr(source_os2, "ulCodePageRange2", 0),
        sxHeight=getattr(source_os2, "sxHeight", 0), sCapHeight=getattr(source_os2, "sCapHeight", 0),
        achVendID="KRDX", fsType=0,
    )
    source_copyright = source["name"].getDebugName(0) or ""
    builder.setupNameTable({
        "copyright": f"{source_copyright} {extra_copyright}".strip(),
        "familyName": family,
        "styleName": "Regular",
        "uniqueFontIdentifier": f"KSP2 Redux: {family}",
        "fullName": f"{family} Regular",
        "psName": family.replace(" ", "") + "-Regular",
        "version": "Version 1.000",
        "licenseDescription": "This Font Software is licensed under the SIL Open Font License, Version 1.1.",
        "licenseInfoURL": "https://openfontlicense.org",
    })
    builder.setupPost(keepGlyphNames=False)
    builder.font["maxp"].maxComponentDepth = 1
    save_font(builder.font, OUT / culture / file_name)
    write(OUT / culture / file_name.replace(".ttf", "-OFL.txt"), license_text)


def main():
    hashes = load_hashes()
    record = {}
    get = lambda name: fetch(name, hashes, record)  # noqa: E731

    build_noto("ja", "JP", get("noto-jp"), get("noto-jp-ofl"))
    build_noto("ko", "KR", get("noto-kr"), get("noto-kr-ofl"))
    build_noto("zh-Hans", "SC", get("noto-sc"), get("noto-sc-ofl"))
    build_noto("zh-Hant", "TC", get("noto-tc"), get("noto-tc-ofl"))

    write(OUT / "zh-Hans" / "ZLabsRoundPix_12px_MD_CN.ttf", get("zlabs"))
    write(OUT / "zh-Hans" / "ZLabsRoundPix_12px_MD_CN-OFL.txt", get("zlabs-ofl"))

    build_dotted(get("maruminya"), "Redux Dot JP", "ja", "ReduxDotJP.ttf", get("maruminya-ofl"),
                 "Round-dot version by KSP2 Redux, derived from x12y12pxMaruMinya.")

    galmuri = get("galmuri")
    galmuri_licence = zip_member(galmuri, "LICENSE.txt")
    build_dotted(zip_member(galmuri, "Galmuri11.ttf"), "Redux Dot KR", "ko", "ReduxDotKR.ttf", galmuri_licence,
                 "Round-dot version by KSP2 Redux, derived from Galmuri11.")

    fusion = get("fusion")
    fusion_licences = zip_members(fusion, lambda n: n.endswith(".txt"))
    fusion_licence_text = b"\n\n".join(
        f"==== {name} ====\n".encode() + text for name, text in fusion_licences.items())
    build_dotted(zip_member(fusion, "fusion-pixel-12px-proportional-zh_hant.ttf"), "Redux Dot TC", "zh-Hant",
                 "ReduxDotTC.ttf", fusion_licence_text,
                 "Round-dot version by KSP2 Redux, derived from Fusion Pixel Font 12px proportional zh_hant.")

    # Round dots at the weight whose dot size is closest to LED Counter 7. Doto's STAT table has no
    # value for ROND=100, so the instance is named here as a family of its own.
    doto = instancer.instantiateVariableFont(TTFont(io.BytesIO(get("doto"))), {"ROND": 100, "wght": 700})
    names = doto["name"]
    for name_id in (16, 17, 21, 22, 25):
        names.removeNames(nameID=name_id)
    names.setName("Doto Round", 1, 3, 1, 0x409)
    names.setName("Regular", 2, 3, 1, 0x409)
    names.setName("Doto Round Regular", 4, 3, 1, 0x409)
    names.setName("DotoRound-Regular", 6, 3, 1, 0x409)
    doto["OS/2"].usWeightClass = 400
    save_font(doto, OUT / "pl" / "DotoRound-Regular.ttf")
    write(OUT / "pl" / "Doto-OFL.txt", get("doto-ofl"))

    write(OUT / "ru" / "DotMatrix-Regular.ttf", get("dotmatrix"))
    write(OUT / "ru" / "DotMatrix-OFL.txt", get("dotmatrix-ofl"))

    if record:
        hashes.update(record)
        HASHES_FILE.write_text("".join(f"{hashes[n]}  {n}\n" for n in sorted(hashes)), encoding="utf-8")
        print(f"Pinned {len(record)} new source hash(es) in {HASHES_FILE.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
