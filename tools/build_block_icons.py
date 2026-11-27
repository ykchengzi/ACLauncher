"""装配方块 3D 图标与加载器图标：直接用官方素材，不自己画。

为什么不用手绘：Minecraft 的方块渲染是 45°/30° 等距投影，可见区域约 268×296（≈0.905），
不是 1:1。手绘压成 1:1 就走形了。这里直接用 Wiki 官方渲染图，只做 2 倍最近邻放大，
不插值、不变形。

素材来源见 tools/block-icon-sources/SOURCES.md：
  - 9 个方块：zh.minecraft.wiki 的方块渲染图（300×300 透明底）
  - Fabric / Quilt / Forge / NeoForge：各自官方 logo（已归一化到 300×300 画布）

输出：Resources/Blocks/*.png（600×600 透明底）+ docs/block-icons-preview.png
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "tools" / "block-icon-sources"
OUT_DIR = ROOT / "Resources" / "Blocks"
PREVIEW = ROOT / "docs" / "block-icons-preview.png"

CANVAS = 600  # 素材统一为 300×300 画布，放大 2 倍即 600，整数倍不引入插值

ITEMS = [
    ("cobblestone", "圆石"),
    ("command_block", "命令方块"),
    ("gold_block", "金块"),
    ("grass_block", "草方块"),
    ("dirt_path", "土径"),
    ("anvil", "铁砧"),
    ("redstone_block", "红石块"),
    ("redstone_lamp_on", "红石灯（亮）"),
    ("redstone_lamp_off", "红石灯（灭）"),
    ("fabric", "Fabric"),
    ("quilt", "Quilt"),
    ("forge", "Forge"),
    ("neoforge", "NeoForge"),
]


def build_icon(name: str) -> Image.Image:
    src = Image.open(SRC / f"{name}.png").convert("RGBA")
    if src.size != (300, 300):
        raise SystemExit(f"{name}: 素材应为 300×300，实际 {src.size}")
    return src.resize((CANVAS, CANVAS), Image.NEAREST)


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    icons: dict[str, Image.Image] = {}
    for name, _ in ITEMS:
        img = build_icon(name)
        img.save(OUT_DIR / f"{name}.png")
        icons[name] = img
        bb = img.split()[3].getbbox()
        w, h = bb[2] - bb[0], bb[3] - bb[1]
        print(f"{name:20s} {img.size} 可见 {w}x{h} 比例 {w / h:.3f}")

    big, small, pad, cellw = 150, 34, 24, 170
    W = cellw * len(ITEMS) + pad
    H = pad + big + 34 + 20 + small + pad
    sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 15)
    except OSError:
        font = ImageFont.load_default()
    for idx, (name, label) in enumerate(ITEMS):
        x = pad // 2 + idx * cellw + (cellw - big) // 2
        sheet.alpha_composite(icons[name].resize((big, big), Image.LANCZOS), (x, pad))
        d.text((pad // 2 + idx * cellw + cellw // 2, pad + big + 8), label,
               fill=(40, 40, 40), font=font, anchor="ma")
        sheet.alpha_composite(icons[name].resize((small, small), Image.LANCZOS),
                              (x + (big - small) // 2, pad + big + 34 + 20))
    sheet.save(PREVIEW)
    print("saved", PREVIEW)


if __name__ == "__main__":
    main()
