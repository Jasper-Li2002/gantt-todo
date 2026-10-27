from PIL import Image, ImageDraw

S = 512
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))

# 渐变底 + 圆角
grad = Image.new('RGB', (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / (S - 1)
    r = int(0x5b + (0x8a - 0x5b) * t)
    g = int(0x6c + (0x63 - 0x6c) * t)
    b = int(0xff + (0xa8 - 0xff) * t)
    gd.line([(0, y), (S, y)], fill=(r, g, b))

mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.23), fill=255)
img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)

# 挂环
d.rounded_rectangle([168, 96, 208, 168], radius=20, fill=(255, 255, 255, 230))
d.rounded_rectangle([304, 96, 344, 168], radius=20, fill=(255, 255, 255, 230))

# 日历纸
d.rounded_rectangle([112, 132, 400, 400], radius=40, fill=(255, 255, 255, 255))
# 顶部色带（下方补直角）
d.rounded_rectangle([112, 132, 400, 212], radius=40, fill=(0x44, 0x53, 0xe0, 255))
d.rectangle([112, 186, 400, 213], fill=(0x44, 0x53, 0xe0, 255))

# 勾选
d.line([(190, 296), (238, 344), (328, 244)], fill=(0x44, 0x53, 0xe0, 255), width=30, joint='curve')

# 小圆点（日期示意）
for cx in (170, 226, 282, 338):
    d.ellipse([cx - 7, 352, cx + 7, 366], fill=(0xc9, 0xcf, 0xe4, 255))

sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save('build/icon.ico', format='ICO', sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save('build/icon_256.png')
print('icon ok')
