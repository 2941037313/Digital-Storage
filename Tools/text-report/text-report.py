#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
text-report.py —— 生成《文本总表》：把 mod 里**所有面向玩家的文本**从源码树里抽出来对账。

为什么要有这个脚本（而不是手写一份 markdown）：这份表是**发版前要反复看**的东西
（键有没有漏、中英是否 1:1、Defs 有没有缺英文、代码里还有没有硬编码中文）。
手写必然过期 —— 上一版表里"139 个 keyed 键"在两次提交后就变成了 152。

用法：
    python Tools/text-report/text-report.py .              # 输出到 Docs/文本总表.md
    python Tools/text-report/text-report.py . out.md

覆盖的六类文本：
  ① Languages/*/Keyed/*.xml      —— keyed 键值（中英对照 + 1:1 对账）
  ② Defs/*.xml                   —— 每个 def 的 label / description
  ③ Languages/*/DefInjected/**   —— 英文覆盖（DefInjected 覆盖了多少 def）
  ④ Source/**/*.cs               —— 还没走 keyed 的中文硬编码（分"玩家可见"与"仅日志/开发工具"）
  ⑤ About/About.xml              —— 模组元信息
  ⑥ docs/工坊*.bbcode            —— 工坊公告与历史（只摘公告段与历史标题，不整段贴）
