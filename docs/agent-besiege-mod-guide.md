# BeyondSpider Agent Mod Guide

这份文档给后续 agent 写 BeyondSpider / Besiege Mod 时使用。它不是玩家说明书，而是实现参考：先理解 Besiege 的限制，再按 BeyondSpider 的太空战舰设定落代码。

## 先读顺序

1. `CONTEXT.md`：项目术语和不要混用的词。
2. `策划案.md`：原始玩法设定。
3. `docs/space-combat-framework.md`：系统架构、阶段计划和复用方向。
4. 本文档：写 Mod 时的约束、默认假设和实现检查表。

## 输入采样规则

所有 `MKey` 瞬时输入必须在 `SimulateUpdateHost()` 中检测；如果客户端只需要做本地显示状态，也可以在 `SimulateUpdateClient()` 中检测。不要只在 `SimulateFixedUpdateHost()` 中读取 `MKey.IsPressed` 或 `MKey.IsHeld`：Besiege 的模拟流速可能很低，FixedUpdate 间隔变长后会漏掉短按。

如果按键触发的是物理、耗能、发射、生成弹体或刚体受力，做法是先在 `SimulateUpdateHost()` 中锁存一个布尔请求，再在 `SimulateFixedUpdateHost()` 中消费该请求并执行实际逻辑。能量检查、装填检查、投射物生成、`Rigidbody` 操作仍然放在 `SimulateFixedUpdateHost()` 中。

`MToggle`/`MSlider`/`MText`/`MMenu`——所有用 `AddToggle`/`AddSlider`/`AddText`/`AddMenu` 暴露的字段——在模拟中**不能被玩家改动**，只有 `AddKey`/`AddEmulatorKey` 创建的 `MKey` 才绑定了一个能在模拟中实时触发的按键。`OnSimulateStart` 时读到的 `MToggle`/`MSlider`/`MText`/`MMenu` 值会在整场模拟里保持不变，除非 Mod 自己调用 `SetValue()` 改它。不要为了"玩家可能在模拟中改掉这些值"去反复重新读取或重新做匹配/绑定——例如两个零件是否共享同一个 `GunGroup`/口径这类配置，判一次、缓存住就够了，没必要每隔几百毫秒重新扫描整条注册表。真正需要在模拟期间持续处理的只有两件事：模拟开始时的注册时序竞争（见下面"注册时序规范"），以及被绑定的对象在模拟中被摧毁需要清理引用。

### 扳手菜单里的只读信息：MInfo

Besiege 没有原生的"只读信息"控件——`AddKey`/`AddSlider`/`AddToggle`/`AddText`/`AddMenu`/`AddLimits` 是官方文档化的字段列表。但游戏本体确实留了一个自定义类型扩展点，而且是真正可用的（第一版文档曾经因为找不到具体用例而判定"不值得接入"，后来参考社区 mod `BlockTransformValues`（`Besiege_Data/Mods/BlockTransformValues/BlockTransformValues.dll`，作者用它在扳手菜单里显示/编辑方块的 Transform）反编译确认了完整调用链，结论反过来了）：

- `Modding.Mapper.MCustom<T>`（继承自 `MapperType`）：不带玩家控件的数据类，子类只需实现 `SerializeValue`/`DeSerializeValue`；`Value` 的 setter 会自动 `InvokeChanged` 触发 `Changed` 事件。
- `Modding.Mapper.CustomSelector<T,TMapper> where TMapper : MCustom<T>`（继承自 `Selectors.Selector`）：真正画 UI 的地方，重写 `CreateInterface()`（建一次视觉子物体）和 `UpdateInterface()`（刷新显示，`CreateInterface` 结尾会调一次，`TMapper.Changed` 触发时引擎也会自动再调）。基类暴露 `protected SelectorElements Elements`（`MakeText`/`MakeBox`/`MakeTexture`/`AddButton` 等运行时建 UI 的方法，不需要自己做 AssetBundle/预制体）、`protected SelectorMaterials Materials`（`LightBackground`/`DarkBackground`/`DarkElement`/`RedHighlight` 几个现成材质）、`protected Transform Content`/`Background`、`protected TMapper CustomMapperType`。`Elements`/`Materials` 都是 `Modding.Mapper` 命名空间下的类，游戏本体自带，不依赖任何外部 mod。
- `Modding.Mapper.CustomMapperTypes.AddMapperType<T,TMapper,TSelector>()`：在 `Mod.OnLoad()` 里调一次，把 `TMapper`（数据类）和 `TSelector`（UI 类）注册成一对。
- 挂到具体字段：`ModBlockBehaviour.AddCustom<T>(MCustom<T> custom)`（`SafeAwake()` 里跟 `AddToggle`/`AddSlider` 同级调用），返回值静态类型是 `MCustom<T>`，需要强转回具体子类。

