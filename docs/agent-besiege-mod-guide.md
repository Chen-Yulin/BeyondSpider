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

**用例 3**：`SpaceFlakTurretBlock` 的跟踪速度和射程也从玩家可调的 `MSlider` 改成了纯只读 `MInfo`，但推算来源不是 `transform.localScale`，而是另外两个仍然存在的字段——`Type` 菜单（决定口径）和保留下来的 `EnergyPerSecond`（Energy Use）滑条：跟踪速度 = `120 ÷ 口径`（clamp 到 0.2~6，口径越粗跟踪越慢，替掉了原来 `DriveTurret()` 里 `caliber < 76f ? ... : ... * 0.5f` 的临时特判，避免和新公式的口径惩罚重复叠加两层）；初速沿用原有的口径项 `190 + 42×√口径`，再加上 `Energy Use × 0.5`（clamp 到 280~1000，上限比原来的 720 更高，给 Energy Use 留出实际能推动数值的空间）；射程 = 初速 × 1.6（一个固定飞行时间预算，clamp 到 80~1800），因此射程不需要再单独吃一次 Energy Use，只是随初速联动。三者都在 `RecomputeStats()` 里一起算：`ResolveType()`（`Type` 改变、口径跟着变时）调一次，`BuildingUpdate()` 里再无条件调一次以跟上 `Energy Use` 滑条的实时拖动。和用例 1/2 不同的是，这里**不需要** `SimulateFixedUpdateAlways()` 钩子——参见"输入采样规则"一节，`MSlider`/`MMenu` 的值在 `OnSimulateStart` 之后整场模拟保持不变，不像 `transform.localScale` 那样是模拟中还会持续变化的纯几何量，`OnSimulateStart` 里 `ResolveType()` 已有的那次调用足够锁定整场模拟用的值。老存档里 `BSSpaceFlakRange`/`BSSpaceFlakTracking` 两个滑条 key 同理变成孤儿数据；`EnergyPerSecond`（`BSSpaceFlakEnergy`）本身没有改动，仍是玩家可调的 `MSlider`。

### 运行时 HUD 面板：程序化 uGUI + IMGUI 兜底

`SpaceCombatRuntime.OnGUI` 最早是唯一的运行时信息面板（纯 `GUILayout`），现在被拆成两条腿：左侧可折叠的 `BeyondSpiderInfoPanel`（真正的 uGUI）是主显示，`SpaceCombatRuntime.OnGUI` 保留作为自动兜底，靠 `if (StatMaster.hudHidden || BeyondSpiderInfoPanel.PanelReady) return;` 互斥——不是没删干净的死代码。

