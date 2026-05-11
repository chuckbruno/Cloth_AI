# Cloth_AI — 球网碰撞测试指南

本文档说明如何在 Unity 工程里搭建并测试足球 vs 球门网的碰撞效果。
当前版本里,**球的发射系统**(BallLauncher + BallPool + 一键 UI 安装器)已经就位,网体本身的 XPBD 模拟仍在开发中(详见 `CLAUDE.md`)。本文档主要面向"我想拖个球进去看效果"的日常测试流程。

---

## 一、工程信息

| 项目 | 值 |
| --- | --- |
| Unity 版本 | 2022.x(URP) |
| 测试场景 | `Assets/Scenes/ClothSim.unity` |
| 球网模型 | `Assets/Models/SM_GoalNet.fbx` |
| 球网材质 | `Assets/Models/M_Net.mat` |
| 新增代码目录 | `Assets/Scripts/GoalNetXPBD/` |

> 旧的 `SampleScene.unity` 已被删除,所有测试都在 **ClothSim.unity** 中进行。

---

## 二、5 分钟快速上手

### 1. 打开测试场景
Unity 编辑器中双击 `Assets/Scenes/ClothSim.unity`。

### 2. 准备一个球的 Prefab(只需做一次)
在 Project 面板任意位置新建一个 Prefab,要求:
- **根节点**自带 `Rigidbody`(Use Gravity 勾选,Mass 推荐 `0.45`,接近真实足球质量)
- **根节点**自带 `SphereCollider`(Radius 推荐 `0.11`,接近 5 号足球半径)
- 一个可见的 Mesh(MeshFilter + MeshRenderer,使用任意球体网格即可)
- 不需要 PhysicMaterial;若希望地面弹跳更真实,后续可自行添加并赋给 Collider

把它命名为例如 `Ball.prefab`。

### 3. 在场景中放置发射器
1. Hierarchy 右键 → Create Empty,命名为 `BallLauncher`
2. Inspector 点 Add Component → 添加 **BallPool**
3. 同一对象再 Add Component → 添加 **BallLauncher**
4. 把第 2 步做好的 `Ball.prefab` 拖入 `BallPool.Ball Prefab`
5. 在 Hierarchy 里再建两个空物体 `SpawnPoint` 和 `AimPoint`,放在球门正前方(例如 `SpawnPoint` 距球门 8m,高 1m;`AimPoint` 放在球网中心或球网内任意一点)
6. 把 `SpawnPoint` 和 `AimPoint` 分别拖到 `BallLauncher` 的对应字段
7. `BallLauncher.Launch Speed` 推荐先用 `25`,后续按下文调参

> 发射方向 = `(AimPoint − SpawnPoint).normalized`。把 AimPoint 放在你想让球飞去的位置即可,选中 BallLauncher 时场景里会画出黄→青色的轨迹辅助线。

### 4. 一键创建 UI 按钮
顶部菜单 → **Tools → Cloth_AI → Setup Launch Button In Scene**

它会自动:
- 在场景里创建 `EventSystem`(若已存在则跳过)
- 创建一个名为 `GoalNetXPBD_UI` 的 Canvas(Screen Space - Overlay)
- 在 Canvas 右下角添加一个名为 `LaunchBallButton` 的 Button(蓝底白字 "Launch Ball")
- **自动找到场景里的 BallLauncher**,把 Button 的 OnClick 持久化绑定到 `BallLauncher.LaunchBall()`

执行完后 Console 会打印 `[Cloth_AI] Launch UI installed. Save the scene to keep the changes.`

**记得 Ctrl+S 保存场景**,否则下次打开会丢失。

### 5. 运行 & 测试
1. 点 Unity 顶栏 ▶ 进入 Play 模式
2. Game 视窗右下角点击 **Launch Ball** 按钮 → 应当看到一个球从 SpawnPoint 飞向 AimPoint 方向
3. 球碰到地面或球门后会弹跳;5 秒后该球自动回收到对象池
4. 也可以直接按 **Space** 键发射(等效),按 **R** 键一键回收所有飞行中的球

---

## 三、组件参数详解

### BallPool

| 字段 | 含义 | 默认 |
| --- | --- | --- |
| Ball Prefab | 要被池化的球预制体,必须含 Rigidbody + Collider | — |
| Pool Size | 池容量;池满时再发射会强制回收最早一个 | 8 |
| Auto Return Seconds | 球被发射后多少秒自动回池 | 5 |
| Inactive Parent | 未激活球挂在哪个 Transform 下;为空则挂自己身上 | null |

