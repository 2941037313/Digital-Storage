#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
svg2png.py —— 极简 SVG → PNG 光栅器（只要有 Pillow + numpy）

为什么不用 cairosvg / inkscape：本机没装（cairosvg 在 Windows 还要拖 cairo DLL）。
而图标是我们自己画的，只需要 SVG 的一个小子集 —— 与其装工具链，不如把子集吃干。
好处：图标源文件是纯文本，改一像素不用开绘图软件，diff 也可读。

===========================================================================================
支持的 SVG 子集（够画 UI 图标）
===========================================================================================
  元素：<svg width height viewBox>、<g>（属性继承）、<path>、<circle>、<rect rx>、
        <polygon>、<polyline>、<line>
  路径指令：M L H V C Q A Z（大小写=绝对/相对）
  属性：fill / fill-opacity / stroke / stroke-width / stroke-opacity / stroke-linecap / opacity
  颜色：#rgb、#rrggbb、#rrggbbaa、rgb(r,g,b)、none

**不**支持（用到会直接报错，绝不静默画错）：transform / 渐变 / text / clipPath / mask /
  filter / stroke-dasharray / S、T 简写。要这些就去装 cairosvg。

===========================================================================================
画法（三条都与"看起来对"有关）
===========================================================================================
  ① **4× 超采样**（--scale）在大画布上光栅化，最后 LANCZOS 降回目标尺寸 —— 这一步就是抗锯齿。
  ② **描边 = 沿折线连续盖圆盘**：天然得到正确的圆头/圆拐角，避免 Pillow `line(width=n)`
     在转角处的缺口（`joint="curve"` 也不总是干净）。盘间距取 stroke_width/3，够密。
  ③ **每个图元先渲成 L 掩膜再合成**（source-over，numpy 手写）：
     比"直接在 RGBA 上画"多花一点代码，但半透明图元自重叠时不会局部变深。

用法：
  python svg2png.py icons/a.svg -o Textures/UI/Gizmos/a.png --size 128
  python svg2png.py icons/ -o out/ --size 128
  python svg2png.py icons/ -o out/ --preview preview.png          # 顺带出一张"按钮实测图"

