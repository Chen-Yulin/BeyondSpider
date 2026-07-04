# Besiege Mod 与游戏底层交互说明书

> 本说明书完全基于对 `WW2-Naval` mod 实际源码（`src/WW2NavalAssembly/`）的阅读整理而成，目的是把"这个 mod 是怎么和 Besiege 引擎打交道的"归纳成可复用的经验，供以后写新 mod（或给别人讲解 Besiege modding）时参考。所有结论都尽量标注文件:行号作为证据；对源码中找不到证据、只能推测的地方，会明确标出"未确认"。
>
> Besiege 基于 Unity 引擎，游戏本体编译进 `Assembly-CSharp.dll`。Mod 通过 **spaar's Besiege Mod Loader**（代码里体现为 `Modding` 命名空间：`ModEntryPoint`、`ModNetworking`、`CustomModules`、`SingleInstance<T>`、`BlockModule`/`BlockModuleBehaviour<T>`、`MToggle`/`MKey`/`MSlider`/`MLimits` 等）挂进游戏进程，不是独立进程、也不是网络代理。

---

## 0. 一个重要的架构事实：这个仓库里其实有两套并存的武器/网络框架

在深入各个问题之前必须先说明这一点，否则容易看错代码：

`src/WW2NavalAssembly/skpCustomModule/` 目录下的文件（`AdShootingBehavour`、`AdProjectileScript`、`AdNetworkBlock`、`AdBoxModCollider`……）几乎全部带有 `// Token: 0x...RID:...RVA:...` 这种反编译器（ILSpy/dnSpy 类工具）留下的注释，说明这是作者之前某个项目编译好的 DLL 被反编译回 C# 源码后**整体 vendor 进了这个仓库**，内部管这套东西叫 "ACM"（AdCustomModule）。它是一套相对通用的"自定义模块 + 对象池投射物 + 网络同步"框架。

而 `WW2-Naval` 真正的舰炮、鱼雷、炸弹逻辑（`Gun.cs`、`TorpedoLauncher.cs`、`TorpedoBehaviour.cs`、`Bomb.cs`）**完全没有引用 `skpCustomModule` 里任何一个类**（全仓库 grep 确认零命中）。也就是说：

- `skpCustomModule` 是 mod 作者带过来的"公共基础设施"，其中一部分（`AdBlockModule`/`AdBlockBehaviour`、`AdCustomModuleMod` 的模块注册、`NetworkBlockReg`/`AdNetworkBlock` 的逐方块网络同步）**确实被整个 mod 依赖**，是骨架的一部分；
- 但另一部分（`AdShootingBehavour`/`AdProjectileScript` 这一整套"对象池投射物"系统）在这个 mod 里是**闲置代码**，真正的舰炮/鱼雷是作者用更朴素的方式（`Instantiate`+`Destroy`定时器、`Physics.RaycastAll` 逐帧扫描）重新写的一遍。

下文两套都会讲，但会明确标出"这是 WW2-Naval 实际用的" vs "这是仓库里存在但没被用上的 ACM 框架，仅供参考对比"。

---

## 1. Mod 的组织方式

### 1.1 入口点

`Mod.cs:9` 定义唯一入口类：

```csharp
public class Mod : ModEntryPoint
{
    public static GameObject myMod;
    public override void OnLoad()
    {
        myMod = new GameObject("WW2 Naval Mod");
        UnityEngine.Object.DontDestroyOnLoad(myMod);
        myMod.AddComponent<AdCustomModuleMod>();
        myMod.AddComponent<CustomBlockController>();
        myMod.AddComponent<ModController>();
        // ... 还有约 35 个 AddComponent<T>() 调用
    }
    public void OnEntityPrefabCreation(int entityId, GameObject prefab)
    {
        if (entityId == 1) prefab.AddComponent<skpCustomModule.AdLevelBlockBehaviour>();
    }
}
```

`ModEntryPoint`（基类）、`OnLoad()`（加载器调用的启动钩子）、`OnEntityPrefabCreation`（关卡实体预制体后处理的可选回调）都是 spaar ModLoader 的标准契约。`.csproj` 只引用了 `Assembly-CSharp.dll` 和 `UnityEngine.dll`（没有单独的 `ModLoader.dll`），说明这个游戏版本里 `Modding` 命名空间的类型是直接编译进 `Assembly-CSharp.dll` 的。

**启动模式 = "一个常驻根 GameObject + 一大堆兄弟组件"**：mod 只创建一个 `DontDestroyOnLoad` 的根物体 `"WW2 Naval Mod"`，然后把几乎所有子系统（`ModController`、`AssetManager`、`CustomBlockController`、`Sea`、`H3NetworkManager`、`CrewManager`、`FireControlManager`、`LanguageManager`，以及各种 `*MsgReceiver`）都挂成它的组件。这样每个子系统白嫖 Unity 组件生命周期（`Awake`/`Update`/`FixedUpdate`），不需要自己再写一层调度器。

`skpCustomModule/AdCustomModuleMod.cs:15-32` 里还有第二个并行的启动对象，用 `SingleInstance<T>`（ModLoader 提供的泛型单例基类）拉起 `AdCustomModule`、`AdShootingModule`、`ProjectileLoader`、`SkyBoxChanger`、`AdSkinLoader` 五个单例，并在这里把四个自定义方块模块注册进游戏、把十几个 `ModNetworking.MessageType` 的回调接上（下文网络部分详述）。

### 1.2 自定义方块怎么注册进游戏

两条并行路径：

**(a) 给游戏自带方块"贴组件"**——`CustomBlockController`（`CustomBlockController.cs:17`，也是 `SingleInstance<CustomBlockController>`）订阅 ModLoader 的方块生命周期静态事件：

```csharp
private void Awake()
{
    Events.OnMachineLoaded += (pmi) => { PMI = pmi; };
    Events.OnBlockInit += AddSliders;
}
private void AddSliders(BlockBehaviour block)
{
    if (block.GetComponent<H3NetworkBlock>() == null)
        block.gameObject.AddComponent<H3NetworkBlock>().blockBehaviour = block;
    switch (block.BlockID)
    {
        case (int)BlockType.SingleWoodenBlock: /* AddComponent(typeof(WoodenArmour)) */
        case (int)BlockType.Rocket: /* AddComponent(typeof(DefaultArmour)) */
        ...
    }
}
```

`Events.OnBlockInit` 是 ModLoader 在**任意方块**（原版或自定义）初始化时都会触发的静态事件。这个 mod 借它给游戏自带方块按 `BlockID` 动态挂装甲、浮力、网络同步等组件——不需要重新做方块预制体，直接"寄生"在原版方块上。

**(b) 真正的自定义方块属性**——通过 `Modding.Modules` API 的 `BlockModule`/`BlockModuleBehaviour<T>` 模式：

```csharp
// AdCustomModuleMod.cs:32-35
CustomModules.AddBlockModule<AdBlockModule, AdBlockBehaviour>("AdBlockProp", true);
CustomModules.AddBlockModule<AdShootingProp, AdShootingBehavour>("AdShootingProp", true);
CustomModules.AddBlockModule<AdAnalogSteeringModule, AdAnalogSteeringModuleBehaviour>("AdAnalogSteeringProp", true);
CustomModules.AddBlockModule<AdAnalogSpinningModule, AdAnalogSpinningModuleBehaviour>("AdAnalogSpinningProp", true);
```

`CustomModules.AddBlockModule<TModule, TBehaviour>(xmlRootName, reloadable)` 是 ModLoader 的标准自定义模块注册接口：

- `TModule`（如 `AdBlockModule`）是**数据 schema**：`[XmlRoot("AdBlockProp")] [Reloadable] class AdBlockModule : BlockModule`，用 `[XmlElement]`/`[DefaultValue]`/`[RequireToValidate]` 声明可在方块 XML 里配置的字段；
- `TBehaviour`（如 `AdBlockBehaviour`）是**运行时行为**：`class AdBlockBehaviour : BlockModuleBehaviour<AdBlockModule>`，在 `SafeAwake()` 里根据反序列化出来的 `AdBlockModule` 数据去接 `ConfigurableJoint`、`Rigidbody`、`PhysicMaterial`、生成碰撞体（`ModCollider.CreateCollider`）等纯 Unity API。

**结论（写给未来 modder）**：想给已有方块加行为 → 订阅 `Events.OnBlockInit`，按 `BlockID` 分支 `AddComponent`；想定义全新的、可在方块 XML 里配置的属性 → 写一对 `BlockModule`/`BlockModuleBehaviour<T>`，用 `CustomModules.AddBlockModule<,>()` 注册一次。

### 1.3 `skpCustomModule`（"ACM"）到底是什么

不是第三方框架，是这个作者自己的一层抽象，建在 ModLoader `Modding.Modules`/`Modding.Serialization`/`ModNetworking` 之上，提供三种可复用形状：

