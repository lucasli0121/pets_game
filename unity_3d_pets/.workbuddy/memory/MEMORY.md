# 项目记忆：unity_3d_pets

**宠屋养成记**（微信小游戏「宠屋」）。Unity 6.6(6000.6.4f1)+URP，ithappy `Dog_001` 做架构验证。
工程路径 `unity_3d_pets/unity_3d_pets`。主场景 `Assets/Scenes/PetPrototype/PetHome.unity`，
由 `PetHomeSceneBuilder` 生成（菜单 `Pet V1 / 3. Build Home Scene`，入口 `BuildHomeScene()`）。

**详细版全部在同目录 `NOTES-unity-3d-pets.md`（取景/背景/uGUI 布局/MCP 限制/环境限制的完整数据），
按需查。逐日过程见 `2026-*.md`。**

## 口径（改代码前必读）
微信小游戏 + 纯 IAA（激励视频），不接虚拟支付。场景 = 2D 背景图(1080×1920,≤500KB) + 3D 宠物
+ 正交相机 + 脚下假阴影，**禁止建完整 3D 房间**（首包 4MB 装不下）。
数据服务端权威，文档数值全是示例且后台可配。合规红线：不做抽奖/开箱/概率付费，
文案不得出现「充值/购买/元/¥」。
**用户口径**：① 需求文档与后台接口为主，展板只是参考 ② 一切以后台为准 ③ 四个底导页全做完。
④ **界面布局、图标显示、视觉方案必须先问用户再动手**（用户原话：「要及时问我，
不能自己瞎猜乱搞一气，浪费时间又浪费积分」）。纯逻辑/接口/数据结构可自行决定。
⑤ **相对位置类口径（「X 在 Y 和 Z 的几分之几处」）必须问清三件事：相对哪两个元素、
从哪头数、对齐哪个点**。少一条就返工（积分胶囊位置已返工过一次）。

## 关键结论（最容易忘的三条）
1. **构图基准是狗脚底线不是地板最远端**：`floorFarLine` 只作自检基准，
   `dogGroundLine` 才是相机构图目标。拿踢脚线当目标 = 强迫狗站贴墙处。
2. **横竖屏保护**：不能改画布根 sizeDelta（CanvasScaler 每帧覆写），内容挂进居中的
   `SafeFrame`（恒 1080×1920），`matchWidthOrHeight` 由 `PetPortraitGuard` 按屏幕比例动态切。
   写死 0 时横屏画布高度塌到 608，顶栏/底导/标题全叠字。
3. **uGUI 不为隐藏层级跑布局**；且 `ForceRebuildLayoutImmediate(页面根)` 不够 ——
   `ContentSizeFitter` 与 `VerticalLayoutGroup` 同挂一个物体时重建到那层就停。
   用 `PetPageBase.RelayoutTree` 自顶向下逐个 force。

## 图标（独立美术资源，不是代码生成）
10 张 PNG 在 `Assets/Resources/PetIcons/`（pet/food/shop/scene/gear/heart/star/paw/bowl/close），
128×128 白底透明，源文件合计 3KB。`PetIcons.Get(name)` = `Resources.Load<Sprite>("PetIcons/"+name)`，
`NewIcon` 建装饰性Image。公开 API 与旧 SDF 版一致，调用点无需改。
**三个必守的约束**：
① **图标必须是纯白 RGB + alpha 覆盖度** —— `Image.color` 逐通道相乘，白图标才能染主蓝/红/灰；
彩图染色会变脏。换图时先确认这条。
② **图标必须「alpha 质心在画布中心」** —— bbox 居中 ≠ 视觉居中。狗头/心形下部轻，
质心天然偏上；不校正就是「图标看着没在中间」。切图脚本已按质心定位窗口。
③ PNG 默认按 `Texture` 导入，`Resources.Load<Sprite>` 返回 **null 且不报错**，
症状是图标位置一块洋红方块。新图/换图后跑菜单 **`Pet V1/6. Import Icons`**
（`PetIconImporter`：Sprite/Single/PPU=100/AlphaIsTransparency/关mip/Clamp/Uncompressed）。
改图标的美术资源后要跑一次。

**图标表重出流程**（生图 1 张表 → 本地切10 张，不额外调生图）：
`IconPreview/cut_sheet.py`（源图写死在脚本常量里）→ 切 10 张 + 打印自检。
`IconPreview/pnglib.py` 通用 PNG 读写。`IconPreview/measure_bias.py` 量单张质心偏移。
`IconPreview/preview_sheet.py <图标目录> <输出> dark` 拼对比图肉眼验。
**源图是「16px 棋盘格透明底」（两种灰 lum≈223/250 交替）+ 深灰图形（峰 lum≈92）**，
不是纯色底 —— 按纯色底处理会切成铺满的脏底。正确流程：连通域去噪 →
膨胀取羽化边 → 亮度反推 alpha → **窗口中心取质心**（`side = max(bw+2|dcx|, bh+2|dcy|)+12`）。

**验证生成物必须看文件本身，不能只看预览图**：预览图是派生文件，脚本失败时可能没被覆盖，
会拿上一轮的旧图当「验证通过」（踩过：图标 alpha 全 0 却看着预览正常）。
白图标在白底预览上看不见，`preview_sheet.py` 必须带 `dark` 参数。
**缩放预览图本身也可能有合成 bug**（网格纹理）—— 拿不准就用 1:1 渲染验。