调研过一个同目录下的独立 mod "UI Factory"（`Besiege_Data\Mods\UIFactory\`，`Besiege.UI.dll`/`Besiege.UI.Bridge.dll`）和一个真实用到它的 mod "Instrumentality"（同目录 `Instrumentality\`，同作者），反编译（`Mono.Cecil`，`Besiege_Data\Managed\Mono.Cecil.dll` 本身就在游戏自带的 Managed 目录里，不需要额外装反编译器）确认了它的真实用法：`Besiege.UI.Make.LoadProject(package, projectName, parent)` 从一个手工/编辑器存的 JSON 反序列化出一整棵 uGUI 层级，再用 `project["ElementName"].GetComponent<T>()` 按名字取具体控件。**这条路径最终没有采用**——用户明确要求不依赖单独安装的 UI Factory mod，改成把从它身上学到的样式/结构直接搬进 BeyondSpider 自己的代码：`BeyondSpiderUI.cs` 是一套纯 `UnityEngine.UI`（游戏自带，不是 UI Factory 的程序集）的建面板/建文字/建进度条/建可折叠表头帮助方法，颜色数值（面板深色半透明 `rgba(0.0235, 0.0235, 0.0549, 0.53)` 等）是从 Instrumentality 真实存档 JSON 里读出来的，不是拍脑袋定的。

关键点，后续再加 uGUI 面板时照抄：

- `BeyondSpiderUI.GetOrCreateRootCanvas()` 优先找场景里已有的 `GameObject.Find("HUD")`/`Object.FindObjectOfType<Canvas>()` 挂靠，找不到才自建一个 `ScreenSpaceOverlay` Canvas；同时确保场景里有 `EventSystem`（没有才建，避免多个 EventSystem 打架）。结果缓存在静态字段里，多个面板共用同一个 Canvas。
- **这个游戏绑定的 `UnityEngine.UI.dll`是旧版本**——`HorizontalOrVerticalLayoutGroup` 没有 `childControlWidth`/`childControlHeight`（后来 Unity UI 版本才加的），只有 `childForceExpandWidth`/`childForceExpandHeight`。这个版本的 layout group 本来就总是从子物体的 `LayoutElement`/固有尺寸取值来控制子物体大小，不需要（也不能）显式打开这个开关——写代码前最好用反射（`[Reflection.Assembly]::LoadFrom` 之类）现查一遍这个 `UnityEngine.UI.dll` 的真实成员，不要照抄新版 Unity 教程的属性名，编译期就会报 `CS1061`。
- 只读的进度条不用 `Slider`（那是给玩家拖的），用 `Image` 设 `type = Filled` + `fillMethod = Horizontal`，代码里逐帧改 `fillAmount`——这也是 Instrumentality 真实存档里的做法，它的两份 JSON 里完全没出现过 `Slider` 类型。
- 每个 uGUI 面板的构建都包一层 `try/catch`：失败就把某个 `public static bool XxxReady` 置 `false` 并 `Debug.LogWarning`，调用方（或者 IMGUI 兜底）据此降级，不让一次构建失败拖垮整个 Mod 加载。
- 新建的 GameObject 默认是激活状态——如果面板要跟着某个 `bool IsOpen` 之类的开关走，必须在构建完成时就显式 `SetActive(false)` 一次（否则会在开关状态生效之前的那几帧/那整段时间里提前露出来），并且在每个改变开关状态的地方都同步维护 `SetActive`，不能假设"这一帧没调用相关方法＝没画"这种 IMGUI 时代的直觉在 uGUI 里还成立。
- `CaptainRadarView` 的雷达面板是"uGUI 当容器、IMGUI 当前台"的混合体：真正的 `RenderTexture` 内容、辉光/扫描线叠加、点选锁定这些仍然是 IMGUI（`OnGUI`/`Physics.RaycastAll`），只有面板背景色块和折叠表头挪成了 uGUI；IMGUI 每帧通过 `RectTransform.GetWorldCorners` 读取 uGUI Body 当前的屏幕像素范围（`ScreenSpaceOverlay` Canvas 下 world corners 就是屏幕像素，Y 轴朝上，和 `Screen.height - Input.mousePosition.y` 那套换算是同一个道理），转换成 IMGUI 用的 `Rect`——两套系统各管一半，互不重写对方的输入/绘制逻辑。

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
4. 舰长在防空通道（Channel 0）持续锁定最高威胁的传感器轨迹（ADR-0012，取代了原来独立的防御指挥官方块）。
5. 近防炮、点防御导弹、护盾、纳米装甲按防空通道的锁定目标和能量供应执行防御。
6. 激光、电磁炮、重型导弹等进攻系统复用同一套舰船身份、能量和轨迹体系。

## 当前可用实现

主要代码在 `src/BeyondSpiderAssembly`：

- `Mod.cs`：加载入口，创建全局运行时对象。
- `ShipPoseNet.cs`：舰船位置全精度联机复制（ADR-0021）。引擎把方块位置压进 6 字节/48 位（17X/17Z/14Y，相对 `NetworkCompression.worldBounds`），远距离精度不够且无法改（`POS_SIZE` const、无 Harmony、装不下更多位）。改成 mod 自己发 **per-cluster-base 全精度位姿**（复用 `machine.simClusters` 分解，**只有 cluster base 流 transform**，一条覆盖整个刚性部件），并在 host 端 `NetworkBlock.pollTransform=false` 关掉引擎自带流去重。**client apply 必须反射写引擎私有字段 `NetworkBlock.transformMatrix`**（子块每帧从这个缓存矩阵重算、`posActive` 私有一次置真永不复位、无 public setter；抑制 base 流会冻住此矩阵→子块冻结），这是本 mod 首次用运行时反射（≠Harmony，缓存 `FieldInfo`），耦合到私有字段名是已知代价。host 用 `machine.simClusters`（`Machine.SimCluster` 是嵌套类型，写全名）+ 死区 + 1s 关键帧 + `MaxRecordsPerShip` 上限；client 在 `LateUpdate` lerp base.transform 再反射写矩阵，`StaleTimeout` 后停手让引擎流自愈。gate 在 `SpaceBoundary.Instance.BoundaryOff`，单机完全不跑。**待双开实测**。
- `HostControlPanel.cs` / `HostControlNet.cs`：房主控制板（ADR-0020）。左上角可折叠 uGUI 面板，**折叠是把 body 水平滑出屏幕左缘**（header 留作重开手柄，靠屏幕边缘裁剪、不用 mask），照抄 `BeyondSpiderInfoPanel` 的 `BeyondSpiderUI` 帮助方法。按角色重建：房主（`NetAuthority.IsAuthority`）显示 `NO BOUNDARY`/`DEFOG` 两个开关（驱动 `SpaceBoundary.Instance` 的两个 flag）；client（`NetAuthority.IsClient`）显示只读房间信息（角色/两个状态/`StatMaster.activePlayerCount`）。`HostControlNet`（`SingleInstance` + `StateMsg(bool,bool)`）房主变更即广播 + 1s 心跳（心跳短是因为压缩盒扩大要 host/client 一致，否则中途加入的 client 会有 ≤1s 全方块错位窗口），client 只收不发。**标签用 ASCII 英文**（Arial 无 CJK 字形，中文会变方框，同雷达/信息 HUD）。
- `SpaceBoundary.cs`：去除 Besiege 空气墙**和地面碰撞**（ADR-0019），去雾（ADR-0020）。ADR-0020 后**不再按"有无舰船核心"自动触发**，改成 `SingleInstance` 持有两个 public flag `BoundaryOff`/`DefogOn`（房主面板写、client 由 `HostControlNet` 写），`Update` 只把场景对齐到 flag。**注意**：既然不再自动去边界，超大船必须**房主先在建造模式开 无边界** 才能开模拟（出界门控是 `Bounding.Enabled`）。去雾 = `Camera.main.farClipPlane=8km` + `RenderSettings.fog=false` + 关掉 `Main Camera` 下名字含 "fog" 的雾体子物体（MAC `ModController.SetFog` 关的那些 `Fog Volume`/`Fog Volume Dark`/`FOG SPHERE`），每轮重申（相机换/雾体重建会把雾带回来），记录关了哪些以便还原；纯本地相机、同步只是让 client 也给自己去雾。空气墙有三层：①每场景一个 `BoundingBoxController`——场景里一有 `SpaceShipCore`（建造或模拟）就调引擎自带的 `machine.boundingBoxController.DisableBounds(machine)`（god-mode "Disable Bounding Box/取消边界" 按钮 / 关卡组件 `StartWithoutBounds` 用的同一个公开方法：关 6 面墙 collider + 把 `StatMaster.Bounding.*Pos` 推到 ±10000 + `Bounding.Enabled=false`，后者还门控"出界拒绝开模拟"，所以必须在建造模式就移除，大船才能开模拟）；②多人关卡里独立的 `WORLD BOUNDARIES` 场景物体的 collider（MAC resize 的就是这个）；③**地面/地板是完全独立的实体碰撞体，`DisableBounds` 不碰**——Besiege 把"地面"定义为图层 24(Environment)/28(BrickWall)/29(Floor)（见 `MachineGround.CreateLayerMask(24,28,29)`，图层号从 `globalgamemanagers` 的 layer 表读出），`SpaceBoundary` 只在模拟中（`levelSimulating`，建造模式留着地面给放置工具用）关掉这三层上的**实体(非 trigger)碰撞体**（trigger 是重力/水/击杀区必须留，同 `SpaceCombatUtil.IsUnrecognizedObstacle` 的 isTrigger 语义），并记住关了哪些好精确还原。没有舰船核心时 `EnableBounds` 还墙、模拟停止时还地面，不影响原版搭建。不照抄 `../ModernAirCombat`（把墙 resize 到 20km + 按名字 toggle `FloorBig`）：引擎自带杠杆 + 按图层更稳，且避免 `worldExtents` 变大粗化多人 `NetworkCompression` 的位置量化。反编译取证：`ilspycmd -p -o <dir> Assembly-CSharp.dll`（`ilspycmd` 在 `~/.dotnet/tools`）。
- `SpaceCombatCore.cs`：EnergyGrid（Armor/Shield/Weapon/Universal 四条 bus）、ShipState、SpaceCombatRegistry、运行时 HUD。多舰之后（ADR-0011）ShipState 带 `Machine`/`PlayerID`/`CoreGuidHash`/`Name`/`Cores`（多核心合并出力）/`HullSize`/`HullVolume`；Registry 以 List + `blockToShip` 字典维护，`ActiveLocalShip` 是 HUD 面板当前选中的本地舰。
- `ShipPartition.cs`：第 3 帧连通性分区（ADR-0011）——simClusters + BlockJoints + 物理 joint 三层邻接的图 BFS，把机器切成若干舰、集中登记子系统、算核心本地 OBB 包络盒；`LateResolve` 处理模拟中补放置的方块。
- `SpaceShipCore.cs`：舰船核心，配置总能量预算和三向分配（含 `ArmorShare`）。总能量预算（反应堆发电量）不再是玩家可调的 `MSlider`，改成由 `transform.localScale` 体积实时推算并通过 `MInfo` 只读展示，三向分配份额仍是玩家可调的 `MSlider`（见"扳手菜单里的只读信息：MInfo"一节）。
- `SuperCapacitorBlock.cs`：按 bus 注册容量的超级电容。容量同样从 `MSlider` 改成了由体积实时推算、`MInfo` 展示，`Bus` 选哪条能量总线仍是玩家可调的 `MMenu`。
- `RadarPanelBlock.cs`：雷达面，扫描 TrackRegistry 生成传感器轨迹。`ship.Tracks` 报告范围/锥角内的一切目标，不再按敌我过滤（见 `docs/adr/0004-hostile-filtering-moves-to-consumers.md`）；`Iff` 滑条为存档兼容保留，目前未被消费。扫描间隔 `ScanInterval` = 0.05s（20Hz，ADR-0013 从 0.25s 提上来），每条轨迹带 `ScanTime` 时间戳供下游推算。**多个雷达面共享 `ship.Tracks` 的方式很脆弱，改这里前先读懂**：谁先扫描谁负责清空列表（`Time.time - ship.LastRefreshTime > TrackRefreshWindow`），同一轮里后到的雷达面只追加。这要求一艘舰的所有雷达面**在同一个 FixedUpdate 里扫描**，所以 `nextScanTime` 对齐到全局栅格 `(floor(t/Δ)+1)·Δ`，而不是 `Time.time + Δ`——后者只在"所有雷达面同帧开始模拟、因而天然同相"时才碰巧成立，模拟中途补放置的雷达面会以自己的相位每轮清掉同伴的轨迹。窗口 `TrackRefreshWindow` 取间隔的一半，缩短间隔时不要忘了它。另外锥角重叠的两个雷达面会给同一目标各产生一条轨迹，`ship.Tracks` 里**允许出现重复目标**，消费方必须自己去重（`CaptainRadarView` 用 `seen.Add()` 的返回值挡住，否则同一光点一帧被推进两次）。
- `ShieldProjectorBlock.cs`：电磁场护盾，用抛物面（而非球形）容纳判定拦截高速实体威胁，无敌我识别，护盾维持自身也要耗电，含可视化。
- `CiwsBlock.cs`：单零件近防炮。
- `SpaceInterceptorLauncherBlock.cs`：点防御拦截导弹（含 `SpaceInterceptorMissile`）。
- `HeavyNuclearMissileBlock.cs`：重型核导弹（含 `ITrackable` 弹体）。
- `RailgunBarrelBlock.cs`：电磁炮管与炮手（`SpaceGunnerBlock`），命中伤害和耗电按口径/初速的动能公式推算，已从 EnergyGrid 的 Weapon bus 请求能量；击穿特效复用 WW2-Naval 移植的 `space-perice` 资源包。口径/倍径/初速不再是玩家可调的 `MSlider`，改成由 `transform.localScale` 实时推算并通过 `MInfo` 只读展示（见"扳手菜单里的只读信息：MInfo"一节的用例）。炮手不再直接依赖 `RailgunBarrelBlock` 具体类型：抽出了 `IShipGun` 接口（`SpaceCombatCore.cs`，暴露 `GunGroup`/`FireKey`/`FireControl`/`transform`/`ApproximateMuzzlePosition()`/`CurrentMuzzleVelocity()`），`ship.Guns` 改成 `List<IShipGun>`，电磁炮和激光炮都注册进同一个列表、被炮手统一绑定/瞄准。为满足接口，`FireKey`/`FireControl`/`GunGroup` 从公有字段改成了公有自动属性（`{ get; private set; }`，赋值仍在 `SafeAwake`，外部读法不变）；`RegisterSubsystem`/`RemoveSubsystem` 调用点显式写成 `<IShipGun>`，否则 `this`（具体类型）和 `List<IShipGun>` 会让泛型推断在两个参数间冲突。
- `LaserLanceBlock.cs`：激光炮管（光矛），和 `RailgunBarrelBlock` 同属"炮管类"武器、同实现 `IShipGun`，共用 Fire 键/Fire Control 开关/Gun Group、炮手瞄准、Weapon bus 耗能、装填环 HUD，以及由 `transform.localScale` 实时推算的只读 `MInfo`（孔径 aperture mm / 焦比 f/xx / 射程 m）。区别在于发射的是**即时射线光束**而非动能弹：`Physics.RaycastAll` 从炮口打出、跳过本舰自身碰撞体（`ParentMachine.PlayerID` 判定，避免烧到自己船体），命中即时结算，视觉是 `LineRenderer`+`ParticleSystem`+`Light` 组成的短暂光矛闪光。三条关键设定：①**护盾无法拦截**——`ShieldProjectorBlock` 只减速 `SpaceKineticRound`/导弹，激光两者都不生成，天然穿透、无需特判；②**火控视其初速为无限**——`CurrentMuzzleVelocity()` 返回 1e6，炮手/船长的提前量解算塌缩成"直接瞄准目标当前位置"（数值压在 int 上限内，避免船长把初速量化成桶时 `RoundToInt` 溢出）；③**对纳米装甲**经 `DamageRouter.RouteLaserHit`→`NanoArmorBehaviour.ApplyLaserDamage`，完好装甲折射掉约 95%（`LaserDamageMultiplier`），只有 Integrity 被动能/爆炸削掉后才打得动，这是"能量-火力"链路里动能与激光的协同。另有随距离衰减（越远威力越低，到最大射程降到 `MinDamageFalloff`）。Block ID 13，`LaserLance.xml`，暂复用电磁炮的炮管网格/贴图（尚无专用激光模型）。
- `SpaceFlakTurretBlock.cs`：多联装近防/对空炮台，同样从 Weapon bus 请求能量。原来 13 种"口径x炮管数"WW2 移植预设（`2x40mm` 等，每种自带一套 WW2 网格）已删除，`Type` 菜单换成了 3 个自建模型：`单管火炮`/`多管火炮`/`近防炮`（`Resources/FlakTurret/` 下三个文件夹的 PaoZuo/PaoTa/PaoGuan），`SpaceFlakTurretAssets` 里原来那些逐 Type 数组（网格/贴图资源名、`GunCount`、offset 四件套、`GunWidth`/`GunSpeed`）全部从 13 项收缩成 3 项，索引 0/1/2 依次对应这三种。可视层级新增了 `TurretBsae`（PaoZuo，炮座，固定不转）——必须挂在 `BaseBase`（偏航枢轴）外面而不是里面，否则会继承偏航旋转，"不随炮塔转动"就无法成立；`Base`（现为 PaoTa）和 `GunBase`>`Gun`（现为 PaoGuan）的挂载关系不变，只是共同父节点从 `simRoot` 换成了新的 `TurretBsae`。build 模式沿用 `Vis` 自渲染的旧技巧，只是现在 `Vis` 本身充当 `TurretBsae`（而不是原来的 `Base`），新增了一个显式 `Base` 子物体承载 PaoTa。`SpaceFlakTurretAssets.MountOffset`/`TurretOffset`/`GunOffset`/`GunBaseOffset` 三个类型目前全部是 `Vector3.zero`——还没有人在游戏里核实过三套新模型各自的轴心位置，需要进游戏对着实际效果调，不像旧的逐 Type 数组那样是针对 WW2 模型手调过的；`GunCount`（`{1, 2, 1}`）和近防炮那组 `GunWidth`/`GunSpeed` 同理是未经游戏内验证的占位值。<br>口径不再由 `Type` 直接给定，而是复用 `RailgunBarrelBlock`/`SuperCapacitorBlock` 那套"由 `transform.localScale` 实时推算"模式：`SpaceFlakTurretAssets.BaseCaliber`（原始比例下单管/多管/近防炮分别是 100/100/30mm，倍径均为 45——倍径没有单独建模，是网格本身固定的长径比，随整体缩放自动保持不变）乘上 `(scale.x·scale.y·scale.z)` 的三次方根（体积的立方根还原成线性缩放系数，非均匀缩放下也成立），跟踪速度/初速/射程/装填时间全部下游于这个口径值，全部挪进了每帧都跑的 `RecomputeStats()`（`BuildingUpdate()` 无条件调用，和 `RailgunBarrelBlock.RecomputeBallistics` 同一模式），不再需要单独处理"缩放期间要不要跟着变"。新增 `CaliberInfo`（`MInfo`）展示当前口径——旧版口径直接写在 `Type` 下拉文本里（如"2x40mm"），换成中文名之后文本里就看不出口径了，得补一个只读展示。口径只在两处更新：build 实时（`BuildingUpdate()` 无条件调用 `RecomputeStats()`）和 `OnSimulateStart()` 锁定一次，此后整场模拟不再变——`SimulateFixedUpdateHost`/`SimulateUpdateAlways` 等模拟期间的钩子都不碰它。`HoldAppearance` 原来的 build/simulation 分支判断是 `simulation && originVis != null`，`originVis` 理论上不该为 null（"Vis" 由方块自己的 XML `<Mesh>` 标签保证创建）但没有代码强制这一点——万一为 null，`simulation == true` 时就会绕过 `return` 掉进下面的 `ResolveType()`/`RecomputeStats()`，在模拟期间偷偷改掉口径。已经把判断改成只看 `simulation`（隐藏 `originVis` 那一步单独判空即可，不影响 `return`），把"口径只在 build 实时和 simulate start 更新"变成不依赖 `originVis` 状态的硬保证。
- `SpaceCaptainBlock.cs`：舰船指挥/UI 入口，持有全舰目标锁定权威——**四条火力通道**（`ShipState.ChannelTargets[4]`，ADR-0010，取代旧的单一 `LockedTarget`/`LockedSolution`）。通道0（防空通道）由船长在主机侧按"接近速度/距离"威胁评分自动锁定（带 1.3× 迟滞防抖；玩家手动覆盖走 `Channel0ManualLock`），按通道提供带桶缓存的提前量解算（`TryGetChannelFireSolution`，通道号并入缓存 key），并托管 `CaptainLockNet` 联机同步（`LockMsg` 含通道号和 manual 标志）。
- `FireChannels.cs` / `MFireChannel.cs`：多通道火控的静态帮助层（通道数/四色/优先级评分 `1/(夹角+10°)×i + 1/距离`，`TrySelectSolution`/`SelectTarget` 通道遍历——通道0绝对优先、射界外剔除）和"横排四个通道按钮"的扳手菜单 mapper（`MCustom<int>` 位掩码 + `Elements.MakeTexture`/`AddButton` 构建的 `CustomSelector`；按钮颜色走程序生成的纯色贴图而不是材质 tint，因为 wrench UI 模板材质的 shader 未知）。防空炮/导弹发射器/炮手各带一个 `FireChannel` mapper（`SpaceBlock.AddFireChannel` 裸调用，默认全通道以保持旧"单锁定"手感）。
- `RadarFilter.cs`：`雷达筛选` 的分类与掩码层（ADR-0013）。四行 `RadarCategory` 存在 `ShipState.ChannelFilters[4]`（每通道一份，默认全开），经 `CaptainLockNet.FilterMsg` 同步——雷达图是客户端本地的，但它现在门控着跑在主机侧的通道0自动防空，不同步等于客户端改了没用。三处别踩：①**分类不能只看 `TrackKind`**，三种尺寸的导弹全部上报 `TrackKind.HeavyMissile`，S/M/L 只能靠物理 `Radius`（3/5/10，阈值取中点 4 / 7.5）区分；光点尺寸档和筛选行**必须**共用这一套判定，所以 `CaptainRadarView.MarkerScaleFor` 也改成问 `TryGetCategory`。②`Shows()`（管显示）和 `Allows()`（管锁定）对"分不出类别的目标"故意相反：裸炮弹（`TrackKind.LargeProjectile`）没有筛选行，`Allows()` 一律放行——若让它走同一条判定，它会永远匹配不上任何一行，等于把通道0对炮弹的点防御静默且永久地关掉，而 UI 上没有开关能打开。想让炮弹也能筛，得加第五行并真的给它画光点，而不是动这条放行分支。③筛选收紧时由 `SpaceCaptainBlock.PruneChannelTargets` 在主机侧掉锁并广播，`FilterReceiver` 自己不掉锁。
- `CaptainRadarView.cs`：船长的 3D 雷达图（口袋维度相机渲染 + `OnGUI` 面板），显示舰船/重型导弹为箭头/圆球，支持右键旋转、滚轮缩放、左键点选锁定；面板左上角有四个火力通道 tab（红/黄/青/紫，激活 tab 决定点击落在哪条通道；tab 点击在 `Update` 的 `HandleClick` 里判定而不是 `GUI.Button`，因为 `ConsumePanelMouseEvent` 会吃掉面板上的 IMGUI 鼠标事件），四条通道的锁定光圈同时显示（各自通道色，激活通道亮、其余淡，多通道锁同一目标时光圈逐级放大嵌套）；面板背景/描边/扫描线走全息风格 IMGUI 绘制，向外扩散的波纹替代早期的旋转扫描效果；折叠开关和面板底色改成右下角常驻的 uGUI dock（见"程序化 uGUI 面板"一节），IMGUI 仍负责纹理/特效/鼠标交互本身。<br>ADR-0013 之后：面板**上方**多了一条 `雷达筛选` 栏（四个按钮：SHIP/HVY/MED/SML，染成当前激活通道的颜色，因为它编辑的是那条通道独有的掩码）。它画在 `rect` 之外，所以每一处"鼠标算不算我的"判定都必须走 `IsInteractivePoint()`（面板 ∪ 筛选栏）而不是 `rect.Contains()`，否则点筛选按钮会顺带甩动游戏相机；只有 `HandleOrbitAndZoom` 仍然只认面板本身。标签用 ASCII 而不是中文——雷达图是 IMGUI，没有任何证据表明 Besiege 的 IMGUI skin 字体带 CJK 字形（mod 里现有的中文串都在扳手菜单控件里，那是游戏自己的字体）。<br>每个光点的状态收敛成一个 `Blip` 记录（marker + 垂线 + 平滑后的**世界**坐标），`blips` 字典按 `ITrackable` 索引。三个容易踩的点：①**池子的"占用"就是 `activeSelf`**，所以拿到手就必须立刻 `SetActive(true)`——本舰光点不画垂线，于是它**根本不租**垂线（`Blip.DropLine` 留 null、由 `UpdateDropLine` 惰性租用）；早先的写法是先租再 `SetActive(false)`，等于当场把它还回池子，下一个目标会租到同一条线再被本舰光点每帧关掉。②光点位置**先按轨迹速度推进、再指数逼近**推算值，不能只 lerp（只 lerp 会稳定落后 `速度/平滑率`）；平滑存世界坐标，本舰转向/缩放才能即时精确。③折叠或关闭时 `HideAllMarkers()` 会清空 `blips`，因为 `SyncMarkers` 那段时间不跑，留着会让重新展开时每个光点从冻结位置滑进来。<br>点击锁定不再用碰撞体 + `Physics.RaycastAll`（小导弹光点世界半径约 0.02 单位，根本点不中），改成把已画出的光点用 `WorldToScreenPoint` 投影到雷达纹理像素空间取最近者；因此口袋场景里**没有任何碰撞体**。"只能锁定筛选显示的物体"是靠"只从已画出的光点里选、且锁的就是画出它们的那条激活通道"从构造上成立的，不是一条额外校验——改 `SyncMarkers` 的筛选或 `FindLockableTarget` 的候选来源时，是这条不变量在兜底。
- `SpaceBallistics.cs`：`SpaceKineticRound` 等弹道实体。
- `SpaceBlock.cs`：所有 Block 的公共基类。
- `BeyondSpiderUI.cs`：两个 uGUI 面板共用的建面板/建按钮/建进度条帮助方法（见"程序化 uGUI 面板"一节）。
- `BeyondSpiderInfoPanel.cs`：左侧可折叠的舰船状态面板，取代 `SpaceCombatRuntime.OnGUI` 原来的调试窗口，用 uGUI 进度条展示能量/装甲百分比。

主要 Mod 文件在 `BeyondSpider`：

- `Mod.xml`：程序集、Block 和资源注册。
- `ShipCore.xml`、`Captain.xml`、`SuperCapacitor.xml`、`RadarPanel.xml`、`ShieldProjector.xml`、`SpaceFlakTurret.xml`、`Railgun.xml`、`LaserLance.xml`、`MissileLauncher.xml`、`SpaceGunner.xml`：当前已注册零件（`DefenseDirector.xml`/`CiwsBlock.xml`/`SpaceInterceptorLauncher.xml` 已随各自方块一起废弃/合并——见 ADR-0009、ADR-0012）。

能量分配已经支持 Armor bus（`SpaceShipCore.ArmorShare`、`EnergyGrid.Charge`/`Request`），现在也有了真正的消费者：`NanoArmorBehaviour`（`NanoArmorBlock.cs`）挂在每个原版木头零件（`SingleWoodenBlock`/`DoubleWoodenBlock`/`Log`）上，维护 `Integrity`（自维护，从 Armor bus 按 `ratio^2` 非线性修复）和 `StructuralValue`（不自我修复，按比例削弱直到断开该零件的 joint）两段血量。`DamageRouter`（`DamageRouter.cs`）是统一入口，`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 的命中和 `HeavyNuclearMissileBlock` 的爆炸都会打到这里。"能量-火力-装甲"链路已经完整闭环。