1. `BlockModule`/`BlockModuleBehaviour<T>` 对——完整自定义方块属性（见上）；
2. `AddScriptBase`（`skpCustomModule/AddScriptBase.cs:8`）——更轻量的"挂在方块上的脚本"基类，`Awake()` 自动帮子类解析好 `ObjectBehavior = GetComponent<BlockBehaviour>()`、`OwnerID = ParentMachine.PlayerID`、联机时的 `NetworkBlock`，再调用一个 `virtual SafeAwake()` 模板方法，省得每个脚本自己重复这些样板代码；
3. `*Data`/`*DataHolder` 对（如 `AdData`/`AdDataHolder`）——持久化配置：`AdData` 是纯 POCO（`[XmlRoot]` 标注），`AdDataHolder` 通过 `Modding.ModIO.DeserializeXml<T>("Config.xml", ...)` / `SerializeXml<T>(...)` 读写磁盘。

`AdCustomModuleMod` 本身就是这一切的组合根：拉起五个单例、注册四个方块模块、接上十几个联机消息回调。

### 1.4 资源加载

统一走 Unity **AssetBundle**，不用 `Resources.Load`。模式是：`AssetManager`（`SingleInstance<AssetManager>`）在 `Awake()` 里，对每个具名 bundle（`"WaterHit AB"`、`"CannonHit AB"`、`"Sea AB"`……）调用 ModLoader 提供的 `ModResource.GetAssetBundle(name)`，再用 `ModAssetBundle.LoadAsset<GameObject>(assetName)` 把里面的预制体解析出来，包进一个小小的 `Asset_*` POCO 缓存成 public 字段：

```csharp
public class Asset_Sea
{
    public GameObject Sea;
    public GameObject UnderWater;
    public Asset_Sea(ModAssetBundle modAssetBundle)
    {
        Sea = modAssetBundle.LoadAsset<GameObject>("Sea");
        UnderWater = modAssetBundle.LoadAsset<GameObject>("underwater");
    }
}
// AssetManager.Awake():
Sea = new Asset_Sea(ModResource.GetAssetBundle("Sea AB"));
```

`AssetManager` 单例就是全 mod 唯一的"运行时要 Instantiate 的预制体"来源，其它系统都从 `AssetManager.Instance.XXX.yyy` 里取，不各自加载。

### 1.5 其它结构性经验

- **`SingleInstance<T>`** 是这个 mod 里几乎所有 manager 的统一写法，等价于"自动实例化的单例"，避免到处 `FindObjectOfType`。
- **调试/调参 UI** 一律用 Unity 原生 IMGUI（`OnGUI()` + `GUILayout`），窗口 ID 通过 `ModUtility.GetWindowId()` 申请（避免和其它 mod 的窗口 ID 冲突）。
- **联机走 `ModNetworking` 静态 API**（`CreateMessageType`/`SendToAll`/`SendToHost`/`Callbacks[...]`），而不是裸的 Unity 网络层——详见第 7 节。

---

## 2. 火炮开火/爆炸特效怎么播放

**结论先行：这个 mod 完全不用 `ParticleSystem`，全部靠"Instantiate 一个特效预制体 + 定时 Destroy"，不做对象池。**

### 2.1 开火烟雾（"炮口焰"）

`BulletBehaviour.PlayGunShot()`（`Gun.cs:631-653`），在炮弹刚开始飞的第一个物理帧调用（不是在炮管本体上放特效，而是在**炮弹预制体**身上放）：

```csharp
// 按口径选三档烟雾预制体之一
GameObject gunsmoke = Instantiate(AssetManager.Instance.GunSmoke.gunsmoke1 /*or 2/3*/, transform.position, transform.rotation);
gunsmoke.transform.localScale *= Caliber / 200f;
Destroy(gunsmoke, 3f); // 小口径 2s
```

### 2.2 命中爆炸

`PlayExploHit(RaycastHit hit, bool AP)`（`Gun.cs:878-927`，另有针对空中引爆的 `PlayExploInAir` 和针对飞机目标的 `PlayExploForAircraft` 变体，逻辑同构）：

```csharp
GameObject explo = Instantiate(AssetManager.Instance.CannonHit.explo /*or exploSmall*/, hit.point, Quaternion.identity);
explo.transform.localScale *= Caliber / 800f * (AP ? 1f : 2f);
Destroy(explo, 3f);
```

预制体统一在 `AssetManager.Awake()` 里从 AssetBundle 一次性加载好，每次开火/命中都 `Instantiate` 一份新的、播完用定时器 `Destroy`。

### 2.3 音效怎么挂、怎么放

**没有预置的 `AudioSource`**，而是每次动态往刚生成的特效物体上 `AddComponent<AudioSource>()`：

```csharp
// Gun.cs: AddFireSound / AddExploSound / AddPierceSound / AddWaterHitSound 都是这个模式
AudioSource audioSource = t.gameObject.AddComponent<AudioSource>();
audioSource.clip = ModResource.GetAudioClip("GunShot Audio");
audioSource.spatialBlend = 1f;
audioSource.rolloffMode = AudioRolloffMode.Linear;
audioSource.maxDistance = 300f; // 按效果类型 300~500
audioSource.volume = Caliber / 1000f; // 音量按口径缩放
audioSource.Play(); // 注意：不是 PlayOneShot，因为这本来就是个用完即扔的一次性 AudioSource
```

**没有音高随机化**（`Gun.cs`/`Bomb.cs`/`TorpedoBehaviour.cs` 里都没有 `Random.Range` 调 `pitch`）——固定音色。`Bomb.cs`/`TorpedoBehaviour.cs` 里的 `AddExploSound`/`AddWaterHitSound` 是同一套代码几乎原样复制。

联机时爆炸特效由 `WeaponMsgReceiver.PlayExploOnClient()`（`Gun.cs:195-251`）在客户端重放，走的是同一套 `Instantiate`+`Destroy`+动态挂 `AudioSource` 逻辑，只是触发源是网络消息而不是本地命中检测。

### 2.4 对比：仓库里存在但未使用的 ACM 特效系统（供设计参考）

`skpCustomModule/AdShootingBehavour.cs`/`AdProjectileScript.cs` 那一套是**真正做了对象池**的版本，如果以后要减少 GC 压力可以参考：

- 炮口焰：`ShotFlashList`（大小 20 的预实例化环形池，`AdShootingBehavour.SafeAwake()`），开火时只是 `SetActive(true)` 复用其中一个，不 `Instantiate`。
- 爆炸特效：同理是固定大小池（`ExplosionEffectContainer`），`AdProjectileScript.Explode()` 只是从池里取一个重新定位、`SetActive(true)`。
- 音高随机化：`RandomSoundPitch`（`skpCustomModule/RandomSoundPitch.cs`）在 `Play()` 时 `pitch = startPitch + Random.Range(-pitchRange, pitchRange)`，并用 `AudioSource.PlayOneShot` 而不是独占 `Play()`。
- 粒子系统生命周期：`LifeTimeP.cs` 用 `yield return new WaitWhile(() => particleSystem.IsAlive(true))` 来在粒子播完后自动 `Destroy`，是"用 `ParticleSystem.IsAlive()` 驱动销毁"而不是纯定时器的另一种写法。

---

## 3. 炮弹/鱼雷怎么生成

### 3.1 舰炮炮弹

`Gun.cs` 里 `Instantiate` 直接调用（宿主开火 / 客户端重放 / AI 模拟三条路径分别在 `Gun.cs:1638`、`1709`、`1760`）：

```csharp
GameObject Cannon = (GameObject)Instantiate(
    CannonPrefab,
    transform.position + 3f * transform.forward * transform.localScale.z,
    transform.rotation);
Destroy(Cannon, timeFaze + 2f); // 纯定时器销毁，不做对象池
```

`CannonPrefab` 在 `InitCannon()`（`Gun.cs:1416-1450`）里每门炮只建一次：`BulletBehaviour` 脚本 + `Rigidbody`（`mass=0.2`，`drag` 按口径缩放，**`useGravity=false`**）+（口径≥100 时）视觉网格 + `GravityModifier` 组件。

**关键点：炮弹不用 Unity/Besiege 自带重力，而是自己接管重力。** `GravityModifier.gravityScale = Constants.BulletGravity / Constants.Gravity ≈ 49/32.4 ≈ 1.51`，配合 `Rigidbody.useGravity=false`，等于用一个自定义标量重新实现了"炮弹重力比正常重力大 51%"。

**初速度直接写 `rigidbody.velocity`，不是 `AddForce`：**

```csharp
// BulletBehaviour.FixedUpdate()，仅在开火后第一个物理帧执行一次
myRigid.velocity = transform.forward * MathTool.GetInitialVel(Caliber, AA);
```

`MathTool.GetInitialVel(caliber, AA)` = `(700 + 0.2*(caliber-100) + 20000/(caliber+30)) * (AA?1.5:1) / 2`——口径越小初速越快，AA（防空）模式下再乘 1.5。之后每帧还会额外 `AddForce` 一个随机散布力模拟精度误差，开炮瞬间对炮身本体 `AddForce(-Caliber*forward*5)` 做后坐力。

