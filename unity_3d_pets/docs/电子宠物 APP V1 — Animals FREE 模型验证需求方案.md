# 电子宠物 APP V1
## Animals FREE 模型验证需求方案

**版本：V1.0**  
**验证阶段：Prototype / 技术可行性验证**  
**Unity：Unity 6.6**  
**Render Pipeline：URP**  
**模型：Animals FREE - Animated Low Poly 3D Models**  
**核心目标：0 元验证电子宠物核心交互与行为架构**

---

# 一、验证背景

电子宠物 APP 第一阶段的主要目标不是制作最终商业版本，而是验证以下核心问题：

> 用户是否愿意持续与一个具有生命感、会自主行动、能够响应触摸和语音指令的 3D 宠物互动？

因此，V1 不应该过早投入高质量 3D 模型、复杂 AI、真实宠物个性化、复杂视觉识别等成本。

本阶段采用免费的 **Animals FREE - Animated Low Poly 3D Models** 作为验证模型。

该资源包含多个低多边形动物，其中包含狗模型，并提供基础 Idle、Walk、Run 等动画，适合低成本验证 Unity 中的动物模型、动画控制、行为状态和交互架构。

本模型**不是最终商业模型**。

它只是：

> **V1 Pet Behavior Prototype 的身体载体。**

---

# 二、验证目标

本阶段需要验证 6 个核心问题。

## 2.1 模型可用性

验证：

- Unity 6.6 是否能够正常导入
- URP 是否正常显示
- 模型材质是否正常
- 模型 Rig 是否可用
- Animation Clip 是否能够正确播放
- Animator 是否能够控制模型
- 模型在移动端性能是否可接受

---

# 2.2 动画控制能力

验证能否建立：

```text
Idle
 ↓
Walk
 ↓
Run
 ↓
Idle
```

以及：

```text
外部事件
 ↓
Behavior Controller
 ↓
Animator
 ↓
Animation Clip
```

核心原则：

> 以后任何触摸、语音、喂养、Emotion、Personality 都不能直接操作 Animator。

统一由 Behavior Controller 控制。

---

# 2.3 触摸互动能力

验证用户能否通过屏幕触摸产生结构化互动事件。

第一阶段至少实现：

```text
单指点击
单指拖动
单指持续触摸
多指触摸
快速滑动
```

然后转化为：

```json
{
  "gesture": "HEAD_TAP",
  "target_region": "HEAD",
  "finger_count": 1,
  "duration_ms": 350
}
```

而不是：

```text
Touch → PlayAnimation()
```

---

# 2.4 宠物行为架构

验证：

```text
Input
 ↓
Perception
 ↓
Intent
 ↓
Emotion
 ↓
Personality
 ↓
Behavior
 ↓
Animation
```

是否能够跑通。

即使当前模型只有少量动画，也必须先把这一层架构建立起来。

---

# 2.5 宠物自主行为

验证宠物不是一个“用户点一下才动”的模型。

至少实现：

```text
Idle
 ↓
随机等待
 ↓
Walk
 ↓
随机移动
 ↓
Idle
```

并增加随机：

- 停留
- 转向
- 观察
- 走动
- 回到初始区域

从而验证最基础的：

> **生命感。**

---

# 2.6 后续模型替换能力

这是本次验证非常重要的工程目标。

未来：

```text
Animals FREE
       ↓
Cartoon Border Collie
       ↓
Realistic Border Collie
       ↓
用户自己的宠物模型
```

不能导致：

- PetController 重写
- BehaviorController 重写
- Touch 系统重写
- Personality 重写
- Emotion 重写

理想状态：

> **换模型，不换宠物大脑。**

---

# 三、本阶段明确不验证的内容

由于 Animals FREE 的动画数量有限，本阶段不要强行让它承担最终宠物全部表现。

以下功能暂时不作为模型验收条件：

### 3.1 高级动作

暂不要求：

- Sit
- Lie Down
- Jump
- Bark
- Eat
- Sleep
- Scratch
- Shake
- Paw
- Roll
- Attack
- Hug

如果模型没有对应 Animation Clip，不进行强行制作。

---

### 3.2 复杂表情

暂不要求：

- 眨眼
- 耳朵独立动作
- 嘴部表情
- 舌头
- 面部 BlendShape
- 眼神跟踪

