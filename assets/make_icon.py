"""Draw the Unravel icon: a grey, faceted CPU chip with a blue spiral in the middle.

Same flat faceted style as the Unbound and Unveil icons (same greys and outline), with the shared accent blue for
the spiral disc: light on the lit left half, dark on the right, like Unveil's lightning bolt. The disc is solid and
large so the icon still reads at 16 px in the taskbar; 32 px and smaller use a bolder spiral with fewer turns and
fewer, larger pins.

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


def spool(small):
    """Centre and radius of the blue spiral disc in the middle of the chip."""
    return 128.0, (54.0 if small else 50.0)


def groove_path(small):
    """The spiral: a dark line wound on the blue disc. Fewer, wider-spaced turns on small icons so it stays open."""
    c, _ = spool(small)
    path = QPainterPath()
    turns, r0, r1, steps = (1.5, 8.0, 38.0, 200) if small else (2.0, 6.0, 37.0, 200)
    end = math.pi / 2          # finishes at the bottom of the disc
    start = end - turns * 2 * math.pi
    for i in range(steps + 1):
        f = i / steps
        a = start + f * turns * 2 * math.pi
        r = r0 + (r1 - r0) * f
        pt = P(c + r * math.cos(a), c + r * math.sin(a))
        if i == 0:
            path.moveTo(pt)
        else:
            path.lineTo(pt)
    return path


def draw_icon(p, small=False):
    """Draws the icon in a 256 x 256 box at the painter's origin.

    A grey faceted chip (Unbound / Unveil style) with a blue spiral disc in the middle. Small sizes use a bolder
    spiral and fewer, larger pins.
    """
    p.setPen(Qt.NoPen)
    edge_width = 4.5 if small else 2.0
    edge = QPen(QColor(EDGE))
    edge.setWidthF(edge_width)
    edge.setJoinStyle(Qt.MiterJoin)

    # Pins, drawn first so the body overlaps them: two big ones per side when small, three otherwise.
    centres, half, length = ((96, 160), 13, 28) if small else ((88, 128, 168), 8, 26)
    pins = []
    for c in centres:
        pins += [
            QRectF(c - half, 48 - length, 2 * half, length + 2),
            QRectF(c - half, 206, 2 * half, length + 2),
            QRectF(48 - length, c - half, length + 2, 2 * half),
            QRectF(206, c - half, length + 2, 2 * half),
        ]
    p.setPen(edge)
    p.setBrush(QColor(TOP))
    for pin in pins:
        p.drawRect(pin)
    p.setPen(Qt.NoPen)

    # Chip body: chamfered square, lit left facet, shaded right facet, lit top bevel.
    l, r, t, b, ch = 44, 212, 44, 212, 18
    cx = (l + r) / 2
    body = [P(l + ch, t), P(r - ch, t), P(r, t + ch), P(r, b - ch), P(r - ch, b), P(l + ch, b), P(l, b - ch), P(l, t + ch)]
    left_facet = [P(l + ch, t), P(cx, t), P(cx, b), P(l + ch, b), P(l, b - ch), P(l, t + ch)]
    right_facet = [P(cx, t), P(r - ch, t), P(r, t + ch), P(r, b - ch), P(r - ch, b), P(cx, b)]
    top_bevel = [P(l + ch, t), P(r - ch, t), P(r, t + ch), P(cx, t + 30), P(l, t + ch)]
    for pts, colour in ((left_facet, LEFT), (right_facet, RIGHT), (top_bevel, TOP)):
        p.setBrush(QColor(colour))
        p.drawPolygon(QPolygonF(pts))
    p.setPen(edge)
    p.setBrush(Qt.NoBrush)
    p.drawPolygon(QPolygonF(body))

    # The blue disc with its outline, two-tone like Unveil's bolt: light on the lit left half, dark on the right.
    c, radius = spool(small)
    p.setPen(Qt.NoPen)
    p.setBrush(QColor(EDGE))
    p.drawEllipse(P(c, c), radius + edge_width, radius + edge_width)
    for colour, clip in ((THREAD_LIGHT, QRectF(0, 0, cx, 256)), (THREAD_DARK, QRectF(cx, 0, 256 - cx, 256))):
        p.save()
        p.setClipRect(clip)
        p.setBrush(QColor(colour))
        p.drawEllipse(P(c, c), radius, radius)
        p.restore()

    # The spiral wound on it; thicker on small icons so it is still a spiral at 16 px.
    groove = QPen(QColor(DIE))
    groove.setWidthF(11 if small else 5)
    groove.setCapStyle(Qt.RoundCap)
    p.setPen(groove)
    p.setBrush(Qt.NoBrush)
    p.drawPath(groove_path(small))


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
