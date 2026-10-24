# 项目记忆：unity_3d_pets

**宠屋养成记**（微信小游戏「宠屋」）。Unity 6.6(6000.6.4f1)+URP，
ithappy `Dog_001` 做架构验证。工程在 `unity_3d_pets/unity_3d_pets`。
详细历史见同目录 `2026-*.md` 日志。

## 口径
微信小游戏 + 纯 IAA（激励视频），不接虚拟支付。场景 = 2D 背景图(1080×1920,≤500KB)
+ 3D宠物 + 正交相机 + 脚下假阴影，**禁止建完整 3D 房间**（首包 4MB 装不下）。
数据服务端权威，文档数值全是示例且后台可配。`happiness/satiety/exp/points` 归服务端，
`excitability/sleepiness` 是客户端临时量（不入库，系数仍从配置注入）。
「点击 5 次 +1 经验」是计数型 `expPerCount`。合规红线：不做抽奖/开箱/概率付费，
文案不得出现「充值/购买/元/¥」。V1 砍掉语音、排泄动画、邀请奖励、训练面板。
**用户口径**：① 需求文档与后台接口为主，展板只是参考 ② 一切以后台为准 ③ 四个底导页全做完。

## 取景（后台已下沉）
`SceneEntry.layout` 每张图一套（`defaultLayout` 仅新场景模板），运行时覆盖入口
`ApplyRemoteConfig(jsonArray, out addedCount)`。**字段名必须与 Go 侧 `domain.SceneLayout`
逐字一致**，否则静默解析成 0 再被 `FillDefaults` 补默认值，看起来「配置没生效」。
实测 `home_day` floorFarLine 0.433 / dogGroundLine 0.30 / ortho 1.9；`home_night` 0.435/0.30/1.9；
`park_day` **0.549/0.32/2.2**（户外无墙做纵深参照，视野要更大）。

`ApplyScene` 重摆清单（漏一项症状不同且查不出坐标）：①orthoSize+俯角+位置
②**背景 quad 的 localScale**（1×1 面片挂相机下，尺寸=orthoSize×2与其9/16，漏了背景不铺满）
③主光 ④环境光 ⑤假阴影透明度 ⑥活动区。配置唯一调用点是 `PetSceneConfigBootstrap`
（`DefaultExecutionOrder(-100)`），注入用公开 `Configure` 不用 `SetPrivateField`，
`OnDestroy` 必须解绑 `ConfigApplied`。

背景：正交+俯角≠平行投影（纵向偏移=距离×tan俯角）；`SetParent(cam.transform)` +
`localPosition=(0,0,8)`，`localRotation` 保持 identity。
**不能转 180° 修背面剔除**（整张左右镜像），要改网格：法线改 `Vector3.forward`、
绕序 `0,2,1/0,3,2`，只翻绕序不改法线等于没改。
背景 aspect **写死 9/16**；验位置量四角 viewport 不量中心点；量 bounds 排除假阴影。
**构图基准是狗脚底线不是地板最远端**（拿踢脚线当目标 = 强迫狗站贴墙处），基准点统一
`(0, bounds.min.y, 0)`。量地面线找踢脚线暗缝（亮度局部极小值），**不是最大跳变**
（会选中阳光斑 → 狗悬空）；`scene_home_day` 实测 0.428。
`GetPixels32` 自下而上，图像 y = 1920 - 数组行号。头骨在 +Z、相机在 -Z，要转 180°。

**假阴影硬条件：必须露在狗的投影轮廓之外**。12° 俯角下地面 quad 纵向被压到 0.21 倍，
长度系数无效，**只有横向 X 有效** → `WidthFactor=1.85`、`LengthFactor=1.5`，
`softness≤0.6`。历史故障：椭圆严丝合缝藏在躯干下（屏幕零像素而 alpha/Cull 全正常），
已在 `VerifyComposition` ⑤ 自动校验。