---

### 3.3 AI 视觉识别

暂不做：

- 摄像头识别
- 真实宠物检测
- 姿态识别
- 行为识别
- 视频分析

---

### 3.4 LLM

暂不做：

- ChatGPT 对话
- 自然语言聊天
- 长期记忆总结
- LLM 控制行为

---

### 3.5 云端系统

暂不要求：

- 用户账号
- 云存档
- PostgreSQL
- Redis
- 对象存储

本阶段优先使用本地数据。

---

# 四、模型验收

## 4.1 导入验证

创建：

```text
Assets/
    Art/
        AnimalsFree/
```

导入模型后检查：

- FBX
- Materials
- Textures
- Rig
- Avatar
- Animation Clips

---

## 4.2 Rig 验证

确认：

```text
Model
 ↓
Rig
 ↓
Avatar
 ↓
Animator
```

能够正常工作。

重点记录：

- Rig 类型
- Bone 数量
- Root Bone
- 是否存在有效 Avatar
- 是否可以正常播放 Animation Clip

---

## 4.3 Animation 验证

建立：

```text
AnimationTestScene
```

将模型放入场景。

创建：

```text
Idle
Walk
Run
```

三个基础状态。

Animator：

```text
        ┌───────┐
        │ Idle  │
        └───┬───┘
            │
       Speed > 0.1
            ↓
        ┌───────┐
        │ Walk  │
        └───┬───┘
            │
       Speed > 2.0
            ↓
        ┌───────┐
        │ Run   │
        └───────┘
```

最终由：

```text
MovementSpeed
```

控制 Idle / Walk / Run。

---

# 五、第一阶段核心行为

由于模型目前主要用于基础动作验证，因此行为系统采用最小版本。

## 5.1 Pet State

定义：

```csharp
public enum PetState
{
    Idle,
    Walking,
    Running,
    Interacting,
    Recovering
}
```

未来再扩展：

```text
Eating
Training
Sleeping
Excited
Growing
```

---

# 5.2 Pet Personality

仍然按照技术方案保留 Personality 数据结构。

第一阶段不需要让所有属性真正影响行为。

保留：

```text
Energy
Sociability
Affection
Curiosity
Courage
Impulsiveness
Obedience
Playfulness
Independence
Sensitivity
LearningAbility
```

范围：

```text
0.0 ~ 1.0
```

第一阶段只实际使用：

```text
Energy
Curiosity
Playfulness
Independence
```

例如：

```text
Energy 高
→ 更频繁 Walk

Curiosity 高
→ 更容易离开原地探索

Playfulness 高
→ 更容易响应互动

Independence 高
→ 用户没有操作时也会自主行动
```

---

# 六、触摸验证需求

## 6.1 第一阶段触摸范围

由于当前模型不一定存在头部/背部专用 Collider，因此先建立：

```text
Pet Collider
```

随后根据模型实际骨骼情况决定是否拆分：

```text
Head
Body
Tail
Leg
```

---

# 6.2 基础触摸

实现：

### Tap

```text
用户点击宠物
 ↓
TouchInputManager
 ↓
GestureRecognizer
 ↓
HEAD_TAP / BODY_TAP
 ↓
PetBehaviorController
```

当前模型没有专用“被摸反应”动画时：

> 不要求立即出现特殊动作。

可以先：

```text
Tap
 ↓
Emotion.Happiness += 2
 ↓
Interaction Event
 ↓
尾部/移动/Idle变化（若模型能力允许）
```

重点验证的是**事件链路**。

---

# 6.3 Drag

用户在宠物区域持续滑动：

```text
Touch Down
 ↓
Move
 ↓
Touch Up
```

记录：

```text
fingerCount
startPosition
currentPosition
distance
speed
duration
trajectory
targetRegion
```

形成：

```json
{
  "gesture": "STROKE",
  "finger_count": 1,
  "duration_ms": 850,
  "distance": 120,
  "speed": 0.14
}
```

---

# 6.4 Multi-Touch

验证：

```text
1 finger
2 fingers
3 fingers
```

是否能够正确识别。

最终输出：

```text
fingerCount
```

并进入行为系统。

---

# 七、行为系统验证

建立：

```text
PetBehaviorController
```

作为核心入口。

结构：

