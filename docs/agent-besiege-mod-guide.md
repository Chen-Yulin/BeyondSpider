# BeyondSpider Agent Mod Guide

这份文档给后续 agent 写 BeyondSpider / Besiege Mod 时使用。它不是玩家说明书，而是实现参考：先理解 Besiege 的限制，再按 BeyondSpider 的太空战舰设定落代码。

## 先读顺序

1. `CONTEXT.md`：项目术语和不要混用的词。
2. `策划案.md`：原始玩法设定。
3. `docs/space-combat-framework.md`：系统架构、阶段计划和复用方向。
4. 本文档：写 Mod 时的约束、默认假设和实现检查表。

## Besiege 基本设定

Besiege 的玩家机器不是传统游戏里的单个 Actor，而是一组物理零件组成的机器。Mod 写法必须尊重这个前提：

- 零件是核心扩展单位。新能力应优先表现为 BlockScript 挂在自定义零件上，而不是凭空生成全局能力。
- 机器身份由玩家、机器和若干核心零件共同决定。BeyondSpider 当前以舰船核心作为太空战舰身份入口。
- 战斗发生在 Unity 物理世界中。高速弹体、导弹、护盾和雷达都要考虑 FixedUpdate、刚体速度、碰撞和性能。
- 多人模式下，能影响战斗结果的状态必须尽量由 host 侧计算或同步，客户端侧更适合做显示和输入。
- 建造模式参数通常通过 slider、toggle、menu、key 暴露，命名要稳定，避免破坏玩家存档。

## BeyondSpider 玩法设定

BeyondSpider 的目标不是堆更多炮，而是把 Besiege 机器升级成“有舰船系统的太空战舰”。

核心循环：

1. 舰船核心定义整船身份、能量预算、雷达中心和指挥入口。
2. 超级电容提供瞬时功率缓冲，让武器、护盾、装甲在高峰负载下不只是开关式失效。
3. 雷达面发现舰船、重型导弹、防御导弹和大型炮弹，生成传感器轨迹。
4. 防御指挥官消费轨迹，按威胁程度生成拦截解算。
5. 近防炮、点防御导弹、护盾、纳米装甲按解算和能量供应执行防御。
6. 激光、电磁炮、重型导弹等进攻系统复用同一套舰船身份、能量和轨迹体系。

## 当前可用实现

主要代码在 `src/BeyondSpiderAssembly`：

- `Mod.cs`：加载入口，创建全局运行时对象。
- `SpaceCombatCore.cs`：EnergyGrid（Armor/Shield/Weapon/Universal 四条 bus）、ShipState、SpaceCombatRegistry、运行时 HUD。
- `SpaceShipCore.cs`：舰船核心，配置总能量预算和三向分配（含 `ArmorShare`）。
- `SuperCapacitorBlock.cs`：按 bus 注册容量的超级电容。
- `RadarPanelBlock.cs`：雷达面，扫描 TrackRegistry 生成传感器轨迹。
- `DefenseDirectorBlock.cs`：消费传感器轨迹，生成拦截解算。
- `ShieldProjectorBlock.cs`：电磁场护盾，用抛物面（而非球形）容纳判定拦截高速实体威胁，无敌我识别，护盾维持自身也要耗电，含可视化。
- `CiwsBlock.cs`：单零件近防炮。
- `SpaceInterceptorLauncherBlock.cs`：点防御拦截导弹（含 `SpaceInterceptorMissile`）。
- `HeavyNuclearMissileBlock.cs`：重型核导弹（含 `ITrackable` 弹体）。
- `RailgunBarrelBlock.cs`：电磁炮管与炮手（`SpaceGunnerBlock`），命中伤害和耗电按口径/初速的动能公式推算，已从 EnergyGrid 的 Weapon bus 请求能量；击穿特效复用 WW2-Naval 移植的 `space-perice` 资源包。
- `SpaceFlakTurretBlock.cs`：多联装近防/对空炮台，同样从 Weapon bus 请求能量。
- `SpaceCaptainBlock.cs`：舰船指挥/UI 入口。
- `SpaceBallistics.cs`：`SpaceKineticRound` 等弹道实体。
- `SpaceBlock.cs`：所有 Block 的公共基类。

主要 Mod 文件在 `BeyondSpider`：

- `Mod.xml`：程序集、Block 和资源注册。
- `ShipCore.xml`、`Captain.xml`、`SuperCapacitor.xml`、`RadarPanel.xml`、`DefenseDirector.xml`、`ShieldProjector.xml`、`CiwsBlock.xml`、`HeavyNuclearMissile.xml`、`SpaceFlakTurret.xml`、`Railgun.xml`、`SpaceInterceptorLauncher.xml`、`SpaceGunner.xml`：当前已注册零件。

