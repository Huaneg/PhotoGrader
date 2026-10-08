"""裁剪截图指定区域，用于细节核对。

用法：python crop.py <源png> <输出png> <left> <top> <right> <bottom> [scale]
"""
import sys
from PIL import Image


def main():
    src, dst = sys.argv[1], sys.argv[2]
    box = tuple(int(v) for v in sys.argv[3:7])
    scale = float(sys.argv[7]) if len(sys.argv) > 7 else 2.0

    img = Image.open(src).convert("RGB")
    crop = img.crop(box)
    if scale != 1.0:
        crop = crop.resize(
            (int(crop.width * scale), int(crop.height * scale)),
            Image.LANCZOS,
        )
    crop.save(dst)
    print(f"{src}[{box}] -> {dst} {crop.width}x{crop.height}")


if __name__ == "__main__":
    main()