```text
Touch
Voice
Food
Auto Behavior
      │
      ▼
Perception / Intent
      │
      ▼
Emotion
      │
      ▼
Personality
      │
      ▼
Behavior Controller
      │
      ▼
Animation Orchestrator
      │
      ▼
Animator
```

---

# 八、Behavior Controller

第一阶段只实现以下行为：

```text
Idle
Walk
Run
Stop
Look / Turn
Recover
```

---

## 8.1 Idle

宠物进入 Idle 后：

```text
等待 2~8 秒
```

随机决定：

```text
继续 Idle
Walk
Turn
```

概率由 Personality 影响。

例如：

```text
Energy 高
→ Walk 概率增加

Independence 高
→ 自动行为概率增加

Curiosity 高
→ 探索概率增加
```

---

# 8.2 Walk

随机选择：

```text
方向
距离
速度
```

例如：

```text
Walk 1~3 秒
 ↓
Stop
 ↓
Idle
```

必须避免宠物：

- 走出场景
- 穿墙
- 穿过摄像机
- 卡在角落

---

# 8.3 Run

Run 暂时不用于普通 Idle。

只用于测试：

```text
用户快速滑动
或
测试按钮
```

触发：

```text
Run
 ↓
1~2 秒
 ↓
Stop
 ↓
Idle
```

用于验证 Animator 状态切换。

---

# 九、屏幕互动 Prototype

本阶段可以先不要求真正的“扑屏动画”。

但是必须把事件接口建立起来。

定义：

```text
SCREEN_SCRATCH
SCREEN_TAP
SCREEN_SWIPE
SCREEN_MULTI_TOUCH
```

例如：

```text
用户快速多指触摸
        ↓
GestureRecognizer
        ↓
SCREEN_SCRATCH
        ↓
PetBehaviorController
        ↓
Excited
```

如果当前模型没有 Jump / Bark：

```text
Excited
 ↓
Run
 ↓
Turn
 ↓
Return
```

即可。

未来换 Border Collie：

```text
Excited
 ↓
Bark
 ↓
Jump
 ↓
Screen Impact
 ↓
Crack VFX
```

不需要修改 Gesture 系统。

---

# 十、Emotion 最小实现

第一阶段只保留：

```text
Happiness
Excitement
Sleepiness
```

范围：

```text
0~100
```

---

## 10.1 Touch

```text
Touch
 ↓
Happiness + 2
Excitement + 1
```

---

## 10.2 Multi Touch

```text
Multi Touch
 ↓
Excitement + 5
```

---

## 10.3 长时间无互动

```text
Time Since Interaction
 ↓
Excitement slowly decreases
```

---

# 十一、自主行为系统

这是本次验证最重要的功能之一。

宠物即使用户什么都不做，也必须会：

```text
Idle
 ↓
等待
 ↓
Walk
 ↓
Turn
 ↓
Idle
```

例如：

```text
0~5 秒
Idle

5~10 秒
可能 Walk

10~15 秒
可能 Turn

15~25 秒
Idle
```

随机性不能完全随机。

应该受到：

```text
Personality
Emotion
CurrentState
```

影响。

---

# 十二、Animation Orchestrator

禁止业务系统直接调用：

```csharp
animator.Play(...)
```

统一：

```text
BehaviorController
       ↓
AnimationOrchestrator
       ↓
Animator
```

例如：

```csharp
PlayBehavior(PetBehavior.Walk);
```

而不是：

```csharp
animator.Play("Walk");
```

这样未来更换模型时，只需要修改：

```text
Animation Mapping
```

---

# 十三、Animation Mapping

建立：

```text
PetAnimationMap
```

例如：

```text
Idle
→ Dog_Idle

Walk
→ Dog_Walk

Run
→ Dog_Run
```

未来：

```text
BorderCollie_Idle
BorderCollie_Walk
BorderCollie_Run
```

只需要替换映射。

---

# 十四、模型能力抽象

定义：

```csharp
public enum PetCapability
{
    Idle,
    Walk,
    Run,
    Sit,
    Lie,
    Jump,
    Bark,
    Eat,
    Sleep,
    Attack
}
```

当前 Animals FREE：

```text
Idle       ✓
Walk       ✓
Run        ✓

Sit        ?
Lie        ?
Jump       ?
Bark       ?
Eat        ?
Sleep      ?
```

