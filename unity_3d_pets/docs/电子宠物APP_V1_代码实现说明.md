# 电子宠物 APP V1 — Animals FREE 模型验证：代码实现说明

> 对应需求文档：`docs/电子宠物 APP V1 — Animals FREE 模型验证需求方案.md`
> 引擎：Unity 6000.6.4f1 / URP 17.6.0 / Input System 1.20.0
> 状态：代码已实现并通过编译；三个场景由 Editor 菜单一键生成

---

## 一、先做的三个技术决定（与文档不同，附原因）

### 1. 触摸层用 `EnhancedTouch` 而不是 `Input.touches`

本工程 `ProjectSettings.asset` 里 `activeInputHandler: 1`，即**只启用新 Input System**。
在此设置下调用旧版 `UnityEngine.Input.touches` / `Input.GetKey` 会直接抛
`InvalidOperationException`，不是警告而是运行时崩溃。

因此：
- `TouchInputManager` 用 `UnityEngine.InputSystem.EnhancedTouch`
- `PetDebugPanel` / `PetModelTestDemo` 的按键用 `Keyboard.current`
- 编辑器内用鼠标左键模拟单指；**多指必须上真机测**，编辑器无法模拟

### 2. Animator 用 Speed 参数驱动，不用 `animator.Play()`

文档第十二节明确禁止业务代码直接 `animator.Play("Walk")`。实现方式是：

```
BehaviorController -> AnimationOrchestrator -> Animator.SetFloat("Speed", v)
```

`Speed` 阈值按文档 4.3 设置：`> 0.1` 进 Walk，`> 2.0` 进 Run。
`AnimationOrchestrator` 是全工程唯一持有 `Animator` 引用的类（除测试诊断代码外），
每帧把 `PetController.CurrentSpeed` 同步到 `Speed`，保证动画与实际位移一致。

**`AnimationOrchestrator` 必须挂在狗本体上，不能挂在独立的空物体上。**
这是踩过的坑：最初把它挂在场景里的 `PetRuntime` 空物体上，而 Animator 在狗prefab 内部，
两者不是父子关系 → `GetComponentInChildren<Animator>()` 返回 null →
Speed 参数永远写不进去 → 动画停在 Idle → 宠物「四肢不动地滑行」，
而且所有触摸交互看起来也没反应（行为在跑但表现不出来）。

另外，Unity 里对**本帧新建、尚未落盘**的 GameObject 上的组件做
`SerializedObject` 引用写入，值会被**静默丢弃**（字段在 Inspector 里看着设了，
场景文件序列化出来是 `{fileID: 0}`）。对照实验证实：同一场景、同一 map 对象、
同一个写入函数，挂在已落盘 prefab 实例上的 `behaviorController.animationMap` 写入成功，
挂在 `new GameObject()` 上的 `animationMap` 写入失败。
所以运行时组件统一挂在狗本体（已落盘的 prefab 实例）上。

### 3. 源动画 Clip 必须重新生成才能循环

已核对 `Assets/ithappy/Animals_FREE/Animations/Other_Animations/*.anim`：
源文件的 `m_AnimationClipSettings.m_LoopTime = 0`。直接使用的话 Idle 播完会**停在最后一帧**，
Walk 会走一步卡住。所以 `PetAnimationBuilder` 会复制一份并强制打开 `loopTime + loopBlend`。

---

## 二、代码结构

```
Assets/Scripts/
├── Core/
│   └── PetLog.cs                      分类日志：System/Touch/Gesture/Emotion/State/Behavior/Animation
│                                       带 key-value 明细 + 环形历史 + 锚点跳转
├── Pet/
│   ├── PetEnums.cs                    PetState / PetBehavior / PetCapability / PetCapabilities / PetAnimationEntry
│   ├── PetPersonality.cs              11 维性格 + Profile 资产；AutonomousDrive / Restlessness / InteractionDrive 三个派生量
│   ├── PetEmotion.cs                  Happiness/Excitement/Sleepiness(0~100) + 衰减 + 移动消耗
│   ├── PetContext.cs                  决策只读上下文
│   ├── PetController.cs               移动/转向/边界钳制/扇形避障/卡住脱困
│   └── PetStateMachine.cs             PetState 迁移 + 计划时长
├── Animation/
│   ├── PetAnimationMap.cs             换模型的唯一改动点；能力映射 + fallback 链
│   └── AnimationOrchestrator.cs       唯一允许触碰 Animator 的类（挂在狗本体上）
├── Interaction/
│   ├── GestureEvent.cs                GestureType / PetRegion / GestureEvent / PetRegionCollider
│   ├── GestureRecognizer.cs           纯 C# 手势识别，阈值全可调
│   └── TouchInputManager.cs           EnhancedTouch 采集 + 射线判定区域
├── Behavior/
│   ├── BehaviorAction.cs              BehaviorAction / BehaviorLibrary / Blackboard / UtilityScorer
│   ├── AutonomousBehaviorDriver.cs    自主决策 + 抗重复 + 性格影响的等待时长
│   └── PetBehaviorController.cs       唯一决策入口
├── UI/
│   └── PetDebugPanel.cs               文档第十五节要求的调试面板
├── Diagnostics/
│   └── PetTestHelpers.cs              PetModelTestDemo / PetPerformanceProbe / PetSceneInfo
└── Editor/
    ├── PetAnimationBuilder.cs         生成循环 Clip + AnimatorController + AnimationMap
    └── PetSceneBuilder.cs             程序化生成三个场景
```

