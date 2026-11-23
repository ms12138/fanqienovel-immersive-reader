from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# rounded cream tile
pad = 40
r = 230
d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=r, fill="#F7F2EA")
# subtle inner border
d.rounded_rectangle([pad + 8, pad + 8, S - pad - 8, S - pad - 8], radius=r - 8, outline="#E7DED1", width=4)

# tomato body
cx, cy = 512, 560
d.ellipse([cx - 300, cy - 260, cx + 300, cy + 280], fill="#E8563F")
# bottom highlight
d.ellipse([cx - 240, cy - 40, cx + 240, cy + 240], fill="#EF6A50")
# leaf
leaf = [(512, 300), (440, 360), (512, 345), (584, 360)]
d.polygon(leaf, fill="#2F6B4F")
d.polygon([(512, 350), (442, 320), (465, 390)], fill="#3B7D5D")
d.polygon([(512, 350), (582, 320), (559, 390)], fill="#3B7D5D")
# stem
d.rounded_rectangle([497, 260, 527, 345], radius=14, fill="#2F6B4F")

# open book glyph in tomato
white = "#FFF9F1"
d.rounded_rectangle([cx - 165, cy + 40, cx - 8, cy + 190], radius=14, fill=white)
d.rounded_rectangle([cx + 8, cy + 40, cx + 165, cy + 190], radius=14, fill=white)
d.polygon([(cx, cy + 55), (cx - 12, cy + 55), (cx - 12, cy + 205), (cx, cy + 190)], fill="#E8DED0")
d.polygon([(cx, cy + 55), (cx + 12, cy + 55), (cx + 12, cy + 205), (cx, cy + 190)], fill="#E8DED0")
# text lines
for y in (85, 120, 155):
    d.rounded_rectangle([cx - 140, cy + y, cx - 35, cy + y + 8], radius=4, fill="#C9BCAB")
    d.rounded_rectangle([cx + 35, cy + y, cx + 140, cy + y + 8], radius=4, fill="#C9BCAB")

# small red bookmark at top right of tile
d.rounded_rectangle([760, 120, 850, 260], radius=18, fill="#E8563F")

img.save("icon.png")
img.resize((256, 256), Image.LANCZOS).save("icon.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print("ok")
