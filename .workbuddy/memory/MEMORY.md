# pets_game 项目长期约定

## 项目定位
电子宠物 APP，V1 = Prototype / 技术可行性验证。
核心命题：**0 元成本证明「宠物大脑 + 宠物身体 + 用户互动」架构可行**。
基线：Unity 6.6 + URP + C#。**V1 验证载体是免费的 Animals FREE - Animated Low Poly 3D Models**（不是 Border Collie）。

## 不可违背的架构原则
1. **业务系统不得直接操作 Animator。** 统一链路：
   `Input → Perception/Intent → Emotion → Personality → BehaviorController → AnimationOrchestrator → Animator`。
   业务只调 `PlayBehavior(PetBehavior.X)`，不调 `animator.Play("X")`。
2. **换模型不换大脑。** 换模型只允许改 `PetAnimationMap` 映射表，不允许改
   PetController / BehaviorController / Touch / Personality / Emotion。
   模型差异通过 `PetCapability` 枚举 + Fallback 兜底（请求能力不存在 → 降级到已有行为）。
3. **所有输入先归一化成事件对象**（Touch/Voice/Feeding → InteractionEvent / GestureEvent），再进决策层。
4. **Personality 是长期参数（0~1），Emotion 是短期状态（0~100），两套独立。**
   11 维 Personality 全保留，但第一阶段只有 Energy / Curiosity / Playfulness / Independence 参与计算；Emotion 第一阶段只有 Happiness / Excitement / Sleepiness。
5. **自主行为是硬需求**：用户不操作时宠物必须会自己 Idle→随机等待→Walk/Turn→Idle，
   概率受 Personality/Emotion/CurrentState 影响，且不能走出场景、不能卡角落、不能高频重复同一动作。

## 工程规范
- 目录：`Assets/{Art,Animations,Prefabs,Scenes,Scripts,UI}`，`Scripts` 按 Core/Pet/Behavior/Interaction/Training/Feeding/Growth/Effects/Save/Network 分。
- 三个场景：`ModelTest` / `PetPrototype` / `PerformanceTest`。
- 配置走 ScriptableObject，**禁止把关键数值散落在 MonoBehaviour 里**。
- 行为 ID、ActionCode、事件 ID 必须稳定；公共系统写接口与注释。
- 每个功能完成 = 真机验收 + 回归测试。
- Debug Panel 必须显示 CurrentState / CurrentBehavior / CurrentAnimation / Emotion / Personality / LastGesture / LastEvent。
- 日志分级打 tag：`[Touch] [Gesture] [Behavior] [Emotion] [Animation]`。
- 性能线：目标设备稳定 60 FPS，低端机最低 30 FPS。

## V1 明确不做
多品种、自制高质量 3D 模型、真实宠物照片生成 3D、视频行为学习、专属大模型训练、
LLM 控制每帧动画、社交/排行榜/公会、大量商城付费、为「AI」增加实时网络依赖、
真实宠物医学模拟。

## 工程实况（2026-10-15 核实）
- Unity 工程在 `unity_3d_pets/unity_3d_pets/`（**双层同名目录**），Unity 6000.6.4f1
- 24 个自写 C# 文件 / 5037 行；`Assets/Scripts/{Core,Pet,Animation,Interaction,Behavior,UI,Diagnostics,Editor}`
- 三个场景 `Assets/Scenes/PetPrototype/`：ModelTest / PetPrototype / PerformanceTest
- 已跑通：触摸→Gesture→Event→BehaviorController→AnimationOrchestrator→Animator；自主行为；4 维性格 + 3 维情绪参与打分
- **未做**：语音、喂养、成长、本地存档、UI 页面
- 唯一允许触碰 Animator 的是 `AnimationOrchestrator`，且必须挂在狗本体 prefab 实例上

## 产品基线（PRD：docs/电子宠物APP_V1_UI与功能需求文档.docx）
- 12 个页面、4 个底部 Tab（宠物/食品/商店/场景）、竖屏
- 场景 home 免费 / garden 100 / park 200 / night 300（金币）；初始金币 500
- 食物：牛肉狗粮 10、狗狗饼干 15、鸡肉干 20、生日蛋糕 50
- 硬约束：**UI 只发命令 + 订阅统一 GameState，不自己维护金币/库存副本**；购买先校验后提交、幂等
- 配色 #4A90E2 / #FFD166 / #F7F9FC

