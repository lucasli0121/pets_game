# pets_game 项目长期约定

## 项目定位
电子宠物 APP，V1 = Prototype / 技术可行性验证。
核心命题：**0 元成本证明「宠物大脑 + 宠物身体 + 用户互动」架构可行**。
基线：Unity 6.6 + URP + C#。**V1 验证载体是免费的 Animals FREE - Animated Low Poly 3D Models**（不是 Border Collie）。

## 不可违背的架构原则
0. **美术形态：3D 宠物 + 2D 场景背景 + 2D 道具图标**（2026-10-18 定）。
   不做 3D 场景建模（首包 4MB 装不下）；不做 AI 生成 3D 模型（拓扑/UV/风格不可用）。
   配套四条：宠物下方必须有跟随的假阴影；背景光源方向与场景灯光一致；
   单层背景下须限定宠物地面活动范围（后期再拆远景/家具/地面三层做遮挡）；相机固定（建议正交）。
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
- **未做**：语音、成长页数据接后台、本地存档
- **2026-10-23 更新**：四个底导页 UI 已完成；**商城 + 喂食 + 场景解锁全部接后台**
  （`config/get` 商品与场景 + `sync-state` 积分/背包/场景 + `shop/buy-food` + `pet/feed`
  + `scene/unlock` + `scene/use`），登录用 devOpenid，离线降级到包内 asset 且禁用写操作。
  场景背景图走 `sceneUrl` 下载 + `persistentDataPath/scene_assets` 缓存。
  新用户赠送食品由后台 `config_points.initialItems` 配置（默认 3/2/5/2/1），管理端 A4 可改。
  仍待接：`checkin/claim`、`ad/reward`、`ad/impression`。详见 `NOTES-unity-3d-pets.md`
- 唯一允许触碰 Animator 的是 `AnimationOrchestrator`，且必须挂在狗本体 prefab 实例上

## 产品基线（2026-10-18 修订，PRD 为准）
- **产品名「宠屋养成记」**（微信小游戏账号已注册，2026-10-18）；简称「宠屋」
  软著全称「宠屋养成记游戏软件 V1.0」；版号名「宠屋养成记」→ 四者必须一致，名称备案后不可改
- 12 个页面、4 个底部 Tab（宠物/食品/商店/场景）、竖屏
- **货币统一叫「积分」，不用「金币」**；V1 不接支付
- 积分来源：初始 500、签到 100/天、升级 100/级、看广告 100/次（15 秒）
  **邀请好友 / 转发奖励已删除**（微信把它算利益诱导分享）
- 场景 3 个：home_day 免费 / home_night 100 / park_day 100
- 食物 5 种：food_normal 5（饱腹+50）、food_beef 10（饱腹+100 快乐+10）、
  food_biscuit 15（+5/+5）、food_chicken 20（+5/+10）、food_cake 50（+30/+50）
- 状态：饱腹 / 快乐 / 经验；饱腹 -10/h、快乐 -5/h；阈值 30 / 10
- 等级：每 100 经验升 1 级；作用 = 体型 + 积分奖励 + 后续解锁门槛
- 性格 4 维（活泼/好奇/亲人/独立，0~10）：**首次领养时选择，之后修改扣积分**
- 宠物：**V1.0 只有一只边境牧羊犬（狗）**；猫与多品种放 V1.1。状态各自独立（数据结构已按 pets[] 预留）
- **账号要做**（首次使用注册，微信授权绑定 openid）；**排行榜不做**
- 语音互动（理解名字）列入 V1 目标，但训练系统仍留 V2
- 硬约束：**UI 只发命令 + 订阅统一 GameState，不自己维护积分/库存副本**；购买先校验后提交、幂等
- 配色 #4A90E2 / #FFD166 / #F7F9FC

## 微信平台合规门槛（已定：企业主体 + 最终要虚拟支付）
- **虚拟支付 ⇒ 必须游戏类目 ⇒ 只能是「微信小游戏」，不是普通小程序**。类目一旦选游戏不可更改。
- 小游戏与小程序技术栈不同：小程序 = WXML/WXSS/DOM，主包 2MB；**小游戏 = 只有 Canvas，无 DOM**，首包 4MB。
- 资质：企业主体可申请版号；软著约 60 天、工信部备案数工作日~数周、**版号 4–6 个月**（须经有出版资质单位提交）。
  资质线必须与开发并行启动。
- 「免版号」只对纯广告变现（IAA）开放；企业主体纯 IAA 是否免版号各来源不一致 → **须向微信官方/代理确认**。
- **微信同声传译插件是「小程序」插件，小游戏不可用**。小游戏语音 = `wx.getRecorderManager` → 云函数 → 第三方 ASR（计费）。
- 包体：小游戏首包 ≤4MB / 分包合计 ≤30MB；资源上 CDN 需 ICP 备案的 https 域名。

