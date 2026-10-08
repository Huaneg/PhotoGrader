"""生成 PhotoGrader 的 UI 演示图库。

目的：造一批**纯合成**的图片（随机尺寸的色块 + 文字标注），
覆盖 PhotoGrader 界面可能出现的所有状态，用来截取「主界面示例图」
交给 Google Stitch 做 UI 重设计参考。

产物目录：<DEMO_ROOT>
"""
import os
import random
import struct
import zlib

from PIL import Image, ImageDraw, ImageFont

DEMO_ROOT = r"E:\Vibe Coding\PhotoGrader\_uidemo"

# 随机种子固定，保证每次生成的图库一致，截图可复现
random.seed(20261005)

# ---------------------------------------------------------------- 画布尺寸池
# 刻意覆盖各种宽高比，让缩略图墙呈现错落的高度
SIZES = [
    (1600, 900),    # 16:9 标准横图
    (1920, 1080),   # 16:9 大横图
    (1680, 720),    # 21:9 宽幅
    (2100, 900),    # 超宽幅
    (1200, 1200),   # 1:1 方图
    (1000, 1000),   # 1:1 小方图
    (900, 1200),    # 3:4 竖图
    (800, 1067),    # 3:4 竖图（小）
    (720, 1280),    # 9:16 竖长图
    (1080, 1350),   # 4:5 竖图
    (2000, 1000),   # 2:1 横图
    (1200, 1600),   # 3:4 大竖图
]

# --------------------------------------------------------------- 调色板
PALETTES = [
    ((244, 246, 248), (52, 58, 64)),    # 浅灰底 / 深灰字
    ((214, 226, 240), (32, 58, 94)),    # 淡蓝 / 深蓝
    ((226, 240, 217), (44, 74, 30)),    # 淡绿 / 深绿
    ((252, 228, 214), (122, 52, 16)),   # 淡橙 / 深橙
    ((233, 216, 240), (74, 36, 94)),    # 淡紫 / 深紫
    ((255, 235, 240), (122, 30, 60)),   # 淡粉 / 深红
    ((255, 247, 214), (122, 96, 16)),   # 淡黄 / 深褐
    ((224, 242, 241), (20, 84, 80)),    # 淡青 / 深青
    ((240, 236, 228), (84, 72, 48)),    # 米色 / 深棕
    ((232, 234, 246), (44, 50, 110)),   # 淡靛 / 深靛
]


def find_font(size):
    """找一个可用的中文字体。"""
    candidates = [
        r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\msyhbd.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\segoeui.ttf",
    ]
    for path in candidates:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                continue
    return ImageFont.load_default()


def draw_demo_image(path, size, index, label, sub_label):
    """画一张演示图：底色 + 对角渐隐块 + 中心大字 + 角落尺寸标注。"""
    width, height = size
    bg, fg = PALETTES[index % len(PALETTES)]

    img = Image.new("RGB", (width, height), bg)
    draw = ImageDraw.Draw(img, "RGBA")

    # 右下角一大块半透明色域，给画面一点层次，避免完全死板
    inset = int(min(width, height) * 0.12)
    for i in range(6):
        alpha = 14 + i * 5
        pad = inset + i * int(inset * 0.55)
        if pad * 2 >= min(width, height):
            break
        draw.rounded_rectangle(
            [pad, pad, width - pad, height - pad],
            radius=int(min(width, height) * 0.04),
            outline=fg + (alpha,),
            width=max(2, int(min(width, height) * 0.006)),
        )

    # 中心序号大字
    big = int(min(width, height) * 0.30)
    font_big = find_font(big)
    text = f"{index:02d}"
    bbox = draw.textbbox((0, 0), text, font=font_big)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (width - tw) // 2 - bbox[0]
    ty = (height - th) // 2 - bbox[1]
    draw.text((tx, ty), text, font=font_big, fill=fg + (46,))

    # 顶部标签文字
    font_tag = find_font(max(20, int(min(width, height) * 0.055)))
    draw.text(
        (int(width * 0.06), int(height * 0.07)),
        label,
        font=font_tag,
        fill=fg + (220,),
    )

    # 底部副标签：显示像素尺寸
    font_sub = find_font(max(18, int(min(width, height) * 0.042)))
    draw.text(
        (int(width * 0.06), height - int(height * 0.07) - int(min(width, height) * 0.055)),
        f"{sub_label}  ·  {width}×{height}",
        font=font_sub,
        fill=fg + (170,),
    )

    img.save(path, "PNG", optimize=True)


# ------------------------------------------------------------------- 场景定义
# 每个子目录模拟一类图库场景。
# 键可以带层级（用 "/" 分隔），用于演示侧栏的树状目录结构。
SCENES = {
    "01-角色设定": ["精灵弓手", "机械工师", "沙丘旅人", "深海歌者", "雪山猎手", "荒原车手"],
    "02-场景概念": ["雨夜霓虹街", "正午集市", "高空栈道", "废弃工厂", "樱花庭院", "极地营地"],
    "03-产品渲染": ["手表主视觉", "耳机侧视", "香氛特写", "球鞋动态", "相机正面", "手袋俯拍"],
    "03-产品渲染/配件特写": ["镜头卡口", "充电底座", "表带细节"],
    "04-情绪氛围": ["清晨薄雾", "黄昏海面", "深夜书房", "午后咖啡馆", "雪落林间", "雨后街道"],
    "04-情绪氛围/夜景专辑": ["城市灯火", "星空长曝"],
    "05-版式测试": ["极宽横幅", "正方形封面", "竖版海报", "超长条幅", "宽银幕", "经典三分"],
    "06-重复样本": ["主图", "主图", "主图", "主图"],
}


def main():
    if os.path.exists(DEMO_ROOT):
        # 只清理本脚本自己生成的 PNG，避免误伤
        for root, _dirs, files in os.walk(DEMO_ROOT):
            for name in files:
                if name.lower().endswith(".png"):
                    os.remove(os.path.join(root, name))
    os.makedirs(DEMO_ROOT, exist_ok=True)

    counter = 1
    for scene, labels in SCENES.items():
        scene_dir = os.path.join(DEMO_ROOT, scene)
        os.makedirs(scene_dir, exist_ok=True)

        for i, label in enumerate(labels):
            size = SIZES[(counter + i) % len(SIZES)]

            # 06 目录专用于演示 MD5 重复检测，不做嵌套
            if scene == "06-重复样本":
                # 同目录内故意生成 4 张**字节完全相同**的图，用于演示 MD5 重复检测。
                # 关键：先画一张，其余直接文件复制 —— 若各自重绘，
                # 序号不同会导致内容不同，MD5 就检测不出重复。
                size = (1600, 900)
                name = f"{label}-{i + 1}.png"
                path = os.path.join(scene_dir, name)
                if i == 0:
                    draw_demo_image(path, size, counter, label, "重复样本")
                else:
                    with open(os.path.join(scene_dir, f"{label}-1.png"), "rb") as src:
                        with open(path, "wb") as dst:
                            dst.write(src.read())
                counter += 1
                continue

            path = os.path.join(scene_dir, f"{label}.png")
            draw_demo_image(path, size, counter, label, scene.split("/", 1)[-1])
            counter += 1

    # 统计
    total = 0
    for root, _dirs, files in os.walk(DEMO_ROOT):
        total += sum(1 for f in files if f.lower().endswith(".png"))
    print(f"已生成演示图库：{DEMO_ROOT}")
    print(f"共 {len(SCENES)} 个目录，{total} 张图片")


if __name__ == "__main__":
    main()