## 微信平台合规门槛（已定：企业主体 + 最终要虚拟支付）
- **虚拟支付 ⇒ 必须游戏类目 ⇒ 只能是「微信小游戏」，不是普通小程序**。类目一旦选游戏不可更改。
- 小游戏与小程序技术栈不同：小程序 = WXML/WXSS/DOM，主包 2MB；**小游戏 = 只有 Canvas，无 DOM**，首包 4MB。
- 资质：企业主体可申请版号；软著约 60 天、工信部备案数工作日~数周、**版号 4–6 个月**（须经有出版资质单位提交）。
  资质线必须与开发并行启动。
- 「免版号」只对纯广告变现（IAA）开放；企业主体纯 IAA 是否免版号各来源不一致 → **须向微信官方/代理确认**。
- **微信同声传译插件是「小程序」插件，小游戏不可用**。小游戏语音 = `wx.getRecorderManager` → 云函数 → 第三方 ASR（计费）。
- 包体：小游戏首包 ≤4MB / 分包合计 ≤30MB；资源上 CDN 需 ICP 备案的 https 域名。

## 2D 议题已结案（2026-10-16）
用户不会手工拆图绑骨 → 2D 的 gate 过不去 → **保持 3D**（当前 Animals_FREE 低模只是"跑通测架构"的载体，
**后续一定会换模型**）。不再讨论 2D。

## ⚠️ 换模型的真实成本（实测，与文档说法有出入）
架构文档称 `PetAnimationMap` 是换模型唯一改动点 —— **只有动画映射这部分兑现**。
另有一批模型硬编码散在 6 个文件里，换模型必撞（详见 `2026-10-16.md`）：
- `Editor/PetSceneBuilder.cs`：prefab 路径、**骨骼名 `scull` / `spine.004` 硬编码**（换模型 → 触摸区域失效）、
  collider 半径 0.22/0.14、demo 组件剥离依赖命名空间前缀 `ithappy.Animals_FREE`
- `Editor/PetAnimationBuilder.cs`：源动画目录、Clip 文件名 `Dog_001_*`、模型 ID、速度 1.2/3.0
- `Pet/PetPersonality.cs` + `PetPersonalityProfile.cs` + `PetBehaviorController.cs`：`CreateBorderCollie()` 被 3 处调用
→ **建议趁现在只有狗一个模型时做「模型假设收敛」到一处 PetModelProfile（未执行）**

## 技术栈已定：Unity（2026-10-15 定）
用户明确「**App 首先需要**」→ 要 iOS/Android 独立 APP ⇒ **Unity 是唯一一次开发出两端的选项**，
Cocos Creator 2D 出局（要废弃已完成的 5037 行 Unity 架构，且换不来 APP 侧收益）。
**2D 路线一并作废**；AI 生成美术仅保留用于 UI 图标 / 场景背景 / 商品图。

## 分发顺序：先微信小游戏，APP 后上
两端共用**同一套 Unity 工程**（Unity 可同时出 APK/Xcode 工程 + 经 WXSDK 转小游戏），开发只做一遍。
- 开发工作量：APP 更少（继续写现有工程）；小游戏净增「首包裁剪 + 资源 CDN + WebGL 兼容」工作量。
- **上线资质：小游戏轻得多**（软著 + 微信备案 + 版号，审核 13–37 工作日）；
  游戏类 APP 还要 **ICP 许可证（40–80 工作日）+ 文网文（20–40 工作日）**，且华为/小米/OPPO/vivo/应用宝
  **各自注册认证一轮**，苹果中国区游戏必须版号 + 企业账号。
- 小游戏首包实测风险：`Skybox_5.png` 单张 **4.61MB** > 首包 4MB 上限，必须外置；
  狗资产（fbx 0.78 + 共享纹理 0.77 + 三动画 0.89）≈ 2.4MB 未压缩，加 wasm 后首包极紧。

## 未决/待确认（阻塞项）
- **iOS 打包需要 Mac**（用户当前是 Windows）→ 需确认是否有 Mac / 云 Mac。
- **同一版号能否覆盖小游戏版与 APP 版两个平台版本？** 须向出版单位确认，不可按「一次搞定」规划。
- 语音方案待定：建议 APP 与小游戏**统一走第三方云 ASR**，共用一份后端。

## 文档状态
`docs/` 多份文档存在**未对齐的契约分歧**（详见 `2026-10-08.md` / `2026-10-15.md`）：
手势 ID 命名体系、触摸分区 Collider、行为优先级表、验收清单颗粒度。
PRD（10-10）是最新基线，冲突时以 PRD 为准，但手势 ID 分歧仍未解决。

## 环境
- 磁盘偏紧，装大包谨慎并清 pip 缓存；系统无 ffmpeg。
- Windows，路径用绝对路径。
- `docs/电子宠物APP_V1_技术方案文档.docx` 有 `~$` 锁文件残留，可能仍被 Word 占用。