能量分配已经支持 Armor bus（`SpaceShipCore.ArmorShare`、`EnergyGrid.Charge`/`Request`），现在也有了真正的消费者：`NanoArmorBehaviour`（`NanoArmorBlock.cs`）挂在每个原版木头零件（`SingleWoodenBlock`/`DoubleWoodenBlock`/`Log`）上，维护 `Integrity`（自维护，从 Armor bus 按 `ratio^2` 非线性修复）和 `StructuralValue`（不自我修复，按比例削弱直到断开该零件的 joint）两段血量。`DamageRouter`（`DamageRouter.cs`）是统一入口，`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 的命中和 `HeavyNuclearMissileBlock` 的爆炸都会打到这里。"能量-火力-装甲"链路已经完整闭环。

**当前开发优先级：优先打通并验证"能量-火力-装甲"链路**（见下面推荐开发顺序），而不是继续加深"导弹-防御"链路。原先的导弹-防御骨架（雷达轨迹、防御指挥官、护盾、CIWS、点防拦截导弹、重型导弹）已经是可玩的第一切片，先保留作为回归对象；新功能应先补齐纳米装甲和伤害结算，再回头强化防御链路。详见 `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`。

## 术语约束

写代码和文档时优先使用这些概念：

- 舰船核心：整船身份、能量预算和指挥入口。
- 能量预算：建造模式分配的长期能力。
- 瞬时功率：短时间动作带来的功率压力。
- 超级电容：瞬时能量缓冲，不是普通“电池”。
- 电磁场护盾：只处理高速实体威胁，不挡激光。
- 纳米装甲：木头零件承载的自维护舰体防护。
- 雷达面：面向锥形空域的传感器零件。
- 传感器轨迹：雷达产生的目标记录，不等同于玩家锁定。
- 防御指挥官：把传感器轨迹变成拦截决策的控制零件。
- 炮手：控制玩家自建炮塔铰链和炮管的火控零件。

不要把舰船核心叫船长零件、反应堆零件或中控。不要把能量预算叫电量，也不要把传感器轨迹简单叫目标。

## 架构规则

优先遵守这个规则：

> blocks register capability, managers aggregate state, directors calculate decisions, weapons execute.

落到代码里就是：

- Block 只注册能力和执行本零件动作。
- Registry 维护可被其他系统查询的对象列表。
- EnergyGrid 统一结算能量，不要让每个武器私自维护整船能量。
- RadarPanel 只生成 SensorTrack，不直接命令武器。
- DefenseDirector 只做目标选择和 firing solution，不直接造成伤害。
- Weapon / Shield / Armor 消费 firing solution 或命中事件，并向 EnergyGrid 请求能量。

## 性能规则

Besiege 的机器可能有很多零件，太空战斗还会有远距离和高速目标。默认做法是注册表优先：

- 需要被雷达发现的对象实现 `ITrackable` 并注册到 `SpaceCombatRegistry`。
- 雷达扫描遍历 TrackRegistry，再做距离、角度、IFF 过滤。
- 不要在每个雷达上频繁做全场 Physics broad scan，除非是兼容旧对象的 fallback。
- 高频计算放在 FixedUpdate 或节流 tick 中；UI、Debug HUD 放在 OnGUI 或显示层。
- 对导弹、炮弹、轨迹列表要注意注销，避免假目标和悬空引用。

## 能量规则

第一版能量模型保持简单、可调、可观察：

- 舰船核心提供总反应堆输出，并按装甲、护盾、武器比例分配。
- 超级电容按 Armor、Shield、Weapon、Universal 四类 bus 存储。
- 消费者优先从对应 bus 抽取，不足时使用 Universal。
- EnergyGrid 返回 0 到 1 的满足比例，系统按比例降级。
- 纳米装甲修复适合用 `ratio^2` 这类非线性降级。
- 护盾拦截不足时应减速或削弱弹体，而不是必然完全挡下。
- 武器能量不足时可以延迟充能、降低持续时间或拒绝发射。

不要把能量不足做成所有系统立刻关闭。这个 Mod 的味道来自“功率不够时系统变差”。

## 防御交互规则

护盾、纳米装甲和点防分工明确：

- 电磁场护盾影响实体高速威胁：电磁炮弹、大型炮弹、导弹。
- 电磁场护盾没有敌我识别：本方发射的高速炮弹和导弹进入抛物面同样会被减速，不做阵营过滤。
- 激光不被护盾拦截。
- 威胁速度越高，护盾触发越明显，但能耗也越高。
- 慢速重型导弹靠质量和厚纳米外壳穿越护盾，但更容易被点防持续拦截。
- 完整纳米装甲反射大部分激光；实体命中会降低局部完整度，使激光后续更有效。
- 近防炮适合近距离、小型、快速目标。
- 点防御导弹适合中距离导弹和大型炮弹。
- 中口径自动炮适合重型慢速导弹和反舰。

## 写新功能前检查

动手前先回答这些问题：

1. 这个能力属于舰船系统、传感器、指挥官、武器、防御，还是伤害结算？
2. 是否需要一个新 Block，还是应该扩展已有 Block？
3. 目标是否应实现 `ITrackable`？
4. 是否需要从 EnergyGrid 请求能量？
5. 是否需要 host 权威计算？
6. 是否会破坏已有 XML block ID、字段名、slider key 或存档兼容？
7. 是否能先接入第一可玩切片，而不是做孤立功能？

## 推荐开发顺序

**当前高优先级：验证"能量-火力-装甲"链路**（进攻侧闭环：分配能量 → 开火 → 命中装甲产生削弱/修复）。

1. ~~实现纳米装甲 Block/行为~~ 已完成：`NanoArmorBehaviour` 挂在原版木头零件上，`Integrity`/`StructuralValue` 两段血量，`ratio^2` 非线性自修复。
2. ~~实现 DamageRouter~~ 已完成：`DamageRouter.RoutePhysicalHit` 是统一入口。
3. ~~让炮的命中结果影响装甲 integrity~~ 已完成：`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 经 `SpaceKineticRound.OnCollisionEnter` 打到 `DamageRouter`；`HeavyNuclearMissileBlock` 爆炸按距离打到附近所有装甲。
4. ~~打通 HUD 显示~~ 已完成：`SpaceCombatRuntime.OnGUI` 显示装甲零件数和平均 Integrity/Structural 百分比；同一窗口内的 `Show Armor HP` 勾选框（`SpaceCombatRuntime.ShowArmorHP`，全局静态，仿照 WW2-Naval `ModController` 的 `ShowArmour`/`ShowCrew` 勾选框，而不是挂在某个 Block 上）控制场景内的绿/红半透明可视化。
5. 补一个最小验证场景（一门炮 + 一段纳米装甲 + 能量预算），手动改变能量分配验证伤害/削弱是否符合"功率不够时系统变差"的设计味道——仍待手动在 Besiege 里搭建验证，不是代码任务。
6. 结构断裂之后的后续效果（零件真的飞出去之后怎么处理、是否需要额外的连锁反应）、激光炮管与 `LaserDamageMultiplier` 的实际对接，留给后续。

