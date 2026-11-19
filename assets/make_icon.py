"""Draw the Unravel icon: a grey, faceted CPU chip with a blue thread unwinding from it.

Same flat faceted style as the Unbound and Unveil icons (same greys and outline), with
the shared accent blue for the thread: light on the lit left facet, dark on the right,
like Unveil's lightning bolt.

Needs PySide6. Unbound's virtual environment already has it:
  "..\\Unbound\\.venv\\Scripts\\python" assets\\make_icon.py
Writes:
  assets/unravel.ico           16-256 px, the exe and window icon
  assets/unravel.png           256 px, the logo in the app's sidebar
  assets/unravel-1280x640.png  GitHub social preview (Settings > General > Social preview)
and copies unravel.ico and unravel.png into src/Unravel.App.
"""

import math
import os
import shutil
import struct

from PySide6.QtCore import QBuffer, QByteArray, QIODevice, QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QGuiApplication, QImage, QPainter, QPainterPath, QPen, QPolygonF

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, "..", "src", "Unravel.App")
SIZES = [16, 24, 32, 48, 64, 128, 256]

# Unbound / Unveil facet greys and outline
TOP = "#B4B9C1"    # lightest, faces the light
LEFT = "#868C96"
RIGHT = "#5C626C"  # darkest
EDGE = "#3E434B"
DIE = "#2b2d31"    # the theme's panel colour

# Shared accent: light and dark facets of the thread
THREAD_LIGHT = "#3b82f6"
THREAD_DARK = "#2563eb"

# Social preview background: the theme's window colour (same as Unveil's preview)
BACKGROUND = "#1e1f22"


def P(x, y):
    return QPointF(x, y)


def thread_path(small):
    """A spiral wound in the centre of the die that unwinds into a loose wave off the chip's corner."""
    path = QPainterPath()
    cx, cy = 122, 122
    # Wide enough spacing between turns that the coil stays readable; fewer turns on small icons.
    turns = 1.25 if small else 1.75
    r0, r1 = (4, 30) if small else (3, 35)
    steps = 160
    # Finish on the right of the coil, where the clockwise coil is heading straight down,
    # so the loose end leaves the coil in one smooth line.
    end_angle = 0.0
    start = end_angle - turns * 2 * math.pi
    for i in range(steps + 1):
        f = i / steps
        a = start + f * turns * 2 * math.pi
        r = r0 + (r1 - r0) * f
        pt = P(cx + r * math.cos(a), cy + r * math.sin(a))
        if i == 0:
            path.moveTo(pt)
        else:
            path.lineTo(pt)

    # Leave the spiral heading down-right, cross the die and the chip, and trail away in a soft wave.
    end = path.currentPosition()
    path.cubicTo(P(end.x(), end.y() + 26), P(160, 164), P(180, 178))
    path.cubicTo(P(198, 194), P(206, 214), P(226, 220))
    path.cubicTo(P(240, 224), P(246, 234), P(247, 246))
    return path