**销毁/失效有两层机制**：(1) 生成时设的硬定时器 `Destroy(Cannon, timeFaze+2f)`；(2) `FixedUpdate` 里的内部计数器，超过 `Constants.BulletAPTimer`(3帧)/`BulletHETimer`(2帧) 还没命中任何东西就触发 `PlayExploInAir()` 自爆（模拟引信）；(3) 掉到 `y<-1` 直接 `Destroy`。

### 3.2 鱼雷发射管

同样是非池化 `Instantiate`+定时 `Destroy`（`TorpedoLauncher.cs:363,381-386`，60 秒生命周期，`Constants.SlowTorpedoTime` 字段存在但代码里两条路径都只用了 `FastTorpedoTime`——可能是遗留死代码）。

**和舰炮的关键差异：**

1. **鱼雷用真实重力**（`Rigidbody.useGravity=true`），舰炮不用；
2. **初速不是简单的 `transform.forward*speed`，而是继承发射管本体的当前速度**：`Cannon.GetComponent<Rigidbody>().velocity = Rigidbody.velocity`（继承所在军舰/发射管的运动速度），随后 `TorpedoBehaviour.FixedUpdate()` 再 `AddForce(-transform.up * 500f)` 打一个一次性的发射冲量——注意方向是 `-transform.up`，说明鱼雷管的建模里"up"朝向发射的反方向；
3. **入水后完全放弃弹道物理，切换成"深度保持"制导力**：一旦 `transform.position.y < 20`（`Constants.SeaHeight`），`TorpedoBehaviour.FixedUpdate()` 强制锁定朝向（`transform.rotation = Quaternion.LookRotation(Vector3.up, transform.up)`）、把 `drag` 拉到 5，然后用一个 PD 控制式的力把鱼雷推向目标深度：`AddForce(-transform.up * 前进速度 + Vector3.up * (SeaHeight - depth - position.y + 6.5))`。三档 `mode`（0/1/2）对应三种前进推力大小（12/19/10），也就是"鱼雷型号选速度档位"；
4. 鱼雷是唯一会往 `SoundSystem.AddSound(playerID, angle, magnitude, error)` 里写数据的武器——这不是播音效，是给第 10 节的"水听/声纳"系统提供探测数据源。

### 3.3 炸弹

`Bomb.cs` 内没有找到它自己的 `Instantiate` 调用点——大概率是从飞机相关的投弹脚本（不在本次阅读范围内）触发的，**这里明确标注为未确认**。存活期间行为：`FixedUpdate` 里 `AddForce(randomForce)`（外部设置的漂移力），没看到 `GravityModifier`/`useGravity=false`，即炸弹应该是用 Unity 原生重力下落；命中检测和舰炮同构（见第 4 节），或飞行超过 10 秒未命中触发空爆，或掉到 `y<-1` 销毁。



---

## 4. 炮弹怎么检测地形/机械零件碰撞

**这是整个 mod 里最值得学的一个"反直觉"设计：既不靠 `OnCollisionEnter`，也没有专门的地形 Tag/Layer 判断。**

### 4.1 检测方式：每帧手动 `Physics.RaycastAll` 扫一条线段，而不是用碰撞体事件

`BulletBehaviour.CannonDetectCollisionHost(bool AP=true)`（`Gun.cs:679-788`），每个 `FixedUpdate` 调用一次：

```csharp
RaycastHit[] hits = Physics.RaycastAll(
    new Ray(transform.position, myRigid.velocity),
    myRigid.velocity.magnitude * Time.fixedDeltaTime);
// 按距离排序后逐个处理，忽略部分具名的 trigger 碰撞体（"AmmoVis"/"WellArmourVis"/"TurrentVis" 除外）
```

本质上是用"沿速度矢量扫一条本帧位移长度的射线"来手搓一个连续碰撞检测（Continuous Collision Detection）替代方案，而不是依赖 Unity 自带的 `Rigidbody.collisionDetectionMode = Continuous`。鱼雷用的是另一种简化版：只在自身正下方打一条 `0.5f` 长的短射线（`TorpedoBehaviour.DetectCollisionHost()`），且只认命中物体的父级上挂了 `WoodenArmour` 组件的情况才算命中船体。

### 4.2 地形 vs. 机械零件 vs. 水面：靠"有没有某个组件"来反推，不是正向判断

**没有任何 `CompareTag("Terrain")` 或 `LayerMask` 检查**（全文件 grep 确认零命中）。真正的判断逻辑在 `Perice(RaycastHit hit)`（`Gun.cs:420-630`）：

```csharp
try {
    BlockBehaviour block = hit.collider.attachedRigidbody.GetComponent<BlockBehaviour>();
    // 找到了 BlockBehaviour -> 走装甲穿深计算逻辑（下面还会查 WoodenArmour/DefaultArmour/CannonWell/AircraftLifter 等组件算厚度）
} catch {
    Debug.Log("not a block");
    return false; // 没有 BlockBehaviour -> 视为不可穿透
}
```

调用方看到 `Perice` 返回 `false`（没有可识别的机械零件组件）就直接判定命中的是地形/环境，立刻起爆并销毁炮弹：

```csharp
if (!exploded) { PlayExploHit(hit, AP); Destroy(gameObject); }
```

**换句话说：地形检测 = "碰撞体所在刚体上找不到 `BlockBehaviour` 组件"这个排除法，不是一个正向的地形标识。** 装甲厚度判定同理靠 `GetComponent<WoodenArmour>()`/`<DefaultArmour>()`/`<CannonWell>()`/`<AircraftLifter>()` 这类"鸭子类型"式的 try/catch 探测，而不是接口或统一基类。

### 4.3 水面检测完全独立，是高度阈值判断，不是碰撞体

```csharp
// CannonDetectWaterHost()
if (transform.position.y < Constants.SeaHeight) // SeaHeight = 20f，一个固定的世界坐标平面
{
    // 施加自定义的水下阻力/浮力，第一次从上往下穿过该高度时(velocity.y<0)播放水花特效+音效
}
```

也就是说 Besiege 地图本身在这个 mod 的用法里**没有物理水体碰撞体/触发体**，水面纯粹是一条 Y=20 的假想平面，靠比较 `transform.position.y` 判断"是否入水"。

### 4.4 对比：ACM 框架里 `AdBoxModCollider` 和碰撞分发（未被此 mod 使用）

`AdBoxModCollider` 不是运行时碰撞检测组件，而是一个**可 XML 序列化的碰撞体描述符**（`center`/`size`/`layer` 等字段），运行时 `CreateCollider()` 只是照着描述生成一个普通的 `BoxCollider` 并设置 `gameObject.layer = SetLayer`——本质是"用配置声明每种炮弹自己的碰撞体形状和所在物理层"，和地形检测没有直接关系。真正的碰撞分发靠标准 Unity `OnCollisionEnter`/`OnTriggerEnter`，同样是"在 `attachedRigidbody` 上找不找得到 `BasicInfo`/`BlockBehaviour` 组件"这种排除法来判断是不是打中了地形——和舰炮系统的思路是一致的，说明**这是这个作者一以贯之的模式：地形永远是"没有找到预期组件"的默认兜底分支，从不正向识别**。

---

## 5. 怎么去除游戏的空气墙（世界边界）

### 5.1 边界的真身：`"WORLD BOUNDARIES"` 下六个具名 `BoxCollider`

Besiege 自带的出界必死墙实际上是场景里一个叫 `"WORLD BOUNDARIES"` 的根物体，下面挂着 `WorldBoundaryBack`/`Front`/`Left`/`Right`/`Top`（默认没有 `Bottom`，mod 会自己补一个）五到六个子物体，每个上面一个 `BoxCollider`，默认整体尺寸 2000 单位、以原点为中心。**这个 mod 不删除/禁用这些碰撞体，而是原地放大再挪回原位**——因为真正的越界判定逻辑很可能写死在游戏本体（`Assembly-CSharp.dll`）里，mod 摸不到，所以选择"把墙推得足够远"而不是"关掉墙"。

### 5.2 具体做法（`skpCustomModule/SkyBoxChanger.cs`，`TerainObjectChecker()`）

