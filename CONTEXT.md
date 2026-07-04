# BeyondSpider Space Combat

BeyondSpider is a Besiege mod context for capital-ship space combat, centered on energy allocation, sensor tracks, layered defense, and automated fire control.

Agent implementation guide: see `docs/agent-besiege-mod-guide.md` before adding or changing gameplay systems.

## Language

**舰船核心**:
一艘太空战舰的本地坐标和系统归属核心。它代表整船身份、能量预算、雷达可锁定中心和火控指挥入口。
_Avoid_: 船长零件, 反应堆零件, 中控

**能量预算**:
舰船核心在建造模式下分配给纳米装甲、护盾和武器的总能力。它是舰船规模、武装强度和质量惩罚的主要平衡量。
_Avoid_: 电量, 蓝条

**瞬时功率**:
某个系统在短时间内完成动作所需的功率压力。它决定超级电容是否需要介入缓冲。
_Avoid_: 耗电量

**超级电容**:
舰船系统的短时能量缓冲。它可以专供武器、护盾、装甲，或作为通用缓冲。
_Avoid_: 电池, 储能罐

**电磁场护盾**:
用于削弱或拦截高速实体弹体的防御层。它不防御激光，且目标速度越高越容易触发更高拦截消耗。
_Avoid_: 护盾

**纳米装甲**:
由舰体木头零件承载的自维护舰体防护。完好时强烈反射激光，破损后反射能力下降。
_Avoid_: 木头血量, 舰体 HP

**雷达面**:
面向指定锥形空域生成传感器轨迹的舰载侦察零件。它发现敌舰、大型炮弹和导弹。
_Avoid_: 雷达, 相控阵

**传感器轨迹**:
雷达面或其他传感器生成的目标记录。它描述目标位置、速度、类型、阵营、可信度和最后更新时间。
_Avoid_: 目标, 锁定物

**防御指挥官**:
消费传感器轨迹并为防空武器生成拦截解算的控制零件。
_Avoid_: AA Captain, 防空雷达

**炮手**:
操控玩家自建炮塔铰链和炮管的自动化火控零件。它可以像单零件炮一样接收攻击命令。
_Avoid_: 自动炮塔

**单零件炮**:
单个零件即可完成瞄准、射击和伤害结算的武器。近防炮和单零件中口径炮都属于此类。
_Avoid_: AA

**炮管武器**:
只提供发射管和弹道能力、需要玩家自建炮塔和炮手控制的武器。激光炮和电磁炮属于此类。
_Avoid_: 玩家炮
