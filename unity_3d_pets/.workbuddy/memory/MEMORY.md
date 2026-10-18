# 项目记忆：unity_3d_pets

## 项目概况
产品名**宠屋养成记**（微信小游戏简称「宠屋」，软著「宠屋养成记游戏软件 V1.0」）。
Unity 6.6 + URP，当前用 ithappy Animals FREE 的 `Dog_001` 做架构验证。

### 文档目录职责（2026-10-18 用户明确）
- `C:/dening_project/pets_game/docs/` —— **全项目**需求与设计文档
  （PRD、后台管理端需求、三端分发资质清单、设计展板、技术方案、启动清单）
- `C:/dening_project/pets_game/unity_3d_pets/docs/` —— **只放前端 Unity 技术文档**
  （当前：`V1_编译与验证报告.md`、`电子宠物APP_V1_代码实现说明.md`）

### V1 已锁定的产品口径（改代码前必读）
- **形态**：微信小游戏 + 纯 IAA（激励视频），不申请版号，不接虚拟支付
- **场景**：2D 背景图（1080×1920，≤500KB）+ 3D 宠物，**相机改正交投影**，
  宠物脚下要有跟随移动的椭圆假阴影。禁止建完整 3D 房间（首包 4MB 装不下）
- **模型**：Cartoon Dog - Border Collie（Red Deer, Asset Store 293098, 113 动画）。
  骨骼命名与 Animals FREE 不同，`scull` / `spine.004` 硬编码会失效
- **数据**：服务端权威。**文档里的养成数值全是示例，不是写死的计算**，
  全部后台可配。后台 1.3 那条「客户端余额只能显示」同样适用于
  快乐值/饱腹/经验 —— 客户端自加算等于给改包刷数值留后门
- **两类情绪要分清**：`happiness`/`satiety`/`exp`/`points` 归服务端；
  `excitement`/`sleepiness` 是客户端表现层临时量，不入库不同步，
  但衰减系数仍从配置注入
- **「点击 5 次 +1 经验」是计数型**（`expPerCount`），不是每次给 0.2。
  浮点累加有精度漂移且不好幂等，服务端按 `(user_id, gesture_type)` 记次数
- **积分**：初始 500 / 签到 100 天 / 升级 100 级 / 广告 100 次
- **等级经验**：×1.5 递增取整，必须走 `config_level` 表，客户端不做等级计算
- **合规红线**：不做抽奖/开箱/概率付费；文案不得出现「充值」「购买」「元」「¥」
- **V1 砍掉**：语音互动、排泄动画、邀请/转发奖励、训练面板（留接口）

## 验证方式（Unity MCP 已接入，2026-10-17）

**优先用 MCP，不要再靠读日志猜。** 之前八轮排查浪费在读 `Logs/Editor.log` 上。

| MCP 工具 | 用途 |
| --- | --- |
| `Unity_RunCommand` | 直接读运行时字段、跑构建菜单、改场景 |
| `Unity_GetConsoleLogs` | 读 Console（比 Editor.log 准，Play 模式的输出也能拿到） |
| `Unity_SceneView_CaptureMultiAngleSceneView` | 四视角截图看场景布局 |

约束（踩过才知道）：
- 类名必须 `internal class CommandScript`，实现 `IRunCommand`
- **禁止反射**（`System.Reflection.BindingFlags.*` 会被拦）
- **禁止 `GetInstanceID()`** —— Unity 6 已废弃，编译失败（CS0619）
- `result.Log` **不支持 `{0:F3}` 格式化**，会当字面量输出
- 改完 C# 要先 `CompilationPipeline.RequestScriptCompilation()`，Unity 会中断 MCP，稍等恢复
- 编辑器里 `LateUpdate`/`Update` 不跑，`QueuePlayerLoopUpdate()` 也不立即生效，
  验证相机跟随之类要进 Play 模式
- `cam.Render()` 离屏渲染在 URP 下出全白，别用它截图，用 SceneView 截图工具

## 关键工程约定

### 1. activeInputHandler = 1，只能用新 Input System
旧版 `UnityEngine.Input.*` 会**抛 InvalidOperationException**（每帧抛）。