```csharp
// 按需先补一个 Bottom 边界（复制 Top 的碰撞体再挪到 y=-250）
if (this.BoundaryBottom == null) {
    GameObject boundaries = GameObject.Find("WORLD BOUNDARIES").gameObject;
    this.BoundaryBottom = Instantiate(boundaries.transform.Find("WorldBoundaryTop").gameObject);
    this.BoundaryBottom.transform.SetParent(boundaries.transform);
    this.BoundaryBottom.name = "WorldBoundaryBottom";
    this.BoundaryBottom.transform.position = new Vector3(0f, -250f, 0f);
}

// ExpandFloor(固定20000) 或 ExExpandFloor(自定义 scale) 二选一生效时：
StatMaster.Bounding.worldExtents = new Vector3(BoundarySize/2f, BoundarySize, BoundarySize/2f);
Transform back = GameObject.Find("WORLD BOUNDARIES").transform.Find("WorldBoundaryBack");
back.position = new Vector3(0f, -250f, -BoundarySize/2f);
back.GetComponent<BoxCollider>().size = new Vector3(BoundarySize, BoundarySize, 20f);
back.GetComponent<BoxCollider>().center = new Vector3(0f, BoundarySize/2f, 0f);
// ... Front/Left/Right/Top/Bottom 同理逐个重新赋值
```

三个要点：

1. **`StatMaster.Bounding.worldExtents`**（Besiege 引擎自己的世界边界记录）要跟着一起改，否则只挪碰撞体、引擎内部的边界记录对不上，可能出别的问题；
2. **`Transform.Find("WorldBoundaryXXX")` + `BoxCollider.size`/`.center` 直接改**，纯 Unity API 操作，没有用反射/Harmony 打游戏本体代码；
3. **调整完之后要重新计算一个包围盒同步给网络压缩层**：`NetworkCompression.SetWorldBounds(worldBounds)`（遍历所有边界碰撞体 `Encapsulate` 出一个总 `Bounds`），否则联机场景下位置同步的精度换算会跟旧边界对不上。

关掉 `ExpandFloor`/`ExExpandFloor` 时，走对称的另一段代码把边界尺寸和位置**改回默认的 2000**——这是"可逆开关"，不是一次性魔改。

### 5.3 地面本体的开关是另一件事

`FloorDeactive` 开关直接 `GameObject.Find("MULTIPLAYER LEVEL/FloorBig").SetActive(false/true)`（单人关卡对应 `"LEVEL BARREN EXPANSE/FloorBig"` 等三个不同路径）——这是关掉可见地面网格，跟上面的边界墙是两码事，不要混淆。

### 5.4 玩家开关入口 + 联机同步

`SkyBoxChanger.OnGUI()` 和 `ModController` 的调参窗口都各自暴露了勾选框（中文版直接标"空气墙扩大10倍"/"空气墙扩大自定义倍"），点"应用"按钮后 `toggle_skyboxApply=true`，联机情况下还会 `ModNetworking.SendToAll(...msgSkyBoxData...)` 把新边界广播给所有客户端——边界改动是host→client 同步的，不是每个客户端各改各的。

---

## 6. 怎么去除游戏的雾气

同样在 `SkyBoxChanger.TerainObjectChecker()` 里，和边界处理在同一个方法内。**两套并行路径**，分别对应单机/联机：

```csharp
// 联机 (StatMaster.isMP) 分支：
while (GameObject.Find("Main Camera/FOG SPHERE"))            GameObject.Find("Main Camera/FOG SPHERE").SetActive(false);
while (GameObject.Find("Main Camera/Fog Volume"))             GameObject.Find("Main Camera/Fog Volume").SetActive(false);
while (GameObject.Find("Main Camera/Fog Volume Dark"))        GameObject.Find("Main Camera/Fog Volume Dark").SetActive(false);
while (GameObject.Find("Main Camera/Fog Volume Dark (1)"))    GameObject.Find("Main Camera/Fog Volume Dark (1)").SetActive(false);
// 单机 (!StatMaster.isClient) 分支：同一批路径名，逻辑一致
```

（注：这里用 `while` 而不是 `if` 看起来像是笔误或防御性写法——`SetActive(false)` 不会让 `GameObject.Find` 找不到它，所以 `while` 循环体只会执行一次就跳出，效果等价于 `if`。）

`ModController.cs` 里还有一个更简单的单人向开关：

```csharp
public void SetFog(bool a)
{
    GameObject.Find("Main Camera").transform.Find("Fog Volume").gameObject.SetActive(a);
}
```

**核心结论：去雾不是调 `RenderSettings.fog = false`，而是把 Besiege 自己挂在 `Main Camera` 下面的几个具名雾效 GameObject（`"FOG SPHERE"`、`"Fog Volume"`、`"Fog Volume Dark"`、`"Fog Volume Dark (1)"`）直接 `SetActive(false)` 关掉。** `RenderSettings.fog` 只在 vendor 进来的第三方海洋渲染插件 Ceto 内部用到（跟 Besiege 自己的雾无关，不要搞混）。同样没有用反射/Harmony，纯粹是按已知场景层级路径 `GameObject.Find` 之后 `SetActive`。

---

## 7. 联机怎么发包

### 7.1 两条并行的网络通道，别搞混

| | 用途 | 载体 |
|---|---|---|
| **`ModNetworking`（游戏本体 API）** | WW2-Naval 自己的所有玩法消息（开火、爆炸、命中、装填、船员、鱼雷、炮手 AI 同步、飞机、弹射器、AA、柴油机、电池、破损事件……） | `Modding` 命名空间下的静态类，直接来自 Besiege 本体 |
| **`H3` 系统（mod 自造，非 Besiege 概念）** | 整船级别的方块簇位置/旋转同步 | 建在 `ModNetworking` 之上的自造二进制打包 |

`H3NetworkManager`/`H3NetworkBlock`/`H3NetCompression` 全部在 mod 自己的 `Navalmod` 命名空间下，"H3" 是作者自己起的代号，源码里没有解释含义。**不要以为 `H3` 是 Besiege 官方的网络系统。**

另外 `skpCustomModule/AdNetworkBlock.cs`（反编译代码）是**继承自游戏本体 `NetworkBlock` 基类**的逐方块网络复制系统（`class AdNetworkBlock : NetworkBlock`），这是 Besiege modding 标准的"给方块加网络同步"路子，被 `NetworkBlockReg`（见 7.4）用来给每个方块自动挂载。

### 7.2 发包的基本用法

```csharp
// 1. 声明消息类型 schema（一次性，通常在启动时）
public static MessageType FireMsg = ModNetworking.CreateMessageType(
    DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3, DataType.Vector3, DataType.Single);
    // playerID, guid, randomForce, forward, vel, time

// 2. 注册接收回调（也是启动时一次性）
ModNetworking.Callbacks[FireMsg] += WeaponMsgReceiver.Instance.fireKeyMsgReceiver;

// 3. 实际发送
ModNetworking.SendToAll(FireMsg.CreateMessage(new object[] { playerID, guid, randomForce, forward, vel, time }));
// 或指定单个玩家：ModNetworking.SendTo(Player.From(networkId), msg);
// 或只发给房主：ModNetworking.SendToHost(msg);
```

`DataType`（`Integer`/`Vector3`/`Single`/`ByteArray`/`String`/`Boolean`/`IntegerArray`/`SingleArray`……）是游戏本体自带的类型化消息 schema，mod 自己**不需要**手写字节序列化——除非是塞进 `DataType.ByteArray` 里的大块自定义二进制（下面 H3 系统那种情况）。

**全 mod 所有 39 个消息回调的注册点集中在一个文件**：`MessageController.cs`，是"启动时一次性把所有玩法消息接线"的中心点，方便审查这个 mod 到底跟联机对端交换哪些数据。

### 7.3 压缩

- Besiege 本体 `NetworkCompression.CompressPosition/CompressRotation` 把一个 `Vector3` 位置压缩到 6 字节（量化）、旋转压缩到 7 字节，这是默认路径；
- `skpCustomModule/AdNetworkCompression`（反编译代码）在开启"扩大世界边界"（`ExExpandFloor`）模式下，改用**未压缩的 12 字节原始浮点数**发位置——因为地图扩大后默认的量化范围/精度不够用了，用带宽换精度：
  ```csharp
  if (ExExpand) { AdNetworkCompression.CompressPosition(pos, buffer, offset); offset += 12; }
  else          { NetworkCompression.CompressPosition(pos, buffer, offset); offset += 6; }
  ```
- mod 自己的 `H3NetCompression` 名字叫"压缩"，但实际上只是把 `Vector3` 的 4 字节浮点原样搬进字节数组（12 字节/位置），**没有真正压缩**，旋转部分则调用游戏本体的 `NetworkCompression.CompressRotation` 复用官方压缩。`H3` 系统整体比 `AdNetworkBlock` 更"重"，是简单粗暴的工具，不是带宽优化过的主同步路径。

### 7.4 方块怎么"注册为可联网"

- **逐方块自动接入（结构性，不是自选）**：`NetworkBlockReg`（`AddScriptBase` 子类）在 `Update()` 里遍历 `PlayerLinkManager.Clusters`，给每个模拟中的方块的 `AdNetworkBlock` 组件调 `Init(...)`，再注册进游戏本体的 `NetworkController`，最后把整个数组挂到 `serverMachine.networkBlocks`——只要方块上挂了 `AdNetworkBlock` 组件就会被自动纳入同步，不是逐方块类型手动打开关。
- **`H3NetworkBlock` 走完全独立的路径**：只要某个物体上挂了这个组件，`H3NetworkManager` 每帧用 `FindObjectsOfType<H3NetworkBlock>()` 找到它并纳入广播——组件存在即生效，不接游戏本体的 `NetworkController`。
- 对于 WW2-Naval 自己的玩法消息（开火/爆炸等），"注册"根本不是方块级别的事，就是在 `MessageController.cs` 里加一行订阅——扁平的消息路由，不是面向方块的接口体系。

