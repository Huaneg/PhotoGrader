"""
独立校验 PhotoGrader 写入结果是否仍是合法 PNG。

刻意不复用项目自身的 C# 代码，而是用 Python 从零解析 PNG 结构：
逐块读取长度、类型与 CRC-32，确认每一块的校验值都正确，
并检查 iTXt 块的位置与内容。这样可以排除"自证清白"的可能。

用法：python verify-structure.py <目录>
"""

import glob
import json
import os
import struct
import sys
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
EXPECTED_KEYWORD = b"PhotoGrader\x00"
IEND = b"\x00\x00\x00\x00IEND\xae\x42\x60\x82"


def parse_chunks(data):
    """按 PNG 规范逐块解析，返回 [(类型, 长度, CRC是否正确, 载荷), ...]"""
    if data[:8] != PNG_SIGNATURE:
        raise ValueError("PNG 签名不匹配")

    chunks = []
    pos = 8
    while pos + 8 <= len(data):
        length = struct.unpack(">I", data[pos : pos + 4])[0]
        chunk_type = data[pos + 4 : pos + 8]
        payload = data[pos + 8 : pos + 8 + length]
        stored_crc = struct.unpack(">I", data[pos + 8 + length : pos + 12 + length])[0]
        actual_crc = zlib.crc32(chunk_type + payload) & 0xFFFFFFFF
        chunks.append((chunk_type.decode("latin1"), length, stored_crc == actual_crc, payload))
        pos += 12 + length
        if chunk_type == b"IEND":
            break

    if pos != len(data):
        raise ValueError(f"文件在第 {pos} 字节处存在 {len(data) - pos} 字节的多余数据")

    return chunks


def check(path):
    with open(path, "rb") as handle:
        data = handle.read()

    chunks = parse_chunks(data)

    name = os.path.basename(path)

    if not all(crc_ok for _, _, crc_ok, _ in chunks):
        bad = [t for t, _, ok, _ in chunks if not ok]
        return name, False, f"CRC 校验失败：{bad}"

    if chunks[-1][0] != "IEND":
        return name, False, f"末块不是 IEND，而是 {chunks[-1][0]}"

    if data[-12:] != IEND:
        return name, False, "文件结尾不是规范要求的 IEND 字节序列"

    idat_bytes = sum(length for t, length, _, _ in chunks if t == "IDAT")
    if idat_bytes == 0:
        return name, False, "缺少 IDAT 图像数据"

    itxt = [payload for t, _, _, payload in chunks if t == "iTXt"]
    if len(itxt) != 1:
        return name, False, f"iTXt 块数量为 {len(itxt)}，期望 1"

    payload = itxt[0]
    if not payload.startswith(EXPECTED_KEYWORD):
        return name, False, "iTXt 的 keyword 不是 PhotoGrader"

    cursor = len(EXPECTED_KEYWORD)
    if payload[cursor] != 0 or payload[cursor + 1] != 0:
        return name, False, "iTXt 的压缩标志或方法不为 0"
    cursor += 2

    if payload[cursor] != 0:
        return name, False, "iTXt 的语言标签不是空"
    cursor += 1

    if payload[cursor] != 0:
        return name, False, "iTXt 的翻译 keyword 不是空"
    cursor += 1

    text = payload[cursor:].decode("utf-8").rstrip(" \x00")
    record = json.loads(text)

    rating = record.get("r")
    if not isinstance(rating, int) or not 0 <= rating <= 5:
        return name, False, f"星级字段异常：{record}"

    width, height = struct.unpack(">II", chunks[0][3][:8])
    return name, True, f"{len(chunks)} 块 · {width}x{height} · IDAT {idat_bytes // 1024} KB · {record}"


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else "."
    files = sorted(glob.glob(os.path.join(target, "**", "*.png"), recursive=True))

    if not files:
        print(f"目录中没有 PNG：{target}")
        return 1

    failed = 0
    for path in files:
        try:
            name, ok, detail = check(path)
        except Exception as exc:  # noqa: BLE001
            name, ok, detail = os.path.basename(path), False, f"解析异常：{exc}"

        if not ok:
            failed += 1

        print(("  OK   " if ok else "  FAIL ") + f"{name[:44]:<46} {detail}")

    print()
    print(f"独立校验：{len(files) - failed} 个合法 / 共 {len(files)} 个")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