### 2. `EnhancedTouchSupport.Enable()` 会把鼠标也模拟成触摸
同一次点击被 Touchscreen + Mouse 各上报一次，中间产生 `TouchPhase.Canceled`，
手势被取消 —— 表现就是「点击没反应」。
修法：`ProcessMouse` 开头 `if (Touchscreen.current != null) return;`
（不能只判断 `primaryTouch.press.isPressed`，按下和抬起会走不同分支）

### 3. 源动画 Clip 的 loopTime = 0
必须复制后用 `AnimationUtility.SetAnimationClipSettings` 打开 loopTime。

### 4. Dog_001 是 Generic Rig（无 Avatar）
- `applyRootMotion = false`（否则 Root 骨骼位移和 PetController 打架）
- 骨骼名：root bone 是 `Root`，头是 **`scull`**（拼写就是这样，不是 skull），背是 `spine`~`spine.008`
- 实际尺寸：**0.42 × 0.76 × 1.27 m**（MCP 实测）
- 34 根骨骼、1170 顶点，材质是 URP/Lit

### 5. Animator 访问边界（需求文档第十二节）
全工程只有 `AnimationOrchestrator` 能在运行期碰 Animator。
用 Speed float 参数驱动（>0.1 进 Walk，>2.0 进 Run），不允许 `animator.Play("Walk")`。

### 6. Editor 程序集里的 MonoBehaviour 不能进 Build
会被挂进场景的组件放 runtime 程序集（本项目 `Assets/Scripts/Diagnostics/`）。

### 7. MonoBehaviour / ScriptableObject 的类名必须与文件名一致（Unity 硬性要求）
类在别的文件里时 `CreateInstance<T>()` 返回 **null**，
Console 报 `No script asset for XXX`，`.asset` 里 `m_Script: {fileID: 0}`。
本项目踩过 5 处，已全部拆开（`PetPersonalityProfile`、`PetModelTestDemo`、
`PetPerformanceProbe`、`PetSceneInfo`、`PetRegionCollider`）。

### 8. Unity fake null —— 最隐蔽的坑
`AssetDatabase` 写操作（`CreateFolder`/`CreateAsset`/`SaveAssets`）会触发资产重导入，
Unity 在过程中**销毁并重建 ScriptableObject 的 native 实例**。
C# 引用还在，但 Unity 重载的 `== null` 为真，而 `??` 只检查真 null，**兜底不触发**。

**场景构建的正确顺序**（`BuildAllScenes`）：
```
1. EnsureFolder / BuildFloorMaterial / CreateInvisibleMaterial / PrepareWallsLayer
   <- 所有 AssetDatabase 写操作集中在这里
2. 每个 Build*Scene 内部重新 LoadAnimationMap()
3. WireOrchestrator / WireBehavior 写入引用
4. CreateWanderWalls(layerIdx)   <- 纯 GameObject，不碰 AssetDatabase
5. SaveScene
```

识别信号：「某个 null 检查通过了，但后面用起来是空的」。

### 9. Editor 写引用的三种方式，只有一种可靠
- ✅ `SetPrivateField`（`SerializedObject` + `ApplyModifiedProperties`）
- ❌ 直接赋字段 —— Unity 不知道对象变脏，`SaveScene` 会丢掉改动
- ⚠️ 对本帧新建未落盘的 GameObject 做引用写入，值会被静默丢弃

写完必须**读回复核**（用独立的 `SerializedObject`）。

### 10. `GetInstanceID()` 在 Unity 6 已废弃
用 `GetEntityId()`，或干脆别用。出现在字典 key 里会直接编译失败（CS0619）。

### 10b. 三个反复踩到的编译错
- `PetLog.System(msg)` **只有单参重载**。带 kv 要写
  `PetLog.Verb(PetLogChannel.System, msg, PetLog.Kv(...))`。已踩两次。
- `const` 成员不能用实例引用访问（CS0176）：
  `cfg.someConst` 不行，要写 `TypeName.someConst`。
- MCP 脚本里 `CompilationPipeline` 有命名空间冲突（`Unity.CompilationPipeline` 也存在），
  必须写全 `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()`。