## 软著（2026-10-18 核实）
- **2026-03-15 新规硬红线**：签章页必须由经办人**手抄「未使用 AI 开发编写代码、撰写文档或生成登记申请材料」**
  + 实名签名 + 身份证号；违规后果 = 列入版权登记失信名单 + 纳入个人/企业征信。功能说明 500–1300 字。
  「内容与软件实际功能脱节」被列为非正常申请，从严审核。
- **电子版权认证 ≠ 软著**：3–5 工作日（最快 1 天）、材料精简，可用于微信小游戏上线；
  但**不能申报版号**（版号只认国家版权保护中心证书）、不能用于维权/高企/补贴。
- 材料门槛：源代码前 30 页 + 后 30 页，每页 ≥50 行，页眉标软件名称与版本号；文档需 10–30 页、每页 ≥30 行。
- 当前工程实测：26 文件 **5824 行 = 116 页**（有效行 4217 = 84 页），无第三方代码依赖，无 Copyright 头。
  若剔除 Editor 工具脚本只剩有效行 2968（59.4 页），卡在 60 页门槛边缘 → **提交必须含 Editor 脚本**。
- 软著名称须与微信小游戏账号主体、版号申报游戏名一致；名称别写死「微信小程序版」，
  否则将来版号要覆盖小游戏 + APP + PC 三端会对不上。

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

## ✅ V1 路线已定：微信小游戏 + 纯 IAA，免版号（2026-10-17）
用户目标排序：**1) 上线快 2) 少花钱**。据此决定性取舍：
- **V1 暂不接虚拟支付、暂不办版号**，只做微信小游戏 + 纯广告变现（IAA）
- 资质只需：企业微信认证 300/年 + 软著（300~2000）+ 小程序备案（免费）+ 自审报告 + 适龄提示 + 隐私协议 ≈ **2,000–4,000 元**
- 周期 ≈ **2–3 个月**（瓶颈在开发，不在资质）
- **放弃**：虚拟支付、Android/iOS APP 上架、Windows 对外发售 —— 全部推迟到版号下来后
  （Android 原生 APP 即使纯 IAA 也强制版号 + ICP 证，故一并放弃）
- Windows 仍可出 exe 做**内部测试/真机验收**，不对外、不收款，不涉资质

**必须同步改的产品设计（否则被判内购）**：金币降级为「游戏内积分」，全站不出现「充值/购买/元/¥」；
金币唯一产出 = 看激励视频（PRD「金币来源待确认」必须补上）；移除任何充值入口（含灰掉按钮）；
**禁做抽奖/开箱/概率付费**；商店只卖金币可买之物。

⚠️ 与用户更早诉求「App 首先需要」冲突：V1 对外版本只有小游戏。已当面指出，需确认接受。
⚠️ 待确认：企业主体纯 IAA 是否确实免版号（主流口径可豁免，个别来源相反）→ 动工前向微信官方确认一次。

## 若将来改走 IAP / 出 APP：资质与分发（备用）
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

## 后端服务（2026-10-18 交付，`pets_server/`）
- 技术栈 **Go 1.27 + net/http 标准库路由 + MongoDB**，无框架无 ORM，34 文件 6473 行
- **硬约束：测试库 192.168.1.63 是 standalone Mongo，不支持多文档事务**。
  故「积分+库存+场景」合并为 `user_assets` 单文档聚合根，扣款把余额条件写进 filter 做原子扣减
- 幂等 `request_dedup(user_id,request_id)` 唯一索引 + 30 天 TTL，业务失败不缓存
- 管理端零依赖 SPA，`go:embed` 进 exe，单文件 16.7MB 部署
- 15 客户端接口 + 17 管理端接口 + 10 管理页面；`go test ./...` 15 用例全绿
- **已定（2026-10-18）**：① 状态值下限 = 1，衰减与加值一律夹 [1,100]，不归零
  （`domain.MinStateValue` / `ClampState`）；② 等级表纯配置、服务端零推导，
  已删除 ×1.5 生成接口与按钮，新增行经验必须手填；③ 单管理员 admin/admin，不做多账号与改密页

## 文档状态
`docs/` 多份文档存在**未对齐的契约分歧**（详见 `2026-10-08.md` / `2026-10-15.md`）：
手势 ID 命名体系、触摸分区 Collider、行为优先级表、验收清单颗粒度。
PRD（10-10）是最新基线，冲突时以 PRD 为准，但手势 ID 分歧仍未解决。

## 环境
- 磁盘偏紧，装大包谨慎并清 pip 缓存；系统无 ffmpeg。
- Windows，路径用绝对路径。
- `docs/电子宠物APP_V1_技术方案文档.docx` 有 `~$` 锁文件残留，可能仍被 Word 占用。