实际导入后填写。

行为系统不能假设所有模型都有这些能力。

例如：

```text
要求 Bark
 ↓
检查 Capability
 ↓
有 Bark？
 ├─ Yes → Bark
 └─ No  → fallback
```

Fallback：

```text
Bark
 ↓
Excited
 ↓
Run
```

---

# 十五、测试 UI

为了提高开发效率，Prototype 必须增加一个 Debug Panel。

建议：

```text
┌─────────────────────────┐
│ PET DEBUG               │
│                         │
│ State: Idle             │
│ Emotion: Happy 63       │
│ Energy: 82              │
│                         │
│ [Idle] [Walk] [Run]     │
│                         │
│ [Touch] [Scratch]       │
│                         │
│ [Reset]                 │
└─────────────────────────┘
```

同时显示：

```text
CurrentState
CurrentBehavior
CurrentAnimation
Emotion
Personality
LastGesture
LastEvent
```

---

# 十六、日志系统

开发阶段增加：

```text
[Touch]
[Gesture]
[Behavior]
[Emotion]
[Animation]
```

例如：

```text
[Touch]
fingerCount=1

[Gesture]
gesture=STROKE

[Emotion]
happiness +2

[Behavior]
Idle → Interacting

[Animation]
Interacting → Idle
```

这会极大降低后续调试成本。

---

# 十七、性能验证

本模型的价值之一就是作为低成本性能基准。

需要测试：

### Editor

```text
1920 × 1080
```

### Mobile Simulation

模拟：

```text
低端 Android
中端 Android
iPhone
```

重点记录：

```text
FPS
CPU
GPU
Memory
Draw Calls
Batches
```

第一阶段目标：

```text
稳定 60 FPS
```

如果低端设备无法稳定 60 FPS：

```text
最低目标：30 FPS
```

---

# 十八、场景要求

建立三个场景。

## Scene 1：ModelTest

只验证：

```text
Model
Rig
Material
Animation
```

---

## Scene 2：PetPrototype

用于核心开发：

```text
Pet
Camera
Floor
Lighting
Touch
Behavior
Debug UI
```

---

## Scene 3：PerformanceTest

用于：

```text
FPS
Memory
Rendering
Mobile
```

---

# 十九、第一阶段开发顺序

严格按照下面顺序进行。

## Day 1

### Unity 项目

```text
Unity 6.6
URP
Git
```

创建：

```text
Assets/
    Art/
    Animations/
    Prefabs/
    Scenes/
    Scripts/
    UI/
```

---

# Day 2

### 导入 Animals FREE

完成：

```text
Model
Material
Rig
Avatar
Animation
```

验收：

```text
Dog appears correctly
Idle works
Walk works
Run works
```

---

# Day 3

### Animator

完成：

```text
Idle
Walk
Run
```

实现：

```text
Speed
```

控制状态。

---

# Day 4

### PetController

实现：

```text
PetState
PetContext
PetController
AnimationOrchestrator
```

---

# Day 5

### Touch

实现：

```text
Tap
Drag
MultiTouch
```

并生成：

```text
GestureEvent
```

---

# Day 6

### Behavior

实现：

```text
Idle
Walk
Run
Stop
Recover
```

---

# Day 7

### Autonomous Behavior

实现：

```text
Idle
 ↓
Random Decision
 ↓
Walk
 ↓
Idle
```

---

# 第二周

加入：

```text
Emotion
Personality
Debug UI
Animation Mapping
Capability System
```

---

# 第三周

加入：

```text
Screen Interaction
Gesture → Behavior
VFX Prototype
Sound
Camera Shake
```

并完成第一轮体验测试。

---

# 二十、V1 验收标准

## A. 模型

- [ ] Unity 6.6 正常导入
- [ ] URP 正常显示
- [ ] 材质正常
- [ ] Rig 正常
- [ ] Avatar 正常
- [ ] Idle 正常
- [ ] Walk 正常
- [ ] Run 正常

---

## B. Animator

- [ ] Idle → Walk
- [ ] Walk → Run
- [ ] Run → Walk
- [ ] Walk → Idle
- [ ] 状态切换没有明显卡顿
- [ ] 不直接由业务代码操作 Animator

---

## C. Touch