**当前开发优先级：优先打通并验证"能量-火力-装甲"链路**（见下面推荐开发顺序），而不是继续加深"导弹-防御"链路。原先的导弹-防御骨架（雷达轨迹、防空通道自动锁定、护盾、CIWS、点防拦截导弹、重型导弹）已经是可玩的第一切片，先保留作为回归对象；新功能应先补齐纳米装甲和伤害结算，再回头强化防御链路。详见 `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`。

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
- 防空通道（Channel 0）：舰长持续自动锁定最高威胁传感器轨迹的火力通道，所有防空/点防御武器直接读取（ADR-0012，取代了原来的防御指挥官零件）。
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
- 舰长（Captain）在防空通道（Channel 0）上做目标选择，不直接造成伤害（ADR-0012，取代了原来独立的 DefenseDirector 零件）。
- Weapon / Shield / Armor 消费 firing solution 或命中事件，并向 EnergyGrid 请求能量。

## 注册时序规范（ADR-0011 之后）

舰船身份由**连通性分区**决定（`ShipPartition.cs`，ADR-0011）：机器开始模拟后的第 3 个 FixedUpdate，对整机做一次图 BFS，每个"含至少一个舰船核心的连通分量"生成一个 `ShipState`，并把分量内每个方块登记进 `blockToShip` 字典。`OwnShip()` 不再是 `FindShip(PlayerID)`，而是 `SpaceCombatRegistry.ShipOf(BlockBehaviour)`——一个玩家可以有多艘舰，方块属于它物理连接到的那艘。

