"""装配方块 3D 图标：直接用 Minecraft Wiki 的官方等距渲染图，不自己画。

为什么改：之前手绘的等距图被压成了 1:1，而 Minecraft 的方块渲染是 45°/30° 投影，
真实比例约 268×296（≈0.905）。这里保留原始长宽比，只做整数倍最近邻放大，不插值、不变形。

素材来源（tools/block-icon-sources/）：
  - 9 个方块：zh.minecraft.wiki 的方块渲染图（JE 版本，300×300 透明底）
  - Fabric：fabricmc.net 官方 logo（52×56，毛线球）
  - NeoForge：neoforged/Documentation 仓库的官方 logo（狐狸，灰底板保留）

输出：Resources/Blocks/*.png（600×600 透明底）+ docs/block-icons-preview.png
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "tools" / "block-icon-sources"
OUT_DIR = ROOT / "Resources" / "Blocks"
PREVIEW = ROOT / "docs" / "block-icons-preview.png"

CANVAS = 600  # 300×300 的 wiki 渲染图放大 2 倍，整数倍不引入插值

# (输出名, 源文件名, 显示名, 放大倍数)
ITEMS = [
    ("cobblestone", "cobblestone.png", "圆石", 2),
    ("command_block", "command_block.png", "命令方块", 2),
    ("gold_block", "gold_block.png", "金块", 2),
    ("grass_block", "grass_block.png", "草方块", 2),
    ("dirt_path", "dirt_path.png", "土径", 2),
    ("anvil", "anvil.png", "铁砧", 2),
    ("redstone_block", "redstone_block.png", "红石块", 2),
    ("redstone_lamp_on", "redstone_lamp_on.png", "红石灯（亮）", 2),
    ("redstone_lamp_off", "redstone_lamp_off.png", "红石灯（灭）", 2),
    ("fabric", "fabric.png", "Fabric 图标", 10),
    ("neoforge", "neoforge.png", "NeoForge 图标", 2),
]


def load(name: str, scale: int) -> Image.Image:
    im = Image.open(SRC / name).convert("RGBA")
    im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
    if im.width > CANVAS or im.height > CANVAS:
        raise SystemExit(f"{name}: {im.size} 超出画布 {CANVAS}")
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((CANVAS - im.width) // 2, (CANVAS - im.height) // 2))
    return canvas


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    icons: dict[str, Image.Image] = {}
    for out_name, src_name, _, scale in ITEMS:
        img = load(src_name, scale)
        img.save(OUT_DIR / f"{out_name}.png")
        icons[out_name] = img
        bb = img.split()[3].getbbox()
        w, h = bb[2] - bb[0], bb[3] - bb[1]
        print(f"{out_name:20s} {img.size} 可见 {w}x{h} 比例 {w / h:.3f}")

    big, small, pad, cellw = 150, 34, 24, 170
    W = cellw * len(ITEMS) + pad
    H = pad + big + 34 + 20 + small + pad
    sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 16)
    except OSError:
        font = ImageFont.load_default()
    for idx, (out_name, _, label, _) in enumerate(ITEMS):
        x = pad // 2 + idx * cellw + (cellw - big) // 2
        sheet.alpha_composite(icons[out_name].resize((big, big), Image.LANCZOS), (x, pad))
        d.text((pad // 2 + idx * cellw + cellw // 2, pad + big + 8), label,
               fill=(40, 40, 40), font=font, anchor="ma")
        sheet.alpha_composite(icons[out_name].resize((small, small), Image.LANCZOS),
                              (x + (big - small) // 2, pad + big + 34 + 20))
    sheet.save(PREVIEW)
    print("saved", PREVIEW)


if __name__ == "__main__":
    main()
