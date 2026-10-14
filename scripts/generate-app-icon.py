"""生成应用图标(AppIcon.png / AppIcon.ico)。

原创 Δ 战术风格:深色圆角底板 + 粗描边三角 + 实心内三角 + 四角取景框。
16/24 px 去掉取景框并加粗描边,保证小尺寸可辨识。用法(仓库根目录):

    python3 -I scripts/generate-app-icon.py
"""
import math
import pathlib

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "DeltaCrafter.App" / "Assets"

BG_TOP = (24, 31, 36)
BG_BOTTOM = (11, 14, 17)
RIM = (54, 66, 74)
ACCENT = (143, 227, 107)
ACCENT_DIM = (96, 160, 72)
SS = 4  # 超采样倍数,缩小时得到平滑边缘


def triangle(cx, cy, r):
    """顶点朝上的正三角形,r 为外接圆半径。"""
    return [(cx + r * math.cos(math.radians(a)), cy - r * math.sin(math.radians(a)))
            for a in (90, 210, 330)]


def inset(points, d):
    """把正三角形向内收缩 d(沿内切圆方向)。"""
    cx = sum(p[0] for p in points) / 3
    cy = sum(p[1] for p in points) / 3
    r = math.dist(points[0], (cx, cy))
    return triangle(cx, cy, r - 2 * d)


def render(size, detailed):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # 底板:竖向渐变 + 圆角蒙版
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        t = y / (n - 1)
        gd.line([(0, y), (n, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)) + (255,))
    mask = Image.new("L", (n, n), 0)
    margin = round(n * 0.04)
    radius = round(n * 0.2)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, n - margin, n - margin], radius, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    d.rounded_rectangle([margin, margin, n - margin, n - margin], radius,
                        outline=RIM + (255,), width=max(SS, round(n * 0.012)))

    # 四角取景框(仅大尺寸)
    if detailed:
        arm = n * 0.11
        off = n * 0.17
        w = max(SS, round(n * 0.022))
        for sx, sy in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
            x0 = off if sx > 0 else n - off
            y0 = off if sy > 0 else n - off
            d.line([(x0, y0), (x0 + sx * arm, y0)], fill=ACCENT_DIM + (255,), width=w)
            d.line([(x0, y0), (x0, y0 + sy * arm)], fill=ACCENT_DIM + (255,), width=w)

    # 主体 Δ:外三角减内三角得到粗描边,再放实心内三角
    cx, cy = n / 2, n * 0.56
    outer_r = n * (0.34 if detailed else 0.42)
    stroke = n * (0.075 if detailed else 0.11)
    outer = triangle(cx, cy, outer_r)
    d.polygon(outer, fill=ACCENT + (255,))
    d.polygon(inset(outer, stroke), fill=BG_BOTTOM + (255,))
    core = triangle(cx, cy, outer_r * (0.34 if detailed else 0.3))
    d.polygon(core, fill=ACCENT + (255,))

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = {s: render(s, detailed=s >= 32) for s in sizes}
    frames[256].save(ASSETS / "AppIcon.png")
    frames[256].save(ASSETS / "AppIcon.ico", sizes=[(s, s) for s in sizes],
                     append_images=[frames[s] for s in sizes if s != 256])
    print(f"Wrote {ASSETS / 'AppIcon.png'} and AppIcon.ico ({', '.join(map(str, sizes))})")


if __name__ == "__main__":
    main()