- [ ] 单指 Tap
- [ ] 单指 Drag
- [ ] 多指 Touch
- [ ] Finger Count
- [ ] Duration
- [ ] Speed
- [ ] Distance
- [ ] Gesture Event

---

## D. Behavior

- [ ] Idle
- [ ] Walk
- [ ] Run
- [ ] Stop
- [ ] Random Behavior
- [ ] Behavior Priority

---

## E. Emotion

- [ ] Happiness
- [ ] Excitement
- [ ] Sleepiness
- [ ] 触摸能够影响 Emotion

---

## F. Personality

- [ ] Energy
- [ ] Curiosity
- [ ] Playfulness
- [ ] Independence

至少 4 个属性实际参与行为计算。

---

## G. Autonomous Behavior

- [ ] 用户不操作时宠物仍会行动
- [ ] 行为具有随机性
- [ ] Personality 能影响行为概率
- [ ] 宠物不会无限走出场景
- [ ] 宠物不会频繁重复同一个动作

---

## H. Debug

- [ ] 当前 State
- [ ] 当前 Behavior
- [ ] 当前 Animation
- [ ] 当前 Emotion
- [ ] Last Gesture
- [ ] Last Event

---

# 二十一、本阶段最重要的验收

本阶段并不是：

> “狗模型做得好不好看？”

真正的验收是下面这条链路：

```text
                    ┌──────────────┐
                    │ User Touch   │
                    └──────┬───────┘
                           ↓
                    GestureRecognizer
                           ↓
                      GestureEvent
                           ↓
                    ┌──────────────┐
                    │   Emotion    │
                    └──────┬───────┘
                           ↓
                    ┌──────────────┐
                    │ Personality  │
                    └──────┬───────┘
                           ↓
                    BehaviorSystem
                           ↓
                 AnimationOrchestrator
                           ↓
                       Animator
                           ↓
                       3D Pet
```

同时：

```text
User 不操作
     ↓
Autonomous Behavior
     ↓
BehaviorSystem
     ↓
Animation
     ↓
Pet 自主行动
```

如果这两条链路跑通：

> **V1 的核心技术验证就成功了一大半。**

---

# 二十二、成功标准

最终我们希望打开 App 后看到的不是：

> “一个 3D 狗模型。”

而是：

> “一个正在自己生活的东西。”

即使当前只有：

```text
Idle
Walk
Run
```

也应该做到：

```text
用户摸它
    ↓
它知道有人在互动

用户停止操作
    ↓
它不会完全僵住

等待一段时间
    ↓
它自己走动

用户再次互动
    ↓
它停止自主行为并响应

不同 Personality
    ↓
自主行为频率不同
```

如果这一点成立，那么我们已经验证了：

> **“Pet Brain + Pet Body” 的核心架构是成立的。**

之后换成 Border Collie，只是增加：

```text
Sit
Lie
Eat
Sleep
Bark
Jump
Scratch
```

以及更丰富的：

```text
Animation
Emotion
VFX
Sound
```

而不是重新开发整个系统。

---

# 二十三、本阶段最终输出物

完成后必须产生以下成果：

```text
01 Unity 6.6 URP Project
02 Animals FREE Dog Prefab
03 PetController
04 PetStateMachine
05 AnimationOrchestrator
06 AnimationMap
07 GestureRecognizer
08 TouchInputManager
09 BehaviorController
10 Personality
11 Emotion
12 AutonomousBehavior
13 PetCapability
14 DebugPanel
15 ModelTest Scene
16 PetPrototype Scene
17 PerformanceTest Scene
18 Test Report
```

最终形成：

> **电子宠物 APP V1 Prototype 0.1**

---

# 二十四、下一阶段升级路线

当 Animals FREE 验证完成后：

```text
Prototype 0
Animals FREE
     ↓
验证架构
     ↓
Prototype 1
Cartoon Border Collie / Malinois
     ↓
加入丰富动画
     ↓
Touch Reaction
     ↓
Training
     ↓
Feeding
     ↓
Growth
     ↓
Voice
     ↓
Personality Learning
     ↓
真实宠物个性化
```

因此：

**Animals FREE 不需要承担最终产品的所有功能。**

它的使命只有一个：

> **用 0 元成本证明我们的“宠物大脑 + 宠物身体 + 用户互动”这套技术架构是可行的。**