### 10c. `FindAnyObjectByType<T>()` 对 ScriptableObject 无效
SO 不是场景对象，这个调用永远返回 null，然后静默退回兜底常量 ——
**不报错、不生效，最难查**。必须 `Resources.Load<T>()`（约定放
`Assets/Resources/PetV1/xxx.asset`）或 AssetDatabase。

### 10d. Editor 类的命名空间是 `PetV1.EditorTools`，不是 `PetV1.Editor`
写 `PetV1.Editor.PetAnimationBuilder` 会报 CS0234 ——
因为 `Editor` 这个标识符被命名空间自己占用了。

### 11. Layers 由构建工具自动建
`EnsureLayerExists("Walls", 8)` 改 `TagManager.asset`，用户层起点是第 8 层（0-7 内置）。
`ConfigureObstacleMask` 把 `1 << layer` 写进 `PetController.obstacleMask`。

### 12. Dog_001.prefab 自带 demo 组件，实例化时必须剥离
`MovePlayerInput`（每帧调旧版 Input，必崩）+ `CreatureMover`（每帧写 Animator 参数，
和 `AnimationOrchestrator` 抢同一个 Animator）。

销毁顺序有硬约束：`MovePlayerInput` 声明了
`[RequireComponent(typeof(CreatureMover))]`，先销毁 CreatureMover 会被拒绝。
用显式数组：MovePlayerInput → ThirdPersonCamera → PlayerCamera → CreatureMover。
销毁前先全部 `enabled = false`。`CreatureMover` 的 RequireComponent 会留下
`CharacterController`，要一并清掉（会跟 PetController 抢移动控制权）。

按 `Namespace.StartsWith("ithappy.Animals_FREE")` + 类型名匹配，换资产包也能复用。
**不要改资产里的 prefab。**

### 13. 活动区、墙体、相机三者要匹配
当前：活动区 ±2.2m，墙 ±3.0m，Floor 8m，相机 offset (0, 1.9, -4.2) FOV 42。

### 14. 调试面板的引用为空 = 按钮静默失效
`PetDebugPanel` 的按钮写成 `if (GUILayout.Button(...) && touchInputManager != null)`，
引用为空时不报错也不响应。所以「点了没反应」要先查引用，
不能只看代码逻辑对不对。

`Awake` 里有 `FindAnyObjectByType` 兜底，但构建时显式写入更可靠。

### 17. 编辑器层级辅助函数必须用 localPosition
`SetParent(parent, true)` + `transform.position = pos` 会把局部偏移当世界坐标。
父节点在原点时碰巧正确，父节点一离开原点就全错 ——
本项目就让绿植的叶片全跑到世界原点、正好压在狗身上，
而自动遮挡自检因为「只看狗身后」这条过滤规则报了假通过。

约定：`SetParent(parent, false)` + `localPosition` / `localScale`。
写完用 MCP dump 子物体的 `world` 和 `local`，不要只看父节点位置。

### 18. 遮挡自检用屏幕空间，不用世界距离
`VerifyNoOcclusion` 现在的判据：把Renderer 包围盒 8 个角
`WorldToScreenPoint` 取屏幕外接矩形，`rect.Overlaps(petRect)` 且
`bounds.center.z < dogDepth` 才算遮挡。

不要退回「中心到相机→狗连线的水平距离」那套 —— 它需要先假设
物体在狗身后才能算，而物体放错位置时正是这个假设不成立。

## 已实现的行为时序陷阱（改动时注意）
1. `BehaviorAction.maxDuration = 0` 表示由决策随机决定长度，
   所以**不能只用 maxDuration 判行为结束**，必须同时看 `PetStateMachine.PlannedDuration`
2. `ExecuteBehavior` 遇到相同 behavior 不能直接 return，要重新掷时长
3. 每次决策后要设 `_decisionCooldown`
4. 行为结束后**不要**在 FinishCurrentBehavior 里写死回到 Idle
5. 转向用 `Quaternion.RotateTowards`（只处理 Y 轴），不要混用 Slerp + 欧拉角 MoveTowards