程序集拆分：`PetV1.Runtime`（进 Build）与 `PetV1.Editor`（仅编辑器）。
放在 `Editor` 目录下的 MonoBehaviour 无法进 Build，所以三个会被挂进场景的组件
（`PetModelTestDemo` / `PetPerformanceProbe` / `PetSceneInfo`）都放在 `Diagnostics`。

---

## 三、如何运行（两步）

### 第一步：生成动画资源与场景

Unity 菜单栏：

```
Pet V1 → 1. Build Animation Assets
Pet V1 → 2. Build Scenes
```

第一条会产出：

| 文件 | 说明 |
| --- | --- |
| `Assets/Animations/Dog/Dog_Idle.anim` | 循环开启的 Idle |
| `Assets/Animations/Dog/Dog_Walk.anim` | 循环开启的 Walk |
| `Assets/Animations/Dog/Dog_Run.anim` | 循环开启的 Run |
| `Assets/Animations/Dog/PetDog.controller` | Speed 参数驱动的 Idle/Walk/Run + OneShot 层 |
| `Assets/Animations/Dog/PetDogAnimationMap.asset` | 能力映射表 |
| `Assets/Animations/Dog/BorderColliePersonality.asset` | 品种性格偏置 |

第二条会产出三个场景：

| 场景 | 用途 |
| --- | --- |
| `Assets/Scenes/PetPrototype/ModelTest.unity` | 只看模型与动画：自动循环 Idle→Walk→Run，空格手动切 |
| `Assets/Scenes/PetPrototype/PetPrototype.unity` | 核心场景，含触摸 + 自主行为 + Debug 面板 |
| `Assets/Scenes/PetPrototype/PerformanceTest.unity` | FPS / 内存 / 对象数每秒打日志 |

### 第二步：打开 PetPrototype 场景按 Play

- 反引号 `` ` `` 开关调试面板
- 鼠标左键点击宠物 = 单指 Tap
- 按住拖动 = Stroke（抚摸）
- 面板上有 Tap / Stroke / FastSwipe / Scratch / ScreenScratch 五个注入按钮，
  用来在没有真机时验证「Gesture → Emotion → Behavior → Animation」链路
- **多指抓屏与多指触摸只能在真机上测**（编辑器无法产生真实多点触控）

---

## 四、两条主链路的实现位置

### 链路一：用户触摸

```
TouchInputManager.OnEnable            EnhancedTouchSupport.Enable()
TouchInputManager.Update              逐帧采集指针 -> TryResolvePet 射线判定区域
GestureRecognizer.Tick                归类为 GestureEvent（纯 C#，无 MonoBehaviour 依赖）
PetBehaviorController.HandleGesture   1) 更新 Emotion  2) 选行为  3) ExecuteBehavior
PetBehaviorController.RespondInPlace  点击/抚摸 -> Interacting 状态，原地响应 interactDuration 秒
PetBehaviorController.ApplyBehavior   -> AnimationOrchestrator.PlayBehavior
AnimationOrchestrator.PlayCapability  -> Map 解析 + fallback -> Animator.SetFloat("Speed")
```

情绪增减按文档第十节实现：
Tap → Happiness +2 / Excitement +1；多指 → Excitement +5；无互动 → Excitement 按秒衰减。

### 链路二：自主行为

```
PetBehaviorController.Update
  -> TickAutonomy
      -> UtilityScorer.Score(候选, context)   Energy/Curiosity/Playfulness/Independence 参与打分
      -> AutonomousBehaviorDriver.WouldRepeat 抗重复（连续同动作上限 2 次）
      -> ExecuteBehavior(chosen, P5)
          -> ApplyBehavior
              -> PetController.MoveTo(GetRandomWanderPoint(), speed)
              -> PetStateMachine.TransitionTo + SetPlannedDuration
              -> AnimationOrchestrator.PlayBehavior