这带来的注册规则：

- **第 3 帧之前 `OwnShip()` 恒为 null**。`OnSimulateStart` 里的注册尝试对存档加载场景永远失败——这是预期行为，不要试图在 start 时"修好"它。
- **主注册路径是分区集中登记**：子系统零件覆写 `SpaceBlock.OnAssignedToShip(ShipState)`（把自己 `RegisterSubsystem(...)` 进对应 `ship.XXX` 列表、置 `registered = true`），分区 pass 会在 host 和 client 上对分量内每个方块调用它。**必须用这个覆写而不是只靠自愈重试**：旧自愈重试挂在 `SimulateFixedUpdateHost`，client 上永远不跑，client 侧的 `ship.XXX` 列表会永远是空的。
- **自愈重试退化为兜底**，只为"模拟中手动补放置零件"服务：`OnSimulateStart` 里 `if (ship != null) OnAssignedToShip(ship);` 尝试一次（此时 `ShipOf` 走 `ShipPartition.LateResolve`，沿邻接找已归属方块继承其舰），`SimulateFixedUpdateHost` 开头保留 `if (ship != null && !registered)` 重试。`LateResolve` 失败不缓存、0.5s 冷却重试——放置当帧 joint 可能还没接好。
- `NanoArmorBehaviour` 不是 `SpaceBlock`，走 `AssignShip(ShipState)` + 自己 `FixedUpdate` 里的 null 重试（host/client 都跑）。
- 舰长特殊：分区按 Guid 排序把第一个舰长坐正（`ship.Captain`），其余待命；`TryClaimShip` 只在 `ship.Captain == null` 时认领（`SimulateFixedUpdateAlways`，client 也跑），绝不抢占。
- 舰船跨端身份是 `(PlayerID, 主核心 GuidHash)`，联机消息要带 CoreGuidHash（见 `CaptainLockNet.LockMsg`），不要再用 PlayerID 单独当舰船 key。