### BallLauncher

| 字段 | 含义 | 默认 |
| --- | --- | --- |
| Pool | 球的来源池;留空时会从同一 GameObject 上找 BallPool | — |
| Spawn Point | 球出生位置 | — |
| Aim Point | 球飞行的目标点(决定方向) | — |
| Launch Speed | 出膛速度 m/s。20–35 之间是一脚正常射门的范围 | 25 |
| Initial Angular Velocity | 出膛旋转(可选,可做香蕉球) | (0,0,0) |
| Enable Keyboard Launch | 启用空格键发射 | true |
| Launch Key | 发射快捷键 | Space |
| Enable Keyboard Reset | 启用 R 键回收所有球 | true |
| Reset Key | 回收快捷键 | R |

---

## 四、调参指南

### 球速太慢 / 太快
改 `BallLauncher.Launch Speed`。

| 场景 | 推荐值 |
| --- | --- |
| 慢动作演示 / 调试网体 | 8 – 12 |
| 一般传球力度 | 15 – 20 |
| 正常射门 | 25 – 35 |
| 重炮远射 | 35 – 50(注意可能穿透网,见下) |

### 球穿透网(等网体模拟接入后再校验)
原因通常是球速过高使得 substep 内位移大于网格边长。可选对策:
1. 调低球速做基础测试
2. 增大球的 `SphereCollider.Radius`(但会失真)
3. 等 GoalNetXPBD 模拟器接入后,提高其 substep 数(详见 CLAUDE.md 中的 4–8 substeps 设定)
4. 给球开启 `Rigidbody.collisionDetectionMode = ContinuousDynamic`

### 球落地后乱滚不停
这是当前没有地面 PhysicMaterial 的副作用。可在 Project 中创建 PhysicMaterial(Dynamic Friction 0.6,Static Friction 0.6,Bounciness 0.3),赋给地面 Collider。

### 想反复观察同一次撞击
- 按 R 键回收所有球
- 把 Auto Return Seconds 调大(例如 30)
- 或把 Pool Size 调大,然后只看你想看的那一个

---

## 五、网体效果验收清单(待 GoalNetXPBD 模拟器接入后逐项核对)

按下面的顺序,从最弱到最强逐级测试:

- [ ] **空载状态**:不发射球时,网体在重力下静态垂下,固定点(顶点色黑)处不动
- [ ] **轻撞**:Launch Speed = 8,球打到网中心 → 网体轻微下凹,球速衰减但不停;离开后网回到静止形状
- [ ] **正常射门**:Launch Speed = 25 → 球嵌入网内形成可见"口袋",1–2 秒内速度归零,网把球缓缓推回
- [ ] **大力射门**:Launch Speed = 40 → 网形变剧烈但**不爆炸**(顶点不应飞出场景),最终也能稳定下来
- [ ] **斜射四面**:分别让 AimPoint 朝顶面、左面、右面、后面 → 四面网都能形成口袋,角缝(panel 之间的接缝)不撕裂
- [ ] **重复打击**:连点 8 次按钮 → 8 个球依次入网,最早一个 5 秒后自动回收;模拟不应随时间累积发散
- [ ] **R 键复位**:按 R → 所有飞行球瞬间消失,网继续保持当时的形状(网状态不会被清)

---

## 六、目录结构

```
Assets/
├─ Models/
│  ├─ SM_GoalNet.fbx        ← 球网模型
│  └─ M_Net.mat             ← 球网材质
├─ Scenes/
│  └─ ClothSim.unity        ← 测试场景
└─ Scripts/
   └─ GoalNetXPBD/          ← 本次新增的代码,与历史无关
      ├─ BallLauncher.cs    ← 发射器(供 UI 按钮调用)
      ├─ BallPool.cs        ← 对象池(预创建 8 个球,5 秒自动回收)
      ├─ GoalNetXPBD.asmdef ← 运行时程序集
      └─ Editor/
         ├─ BallLauncherUIInstaller.cs   ← 一键创建 UI 按钮的菜单项
         └─ GoalNetXPBD.Editor.asmdef    ← Editor 程序集
```

> XPBD 网体模拟相关脚本(`GoalNetSimulator.cs` 等)尚未实现,设计方案见 `CLAUDE.md`。

---

## 七、常见问题

**Q: 点了菜单 "Setup Launch Button In Scene",但 Console 报警告说 "No BallLauncher found"。**
A: 你还没在场景里加 BallLauncher 组件。先做完上面"5 分钟快速上手"第 3 步,再跑一次菜单即可,Button 会被自动重新绑定。