### 7.5 投射物同步：事件重放 + 定时纠偏，不是逐帧广播位置

- **舰炮炮弹**：完全不同步位置。宿主本地开火、本地判定命中，只广播"事件"本身（`FireMsg` 带 `forward`/`vel`/`time` 等参数，`ExploMsg`/`WaterHitMsg`/`HitHoleMsg` 带命中结果）。客户端收到 `FireMsg` 后**在本地按同样的初速度/方向重新模拟一遍弹道**——这是标准的"只同步生成事件+关键参数，客户端各自预测/重放"模式，不是每帧位置广播。
- **`AdNetworkProjectile`**（反编译代码，被基类结构使用）：初始生成时同步一次位置/旋转（`Spawn(...)` 解出 `spawnInfo` 里的压缩位置），之后走delta阈值轮询：`PollObject()` 只有当本地物理位置和上次同步值偏差超过阈值（`!posTracker.WithinThreshold(vector)`）才补发一次修正——"相信本地物理模拟，漂得太远才纠偏"，同样不是逐帧广播。

---

## 8. 联机里怎么判断敌我、判断玩家身份

### 8.1 玩家身份：直接读游戏本体的 `BlockBehaviour.ParentMachine.PlayerID`

不是 mod 自己发明的概念。几乎每个玩法组件在 `SafeAwake()`/`Awake()` 里都会缓存一份：

```csharp
public override void SafeAwake()
{
    myPlayerID = BlockBehaviour.ParentMachine.PlayerID; // 0~15 的玩家槽位号
}
```

（`Gun`、`AABlock`、`AAController`、`Aircraft`、`Bomb`、`TorpedoBehaviour`、`Gunner`、`Controller` 等 24 处几乎一字不差地重复这一行。）后续所有按玩家分桶的数据结构（`Grouper`、`FireControlManager`、`CrewManager`，各种 `MsgReceiver` 字典）都以这个 `[0..15]` 索引的 `playerID` 为 key——这直接对应 Besiege 多人游戏最多 16 人的槽位设计。

判断"是不是本地玩家自己"的标准写法：

```csharp
return StatMaster.isMP ? myPlayerID == PlayerData.localPlayer.networkId : true;
```

**全 mod 没有出现 `steamID`/`SteamID` 字样**（grep 零命中）——身份完全通过 Besiege 自己的 `PlayerData`/`Playerlist`/`networkId` 玩家槽位抽象来做，不直接碰 Steam 层。

### 8.2 队伍/阵营：读游戏本体的 `BlockBehaviour.Team`，mod 从不赋值

```csharp
if (IFF.isDefaultValue && a.BlockBehaviour.Team.Equals(BlockBehaviour.Team)) { ... }
```

`Team` 字段是 Besiege 本体在 `BlockBehaviour` 上暴露的属性（用 `.Equals()` 比较，具体类型未在本仓库源码中出现），这个 mod 全文件搜索**没有任何地方给 `.Team =` 赋值**——只读不写。也就是说队伍归属是游戏大厅本身分配好的（大概率是 Besiege 联机大厅自带的分队 UI），mod 拿到手直接用，选队界面这部分代码不在本仓库范围内，**这里明确标注未确认**。

### 8.3 判断敌我的实际逻辑：`Team.Equals` 为主，个别地方叠加 `playerID` 短路

**AA 自动索敌**（`AAController.UpdateDetectedAircraft()`）：

```csharp
if (IFF.isDefaultValue && a.BlockBehaviour.Team.Equals(BlockBehaviour.Team))
{
    break; // 同队且 IFF 开关开启 -> 跳过（注意这里是 break 不是 continue，逻辑上略粗糙）
}
```

`IFF`（敌我识别器）本身是每个 `AAController` 方块自带的一个可配置开关（`MToggle`，Besiege modding 里叫 "M-属性"，会显示在方块的游戏内配置面板上），默认开启。

**飞机侧的威胁扫描**（`Aircraft.AlertOnCruise`）：

```csharp
if (a.myPlayerID == myPlayerID || a.BlockBehaviour.Team.Equals(BlockBehaviour.Team))
{
    continue; // 同一玩家 或 同队 -> 不视为威胁
}
```

这里是 `playerID` 相等**或** `Team` 相等，只要满足其一就判定为友军——`playerID` 相等是更细的"同一个人"的快速短路判断，`Team` 是更宽的分组。

**HUD 上的敌我配色**（`AAController`/`Aircraft` 的战术视图 `OnGUI`）：敌方画红框，同队画黄框，用的是同一个 `Team.Equals` 谓词——纯视觉，不影响判定逻辑。

**需要特别提醒的坑**：直射炮弹的伤害判定代码（`Gun.cs` 的命中/爆炸处理）里**没有找到任何队伍检查**——也就是说这个 mod 目前的舰炮似乎不区分敌我，可能存在误伤自己人的情况（或者说这是设计如此，让玩家自己小心瞄准）。这只在 AA 自动索敌和飞机威胁判断这两处用到了敌我识别，不是全局统一的伤害门禁。

---

## 9. 炮手怎么控制炮台？铰链的操控逻辑

### 9.1 "进入炮位"不是一个专门的"占用方块"API，而是靠共享按键绑定"对号入座"

`Gunner`（`Gunner.cs:124`，继承 Besiege 的 `BlockScript` 基类）**从来不会抢走摄像机/输入焦点**。它用的是 Besiege 自己的**按键仿真（key emulation）**机制：

```csharp
public override bool EmulatesAnyKeys { get { return true; } } // 声明自己要用"仿真按键"这套系统

// SafeAwake():
AddKey(...);           // 普通按键：ActiveSwitch / AASwitch
AddEmulatorKey(...);   // 仿真按键：FireKey / LeftKey / RightKey / UpKey / DownKey
```

`AddEmulatorKey` 是 Besiege 提供的"允许这个方块的按键和别的方块绑到同一个物理键上"机制。`Gunner.FindHinge()`/`FindGun()`（`Gunner.cs:450-545`）会扫描全局注册表 `GunnerDataBase.Instance.HingeInfo`/`GunInfo` 里同一 `playerID` 下的所有铰链和炮，**按键位是否相同**来配对：如果某个 `SteeringWheel`（转向轮方块，游戏本体自带）配置的按键正好等于 `Gunner` 的 `LeftKey`，就把这个铰链收编为 `OrienHinge`；`UpKey` 对应 `PitchHinge`；炮的 `FireKey` 匹配上就收编为 `FireGun`。

**通俗地说：玩家把炮塔铰链和炮手方块的按键都绑到同一个键（比如都绑 `G`）上，`Gunner` 就靠"按键相同"这个信号反推出"这个铰链/炮是归我控制的"，不存在真正的"进入/占用"概念。** 全文件搜索确认 `Gunner.cs` 里没有任何 `Camera`/`FPVCamera` 引用——第一人称炮手视角（如果有的话）不在这个文件里实现。

`GunnerActive`（由 `ActiveSwitch` 按键切换）总开关整个炮手 AI 循环；`AASwitch` 切换是否进入防空（AA）目标模式；两者都会联网广播（`ModNetworking.SendToAll(GunnerMsgReceiver.GunnerActiveMsg...)`）。

### 9.2 铰链驱动方式：不是 `HingeJoint.motor`，是每 tick 累加一个目标角度字段

**全仓库搜索确认 `HingeJoint`、`JointMotor`、`JointSpring`、`targetVelocity` 一次都没出现过**（唯一一处 `.spring` 是 `Engine.cs` 里跟浮力/引擎相关的弹簧，与关节无关）。所以炮塔转动**不是**通过 Unity `HingeJoint.motor.targetVelocity` 驱动的。

真正的机制是操纵 Besiege 本体 `SteeringWheel`（转向轮）方块脚本暴露出来的公开字段 `AngleToBe`（这个字段大概率在 `SteeringWheel` 内部驱动着一个物理铰链，但那部分实现不在本仓库源码里，**未确认**）。`Gunner` 每个物理 tick 直接累加这个字段：

```csharp
// TurnLeft() 节选
if (same) sw.AngleToBe += (!sw.Flipped) ? mySpeed : -mySpeed;
else      sw.AngleToBe += (sw.Flipped) ? mySpeed : -mySpeed;
if (OrienLimitValid) {
    float center = OrienCenterAngle * (...);
    sw.AngleToBe = Mathf.Clamp(sw.AngleToBe, center - OrienGunSpan, center + OrienGunSpan);
}
```

