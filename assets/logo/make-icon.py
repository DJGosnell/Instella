"""Instella icon: a blue open box with a gold four-point star, drawn as flat shapes with smooth
linear gradients. Geometry is in the coordinates of the original 1254 px artwork.

Two variants share one description: "detailed" (48 px and up) and "small" (16-32 px: no back
flaps, fatter star arms, fewer tones and a heavier outline, so it stays legible). Every size is
rendered supersampled (anti-aliased). Writes SVGs, PNGs and a multi-size ICO.

Usage (needs numpy and Pillow):
    python make-icon.py <out-dir>        # e.g. assets/logo

See README.md next to this script for where the outputs are copied in the repository.
"""
import io
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

if len(sys.argv) != 2:
    sys.exit(__doc__)
OUT = Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)

VIEW = (15, 15, 1239, 1239)  # square around the artwork, with room for the star outline at the tips

# ---------------------------------------------------------------- geometry (source pixels)
TOP, LEFT, RIGHT, CENTER = (627, 34), (253, 424), (1001, 424), (627, 426)
# The bottom tip is measured at y=749, where the front flaps meet. Ending the star there left the
# dark interior showing in a V between its thin tip and the flaps' edges, so it runs on below the
# junction; the side faces and flaps (drawn later) hide the extra length.
BOTTOM = (627, 820)


def quad_pts(p0, c, p1, n=40):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * c[0] + t * t * p1[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * c[1] + t * t * p1[1]) for t in (i / n for i in range(n + 1))]


def mirror(p):
    return (1254 - p[0], p[1])


def shift(poly, dx, dy):
    return [(x + dx, y + dy) for x, y in poly]


def hull(points):
    """Convex hull (Andrew's monotone chain), counter-clockwise."""
    pts = sorted(set(points))
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def _line_meet(p, d, q, e):
    """Intersection of the lines p + t*d and q + s*e."""
    den = d[0] * e[1] - d[1] * e[0]
    t = ((q[0] - p[0]) * e[1] - (q[1] - p[1]) * e[0]) / den
    return (p[0] + t * d[0], p[1] + t * d[1])


def _clip_x(poly, x, keep_left):
    """Sutherland-Hodgman clip of a polygon against the vertical line at x."""
    inside = (lambda p: p[0] <= x) if keep_left else (lambda p: p[0] >= x)
    out = []
    for i in range(len(poly)):
        a, b = poly[i - 1], poly[i]
        if inside(b):
            if not inside(a):
                out.append((x, a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0])))
            out.append(b)
        elif inside(a):
            out.append((x, a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0])))
    return out


