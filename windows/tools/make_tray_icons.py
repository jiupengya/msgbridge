"""生成托盘用的纯色线条图标（Windows 系统托盘里的那种单色描边风格）。

设计：一个圆角对话气泡 + 左下角小尾巴，只有描边、没有填充，
按目标尺寸分别渲染（不是一张大图缩下来），小尺寸才不会糊。

用法：python make_tray_icons.py
输出：../MessageBridge/Resources/tray_dark.ico   深色线条，给浅色任务栏用
      ../MessageBridge/Resources/tray_light.ico  浅色线条，给深色任务栏用
"""

from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64]

# 配色：纯色，不带渐变/抗锯齿彩色边缘
DARK = (32, 32, 32, 255)     # 浅色任务栏上用深色线条
LIGHT = (242, 242, 242, 255)  # 深色任务栏上用浅色线条

SUPERSAMPLE = 8


def render(size: int, color: tuple[int, int, int, int]) -> Image.Image:
    """按 16 单位网格画一个对话气泡轮廓，再缩到目标尺寸。"""
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    u = s / 16.0                       # 1 个设计单位等于多少像素
    lw = max(u * 1.35, s / 24.0)       # 线宽：小尺寸下至少保证可见
    lw_i = max(1, round(lw))

    # 气泡主体：圆角矩形描边
    box = (1.7 * u, 2.4 * u, 14.3 * u, 10.8 * u)
    radius = 2.6 * u
    d.rounded_rectangle(box, radius=radius, outline=color, width=lw_i)

    # 左下角小尾巴：气泡底边伸出的折线
    tail = [(4.4 * u, 10.8 * u), (4.4 * u, 14.1 * u), (8.2 * u, 10.8 * u)]
    d.line(tail, fill=color, width=lw_i, joint="curve")
    # 折线端点补圆，避免细线在拐角处出现缺口
    r = lw_i / 2.0
    for px, py in (tail[0], tail[1], tail[2]):
        d.ellipse((px - r, py - r, px + r, py + r), fill=color)

    return img.resize((size, size), Image.LANCZOS)


def build(color: tuple[int, int, int, int]) -> list[Image.Image]:
    return [render(n, color) for n in SIZES]


def save_ico(frames: list[Image.Image], path: Path) -> None:
    base = frames[-1]
    base.save(path, format="ICO", sizes=[(n, n) for n in SIZES],
              append_images=frames[:-1])


def save_preview(frames: list[Image.Image], path: Path) -> None:
    """把各尺寸放大后拼成一张预览图，方便肉眼检查小尺寸效果。"""
    scale = 8
    gap = 12
    w = sum(n * scale + gap for n in SIZES) + gap
    h = max(SIZES) * scale + gap * 2
    sheet = Image.new("RGBA", (w, h), (255, 255, 255, 255))
    x = gap
    for f in frames:
        big = f.resize((f.width * scale, f.height * scale), Image.NEAREST)
        sheet.alpha_composite(big, (x, gap))
        x += big.width + gap
    sheet.save(path)


def main() -> None:
    out = Path(__file__).resolve().parent.parent / "MessageBridge" / "Resources"
    out.mkdir(parents=True, exist_ok=True)

    dark = build(DARK)
    light = build(LIGHT)
    save_ico(dark, out / "tray_dark.ico")
    save_ico(light, out / "tray_light.ico")
    save_preview(dark, Path(__file__).resolve().parent.parent.parent / ".tmp" / "tray_preview.png")
    print("done:", out / "tray_dark.ico", out / "tray_light.ico")


if __name__ == "__main__":
    main()
