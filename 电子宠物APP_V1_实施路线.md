# 电子宠物 APP V1 实施路线（C# / Unity 初学者版）

> 配套文档：docs/电子宠物 APP V1 — Animals FREE 模型验证需求方案.md  
> docs/电子宠物APP_V1_代码实现说明.md  
> docs/V1_编译与验证报告.md  
> docs/电子宠物APP_V1_UI与功能需求文档.docx

## 0. 你现在在哪里（先看清地图）

**好消息：核心代码已经写完并通过编译。**

- 19 个 C# 文件，2 个程序集（PetV1.Runtime / PetV1.Editor），**0 error / 0 warning**
- 三个场景已经由 Editor 菜单一键生成：`Assets/Scenes/PetPrototype/` 下：
  - `ModelTest.unity`（只看模型与动画）
  - `PetPrototype.unity`（核心场景，含触摸 + 自主行为 + Debug 面板）
  - `PerformanceTest.unity`（FPS / 内存打日志）
- 已经验证的链路：
  - 触摸 → GestureRecognizer → GestureEvent → BehaviorController → AnimationOrchestrator → Animator
  - 自主行为（用户不操作时，狗会自己 Idle / Walk / Turn）
  - 性格（Personality 4 维）和情绪（Emotion 3 维）实际参与打分
- **还没做的**：语音训练、喂养、成长、本地存档、LLM、云端。`docs/电子宠物APP_V1_代码实现说明.md` 第六节明确列出

**所以你不是从零搭，而是**：

```
跑起来 → 读懂 → 接着做没做的功能
```

---

## 1. 第一步：把工程跑起来（最重要！）

### 1.1 打开工程

Unity Hub → Add → 选 `unity_3d_pets/`（不是 pets_game 根目录，里面是 unity_3d_pets 子目录）  
→ 选 Unity **6000.6.4f1** 版本（其他版本编译可能挂）

工程打开后会在右下角转圈导入资源，等 AssetDatabase 完成。

### 1.2 如果场景文件存在就直接跑

如果 `Assets/Scenes/PetPrototype/` 下三个 .unity 都已经在，直接：

1. Project 面板里双击 `Assets/Scenes/PetPrototype/PetPrototype.unity`
2. 上面点 Play ▶
3. 等几秒，狗出现，会自己走动 / 摇尾巴 / 偶尔转身

### 1.3 如果场景文件不在，重新生成

菜单栏：
```
Pet V1 → 1. Build Animation Assets
Pet V1 → 2. Build Scenes
```
第一条建动画资源和 AnimatorController；第二条建 3 个场景。第一次跑完后双击 PetPrototype 进 Play。

### 1.4 跑起来后做什么