def slab(poly, radii, depth, clip=None, meet=None):
    """A flap's thickness: a band of the same width (depth, measured square to the edge) under
    every edge that faces down, from one end of the edge to the other, like the cut edge of a
    sheet of cardboard; the hinge edge (corner 3 -> 0) and edges facing up get none. Adjacent
    bands meet in a mitre, rounded to match the flap's corners; where a band ends at a free
    corner, it is cut vertically under that corner. clip=("left"|"right", x) cuts the
    band at the box's centre line, so the two front flaps' bands meet there in a clean mitre.
    meet=(point, direction) is a line the band runs into at hinge corner 0 (a back flap's band
    runs down its lower edge until it meets the front flap's top edge, so it touches the box)."""
    k = len(poly)
    area = sum(poly[i][0] * poly[(i + 1) % k][1] - poly[(i + 1) % k][0] * poly[i][1] for i in range(k))
    lines, offsets = [], []
    for i in range(k):
        a, b = poly[i], poly[(i + 1) % k]
        d = (b[0] - a[0], b[1] - a[1]); n = math.hypot(*d)
        # Outward normal (depends on the winding), in screen coordinates (y down).
        nx, ny = (d[1] / n, -d[0] / n) if area > 0 else (-d[1] / n, d[0] / n)
        off = depth if (i != k - 1 and ny > 0.1) else 0.0
        offsets.append(off)
        lines.append(((a[0] + nx * off, a[1] + ny * off), d))
    grown = []
    for i in range(k):
        before, after = offsets[i - 1] > 0, offsets[i] > 0
        at_hinge = i in (0, k - 1)       # corners on the hinge edge (k-1: corner k-1 -> corner 0)
        if (before and after) or ((before or after) and at_hinge):
            # Mitre between two bands, or a band running into the hinge line (at the joint under
            # the star, where the clip then cuts it at the centre line).
            grown.append(_line_meet(*lines[i - 1], *lines[i]))
        elif before or after:
            # A band ends here: cut it straight down under the corner, so it sits beneath the
            # flap's corner instead of running out along the neighbouring edge's line.
            banded = lines[i - 1] if before else lines[i]
            grown.append(_line_meet(*banded, poly[i], (0, 1)))
        else:
            grown.append(poly[i])
    # A corner between two banded edges keeps its roundness around the thicker outline.
    grown_r = [(radii[i] + depth) if radii[i] and offsets[i - 1] and offsets[i] else radii[i] for i in range(k)]
    corners = list(grown)          # one grown point per corner, for the tip sides below
    if meet is not None and offsets[0] > 0:
        # Corner 0 becomes: the hinge corner itself, then down the meet line to the band's underside.
        grown = [poly[0], _line_meet(*lines[0], *meet)] + grown[1:]
        grown_r = [0, 0] + grown_r[1:]
    parts = [fillet(grown, grown_r)]
    # Solid sides at the free tips: where a band ends at a rounded corner, the corner is swept
    # straight down to the band, so the top and its thickness read as one piece (vertical sides).
    top = fillet(poly, radii)
    for i in range(k):
        before, after = offsets[i - 1] > 0, offsets[i] > 0
        if i in (0, k - 1) or before == after or not radii[i]:
            continue
        drop = corners[i][1] - poly[i][1]
        reach = radii[i] * 2.5
        arc = [q for q in top if math.hypot(q[0] - poly[i][0], q[1] - poly[i][1]) <= reach]
        parts.append(hull(arc + shift(arc, 0, drop)))
    if clip:
        parts = [_clip_x(part, clip[1], keep_left=clip[0] == "left") for part in parts]
    return parts


def star_quadrants(fat):
    """Four concave sides as (tip_a, control, tip_b); fat pulls the controls out (thicker arms)."""
    ul, ll = (567, 314), (567, 516)
    if fat:
        ul, ll = (520, 290), (520, 545)
    return [(TOP, ul, LEFT), (LEFT, ll, BOTTOM), (BOTTOM, mirror(ll), RIGHT), (RIGHT, mirror(ul), TOP)]


def fillet(poly, r, n=8):
    """Round the corners of a closed polygon; r is one radius or one per corner (0 = sharp)."""
    radii = r if isinstance(r, (list, tuple)) else [r] * len(poly)
    out = []
    k = len(poly)
    for i in range(k):
        p0, p1, p2 = poly[i - 1], poly[i], poly[(i + 1) % k]
        if radii[i] <= 0:
            out.append(p1)
            continue
        v1 = (p0[0] - p1[0], p0[1] - p1[1]); v2 = (p2[0] - p1[0], p2[1] - p1[1])
        l1, l2 = math.hypot(*v1), math.hypot(*v2)
        d = min(radii[i], l1 / 2, l2 / 2)
        a = (p1[0] + v1[0] / l1 * d, p1[1] + v1[1] / l1 * d)
        b = (p1[0] + v2[0] / l2 * d, p1[1] + v2[1] / l2 * d)
        out += quad_pts(a, p1, b, n)
    return out


# Box: one rim (a symmetric rhombus) that every face and flap hangs from, so the flaps, the
# side faces and the opening meet at the same corners, like a real box. The rim's slope (0.389)
# is the source's hinge lines; the side faces' vertical edges are at x 228 / 1026.
RIM_F, RIM_L = (627, 748), (228, 593)
RIM_R, RIM_B = mirror(RIM_L), (627, 438)
BOX_BOTTOM, BOTTOM_L = (627, 1221), (228, 1030)


def flap(a, b, fold):
    """A flap hinged on rim edge a-b, folded out by the vector fold: [hinge a, outer a, outer b, hinge b]."""
    return [a, (a[0] + fold[0], a[1] + fold[1]), (b[0] + fold[0], b[1] + fold[1]), b]


