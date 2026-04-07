"""Generate the NimBus README banner PNG with gradient ASCII art."""

import pyfiglet
from PIL import Image, ImageDraw, ImageFont

# --- Configuration ---
TEXT = "NimBus"
TAGLINE = "Azure Service Bus. Simplified. Observed. Controlled."
FONT_NAME = "doom"
BG_COLOR = (30, 30, 30)          # #1E1E1E dark background
COLOR_START = (0, 120, 212)      # #0078D4 Azure blue
COLOR_END = (255, 255, 255)      # #FFFFFF white
MONO_FONT_SIZE = 36
TAG_FONT_SIZE = 24
PADDING = 60
BOLD_OFFSET = 1                  # Draw text with 1px offset for bolder look
OUTPUT = "assets/banner.png"

# --- Generate ASCII art ---
ascii_art = pyfiglet.figlet_format(TEXT, font=FONT_NAME)
lines = ascii_art.rstrip("\n").split("\n")

# --- Load monospace font (fall back to default) ---
try:
    mono_font = ImageFont.truetype("consola.ttf", MONO_FONT_SIZE)
except OSError:
    try:
        mono_font = ImageFont.truetype("cour.ttf", MONO_FONT_SIZE)
    except OSError:
        mono_font = ImageFont.load_default()

try:
    tag_font = ImageFont.truetype("consola.ttf", TAG_FONT_SIZE)
except OSError:
    try:
        tag_font = ImageFont.truetype("cour.ttf", TAG_FONT_SIZE)
    except OSError:
        tag_font = ImageFont.load_default()

# --- Measure text dimensions ---
dummy = Image.new("RGB", (1, 1))
draw = ImageDraw.Draw(dummy)

# Measure each ASCII art line
line_bboxes = [draw.textbbox((0, 0), line, font=mono_font) for line in lines]
char_height = max(bb[3] - bb[1] for bb in line_bboxes) if line_bboxes else MONO_FONT_SIZE
line_spacing = int(char_height * 0.15)
text_width = max(bb[2] - bb[0] for bb in line_bboxes)
text_height = len(lines) * char_height + (len(lines) - 1) * line_spacing

# Measure tagline
tag_bbox = draw.textbbox((0, 0), TAGLINE, font=tag_font)
tag_width = tag_bbox[2] - tag_bbox[0]
tag_height = tag_bbox[3] - tag_bbox[1]

# --- Create image ---
img_width = max(text_width, tag_width) + PADDING * 2
img_height = text_height + tag_height + PADDING * 3  # top + gap + bottom
img = Image.new("RGB", (img_width, img_height), BG_COLOR)

# --- Create gradient ---
gradient = Image.new("RGB", (img_width, img_height))
for x in range(img_width):
    t = x / max(img_width - 1, 1)
    r = int(COLOR_START[0] + (COLOR_END[0] - COLOR_START[0]) * t)
    g = int(COLOR_START[1] + (COLOR_END[1] - COLOR_START[1]) * t)
    b = int(COLOR_START[2] + (COLOR_END[2] - COLOR_START[2]) * t)
    for y in range(img_height):
        gradient.putpixel((x, y), (r, g, b))

# --- Render text as mask ---
mask = Image.new("L", (img_width, img_height), 0)
mask_draw = ImageDraw.Draw(mask)

# Draw ASCII art lines (with bold offset for thicker strokes)
y_offset = PADDING
for line in lines:
    # Center each line
    bbox = mask_draw.textbbox((0, 0), line, font=mono_font)
    lw = bbox[2] - bbox[0]
    x = (img_width - lw) // 2
    for dx in range(BOLD_OFFSET + 1):
        for dy in range(BOLD_OFFSET + 1):
            mask_draw.text((x + dx, y_offset + dy), line, fill=255, font=mono_font)
    y_offset += char_height + line_spacing

# Draw tagline centered
tag_y = y_offset + PADDING // 2
tag_x = (img_width - tag_width) // 2
mask_draw.text((tag_x, tag_y), TAGLINE, fill=180, font=tag_font)  # slightly dimmer

# --- Composite gradient through mask ---
img = Image.composite(gradient, img, mask)

# --- Save ---
img.save(OUTPUT, optimize=True)
print(f"Banner saved to {OUTPUT} ({img_width}x{img_height})")