- 反引号 `（键盘 1 左边的键）开关 Debug 面板
- 鼠标左键点狗 = 单指 Tap
- 按住拖动 = 抚摸（Stroke）
- Debug 面板上五个按钮（Tap / Stroke / FastSwipe / Scratch / ScreenScratch）可以注入手势事件
- 多指必须在真机上测，编辑器只能模拟单指

**先确认狗能动、能响应触摸、能自主走**——这一步完成了才算"项目真的活了"。

---

## 2. C# / Unity 你需要先懂什么

工程里用到的核心概念，建议按顺序看：

| 概念 | 第一次出现的文件 | 一句话解释 |
| --- | --- | --- |
| `class` / 命名空间 | PetEnums.cs | C# 的"类"，代码组织单位 |
| `[SerializeField]` | PetController.cs | 私有字段在 Inspector 里可见可改 |
| `MonoBehaviour` | TouchInputManager.cs | 挂到 GameObject 上的脚本基类 |
| `Update()` / `OnEnable()` | TouchInputManager.cs | 每帧 / 启用时 Unity 自动调用 |
| `enum` | PetEnums.cs | 枚举，一组命名的常量 |
| `ScriptableObject` | PetPersonality.cs | 数据资产文件（.asset），可拖到多个对象 |
| 命名空间 `using` | 所有文件 | 引入其他命名空间，例如 `using UnityEngine;` |
| `[RequireComponent]` | （Editor 工具） | 强制该组件依赖另一组件存在 |
| `Animator.SetFloat("Speed", v)` | AnimationOrchestrator.cs | 写 Animator 参数，不直接 `Play` |
| `LayerMask` | PetController.cs | 射线只打到指定层（避免打到地板） |
| 序列化歧义 | 编译报告 7.5 节 | Unity 的 "fake null"：C# 看着不为 null 但 Unity 判 null |

**不用现在全懂**——遇到再说，每读一个文件我会告诉你它用了哪些。

---

## 3. 按模块读代码（每步大约 1 个文件）

### 3.1 Core：PetLog.cs

**学到的概念**：静态方法、字典 key-value、`Application.persistentDataPath`、时间戳  
**为什么读**：所有日志走这里，先建立"看 Console 就能调试"的习惯

### 3.2 Pet：PetEnums.cs

**学到的概念**：`enum`、位掩码 `[Flags]`、struct  
**为什么先读**：所有其他文件都引用这里的枚举

### 3.3 Pet：PetPersonality.cs

**学到的概念**：`ScriptableObject`、`CreateAssetMenu`、Vector-like 字段、`Range`  
**为什么读**：理解"性格 = 数据资产"，可以从 Inspector 改数值看狗行为变化  
**对应 .asset**：`Assets/Animations/Dog/BorderColliePersonality.asset`

### 3.4 Pet：PetEmotion.cs

**学到的概念**：私有字段 + 公开属性、Clamp  
**为什么读**：情绪如何被触摸影响、按秒衰减

### 3.5 Pet：PetController.cs

**学到的概念**：`Vector3`、`Quaternion`、`LayerMask`、`Physics.SphereCast`、`NavMesh` 之外的简单 AI 移动  
**为什么读**：狗怎么移动、转向、避障、卡住脱困  
**坑**：obstacleMask 必须指向 Walls 层（第 8 层），不要手动改

### 3.6 Pet：PetStateMachine.cs

**学到的概念**：状态机、`switch` + `enum`、`SetPlannedDuration`  
**为什么读**：状态怎么迁移、动作时长怎么定

### 3.7 Animation：PetAnimationMap.cs

**学到的概念**：`ScriptableObject`、能力枚举、fallback 链  
**为什么读**：换模型时**只改这一个文件**——这是设计的关键  
**对应 .asset**：`Assets/Animations/Dog/PetDogAnimationMap.asset`

### 3.8 Animation：AnimationOrchestrator.cs

**学到的概念**：唯一 `Animator` 引用持有者、`SetFloat` vs `Play`  
**为什么读**：理解"业务代码永远不直接 Play 动画"是怎么实现的  
**关键**：`AnimationOrchestrator` 必须挂在**狗本体 prefab 实例**上，不能挂在独立的空物体（详见代码实现说明第一节）

### 3.9 Interaction：GestureEvent.cs

**学到的概念**：`struct`、序列化、`ToJson()`、`PetRegion` 枚举  
**为什么读**：所有手势事件的"通用信封"，Emotion / Behavior 都基于它

### 3.10 Interaction：GestureRecognizer.cs

**学到的概念**：纯 C# 类（非 MonoBehaviour）、轨迹采样、阈值  
**为什么读**：把 `TouchInputManager` 的原始数据归类成 GestureEvent

### 3.11 Interaction：TouchInputManager.cs

**学到的概念**：`EnhancedTouch`、射线判定、`Camera.ScreenPointToRay`、`LayerMask`  
**为什么读**：采集手指、判定点中了宠物哪个区域（Head / Body / Back）  
**注意**：不能用旧版 `Input.touches`——工程 `activeInputHandler=1` 会抛异常

### 3.12 Behavior：BehaviorAction.cs

**学到的概念**：行为定义结构、`Blackboard`、Utility Score 公式  
**为什么读**：所有行为（Idle / Walk / Run / Stop / Look …）怎么打分

### 3.13 Behavior：AutonomousBehaviorDriver.cs

**学到的概念**：抗重复（连续同动作上限 2 次）、性格修正等待时长  
**为什么读**：自主行为的"反作弊"机制

### 3.14 Behavior：PetBehaviorController.cs

**学到的概念**：Update 调度、`HandleGesture` 流程、ExecuteBehavior、ApplyBehavior  
**为什么读**：**唯一的决策入口**，所有事件最终汇聚到这里  
**学到的原则**：业务代码只调 `ExecuteBehavior()`，不要碰 Animator

### 3.15 UI：PetDebugPanel.cs

**学到的概念**：OnGUI / IMGUI（最原始的 UI）、`KeyCode` 切换、`Time.frameCount`  
**为什么读**：调试时实时看 State / Emotion / Personality  
**用法**：反引号开关，编辑器 / 真机都能用

### 3.16 Diagnostics：PetTestHelpers.cs

**学到的概念**：运行时测试类、`FindAnyObjectByType`  
**为什么读**：ModelTest / PerformanceTest 场景怎么自动跑测试

### 3.17 Editor：PetAnimationBuilder.cs

**学到的概念**：`MenuItem`、`AssetDatabase`、`AnimationUtility.SetAnimationClipSettings`、`AnimatorController` 编程创建  
**为什么读**：理解"循环 Clip 怎么打开 loopTime"  
**坑**：源 `.anim` 的 `m_LoopTime=0`，必须复制一份再开 loop

### 3.18 Editor：PetSceneBuilder.cs

**学到的概念**：`SerializedObject`、`LayerMask` 写入、SceneManagement、避障层自动建  
**为什么读**：理解一键生成 3 个场景做了什么  
**坑**：AssetDatabase 写操作必须在场景接线**之前**完成（详见编译报告 7.5 节 Unity fake null）

---

## 4. 接下来要开发的功能（按优先级）

文档说"代码里预留了接口位置但没有实现"，所以你可以接着写：

### 4.1 喂养（P0，最高优先级）

文档：13 节 + 需求文档 3.3 节

**接口位置**：
- `BehaviorController.ExecuteBehavior` 增加 `Eating` 行为 + `EATING` 状态
- `PetAnimationMap` 增加 Eat 映射（fallback 到 Idle）
- `Inventory` 数据结构：`List<FoodItem> { itemId, quantity }`
- `FeedingController` 新建文件，放 `Scripts/Feeding/`
- 喂食流程：选食品 → 拖到宠物 → 闻到 → 走近 → 吃 → 反馈
- 状态更新：`hunger -= food.hungerDelta`，`happiness += food.happinessDelta`，`exp += food.exp`

**新建 ScriptableObject**：`FoodDefinition.asset`（itemId、icon、hungerDelta、happinessDelta、exp、price）

### 4.2 语音训练（P0）

文档：12 节

**接口位置**：
- 新建 `Scripts/Training/SpeechCommandManager.cs`：调用端侧 ASR SDK（iOS Speech / Android SpeechRecognizer / 第三方）
- 新建 `Scripts/Training/CommandDictionary.cs`：中文词库 → ActionCode 映射（坐下/坐好 → SIT）
- `BehaviorController.HandleVoiceCommand(command)`：更新 `commandFamiliarity[command]` + 触发行为
- 训练熟悉度按 V1 简化：成功 +5，连续训练 5 次后响应概率提升

**测试方法**：先在 Debug 面板加按钮"模拟语音 SIT"，确认链路；再接入真 ASR

### 4.3 成长（P1）

文档：13 节

**接口位置**：
- `PetController` 加 `level`、`exp`、`stage` (PUPPY/TEEN/ADULT)
- `GrowthStageDefinition.asset` 定义每级所需经验、模型切换标记
- `BehaviorController.Update` 每秒衰减 `hunger/happiness/energy`，离线按时间差补
- 升级时切换 prefab 或材质，V1 可先只改数值 + UI 显示

### 4.4 本地存档（P1）

文档：14 节

**接口位置**：
- 新建 `Scripts/Save/SaveManager.cs` + `SaveData.cs`
- 用 `Application.persistentDataPath + "/save.json"` 存 JSON
- `JsonUtility.ToJson` / `FromJson`，**不要用 PlayerPrefs 存金币和背包**（需求文档 6 节）
- 存档时机：状态变化时（如喂食完成、训练成功）+ 切后台时 `OnApplicationPause`
- `schemaVersion` 字段必备，未来升级走 Migration

### 4.5 UI 面板（P1）

需求文档第二节页面清单

**接口位置**：
- 新建 `Scripts/UI/HUDController.cs`、`Scripts/UI/FoodInventoryPanel.cs`、`Scripts/UI/ShopPanel.cs`
- 推荐用 uGUI Canvas + TextMeshPro（需求文档 7 节）
- 4 个底部 Tab：宠物 / 食品 / 商店 / 场景

---

## 5. 真机打包测试

需求文档 8 节：Android 8.0+ / iOS 13+

### Android

1. Edit → Project Settings → Player → Android
2. Minimum API Level = 26 (Android 8.0)
3. Scripting Backend = IL2CPP（性能比 Mono 好）
4. Target Architectures = ARM64（主流手机）
5. File → Build And Run

### iOS

1. Edit → Project Settings → Player → iOS
2. Target minimum iOS Version = 13.0
3. 第一次会要求选 Xcode 路径
4. 导出 Xcode 工程 → Xcode 真机编译

### 真机必测项

- 多指抓屏（编辑器无法模拟）
- FPS（目标设备稳定 30+ FPS，高端机 60 FPS）
- 麦克风权限拒绝时降级到 Debug 面板按钮
- 后台切回前台存档不丢

---

## 6. 学习资源（不需要现在全看）

- **C# 基础**：微软官方 C# 文档，或《C# 入门经典》——重点看类、属性、泛型、委托
- **Unity 基础**：[Unity Learn](https://learn.unity.com/) 的 Junior Programmer 路径，约 20 小时
- **本项目架构**：再读一遍《技术方案文档》第三节"总体系统架构"——所有代码都在落实这个图
- **Input System**：搜 Unity 官方 "Input System" 文档，重点看 EnhancedTouch 部分
- **Animator**：搜 "Unity Animator Controller" 文档，重点看 Parameter + Transition
- **ScriptableObject**：搜 "Unity ScriptableObject" 教程，重点看 `[CreateAssetMenu]`

---

## 7. 容易踩的坑（提前知道）

| 坑 | 怎么避 |
| --- | --- |
| 旧版 `Input.touches` 在新工程里抛异常 | 全用 EnhancedTouch，参见代码实现说明第一节 |
| 业务代码 `animator.Play("Walk")` 直接调 | 走 AnimationOrchestrator，参见代码实现说明第二节 |
| 源动画 Clip `loopTime=0` 导致 Idle 卡住 | 复制一份再开 loop，见 PetAnimationBuilder |
| `AnimationOrchestrator` 挂在空物体上 | 必须挂狗本体 prefab 实例 |
| AssetDatabase 写操作后 `?? fallback` 不触发 | 写操作必须前置到场景接线之前，详见编译报告 7.5 |
| 类名与文件名不一致 → ScriptableObject 创建失败 | 一个类一个文件，文件名与类名完全一致 |
| 资产自带 demo 脚本（MovePlayerInput 等） | PetSceneBuilder 会自动剥离，但不要手动加回去 |
| `Random` 在 `UnityEngine` / `System` 歧义 | 显式写 `UnityEngine.Random` |
| 枚举不能直接移位 | `(PetCapabilities)(1 << (int)x)` |
| 多指必须在真机 | 编辑器模拟不到真实多点触控 |

---

## 8. 你的下一步（建议今天就做）

1. **打开 Unity → 双击 `Assets/Scenes/PetPrototype/PetPrototype.unity` → 按 Play**
2. 看到狗动了之后，反引号开 Debug 面板
3. 看一眼 `Scripts/Pet/PetEnums.cs`（最短的文件，建立信心）
4. 看一眼 `Scripts/Pet/PetEmotion.cs`（60 行左右，看 `OnTouch` / `Tick`）

如果第 1 步跑不通：
- 报错信息贴给我
- 看 `Library/Logs/Editor.log`（最后一段，不是整段）
- 不要在场景里手动删组件——会被 PetSceneBuilder 一键重建覆盖
