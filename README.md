# Digital Storage 数字存储 4.0

[English](#english) | [中文](#中文)

RimWorld 1.6 · 需要 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) · [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3623684313)

---

## English

A RimWorld mod inspired by Applied Energistics 2 (Minecraft). Build a powered **Storage Core** and your items live inside it — no map footprint, no rendering, no tick cost, no rot.

Unlike 3.0 (which kept numbers in a ledger), 4.0 stores **real Things**: apparel, weapons, quality and durability all survive, and vanilla hauling and trading recognise the contents natively.

### ⚠️ 3.0 saves: what the migration does

Loading an old save does exactly two things:

- The old ledger is turned back into **real items inside the container**; whatever does not fit is dropped at the core's feet instead of being lost
- Your old storage filter is carried over (only the "blocked" side is kept)

Everything else from 3.0 is gone for good: **disk cabinets, storage interfaces, terminal chip implants, buffer warehouses and the core upgrade component no longer exist** — old buildings, chips and implants cannot be resolved any more and disappear when the save is loaded (a few one-off "could not find Def" lines in the log). There are no tombstones and nothing is refunded.

**Cross-map access was _not_ removed** — in 4.0 any powered core on any map can supply materials (the current map is preferred).

**Back up your save before updating.**

### Features

**Storage Core**
- 3x3 powered building (100 W), complete the moment it is built — no upgrade parts to install
- Capacity comes from research: 500 → 1000 → 1500 → 3000 stacks (existing cores expand the moment the research finishes)
- Contents are real Things held in a `ThingOwner` — they never enter `listerThings` or the tick manager

**Remote access**
- Withdraw and deposit from any position, at any distance, **including other maps** (any powered core can supply; your own map is preferred)
- Construction: blueprints pull materials straight from the core
- Bill ingredients: loaded into a pawn's backpack first (zero steps), then carried to the bench
- Eating, drugs, medicine, refuelling, repairs, feeding and trade all go through the core
- No power, no access

**Auto-ingest**
- Three research tiers (Auto-Ingest I/II/III): 1 / 5 / 10 stacks per 15 ticks
- Map-wide, not limited by home area or distance; beam effect on each pickup
- Skips prison cells (that food belongs to prisoners) and workbench ingredient areas (that belongs to a bill); never pulls unmined ore

**Proxy buildings (digital workers)** — 12 buildings that do the work themselves: mining / construction & deconstruction / cleaning / growing (sow · harvest · chop), in three tiers (speed 0.8x / 1.2x / 2.0x, skill 8 / 15 / 20); the **Transcendent proxy** covers all five work types with up to 75 parallel jobs each

**Crafting proxy** — a building that runs the recipes of nearby workbenches on its own: it picks ingredients through the vanilla bill chain, pays from the core and delivers the products straight back into it. Four tiers (skill 5/10/15/20, speed 0.8x–2.0x) and 3/6/9 GHz overclocking (speed x3/x6/x9), driven from a three-column panel where you pick recipes, type amounts or set a "target count"

**Vanilla machines draw from the core** — subcore scanners (steel + components), growth vats (food and embryos) and hoppers (raw feedstock) take what they need straight from the core instead of waiting for a hauler

**Settings** — cost / power / research multipliers (1/100 to 100x, applied to everything) plus toggles: auto-ingest, the vanilla performance fixes, backpack on mechanoids, worker completions per tick, fleck budget and detailed logging

**Trade** — caravans, orbital trade beacons and visiting traders can all buy and sell straight from the core; Phinix (multiplayer chat/trade) and its Red Packet plugin are supported

**Vanilla systems recognise the core natively** — resource readout, low food/medicine alerts, wealth, warm-clothes alert, auto apparel, opportunistic weapon pickup, transport pod loading, portals, caravan loading, bill ×N counting, doctors fetching medicine, refuelling, repairs and feeding

**Compatibility layer** — the core joins the vanilla storage-group list as a "zero-cell slot group", so any third-party scanner that iterates slot groups can enumerate its contents; mods that enumerate map-spawned things directly cannot see the contents by design (our items are never spawned)

### Research tree

| Research | Cost | Unlocks |
|---|---|---|
| Digital Storage | 1000 | the Storage Core |
| Core Lv2 / Lv3 / Lv4 | 1000 / 1500 / 3000 | capacity 1000 / 1500 / 3000 stacks |
| Auto-Ingest I / II / III | 1000 / 2000 / 3000 | 1 / 5 / 10 stacks per 15 ticks |
| Crafting proxy I–IV | 1000 / 1500 / 2000 / 3000 | 4 tiers of the crafting proxy |
| Mining / Construction / Cleaning / Growing proxy I–III | 1000 / 2000 / 3000 each | the 12 proxy buildings |
| Transcendent proxy | 6000 | one building covering all five work types |

