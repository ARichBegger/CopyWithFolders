#!/usr/bin/env python3
# 生成 CopyWithFolders 图标：蓝色圆角方块 + 白色文件夹 + 绿色向下箭头
from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 背景圆角方块（蓝）
d.rounded_rectangle([8, 8, S - 8, S - 8], radius=52, fill=(46, 111, 242, 255))
# 底部高光
d.rounded_rectangle([8, S - 60, S - 8, S - 8], radius=52, fill=(58, 128, 255, 90))

# 文件夹（白）
# 上折页
d.rounded_rectangle([56, 76, 138, 108], radius=8, fill=(255, 255, 255, 255))
# 主体
d.rounded_rectangle([44, 96, 212, 172], radius=12, fill=(255, 255, 255, 255))
# 折页分隔线（浅灰蓝）
d.rounded_rectangle([44, 118, 212, 122], radius=2, fill=(220, 228, 245, 255))

# 绿色向下箭头（复制语义）
cx = 128
stem_top, stem_bot = 168, 216
head_w = 56
d.rounded_rectangle([cx - 12, stem_top, cx + 12, stem_bot - 4], radius=6, fill=(32, 196, 90, 255))
d.polygon([(cx - head_w // 2, stem_bot - 26), (cx + head_w // 2, stem_bot - 26), (cx, stem_bot + 6)], fill=(32, 196, 90, 255))

# 输出多尺寸 ICO
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save("app.ico", format="ICO", sizes=sizes)
print("icon saved")