**走路颠簸是对角步态不是 bug**：骨盆 Y 恒定 0.504m 全程不起伏，摆动的是四肢
（前后蹄高差往复 15cm）。躯干俯仰补偿原理上无效，`PetGaitStabilizer` 已摘挂载。
源 idle 动画 9.63s 不是呼吸，已按 `IdleTrimSeconds=2.0f` 裁到前 2 秒（俯仰 31°→0.4°），
裁剪用 `GetCurveBindings` 逐曲线截断（曲线是四元数，按 eulerAngles 过滤得 0 条）。

## uGUI 原型（PetV1.UI，四底导页）
纯代码构建，无 UI Toolkit/TMP。状态只画饱腹与快乐（后台 `decay.go` 只结算这两项）；
食品用后台 `config_food` 五条。

**横竖屏保护（PetPortraitGuard）**：不能改画布根 sizeDelta（CanvasScaler 每帧覆写），
内容挂进居中的 `SafeFrame`（恒 1080×1920）。`matchWidthOrHeight` 动态切：
比 9:16 宽（含横屏）按高、比 9:16 高（20:9）按宽。**写死 0 时横屏画布高度塌到 608，
顶栏+底导+标题全挤一起叠字**。UI 框必须与 `cam.rect` 像素级重合（Guard 只报警不改相机，
rect 归 `PetPortraitViewport` 管）。`SafeFrame` 是纯空节点不带 Graphic（带 Image 就是拦截板）。

**布局坑**：
- uGUI **不为隐藏层级跑布局**，隐藏状态重建的行高停在 0（自检读到 -20）。
  修法挂在 `SwitchTab` 里对即将显示的那一页重排
- ❌ `ForceRebuildLayoutImmediate(页面根)` **不够**：`ContentSizeFitter` 与
  `VerticalLayoutGroup` 同挂一个物体（`Scroll/Inner`）时，从祖先发起的重建到这层就停
  （实测页面根 force 完 5 行全 -20，直接 force Inner 才 140）。
  修法 `PetPageBase.RelayoutTree` 自顶向下逐个 force（父先子后）
- ❌ 不要在 `Start`/`LateUpdate` 全页暖机：CanvasScaler 在 Start 之后才定尺寸，
  那时 Canvas rect 是 0×0，排出来必然 0，且被「后面不再重排」掩盖成「修复无效」
- `NewSolid(radius:0)` 默认 `raycastTarget=false`，接点击的透明板必须用 `NewHitArea`
- 同层兄弟节点先命中：底板/图标/文字压在 Hit 上会吃掉点击 → 显式关 raycastTarget。
  Backdrop 同理（会吃掉宠物页 3D 触摸）
- 滚动视图 Viewport 会吃掉 3D 触摸（Mask 需可射线才能拖），宠物页/成长页用 `MakeStaticList`
- 场景必须有 EventSystem（用 `InputSystemUIInputModule`），缺则 UI 全死且零报错
- `MakeTabRow` 必须锚顶部通栏（anchorMin/Max=(0,1)/(1,1) + pivot=(0.5,1) + offsetMin/offsetMax），
  只写 `sizeDelta=(0,TabHeight)` 而 anchor 用默认 (0.5,0.5) ⇒ 整行宽 0，Tab 全 0 宽
- `HorizontalLayoutGroup` 子项是 `Image` 的必须补 `LayoutElement`（Image 不实现 ILayoutElement，
  布局组算不出尺寸）；`Text` 自带 ILayoutElement，不用补

**离线截图（Play 挂起时唯一可用）**：把 Canvas 切 `ScreenSpaceCamera` + 临时正交相机
（`orthographicSize=960`、`aspect=1080/1920`、位置 (0,0,-100)、`planeDistance=100`）渲到
RenderTexture → `ReadPixels` → `EncodeToPNG`。同步调用，不依赖帧推进。
**画布根在世界坐标里不以原点为中心**（实测 y∈[0,1920]），但切 ScreenSpaceCamera 后
CanvasScaler 会重排成居中（SafeFrame 落在 y∈[-960,960]），所以直接按 1080×1920 取景即可。

