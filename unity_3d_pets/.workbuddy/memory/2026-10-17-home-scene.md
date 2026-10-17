# 2026-10-17 主场景（PetHome）

## 依据
`C:\dening_project\pets_game\docs\` 下的需求文档与设计展板：
- `电子宠物 APP V1 需求文档.md` 3.2 主场景（首页）：3D 场景显示宠物、触摸互动
- `电子宠物 APP V1 设计展板.html` 第 2 页「2. 宠物主场景（首页）」原型图

展板 HTML 是 JS 动态渲染的（12 个页面，数据在 `{"id":"sN","t":"标题","g":"分类","img":"base64..."}` 里），
要拿原型图得先用正则切块再解 base64：
```python
m = re.search(r'\{"id":\s*"s2"(.*)', d, re.S)
im = re.search(r'"img":\s*"data:image/(\w+);base64,([A-Za-z0-9+/=]+)"', m.group(1))
```

## 展板原型的空间构成（我逐项对照还原）
| 展板元素 | 本实现 |
| --- | --- |
| 木地板 | `Home_WoodFloor` 材质 + `Floor_Wood` |
| 草地地毯（绿） | `Floor_Grass` |
| 蓝色小方垫 | `Mat_Pet` |
| 石板小路 | `Path_Stone_0..4` |
| 木框落地窗 | `Wall_Window`（框 + 半透明玻璃 + 窗台） |
| 窗台绿植 | `Planter_A/B/C`（球体灌木） |
| 沙发 | `Sofa_Base/Back/ArmL/ArmR` |
| 挂画 | `Picture_A` |

## 用户的选择
1. **低多边形体块还原**（不追求写实保真）—— 资产包里只有动物，没有环境资源，
   所以用 Unity 内置几何体 + 程序化材质搭空间构成与配色
2. **只做 3D 场景**（不做 UI）—— 顶部等级条、底部导航、食物/训练入口、气泡提示都留到下一轮

## 踩了两个坑，都靠 MCP 定位

### 坑 1：房间尺寸照抄写实比例，狗变成一个小点
第一版用 9 × 7.5 × 3.2 m（按展板写实室内反推）。截出来狗只有白点大小。
**原因**：展板是写实渲染，狗 0.76m 高在写实房间里看着很大；
换成低模狗 + FOV 45，房间必须缩到 5 × 4.5 × 2.2 m 才有那个视觉比例。

**改法**：用 MCP 直接算狗在画面里占多少像素，不用进 Play 就能调
```csharp
var s1 = cam.WorldToScreenPoint(b.center + Vector3.up * b.extents.y);
var s2 = cam.WorldToScreenPoint(b.center - Vector3.up * b.extents.y);
pct = Mathf.Abs(s1.y - s2.y) / cam.pixelHeight * 100;   // 目标 ~33%
```
改到 32%。

### 坑 2：狗的朝向猜错两次
先按「模型默认朝 +Z」写 → 截图背对；改成不转 → 还是不对。
最后用 MCP 读 `scull`（头骨）骨骼坐标才定论：
Dog_001 默认头在 **+Z**，相机在 -Z，**必须转 180°**。

**教训**：朝向用骨骼位置判定，不要凭感觉。

## 产出
- `Assets/Scripts/Editor/PetHomeSceneBuilder.cs`（新建，~480 行）
- `Assets/Scenes/PetPrototype/PetHome.unity`
- `Assets/Scenes/PetPrototype/Materials/Home_*.mat`（12 个）
- 已挂进 `PetSceneBuilder.BuildAllScenes` 末尾
- 菜单 `Pet V1 / 3. Build Home Scene` 可单独重建

为了让新文件能复用 PetSceneBuilder 的辅助方法，
把 `NewScene` / `CreateController` / `WireOrchestrator` / `SetupRegionColliders` /
`WireBehavior` / `CreateLights` / `SaveScene` / `PlaceDog` / `CreateCamera` /
`CreateWanderWalls` / `PetLayerIndex` / `DogPrefabPath` / `DogHeight` 从 private 改成 internal。

## 编译踩的一个错
新文件缺 `using PetV1.UI;` → CS0246 PetDebugPanel 找不到。
另外 URP 半透明材质不能只设 `_Surface`，还要配 `_SrcBlend` / `_DstBlend` /
`_ZWrite=0` / `renderQueue=Transparent` / 开 `_SURFACE_TYPE_TRANSPARENT` 关键字。

## 待验证（需要进 Play）
- 狗在新场景里走动的活动范围是否合适（±1.15 × ±1.0 m）
- 触摸射线只测 Pet 层后，点击是否准确命中狗
- 相机不跟随，狗走到边缘会不会出画（活动区 ±1.15m，相机距离 2.6m，应该刚好）