`BlockTransformValues` 的 `TransformSelector.CreateInterface()` 里还用了 `ToggleButton`/`Tooltip`/`UIFactory`/`UIConfig` 这些类——那些是那个 mod自己写的辅助层，不是引擎 API，不用照抄；它的 `.csproj` 里还多引用了一个本机根本不存在的 `Besiege.UI.dll`（大概率是作者本地一个共享库、没有随 mod 分发），如果以后再翻这个 mod 的反编译代码，看到解析不出来的引用不用纠结，`CustomSelector`/`SelectorElements`/`SelectorMaterials`/`CustomMapperTypes` 这几个核心类完全在 `Assembly-CSharp.dll` 里，自成一体。

`MInfo.cs`（`BeyondSpiderAssembly` 命名空间）把上面这套包了一层：`MInfo : MCustom<string>`（序列化是占位实现，不持久化任何东西——纯显示值，没必要存档）+ `InfoSelector : CustomSelector<string, MInfo>`（`CreateInterface` 里用 `Elements.MakeText` 建两个文本节点：一个显示 `DisplayName` 当标题，一个显示 `Value` 当内容——别只建一个只画 `Value` 的，那样玩家根本看不出这一行是什么参数）+ `Mod.OnLoad()` 里的 `CustomMapperTypes.AddMapperType<string, MInfo, InfoSelector>()` 注册。

`AddInfo(displayName, key[, initialValue])` 声明在 `SpaceBlock.cs`，是普通实例方法，不是 extension method——第一版写成 `public static class MInfoExtensions { public static MInfo AddInfo(this ModBlockBehaviour block, ...) }` 能编译通过，但在 `SafeAwake()` 里像 `AddSlider`/`AddToggle` 那样裸调用 `AddInfo(...)` 直接报 `CS0103`：C# 的 extension method 必须写成 `receiver.Method()` 才能被找到，裸方法名只走普通成员查找，不会 fallback 到 extension method——哪怕调用点和 extension method 定义在同一个 namespace 也不行。改成 `SpaceBlock` 上的普通实例方法（`return (MInfo)AddCustom(new MInfo(...));`）之后才能像 `AddSlider` 一样裸调用。以后再给 `BlockScript`/`SpaceBlock` 加"应该和 AddXXX 一起裸调用"的帮助方法，直接写成实例方法，不要走 extension method 这条路。

用法：`SafeAwake()` 里 `AddInfo(displayName, key)` 拿到 `MInfo`，之后随时 `.Set(string)`（值不变时跳过，避免空转触发刷新）。已经在实机里验证过扳手菜单会正确显示标题+数值两行（"MInfo显示没问题"）。

**用例 1**：`RailgunBarrelBlock` 的口径/倍径/初速已经从玩家可调的 `MSlider` 改成纯只读的 `MInfo`，由方块自身的 `transform.localScale` 实时推算（`RecomputeBallistics()`）：口径(mm) = 400×sqrt(scale.x×scale.y)，倍径 = 16000×scale.z÷口径，初速(m/s) = 65×倍径（默认缩放 1,1,1 时口径400mm/倍径40/初速2600，和改动前滑条的默认值对齐，但两者数值来源已经完全不同——不要假设默认值对齐等于行为对齐）。这类"数值来自 transform、不来自玩家输入"的 MInfo，必须在 `BuildingUpdate()`（建造模式里玩家拖缩放手柄时）和 `SimulateFixedUpdateAlways()`（模拟中——注意是 `Always` 不是 `Host`，缩放是纯几何量不需要 host 权威，客户端也要能在自己屏幕上看到这门炮当前的口径）两个钩子里都重新算一遍，不能只在 `OnSimulateStart` 算一次，否则建造模式里玩家缩放炮管时数值不会刷新。这也是一次故意的存档兼容性破坏：老存档里 `BSRailgunCaliber`/`BSRailgunVelocity` 两个滑条 key 保存的玩家自定义数值会变成孤儿数据（不再被任何字段读取），该炮会完全按新公式重新计算，不再读取玩家以前手调的口径/初速。

