"""Generates the RimMCP preview art as a self-contained SVG: the robot looking out over a
colony drawn as the text map an agent reads (get_map_view), lit up inside its cone of vision.

    python workshop/artwork/preview.py workshop/artwork/preview.svg

The map is generated from fixed seeds, so the output is the same every run.
"""
import math
import sys
from xml.sax.saxutils import escape

OUT = sys.argv[1]

W, H = 1280, 720
BG = "#0f1a1e"
CREAM = "#edf3ea"
MINT = "#9fe2bf"
MONO = "Consolas, 'Cascadia Mono', Menlo, monospace"
COLS, ROWS, CW, CH, X0, Y0 = 97, 30, 13.2, 24, 8, 22

COLOR = {".": "#4a5a60", "^": "#8a949b", "~": "#6aa6d6", ":": "#c9a45c", "t": "#76b866",
         "#": "#c4b08c", "=": "#d8d2c4", "A": MINT, "B": MINT, "C": MINT}


def rnd(x, y, s):
    v = math.sin(x * 127.1 + y * 311.7 + s * 74.7) * 43758.5453
    return v - math.floor(v)


def noise(x, y, s):
    xi, yi = math.floor(x), math.floor(y)
    xf, yf = x - xi, y - yi
    u, v = xf * xf * (3 - 2 * xf), yf * yf * (3 - 2 * yf)
    a, b, c, d = rnd(xi, yi, s), rnd(xi + 1, yi, s), rnd(xi, yi + 1, s), rnd(xi + 1, yi + 1, s)
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v


def colony():
    m = []
    for y in range(ROWS):
        row = []
        for x in range(COLS):
            ch = "."
            if noise(x / 9, y / 6, 1) > 0.69:
                ch = "^"
            elif noise(x / 10, y / 5, 2) > 0.72 and x > 40:
                ch = "~"
            elif noise(x / 7, y / 5, 3) > 0.68:
                ch = ":"
            elif rnd(x, y, 4) < 0.06:
                ch = "t"
            row.append(ch)
        m.append(row)
    # A walled room with a door, a stockpile, and three colonists.
    for y in range(12, 17):
        for x in range(62, 72):
            edge = y in (12, 16) or x in (62, 71)
            m[y][x] = "#" if edge and not (y == 16 and x == 66) else "."
    for y in range(19, 22):
        for x in range(74, 81):
            m[y][x] = "="
    m[14][65], m[20][77], m[9][58] = "A", "B", "C"
    return m


EYE = (300, 331)
SLOPE = 0.42
FEATHER = 0.07


def lit(px, py):
    """How brightly the agent sees a point: 1 inside the cone, fading at its edge."""
    dx = px - EYE[0]
    if dx <= 0:
        return 0.0
    angle = abs(py - EYE[1]) / dx
    if angle <= SLOPE - FEATHER:
        return 1.0
    if angle >= SLOPE + FEATHER:
        return 0.0
    return (SLOPE + FEATHER - angle) / (2 * FEATHER)


CLEAR = [(60, 180, 440, 480), (60, 560, 760, 700)]


def cleared(px, py):
    return any(x0 < px < x1 and y0 - 14 < py < y1 for x0, y0, x1, y1 in CLEAR)


def mix(a, b, t):
    ca = [int(a[i:i + 2], 16) for i in (1, 3, 5)]
    cb = [int(b[i:i + 2], 16) for i in (1, 3, 5)]
    return "#" + "".join("%02x" % round(x + (y - x) * t) for x, y in zip(ca, cb))