## 架构（已实现）
- `PetV1.Runtime` / `PetV1.Editor` 两个 asmdef
- `GestureRecognizer` 是**纯 C#**（不依赖 MonoBehaviour），阈值在 `Config` 里可调
- `PetAnimationMap` 是换模型的唯一改动点 + fallback 链（Bark→Jump→Sit→Run→Walk→Idle）
- `PetBehaviorController` 是唯一决策入口；`PetController` 只管身体

## 场景清单
| 场景 | 路径 | 用途 |
| --- | --- | --- |
| **PetHome** | `Assets/Scenes/PetPrototype/PetHome.unity` | **主场景**，对照展板第 2 页 |
| PetPrototype | 同目录 | 原型验证（触摸 + 自主行为） |
| PerformanceTest | 同目录 | 性能探针 |
| ModelTest | 同目录 | 模型/动画自检 |

主场景由 `PetHomeSceneBuilder`（菜单 `Pet V1 / 3. Build Home Scene`）生成，
已挂进 `BuildAllScenes` 末尾。空间构成对照展板：
木地板 + 草地地毯 + 蓝垫 + 石板路 + 木框落地窗 + 窗台绿植 + 沙发 + 挂画。
房间 5 × 4.5 × 2.2 m，相机 FOV 45 / 俯角 9° / 位置 (0, 0.62, -2.6)。

**主场景未做 UI**（顶部等级条、底部导航、食物/训练入口、气泡提示），
需要另开一轮，见需求文档 3.2。调试仍用 PetDebugPanel。

### 15. 场景比例要按「狗在画面里占多少」倒推，不能照抄写实尺寸
展板是写实室内，狗看着很大。换成 0.76m 高的低模狗后，
第一版按写实比例用 9 × 7.5 × 3.2 m 的房间，截出来狗只有一个小点。
改成 5 × 4.5 × 2.2 m 之后，狗占画面 32%，接近展板的 1/3。

**验证方法**（MCP 可做，不用进 Play）：
```csharp
var s1 = cam.WorldToScreenPoint(bounds.center + Vector3.up * bounds.extents.y);
var s2 = cam.WorldToScreenPoint(bounds.center - Vector3.up * bounds.extents.y);
float pct = Mathf.Abs(s1.y - s2.y) / cam.pixelHeight * 100;   // 目标 ~33%
```

### 15d. 定构图位后必须做遮挡自检（用户截图才发现的）
相机和狗都在 x=0 中轴上，任何放在狗**身后**的家具都会正对玩家把它挡住。
第一版三盆绿植都在 x≈0，结果玩家只看到狗头从树冠后面冒出来。
这类问题**从数字上算不出来**（距离/角度都在合理范围），
只有看渲染画面才能发现 —— 而 `cam.Render()` 在 URP 下出全白，
只能用 SceneView 截图工具。

已在 `PetHomeSceneBuilder.VerifyNoOcclusion` 里做成构建时自动校验：
从相机到狗中心连线段，算每个家具到该线段的水平距离，
小于「家具半径 + 狗半宽 + 余量」就报 LogError。
只检查狗身后、高于 0.35m 的物体（贴地的地毯/石板路不算）。

**教训**：搭完场景先看渲染图，别只信坐标数字。

### 16. 模型朝向要用骨骼判断，不要凭感觉
我先按「默认朝 +Z」写，截图发现背对；改成不转，又发现还是不对。
最后用 MCP 读 `scull`（头骨）骨骼坐标才定论：
Dog_001 默认头在 **+Z** 侧，相机在 -Z，所以要转 **180°** 才是正面朝镜头。

**教训**：朝向问题用骨骼位置判定，不要靠猜。

## 操作入口
Unity 菜单：
- `Pet V1 / 1. Build Animation Assets`
- `Pet V1 / 2. Build Scenes`（含主场景，四个一起建）
- `Pet V1 / 3. Build Home Scene`（只建主场景）
- `Pet V1 / Report Capabilities`

有了 MCP 后可以直接 `Unity_RunCommand` 调 `PetSceneBuilder.BuildAllScenes()`
或 `PetHomeSceneBuilder.BuildHomeScene()`，不用再让用户点窗口触发重编译。