### Known limitations

- Items only — corpses are rejected, and non-item categories (buildings, plants, terrain) are out of scope
- Prisoners do not use the core; their meals are delivered into the cell
- Mods that enumerate map-spawned things directly cannot see the contents (by design)

### Compatibility

Verified compatible with Phinix (+ Red Packet), Vanilla Expanded Framework, Pick Up And Haul, Combat Extended, Achtung!, Allow Tool. Patches that overlap with VMF/VEF (`WorkGiver_DoBill`, `ReservationManager`, `StoreUtility`, `ResourceCounter`) are additive.

### Repository layout

- `Source/` — the C# code. `DigitalStorage.csproj` is an **explicit file list** (no wildcards): new `.cs` files must be added to it, or they silently never compile. Targets .NET Framework 4.7.2; build with MSBuild / Visual Studio 2022.
- `Defs/`, `Patches/`, `Languages/`, `Textures/`, `About/` — the mod content exactly as shipped
- `Assemblies/` — the built `DigitalStorage.dll` (+ `.pdb`) and `0Harmony.dll` (build-time reference for the csproj)
- `Tools/` — developer scripts: `deploy.ps1` (copy the mod into the game's `Mods` folder with SHA-256 verification), `svg2png/` (icon pipeline), `text-report/` (localisation report)

---

## 中文

一个环世界 mod，灵感来自 Minecraft 的应用能源2（AE2）。建造一台通电的**存储核心**，物品就住进它内部 —— 不占地图格子、不渲染、不参与 tick、不腐烂。

与 3.0（账本存数字）不同，4.0 存的是**真实的 Thing**：衣物、武器、品质、耐久差异全部能存，原版搬运与交易原生就认它。

### ⚠️ 3.0 存档会自动迁移

读档时**只做两件事**：

- 核心里的库存（旧账本）逐项还原成**真实物品放进容器**；装不下的落在核心脚下，不会丢
- 旧的存储筛选会被沿用（只保留"禁用"的那一侧）

3.0 的其它机制全部**彻底不存在了**：**磁盘柜、存储接口、终端芯片植入体、缓冲仓库、核心升级组件** —— 旧存档里已建好的这些东西不会再解析出来，会随读档消失（日志里留几条一次性的「找不到 Def」）。没有墓碑、也不退还材料。

**跨图取料没有被砍** —— 4.0 里任意地图上的通电核心都能供货（本图优先）。

**建议更新前先备份存档。**

### 功能

**存储核心**
- 3x3 通电建筑（100W），**一放就是完全体**，没有需要安装的升级组件
- 容量靠研究解锁：500 → 1000 → 1500 → 3000 堆（研究一完成，已有核心立刻变大）
- 内容物是真实 Thing，住在 `ThingOwner` 里 —— 不进 `listerThings`、不进 tick 管理器

**全图存取**
- 任何位置、任何距离取放，**含跨图**（任意地图上的通电核心都能供货，本图优先）
- 建造：蓝图材料直接从核心扣
- bill 取料：所需原料先进小人的随身背包（0 步），再掏出来上台工作
- 吃、嗑药、用药、加油、维修、喂食、交易全部走核心
- 断电即无法存取

**自动收纳**
- 三级研究（自动收纳 I/II/III）：每 15 tick 吸收 1 / 5 / 10 堆
- 全图范围，不受活动区与距离限制；每次收纳有光束特效
- 跳过牢房（那是给囚犯的饭）与工作台材料区（那是给 bill 的料）；不会把未开采的矿脉吸走

**代理建筑（数字工人）** —— 12 台建筑自己干活：挖掘 / 建造与拆除 / 清洁 / 种植（播种 · 收割 · 伐木），三级（速度 0.8× / 1.2× / 2.0×，资质 8 / 15 / 20）；**超凡代理**一台接管全部五类工作、每类并行 75 件

**制作代理** —— 建筑自己完成附近工作台的配方：借原版账单链选料、材料从核心扣、产物直塞核心。四级（资质 5/10/15/20，速度 0.8×–2.0×）与 3/6/9GHz 超频（速度 ×3/×6/×9），操作全在三栏面板里（挑配方、输数量、或设「维持数量」）

**原版机器直连核心** —— 亚核心扫描仪（钢材与零件）、生育舱（食物与胚胎）、料斗（生食）需要的料直接从数字存储核心供给，不用等人搬

**设置** —— 造价 / 电力 / 研究点数三个倍率（1/100 ~ 100×，全局生效），以及若干开关：自动收纳、原版性能修复、机械族是否挂背包、每 tick 完成件数、每帧特效上限、详细日志

**交易**
- 商队、轨道交易信标、来访商队都能直接买卖核心里的东西
- Phinix（联机聊天交易）与红包插件已适配，含「允许交易不在储存区中的物品」分支

**原版系统原生就认核心**
- 资源计数、食物/药品警报、财富、保暖衣物警报
- 自动换装、顺手捡武器、装运输仓、传送门、商队装货
- bill 的 ×N 计数、医生取药、加油、维修、喂食

**兼容层**
- 核心以「零格子储存组」的身份出现在原版储存组列表里，任何遍历 SlotGroup 的第三方扫描器都能枚举到内容物
- 直接枚举"地图上已生成物品"的 mod 看不到内容物 —— 这是设计使然（我们的物品从不 Spawn）

### 研究树

| 研究 | 点数 | 解锁 |
|---|---|---|
| 数字存储 | 1000 | 存储核心 |
| 核心 Lv2 / Lv3 / Lv4 | 1000 / 1500 / 3000 | 容量 1000 / 1500 / 3000 堆 |
| 自动收纳 I / II / III | 1000 / 2000 / 3000 | 每 15 tick 1 / 5 / 10 堆 |
| 制作代理 I–IV | 1000 / 1500 / 2000 / 3000 | 制作代理四级 |
| 挖掘 / 建造 / 清洁 / 种植代理 I–III | 各 1000 / 2000 / 3000 | 12 台代理建筑 |
| 超凡代理 | 6000 | 一台接管五类工作 |

### 已知限制

- 只收物品类 —— 不收尸体，建筑 / 植物 / 地板之类的非物品类别不在可存范围
- 囚犯不使用核心，他们的饭由典狱长送进牢房
- 直接枚举"地图上已生成物品"的 mod 看不到核心内容物（设计使然）

### 兼容性

已验证兼容 Phinix（+ 红包插件）、Vanilla Expanded Framework、Pick Up And Haul、Combat Extended、Achtung!、Allow Tool。与 VMF/VEF 重叠的补丁（`WorkGiver_DoBill`、`ReservationManager`、`StoreUtility`、`ResourceCounter`）是加性共存。

### 仓库结构

- `Source/` —— C# 源码。`DigitalStorage.csproj` 是**显式文件清单**（没有通配符）：新增 `.cs` 必须同时加进 csproj，否则构建照样成功但那个文件根本不参与编译。目标框架 .NET Framework 4.7.2，用 MSBuild / Visual Studio 2022 构建。
- `Defs/`、`Patches/`、`Languages/`、`Textures/`、`About/` —— mod 内容，与发布版逐字节一致
- `Assemblies/` —— 构建产物 `DigitalStorage.dll`（+ `.pdb`）与 `0Harmony.dll`（csproj 的编译期引用）
- `Tools/` —— 开发脚本：`deploy.ps1`（把 mod 同步进游戏的 `Mods` 目录，逐文件 SHA-256 校验）、`svg2png/`（图标管线）、`text-report/`（本地化对账报告）

---

## 许可协议 / License

**禁止商用 + 必须署名 + 衍生必须开源 / Non-commercial, attribution required, derivatives must be open source**

- 允许自由使用、修改、分发（非商用）；制作衍生作品（非商用）；参考、借鉴代码（非商用）
- 分发、修改、衍生、参考本模组的作品必须注明原作者（Cagier.阳 / CagierAsh123）与主仓库地址：
  `https://github.com/CagierAsh123/Digital-Storage`（衍生作品同样适用）
- 衍生作品必须以本协议（或等效的「非商用 + 署名 + 相同方式共享」协议）发布，**并公开其完整源代码**
  （不得只发编译好的 dll）
- 完整条款见 [LICENSE](LICENSE)

本项目的部分代码由 AI 辅助开发（Claude, Gemini）。

Free to use, modify and distribute (non-commercial). Any distribution, derivative
or reference must credit the original author and link the main repository:
`https://github.com/CagierAsh123/Digital-Storage`. Derivatives must be released under
this license and their **complete source code must be published** (binaries alone are
not allowed). See [LICENSE](LICENSE) for full terms.

Portions of this project were developed with AI assistance (Claude, Gemini).