def text_rows(m):
    out = []
    for y, row in enumerate(m):
        py = Y0 + y * CH
        spans = []
        for x, ch in enumerate(row):
            px = X0 + x * CW
            if cleared(px + CW / 2, py - 7):
                continue
            seen = lit(px + CW / 2, py - 7)
            dim = 0.22 if ch == "." else 0.2
            bright = 0.85 if ch == "." else 1.0
            opacity = round(dim + (bright - dim) * seen, 2)
            weight = ' font-weight="bold"' if ch in "ABC" else ""
            fill = mix(COLOR[ch], "#8aa3ab", seen) if ch == "." else COLOR[ch]
            spans.append('<tspan x="%.1f" fill="%s" fill-opacity="%s"%s>%s</tspan>'
                         % (px, fill, opacity, weight, escape(ch)))
        out.append('<text y="%d">%s</text>' % (py, "".join(spans)))
    return "\n".join(out)


def robot(cx, cy, k):
    def r(x, y, w, h, rx, fill):
        return '<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="%.1f" fill="%s"/>' % (
            cx + (x - 200) * k, cy + (y - 371) * k, w * k, h * k, rx * k, fill)
    parts = [
        r(195, 260, 10, 40, 5, CREAM),
        '<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s"/>' % (cx, cy + (255 - 371) * k, 12 * k, MINT),
        r(90, 344, 21, 50, 8, CREAM), r(289, 344, 21, 50, 8, CREAM),
        r(108, 292, 184, 158, 27, CREAM), r(118, 302, 164, 138, 17, BG),
        r(149, 341, 25, 27, 5, MINT), r(226, 341, 25, 27, 5, MINT),
        r(167, 405, 66, 10, 5, CREAM),
    ]
    return "\n".join(parts)


def brackets(x0, y0, x1, y1, size=14):
    d = ("M%d %dV%dH%d M%d %dH%dV%d M%d %dV%dH%d M%d %dH%dV%d" % (
        x0, y0 + size, y0, x0 + size, x1 - size, y0, x1, y0 + size,
        x1, y1 - size, y1, x1 - size, x0 + size, y1, x0, y1 - size))
    return '<path d="%s" fill="none" stroke="%s" stroke-width="2.5" stroke-opacity="0.9"/>' % (d, MINT)


m = colony()
# The walled room on the grid, framed like a target the agent is reading.
room = (X0 + 62 * CW - 8, Y0 + 12 * CH - 26, X0 + 72 * CW + 2, Y0 + 16 * CH + 10)
far = W + 40
svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img">
<title>RimMCP: agentic intelligence for the Rim</title>
<desc>The RimMCP robot looks out over a colony drawn as the text map an agent reads. Inside its cone of vision the map lights up in colour; outside it stays dim.</desc>
<defs>
  <linearGradient id="beam" x1="0" y1="0" x2="1" y2="0">
    <stop offset="0" stop-color="{MINT}" stop-opacity="0.10"/>
    <stop offset="1" stop-color="{MINT}" stop-opacity="0.02"/>
  </linearGradient>
</defs>
<rect width="{W}" height="{H}" fill="{BG}"/>
<path d="M{EYE[0]} {EYE[1]} L{far} {EYE[1] - SLOPE * (far - EYE[0]):.0f} L{far} {EYE[1] + SLOPE * (far - EYE[0]):.0f} Z" fill="url(#beam)"/>
<g font-family="{MONO}" font-size="21" xml:space="preserve">
{text_rows(m)}
</g>
<path d="M{EYE[0]} {EYE[1]} L{far} {EYE[1] - SLOPE * (far - EYE[0]):.0f} M{EYE[0]} {EYE[1]} L{far} {EYE[1] + SLOPE * (far - EYE[0]):.0f}" stroke="{MINT}" stroke-opacity="0.35" stroke-width="2" fill="none"/>
{brackets(*[round(v) for v in room])}
<g id="robot">
{robot(250, 345, 0.95)}
</g>
<text x="80" y="636" font-family="{MONO}" font-size="84" font-weight="bold" fill="{CREAM}">RimMCP</text>
<text x="84" y="682" font-family="{MONO}" font-size="28" fill="{MINT}">Agentic intelligence for the Rim</text>
</svg>
'''
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write(svg)
print("wrote", OUT, len(svg), "bytes")