**Q: 我已经有一个 Canvas / EventSystem 了,菜单会重复创建吗?**
A: 不会。菜单按名字识别 `GoalNetXPBD_UI` Canvas;EventSystem 全场景找一次。重复执行只会重新绑定 Button 的 OnClick(它会先删除旧的同方法监听器,再加一个新的)。

**Q: 想换 UI 按钮的位置/外观?**
A: 直接在场景里改 `LaunchBallButton` 的 RectTransform 即可;只要不改名,下次跑菜单也不会被覆盖。

**Q: 球 Prefab 必须是球形吗?**
A: 不必,任何带 Rigidbody + Collider 的物体都能发射。但球网仿真会假设碰撞体是球(后续 GoalNetBallCoupling 会做球-顶点投影),非球形会有视觉穿插。

**Q: 池满了会怎样?**
A: 强制回收最早一个仍在飞行的球,把该位置让给新发射的球。日常测试不会触发(8 个球 × 5 秒回收 = 平均每秒 1.6 球以下都安全)。

---

## 八、调试工具:顶点色检查器

XPBD 模拟器靠**顶点色为黑色**的顶点识别"固定点"(球门正面边线)。在写模拟器之前,需要先确认 `SM_GoalNet.fbx` 的顶点色确实在我们期望的位置上画好了。

### 用法
1. 在场景里选中挂着 `SM_GoalNet` 网格的 GameObject
2. 顶部菜单 **Tools → Cloth_AI → Inspect Goal Net Vertex Colors**
3. 弹出窗口会显示:
   - 网格来源(MeshFilter / SkinnedMeshRenderer)
   - 顶点总数
   - 三个分类计数:**黑色顶点(固定点候选)**、白色顶点、其他颜色
   - 黑/白阈值滑块,默认黑 ≤ 0.1、白 ≥ 0.9
4. 切到 Scene 视图,会看到:
   - **红球** = 被识别为"黑色 / 固定"的顶点
   - **绿球** = 被识别为"白色 / 自由"的顶点
   - **蓝球** = 介于两者之间的"其他"顶点

### 验收点
- 红点应当落在球门**正面**:横梁、左右两根门柱、两根门柱底部触地线
- 红点**不应当**落在网的内部、顶/侧/后面的中间位置
- "其他颜色"应当占少数;若占多数说明顶点色没刷干净

### 按钮
- **Refresh**:强制重新读取当前选中的网格
- **Print Color Histogram**:在 Console 打印顶点色直方图(每种独特颜色的数量)
- **Frame Pinned**:让 Scene 视窗自动框选所有黑色顶点形成的包围盒,方便查看分布

### 异常情况
- 若窗口报"This mesh has NO vertex colors":说明 FBX 没带顶点色信息,**XPBD 模拟会拒绝运行**。需要让美术在 DCC 工具里给球门正面边线刷上黑色顶点色,然后重新导出 FBX。

---

## 九、阶段 1:数据骨架 + 重力下垂(已交付)

XPBD 模拟器的最小可跑版本。**只**实现:粒子化 + Pin 约束 + 距离约束 + 重力。**没有**弯曲、空气、球碰撞。

### 挂载方法
1. 在 Hierarchy 选中挂着 `SM_GoalNet` 网格的 GameObject(它已经有 MeshFilter + MeshRenderer)
2. Add Component → **GoalNetSimulator**
3. Inspector 里的关键参数:

