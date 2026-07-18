# ADR 0012: 废弃防御指挥官，防空/点防御并入防空通道（Channel 0）

日期：2026-07-16
状态：已实现，待游戏内验证

## 背景

`DefenseDirectorBlock` 是一个独立方块：消费全部传感器轨迹（`ship.Tracks`），按 TTI 或重型威胁
两种策略之一打分，写入 `ship.DefensiveSolution`，供 CIWS/近防炮（`SpaceFlakTurretBlock`）、点防
御导弹（`MissileLauncherBlock`）、炮手（`RailgunBarrelBlock.SpaceGunnerBlock`）在各自的多通道
选择（`FireChannels.TrySelectSolution`/`SelectTarget`）失败时兜底消费。

同时，舰长（`SpaceCaptainBlock`）已经独立维护着火力通道 0——防空通道：主机侧持续对
`ship.Tracks` 中的目标按"距离近且接近速度快"打分并自动锁定，本质上和防御指挥官做的是同一件事
（威胁评分 + 自动选目标），只是数据源、评分公式和消费路径都各自一套，且防御指挥官的选择完全不
经过 IFF：无论舰长 IFF 开关状态如何，防御指挥官永远只考虑 `SpaceBallistics.IsHostile` 判定的敌
方目标。

用户要求：删除防御指挥官模块，所有防空/点防御武器直接读 Channel 0 的数据；并且在舰长关闭 IFF
时，友军或玩家自己存档中其他舰船射出的有威胁抛射物，也应该被自动锁定并拦截。

## 决策

1. **删除 `DefenseDirectorBlock.cs`**（连同 `BeyondSpider/DefenseDirector.xml`、Mod.xml 里的
   Block/Resource 注册、.csproj 编译项）。`ShipState.Directors` 列表和 `DefensiveSolution` 字段
   一并移除——这是有意的存档兼容性破坏：已放置的防御指挥官方块在旧存档里会变成未知孤儿方块。
2. **`SpaceCaptainBlock.UpdateAutoAirDefenseLock` 扩大候选池**：候选不再要求
   `target is ILockable`（原本只能选舰船/重型导弹），而是 `ship.Tracks` 里任何存活目标，覆盖了
   原来防御指挥官能处理但 Channel 0 处理不了的裸弹体/大口径炮弹（`TrackKind.LargeProjectile`，
   如 `SpaceKineticRound`，没有 `GuidHash`、无法从雷达手动点选）。
3. **候选的敌我过滤改为受 IFF 开关控制**：原来无条件 `SpaceBallistics.IsHostile(this, target)`，
   现在改成 `if (Iff.IsActive && !IsHostile) continue;`——IFF 开（默认）时行为不变，只自动锁定
   真正敌对目标；**IFF 关时不再过滤敌我**，任何正在接近本舰的目标都算作威胁，包括友军的流弹和
   玩家自己另一艘船（ADR-0011 多舰）打出的、恰好朝本舰飞来的炮弹/导弹。这与已有的
   `CanCommandLockedFireAt`（`!Iff.IsActive || IsHostile(...)`，武器开火前的许可判定）语义一致
   ——两处读同一个 `Iff.IsActive`，选择时和开火时的口径统一，不会出现"选中了却因为敌我判定拒绝
   开火"的不一致。
4. **广播分支加空安全转换**：自动锁定结果不再保证是 `ILockable`（裸弹体不是），锁定广播前把
   `(ILockable)best` 的强制转换改成 `as` 判空——非 `ILockable` 目标（裸弹体）保持 host 本地、不
   广播。这与旧 `DefensiveSolution` 从来不跨端同步的先例一致（`CaptainRadarView` 本来就只给
   `Ship`/`HeavyMissile` 两种轨迹画雷达标记，裸弹体从来没有可见的雷达图标，不广播不影响任何人
   能看到什么）。
5. **三个消费者改为直接依赖 `FireChannels.TrySelectSolution`/`SelectTarget`**，移除各自的
   `ship.DefensiveSolution` 兜底分支：
   - `RailgunBarrelBlock.SpaceGunnerBlock.TryGetSolution`
   - `SpaceFlakTurretBlock.UpdateFireControlTarget`
   - `MissileLauncherBlock.MissileFireControl.SelectTarget`（连带删除了只服务于这个兜底分支的
     `IsViable` 私有方法）

   这是可行的，因为 `TryGetChannelFireSolution`（`SpaceCaptainBlock.cs`）内部通过
   `ship.Tracks` 按引用查找目标对应的 `SensorTrack` 来算提前量，本来就不限定 `ILockable`——
   只要 Channel 0 能选中裸弹体，消费侧不需要另外改。三个消费者原本"先查通道、通道没有再退回防御
   解算"的双通道模式合并成单通道，`FireChannels.SelectTarget`/`TrySelectSolution` 里 channel 0
   本来就绝对优先（在通道遍历里第一个判断、命中直接返回），语义没有变化。

   行为上的副作用：以前 `DefensiveSolution` 兜底是**无条件**触发的，不受各武器自己的
   `FireChannel` 位掩码影响；现在如果玩家手动把某个近防炮/发射器的 Channel 0 复选框关掉，它就
   不再自动响应防空通道的锁定了。这是有意的简化——一条通道、一套消费方式，不再有"通道掩码管
   得住其他三条、管不住防空这条"的特例。默认掩码仍是全通道开启（`FireChannels.AllMask`），新
   放置的武器行为和以前一致。

## 后果

- 火控只剩一条自动选择逻辑（舰长的 Channel 0），不再有两套威胁评分/两套数据结构并存。
- IFF 关闭现在有了明确、双向一致的效果：关闭本舰 IFF 后，本舰的防空通道会自动锁定并允许拦截
  友军/己方其他舰船的威胁抛射物；重新打开 IFF 立刻恢复"只打真正敌人"。
- `DefenseDirectorBlock` 原本的两种评分策略（TTI 优先 / 重型威胁优先，`Priority` 菜单）没有
  移植——Channel 0 只有一种评分公式（接近速度 / 距离）。如果以后需要"重型威胁优先"这种可切换
  策略，应该做成舰长的一个新滑条/菜单，而不是重新引入一个独立方块。
- 存档兼容性：已放置的防御指挥官方块会在加载时变成未知孤儿方块（mod 仍处于开发阶段，尚未发布，
  可接受）。