"""

import argparse
import pathlib
import re
import sys
from xml.etree import ElementTree as ET

# 只跑日志/开发工具的文件：里面出现的中文不会进玩家界面
DEV_ONLY_FILES = {
    "AutoIngestDevTool.cs", "DevDrawProfiler.cs", "HarmonyInit.cs", "Legacy30Migration.cs",
    "DigitalStorageGameComponent.cs", "PhinixCompatPatch.cs",
}
CJK = re.compile(r"[\u4e00-\u9fff]")
LIT = re.compile(r'"(?:[^"\\]|\\.)*[\u4e00-\u9fff](?:[^"\\]|\\.)*"')


def cell(s):
    """markdown 单元格：竖线转义、换行变 <br>。"""
    if s is None:
        return ""
    return str(s).replace("|", "\\|").replace("\r\n", "\n").replace("\n", "<br>").strip()


def read(path):
    return pathlib.Path(path).read_text(encoding="utf-8-sig")


def parse_keyed(path):
    if not path.exists():
        return {}
    root = ET.parse(str(path)).getroot()
    out = {}
    for node in root:
        if not isinstance(node.tag, str):
            continue
        out[node.tag] = (node.text or "").strip()
    return out


def parse_defs(root):
    """→ [(file, defType, defName, label, desc)]"""
    rows = []
    for xf in sorted((root / "Defs").rglob("*.xml")):
        try:
            tree = ET.parse(str(xf))
        except ET.ParseError as e:
            rows.append((xf.name, "PARSE_ERROR", str(e), "", ""))
            continue
        for node in tree.getroot():
            if not isinstance(node.tag, str) or not node.findtext("defName"):
                continue
            if node.attrib.get("Abstract", "").lower() == "true":
                continue
            rows.append((xf.name, node.tag, node.findtext("defName"),
                         (node.findtext("label") or "").strip(),
                         (node.findtext("description") or "").strip()))
    return rows


def parse_definjected(root):
    rows = []          # (relpath, "DefName.field", value)
    for xf in sorted((root / "Languages").rglob("DefInjected/**/*.xml")):
        rel = xf.relative_to(root / "Languages").as_posix()
        try:
            tree = ET.parse(str(xf))
        except ET.ParseError as e:
            rows.append((rel, "PARSE_ERROR", str(e)))
            continue
        for node in tree.getroot():
            if isinstance(node.tag, str):
                rows.append((rel, node.tag, (node.text or "").strip()))
    return rows


def scan_source(root):
    """→ (玩家可见候选, 内部名称/资产路径, 仅日志/开发工具按文件计数)

    分类靠"这个中文会不会出现在玩家界面上"：
      · 玩家可见 = 返回值/标签/消息里的中文（检视栏、Gizmo、按钮）；
      · 内部名称 = 贴图路径、假 pawn 的名字、Def 名 —— 玩家看不到（或看到也无所谓）；
      · 开发诊断 = 日志、ConfigErrors（XML 校验，给 mod 作者看的）、性能探针计数。
    ⚠️ 扫之前先**砍掉行尾注释**：注释里也常出现成对引号包着的中文（如 `（贴在目标"底下"）`），
       不砍就会把注释当成字符串报出来（第一版报了 15 处，其实 15 处全是假的）。
    """
    visible, internal, per_file = [], [], {}
    for f in sorted((root / "Source").rglob("*.cs")):
        if re.search(r"\\(obj|bin)\\", str(f)):
            continue
        lines = f.read_text(encoding="utf-8").splitlines()
        for i, line in enumerate(lines):
            code = line.split("//")[0]
            if "Translate(" in code or not LIT.search(code):
                continue
            lits = [m.group(0) for m in LIT.finditer(code) if "/" not in m.group(0)]
            if not lits:
                continue
            s = line.strip()
            per_file[f.name] = per_file.get(f.name, 0) + 1

            if re.search(r"ContentFinder|texPath|\bGet\(|DigitalWorkerFactory\.Create|MoteDefName", code):
                internal.append((f.name, i + 1, s))          # 资产路径 / 假 pawn 名字
            elif re.search(r"Log\.|yield return parentDef\.defName|Bump\(", code) or "Log." in (lines[i - 1] if i else ""):
                continue                                     # 日志 / XML 校验 / 探针
            elif f.name in DEV_ONLY_FILES or f.name.startswith("Patch_"):
                continue                                     # 开发工具与补丁里的诊断
            else:
                visible.append((f.name, i + 1, s))
    return visible, internal, per_file


def bbcode_announcement(root):
    """摘出工坊文档里的"公告段"（第一个 [h2] 到下一个 [h2]）与历史标题。"""
    out = []
    for f in sorted((root / "Docs").glob("工坊*.bbcode")):
        lines = read(f).replace("\r\n", "\n").split("\n")
        heads = [(i, l) for i, l in enumerate(lines) if l.strip().startswith("[h2]")]
        ann = []
        if len(heads) >= 2:
            ann = [l for l in lines[heads[0][0] + 1:heads[1][0]] if l.strip()]
        hist = [l.strip() for l in lines if l.strip().startswith("[b]20")]
        out.append((f.name, ann, hist))
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description="生成《文本总表》")
    ap.add_argument("root", help="mod 仓库根目录")
    ap.add_argument("out", nargs="?", default=None, help="输出文件（默认 Docs/文本总表.md）")
    args = ap.parse_args(argv)

    root = pathlib.Path(args.root).resolve()
    out_path = pathlib.Path(args.out) if args.out else root / "Docs" / "文本总表.md"

    cn = parse_keyed(root / "Languages/ChineseSimplified/Keyed/DigitalStorage.xml")
    en = parse_keyed(root / "Languages/English/Keyed/DigitalStorage.xml")
    defs = parse_defs(root)
    inj = parse_definjected(root)
    visible, internal, per_file = scan_source(root)
    bb = bbcode_announcement(root)

    # 代码/XML 里真正引用到的 key
    refs = set()
    blob = []
    for sub in ("Source", "Defs", "Patches"):
        for f in (root / sub).rglob("*"):
            if f.is_file() and f.suffix in (".cs", ".xml"):
                blob.append(f.read_text(encoding="utf-8", errors="ignore"))
    blob = "\n".join(blob)
    for k in set(list(cn) + list(en)):
        if re.search(r'"%s"' % re.escape(k), blob):
            refs.add(k)

    inj_defs = set()
    for _, tag, _v in inj:
        m = re.match(r"^(.*?)\.([A-Za-z_][A-Za-z0-9_]*)$", tag)
        if m:
            inj_defs.add(m.group(1))

    missing_en = [d for d in defs if d[3] and d[2] not in inj_defs]
    dead = sorted([k for k in cn if k not in refs])
    only_cn = sorted(set(cn) - set(en))
    only_en = sorted(set(en) - set(cn))

    L = []
    A = L.append
    A("# 数字存储 · 文本总表（all）")
    A("")
    A("> 本文件由 `Tools/text-report/text-report.py` **自动生成**，不要手改（改了下次就被覆盖）。")
    A("> 重新生成：`python Tools/text-report/text-report.py .`")
    A("")
    A("## 0. 概览")
    A("")
    A("| 类别 | 位置 | 条数 |")
    A("|---|---|---|")
    A("| keyed 键值（中文） | `Languages/ChineseSimplified/Keyed/` | %d |" % len(cn))
    A("| keyed 键值（English） | `Languages/English/Keyed/` | %d |" % len(en))
    A("| Defs 的 label/description | `Defs/*.xml` | %d 个 def |" % len(defs))
    A("| 英文 DefInjected 条目 | `Languages/English/DefInjected/**` | %d |" % len(inj))
    A("| 代码内中文硬编码（玩家可见候选） | `Source/**/*.cs` | %d |" % len(visible))
    A("| 代码内中文硬编码（内部名称/资产路径） | `Source/**/*.cs` | %d |" % len(internal))
    A("| 代码内中文硬编码（仅日志/开发工具） | `Source/**/*.cs` | %d |" % (sum(per_file.values()) - len(visible) - len(internal)))
    A("| 工坊文档（bbcode） | `Docs/工坊*.bbcode` | %d 个文件 |" % len(bb))
    A("")
    A("**对账结论**")
    A("")
    A("- keyed 中英键集合：%s" % ("**完全一致**" if not only_cn and not only_en else "有差异"))
    if only_cn:
        A("  - 只有中文：%s" % "、".join("`%s`" % k for k in only_cn))
    if only_en:
        A("  - 只有英文：%s" % "、".join("`%s`" % k for k in only_en))
    A("- 定义了但没有代码/XML 精确引用的键：%s" % ("无" if not dead else "、".join("`%s`" % k for k in dead)))
    A("- Defs 里有文本但英文 DefInjected 没覆盖的 def：%s" % ("无" if not missing_en else "%d 个（见第 6 节）" % len(missing_en)))
    A("- 代码里仍有可能面向玩家的中文硬编码：**%d 处**（见第 4a 节）" % len(visible))
    A("")

    A("## 1. 本地化键值（Keyed）")
    A("")
    A("`Languages/{ChineseSimplified,English}/Keyed/DigitalStorage.xml`，共 %d 键。" % len(cn))
    A("")
    A("| key | 中文 | English |")
    A("|---|---|---|")
    for k in sorted(set(list(cn) + list(en))):
        A("| `%s` | %s | %s |" % (k, cell(cn.get(k, "**缺失**")), cell(en.get(k, "**缺失**"))))
    A("")

    A("## 2. Defs 文本（label / description）")
    A("")
    A("Defs 里的中文是**默认语言**（中文玩家直接看它）；英文靠第 3 节的 DefInjected 覆盖。")
    A("")
    cur = None
    for fname, dtype, defname, label, desc in sorted(defs, key=lambda r: (r[0], r[1])):
        if fname != cur:
            cur = fname
            A("### %s" % fname)
            A("")
            A("| defName | 类型 | label | description |")
            A("|---|---|---|---|")
        A("| `%s` | %s | %s | %s |" % (defname, dtype, cell(label), cell(desc)))
    A("")

    A("## 3. 英文 DefInjected（覆盖 Defs 的英文）")
    A("")
    A("`Languages/English/DefInjected/<DefType>/<file>.xml`，条目数 %d，覆盖 %d 个 def。" % (len(inj), len(inj_defs)))
    A("")
    cur = None
    for rel, tag, val in inj:
        if rel != cur:
            cur = rel
            A("### %s" % rel)
            A("")
            A("| 字段 | English |")
            A("|---|---|")
        A("| `%s` | %s |" % (tag, cell(val)))
    A("")

    A("## 4. 代码内硬编码文本")
    A("")
    A("### 4a. 玩家可见候选（检视栏 / Gizmo / 消息，尚未走 keyed）")
    A("")
    if visible:
        A("| 文件 | 行 | 代码 |")
        A("|---|---|---|")
        for fname, line, code in visible:
            A("| `%s` | %d | `%s` |" % (fname, line, code.replace("|", "\\|")))
    else:
        A("（无 —— 玩家可见文本全部走 keyed / DefInjected 了）")
    A("")
    A("### 4b. 内部名称与资产路径（不进界面，不需要翻译）")
    A("")
    A("假 pawn 的名字（只在历史图/记录里出现）、贴图路径、Mote defName 等。")
    A("")
    A("| 文件 | 行 | 代码 |")
    A("|---|---|---|")
    for fname, line, code in internal:
        A("| `%s` | %d | `%s` |" % (fname, line, code.replace("|", "\\|")))
    A("")
    A("### 4c. 仅日志 / 开发者诊断工具 / Def 校验消息（不影响玩家界面）")
    A("")
    A("| 文件 | 中文硬编码条数 |")
    A("|---|---|")
    for fname, n in sorted(per_file.items(), key=lambda kv: (-kv[1], kv[0])):
        A("| `%s` | %d |" % (fname, n))
    A("")

    A("## 5. 模组元信息（About/About.xml）")
    A("")
    about = root / "About" / "About.xml"
    if about.exists():
        r = ET.parse(str(about)).getroot()
        A("- name：%s" % cell(r.findtext("name")))
        A("- packageId：`%s`" % (r.findtext("packageId") or "").strip())
        A("- supportedVersions：%s" % "、".join(x.text for x in r.findall("supportedVersions/li")))
        A("")
        A("description：")
        A("")
        A("```")
        A((r.findtext("description") or "").strip())
        A("```")
    A("")

    A("## 6. 英文覆盖缺口（Defs 有文本、但 English/DefInjected 没给）")
    A("")
    if not missing_en:
        A("**无缺口** —— 所有带 label 的 def 都有英文覆盖。")
    else:
        A("| 文件 | defName | 缺的字段 |")
        A("|---|---|---|")
        for fname, dtype, defname, label, desc in missing_en:
            fields = [f for f, v in (("label", label), ("description", desc)) if v]
            A("| %s | `%s` | %s |" % (fname, defname, "、".join(fields)))
    A("")

    A("## 7. 工坊文档（bbcode）")
    A("")
    A("这些文件里的文本**不进游戏**，是贴到 Steam 工坊页/变更说明用的；`Docs/` 本身是 gitignore 的本地目录。")
    A("")
    for fname, ann, hist in bb:
        A("### %s" % fname)
        A("")
        A("公告段（发布时粘贴的正文）：")
        A("")
        A("```")
        for l in ann:
            A(l)
        A("```")
        A("")
        A("历史条目：%s" % "；".join(h.replace("[b]", "").replace("[/b]", "") for h in hist))
        A("")

    A("## 8. 待办与建议")
    A("")
    n = 0

    def todo(text):
        nonlocal n
        n += 1
        A("%d. %s" % (n, text))

    if only_cn or only_en:
        todo("**中英键集合不一致**：补上差异（见第 0 节）。")
    if dead:
        todo("**%d 个 keyed 键没有任何引用**（%s）—— 确认后删掉，别让它们在下一次改文案时误导人。"
             % (len(dead), "、".join("`%s`" % k for k in dead)))
    if missing_en:
        todo("**英文 DefInjected 缺 %d 个 def**（见第 6 节）。" % len(missing_en))
    if visible:
        todo("**%d 处中文硬编码**还没走 keyed（见第 4a 节）—— 日志可以留，检视栏/Gizmo/消息要挪进 Keyed。"
             % len(visible))
    todo("工坊变更说明发版前记得更新第 7 节的公告段（`Docs/工坊更新日志-*.bbcode`）。")
    todo("`Docs/` 是本地目录不进 git；要留档就把本文件贴到 obsidian 或工坊。")
    A("")

    out_path.write_text("\n".join(L) + "\n", encoding="utf-8")
    print("已生成 %s（%d 行）" % (out_path, len(L)))
    print("keyed 中/英 = %d/%d，Defs = %d，DefInjected = %d，玩家可见硬编码 = %d，死键 = %d，缺英文 = %d"
          % (len(cn), len(en), len(defs), len(inj), len(visible), len(dead), len(missing_en)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
