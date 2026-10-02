# Digital Storage 数字存储 4.0

[English](#english) | [中文](#中文)

---

## English

A RimWorld mod inspired by Applied Energistics 2 (Minecraft). Build a powered **Storage Core** and your items live inside it — no map footprint, no rendering, no tick cost, no rot.

Unlike 3.0 (which stored numbers in a ledger), 4.0 stores **real Things**: apparel, weapons, quality and durability all survive, and vanilla hauling and trading recognise the contents natively.

### ⚠️ 3.0 saves are migrated automatically

4.0 removes **storage cabinets, storage interfaces, terminal chip implants, buffer warehouses, the core upgrade system and cross-map logistics**, but existing 3.0 saves are migrated on load:

- Inventory stored in a 3.0 core (the old ledger) is turned back into **real items inside the container**; whatever does not fit is dropped at the core's feet instead of being lost
- The old storage filter is carried over (only the "blocked" side is kept)
- 3.0 interfaces and buffer warehouses become **deconstructable legacy buildings** (deconstruct to get the materials back)
- Old chip implants are removed and returned as a terminal chip item

Removed for good: cross-map logistics, storage interfaces, terminal chips, buffer warehouses, core upgrades. **Back up your save before updating.**

### Features

**Storage Core**
- 3x3 powered building, **complete the moment it is built** — no upgrade system
- Fixed 500-stack capacity, requires 100W, can break down
- Unlocked by the "Digital Storage" research (requires Microelectronics basics)
- Contents are real Things, held in a `ThingOwner` — they never enter `listerThings` or the tick manager

**Remote access**
- Withdraw and deposit from any position, at any distance; pawns never walk to the core
- Construction: blueprints pull materials straight from the core
- Bill ingredients: delivered into a pawn's backpack first (zero steps), then carried to the bench
- Eating, drugs, medicine, refuelling, repairs and feeding all go through the core
- No power, no access

**Auto-ingest**
- Three research tiers (Auto-Ingest I/II/III): 1 / 5 / 10 stacks per 15 ticks
- Map-wide, not limited by home area or distance; plays a beam effect on each pickup
- Skips prison cells (that food belongs to prisoners) and workbench ingredient areas (that belongs to a bill)
- Never pulls unmined ore

**Trade**
- Caravans, orbital trade beacons and visiting traders can all buy and sell straight from the core
- Phinix (multiplayer chat/trade) and its Red Packet plugin are supported, including the "allow trading items that are not in storage" branch

**Vanilla systems, natively** (zero patches)
- Resource readout, low food/medicine alerts, wealth, warm-clothes alert
- Auto apparel, opportunistic weapon pickup, transport pod loading, portals, caravan loading
- Bill ×N counting, doctors fetching medicine, refuelling, repairs, feeding

**Compatibility layer**
- The core joins the vanilla storage-group list as a "zero-cell slot group", so any third-party scanner that iterates slot groups can enumerate its contents
- Mods that enumerate map-spawned things directly cannot see the contents by design (our items are never spawned)

### Research tree

1. **Digital Storage** (Industrial) → unlocks the Storage Core
2. **Auto-Ingest I** (Spacer) → 1 stack per 15 ticks
3. **Auto-Ingest II** (Spacer) → 5 stacks per 15 ticks
4. **Auto-Ingest III** (Spacer) → 10 stacks per 15 ticks

### Known limitations

- Items only — corpses are rejected, and non-item categories (buildings, plants, terrain) are out of scope
- Prisoners do not use the core; their meals are delivered into the cell
- 3.0 saves are migrated automatically, but the removed features do not come back

### Compatibility

Verified compatible with Phinix (+ Red Packet), Vanilla Expanded Framework, Pick Up And Haul, Combat Extended, Achtung!, Allow Tool. Patches that overlap with VMF/VEF (`WorkGiver_DoBill`, `ReservationManager`, `StoreUtility`, `ResourceCounter`) are additive.

---

## 中文

一个环世界 mod，灵感来自 Minecraft 的应用能源2（AE2）。建造一台通电的**存储核心**，物品就住进它内部 —— 不占地图格子、不渲染、不参与 tick、不腐烂。

与 3.0（账本存数字）不同，4.0 存的是**真实的 Thing**：衣物、武器、品质、耐久差异全部能存，原版搬运与交易原生就认它。

### ⚠️ 3.0 存档会自动迁移

4.0 移除了**磁盘柜、存储接口、终端芯片植入体、缓冲仓库、核心升级系统、跨图物流**，但 3.0 老存档在**读档时会自动迁移**：

- 核心里的库存（旧账本）逐项还原成**真实物品放进容器**；装不下的落在核心脚下，不会丢
- 旧的存储筛选会被沿用（只保留"禁用"的那一侧）
- 3.0 的接口 / 缓冲仓库变成**可拆除的墓碑建筑**（拆除退回材料）
- 植入体内的终端芯片被摘除，并退回一枚终端芯片物品

已移除且不会复活：跨图物流、存储接口、终端芯片、缓冲仓库、核心升级。**建议更新前先备份存档。**

### 功能

**存储核心**
- 3x3 通电建筑，**一放就是完全体**，没有升级系统
- 固定 500 堆上限，耗电 100W，会故障
- 研究「数字存储」（前置：微电子学）解锁
- 内容物是真实 Thing，住在 `ThingOwner` 里 —— 不进 `listerThings`、不进 tick 管理器

**全图存取**
- 任何位置、任何距离取放，小人不必走到核心旁边
- 建造：蓝图材料直接从核心扣
- bill 取料：所需原料先进小人的随身背包（0 步），再掏出来上台工作
- 吃、嗑药、用药、加油、维修、喂食全部走核心
- 断电即无法存取

**自动收纳**
- 三级研究（自动收纳 I/II/III）：每 15 tick 吸收 1 / 5 / 10 堆
- 全图范围，不受活动区与距离限制；每次收纳有光束特效
- 跳过牢房（那是给囚犯的饭）与工作台材料区（那是给 bill 的料）
- 不会隔着把未开采的矿脉吸走

**交易**
- 商队、轨道交易信标、来访商队都能直接买卖核心里的东西
- Phinix（联机聊天交易）与红包插件已适配，含「允许交易不在储存区中的物品」分支

**原版系统原生正确**（零补丁）
- 资源计数、食物/药品警报、财富、保暖衣物警报
- 自动换装、顺手捡武器、装运输仓、传送门、商队装货
- bill 的 ×N 计数、医生取药、加油、维修、喂食

**兼容层**
- 核心以「零格子储存组」的身份出现在原版储存组列表里，任何遍历 SlotGroup 的第三方扫描器都能枚举到内容物
- 直接枚举"地图上已生成物品"的 mod 看不到内容物 —— 这是设计使然（我们的物品从不 Spawn）

### 研究树

1. **数字存储**（工业）→ 解锁存储核心
2. **自动收纳 I**（太空）→ 每 15 tick 1 堆
3. **自动收纳 II**（太空）→ 每 15 tick 5 堆
4. **自动收纳 III**（太空）→ 每 15 tick 10 堆

### 已知限制

- 只收物品类 —— 不收尸体，建筑/植物/地板之类的非物品类别不在可存范围
- 囚犯不使用核心，他们的饭由典狱长送进牢房
- 3.0 存档会自动迁移，但已移除的机制不会复活

### 兼容性

已验证兼容 Phinix（+ 红包插件）、Vanilla Expanded Framework、Pick Up And Haul、Combat Extended、Achtung!、Allow Tool。与 VMF/VEF 重叠的补丁（`WorkGiver_DoBill`、`ReservationManager`、`StoreUtility`、`ResourceCounter`）是加性共存。

---

## 许可协议 / License

**禁止商用 / Non-commercial only**

- 允许自由使用、修改、分发（非商用）；制作衍生作品（非商用）；参考、借鉴代码（非商用）
- 分发、修改、衍生、参考本模组的作品必须注明原作者与源仓库地址：
  `https://github.com/CagierAsh123/Digital-Storage`（衍生作品同样适用）
- 完整条款见 [LICENSE](LICENSE)

本项目的部分代码由 AI 辅助开发（Claude, Gemini）。

Free to use, modify and distribute (non-commercial). Any distribution, derivative
or reference must credit the original author and link the source repository:
`https://github.com/CagierAsh123/Digital-Storage`. See [LICENSE](LICENSE) for full terms.

Portions of this project were developed with AI assistance (Claude, Gemini).