**用例 2**：同样的模式后来又用在了 `SuperCapacitorBlock`（容量）和 `SpaceShipCore`（总发电量/反应堆输出）上，两者也从玩家可调 `MSlider` 改成了纯只读 `MInfo`，由方块自身 `transform.localScale` 的**体积**（x×y×z，注意不是 `SuperCapacitorBlock` 原来用的 `lossyScale.magnitude`）实时推算：`SuperCapacitorBlock.Capacity = 1000 × 体积`（MJ），`SpaceShipCore` 反应堆输出 = `1200 × 体积`（MW）。这两个常数（1000、1200）不是用户给的精确公式——用户只说"通过体积直接计算"，没给系数——是为了让默认缩放（1,1,1）下的数值贴近改动前的滑条默认值（容量 1039→1000，总电量 1200→1200 正好对上）反推出来的线性关系，确定性比 `RailgunBarrelBlock` 那三个用户逐一确认过的公式低得多，如果游戏里数值手感不对先怀疑这两个常数。两个方块同样需要 `BuildingUpdate()` + `SimulateFixedUpdateAlways()` 双钩子实时重算；`SuperCapacitorBlock.CapacityScale`/`SpaceShipCore.TotalPower` 两个旧滑条 key（`BSCapacity`/`BSTotalEnergy`）同理变成存档孤儿数据。`SuperCapacitorBlock.BusMenu`（选哪条能量总线）和 `SpaceShipCore` 的三向分配份额（`ArmorPowerShare`/`ShieldPowerShare`/`WeaponPowerShare`）不属于"能从体积/transform 推出来"的量，仍然是玩家可调的 `MMenu`/`MSlider`，没有改动。

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
- `SpaceShipCore.cs`：舰船核心，配置总能量预算和三向分配（含 `ArmorShare`）。总能量预算（反应堆发电量）不再是玩家可调的 `MSlider`，改成由 `transform.localScale` 体积实时推算并通过 `MInfo` 只读展示，三向分配份额仍是玩家可调的 `MSlider`（见"扳手菜单里的只读信息：MInfo"一节）。
- `SuperCapacitorBlock.cs`：按 bus 注册容量的超级电容。容量同样从 `MSlider` 改成了由体积实时推算、`MInfo` 展示，`Bus` 选哪条能量总线仍是玩家可调的 `MMenu`。
- `RadarPanelBlock.cs`：雷达面，扫描 TrackRegistry 生成传感器轨迹。`ship.Tracks` 报告范围/锥角内的一切目标，不再按敌我过滤（见 `docs/adr/0004-hostile-filtering-moves-to-consumers.md`）；`Iff` 滑条为存档兼容保留，目前未被消费。
- `DefenseDirectorBlock.cs`：消费传感器轨迹，自己用 `SpaceBallistics.IsHostile` 过滤出敌方目标，再生成拦截解算。
- `ShieldProjectorBlock.cs`：电磁场护盾，用抛物面（而非球形）容纳判定拦截高速实体威胁，无敌我识别，护盾维持自身也要耗电，含可视化。
- `CiwsBlock.cs`：单零件近防炮。
- `SpaceInterceptorLauncherBlock.cs`：点防御拦截导弹（含 `SpaceInterceptorMissile`）。
- `HeavyNuclearMissileBlock.cs`：重型核导弹（含 `ITrackable` 弹体）。
- `RailgunBarrelBlock.cs`：电磁炮管与炮手（`SpaceGunnerBlock`），命中伤害和耗电按口径/初速的动能公式推算，已从 EnergyGrid 的 Weapon bus 请求能量；击穿特效复用 WW2-Naval 移植的 `space-perice` 资源包。口径/倍径/初速不再是玩家可调的 `MSlider`，改成由 `transform.localScale` 实时推算并通过 `MInfo` 只读展示（见"扳手菜单里的只读信息：MInfo"一节的用例）。
- `SpaceFlakTurretBlock.cs`：多联装近防/对空炮台，同样从 Weapon bus 请求能量。
- `SpaceCaptainBlock.cs`：舰船指挥/UI 入口，持有全舰目标锁定权威（`ShipState.LockedTarget`/`LockedSolution`），并托管 `CaptainLockNet` 联机同步。
- `CaptainRadarView.cs`：船长的 3D 雷达图（口袋维度相机渲染 + `OnGUI` 面板），显示舰船/重型导弹为箭头/圆球，支持右键旋转、滚轮缩放、左键点选锁定。
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