`mySpeed` 是按角度误差算出来的一个类比例控制步长，乘上 `turningSpeed`（跟口径和一个"转速滑条"相关，口径越大转得越慢）。也就是**用增量式的"每 tick 挪一点目标角度"来模拟转速**，而不是设置一个恒定角速度让物理引擎去插值。是否"瞄准到位"纯粹靠比较角度差：`Gun.GetFCOrienPara()`/`GetFCPitchPara()` 直接读炮管 `transform.forward` 的世界朝向（说明瞄准判定看的是实际物理朝向，而不是一个独立维护的"目标角度"状态），差值在容差滑条阈值内就调用 `Fire()`。

### 9.3 `WW2Hinge`：不是铰链本身，是给 `SteeringWheel` 打补丁的"炮手接管期间行为覆盖器"

`WW2Hinge`（`BlockBehaviour` 子类，跟同一个方块上已有的 `SteeringWheel` 共存，不新建自己的关节）主要做三件事：

1. **注册**：`Start()` 把自己塞进全局 `GunnerDataBase.Instance.HingeInfo[playerID][guid]`，供 `Gunner.FindHinge()` 按键位匹配找到（见 9.1）；同时记下 `SteeringWheel.ReturnToCenterToggle`/`SpeedSlider` 的原始值以便复原；
2. **炮手接管时每 10 tick 一个占空比切换**（`FixedUpdate`，靠全局 tick 计数器 `ModController.Instance.state % 10`）：
   ```csharp
   if (ModController.Instance.state % 10 == 0) {
       SW.ReturnToCenterToggle.SetValue(true);
       SW.SpeedSlider.SetValue(0.01f);
   } else {
       SW.ReturnToCenterToggle.SetValue(false);
       SW.SpeedSlider.SetValue(originSpeed);
   }
   ```
   这看起来是个 workaround/怪癖：强制周期性地开一下"自动回中"再关掉，可能是为了防止底层 `SteeringWheel` 自己的插值逻辑跟 `AngleToBe` 的快速外部赋值打架、或者强制周期性对齐。它直接读写的是 `SteeringWheel` 自己暴露的两个可配置项（`MToggle`/`MSlider`），不是什么专门的马达 API。
3. **销毁时复原**：`OnDestroy()` 把 `ReturnToCenterToggle`/`SpeedSlider` 恢复成接管前的值，并从 `GunnerDataBase` 里注销。

**角度限位不在 `WW2Hinge` 里**，是 `Gunner` 直接读 `SteeringWheel.LimitsSlider`；铰链角度的网络同步也不在 `WW2Hinge` 里，是 `Gunner`/`GunnerMsgReceiver` 单独处理目标位置/俯仰的网络消息。

### 9.4 `CannonWell` / `CannonTrackManager`：不管转向，一个管装甲/装填/毁伤，一个管"追踪已发射炮弹"

容易望文生义搞错这两个类的作用，实测：

- **`CannonWell`**：建模炮塔弹药井的装甲厚度和易损性——根据滑条（`Thickness`/`Depth`/`Offset`/`AmmoResize`/`TurretSize`）摆放视觉网格、按口径和炮塔尺寸算 `Gun.reloadefficiency`（装填效率，不是转向速度）、被击穿时用 `Physics.OverlapSphere` 做范围伤害并禁用整组炮（`disableGun`）——是一个**毁伤模型模块**，和转向/瞄准完全无关。
- **`CannonTrackManager`**：`SingleInstance`，每个玩家维护一个 `Queue<GameObject>` 存"已发射、仍在飞行的炮弹"（`Gun` 在 `TrackOn` 开关开启时才会往这里塞），用途是给"追踪炮弹"的观察视角（比如切镜头看自己打出去的炮弹）用，跟转向限位、后坐力都没有关系。

真正的"射界限位"数据在 `SteeringWheel.LimitsSlider` 上（`Gunner` 直接读取）；"后坐力"是 `Gun.SimulateFixedUpdateHost()` 里对炮身 `Rigidbody.AddForce(-forward*常数)` 加的一个小外力，加上 `VisTransform.localPosition` 的视觉回弹插值——都不在 `CannonWell`/`CannonTrackManager` 里。

### 9.5 火控解算：`FireControlManager` 只是登记表，真正算弹道提前量的是 `AAController`

`FireControlManager` 本身**没有任何弹道/提前量计算**，纯粹是按口径、按类型分桶的登记表（`AddGun`/`RemoveGun`/`GetGun`），供别的系统（主要是 `AAController`）快速枚举"某玩家所有某口径的炮"。

**真正的解算在 `AAController`**（这个类身兼两职：既服务舰炮对海开火，也服务防空），核心两个方法：

- `CalculateGunPitchFromDist()`：一个**迭代 6 次的数值弹道解算器**——从仰角 0 开始，每次用 `MathTool.GetInitialVel` 算初速，套一个指数阻力模型积分出飞行时间和垂直落差，再跟直线弹道比较误差修正仰角，收敛出实际需要的超仰角；还有一个 `maxTime = sqrt(caliber)/6` 的隐式最大射程截断。
- `CalculateGunFCPara()`：在上面基础上加**目标运动提前量**——用飞行时间乘目标速度算出预测点，再拿这个预测点重新解算仰角，如此迭代 3 次收敛，同时返回方位角、仰角、预测点、飞行时间。

`AAController.UpdateAAResult()` 是每 tick 的驱动者：锁定海面目标时用一个随机化的"提前量修正系数"（`1.45 - Random*0.8`），选中空中目标时用另一档系数（`1.45 - Random*0.6`），按 `FireControlManager` 里登记的每个口径分组分别解算，结果写回全局的 `ControllerDataManager.Instance.AAControllerFCResult[playerID]`——`Gunner.GetFCPara()`（对海）和 `AABlock.GetFCPara()`（防空，见第 11 节）都是从这个共享结果表里取数，**是同一套弹道解算器同时服务两种火控场景，不是两套独立实现**。

---

## 10. HUD 怎么在玩家屏幕上绘制控制面板

**结论先行：这个 mod 里没有用到 `GL.Begin` 沉浸式绘制，也没有用 `UnityEngine.LineRenderer`（3D 世界空间那个组件）。实际用了两种技术，按元素分工：**

| 元素 | 技术 | 备注 |
|---|---|---|
| 俯仰梯尺 / 方位转盘（火控面板整体） | **Unity UI**（Canvas + RectTransform），美术做好的预制体 `FireControlCanvas` | 只是旋转/挪动子物体的 `RectTransform`，不是画出来的 |
| 锁定目标的绿色锁定框 | **IMGUI**（`OnGUI` + `GUI.DrawTexture`） | 每帧 `Camera.main.WorldToScreenPoint` 现算屏幕坐标 |
| 水听/声纳波形（"SoundTrack"） | **自定义 `Graphic` 子类**（`UILineRenderer : UnityEngine.UI.Graphic`），手写 `OnPopulateMesh` 生成三角形网格 | 挂在 Canvas 下面，本质是 UI 网格，不是 `LineRenderer` 组件 |

### 10.1 火控面板：整体是预制体，靠改子物体 Transform "指针式"呈现

`AssetManager.cs` 里从 AssetBundle 加载一个叫 `"FireControlCanvas"` 的 Canvas 预制体，`Controller.InitFireControlPanel()` 直接 `Instantiate` 它并 `Find` 出各个子节点（`PitchController`/`OrienController`/`Offset`……）。之后所有"指针转动"都只是改 `RectTransform.localPosition`/`localEulerAngles`：

```csharp
// 方位转盘随船艏朝向旋转——直接转整个 Canvas 子物体，不是重绘
FCOrien.transform.localEulerAngles = -new Vector3(0, 0, MathTool.SignedAngle(...));

// 俯仰预测图标按落点挪位置
PitchPred.Value.transform.Find("PredIcon").transform.localPosition = new Vector3(-PitchPred.Key/5 - 50, 0, 0);
```

也就是说"锁定的 orientation 指示"就是**转一下 Canvas 里一个子节点的 `localEulerAngles`**，没有额外的绘制代码。

### 10.2 锁定目标框：经典 IMGUI 世界坐标投影

```csharp
private void OnGUI()
{
    if (StatMaster.hudHidden) return;
    if (StatMaster.isMP && PlayerData.localPlayer.networkId != myPlayerID) return; // 只画自己的

    if (ControllerDataManager.Instance.lockData[myPlayerID].valid)
    {
        Vector3 onScreen = Camera.main.WorldToScreenPoint(lockData.position);
        if (onScreen.z >= 0)
            GUI.DrawTexture(new Rect(onScreen.x - iconSize/2, Camera.main.pixelHeight - onScreen.y - iconSize/2, iconSize, iconSize), LockIconOnScreen);
    }
}
```