# Fold vectors measured on the source flaps (from each hinge end to the outer corner beside it).
FRONT_FOLD, BACK_FOLD = (-137, 120), (-126, -75)
FRONT_LEFT = flap(RIM_L, RIM_F, FRONT_FOLD)
FRONT_RIGHT = [mirror(p) for p in FRONT_LEFT]
BACK_LEFT = flap(RIM_L, RIM_B, BACK_FOLD)
BACK_RIGHT = [mirror(p) for p in BACK_LEFT]
INTERIOR = [RIM_L, RIM_B, RIM_R, RIM_F]
# The left face runs 3 px under the right one so the shared front edge has no anti-aliasing seam.
# Its top corner is 2 px lower, so no wall shows above the flaps' joint under the star's tip.
# The bottom corner of that overlap sits just inside the right face's bottom edge, so both
# walls still meet in one point at the bottom (nothing of the left face peeks out under it).
_OVER = 3
_BOTTOM_R = mirror(BOTTOM_L)
_bottom_edge_y = BOX_BOTTOM[1] + (_BOTTOM_R[1] - BOX_BOTTOM[1]) * _OVER / (_BOTTOM_R[0] - BOX_BOTTOM[0])
LEFT_FACE = [RIM_L, (RIM_F[0] + _OVER, RIM_F[1] + 2), (BOX_BOTTOM[0] + _OVER, _bottom_edge_y - 0.8), BOX_BOTTOM, BOTTOM_L]
RIGHT_FACE = [mirror(p) for p in [RIM_L, RIM_F, BOX_BOTTOM, BOTTOM_L]]
# Hinge corners are attached to the box and stay sharp; only the two free outer corners are rounded.
def flap_radii(r):
    return [0, r, r, 0]

THICK = 15      # cardboard thickness (source px), square to each edge: the band under each flap
RIM = 18        # wall thickness showing along the back of the opening


# ---------------------------------------------------------------- drawing operations
# fill:   ("fill", points, gradient)        gradient = (p0, colour0, p1, colour1)
# stroke: ("stroke", points, colour, width, opacity, closed)

def solid(c):
    return ((0, 0), c, (1, 0), c)