## 注册时序规范

`OnSimulateStart` 在同一台机器的所有零件之间**没有确定的先后顺序保证**——手动把一个子系统零件放到已经在模拟的船上时，`SpaceShipCore` 早就注册完了，`OwnShip()` 自然能拿到 `ShipState`；但从存档加载时，整机所有零件是"一起"开始模拟的，谁的 `OnSimulateStart` 先跑完全不保证。任何在 `OnSimulateStart` 里"仅当 `ship != null` 才注册进 `ship.XXX` 列表、失败了也不重试"的写法，都会在这种加载顺序下静默地永久注册失败（`SpaceCaptainBlock` 曾经就是这样：存档加载后船长按钮消失，重新放置船长方块才会出现）。

区分两类注册：

- **不依赖其他零件的注册**：`SpaceShipCore.RegisterCore` 本身就是创建 `ShipState` 的那一步，没有前置依赖，留在 `OnSimulateStart` 里，不需要改。
- **依赖 `ShipState` 已存在的注册**（几乎所有其它子系统零件：`RadarPanelBlock`/`DefenseDirectorBlock`/`ShieldProjectorBlock`/`CiwsBlock`/`SuperCapacitorBlock`/`SpaceInterceptorLauncherBlock`/`SpaceFlakTurretBlock`/`RailgunBarrelBlock`/`SpaceGunnerBlock`/`SpaceCaptainBlock` 往 `ship.XXX` 列表或 `ship.Captain` 等字段里写自己）：
  1. `OnSimulateStart` 里仍然按原样尝试注册一次（覆盖"手动放置到已运行的船上"这个最常见场景，不必等一帧）。
  2. 额外维护一个 `private bool registered;` 字段，成功注册时置 `true`，`OnSimulateStop` 里清理注册的同时置回 `false`。
  3. 在 `SimulateFixedUpdateHost`（每 tick 都跑）的最前面加一段自愈重试：`ShipState ship = OwnShip(); if (ship != null && !registered) { 注册; registered = true; }`——这段要放在该方法**任何提前 return 之前**（包括 `if (!DefaultActive.IsActive) return;` 这类开关判断），否则零件被关闭时会连注册都做不了。

新写一个需要往 `ship.XXX` 注册自己的子系统零件时，直接照抄这个模式，不要只在 `OnSimulateStart` 里写一次。

## SteeringWheel 驱动规则

用 API 直接写 `SteeringWheel.AngleToBe` 驱动铰链时（炮手/自动炮塔这类场景，没有玩家真的按着键），不要把 `ReturnToCenterToggle` 稳定设成 `false` 就完事。反编译 `Assembly-CSharp.dll` 里的 `SteeringWheel.FixedUpdateBlock` 可以看到：当 `input==0`（没人按键时恒成立）且 `ReturnToCenterToggle` 为 `false`，方法会走一条提前跳转的分支，直接跳过检查 `Rigidbody.IsSleeping()` 并调用 `WakeUp()` 的那段代码（铰链自身的刚体和关节 `connectedBody` 都在跳过范围内）。铰链闲置一段时间后刚体会被 Unity 物理引擎置为休眠，一旦休眠，后续对 `targetRotation`/`targetAngularVelocity` 的写入会被静默忽略，直到有什么东西把它唤醒——包括我们自己写的 `AngleToBe` 变化。

