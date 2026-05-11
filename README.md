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
| Substeps | 每 FixedUpdate 子步数 | 4 |
| Iterations | 每子步约束迭代次数 | 12 |
| Gravity | 重力加速度(世界空间) | (0, −9.81, 0) |
| Distance Compliance | XPBD 柔度,**0 = 完全刚性**;值越大越软。先用 0 看下垂正确性,然后调到 1e-6 / 1e-5 / 1e-4 看口袋 | 0 |
| Damping | 速度阻尼,每子步 `v *= (1 − damping)` | 0.02 |
| Max Velocity | 速度上限(防止数值爆炸) | 50 |
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
- 如果**下垂太慢/不下垂**:Iterations 从 12 调到 6 看会不会反而下垂更明显(高刚性 + 高迭代会过度抑制运动);或把 Compliance 从 0 调到 1e-5
- 如果**下垂剧烈抖动**:Substeps 从 4 调到 8;或把 Damping 从 0.02 调到 0.05
- 如果**网像橡皮筋拉太长**:把 Compliance 调小或归 0

---

## 十、下一步

阶段进度:

| 阶段 | 目标 | 状态 |
| --- | --- | --- |
| 0 | 顶点色调试工具 | 已交付 |
| 1 | 数据骨架 + 重力下垂 | 已交付 |
| 2 | 弯曲约束(网形不塌陷) | 待开发 |
| 3 | 球-网碰撞投影(网躲球,出口袋) | 待开发 |
| 4 | 反作用力(球被网吸住、回弹) | 待开发 |
| 5 | 调参 + 空气动力 | 待开发 |

完整设计见 `CLAUDE.md`。验收清单(第五节)会随阶段推进逐步打钩。