def ops(variant, star_stroke=None):
    """variant: "detailed" (48 px+), "small" (24-32 px) or "tiny" (16-20 px: small, no star outline).
    star_stroke: the star outline's width in source units (None: the variant's default)."""
    small = variant in ("small", "tiny")
    o = []
    flap_r = flap_radii(22 if not small else 10)
    depth = THICK if not small else THICK * 1.6
    bl, br = BACK_LEFT[1], BACK_RIGHT[1]            # outer corners, for the gradients
    fl, fr = FRONT_LEFT[1], FRONT_RIGHT[1]

    if not small:
        for poly, edge, grad in [
            (BACK_LEFT, "#1f6fd8", (bl, "#4aa3fc", BACK_LEFT[2], "#2f8cf6")),
            (BACK_RIGHT, "#175fc4", (br, "#3a95fb", BACK_RIGHT[2], "#2a84ee")),
        ]:
            front = FRONT_LEFT if poly is BACK_LEFT else FRONT_RIGHT
            meet = (front[0], (front[1][0] - front[0][0], front[1][1] - front[0][1]))   # front flap's top edge
            o.append(("fill", slab(poly, flap_r, depth, meet=meet), solid(edge)))    # thickness
            o.append(("fill", fillet(poly, flap_r), grad))

    # Opening: the lighter rim is the top of the back walls; the dark inside sits below it.
    o.append(("fill", INTERIOR, (RIM_B, "#5aa9ff", RIM_L, "#3b8ff5")))
    o.append(("fill", shift(INTERIOR, 0, RIM if not small else RIM * 1.6),
              ((RIM_B[0], RIM_B[1] + 12), "#001a5c", RIM_F, "#0a4fc4")))

    # Star, behind the side faces and front flaps.
    quads = star_quadrants(fat=small)
    outline = []
    for a, c, b in quads:
        outline += quad_pts(a, c, b, 60)[:-1]
    lights = [("#fff6a8", "#fff1a0"), ("#ffd23a", "#fbb41e"), ("#f39a0c", "#de7a02"), ("#f7a712", "#f7b52a")]
    darks = [("#fbe36a", "#fbd44a"), ("#fbb41e", "#f6a50c"), ("#e0860a", "#c96a02"), ("#f39c05", "#fac23a")]
    mids = []
    for i, (a, c, b) in enumerate(quads):
        curve = quad_pts(a, c, b, 60)
        mid = curve[30]; mids.append(mid)
        if small:
            col = "#fdd34a" if i in (0, 1) else "#f39a0c"
            o.append(("fill", [CENTER] + curve, solid(col)))
            continue
        la, lb = lights[i]; da, db = darks[i]
        o.append(("fill", [CENTER] + curve[:31], (CENTER, la, a, lb)))
        o.append(("fill", [CENTER] + curve[30:], (CENTER, da, b, db)))
    if not small:
        # Creases: faint light lines on the facet edges (the tips' axes and the valley diagonals).
        for p in (TOP, LEFT, RIGHT, BOTTOM, *mids):
            o.append(("stroke", [CENTER, p], "#fff4c2", 4, 0.55, False))
    if variant != "tiny":   # under a pixel wide at 16-20 px, a white line only blurs the star
        width = star_stroke if star_stroke is not None else (9 if not small else 34)
        o.append(("stroke", outline, "#ffffff", width, 1.0, True))

    radii = [0, 0, 0, 30] if not small else [0, 0, 0, 12]    # only the outer bottom corner is rounded
    left_radii = radii[:3] + [0] + radii[3:]                  # the left face has the extra bottom point
    # Side faces: shaded at the top, under the flaps, lightening towards the bottom.
    o.append(("fill", fillet(LEFT_FACE, left_radii), ((560, 760), "#0c58cc", (420, 1060), "#2286fd")))
    o.append(("fill", fillet(RIGHT_FACE, radii), ((694, 760), "#06399a", (834, 1060), "#0a52c4")))

    for poly, edge, grad, side in [
        (FRONT_LEFT, "#1d74e6", (fl, "#63b8ff", RIM_F, "#3e97fb"), "left"),
        (FRONT_RIGHT, "#1463d6", (RIM_F, "#2e8cfd", fr, "#2380f4"), "right"),
    ]:
        o.append(("fill", slab(poly, flap_r, depth, clip=(side, RIM_F[0])), solid(edge)))
        o.append(("fill", fillet(poly, flap_r), grad))
    return o


def polygons(pts):
    """A fill's points: one polygon, or a list of polygons drawn as one shape."""
    return pts if pts and isinstance(pts[0][0], (list, tuple)) else [pts]


def ccw(poly):
    """The polygon wound one way, so overlapping subpaths of one SVG path add up (nonzero rule)."""
    area = sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1]
               for i in range(len(poly)))
    return poly if area >= 0 else list(reversed(poly))


# ---------------------------------------------------------------- raster renderer
def to_view(p, size):
    x0, y0, x1, _ = VIEW
    k = size / (x1 - x0)
    return ((p[0] - x0) * k, (p[1] - y0) * k)


def hexrgb(h):
    return np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)], dtype=np.float32)


def gradient_rgb(size, grad):
    p0, c0, p1, c1 = grad
    p0, p1 = np.array(to_view(p0, size)), np.array(to_view(p1, size))
    d = p1 - p0
    L2 = float(d @ d) or 1.0
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float32)
    t = np.clip(((xs - p0[0]) * d[0] + (ys - p0[1]) * d[1]) / L2, 0, 1)[..., None]
    return hexrgb(c0) * (1 - t) + hexrgb(c1) * t