完成以上后再回头强化"导弹-防御"链路（原优先级，降为次高）：

7. 稳定舰船注册：处理多核心、无核心、模拟停止、玩家机器切换。
8. 强化雷达轨迹：去重、可信度、最后更新时间、按舰合并 track picture。
9. 强化 DefenseDirector：目标分配、过量拦截控制、武器类别选择。
10. 强化重型导弹：发射状态、制导、生命值、爆炸效果、网络显示。
11. ~~强化护盾：从球形距离检测过渡到可配置抛物面/扇面场~~ 已完成抛物面部分：`ShieldProjectorBlock` 现在用真正的抛物面容纳判定（`Radius`/`Depth` 两个滑条），去掉了敌我识别（对本方发射物同样生效），新增护盾维持本身的电量开销，以及可选常显/拦截闪烁的护盾罩可视化。扇面场变体仍留待后续。
12. 再添加激光、电磁炮、炮手模式切换和进攻 UI 细化。

## 验收标准

一个改动完成时，至少满足：

- 代码能编译。
- 新零件已在 `BeyondSpider/Mod.xml` 注册。
- 新 BlockScript 类名和 XML 中引用一致。
- 模拟开始和停止都会注册/注销。
- 没有每帧无限增长的列表或未清理对象。
- HUD 或 Debug.DrawLine 能帮助验证第一版行为。
- 玩法行为符合“能量、传感器、指挥官、武器执行”的链路。

## 备注给后续 agent

如果用户补充新的世界观或 Besiege 机制，优先把稳定设定写进本文档的对应章节；如果是术语，写进 `CONTEXT.md`；如果是架构决策，新增 `docs/adr/`。不要把新设定只留在聊天记录里。