预览图会按原版 <c>Command</c> 的画法把图标贴在深色按钮上（64/32 两档），并在右上角叠原版
复选框贴图（Command_Toggle 会画那个）——用于确认"缩小到 32px 还认得出、右上角没被盖掉关键笔画"。
"""

import argparse
import math
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# ==========================================================================================
# 颜色
# ==========================================================================================
_NAMED = {
    "none": None,
    "transparent": (0, 0, 0, 0.0),
    "white": (255, 255, 255, 1.0),
    "black": (0, 0, 0, 1.0),
}


def parse_color(text):
    """返回 (r, g, b, a) / None(=none)。"""
    if text is None:
        return None
    s = text.strip().lower()
    if s in _NAMED:
        return _NAMED[s]
    if s.startswith("#"):
        h = s[1:]
        if len(h) == 3:
            h = "".join(c * 2 for c in h)
        if len(h) == 6:
            h += "ff"
        if len(h) != 8:
            raise ValueError("颜色位数不对: " + text)
        return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), int(h[6:8], 16) / 255.0)
    m = re.match(r"rgba?\(([^)]*)\)$", s)
    if m:
        parts = [p.strip() for p in m.group(1).replace("/", " ").split(",")]
        if len(parts) == 1:
            parts = parts[0].split()
        vals = []
        for p in parts:
            vals.append(float(p[:-1]) / 100.0 if p.endswith("%") else float(p))
        r, g, b = (int(round(v)) for v in vals[:3])
        a = vals[3] if len(vals) > 3 else 1.0
        return (r, g, b, a)
    raise ValueError("看不懂的颜色: " + text)


# ==========================================================================================
# 继承的样式
# ==========================================================================================
DEFAULT_STYLE = {
    "fill": "black",
    "fill-opacity": "1",
    "stroke": "none",
    "stroke-width": "1",
    "stroke-opacity": "1",
    "stroke-linecap": "butt",
    "opacity": "1",
}


def child_style(parent, node):
    style = dict(parent)
    for k in DEFAULT_STYLE:
        if k in node.attrib:
            style[k] = node.attrib[k]
    # style="fill:#fff;stroke-width:2"
    inline = node.attrib.get("style")
    if inline:
        for item in inline.split(";"):
            if ":" in item:
                k, v = item.split(":", 1)
                k = k.strip()
                if k in DEFAULT_STYLE:
                    style[k] = v.strip()
    return style


# ==========================================================================================
# 路径解析 → 折线（list[list[(x, y)]]，每个子路径一条）
# ==========================================================================================
_TOKEN = re.compile(r"([MmLlHhVvCcQqAaZz])|(-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)")


def _arc_points(x1, y1, rx, ry, phi_deg, large_arc, sweep, x2, y2, seg=72):
    """SVG 端点式圆弧 → 采样点（SVG 规范 F.6.5 的换算式）。"""
    if rx == 0 or ry == 0 or (abs(x1 - x2) < 1e-12 and abs(y1 - y2) < 1e-12):
        return [(x2, y2)]
    phi = math.radians(phi_deg)
    cos_p, sin_p = math.cos(phi), math.sin(phi)
    dx2, dy2 = (x1 - x2) / 2.0, (y1 - y2) / 2.0
    x1p = cos_p * dx2 + sin_p * dy2
    y1p = -sin_p * dx2 + cos_p * dy2
    rx, ry = abs(rx), abs(ry)
    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
    if lam > 1:
        s = math.sqrt(lam)
        rx *= s
        ry *= s
    num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    co = math.sqrt(max(0.0, num / den)) if den > 0 else 0.0
    if large_arc == sweep:
        co = -co
    cxp = co * rx * y1p / ry
    cyp = -co * ry * x1p / rx
    cx = cos_p * cxp - sin_p * cyp + (x1 + x2) / 2.0
    cy = sin_p * cxp + cos_p * cyp + (y1 + y2) / 2.0

    def angle(ux, uy, vx, vy):
        dot = ux * vx + uy * vy
        n = math.hypot(ux, uy) * math.hypot(vx, vy)
        a = math.acos(max(-1.0, min(1.0, dot / n))) if n > 0 else 0.0
        return -a if (ux * vy - uy * vx) < 0 else a

    ux, uy = (x1p - cxp) / rx, (y1p - cyp) / ry
    vx, vy = (-x1p - cxp) / rx, (-y1p - cyp) / ry
    theta1 = angle(1.0, 0.0, ux, uy)
    dtheta = angle(ux, uy, vx, vy)
    if sweep == 0 and dtheta > 0:
        dtheta -= 2 * math.pi
    elif sweep == 1 and dtheta < 0:
        dtheta += 2 * math.pi

    pts = []
    for i in range(1, seg + 1):
        t = theta1 + dtheta * (i / float(seg))
        x = cx + rx * math.cos(t) * cos_p - ry * math.sin(t) * sin_p
        y = cy + rx * math.cos(t) * sin_p + ry * math.sin(t) * cos_p
        pts.append((x, y))
    return pts


def parse_path(d):
    """路径 → list[子路径折线]。曲线/圆弧按固定段数展平。"""
    tokens = []
    for m in _TOKEN.finditer(d):
        tokens.append(m.group(1) if m.group(1) else float(m.group(2)))
    if not tokens:
        return []

    subpaths = []
    cur = []
    x = y = 0.0          # 当前点
    sx = sy = 0.0        # 子路径起点
    cmd = None
    i = 0
    nums = lambda: None

    def need(n):
        if i + n > len(tokens):
            raise ValueError("路径数字不够: " + d)
        return tokens[i:i + n]

    while i < len(tokens):
        t = tokens[i]
        if isinstance(t, str):
            cmd = t
            i += 1
            if cmd in "Zz":
                if cur:
                    cur.append((sx, sy))
                    subpaths.append(cur)
                    cur = []
                x, y = sx, sy
                continue
        elif cmd is None:
            raise ValueError("路径没以指令开头: " + d)

        rel = cmd.islower()
        c = cmd.upper()

        if c == "M":
            vals = need(2); i += 2
            nx, ny = vals
            if rel:
                nx, ny = x + nx, y + ny
            if cur:
                subpaths.append(cur)
            cur = [(nx, ny)]
            x, y = nx, ny
            sx, sy = nx, ny
            cmd = "l" if rel else "L"       # M 后跟多组坐标 = 隐式 L
        elif c == "L":
            vals = need(2); i += 2
            nx, ny = vals
            if rel:
                nx, ny = x + nx, y + ny
            cur.append((nx, ny)); x, y = nx, ny
        elif c == "H":
            vals = need(1); i += 1
            nx = x + vals[0] if rel else vals[0]
            cur.append((nx, y)); x = nx
        elif c == "V":
            vals = need(1); i += 1
            ny = y + vals[0] if rel else vals[0]
            cur.append((x, ny)); y = ny
        elif c == "C":
            vals = need(6); i += 6
            x1, y1, x2, y2, x3, y3 = vals
            if rel:
                x1, y1 = x + x1, y + y1
                x2, y2 = x + x2, y + y2
                x3, y3 = x + x3, y + y3
            for k in range(1, 33):
                u = k / 32.0
                a = (1 - u) ** 3
                b = 3 * (1 - u) ** 2 * u
                cc = 3 * (1 - u) * u * u
                dd = u ** 3
                cur.append((a * x + b * x1 + cc * x2 + dd * x3, a * y + b * y1 + cc * y2 + dd * y3))
            x, y = x3, y3
        elif c == "Q":
            vals = need(4); i += 4
            x1, y1, x2, y2 = vals
            if rel:
                x1, y1 = x + x1, y + y1
                x2, y2 = x + x2, y + y2
            for k in range(1, 25):
                u = k / 24.0
                a = (1 - u) ** 2
                b = 2 * (1 - u) * u
                cc = u ** 2
                cur.append((a * x + b * x1 + cc * x2, a * y + b * y1 + cc * y2))
            x, y = x2, y2
        elif c == "A":
            vals = need(7); i += 7
            rx, ry, rot, laf, sf, nx, ny = vals
            if rel:
                nx, ny = x + nx, y + ny
            cur.extend(_arc_points(x, y, rx, ry, rot, int(laf), int(sf), nx, ny))
            x, y = nx, ny
        else:
            raise ValueError("不支持的路径指令 %s（本脚本只支持 M L H V C Q A Z）" % cmd)

    if cur:
        subpaths.append(cur)
    return subpaths


# ==========================================================================================
# 光栅化
# ==========================================================================================
def _stamp_polyline(draw, pts, radius):
    """沿折线连续盖圆盘 ⇒ 圆头 + 圆拐角（比 Pillow 的 line(width=n) 稳）。"""
    step = max(0.5, radius / 3.0)
    for i in range(len(pts) - 1):
        (x1, y1), (x2, y2) = pts[i], pts[i + 1]
        dist = math.hypot(x2 - x1, y2 - y1)
        n = max(2, int(math.ceil(dist / step)) + 1)
        for k in range(n + 1):
            u = k / float(n)
            cx = x1 + (x2 - x1) * u
            cy = y1 + (y2 - y1) * u
            draw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=255)


def _ellipse_points(cx, cy, rx, ry, n=128):
    return [(cx + rx * math.cos(2 * math.pi * i / n), cy + ry * math.sin(2 * math.pi * i / n))
            for i in range(n + 1)]


def _rounded_rect_points(x, y, w, h, r, arc=12):
    """圆角矩形外轮廓（顺时针），用于描边 —— SVG 的描边是**骑在路径上**的，
    而 Pillow 的 rect/ellipse(outline=, width=) 是**往里画**，两者会差半个线宽。"""
    if r <= 0:
        return [(x, y), (x + w, y), (x + w, y + h), (x, y + h), (x, y)]
    r = min(r, w / 2.0, h / 2.0)
    pts = []
    corners = [(x + w - r, y + r, -math.pi / 2, 0.0),
               (x + w - r, y + h - r, 0.0, math.pi / 2),
               (x + r, y + h - r, math.pi / 2, math.pi),
               (x + r, y + r, math.pi, 3 * math.pi / 2)]
    for cx, cy, a0, a1 in corners:
        for i in range(arc + 1):
            a = a0 + (a1 - a0) * i / float(arc)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    pts.append(pts[0])
    return pts


def render_shape(mask, node_name, node, style, scale):
    """把一个图元画进 L 掩膜（坐标已乘 scale）。返回是否画了东西。"""
    d = ImageDraw.Draw(mask)
    fill = parse_color(style["fill"]) if style["fill"] != "none" else None
    stroke = parse_color(style["stroke"]) if style["stroke"] != "none" else None
    sw = float(style["stroke-width"]) * scale
    drew = False

    def mul(color, extra):
        return (color[0], color[1], color[2], color[3] * extra)

    def fill_parts(subpaths):
        for pts in subpaths:
            if len(pts) >= 3:
                d.polygon([(px, py) for px, py in pts], fill=255)

    if node_name == "path":
        subs = parse_path(node.attrib["d"])
        subs = [[(px * scale, py * scale) for px, py in s] for s in subs]
        if fill is not None:
            fill_parts(subs)
        if stroke is not None:
            for pts in subs:
                if len(pts) == 1:
                    d.ellipse([pts[0][0] - sw / 2, pts[0][1] - sw / 2, pts[0][0] + sw / 2, pts[0][1] + sw / 2], fill=255)
                else:
                    _stamp_polyline(d, pts, sw / 2.0)
        drew = True
        return drew, fill, stroke

    if node_name == "circle":
        cx = float(node.attrib["cx"]) * scale
        cy = float(node.attrib["cy"]) * scale
        r = float(node.attrib["r"]) * scale
        if fill is not None:
            d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
        if stroke is not None:
            _stamp_polyline(d, _ellipse_points(cx, cy, r, r), sw / 2.0)
        return True, fill, stroke

    if node_name == "rect":
        x = float(node.attrib.get("x", 0)) * scale
        y = float(node.attrib.get("y", 0)) * scale
        w = float(node.attrib["width"]) * scale
        h = float(node.attrib["height"]) * scale
        rx = float(node.attrib.get("rx", node.attrib.get("ry", 0))) * scale
        if fill is not None:
            if rx > 0:
                d.rounded_rectangle([x, y, x + w, y + h], radius=rx, fill=255)
            else:
                d.rectangle([x, y, x + w, y + h], fill=255)
        if stroke is not None:
            _stamp_polyline(d, _rounded_rect_points(x, y, w, h, rx), sw / 2.0)
        return True, fill, stroke

    if node_name in ("polygon", "polyline"):
        nums = [float(v) for v in re.split(r"[\s,]+", node.attrib["points"].strip()) if v]
        pts = [(nums[i] * scale, nums[i + 1] * scale) for i in range(0, len(nums) - 1, 2)]
        if fill is not None and node_name == "polygon":
            d.polygon(pts, fill=255)
        if stroke is not None:
            if node_name == "polygon":
                pts = pts + [pts[0]]
            _stamp_polyline(d, pts, sw / 2.0)
        return True, fill, stroke

    if node_name == "line":
        pts = [(float(node.attrib["x1"]) * scale, float(node.attrib["y1"]) * scale),
               (float(node.attrib["x2"]) * scale, float(node.attrib["y2"]) * scale)]
        if stroke is not None:
            _stamp_polyline(d, pts, sw / 2.0)
        return True, None, stroke

    return False, None, None


def composite(img, mask, rgb, alpha):
    """source-over：把 mask（L，0..255）按 rgb/alpha 合到 RGBA img 上。"""
    if alpha <= 0:
        return
    m = (np.asarray(mask, dtype=np.float32) / 255.0) * float(alpha)
    if m.max() <= 0:
        return
    a = np.asarray(img, dtype=np.float32) / 255.0
    src = np.zeros_like(a)
    src[:, :, 0] = rgb[0] / 255.0
    src[:, :, 1] = rgb[1] / 255.0
    src[:, :, 2] = rgb[2] / 255.0
    m3 = m[:, :, None]
    out_rgb = a[:, :, :3] * (1 - m3) + src[:, :, :3] * m3
    out_a = a[:, :, 3] + m3[:, :, 0] * (1 - a[:, :, 3])
    res = np.zeros_like(a)
    res[:, :, :3] = out_rgb
    res[:, :, 3] = out_a
    img.paste(Image.fromarray((np.clip(res, 0, 1) * 255).astype(np.uint8), "RGBA"))


def render_svg(path, size, scale=4):
    tree = ET.parse(str(path))
    root = tree.getroot()
    vb = root.attrib.get("viewBox")
    if vb:
        _, _, vw, vh = [float(v) for v in re.split(r"[\s,]+", vb.strip())]
    else:
        vw, vh = float(root.attrib.get("width", size)), float(root.attrib.get("height", size))

    W, H = int(size), int(size)
    big = Image.new("RGBA", (W * scale, H * scale), (0, 0, 0, 0))
    # viewBox → 画布：等比缩放（注意是"直接把坐标乘 k 画上去"，不是先画小再放大 ——
    # 后者会让硬边先被双线性糊一遍，最后再降采样等于二次模糊，边缘会发虚）
    k = min((W * scale) / vw, (H * scale) / vh)
    ox = (W * scale - vw * k) / 2.0
    oy = (H * scale - vh * k) / 2.0

    def walk(node, style, tx, ty):
        for child in node:
            tag = child.tag.split("}")[-1]
            if tag in ("defs", "title", "desc", "metadata"):
                continue
            if "transform" in child.attrib or "transform" in style.get("_own", ""):
                raise ValueError("%s: 本脚本不支持 transform（请在 SVG 里直接把坐标算好）" % path.name)
            st = child_style(style, child)
            if tag == "g":
                walk(child, st, tx, ty)
                continue
            if tag in ("path", "circle", "rect", "polygon", "polyline", "line"):
                mask = Image.new("L", (W * scale, H * scale), 0)
                drew, fill, stroke = render_shape(mask, tag, child, st, k)
                if not drew:
                    continue
                # 只做居中平移（viewBox 与实际画布长宽比不一致时才非零）
                if ox or oy:
                    mask = mask.transform(mask.size, Image.AFFINE, (1, 0, -ox, 0, 1, -oy), resample=Image.BILINEAR)
                base_op = float(st["opacity"])
                if fill is not None:
                    composite(big, mask, fill, base_op * float(st["fill-opacity"]) * fill[3])
                if stroke is not None:
                    composite(big, mask, stroke, base_op * float(st["stroke-opacity"]) * stroke[3])

    walk(root, dict(DEFAULT_STYLE), 0.0, 0.0)
    return big.resize((W, H), Image.LANCZOS)


# ==========================================================================================
# 预览：按原版 Command 的画法贴到深色按钮上
# ==========================================================================================
def _button(size):
    """原版命令按钮：深灰底 + 浅边（只求"能判断辨识度"，不追求像素级复刻）。"""
    img = Image.new("RGBA", (size, size), (46, 49, 54, 255))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=max(2, size // 14),
                        outline=(122, 128, 138, 255), width=max(1, size // 32))
    return img


def _checkbox(size, on=True):
    """Command_Toggle 右上角那个复选框的近似（拿不到原版贴图时的替身）。"""
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([1, 1, s - 2, s - 2], radius=max(2, s // 5),
                        fill=(24, 26, 30, 235), outline=(150, 156, 166, 255), width=max(1, s // 10))
    if on:
        w = max(2, s // 7)
        d.line([(s * 0.24, s * 0.52), (s * 0.44, s * 0.72)], fill=(126, 214, 118, 255), width=w)
        d.line([(s * 0.44, s * 0.72), (s * 0.78, s * 0.28)], fill=(126, 214, 118, 255), width=w)
    return img


def make_preview(icons, out_path, checkbox=None):
    sizes = [128, 64, 32]
    pad = 14
    cell_w = max(sizes) + pad * 2
    cell_h = max(sizes) + pad * 2 + 10
    W = pad + len(icons) * cell_w
    H = pad + 3 * cell_h + 40
    sheet = Image.new("RGBA", (W, H), (24, 25, 28, 255))
    d = ImageDraw.Draw(sheet)

    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 16)
        font_small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 13)
    except OSError:
        font = font_small = ImageFont.load_default()

    d.text((pad, 10), "原版按钮实测（128 / 64 / 32 px，图标按 85% 缩放居中；右上角 = Command_Toggle 复选框）",
           font=font, fill=(200, 205, 214, 255))

    cb_tex = Path(checkbox) if (checkbox and Path(checkbox).exists()) else None

    for ci, svg in enumerate(icons):
        name = svg.stem
        x0 = pad + ci * cell_w
        d.text((x0, 34), name, font=font, fill=(150, 200, 240, 255))
        big = render_svg(svg, 512)
        for ri, s in enumerate(sizes):
            by = 56 + ri * cell_h
            btn = _button(s)
            # 原版 DrawIcon：Widgets.DrawTextureFitted(rect, icon, iconDrawScale * 0.85f)
            inner = int(round(s * 0.85))
            off = (s - inner) // 2
            btn.alpha_composite(big.resize((inner, inner), Image.LANCZOS), (off, off))
            cbs = max(9, int(round(s * 24.0 / 75.0)))
            cb = (Image.open(cb_tex).convert("RGBA").resize((cbs, cbs), Image.LANCZOS)
                  if cb_tex else _checkbox(cbs))
            btn.alpha_composite(cb, (s - cbs, 0))
            sheet.alpha_composite(btn, (x0, by))
        d.text((x0, 56 + 3 * cell_h), "%s.png" % name, font=font_small, fill=(120, 126, 136, 255))

    if checkbox and cb_tex is None:
        d.text((pad, H - 24), "（没找到原版复选框贴图 %s，预览里用替身画的）" % checkbox,
               font=font_small, fill=(200, 160, 120, 255))
    sheet.convert("RGBA").save(out_path, "PNG")
    return out_path


# ==========================================================================================
# CLI
# ==========================================================================================
def main(argv=None):
    ap = argparse.ArgumentParser(description="极简 SVG → PNG 光栅器（Pillow + numpy）")
    ap.add_argument("src", help="一个 .svg 或一个目录（递归找 *.svg）")
    ap.add_argument("-o", "--out", required=True, help="输出 .png 或目录")
    ap.add_argument("--size", type=int, default=128, help="输出边长（正方形），默认 128")
    ap.add_argument("--scale", type=int, default=4, help="超采样倍数，默认 4")
    ap.add_argument("--preview", help="额外输出一张按钮实测预览图")
    ap.add_argument("--checkbox", help="原版复选框贴图路径（预览用）")
    args = ap.parse_args(argv)

    src = Path(args.src)
    files = sorted(src.rglob("*.svg")) if src.is_dir() else [src]
    if not files:
        print("没找到 .svg", file=sys.stderr)
        return 1

    out = Path(args.out)
    if len(files) > 1 or out.suffix.lower() != ".png":
        out.mkdir(parents=True, exist_ok=True)

    for svg in files:
        img = render_svg(svg, args.size, args.scale)
        dst = (out / (svg.stem + ".png")) if out.is_dir() else out
        img.save(dst, "PNG")
        print("%-40s -> %s  (%dx%d)" % (svg.name, dst, img.width, img.height))

    if args.preview:
        make_preview(files, Path(args.preview), args.checkbox)
        print("预览 -> " + args.preview)
    return 0


if __name__ == "__main__":
    sys.exit(main())