新写子系统零件的检查表：覆写 `OnAssignedToShip` 注册 + `OnSimulateStart` 尝试一次 + host 重试兜底 + `OnSimulateStop` 注销并清 `registered`。

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

## 宏观平衡参数（MacroBalance）

`MacroBalance.cs` 是平衡的**最顶层**，四个宏观旋钮供设计者一次性重塑整个经济，`SpaceBalance` 里所有具体数值（武器/护盾/装甲兑换率、主动防御耗能、反应堆输出）都从这四个派生。要调手感先动这四个，不要回到 `SpaceBalance` 或各方块改。

战斗经济恰好有四根独立的杠杆，这四个参数一一对应、互不重叠——三根是"哪边更便宜"的树状拆分，一根是纯供给：

```
供给 ─────────────────────────  EnergyAbundance    → 反应堆输出
攻 ÷ 防 ──────────────────────  OffenseEfficiency  → 武器耗率 vs 全体防御
  防御 内部:
    主动 ÷ 被动 ──────────────  ActivePassiveSplit → 主动防御能耗 vs 被动
      被动 内部:
        护盾 ÷ 装甲 ──────────  ShieldArmorSplit   → 护盾耗率 vs 装甲耗率
```

- **主动防御** = AA 防空炮台 / 近防炮 / 点防御导弹（主动出手拦截来袭目标，都从 Weapon bus 抽能）。
- **被动防御** = 护盾 / 装甲（挂在船体上吸收、偏转）。