## 静默失效（不报错不生效）
1. **fake null**：AssetDatabase 写操作会销毁重建 ScriptableObject，`== null` 为真但 `??`
   不触发 → 所有 AssetDatabase 写集中在构建开头
2. 字段默认值只在首次 `CreateInstance` 生效 → 每次构建都要显式赋值
3. `FindAnyObjectByType<T>()` 对 ScriptableObject 永远返回 null
4. **背面剔除在光栅化阶段，`WorldToViewportPoint` 量不出来**
5. 写引用只有 `SetPrivateField` 可靠；且传装箱枚举静默失效（`value is int` 恒 false）
   → `value is Enum en ? Convert.ToInt32(en) : ...`
6. 类名必须与文件名一致，否则 `CreateInstance<T>()` 返回 null
7. **非序列化私有字段运行期必为 null**（`PetHomeUI` 的 `_pages`/`_tabButtons`/`_toast`/
   页面实例全中招）。症状：射线命中、按钮变色，但页面不切换、Refresh 空转。
   **判据不能只看「射线命中」**。修法：`Start()` 检测到空就 `DestroyImmediate` 清子节点后
   运行期自建（不能用 `Destroy`：帧末生效期间 `transform.Find` 会命中旧死树）
8. `[Serializable]` 普通类字段初始化器反序列化时不跑 → `[NonSerialized] Dictionary`
   变空字典（症状「其他字段全对，唯独背包全空」）。读写入口兜底 `EnsureInventory()`
9. **ScrollRect 的 Viewport 当 Mask 用的那张 Image，alpha 不能写 0.001「几乎透明」**。
   alpha 转字节为 0 → CanvasRenderer 整条绘制被跳过 → 模板缓冲一个字都没写 →
   所有子项过不了 NotEqual 测试 → **滚动区一张卡片都不显示**，而 rect / 文本 / sprite /
   fillAmount 全部量得到，Console 零报错。正确写法 `color = Color.white` + `showMaskGraphic = false`
   （只写模板不上屏，不靠 alpha 藏）。判据：截图 PNG 字节数 26933 → 74766。
10. **`Object.Destroy` 帧末才生效**：同一帧内先 Show(A) 再 Show(B)，A 建的子节点还在。
   会重复建的列表一律用「行池 + SetActive」，不要每次新建销毁。
11. uGUI 富文本**不支持 `<align>`**（标签原样打出）。单/双引号、`<color>`、`<size>` 都正常。
   要「左名称右数值」就建两个 Text 各 Fill 整行、靠 `TextAnchor.MiddleLeft/Right` 分工。
12. 「空列表提示」只在空分支 `SetActive(true)` 会粘滞：启动时数据未到先跑一轮空判，
    之后数据到了也没有第二次空分支来关。非空分支必须显式 `SetActive(false)`。
13. **自动属性的 backing field 不被 Unity 序列化**。`public T X { get; set; }` 的
    `<X>k__BackingField` 是编译器生成的私有字段，构建期写的值进 Play 反序列化后是 null。
    踩过：`PetUIRunner.Home` 让 Toast 的每帧驱动整条 null 短路，提示框弹得出、到死不消失，
    切页面点哪儿都没用，Console 零报错。跨对象引用的配置字段一律写成
    `[SerializeField] private T x;` + 属性包装，并且在消费方 `Start()` 里自愈绑定。