这个"`OnGUI` + `Camera.main.WorldToScreenPoint` + `GUI.DrawTexture`/`GUI.Box`"模式在 `Engine.cs`（引擎转速表）、`AAController.cs`（防空目标框）、`Aircraft.cs`（编队标签）里反复出现，是这个 mod 画"世界锚定的屏幕提示"的标准写法。**很多其它类（`Gun`/`Gunner`/`TorpedoLauncher`/`TorpedoBehaviour`/`AABlock` 等）里也声明了 `OnGUI()`，但里面的绘制调用全部被注释掉了**——是调试用的死代码，说明这套 IMGUI 模式原本是给更多元素用的调试手段，最后只留下少数几处真正上线。

**门禁写法统一**：`StatMaster.hudHidden` 尊重玩家的隐藏 HUD 开关；联机下 `StatMaster.isMP && PlayerData.localPlayer.networkId != myPlayerID` 保证只画本地玩家自己的 HUD，不画别人的。建造/模拟模式的区分靠 `BlockBehaviour.isSimulating`（一个实例属性，**不是**一个叫 `Game.IsSimulating` 的静态 API——全仓库没有这个名字，写文档时不要瞎编）。

### 10.3 水听波纹：不是粒子/Shader/UI缩放动画，是"360 方位强度数组 + 每 tick 衰减"

```csharp
// SoundSystem.cs
public float[] SoundTrackResult = new float[360]; // 每个方位角一个强度值

public void AddSound(int playerID, int angle, float magnitude, float error)
{
    for (int i = 0; i < 360; i++)
        SoundTrackResult[i] += magnitude / (0.5f + 1f/(0.2f+error) * Mathf.Pow(AngleDiff(i, angle), 2));
}
public void FixedUpdate() { for (int i = 0; i < 360; i++) SoundTrackResult[i] *= 0.8f; } // 每帧衰减 20%
```

```csharp
// Controller.UpdateSound()，每 tick 调用
SoundTrack.points = MathTool.PolarToCartesian(SoundSystem.Instance.SoundTrackResult, 1);
SoundTrack.SetVerticesDirty(); // 触发 UILineRenderer 重新生成网格
```

`UILineRenderer`（`UILineRender.cs`）是一个继承 `UnityEngine.UI.Graphic` 的自定义组件，`OnPopulateMesh(VertexHelper vh)` 里把 `points` 数组里相邻两点连成矩形三角形带，手工 `vh.AddTriangle(...)`——是真正的"Canvas 内网格生成"，不是 `LineRenderer` 组件，也不是 shader。

**波纹动画的真实机理**：一个 360 元素的"方位强度"浮点数组，靠 `AddSound()`（引擎噪音等事件触发）往对应方位加值、`FixedUpdate` 里每 tick 乘 0.8 做指数衰减；每帧转成极坐标转直角坐标的多边形点集，重建成 UI 网格。视觉上像是"此起彼伏的波纹"，机理上是一个不断被重建的衰减多边形，跟 `Time.time` 没有直接关系（是被 `FixedUpdate` 的调用节奏驱动的）。

### 10.4 相机类不画 HUD，只是换镜头模式，但都复用同一个 `Camera.main`

`FPVCamera`（第一人称视角）和 `TacCamera`（俯视战术视角）本身都不含任何 `OnGUI`/画线代码，只是改 `Camera.main` 的 `fieldOfView`/`orthographic`/`transform.rotation` 等参数；`ModCameraController` 用一个 `enum Mode { MO, FPV, TAC }` 在三者间切换，但它们操作的**始终是同一个 `Camera.main`**，从未见到 HUD 代码去缓存/引用某个具体相机模式的相机对象。所以**所有 HUD 世界坐标投影统一用 `Camera.main.WorldToScreenPoint`，自动适配当前不管处于哪种视角模式**，不需要 HUD 代码关心当前是 FPV 还是战术视角。

### 10.5 有没有一个统一的 HUD 管理器？——半统一

`BlockUIManager`（`SingleInstance`）管了一个共享子 Canvas（`"WW2BlockUI"`，挂在游戏根 `Canvas` 下）和一个通用的"世界坐标跟随图标"工厂方法 `CreateFollowerUI(...)`，任何方块都能调它拿一个自动跟随自己的屏幕图标（内部同样是 `WorldToScreenPoint` + `RectTransformUtility.ScreenPointToLocalPointInRectangle`）。**但这只是 `FollowerUI` 这一种元素的管理器**——火控面板是 `Controller` 自己独立 `Instantiate`/持有的，各处 `OnGUI` 也是各个组件各自独立的 Unity 消息循环。**没有一个"HUD 总调度器"统一驱动全部 HUD 元素**，是"一个共享小工具 + 若干各自为战的 `OnGUI`/Canvas 实例"的混合架构。

---

## 11. 单零件 AA 炮的控制逻辑

### 11.1 只有一个开关，不是"手动/自动/关闭"三态

跟"炮手手动控制炮台"完全不同，单方块 AA 炮（`AABlock`）**没有手动瞄准模式**，只有一个布尔开关：

```csharp
public bool AA_active = false; // 唯一状态
public MKey SwitchActive;      // 唯一按键绑定，默认 KeyCode.None
```

```csharp
// SimulateUpdateHost()
if (SwitchActive.IsPressed) {
    AA_active = !AA_active;
    if (StatMaster.isMP) ModNetworking.SendToAll(AABlockMsgReceiver.aaActiveMsg.CreateMessage(myPlayerID, myGuid, AA_active));
}
```

关掉时就单纯闲置；开着时**只要有目标就无条件自动瞄准开火**，没有"开着但手动控制"这个中间态——这和 `Gunner`（有 `LeftKey`/`RightKey`/`UpKey`/`DownKey`/`FireKey` 五个手动控制键）形成鲜明对比。另外还有一个高度门禁 `transform.position.y > 19.7f`（大概是"必须在水面以上/甲板上"的合理性检查），不算真正的模式。

### 11.2 索敌：不做自己的物理查询，直接读第 9.5 节的共享火控解算结果

`AABlock` 自己**不做** `Physics.OverlapSphere`/射线索敌，而是轮询同一艘船上的 `AAController` 单例算好的结果：

```csharp
// AABlock.GetFCPara()
if (ControllerDataManager.Instance.aaController[myPlayerID].hasTarget
    && ControllerDataManager.Instance.AAControllerFCResult[myPlayerID][caliber].hasRes)
{
    targetPitch = ...Pitch;
    targetPos   = ...predPosition;
    targetTime  = ...timer;
    hasTarget = true;
}
```

即真正的目标扫描（距离+`isFlying`过滤，`IFF`敌我识别）、弹道提前量解算，都是第 9.5 节讲的 `AAController.UpdateDetectedAircraft()`/`CalculateGunFCPara()` 那一套，`AABlock` 只是按自己的口径去查预先算好的表。**目标提前量预测是有的，而且和舰炮共用同一套迭代解算器**,不是简化版。

`AABlock` 每 tick 把查到的结果转成本地目标偏转角，额外多算了一个船体横摇补偿量（`Gunner` 那条路径没有这个）：

```csharp
float yaw = MathTool.SignedAngle(targetPos - Get2DCoordinate(transform.position), Get2DCoordinate(-transform.up));
Vector3 proj = Vector3.ProjectOnPlane(new Vector3(targetPos.x-position.x, 0, targetPos.y-position.z), transform.forward);
float makeup = Vector3.Angle(Vector3.up, proj) - 90f; // 船体横摇/俯仰补偿
AAVC.TargetPitch = Mathf.Clamp(targetPitch + makeup, 0, 90);
```

小口径（<76mm）另有一套简化的"近防炮式"命中判定：`AATurretController.FindAircraft(angle)` 用一个锥角+距离判断（不经过弹道解算），命中概率随机数直接掉血——是给轻型 AA 用的简化命中模型，和大口径走真正的物理炮弹（`BulletBehaviour`）分开处理。

### 11.3 瞄准执行方式：和炮手系统完全不同的两条腿——纯运动学，不碰任何关节

这是和第 9 节最大的反差点。AA 炮塔**完全不用 `WW2Hinge`/`SteeringWheel`/任何 Unity Joint**，直接每帧写 `transform.localEulerAngles`：

```csharp
public float Pitch {
    set { _pitch = value; Gun.transform.localEulerAngles = new Vector3(-value, 0, 0); }
}
public float Yaw {
    set { _yaw = value; Base.transform.localEulerAngles = new Vector3(0, value, 0); }
}
```

`Base`（水平转台）和 `Gun`（炮管俯仰）是代码里 `InitBaseGunObjectSimulate()` 手工搭出来的两级子物体，**没有刚体、没有关节，纯粹是父子 Transform 的角度旋转**——本质是"程序化建模的可视化转台"，不参与物理仿真。

平滑/伺服手感是手写的指数逼近 + 匀速限幅两段式：