| 字段 | 含义 | 默认 |
| --- | --- | --- |
| Black Threshold | 顶点色被认作"黑/固定"的阈值,与第八节调试工具保持一致 | 0.1 |
| Weld Distance | 顶点焊接距离(本地空间);两个顶点位置距离小于此值会被合并为同一粒子。**用于把绳子末端和球网边缘正确缝合**。设为 0 关闭焊接 | 0.001 |
| Substeps | 每 FixedUpdate 子步数 | 2 |
| Iterations | 每子步约束迭代次数 | 6 |
| Gravity | 重力加速度(世界空间) | (0, −9.81, 0) |
| Distance Compliance | XPBD 柔度,**0 = 完全刚性**;值越大越软。当前默认先用用户实测可用值 | 1e-6 |
| Enable Bending | 开启相邻三角面的弯曲约束,减少网面塌陷和折叠 | true |
| Bending Compliance | 弯曲约束柔度,值越大越容易折叠/变软。当前默认先用用户实测可用值 | 0.01 |
| Enable Ball Collision | 开启阶段 3 球体投影碰撞。当前只让球推开网,还不会给球反作用力 | true |
| Collision Ball | 可选,手动指定参与碰撞的球 Rigidbody。为空时自动使用 BallPool 最近发射的球 | null |
| Ball Pool | 可选,自动寻找当前活动球的对象池。为空时启动时会在场景中查找 | null |
| Collision Skin | 球半径外额外推出距离,减少视觉穿插 | 0.01 |
| Collision Radius Padding | 只用于网碰撞求解的额外半径。视觉球可以保持真实尺寸,这里补偿当前“粒子点 vs 球体”碰撞容易漏检的问题 | 0.11 |
| Collision Search Margin | 球半径外检测余量,高速球可适当加大 | 0.05 |
| Enable Ball Reaction | 开启阶段 4 反作用力,把网粒子碰撞修正转换成冲量推回 Rigidbody | true |
| Collision Particle Mass | 把网粒子位置修正转换成球冲量的有效质量系数。它是调参值,不等于真实单个网格顶点质量 | 0.43 |
| Reaction Impulse Scale | 反作用冲量缩放。越大球越容易减速/回弹 | 0.5 |
| Max Reaction Impulse | 每个 FixedUpdate 给球的最大冲量,用于限制接触尖峰 | 6.5 |
| Velocity Opposing Impulse Scale | 把接触修正量额外转换成沿球速度反方向的阻尼冲量,避免球面各方向修正互相抵消 | 1 |
| Damping | 速度阻尼,每子步 `v *= (1 − damping)` | 0.02 |
| Max Velocity | 速度上限(防止数值爆炸) | 50 |
| Recalculate Normals | Mesh 写回后是否重算法线。密集球网调试时建议关闭 | false |
| Recalculate Bounds | Mesh 写回后是否重算包围盒。球网保持在导入包围盒附近时建议关闭 | false |
| Geometry Recalculate Interval | 开启法线/包围盒重算时,每 N 次 Mesh 写回执行一次 | 5 |
| Draw Debug Gizmos | 选中时画红/绿粒子点 | false |
| Pause Simulation | 暂停求解,定格画面方便观察 | false |

### 阶段 1 验收清单
按下面顺序逐项核对:

- [ ] **进入 Play 时**:网体形态正确,**没有**任何顶点跑飞,Console 打印 `[GoalNetMesh] Initialized: verts=..., pinned=..., edges=...`
- [ ] **静止形态**:几秒内网在重力作用下从静止网格下垂,**红色顶点不动**(贴在球门框架上),自由顶点向下凹陷
- [ ] **稳定后**:进入静止平衡(粒子不再明显移动)
- [ ] **不爆炸**:把 Distance Compliance 调到 0、1e-6、1e-5、1e-4 切换看,均能稳定;调到 1e-2 这种过软会明显拉伸,但**不应该飞**
- [ ] **不漂浮**:任何位置都不应该有顶点反重力向上飞
- [ ] **可重置**:Inspector 右键 GoalNetSimulator → "Reset To Rest Pose",网瞬间回到初始形态

### 已知不会处理的事
这阶段只看下垂。下面这些**预期不正常**,留到后续阶段处理:
- 网会塌成"过分平面"——还没加弯曲约束,会在阶段 2 修复
- 球穿透网——还没加碰撞约束,会在阶段 3 修复
- 球不会被网弹回——还没加反作用力,会在阶段 4 修复