## Unity MCP
用 `Unity_RunCommand`/`Unity_GetConsoleLogs`，不要读 Editor.log。
约束：类名 `internal class CommandScript` + `IRunCommand`；**禁反射**（含 `BindingFlags`）；
**禁 `GetInstanceID()`**（Unity 6 CS0619，用 `GetEntityId()`）；`result.Log` 不支持 `{0:F2}`；
`int e0 = s.exp` 报 CS0266（exp 是 long）；`GetWorldCorners()` 必须传数组。
其他：`Graphic` 无 `maskable`；`Canvas`/`RectTransform` **没有** `onRectTransformDimensionsChange`
（Unity 6 移除）；`RectTransform.rect` 无 `sizeDelta`；`WorldToScreenPoint` 第一参是 Camera
（Overlay 传 null）；`foreach (Transform b in ...GetComponentsInChildren<Button>())` → CS0030；
`Find` 返回 `Transform` 调 `.rect` 要先 cast；`Mathf` 无 `LN2`。
改完 C# 要 `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()`。
Edit 模式 Update 不跑，取景问题必须进 Play 量。

看相机画面：`cam.Render()` 渲到 RenderTexture + `ReadPixels` **有效**，但要先临时关
`PetPortraitViewport` 并设 `cam.rect=(0,0,1,1)`、`cam.aspect=1080/1920`，
否则渲出全屏黑底中间一条竖带。**连续帧不行**：无论怎么改 `animator.Play/Update`
（骨骼确实变了），多张 PNG MD5 完全相同。

## 环境限制
- **Play 会因 Game 窗口未聚焦被挂起**：`isPlaying=True` 但 `Time.frameCount` 停在 1~2。
  必须用户侧聚焦窗口后手动进 Play
- **挂起时 Canvas 从未渲染**（200 个 Graphic 里 148 个 `depth==-1`），
  `GraphicRaycaster.Raycast` 返回 0 命中、`Camera Capture` 拍不到 Overlay Canvas ——
  **这两种「点不到」是假信号**。验证业务用 `Button.onClick.Invoke()` + 读状态字段
- `ScreenCapture.CaptureScreenshot` 在挂起时不出文件

## 其他约定
`activeInputHandler=1`，只能用新 Input System。`EnhancedTouchSupport.Enable()` 会把鼠标
当触摸 → 一次点击上报两次中间 Canceled，表现为「点击没反应」，
`ProcessMouse` 开头 `if (Touchscreen.current != null) return;`。
源 Clip `loopTime=0`，要复制后用 `SetAnimationClipSettings` 打开。
Dog_001 是 Generic Rig（`applyRootMotion=false`），头骨叫 **`scull`**。
prefab 自带 demo 组件必须剥离（`MovePlayerInput`/`CreatureMover`/`CharacterController`），
硬顺序：先全部 `enabled=false` 再按序显式销毁。
编辑器层级辅助用 `SetParent(parent,false)` + `localPosition`（用 `position` 会全错）。
换模型只改 `DogModelProfile.asset`。`GetRandomWanderPoint` 的 margin 不能写死 0.5
（活动区半宽 0.55 时减完只剩 0.05 → stuck 死循环刷屏），现按短边 25% clamp[0.05,0.5]。
行为时序：`maxDuration=0` 是随机时长，判结束看 `PlannedDuration`；相同 behavior 要重掷时长。
**改完场景参数必须先退出 Play 再重建**（`NewScene` 在 Play 下抛）。
Editor 命名空间是 `PetV1.EditorTools`。

## 场景与架构
主场景 `Assets/Scenes/PetPrototype/PetHome.unity`，由 `PetHomeSceneBuilder` 生成
（菜单 `Pet V1 / 3. Build Home Scene`，入口 `BuildHomeScene()`）。
当前参数 orthoSize 1.9、俯角 12°、cameraDistance 3.2、活动区 (0.95,0.70) 偏移 (-0.30,-0.10)。
`PetV1.Runtime`/`PetV1.Editor` 两个 asmdef；`PetBehaviorController` 唯一决策入口；
全工程只有 `AnimationOrchestrator` 能碰 Animator。