四个参数的语义（对应用户原始定义）：

1. `OffenseEfficiency` 进攻效率（0~1，默认 0.5）：0 = 防御相较攻击无成本（进攻低效），1 = 攻击相较防御无成本（进攻高效），0.5 = 均衡=当前基线。
2. `EnergyAbundance` 能量充沛度（0~+∞，默认 1）：反应堆产能倍率，是唯一不是 0~1 拆分的旋钮。
3. `ShieldArmorSplit` 护盾-装甲占比（0~1，默认 0.5）：被动防御里偏重哪个。0 = 偏护盾（护盾便宜、当主力），1 = 偏装甲，0.5 = 当前比例。
4. `ActivePassiveSplit` 主动-被动防御（0~1，默认 0.5）：0 = 主动防御耗能极低、偏主动，1 = 被动防御耗能极低、偏被动，0.5 = 均衡。

每根拆分是绕 0.5 的**倒数 tilt**（`Tilt(x)=x/(1-x)`，clamp 到 0.02~0.98）：0.5→1（中性，保持已调好的比例），往两端把成本从一侧推到另一侧，极端处趋近"一侧免费"（clamp 住，所以 0/1 是接近免费的渐近线而非精确 0/∞）。嵌套 tilt 逐层相乘：装甲兑换率 = 防御总体系数 × 被动份额 × 装甲份额。**四个都在中性点（0.5/0.5/0.5、充沛度 1）时每个派生倍率都恰好是 1**，`SpaceBalance` 返回和这层不存在时完全一样的数值——即宏观层默认是 no-op。