### 调参建议(阶段 1)
- 如果**绳子和网分开,绳子直接掉地上**:Weld Distance 不够大。FBX 中独立子网格的顶点几何上虽然重合,但拓扑不连。把 Weld Distance 调到 `0.005`(5mm)或 `0.01`(1cm)再 Play,Console 应当能看到 `[GoalNetMesh] Initialized: meshVerts=X, particles=Y (welded N away)...`,N 大于 0 才说明焊接生效
- 如果**焊接把不该合的顶点合掉了**(例如两根近距离的绳子缠在一起):把 Weld Distance 调小
- 如果**Play 模式非常卡**:先在 `GoalNetSimulator` 右键菜单执行 `Apply Realtime Preview Settings`,保持 Substeps = 2、Iterations = 6,并关闭 Recalculate Normals / Recalculate Bounds。2026-05-11 测试中,FPS 从约 2 提升到约 200。
- 如果**下垂太慢/不下垂**:Iterations 可以从 6 临时调到 4 看下垂是否更明显(高刚性 + 高迭代会过度抑制运动);或把 Compliance 从 0 调到 1e-5
- 如果**下垂剧烈抖动**:Substeps 从 2 调到 4 或 8;或把 Damping 从 0.02 调到 0.05
- 如果**网像橡皮筋拉太长**:把 Compliance 调小或归 0
- 如果**网面像纸一样塌平/折叠**:确认 Enable Bending 已开启,然后把 Bending Compliance 从 0.0005 往 0.0001 调小;如果变卡或过硬,再往 0.001 调大
- 阶段 2 实测基线:Enable Bending 开启、Bending Compliance = 0.0005 时,网形可接受,FPS 约 70。后续接入球碰撞时建议用这个作为性能对比基线。
- 阶段 3 注意:当前是**单向球体投影**。球会把网粒子推出球体形成凹陷,但球还不会被网减速或弹回;这是阶段 4 的反作用力目标。
- 如果**球仍然明显穿过网**:先确认 Ball prefab 根对象有 `SphereCollider`,并且 `GoalNetSimulator` 能找到 BallPool;再尝试把 Collision Skin 调到 0.02 或把 Collision Search Margin 调到 0.1。
- 阶段 4 调参:当前 `SoccerBall.prefab` 是 Mass = 1kg,有效 SphereCollider 半径约 0.25m。Launch Speed = 25m/s 时,把球停住本来就需要约 25 N*s 冲量,所以 Max Reaction Impulse 在 20 左右是合理量级。
- 如果把足球改成真实半径 0.11m 后完全穿网,先检查 Transform Scale。如果球对象 Scale = 0.5,那么 SphereCollider Radius = 0.11 的有效半径只有 0.055m;要有效半径 0.11m,应设 SphereCollider Radius = 0.22,或把 Scale 改成 1。
- 当前碰撞是“网粒子点 vs 球体”,真实半径的小球可能从粒子点之间穿过。保持视觉真实时,建议把 Collision Radius Padding 设到 0.06–0.12,让求解器用略大的隐形碰撞球捕捉接触。
- 当前默认参数已按 2026-05-11 用户实测值固化:Distance Compliance = 1e-6,Bending Compliance = 0.01,Collision Radius Padding = 0.11,Collision Particle Mass = 0.43,Reaction Impulse Scale = 0.5,Max Reaction Impulse = 6.5。
- 如果**只有很小一块网在动,缺少真实球网的大面积联动**:这不是单个参数能完全解决的问题。可以先把 Distance Compliance 提到 1e-5 或 1e-4、Iterations 提到 8–12 试试传播范围;后续更可靠的方向是做边/三角碰撞、碰撞影响邻域扩散、空气阻尼/全局 damping 调整,让冲击能量沿网线传出去。
- 如果球几乎不减速,优先把 Collision Particle Mass 保持在 0.5 或更高,Max Reaction Impulse 保持在 20 左右,再把 Reaction Impulse Scale 从 0.35 提到 0.5 或 0.8;如果球被猛烈弹飞,先把 Reaction Impulse Scale 降到 0.2,再把 Max Reaction Impulse 往 10 降。
- `Collision Ball = None` 是正常状态:模拟器会通过 BallPool 自动使用最近发射的球。只有在场景里没有 BallPool、或想固定测试某一个手动摆放的球时,才需要把该球的 Rigidbody 拖到 Collision Ball。
- 如果球仍然几乎不减速,优先把 Velocity Opposing Impulse Scale 从 1 调到 2;这比盲目增大 Collision Skin 更直接。
- 如果**接触瞬间掉帧明显**:先保持 Realtime Preview Settings,然后临时把 Iterations 从 6 降到 4,或把 Collision Search Margin 从 0.05 降到 0.02。确认效果后再慢慢加回质量。

---

## 十、下一步

阶段进度:

| 阶段 | 目标 | 状态 |
| --- | --- | --- |
| 0 | 顶点色调试工具 | 已交付 |
| 1 | 数据骨架 + 重力下垂 | 已交付 |
| 2 | 弯曲约束(网形不塌陷) | 已验收,FPS 约 70 |
| 3 | 球-网碰撞投影(网躲球,出口袋) | 已接入,待 Unity 验收 |
| 4 | 反作用力(球被网吸住、回弹) | 已接入,待 Unity 验收 |
| 5 | 调参 + 空气动力 | 待开发 |

完整设计见 `CLAUDE.md`。验收清单(第五节)会随阶段推进逐步打钩。