## 后台接口（`pets_server`，均在 `/api/v1/` 下）
**实测核对过的路由（2026-10-23，直接读 `internal/api/router.go` + curl 打真实响应）**：
`login`、`config/get`、`sync-state`、`pet/feed`、`pet/interaction`、`pet/switch`、
`pet/adopt`、`pet/personality`、**`shop/buy-food`**、`scene/unlock`、`scene/use`、
`checkin/claim`、`ad/reward`、`ad/impression`、`event/report`

⚠️ **之前记的 `shop/buy` 和 `sync` 是错的**，实际是 `shop/buy-food`、`sync-state`。
写请求体一律有 `requestId`（幂等键），且后台 `decode` 开了 `DisallowUnknownFields`
—— 请求体多一个字段就 400，字段名必须逐字对齐。

响应统一 `{code, msg, data}`，code=0 才算成功。鉴权支持
`Authorization: Bearer` / `X-Token` / `?token=` 三种。

**初始积分不是 login 发的，是首次 `pet/adopt` 时发的**（`config_points.initial`，默认 500）。
新用户：`login`（points=0、inventory={}、pets=[]）→ `pet/adopt` → `sync-state` 才见 500。
**后台不白送初始食品**，接上之后背包就是空的。

## 客户端数据层（2026-10-23 接入）
- `Data/PetJson.cs` 手写 JSON 解析器。**不能用 `JsonUtility`**：它解不了三层嵌套
  （`data.config.food`），也解不了 `inventory` 这种动态键名的 map。
  资源键 `icon` → 客户端字段 `iconKey`（唯一不一致的一对）。
- `Data/PetApiClient.cs` 网络层，每接口一个 `*Routine` 协程，`PetApiResult` 汇报结果
  （不用异常：网络失败在小游戏是常态，必须能落到离线分支）。
- `Data/PetSession.cs` 唯一数据源。启动链 `login → adopt(无宠物时) → config/get → sync-state`，
  写操作 `BuyFood` / `Feed` 成功后用服务端返回值覆盖本地。
- 入口：`PetHomeUI.Start()` 末尾 `BindSession()`（**必须在 Build 之后**，否则拿到的是
  `Catalog` getter 兜底出来的临时 `Fallback()` 实例，服务端数据写进去没人读）。
- 离线：任一请求失败都不阻断启动，`Online=false`，商城按钮显示「未联网」并禁用，
  商品用包内 asset 兜底。**绝不能在离线时假装买成功。**
- `PetHomeUI.EnsureStateCollections()` 的本地兜底（把空背包填成 3/2/5/2/1）
  必须由 `PetSession.RemoteAuthoritative` 关掉，否则服务端的空背包永远被盖住。

## 待接
`checkin/claim`、`ad/reward`、`ad/impression` 的 UI 入口；
背景图 `sceneUrl` 已接（`PetSceneAssetCache` 下载 + `persistentDataPath/scene_assets` 缓存）；
换宠物模型。离线恢复后没有 UI 重连入口，只有 `PetSession.Retry()`。

## 场景资源下发链路（2026-10-23 打通）
后台 `ASSET_BASE_URL` 为空时 `assetURL()` 返回**相对路径**（不是空串，跟注释不一致），
所以客户端判断「能不能下载」必须看 `http://` / `https://` 前缀，不能看非空。
`config/get` 一次请求同时带 food 与 scene，由 `PetSession` 分发：
food 自己用，原始 JSON 通过 `PetApiResult.RawText` 喂给 `PetSceneConfigCache.TryApplyResponse`。
`PetSceneConfigCache` **没有登录态**，无 token 时静默不发请求（发了必然 401，
日志会把人引到「后台挂了」的错误方向）。`ConfigApplied` 事件在 `TryApplyResponse` 内部抛，
因为它有两个入口（自己拉 / 外部喂），放调用方会漏掉后者。
本地联调起后台：`ASSET_BASE_URL=http://127.0.0.1:8080/assets ASSET_DIR=assets ./bin/pets_server.exe`。