def render(variant, size, ss=4, star_stroke=None):
    big = size * ss
    acc = np.zeros((big, big, 4), dtype=np.float32)       # premultiplied RGBA
    k = big / (VIEW[2] - VIEW[0])
    for op in ops(variant, star_stroke):
        mask = Image.new("L", (big, big), 0)
        d = ImageDraw.Draw(mask)
        if op[0] == "fill":
            _, pts, grad = op
            for poly in polygons(pts):
                d.polygon([to_view(p, big) for p in poly], fill=255)
            rgb, opacity = gradient_rgb(big, grad), 1.0
        else:
            _, pts, colour, width, opacity, closed = op
            vp = [to_view(p, big) for p in pts] + ([to_view(pts[0], big)] if closed else [])
            w = max(1, round(width * k))
            d.line(vp, fill=255, width=w, joint="curve")
            for x, y in (vp if closed else [vp[0], vp[-1]]):     # round caps/joins
                d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=255)
            rgb = np.broadcast_to(hexrgb(colour), (big, big, 3))
        a = (np.asarray(mask, dtype=np.float32) / 255.0 * opacity)[..., None]
        acc[..., :3] = rgb * a + acc[..., :3] * (1 - a)
        acc[..., 3:] = a + acc[..., 3:] * (1 - a)
    # Straight (unpremultiplied) RGBA; Pillow's resize premultiplies RGBA itself, so the box
    # filter averages colours by coverage and edges get no fringe.
    img = Image.fromarray(np.clip(acc[..., :3] / np.maximum(acc[..., 3:], 1e-6), 0, 255).astype(np.uint8), "RGB")
    img.putalpha(Image.fromarray(np.round(acc[..., 3] * 255).astype(np.uint8), "L"))
    return img.resize((size, size), Image.BOX) if ss > 1 else img


# ---------------------------------------------------------------- SVG
def svg(variant, star_stroke=None):
    size = 256
    k = size / (VIEW[2] - VIEW[0])
    defs, body = [], []
    for i, op in enumerate(ops(variant, star_stroke)):
        if op[0] == "fill":
            _, pts, (p0, c0, p1, c1) = op
            d = " ".join("M" + " L".join(f"{x:.2f},{y:.2f}" for x, y in (to_view(p, size) for p in ccw(poly))) + " Z"
                         for poly in polygons(pts))
            if c0 == c1:
                body.append(f'<path d="{d}" fill="{c0}"/>')
                continue
            a, b = to_view(p0, size), to_view(p1, size)
            defs.append(f'<linearGradient id="g{i}" gradientUnits="userSpaceOnUse" x1="{a[0]:.2f}" y1="{a[1]:.2f}" '
                        f'x2="{b[0]:.2f}" y2="{b[1]:.2f}"><stop offset="0" stop-color="{c0}"/><stop offset="1" stop-color="{c1}"/></linearGradient>')
            body.append(f'<path d="{d}" fill="url(#g{i})"/>')
        else:
            _, pts, colour, width, opacity, closed = op
            d = "M" + " L".join(f"{x:.2f},{y:.2f}" for x, y in (to_view(p, size) for p in pts)) + (" Z" if closed else "")
            body.append(f'<path d="{d}" fill="none" stroke="{colour}" stroke-width="{width * k:.2f}" '
                        f'stroke-opacity="{opacity}" stroke-linejoin="round" stroke-linecap="round"/>')
    head = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}">'
    return "\n".join([head, "<defs>", *defs, "</defs>", *body, "</svg>"]) + "\n"


def write_ico(path, images):
    """ICO with PNG-compressed entries (Vista+), one per size."""
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for img in images:
        buf = io.BytesIO(); img.save(buf, "PNG"); blob = buf.getvalue()
        w = img.width if img.width < 256 else 0
        entries.append(struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob); offset += len(blob)
    Path(path).write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs))


# Star outline: the design width (9 source units) plus extra whole pixels per size, as asked
# after review; the small sizes keep their own width. 1024 px scales with 256 px.
STAR_EXTRA_PX = {256: 2, 128: 2, 96: 2, 64: 1, 48: 1, 40: 1}


def star_units(n):
    if n <= 32:
        return None
    px = 9 * n / (VIEW[2] - VIEW[0]) + STAR_EXTRA_PX.get(n, 0)
    return px * (VIEW[2] - VIEW[0]) / n


if __name__ == "__main__":
    (OUT / "instella-icon.svg").write_text(svg("detailed", star_units(256)), encoding="utf-8")
    (OUT / "instella-icon-small.svg").write_text(svg("small"), encoding="utf-8")

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = []
    for n in sizes:
        variant = "tiny" if n <= 20 else "small" if n <= 32 else "detailed"
        img = render(variant, n, ss=16 if n <= 64 else 8, star_stroke=star_units(n))
        img.save(OUT / f"instella-icon-{n}.png")
        images.append(img)
    render("detailed", 1024, ss=4, star_stroke=star_units(256)).save(OUT / "instella-icon-1024.png")
    write_ico(OUT / "instella.ico", images)

    print("done")
