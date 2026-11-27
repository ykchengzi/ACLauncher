"""ACLauncher 图标 v2（依据图标设计规范：强轮廓、少元素、多尺寸可读）。

- 1024 主画布，四周留白约 6%，符合图标栅格边距
- 橙色圆角方块底（品牌色），底部略深做轻微体积
- 奶油色"方块猫"：大轮廓 + 方形耳朵 + 两个粗眼点 + 小橙鼻
- 去掉胡须/嘴等小尺寸下会糊的细节
- 右下角两个像素方块呼应 Minecraft
输出：Resources/ACLauncher.ico + 多尺寸预览条
"""

from PIL import Image, ImageDraw

S = 1024
OUT_ICO = r"E:\Wendang\ACLauncher\Resources\ACLauncher.ico"
OUT_STRIP = r"E:\Wendang\ACLauncher\Resources\ACLauncher-icon-preview.png"

ORANGE_TOP = (247, 162, 60)
ORANGE_BOTTOM = (191, 90, 12)
CREAM = (255, 247, 236)
INK = (43, 33, 24)
NOSE = (194, 94, 14)
WHITE = (255, 255, 255)
PIXEL = (150, 68, 6)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# 背景：圆角方块 + 垂直渐变
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=200, fill=255)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    gd.line([(0, y), (S, y)], fill=lerp(ORANGE_TOP, ORANGE_BOTTOM, y / S))
img.paste(grad, (0, 0), mask)

# 右下角像素方块（低对比，弱化存在感）
for (x, y, w, h, a) in [(712, 792, 108, 108, 70), (828, 792, 108, 108, 55), (828, 676, 108, 108, 45)]:
    ov = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(ov).rounded_rectangle([x, y, x + w, y + h], radius=20, fill=PIXEL + (a,))
    img.alpha_composite(ov)

d = ImageDraw.Draw(img)

# 方块猫头：阶梯像素耳（耳根嵌入头部，保证剪影完整）+ 大圆角头
# 左耳
d.polygon([(296, 360), (296, 246), (340, 246), (340, 192), (384, 192),
           (384, 160), (436, 160), (436, 360)], fill=CREAM)
# 右耳（镜像）
d.polygon([(728, 360), (728, 246), (684, 246), (684, 192), (640, 192),
           (640, 160), (588, 160), (588, 360)], fill=CREAM)

# 头
d.rounded_rectangle([220, 300, 804, 812], radius=170, fill=CREAM)

# 眼睛（粗方块点 + 白色高光方块，像素感）
for cx in (404, 620):
    d.rounded_rectangle([cx - 52, 476, cx + 52, 596], radius=34, fill=INK)
    d.rectangle([cx - 24, 502, cx + 2, 528], fill=WHITE)

# 鼻子（小方块）
d.rounded_rectangle([488, 656, 536, 698], radius=12, fill=NOSE)

# 导出多尺寸
img.save(OUT_ICO, format="ICO",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

# 预览条：256 / 64 / 32 / 16（含放大镜效果便于检查）
sizes = [256, 128, 64, 48, 32, 24, 16]
pad = 24
strip_w = sum(sizes) + pad * (len(sizes) + 1) + 256  # 额外放一个 16px 放大
strip_h = 300
strip = Image.new("RGBA", (strip_w, strip_h), (240, 240, 240, 255))
x = pad
for s in sizes:
    im = img.resize((s, s), Image.LANCZOS)
    strip.alpha_composite(im, (x, pad + (256 - s) // 2))
    x += s + pad
img.resize((256, 256), Image.LANCZOS).save(OUT_STRIP)
print("saved icons")