派生落点（`SpaceBalance` 里，Ref* 是中性参考值）：`WeaponEnergyPerDamage = RefWeapon × OffenseMult`、`ShieldEnergyPerDamage = RefShield × ShieldMult`、`ArmorEnergyPerHP = RefArmor × ArmorMult`、`ReactorPowerPerVolume = 1200 × ReactorMult`、`ActiveDefenseEnergyScale = ActiveMult`。最后这个是 AA/CIWS/点防三个方块给自己 Weapon-bus 抽能请求乘的系数（`SpaceFlakTurretBlock`/`CiwsBlock`/`SpaceInterceptorLauncherBlock` 各一处），是让这三个"能量模型各自为政、尚未接入兑换率"的主动防御方块响应宏观层的唯一钩子——不动它们的伤害模型（那属于 flak/missile 接入 SpaceBalance 的后续项）。注意 `SpaceBalance` 里这几个从 `const` 改成了 `static readonly`（运行时从 MacroBalance 算），连带 `NanoArmorBlock.EnergyPerRepairPoint` 也从 `const` 改成 `static readonly`；血池/修复速率/动能系数仍是 `const`（宏观层不动它们，属于固定的"单位标尺"）。

## 战斗数值平衡模型（SpaceBalance）

`SpaceBalance.cs` 是"能量-火力-装甲"三角（电磁炮/装甲/护盾）的唯一数值来源。在它出现之前，每个方块的伤害/耗能/血量都是各写各的占位数，彼此对不上（电磁炮一发耗能约是自己武器预算的 10 倍，却能把一块装甲超杀 5 倍，护盾又几乎免费就能拦下这一发）。现在所有攻防数值都从 `SpaceBalance` 里的少数几个设计常数推导，三系统的经济关系被钉死，不随各方块缩放而漂移。**要调游戏手感就改这里，不要回到各方块里改魔数**——方块只负责读取。