最早的解法是每 10 tick 有 1 tick 把 `ReturnToCenterToggle` 打开、`SpeedSlider` 压到 0.01，靠这个强制走一遍完整代码路径顺带触发唤醒——能用，但是把一个跟"回中"毫不相关的效果耦合在一个建造模式开关上，还会在那一 tick 里短暂重新引入 vanilla 回中拉力（哪怕幅度可忽略）。更干净的做法是 `SpaceGunnerHinge` 现在用的：`Awake()` 里额外 `GetComponent<Rigidbody>()`/`GetComponent<Joint>()` 缓存住铰链自身刚体和 `Joint.connectedBody`，`FixedUpdate` 每 tick 直接对这两个刚体判断 `IsSleeping()`/调用 `WakeUp()`，`ReturnToCenterToggle` 全程稳定保持 `false`，不再需要周期性切换。真正的可见抖动另有原因，来自瞄准逻辑本身把命令角度一次顶到底、没有减速缓冲（见 `RailgunBarrelBlock.EaseStep`），跟这里的刚体唤醒无关，不要混为一谈去动 `ReturnToCenterToggle`。

## Vis 层级的旋转基准

`SpaceFlakTurretBlock`/WW2-Naval 的 `AABlock` 共用同一套 `myVis`/`BaseBase`/`GunBase` 层级，`InitSimulationModel` 里给 `simRoot` 设了固定的 `localEulerAngles = (90,0,0)`。这个偏移只是为了让美术模型摆正，但意味着炮的机械零位（yaw=0、pitch=0）指向的是 `transform` 的本地 `-up`，不是 `transform.forward`；真正的偏航轴（炮塔转台的竖直轴）是 `transform.forward`。

算目标 yaw/pitch 时不要用 `Quaternion.Inverse(transform.rotation) * Quaternion.LookRotation(delta, transform.up)` 再取 `eulerAngles.y`/`.x`——这隐含假设零位是 `transform.forward`，跟上面这套层级的实际零位差了约 90°，`SpaceFlakTurretBlock` 曾经因为这个瞄错方向。正确做法是照抄 WW2-Naval `AATurretController.GunYaw`/`TargetYaw`/`GunPitch` 的基准：yaw 以 `-transform.up` 为零点、绕偏航轴算带符号夹角（`SpaceFlakTurretBlock.SignedAngleAroundAxis`），pitch 用 `90 - Vector3.Angle(目标方向, 偏航轴)`。WW2 那边直接用世界 `Vector3.up` 当偏航轴，因为船体永远正着漂在水面上；BeyondSpider 的飞船在太空里可以任意朝向，必须换成炮塔自己的 `transform.forward`，不能照抄世界轴。

以后再给别的零件（不管是不是复用这套 Vis 层级）写"给一个世界坐标方向、求本地 yaw/pitch"的逻辑时，先确认清楚零位方向和偏航轴到底是哪两个向量，不要想当然地套 `LookRotation` + Euler 分解。

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
5. ~~补一个最小验证场景（一门炮 + 一段纳米装甲 + 能量预算），手动改变能量分配验证伤害/削弱是否符合"功率不够时系统变差"的设计味道~~ 已在 Besiege 里手动验证完成。"能量-火力-装甲"链路视为已闭环验证，可以回头强化"导弹-防御"链路。
6. 结构断裂之后的后续效果（零件真的飞出去之后怎么处理、是否需要额外的连锁反应）、激光炮管与 `LaserDamageMultiplier` 的实际对接，留给后续。

完成以上后再回头强化"导弹-防御"链路（原优先级，降为次高）：

7. 稳定舰船注册：处理多核心、无核心、模拟停止、玩家机器切换。
8. 强化雷达轨迹：去重、可信度、最后更新时间、按舰合并 track picture。
9. 强化 DefenseDirector：目标分配、过量拦截控制、武器类别选择。
10. ~~强化重型导弹：发射状态、制导、生命值、爆炸效果、网络显示~~ 制导和网络显示已完成：`HeavyNuclearMissileBlock` 发射后实时跟随全舰当前锁定（`ship.LockedTarget`），雷达丢失目标时主动减速到静止，从未锁定则沿本体朝向直飞；`IsAlive` 现在要求 `launched`，未发射的导弹（含盟友的）不会出现在任何雷达上。爆炸效果与生命值模型本身未变动，仍是后续项。
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