```

性格如何影响行为（文档 5.2 / 8.1）：

| 性格 | 影响 |
| --- | --- |
| Energy | `Restlessness` 高 → Idle 等待时间短、Walk 分数高 |
| Curiosity | 探索与转向分数高 |
| Playfulness | `InteractionDrive` 高 → 触摸更可能触发 Excited |
| Independence | `AutonomousDrive` 高 → Idle 分数降低，更倾向自己动 |

---

## 五、模型能力现状（对应文档第十四节）

已核对 `Animals_FREE` 实际资源，只有三个动画 Clip：

| 能力 | 状态 | 依据 |
| --- | --- | --- |
| Idle | 有 | `Dog_001_idle.anim`（9.63s） |
| Walk | 有 | `Dog_001_walk.anim` |
| Run | 有 | `Dog_001_run.anim` |
| Sit / Lie / Jump / Bark / Eat / Sleep | 无 | 目录下无对应 .anim |

所以 `PetAnimationMap` 只登记 Idle/Walk/Run。缺能力时走 fallback 链，例如：

```
Bark -> Jump -> Sit -> Run -> Walk -> Idle
Jump -> Bark -> Run -> Walk -> Idle
```

`PlayCapability` 里会打印 `[Animation] capability fallback requested=... resolved=...`，
可在 Console 确认降级确实发生了。

想看完整能力报告：菜单 `Pet V1 → Report Capabilities`。

---

## 六、已知限制（验收时会遇到，先说清楚）

1. **多指手势无法在编辑器验证。** `EnhancedTouch` 在编辑器里拿不到真实多点触控，
   必须打包到手机或用 Unity Remote 调试。面板的 Scratch 按钮是合成事件，只验证链路不验证识别。

2. **触地区域目前只有 Head / Body / Back。** 骨骼里确认有 `scull`（头）、
   `spine` 链（背），场景构建时按这些名字挂 Collider。
   Leg 与 Tail 暂未拆——`scull` 之后的链路末端没有独立尾骨，等真实模型确认再补。

3. **避障层由构建工具自动创建。** `Build Scenes` 会在 `TagManager` 第 8 层建 `Walls`，
   四面墙放该层，并把 `PetController.obstacleMask` 指向它（`m_Bits: 256`）。
   无需手工建层。Floor 留在第 0 层，不参与避障探测。

4. **资产自带 demo 组件已在实例化时剥离。** `Dog_001.prefab` 挂着
   `ithappy.Animals_FREE.MovePlayerInput` / `CreatureMover`，前者每帧调旧版 `Input`
   （本工程 `activeInputHandler=1` 会抛异常），后者每帧写 Animator 参数会与
   `AnimationOrchestrator` 冲突。`PetSceneBuilder.RemoveAssetDemoComponents` 负责剥离，
   不改资产本身。

5. **Collider 是手摆的，不是自动生成的。** 胶囊/球体的尺寸（半径 0.22、0.14）按
   Dog_001 的实际比例估的。狗的实际身高约 0.5m，如果显示比例不对需要调
   `PetSceneBuilder.SetupRegionColliders` 里的数值。

6. **速度值 1.2 / 3.0 m/s 是拍的。** 文档没给具体数值，这两个值写在
   `PetAnimationMap.defaultWalkSpeed / defaultRunSpeed`，按实际步态手感再调。
   Animator 的 Speed 阈值（0.1 / 2.0）是文档 4.3 明确给的，没有改。

7. **Walk Clip 只有 1 秒一个循环**（资产原时长，Run 是 0.57s）。步态可能偏急促，
   需要在 Play 模式里看实际效果再决定是否调速度或过渡时长。

8. **没有做语音、喂养、成长、存档。** 文档第三节把这些列为本阶段不验证，
   代码里预留了接口位置但没有实现。

---

## 七、编译验证记录

用 Unity 6000.6.4f1 batchmode 编译 `PetV1.Runtime` 与 `PetV1.Editor`，结果见
`docs/V1_编译与验证报告.md`。

编译期修掉的实际错误（均已在报告中记录）：
- `Touch` 在 `UnityEngine` 与 `EnhancedTouch` 之间歧义 → 改用命名空间别名
- `EnhancedTouchSupport` 未找到 → 补 `using` 别名
- `Physics.defaultRaycastLayers` 在 Unity 6 已移除 → 改用 `~0`
- `Random` 在 `UnityEngine` 与 `System` 之间歧义 → 显式限定 `UnityEngine.Random`
- 能力位枚举移位需显式转 int