def draw_icon(p, small=False):
    """Draws the icon in a 256 x 256 box at the painter's origin."""
    p.setPen(Qt.NoPen)

    # Pins: three per side, in the lit grey, drawn first so the body overlaps them.
    pins = []
    for c in (88, 128, 168):
        pins += [
            QRectF(c - 8, 22, 16, 26),    # top
            QRectF(c - 8, 208, 16, 26),   # bottom
            QRectF(22, c - 8, 26, 16),    # left
            QRectF(208, c - 8, 26, 16),   # right
        ]
    # Outlines at every size keep small icons crisp on light taskbars.
    edge = QPen(QColor(EDGE))
    edge.setWidthF(3.2 if small else 2.0)
    edge.setJoinStyle(Qt.MiterJoin)
    p.setPen(edge)
    p.setBrush(QColor(TOP))
    for pin in pins:
        p.drawRect(pin)
    p.setPen(Qt.NoPen)

    # Chip body: a square with chamfered corners, split into a lit left facet and a shaded right facet,
    # with a lit top bevel (the same construction as Unveil's battery).
    l, r, t, b, ch = 44, 212, 44, 212, 18
    cx = (l + r) / 2
    body = [P(l + ch, t), P(r - ch, t), P(r, t + ch), P(r, b - ch), P(r - ch, b), P(l + ch, b), P(l, b - ch), P(l, t + ch)]
    left_facet = [P(l + ch, t), P(cx, t), P(cx, b), P(l + ch, b), P(l, b - ch), P(l, t + ch)]
    right_facet = [P(cx, t), P(r - ch, t), P(r, t + ch), P(r, b - ch), P(r - ch, b), P(cx, b)]
    top_bevel = [P(l + ch, t), P(r - ch, t), P(r, t + ch), P(cx, t + 30), P(l, t + ch)]
    for pts, colour in ((left_facet, LEFT), (right_facet, RIGHT), (top_bevel, TOP)):
        p.setBrush(QColor(colour))
        p.drawPolygon(QPolygonF(pts))

    # The die: a dark inset square the thread is wound in.
    die = QRectF(78, 78, 88, 88) if not small else QRectF(74, 74, 96, 96)
    p.setBrush(QColor(DIE))
    p.drawRect(die)

    # Body and die outlines (pins were outlined before the body covered their inner ends; the thread goes on top).
    p.setPen(edge)
    p.setBrush(Qt.NoBrush)
    p.drawPolygon(QPolygonF(body))
    p.drawRect(die)

    # Thread: two-tone like Unveil's bolt. Light where it lies over the left half, dark over the right.
    path = thread_path(small)
    width = 15 if small else 8.5
    for colour, clip in ((THREAD_LIGHT, QRectF(0, 0, cx, 256)), (THREAD_DARK, QRectF(cx, 0, 256 - cx, 256))):
        p.save()
        p.setClipRect(clip)
        pen = QPen(QColor(colour))
        pen.setWidthF(width)
        pen.setCapStyle(Qt.RoundCap)
        pen.setJoinStyle(Qt.RoundJoin)
        p.setPen(pen)
        p.drawPath(path)
        p.restore()


def render(size):
    img = QImage(size, size, QImage.Format_ARGB32)
    img.fill(Qt.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.Antialiasing)
    p.scale(size / 256, size / 256)
    draw_icon(p, small=size <= 32)
    p.end()
    return img


def render_social():
    """1280 x 640: the icon centred on the theme background, as Unveil's preview is."""
    img = QImage(1280, 640, QImage.Format_ARGB32)
    img.fill(QColor(BACKGROUND))
    p = QPainter(img)
    p.setRenderHint(QPainter.Antialiasing)
    scale = 560 / 256
    p.translate((1280 - 256 * scale) / 2, (640 - 256 * scale) / 2)
    p.scale(scale, scale)
    draw_icon(p)
    p.end()
    return img


def png_bytes(img):
    data = QByteArray()
    buf = QBuffer(data)
    buf.open(QIODevice.WriteOnly)
    img.save(buf, "PNG")
    return bytes(data)


def write_ico(path, images):
    """ICO file with PNG-compressed entries (supported since Windows Vista)."""
    pngs = [png_bytes(i) for i in images]
    out = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    for img, data in zip(images, pngs):
        w = img.width() if img.width() < 256 else 0  # 0 means 256
        out += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as fh:
        fh.write(out + b"".join(pngs))


if __name__ == "__main__":
    app = QGuiApplication([])
    images = [render(s) for s in SIZES]
    ico = os.path.join(HERE, "unravel.ico")
    png = os.path.join(HERE, "unravel.png")
    write_ico(ico, images)
    images[-1].save(png)
    render_social().save(os.path.join(HERE, "unravel-1280x640.png"))
    shutil.copyfile(ico, os.path.join(APP, "unravel.ico"))
    shutil.copyfile(png, os.path.join(APP, "unravel.png"))
    print("Wrote unravel.ico, unravel.png and unravel-1280x640.png; copied the icon and logo into src/Unravel.App")