## 静默失效（不报错不生效，靠读代码发现）
- **`NewSolid` 的 `radius` 必须真正参与 sprite 生成**。9-slice 的 border 以 sprite
  自身像素为单位、四角不缩放，所以共用一张 border=32 的图 ⇒ 所有控件圆角都是
  32px（正方形像方角块、胶囊被拉扁）。走 `RoundedSpriteFor(radius)` 按半径缓存生成。
  **圆角半径必须 ≤ 控件短边的一半**，否则上下圆角加起来超过高度，9-slice 中间
  被压成负数、边缘鼓出来。
- **`Fill()` 与定尺寸不能连用**：先 `sizeDelta = w*0.52` 再 `Fill()` 会铺满父物体，
  图标顶到圆边（看起来就是「没居中」）。要居中就用 anchor 中心 + 定尺寸。
- **UI 验收必须量 `rect.width` / `rect.height`，不能只验 `fillAmount` 和文本**。
  踩过：`BuildBarRow` 里行用 `anchorMin=anchorMax=(0,1)` + `sizeDelta=(0,48)`
  ⇒ 行宽恒 0，标题竖排、进度条 Track 宽 0 全不可见，而 fillAmount 数值全对。
- **`NewProgressBar` 返回值 = Fill，`out` 参数 = Track 底槽**。把 out 当填充条用，
  `fillAmount` 设到底槽上不起作用，屏幕上那条 Fill 停在工厂写死的 1f
  → 症状「进度条满格但百分比很小」。三处已修（顶栏经验条 / 状态属性浮层 / BuildStateBar）
- 非序列化私有字段运行期必为 null（`PetHomeUI` 的 `_pages`/`_tabButtons`/`_toast`）
  → `Start()` 检测到空就 `DestroyImmediate` 清子节点后运行期自建。
  **判据不能只看「射线命中」**——按钮会变色、页面却不切换
- `[Serializable]` 普通类字段初始化器反序列化时不跑 → `[NonSerialized] Dictionary` 变空
- `AssetDatabase` 写操作造成 fake null（`== null` 为真但 `??` 不触发）
- `FindAnyObjectByType<T>()` 对 ScriptableObject 永远返回 null
- **PNG 默认按 Texture 导入 → `Resources.Load<Sprite>` 返回 null 且不报错**，
  症状是图标位置一块洋红方块。跑菜单 `Pet V1/6. Import Icons`
- 背面剔除在光栅化阶段，`WorldToViewportPoint` 量不出来
- **量 UI 位置只能用 `GetWorldCorners` 换算出的中心，不能用 `anchoredPosition`** ——
  `anchoredPosition` 的含义随 pivot 变（pivot=(0,1) 时是顶边、(0,0.5) 时是中心），
  拿它当中心会把两个 pivot 不同的元素比错方向（踩过：把「偏下 25px」算成「偏上 28px」，
  还反过来质疑用户描述）。
  且**查找必须限定在同一父节点子树内** —— 全图按名字 `Find` 会命中别的页面的同名
  `Track`/`Value`，量到完全无关的数据。
- **写格子定位前先确认 pivot 语义**：`anchorMin=anchorMax=(0,0)` + `pivot=(0.5,0)`
  时 `anchoredPosition` 是**中心**（写 `slot*i` 而非 `slot*(i+0.5)` ⇒ 整排 Hit
  左移半格，症状「点 A 选中 B」）；`pivot.x=0` 才是左边缘。
- **均分宽度不要用硬编码常量**：底导 slot 写死 270×4=1080，但 SafeFrame 恒 1080 宽
  而画布按屏幕缩放，导航条实测只有 1018 ⇒ 第 4 格溢出屏幕点不到。
  要 `brt.rect.width / n`（构建时布局可能没跑，需 `slot <= 1f` 兜底）。
- **改代码后必须重进 Play 才能验**：编辑器里跑的是重载前的旧 UI 实例。
  **改了 UI 层级结构还要先重建场景**（菜单 `Pet V1/3. Build Home Scene`）——
  `_pages` 非空时 `Start()` 不自建，拿到的是场景文件里的旧层级

## MCP 编译限制（老坑复发）
- `Unity.AI.Image` 与 `UnityEngine.UI.Image` 冲突 → 一律写全限定名
- `string.Join(",", arr)` 与超过 3 个 `{n}` 的 `result.Log` 都报 CS1503
  （`IFormatProvider` 重载）→ 用 `StringBuilder` 手工拼串

## 环境限制（别重复踩）
- **Play 会因 Game 窗口未聚焦被挂起**：`isPlaying=True` 但 `Time.frameCount` 不动。
  验证业务逻辑用 `Button.onClick.Invoke()` + 读状态字段；视觉确认必须用户聚焦后手动进 Play
- 挂起时 Canvas 从未渲染，`GraphicRaycaster` 返回 0 命中是**假信号**
- 离线渲连续帧走不通（多张 PNG MD5 相同），单帧静态截图可用
- `activeInputHandler=1`，只能用新 Input System。Editor 命名空间是 `PetV1.EditorTools`
- **改完场景参数必须先退出 Play 再重建**（`NewScene` 在 Play 下抛）

## 后台接口（`pets_server`，均在 `/api/v1/` 下）
`config/get`、`pet/feed`、`pet/interaction`、`pet/switch`、`pet/adopt`、`pet/personality`、
`shop/buy`、`scene/unlock`、`scene/use`、`sync`、`login`、`checkin/claim`、
`ad/reward`、`ad/impression`、`event/report`

## 待接
`shop/buy` 与 `pet/feed`（现在客户端本地扣库存是临时口径）；背景图 `sceneUrl` 下载器；换宠物模型。