它承载用户要求的两套"算法"：

- **平衡电量使用的算法**：每条 bus 一个兑换率，把"战斗效果"换算成它要花的能量，于是舰船在某条 bus 上的反应堆预算直接映射成一个可持续的效果速率（伤害/秒、修复/秒、减速/秒）：
  - Weapon bus：`WeaponEnergyPerDamage = 0.6` MJ / 每点潜在伤害
  - Armor bus：`ArmorEnergyPerHP = 0.5` MJ / 每点 Integrity 修复
  - Shield bus：`ShieldEnergyPerDamage = 0.9` MJ / 每点被抵消的速度伤害
  - 三个率刻意排成 护盾 > 武器 > 装甲：主动拦截最贵（覆盖面、无敌我识别、直接削掉整段速度伤害），事后修复最便宜（只补可再生层、结构永久损失不管），开火居中。
- **平衡伤害和回复的算法**：一切以 Armor Unit（AU）计量，1 AU = 一块默认纳米装甲的总血量 `ArmorUnitHP = 1000`（Integrity 400 + Structural 600）。武器按"几发剥掉 1 AU"定标，修复按"和单门武器 DPS 的比值"定标，护盾按"每次爆发削掉多少速度伤害"定标。
  - 动能伤害：`damage(v) = 口径×KineticFloorPerCaliber(0.28) + 质量×v²×KineticDamagePerKE(0.00005)`。分成一段口径地板（约 20%，护盾再强也削不到 0）和一段随速度走的动能项（约 80%，护盾把弹速削低，这一发的伤害就跟着降）。
  - 修复：Integrity 每秒回满血池的 `ArmorRepairFraction(0.1)`（默认块 40 HP/s），这个单块速率远低于任何武器 DPS，所以集火单块必胜；Armor bus 预算只决定"能同时修几块不同的破损块"。Integrity 越低修得越慢（趋向 `ArmorMinRepairSpeedFactor`）；Structural 不自修。

默认缩放下的参照数（反应堆 1200 MW，20/40/40 分配 → Armor 240 / Shield 480 / Weapon 480 MJ/s）：

| 系统 | 结果 |
| --- | --- |
| 电磁炮（400mm/2600m/s） | 一发 **563** 伤害（地板 112 + 动能 451），耗能 **378 MJ**，装填 2.57s，DPS ≈ 219，约 **1.8 发剥掉 1 AU**（≈4.6s 集火杀穿一块） |
| 护盾拦一发电磁炮弹（2600→600 封顶） | 抵消 427 伤害花 **384 MJ**，弹体仍以 600m/s 命中造成 136（**76% 减伤**），不再一 tick 删除弹体 |
| 激光（孔径300→光束270） | 耗能 **187 MJ**/发，完好装甲仍只吃 5%（需动能先剥 Integrity） |
| 纳米装甲 | 1 AU=1000HP，单块修复 40 HP/s、20 MJ/s |

护盾这次除了改耗能，还修了一个和设计意图不符的行为：`Decelerate` 现在带 `minSpeed` 参数，动能弹只被削到 `HyperVelocityThreshold(600)` 为止（削到阈值就不再符合拦截条件，弹体带着降低后的速度伤害继续飞向船体），不再像旧代码那样一 tick 把弹速削到 0、直接删掉弹体——对应"减速/削弱、不必然完全挡下"这条防御规则。导弹传 `minSpeed=0`，仍可被完全拦停并一路吃减速伤害。

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
6. 结构断裂之后的后续效果（零件真的飞出去之后怎么处理、是否需要额外的连锁反应）留给后续。~~激光炮管与 `LaserDamageMultiplier` 的实际对接~~ 已完成：`LaserLanceBlock`（`DamageRouter.RouteLaserHit`→`NanoArmorBehaviour.ApplyLaserDamage`）。伤害/耗能/射程等数值是可玩的占位值，待进游戏手感微调。

完成以上后再回头强化"导弹-防御"链路（原优先级，降为次高）：

7. 稳定舰船注册：处理多核心、无核心、模拟停止、玩家机器切换。
8. 强化雷达轨迹：去重、可信度、最后更新时间、按舰合并 track picture。
9. ~~强化 DefenseDirector：目标分配、过量拦截控制、武器类别选择~~ DefenseDirectorBlock 已整体废弃，防空/点防御目标选择并入舰长的防空通道（ADR-0012）；目标分配/过量拦截控制等更精细的调度，如果后续需要，应该做在 Channel 0 的选择逻辑或消费侧，而不是重新引入一个独立方块。
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