```csharp
// Update()：把内部"真实角度"再做一次带随机误差的平滑逼近，模拟瞄准抖动
Pitch += (_real_pitch + err.y - Pitch) * 0.2f;

// FixedUpdate()：内部真实角度逼近目标角度——离得近用指数逼近，离得远用恒定角速度追
if (Mathf.Abs(Yaw - TargetYaw) < equv_speed * 8) _real_yaw += (TargetYaw - _real_yaw) * 0.2f;
else                                              _real_yaw += Mathf.Sign(TargetYaw-_real_yaw) * equv_speed;
if (hasLimit) { _real_yaw = Mathf.Clamp(_real_yaw, -MinLimit, MaxLimit); _real_pitch = Mathf.Clamp(_real_pitch, -5, 90); }
```

`UpdateRandomError()` 还叠加了一个缓慢游走的伪随机误差向量，模拟瞄准精度误差——这是 `Gunner` 路径里完全没有的"人工添加的不精确感"（`Gunner` 路径的不精确感是真实关节物理+按键增量控制自带的，不需要额外模拟）。

射界限位 `AABlock.YawLimit` 虽然也是 `MLimits`（跟 `SteeringWheel.LimitsSlider` 同一种 Besiege 配置控件类型），但这里只是被读成普通的 `Min`/`Max` float 拿去 `Mathf.Clamp`，**跟任何物理关节无关**。

开火门禁是纯角度容差判断：`|_real_yaw-TargetYaw| + |_real_pitch-TargetPitch| < 10` 才认为"瞄准到位"（`ok`），驱动 `Shoot` setter 触发开火。

### 11.4 和炮手系统的对比小结

| | 炮手座（`Gunner`） | 单方块 AA 炮（`AABlock`） |
|---|---|---|
| 玩家控制 | 有——共享按键绑定"对号入座"，`LeftKey`/`RightKey`/`UpKey`/`DownKey`/`FireKey` | 无——只有一个开/关键 |
| 模式数量 | 2 个独立布尔叠加（`GunnerActive` × `AA`目标源），不是枚举 | 1 个布尔（`AA_active`），无枚举 |
| 转动机制 | 每 tick 累加 `SteeringWheel.AngleToBe`（推测内部驱动真实物理铰链，未在本仓库确认） | 每帧直接写 `transform.localEulerAngles`，无关节、无刚体 |
| 专属辅助组件 | `WW2Hinge`（10-tick 占空比覆盖 `SteeringWheel` 的回中/速度配置，纯 workaround） | 无 |
| 索敌 | 对海：读全局锁定目标；对空：委托给 `AAController` | 直接读 `AAController` 算好的结果，不自己搜索 |
| 弹道/提前量解算 | `AAController.CalculateGunFCPara`（迭代弹道+目标提前量） | 同一套解算器，`AABlock` 只是消费者 |
| 瞄准误差模拟 | 无额外模拟（关节物理本身自带迟滞） | 显式叠加伪随机误差向量 `err` |
| 开火门禁 | 方位/俯仰误差在滑条设定的容差阈值内 | 角度误差和 `<10` 的固定阈值 |

**一句话总结这条经验**：这个 mod 里"由玩家占位控制的炮塔"永远走 Besiege 原生 `SteeringWheel`/物理铰链这条路（哪怕要打补丁 workaround），而"无人自动炮塔"完全绕开物理关节、直接改 Transform——这是两种模式在实现选型上的分野，不是同一套代码的参数化。想写一个新的自动炮塔，`AABlock` 这种纯 Transform 驱动、外挂随机误差模拟精度的写法是更简单可控的起点；想写一个可以被玩家手动摇的炮塔，绕不开去研究 Besiege 自带的 `SteeringWheel`/`MLimits` 这一套。

---

## 12. 归纳：给以后写 Besiege mod 的人的通用经验

1. **一切从 `ModEntryPoint.OnLoad()` 开始，用一个常驻 `DontDestroyOnLoad` 根物体挂满 `SingleInstance<T>` 单例组件**——不需要自己写调度器，白嫖 Unity 组件生命周期。
2. **改已有方块的行为，订阅 `Events.OnBlockInit`，按 `BlockID` 分支 `AddComponent`；要新增可在 XML 里配置的方块属性，写 `BlockModule`/`BlockModuleBehaviour<T>` 对，用 `CustomModules.AddBlockModule<,>()` 注册。**
3. **一切"看起来是场景固定对象"的东西（雾、边界墙、地面）都可以用 `GameObject.Find("已知层级路径")` 找到再 `SetActive`/改 `Transform`/改 `Collider` 属性来魔改，完全不需要 Harmony/反射打游戏本体代码**——前提是你得先知道场景层级里这些物体叫什么、挂在哪。这个经验最大的价值在于：Besiege 的很多"限制"其实只是场景里普通的 GameObject+Collider，不是硬编码在编译后的逻辑里。
4. **联机走 `ModNetworking.CreateMessageType`/`SendTo`/`SendToAll`/`Callbacks[...]`，用官方 `DataType` 家族做 schema，不需要手搓字节协议**（除非你要塞大块自定义二进制进 `ByteArray`）。投射物同步优先选"广播生成事件+客户端本地重放"而不是"每帧广播位置"，省带宽。
5. **身份系统直接吃现成的 `BlockBehaviour.ParentMachine.PlayerID` 和 `BlockBehaviour.Team`，不用自己发明玩家/队伍概念**——但 `Team` 目前只读不写，队伍分配这块 UI/流程需要另外确认游戏本体怎么做。
6. **地形/机械零件/水面三选一的判断，最稳的写法是"反向排除"**：碰撞到的刚体上有没有 `BlockBehaviour`（或更细的装甲组件）——找不到就当地形处理；水面单独用一个固定的世界坐标高度阈值判断，不依赖碰撞体。
7. **"由玩家操控的可动部件"（炮塔）优先复用 Besiege 自带的 `SteeringWheel`/铰链方块，通过累加它暴露的 `AngleToBe` 字段来驱动，而不是自己新建 Unity `HingeJoint`**——这样能天然继承游戏本体的关节限位、网络同步等基础设施。"无人自动部件"（AA炮）则可以放弃物理关节，直接对 Transform 做运动学插值，实现更简单、手感更好控制。
8. **HUD 优先分层选型**：固定布局的仪表盘用美术做好的 Canvas 预制体，靠改子物体 `Transform` 呈现指针类动画；世界锚定的标记（锁定框、目标框）用 `OnGUI`+`Camera.main.WorldToScreenPoint`；需要动态生成任意形状的（声纳波形）用继承 `UI.Graphic` 自己写 `OnPopulateMesh`。三者可以在同一个 mod 里混用，不需要强行统一成一种技术。

---

### 附：证据来源

本文档全部结论来自对 `/Users/chenyulin/WW2-Naval/src/WW2NavalAssembly/` 下以下文件的实际阅读：`Mod.cs`、`ModController.cs`、`CustomBlockController.cs`、`AssetManager.cs`、`Gun.cs`、`TorpedoLauncher.cs`、`TorpedoBehaviour.cs`、`Bomb.cs`、`MathTool.cs`、`Constants.cs`、`SoundSystem.cs`、`H3NetworkManager.cs`、`H3NetworkBlock.cs`、`H3NetCompression.cs`、`H3ClustersTest.cs`、`MessageController.cs`、`CrewManager.cs`、`Grouper.cs`、`FireControlManager.cs`、`Gunner.cs`、`WW2Hinge.cs`、`CannonWell.cs`、`CannonTrackManager.cs`、`AABlock.cs`、`AAController.cs`、`AAAssetManager.cs`、`UILineRender.cs`、`BlockUIManager.cs`、`FPVCamera.cs`、`TacCamera.cs`、`ModCameraController.cs`、`FlightDataBase.cs`、`Controller.cs`、`Aircraft.cs`、`Engine.cs`，以及 `skpCustomModule/` 目录下的 `AdCustomModule.cs`、`AdCustomModuleMod.cs`、`AdBlockModule.cs`、`AdBlockBehaviour.cs`、`AdLevelBlockBehaviour.cs`、`NetworkBlockReg.cs`、`AdData.cs`、`AdDataHolder.cs`、`AddScriptBase.cs`、`SkyBoxChanger.cs`、`AdShootingBehavour.cs`、`AdShootingModule.cs`、`AdExplosionEffect.cs`、`Asset_Explosion.cs`、`AdCollisionExplosionComponent.cs`、`AdActiveChaffExplosionComponent.cs`、`RandomSoundPitch.cs`、`AdProjectileScript.cs`、`AdBoxModCollider.cs`、`CollisionType.cs`、`LifeTimeP.cs`、`AdNetworkBlock.cs`、`AdNetworkCompression.cs`、`AdNetworkProjectile.cs`。

标注"未确认"的地方，是指相关类的完整实现不在本仓库源码范围内（多为 Besiege 游戏本体 `Assembly-CSharp.dll` 内编译好的类，如 `SteeringWheel`、`BlockBehaviour.Team` 的具体类型），只能通过这个 mod 对它们公开成员的调用方式反推其行为，不代表已经验证过内部实现。
