# ADR 0011: 多舰分区 —— 舰船身份由连通性决定，而非玩家 ID

日期：2026-07-16
状态：已实现，待游戏内验证

## 背景

此前 `SpaceCombatRegistry.ships` 以 PlayerID 为 key，一个玩家只有一艘舰：任何子系统方块用
`FindShip(PlayerID)` 找到"自己的船"。用户需要一个玩家在同一存档里放置多艘战舰互相/共同作战，
每艘舰的火控、雷达、电力彼此独立。

## 决策

1. **舰 = 含至少一个舰船核心的连通分量。** 机器开始模拟后的第 3 个 FixedUpdate
   （`ShipPartition.PartitionDelayTicks`，此时 simClusters 和所有 joint 都已建好），
   `ShipPartition.Partition(machine)` 对整机做一次带 visited 集合的图 BFS（机器图有环——支撑杆、
   闭合船体——"树状搜索"按字面递归会死循环）。分区结果整场模拟静态不变（战斗中断裂的船块仍算原舰，
   用户决策）。
2. **邻接边来源（全部并集）**：
   - 刚性层：`Machine.simClusters` —— 同 SimCluster 的方块焊死共享刚体，直接同舰；
   - 结构层：`BlockBehaviour.BlockJoints[].connectedBlock`；
   - 物理层：`iJointTo` / `jointsToMe`（双向）。绳索/弹簧连接的两个船体**算一艘**（用户决策，
     不按关节类型过滤）。
3. **每分量一个 ShipState**；多核心时**合并出力**（`SpaceCombatRuntime` 对 `ship.Cores` 求和），
   三向分配份额始终读主核心的滑条。主核心 = 体积（发电量）最大者，平手按 `BuildingBlock.Guid`
   排序——分区在 host 和每个 client 各自本地跑，这个确定性保证所有机器推导出同一个主核心。
   非主核心从 trackables 注销（一艘舰只有一个雷达回波）。
4. **舰船跨端身份 = (PlayerID, 主核心 GuidHash)**。`CaptainLockNet.LockMsg` 增加了
   shipCoreGuidHash 字段（7 字段）——**联机协议破坏性变更**，新旧版本不能混跑。
5. **成员登记集中化**：分区 pass 对分量内每个方块调 `SpaceBlock.OnAssignedToShip(ship)` /
   `NanoArmorBehaviour.AssignShip(ship)`，在 host 和 client 上都把子系统塞进 `ship.XXX` 列表
   （旧自愈重试是 host-gated 的，client 永远注册不上）。`OwnShip()` 从 `FindShip(PlayerID)` 改为
   `SpaceCombatRegistry.ShipOf(BlockBehaviour)`（blockToShip 字典）。
6. **模拟中新放置的方块**走 `ShipPartition.LateResolve`：从该方块沿邻接 BFS 找到第一个已有归属的
   方块并继承其舰。失败不缓存（放置当帧 joint 可能还没接好），改为 0.5s 冷却后重试；
   搜不到核心的残骸在全量分区时显式映射为 null，后续查询 O(1)。
7. **包络盒**：分区同帧以主核心的本地朝向（纯旋转逆变换，刻意不用 `InverseTransformPoint`——
   那会把核心的建造缩放也除进去）统计全分量 collider bounds 的 OBB，长宽高与体积存
   `ShipState.HullSize/HullVolume`，此后不再更新。
8. **UI**：信息面板（左下）顶部一行舰船标签页 + 眼睛按钮（`MouseOrbit.FocusBlock` 把相机 orbit
   到激活舰的舰长，无舰长则核心）；标签名来自舰长新增的 `Ship Name` MText（空则 "SHIP n"）。
   `CaptainRadarView`（右下）改为每帧轮询 `SpaceCombatRegistry.ActiveLocalShip`：激活舰有舰长才
   显示雷达面板，锁定点击带激活舰身份。舰长块不再自己 SetOpen。
9. **雷达自船过滤修正**：`RadarPanelBlock` 原来按 `target.PlayerID == PlayerID` 跳过"自己"，
   会把同玩家的姊妹舰也滤掉；改为只跳过 `ReferenceEquals(target, ship.Core)`。

## 后果

- 各舰火控（ChannelTargets/舰长解算）、雷达（ship.Tracks）、电力（EnergyGrid）天然独立——
  它们全都挂在 ShipState 上，分区后每个分量一份。
- 导弹在发射时缓存发射舰的 `ShipState` 引用（`MissileProjectile.ownerShip`），不再按
  ownerPlayerID 查船。
- 同一船体上多个舰长：分区按 Guid 排序取第一个坐正；其余待命（`TryClaimShip` 只在
  `ship.Captain == null` 时认领，绝不抢占），正职舰长的 OnSimulateStop 让位后自动接替。
- 无核心的结构（残骸、纯装饰）不属于任何舰，子系统全部惰性，与旧行为一致。
- guide 的"注册时序规范"一节已按新模式改写（分区集中登记为主、host 自愈重试退为
  模拟中补放置的兜底）。
