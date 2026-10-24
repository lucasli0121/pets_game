using System.Collections.Generic;
using PetV1.Animation;
using PetV1.Behavior;
using PetV1.Data;
using PetV1.Diagnostics;
using PetV1.Interaction;
using PetV1.Model;
using PetV1.Pet;
using PetV1.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 主场景（首页）构建器。
    ///
    /// 依据《电子宠物 APP V1 设计展板》第 2 页「宠物主场景（首页）」原型。
    ///
    /// **场景形态（PRD 4.5，2026-10-18 定）**：
    /// 一张竖屏 2D 背景图 + 3D 宠物，不再搭 3D 房间。
    ///
    /// 改这个的原因很实际：小游戏首包上限 4MB。完整 3D 房间
    /// （墙体、地板、家具、光照贴图）单场景就超过 1.5MB，三个场景不可行。
    /// 背景图单张可压到 200–500KB —— 实测三张合计 880KB：
    ///   scene_home_day.jpg    1080×1920  178 KB
    ///   scene_home_night.jpg  1080×1920  282 KB
    ///   scene_park_day.jpg    1080×1920  420 KB
    ///
    /// 因此本构建器**不再创建**：Floor_Wood / Floor_Grass / Wall_Window /
    /// Sofa_* / Planter_* / Picture_A / Path_Stone / Mat_Pet ——
    /// 这些全部是背景图里的内容，建 3D 版只会既超包又与背景重复。
    ///
    /// 现在创建的：
    /// - Background quad（Unlit，贴场景图）
    /// - 正交相机（固定，orthoSize 从 PetSceneLibrary 读）
    /// - 主光 + 环境光（方向与背景图光源一致：左上）
    /// - 狗 + PetController + 触摸分区 Collider
    /// - PetFakeShadow（椭圆半透明假阴影，PRD 硬性要求）
    /// - PetSceneStage（运行时切场景）
    /// - TouchInputManager / PetDebugPanel / PetSceneInfo
    ///
    /// 本阶段**不做 UI**（顶部等级条、底部导航、食物/训练入口），
    /// 见需求文档 3.2。调试仍用 PetDebugPanel。
    /// </summary>
    public static class PetHomeSceneBuilder
    {
        public const string HomeScenePath = PetSceneBuilder.ScenesDir + "/PetHome.unity";
        public const string SceneLibraryPath = PetSceneBuilder.ScenesDir + "/PetSceneLibrary.asset";
        public const string BackgroundDir = PetSceneBuilder.ScenesDir + "/Backgrounds";

        /// <summary>
        /// 背景图在**世界坐标**里距相机多远（米）。
        /// 正交相机没有透视缩放，背景放多远都不影响大小 ——
        /// 只影响它与宠物的深度排序。放远处保证宠物一定在前面。
        /// </summary>
        /// <summary>
        /// 背景 quad 挂在相机下的距离（米）。
        ///
        /// 背景是相机的子物体，局部坐标 (0,0,d)，所以它**永远正对取景平面**，
        /// 画面位置不随 d 变化 —— d 只决定深度排序。
        ///
        /// 唯一的约束是 d 要大于狗到相机的距离（cameraDistance = 3.2m），
        /// 否则深度缓冲里狗会盖在背景前面。取 8m 有充足余量。
        ///
        /// 之前这里是 40f 且**不挂在相机下**，那时 d 才会影响画面位置：
        /// 俯角 12°下纵向偏移 = d × tan(12°)，40m 时是 8.5m，
        /// 远超可视高 3.8m，背景整个掉到画面外，Game 视图里只剩纯色 clearFlags。
        /// </summary>
        private const float BackgroundDistance = 8f;

        /// <summary>地面在世界的 Y 坐标。宠物、假阴影、相机反推都以它为基准。</summary>
        private const float GroundWorldY = 0f;

        [MenuItem("Pet V1/3. Build Home Scene", false, 3)]
        public static void BuildHomeScene()
        {
            if (!AssetExists<PetAnimationMap>(PetAnimationBuilder.AnimationMapPath))
            {
                Debug.LogError("[PetV1] 未找到 AnimationMap，请先执行 Pet V1 / 1. Build Animation Assets");
                return;
            }

            var dogPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PetSceneBuilder.DogPrefabPath);
            if (dogPrefab == null)
            {
                Debug.LogError($"[PetV1] 未找到 Dog Prefab：{PetSceneBuilder.DogPrefabPath}");
                return;
            }

            var modelProfile = PetAnimationBuilder.LoadModelProfile();

            // ---- 所有 AssetDatabase 写操作集中在这里 ----
            // 之后的代码只碰 GameObject，不再写资产。
            // 原因：AssetDatabase 写操作会触发资产重导入，
            // Unity 在过程中销毁并重建 ScriptableObject 的 native 实例，
            // 于是 C# 引用还在但 Unity 重载的 == null 为真，?? 兜不住。
            EnsureFolders();
            var library = BuildSceneLibrary();
            BuildBackgroundAssets();

            // 主场景用第一个场景的构图数据点。
            // 构图参数是**每张背景图各不相同的实测值**（见 SceneLayout 注释），
            // 所以建场时取scenes[0] 的 layout，切场景时由 PetSceneStage 重摆。
            var mainEntry = library.scenes.Count > 0 ? library.scenes[0] : null;
            if (mainEntry == null)
            {
                Debug.LogError("[PetV1] 场景库里一个场景都没有，无法建场。");
                return;
            }
            var mainLayout = mainEntry.ResolveLayout();

            // ---- 建场景 ----
            var scene = NewSceneInternal();

            var map = PetAnimationBuilder.LoadAnimationMap();
            var pet = PetSceneBuilder.PlaceDog(dogPrefab, out Transform rigRoot);

            // 活动区跟着场景的构图数据点走。
            // scene_home_day 里沙发与边桌占画面右侧，所以 layout.wanderCenterX 是负的
            // （PRD 4.5 要求「不进入家具位置」）；户外的 park_day 是 0。
            pet.SetWanderArea(mainLayout.WanderHalf, mainLayout.WanderCenter);

            // 狗站在画面下方偏前的位置。
            // 依据：背景图是俯视构图，地面占下方约 55%，
            // 狗 0.76m 高放在这个位置约占画面 16%（正交下更可控）。
            pet.transform.position = new Vector3(0f, GroundWorldY, 0f);

            // 实测（MCP 读 scull 骨骼）：模型默认头朝 +Z，
            // 而背景图是「从门口看进去」的视角，狗要正面朝镜头（-Z）。
            pet.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var controller = PetSceneBuilder.CreateController(pet.gameObject);
            PetSceneBuilder.WireOrchestrator(controller, pet.gameObject, map, "PetHome");

            // ---- 触摸输入 ----
            var touchGo = new GameObject("TouchInputManager");
            var touch = touchGo.AddComponent<TouchInputManager>();

            // ---- 相机（正交，固定）----
            // 先量狗脚的实际世界 Y：模型 bounds.min.y 往往是 -0.01 这种小负值
            // （蒙皮网格原点不在脚底），拿它当地面线基准才准。
            float petFootY = MeasureFootY(pet);
            var cam = CreateOrthographicCamera(mainLayout, petFootY);

            // 构图迭代校正：正交俯视下「地面线」与 camY 的关系式推导容易出错，
            // 改成实测 —— 移动相机量到地面线对齐为止。
            // 最多迭代 4 次，每次按实测误差的 90% 修正，足够收敛。
            int iter = AlignGroundLine(cam, petFootY, mainLayout);
            if (iter < 0)
            {
                Debug.LogError("[PetV1] 地面线对齐失败，已放弃迭代（相机位置可能不准）。");
            }

            // ---- 背景 quad ----
            // 必须传 cam：背景要挂成相机的子物体才能对齐取景平面
            var bgRenderer = CreateBackgroundQuad(mainLayout, cam, mainEntry);

            // ---- 灯光 ----
            var keyLight = CreateKeyLight(mainLayout);

            PetSceneBuilder.SetPrivateField(touch, "targetCamera", cam);
            int petLayer = PetSceneBuilder.PetLayerIndex();
            if (petLayer >= 0)
                PetSceneBuilder.SetPrivateField(touch, "petLayers", 1 << petLayer);

// ---- 身体区域 Collider（Pet 层）----
            // 骨骼名 / 体型 / 活动区全部从 PetModelProfile 读，换模型只改那个 asset。
            PetSceneBuilder.SetupRegionColliders(pet, rigRoot, modelProfile);
        // 注：曾在此挂PetGaitStabilizer 做躯干俯仰补偿，实测证明无效，已摘除。
        // 实测（Dog_Walk 一个周期，60fps 采样）：
        //   骨盆 spine.004 的 Y 恒定 0.504m，全程不起伏；
        //   躯干 spine.001→spine.003 的 pitch 只摆 6.5°（约 25px）；
        //   前后蹄高差在 +0.115 ~ -0.037m 之间往复（15cm），来自四肢反相摆动。
        // 也就是说「前腿高/屁股高」是对角步态（trot）本身，不是躯干俯仰问题，
        // 补偿躯干俯仰改不了它。详见 Assets/Scripts/Pet/PetGaitStabilizer.cs 顶部注释。

            // ---- 椭圆假阴影（PRD 4.5 硬性要求）----
            var shadow = CreateFakeShadow(pet.transform, modelProfile, mainLayout, petFootY);

            // ---- 场景控制（切场景 / 改光）----
            var stageGo = new GameObject("PetSceneStage");
            var stage = stageGo.AddComponent<PetSceneStage>();
            // stage 有专门的 Configure 方法，优先用它；
            // 剩下的引用走 SetPrivateField（Unity 不知道对象变脏时 SaveScene 会丢改动）
            stage.Configure(library, pet.transform, keyLight);
            // 把相机与狗脚高度也交给它：切场景时要按新场景的 layout 重摆取景
            stage.ConfigureCamera(cam, petFootY);
            PetSceneBuilder.SetPrivateField(stage, "backgroundRenderer", bgRenderer);
            PetSceneBuilder.SetPrivateField(stage, "currentSceneId", mainEntry.sceneId);

            // ---- 行为控制器 ----
            var behavior = pet.gameObject.AddComponent<PetBehaviorController>();
            PetSceneBuilder.WireBehavior(behavior, pet, controller, touch);
            PetSceneBuilder.SetPrivateField(behavior, "rewardConfig",
                Resources.Load<InteractionRewardConfig>("PetV1/DefaultInteractionReward"));

            // ---- 场景配置引导（读缓存 + 后台覆盖）----
            // 少了这一步，后台下发的构图参数在运行期没有任何应用入口：
            // PetSceneConfigCache 写完了但全工程没人调用，画面永远用构建期那套数值。
            // 必须在 SaveScene 之前挂上并写好引用，否则运行时找不到就静默跳过。
            var configGo = new GameObject("PetSceneConfig");
            var configCache = configGo.AddComponent<PetSceneConfigCache>();
            // 背景图下载与本地缓存。后台配了 ASSET_BASE_URL 时才真正下载，
            // 没配（下发相对路径）就跳过、只用包内图。
            configGo.AddComponent<PetSceneAssetCache>();
            var configBootstrap = configGo.AddComponent<PetSceneConfigBootstrap>();
            configBootstrap.Configure(library, stage, configCache);

            // ---- UI（顶栏 / 底导 / 四页）----
            var ui = BuildHomeUI(library, stage);

            // ---- 调试面板 ----
            var panelGo = new GameObject("PetDebugPanel");
            var panel = panelGo.AddComponent<PetDebugPanel>();
            PetSceneBuilder.SetPrivateField(panel, "touchInputManager", touch);
            PetSceneBuilder.SetPrivateField(panel, "behaviorController", behavior);
            // IMGUI 面板的绘制在 Overlay Canvas 之后，会整块盖住 UI。
            // 默认关掉，需要时按 ` 键（面板自带开关）。
            PetSceneBuilder.SetPrivateField(panel, "visible", false);

            var infoGo = new GameObject("SceneInfo");
            var info = infoGo.AddComponent<PetSceneInfo>();
            info.SetText(
                $"场景：{library.scenes.Count} 个背景（2D 背景 + 3D 宠物）\n" +
                $"正交相机 orthoSize={mainLayout.orthographicSize:F2}" +
                $"（可视高 {mainLayout.orthographicSize * 2f:F2} m），俯角 {mainLayout.cameraPitch:F0}°\n" +
                $"模型 {modelProfile.bodyWidth:F2}×{modelProfile.bodyHeight:F2}×{modelProfile.bodyLength:F2} m\n" +
                $"活动区 ±{mainLayout.wanderHalfX:F2}×±{mainLayout.wanderHalfZ:F2} m" +
                $"（中心偏移 {mainLayout.wanderCenterX:F2}, {mainLayout.wanderCenterZ:F2}）\n" +
                $"背景：{BackgroundDir}/（1080×1920）\n" +
                $"UI：顶栏（等级/经验/积分）+ 底导四页（宠物/食品/商店/场景），下方面板仅调试用");

            // ---- 自检 ----
            VerifyComposition(pet, shadow, bgRenderer, cam, mainLayout);

            PetSceneBuilder.SaveScene(scene, HomeScenePath);

            Debug.Log("[PetV1] 主场景构建完成（2D 背景 + 3D 宠物）：\n" +
                      $"  {HomeScenePath}\n" +
                      $"  背景库 {SceneLibraryPath}，共 {library.scenes.Count} 个场景\n" +
                      $"  当前场景 {mainEntry.sceneId}（{mainEntry.displayName}）\n" +
                      $"  正交相机 orthoSize={mainLayout.orthographicSize:F2}" +
                      $"（可视高 {mainLayout.orthographicSize * 2f:F2}m），俯角 {mainLayout.cameraPitch:F0}°\n" +
                      $"  狗脚底线 {mainLayout.dogGroundLine:P0}" +
                      $"（地板最远端 {mainLayout.floorFarLine:P0}，只作自检基准）\n" +
                      $"  模型 {modelProfile.bodyWidth:F2} × {modelProfile.bodyHeight:F2} × {modelProfile.bodyLength:F2} m" +
                      $"（{PetAnimationBuilder.ModelProfilePath}）\n" +
                      $"  活动区 ±{mainLayout.wanderHalfX:F2} × ±{mainLayout.wanderHalfZ:F2} m" +
                      $"（中心偏移 {mainLayout.wanderCenterX:F2}, {mainLayout.wanderCenterZ:F2}）\n" +
                      "  每张图的构图数据点存在 SceneEntry.layout，与后台 config_scene.layout 同构，\n" +
                      "  运行时下发可覆盖（PetSceneLibrary.ApplyRemoteConfig）。\n" +
                      "  已删除的 3D 房间元素（地板/墙/窗/沙发/绿植/挂画/石板路/蓝垫）\n" +
                      "    —— 全部已画进背景图，建 3D 版既超首包又与背景重复。\n" +
                      "本阶段未做 UI（顶部等级条 / 底部导航 / 食物训练入口 / 气泡提示）。\n" +
                      "打开该场景按 Play 即可看到狗在客厅里活动。");
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------

        /// <summary>
        /// 构建主场景 UI：Canvas + 顶栏 + 底导 + 四个页面。
        ///
        /// 画布用 ScreenSpaceOverlay 而不是 Camera 空间 ——
        /// Overlay 不受正交相机的俯角与裁剪影响，UI 永远是正的、
        /// 不会被 12° 俯角压歪。竖屏取景裁切由 <see cref="PetPortraitViewport"/>
        /// 改 <c>cam.rect</c> 完成，UI 不参与那个裁切（它是屏幕空间的）。
        ///
        /// 设计分辨率固定 1080×1920（ScaleWithScreenSize）。
        /// UI 内容不挂在画布根上，而是挂在居中的 SafeFrame（1080×1920）里，
        /// 由 <see cref="PetPortraitGuard"/> 按屏幕比例摆位——
        /// 这样横屏时左右留黑边（与 PetPortraitViewport 的相机裁切对齐），
        /// UI 仍是竖屏版式，不会叠字。
        /// </summary>
        private static PetHomeUI BuildHomeUI(PetSceneLibrary library, PetSceneStage stage)
        {
            var go = new GameObject("PetHomeUI");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;
            canvas.sortingOrder = 100;   // 盖在场景与调试面板之上

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            // matchWidthOrHeight 的值不写死，由PetPortraitGuard 每帧按屏幕比例决定：
            // 比9:16 更宽（含横屏）时按高度匹配、比 9:16 更高（长屏 20:9）时按宽度匹配。
            // 写死 0 的话，编辑器横屏下画布本地高度会塌到 608，
            // 顶栏/底导/标题这套按 1920 排的常量全挤在一起叠字。
            // 这里的初值只影响编辑器里未进 Play 时的观感。
            scaler.matchWidthOrHeight = 0f;
            scaler.referencePixelsPerUnit = 100f;

            // 横竖屏保护。必须在 home.Build 之前挂上：Build 里会建SafeFrame 并找这个组件。
            go.AddComponent<PetPortraitGuard>();

            go.AddComponent<GraphicRaycaster>();

            // EventSystem：uGUI 点击的唯一入口，缺它整个 UI 是死的
            //（画面正常显示、按钮点下去完全没反应，且不报任何错）。
            //
            // 用 InputSystemUIInputModule 而不是 StandaloneInputModule：
            // 工程 activeInputHandler=1（只启用新 Input System），
            // StandaloneInputModule 依赖旧 Input，每帧抛 InvalidOperationException。
            //
            // 放独立 GameObject 而不是挂在 Canvas 上：EventSystem 是全场景唯一的，
            // 挂在 Canvas 上会让「换 Canvas 就得换 EventSystem」这种隐式耦合。
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

            // Toast 淡出需要每帧驱动，挂在 Canvas 上。
            var runner = go.AddComponent<PetUIRunner>();

            // 服务端会话：登录 → 领养 → 拉配置 → 同步状态 → 买/喂。
            // 和 PetHomeUI 挂同一个 Canvas 上，`Begin` 由 PetHomeUI.Start 在
            // 整棵 UI 建好之后调用（顺序理由见 PetHomeUI.BindSession 的注释）。
            // 这里只负责把组件放上去，不在这里初始化网络。
            go.AddComponent<PetV1.Data.PetApiClient>();
            go.AddComponent<PetV1.Data.PetSession>();

            var home = go.AddComponent<PetHomeUI>();

            // 引用不能走 SetPrivateField：library 是 AssetDatabase 加载的实例，
            // 构建过程中的写操作可能让它变成 fake null，那样会被写成 null。
            // PetHomeUI.Build 是显式方法赋值，fake null 也照原样传过去。
            home.Build(library, stage, CreateDefaultState(), BuildFoodCatalog());
            runner.Home = home;

            Debug.Log("[PetV1] UI 构建完成：顶栏（等级/经验/积分/设置）、" +
                      "底导四页（宠物/食品/商店/场景）、食品详情与购买/解锁确认弹窗。\n" +
                      "  状态条只有饱腹与快乐两项 —— 后台 decay.go 的 Settle() 只结算这两项，\n" +
                      "  展板上的「亲密」「精力」后台没有对应字段，按「以后台为准」不画。\n" +
                      "  食品用后台 config_food 的五条（普通狗粮/牛肉罐头/磨牙饼干/鸡胸肉/生日蛋糕），\n" +
                      "  不是展板那四种。");
            return home;
        }

        /// <summary>
        /// 生成 / 加载食品配置 asset。
        ///
        /// 每次构建都重置内容：asset 存在就跳过的写法会让改了 FoodCatalog.Fallback()
        /// 的数值不生效（字段初始值只在首次 CreateInstance 时起作用，
        /// 和 ScriptableObject 的 fake null 是同一类坑）。
        /// </summary>
        private static FoodCatalog BuildFoodCatalog()
        {
            var path = PetSceneBuilder.ScenesDir + "/DefaultFoodCatalog.asset";
            var cat = AssetDatabase.LoadAssetAtPath<FoodCatalog>(path);
            if (cat == null)
            {
                cat = ScriptableObject.CreateInstance<FoodCatalog>();
                AssetDatabase.CreateAsset(cat, path);
            }

            var fallback = FoodCatalog.Fallback();
            cat.items = fallback.items;
            cat.initialPoints = fallback.initialPoints;
            cat.initialItems = fallback.initialItems;
            EditorUtility.SetDirty(cat);
            return cat;
        }

        /// <summary>
        /// 原型期的初始状态。数值取后台 seed 的量级，**不是设计结论** ——
        /// 真值以 /api/v1/sync 返回为准。
        /// 背包里的初始食品从 FoodCatalog.initialItems 读，不在这里硬编码，
        /// 免得改了配置还要回来改代码。
        /// </summary>
        private static PetRuntimeState CreateDefaultState()
        {
            var s = PetRuntimeState.CreateDefault();
            var cat = Resources.Load<FoodCatalog>("PetV1/DefaultFoodCatalog")
                      ?? AssetDatabase.LoadAssetAtPath<FoodCatalog>(
                             PetSceneBuilder.ScenesDir + "/DefaultFoodCatalog.asset");

            // 用配置覆盖默认背包
            if (cat != null && cat.initialItems != null)
            {
                s.inventory.Clear();
                foreach (var init in cat.initialItems)
                {
                    if (string.IsNullOrEmpty(init.itemId)) continue;
                    s.inventory[init.itemId] = init.count;
                }
                s.points = cat.initialPoints;
            }
            return s;
        }

        // ------------------------------------------------------------------
        // 场景库与背景图资产
        // ------------------------------------------------------------------

        /// <summary>
        /// 构建 <see cref="PetSceneLibrary"/>。场景条目与后台 config_scene 对齐。
        /// 贴图缺失时留空并报警 —— 不能因为图没导入就整个构建失败。
        /// </summary>
        private static PetSceneLibrary BuildSceneLibrary()
        {
            var lib = AssetDatabase.LoadAssetAtPath<PetSceneLibrary>(SceneLibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<PetSceneLibrary>();
                AssetDatabase.CreateAsset(lib, SceneLibraryPath);
            }

// 默认模板（给新场景的初值）。运行时不读这里，读的是每条entry 自己的 layout。
            // 每次都重置：改这些数值时如果保留旧值，改了不会生效 ——
            // asset 存在就会跳过创建，而字段初始值只在首次 CreateInstance 时起作用。
            // 这个坑和 ScriptableObject 的 fake null 是同一类问题。
            lib.defaultLayout = new SceneLayout();

            // 每张背景图一套实测构图数据点，与后台 config_scene.layout 一一对应。
            //
            // floorFarLine（地板最远端，只作自检基准）的量法：
            //  临时开 TextureImporter.isReadable，量完改回 false；
            //  避开家具的空白列，逐列找亮度**局部最暗点**（室内是踢脚线暗缝）；
            //  **GetPixels32 返回的是自下而上的数组行号**（首次实测把行号当图像坐标，
            //  偏差近 300px），图像坐标 y = 1920 - 数组行号。
            //踩过的坑：先用「最强亮度跳变」找交界，选中的是地板上的阳光斑
            //  （y=1000，亮度 236）而不是墙脚，导致设成 0.54狗悬空。
            var defs = new[]
            {
                new
                {
                    id = "home_day", name = "温馨小家（白天）",
                    price = 0, unlockable = false, file = "scene_home_day.jpg",
                    layout = new SceneLayout
                    {
                        orthographicSize = 1.9f, cameraPitch = 12f, cameraDistance = 3.2f,
                        // 踢脚线暗缝实测（图像 y≈1085~1090，x=420/450/480/510 四列一致）
                        // → viewport = 1 - 1090/1920 = 0.433
                        floorFarLine = 0.433f,
                        // 地板可站区占画面下方 57%（踢脚线 0.434、地毯上缘 0.370、底边 0），
                        // 0.30 约当地毯中部 —— 有地毯做视觉锚点，且离墙脚足够远。
                        dogGroundLine = 0.30f,
                        wanderHalfX = 0.95f, wanderHalfZ = 0.70f,
                        // 沙发与边桌占画面右侧，中心往左偏（PRD 4.5：不进入家具位置）。
                        wanderCenterX = -0.30f, wanderCenterZ = -0.10f,
                        lightEulerX = 42f, lightEulerY = -35f, lightEulerZ = 0f,
                        lightColorR = 1f, lightColorG = 0.97f, lightColorB = 0.90f,
                        ambientIntensity = 1.15f, shadowOpacity = 0.32f,
                        layoutNote = "室内白天，光从左上的窗进来。踢脚线四列一致、暗缝特征明显。",
                    },
                },
                new
                {
                    id = "home_night", name = "温馨小家（夜晚）",
                    price = 100, unlockable = true, file = "scene_home_night.jpg",
                    layout = new SceneLayout
                    {
                        orthographicSize = 1.9f, cameraPitch = 12f, cameraDistance = 3.2f,
                        // 同一张布局换夜色，踢脚线实测图像 y≈1067~1085（x=430/480/530/580）
                        floorFarLine = 0.435f,
                        dogGroundLine = 0.30f,
                        wanderHalfX = 0.95f, wanderHalfZ = 0.70f,
                        wanderCenterX = -0.30f, wanderCenterZ = -0.10f,
                        lightEulerX = 42f, lightEulerY = -35f, lightEulerZ = 0f,
                        // 暖黄台灯。环境光必须压到 0.42，否则宠物亮度远高于背景，
                        // 看起来像贴纸浮在画面上。
                        lightColorR = 1f, lightColorG = 0.82f, lightColorB = 0.60f,
                        ambientIntensity = 0.42f, shadowOpacity = 0.22f,
                        layoutNote = "室内夜晚，暖黄台灯。环境光压到 0.42，否则宠物像贴纸。",
                    },
                },
                new
                {
                    id = "park_day", name = "阳光公园",
                    price = 100, unlockable = true, file = "scene_park_day.jpg",
                    layout = new SceneLayout
                    {
                        // 户外要拉远：草地是开阔平面，没有墙做纵深参照，
                        // 用室内的 1.9狗会显得过大、贴在画面上。
                        orthographicSize = 2.2f, cameraPitch = 14f, cameraDistance = 3.2f,
                        // 户外没有踢脚线。取「远处草地与灌木的交界」：
                        // 图像 y≈865（x=380/430/480/530/580 五列，viewport 0.506~0.594，
                        // 分散是因为草地本身有明暗斑块，取中位 0.549）。
                        floorFarLine = 0.549f,
                        // 中景草地，比室内略高一点；此时距交界仍差 0.25，不会贴到远处。
                        dogGroundLine = 0.32f,
                        // 活动区可以开满：画面里没有家具需要避让。
                        wanderHalfX = 1.30f, wanderHalfZ = 0.90f,
                        wanderCenterX = 0f, wanderCenterZ = 0f,
                        lightEulerX = 42f, lightEulerY = -35f, lightEulerZ = 0f,
                        lightColorR = 1f, lightColorG = 1f, lightColorB = 0.98f,
                        ambientIntensity = 1.30f, shadowOpacity = 0.28f,
                        layoutNote = "户外白天，草地开阔所以可视范围比室内大。" +
                                     "地面线取远处草地与灌木交界，不是踢脚线。",
                    },
                },
            };

            lib.scenes = new List<PetSceneLibrary.SceneEntry>();
            foreach (var d in defs)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{BackgroundDir}/{d.file}");
                if (tex == null)
                {
                    Debug.LogError($"[PetV1] 背景图缺失：{BackgroundDir}/{d.file}" +
                                   $"（从 art/scenes/final/ 复制过来）");
                }
                else
                {
                    CheckBackgroundSize(d.file, tex);
                }

                d.layout.FillDefaults();
                lib.scenes.Add(new PetSceneLibrary.SceneEntry
                {
                    sceneId = d.id,
                    displayName = d.name,
                    background = tex,
                    price = d.price,
                    unlockable = d.unlockable,
                    layout = d.layout,
                });
            }

            // 三张图的实测数据点打进日志，方便跟后台对账。
            foreach (var e in lib.scenes)
            {
                var l = e.ResolveLayout();
                Debug.Log($"[PetV1] 构图数据点 {e.sceneId}：" +
                          $"orthoSize={l.orthographicSize:F2} 俯角={l.cameraPitch:F0}° " +
                          $"地板最远端={l.floorFarLine:P1} 狗脚线={l.dogGroundLine:P1} " +
                          $"活动区=±{l.wanderHalfX:F2}×±{l.wanderHalfZ:F2} " +
                          $"环境光={l.ambientIntensity:F2} 阴影={l.shadowOpacity:F2}");
            }

            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            return lib;
        }

        /// <summary>校验背景图尺寸。PRD 要求 1080×1920，非这个比例会与宠物比例失调。</summary>
        private static void CheckBackgroundSize(string file, Texture2D tex)
        {
            if (tex.width != 1080 || tex.height != 1920)
            {
                Debug.LogError($"[PetV1] 背景图 {file} 尺寸为 {tex.width}×{tex.height}，" +
                               "PRD 要求 1080×1920。比例不对会导致宠物与背景透视对不上。");
            }
        }

        // ------------------------------------------------------------------
        // 场景物件
        // ------------------------------------------------------------------

        /// <summary>
        /// 正交相机。PRD 4.5：相机固定，用正交投影，
        /// 避免宠物远近变化导致与背景比例失调。
        /// </summary>
        private static Camera CreateOrthographicCamera(SceneLayout lay, float groundWorldY)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            // 相机高度由地面线反推，保证狗脚精确落在背景的地面线上。
            // 见 SceneLayout.ComputeCameraPosition 的注释。
            go.transform.position = lay.ComputeCameraPosition(groundWorldY);
            go.transform.rotation = Quaternion.Euler(-lay.cameraPitch, 0f, 0f);

            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;                       // 正交，不用 FOV
            cam.orthographicSize = lay.orthographicSize;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f);
            cam.cullingMask = ~0;
            cam.nearClipPlane = 0.05f;
            // 背景挂在相机下 BackgroundDistance 处，留 2m 余量即可。
            // 之前是 40+10=50m —— 那个距离是为「背景摆在世界远处」设计的，
            // 背景改成相机子物体后完全不需要这么长的远裁剪面。
            cam.farClipPlane = BackgroundDistance + 2f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            // 竖屏取景裁切：编辑器 Game 窗口是 16:9 时，可视宽 6.76m 而背景只有 2.14m，
            // 两侧各空2.3m —— 看着像「背景缩得很小」。加上这个组件后渲染区域
            // 限制为居中 9:16，宽屏窗口两侧留黑边，与真机一致。
            // 详见 PetPortraitViewport 的注释。
            var viewport = go.AddComponent<PetPortraitViewport>();

            Debug.Log($"[PetV1] 正交相机：orthoSize={lay.orthographicSize:F2}" +
                      $"（可视高 {lay.orthographicSize * 2f:F2}m），俯角 {lay.cameraPitch:F0}°，" +
                      $"位置 {go.transform.position}，狗脚线 {lay.dogGroundLine:P0}，" +
                      $"地板最远端 {lay.floorFarLine:P0}，" +
                      $"狗脚 y={groundWorldY:F3}");
            return cam;
        }

        /// <summary>
        /// 量宠物脚底的实际世界 Y。
        /// 用 Renderer bounds.min.y 而不是 transform.position.y ——
        /// 模型原点通常在身体中心或略微下沉，不是脚底。
        ///
        /// **必须排除 PetFakeShadow。** 阴影 quad 是水平薄片，
        /// 它的 bounds.min.y 比狗脚低 0.4~1cm；混进来会让「脚底线」整体下移，
        /// 而 AlignGroundLine 的 probe 用的是 bounds.min 的 XZ，两处基准不一致，
        /// 实测脚底落在画面 33.7% 而目标是 30%（差 3.7%，肉眼明显）。
        /// </summary>
        private static float MeasureFootY(PetController pet)
        {
            var renderers = pet.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = new Bounds();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.GetComponentInParent<PetFakeShadow>() != null) continue;   // 跳过阴影
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? b.min.y : GroundWorldY;
        }

        /// <summary>
        /// 迭代校正相机高度，让狗脚底精确落在 <c>dogGroundLine</c>。
        ///
        /// 注意对齐目标是**狗的脚底**，不是地板最远端（踢脚线 floorFarLine）。
        /// 早前版本用踢脚线做对齐目标，等价于强迫狗永远站在贴墙的位置 ——
        /// 数值算得再准，语义也是错的。
        ///
        /// 为什么不用闭式公式：正交俯视相机里，脚底在画面中的位置受
        /// 俯角、正交尺寸、模型原点三者影响，闭式解很容易漏项。
        /// 实测校正没有这个风险 —— 直接量 WorldToViewportPoint 的结果。
        ///
        /// 收敛：每步按误差的 90% 修正，残余误差按 0.1 倍递减，
        /// 4 次后为初始的 1e-4，对画面完全不可见。
        /// 返回迭代次数，-1 表示不收敛。
        /// </summary>
        private static int AlignGroundLine(Camera cam, float footWorldY, SceneLayout lay)
        {
            // 地面判定点：狗的正下方。用 x=0/z=0 而不是 bounds.min 的 XZ ——
            // 正交投影下画面纵向只跟相机高度有关，但 XZ 会让 probe 落到包围盒角上，
            // 在俯角下与狗正下方差一个 sin(pitch)×偏移量 的纵向差。
            var probe = new Vector3(0f, footWorldY, 0f);

            const int maxIter = 4;
            const float tolerance = 0.004f;   // 0.4% 画面高度，肉眼不可见
            Vector3 pos = cam.transform.position;

            for (int i = 1; i <= maxIter; i++)
            {
                // 每轮开始都把transform 写回相机并刷新矩阵
                cam.transform.position = pos;
                cam.ResetWorldToCameraMatrix();   // worldToCameraMatrix 是只读属性，必须走这个方法刷新
                float actual = cam.WorldToViewportPoint(probe).y;
                float err = lay.dogGroundLine - actual;
                if (Mathf.Abs(err) <= tolerance)
                {
                    cam.transform.position = pos;
                    Debug.Log($"[PetV1] 脚底线对齐：{i - 1} 次迭代收敛，" +
                              $"脚底在画面 {actual:P1}（目标 {lay.dogGroundLine:P0}，" +
                              $"地板最远端 {lay.floorFarLine:P0}），相机 Y={pos.y:F3}");
                    return i - 1;
                }

                // 实测斜率而不是推导：相机上移 1cm，地面 viewportY 动多少。
                // 这样俯角、正交尺寸的影响全被自动吸收。
                //
                // 注意：改 position 后必须先 WorldToCameraMatrix 再测。
                // WorldToViewportPoint 走的是缓存矩阵，直接改 transform 有时读不到
                // （表现为 slope≈0，因为两次量的是同一个旧矩阵的结果）。
                float currentY = pos.y;
                pos.y = currentY + 0.01f;
                cam.transform.position = pos;
                cam.ResetWorldToCameraMatrix();   // worldToCameraMatrix 是只读属性，必须走这个方法刷新
                float slope = (cam.WorldToViewportPoint(probe).y - actual) / 0.01f;
                if (Mathf.Abs(slope) < 1e-4f)
                {
                    Debug.LogError("[PetV1] 脚底线对齐失败：相机高度对脚底位置无影响" +
                                   "（slope≈0），检查是否真用了正交投影。");
                    return -1;
                }

                pos.y = currentY + err / slope * 0.9f;
            }


            cam.transform.position = pos;
            float finalV = cam.WorldToViewportPoint(probe).y;
            Debug.LogWarning($"[PetV1] 脚底线对齐：{maxIter} 次迭代后仍未完全收敛，" +
                             $"脚底在画面 {finalV:P1}（目标 {lay.dogGroundLine:P0}），建议看截图确认。");
            return maxIter;
        }

        /// <summary>
        /// 背景 quad。Unlit 材质不吃场景光，
        /// 这样换场景时只改贴图与灯光，背景本身始终保持原图亮度。
        /// </summary>
        private static MeshRenderer CreateBackgroundQuad(SceneLayout lay, Camera cam,
                                                       PetSceneLibrary.SceneEntry first)
        {
            var go = new GameObject("SceneBackground");

            // quad 要与正交画面的宽高比一致。
            // 正交可视区：宽 = orthoSize*2*aspect，高 = orthoSize*2。
            //
            // aspect 取 9:16 而不是相机的实时 aspect：
            // 构建时 Game 窗口往往是 16:9，用实时 aspect 会算出 16:9 的背景尺寸，
            // 之后切到竖屏就又对不上。背景尺寸只跟图自身比例 + 可视高有关，
            // 与当前窗口比例无关 —— 这正是它该有的样子。
            float aspect = 9f / 16f;
            float viewH = lay.orthographicSize * 2f;
            float viewW = viewH * aspect;

            // 背景图是 9:16（1080×1920）。**绝对不能为了铺满视野去拉伸它**——
            // 之前写成 if (w < viewW) w = viewW，宽屏视图下背景被横向拉宽
            //（实测 6.76 × 3.80 = 16:9，而原图是 9:16），画面全变形了。
            //
            // 正确做法：按图片自身比例取「高度铺满」的尺寸，
            // 宽窄出来的部分交给相机裁剪。竖屏设备（aspect≈0.5625）刚好完整显示；
            // 编辑器宽屏下左右会露出背景外的区域，正式分辨率以竖屏为准，
            // 那部分由 UI 的顶栏与底部导航盖住。
            const float imgAspect = 1080f / 1920f;
            float h = viewH;
            float w = viewH * imgAspect;

            Debug.Log($"[PetV1] 背景 {w:F2} × {h:F2} m（图比例 9:16），" +
                      $"正交视野 {viewW:F2} × {viewH:F2} m（比例 {aspect:F3}）");

            // quad 挂在相机下、局部坐标 (0,0,d)：永远正对取景平面。
            //
            // 原来写的是 go.transform.position = new Vector3(0, 0, BackgroundDistance)
            // —— 把背景摆在世界固定 Z 上，而相机在 z=-3.2 且带 12°俯角。
            // 正交投影下远处物体的画面纵向位置 = 距离 × tan(pitch)，
            // 40m 时偏移 8.5m，可视高只有 3.8m —— 背景整个掉到画面外，
            // Game 视图里只剩 clearFlags 的纯色，看起来就是「背景全黑」。
            //
            // 挂成子物体后偏移恒为零，改 orthographicSize / cameraPitch 都自动跟随。
            go.transform.SetParent(cam.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, BackgroundDistance);
            // 【不要转 180°】
            //
            // 转 180° 确实能让相机看到正面（`BuildQuadMesh` 的法线是
            // Vector3.back，正面朝本地 -Z），但代价是**整张背景左右镜像**：
            // 原图沙发在左，转完后沙发跑到右，与美术原图对不上。
            // 实测截图里就是这个症状。
            //
            // 正确做法：翻转网格绕序与法线，让正面朝本地 +Z
            // （相机看向自身 +Z，这才是要看到的那一面）。
            // 见 BuildQuadMesh(out bool faceForward) 的说明。
            go.transform.localRotation = Quaternion.identity;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildQuadMesh();

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetBackgroundMaterial();
            // 构建时就贴上当前场景的背景图。
            // PetSceneStage.Start() 只在 Play 模式跑，编辑器里不触发 ——
            // 不在这里贴的话，编辑器 SceneView 里背景是纯白的，看不出构图对不对。
            if (mr.sharedMaterial != null && first != null && first.background != null)
            {
                mr.sharedMaterial.mainTexture = first.background;
                Debug.Log($"[PetV1] 背景贴图已绑定：{first.background.name}（{first.sceneId}）");
            }
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // 背景永远画在最底层
            mr.sortingOrder = -100;

            // quad 本身是 1×1，靠 scale 放大
            go.transform.localScale = new Vector3(w, h, 1f);

            Debug.Log($"[PetV1] 背景 quad {w:F2} × {h:F2} m，距相机 {BackgroundDistance:F0} m，" +
                      $"正交可视区 {viewW:F2} × {viewH:F2} m");

            VerifyBackgroundFacingCamera(mr, cam);

            return mr;
        }

        /// <summary>
        /// 构建时校验背景 quad 的**正面朝向相机**，且四角落在裁切区内。
        ///
        /// 为什么必须查：
        /// ① 背面剔除发生在光栅化阶段，WorldToViewportPoint 量出来完全正常
        ///    （四角 viewport 0~1 铺满），几何是对的，只有「面朝哪边」错了。
        ///    这类 bug 从坐标数字上算不出来，只能查法线与视线方向。
        /// ② 之前两次都栽在这：一次是背面剔除导致全黑，
        ///    一次是背景摆在远处被俯角推出画面。两者症状一样（画面全黑）。
        ///
        /// 判据：法线方向与「相机→背景」方向同向即为正面朝向相机。
        /// </summary>
        private static void VerifyBackgroundFacingCamera(MeshRenderer mr, Camera cam)
        {
            // sharedMesh 在 MeshFilter 上，MeshRenderer 没有这个成员
            var mf = mr.GetComponent<MeshFilter>();
            var normals = mf != null && mf.sharedMesh != null ? mf.sharedMesh.normals : null;
            if (normals == null || normals.Length == 0)
            {
                Debug.LogError("[PetV1] 背景 mesh 没有法线，无法校验朝向。");
                return;
            }

            // quad 正面方向（法线在本地空间，取第一个即可，四角一致）
            Vector3 worldNormal = mr.transform.TransformDirection(normals[0]).normalized;
            // 相机指向背景的方向
            Vector3 toBg = (mr.bounds.center - cam.transform.position).normalized;
            float dot = Vector3.Dot(worldNormal, toBg);

            if (dot <= 0f)
            {
                Debug.LogError($"[PetV1] 背景 quad 背面朝向相机（法线·视线 = {dot:F2} <= 0），" +
                               "会被背面剔除，Game 视图里背景全黑。" +
                               "修法：网格法线应为 Vector3.forward 且绕序 0,2,1/0,3,2，" +
                               "localRotation 保持 identity。" +
                               "**不要用转 180° 的办法**，那会让背景左右镜像。");
            }
            else
            {
                Debug.Log($"[PetV1] 背景朝向校验通过（法线·视线 = {dot:F2}）。");
            }

            // 顺带查镜像：绕 Y 转 180° 的 quad 法线仍朝相机（dot > 0，能过上一项），
            // 但贴图左右翻转。判据是 quad 本地 +X（UV 从 0 到 1 的方向）
            // 经变换后是否与相机本地 +X 同向。
            Vector3 bgRight = mr.transform.TransformDirection(Vector3.right).normalized;
            Vector3 camRight = cam.transform.right.normalized;
            float mirrorDot = Vector3.Dot(bgRight, camRight);
            if (mirrorDot < 0f)
            {
                Debug.LogError($"[PetV1] 背景左右镜像了（UV+X 方向 · 相机右方向 = {mirrorDot:F2} < 0）。" +
                               "画面与美术原图左右相反。检查 localRotation 是否有绕 Y 的 180° 旋转。");
            }

            // 顺带校验四角是否在裁切区内
            Bounds b = mr.bounds;
            var corners = new[]
            {
                new Vector3(b.min.x, b.min.y, b.center.z),
                new Vector3(b.max.x, b.min.y, b.center.z),
                new Vector3(b.min.x, b.max.y, b.center.z),
                new Vector3(b.max.x, b.max.y, b.center.z),
            };
            foreach (var c in corners)
            {
                Vector3 v = cam.WorldToViewportPoint(c);
                if (v.x < -0.05f || v.x > 1.05f || v.y < -0.05f || v.y > 1.05f)
                {
                    Debug.LogError($"[PetV1] 背景角点 {c} 在画面外（viewport = {v.x:F2}, {v.y:F2}），" +
                                   "画面会有大面积空白。检查 backgroundDistance 与 cameraPitch。");
                }
            }
        }

        /// <summary>
        /// 主光。方向必须与背景图光源一致（PRD：统一左上），
        /// 否则宠物身上会有与背景矛盾的第二套影子。
        /// </summary>
        private static Light CreateKeyLight(SceneLayout lay)
        {
            var go = new GameObject("Key Light");
            go.transform.rotation = Quaternion.Euler(lay.LightEuler);

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;   // 背景不吃阴影，投影没有接收面
            light.color = lay.LightColor;
            light.intensity = 1f;
            light.renderMode = LightRenderMode.ForcePixel;

            // 补一盏很弱的反向光，避免背面全黑
            var fillGo = new GameObject("Fill Light");
            fillGo.transform.rotation = Quaternion.Euler(-20f, 140f, 0f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.shadows = LightShadows.None;
            fill.color = new Color(0.85f, 0.88f, 0.95f);
            fill.intensity = 0.35f;
            fill.renderMode = LightRenderMode.ForcePixel;

            RenderSettings.ambientIntensity = lay.ambientIntensity;
            return light;
        }

/// <summary>
 /// 椭圆半透明假阴影。PRD 4.5 明确要求 ——
        /// 2D 背景上的阴影是画出来的，3D 宠物没有接收阴影的地面，
   /// 没有这个假阴影玩家会觉得狗浮在半空。
  /// </summary>
        private static PetFakeShadow CreateFakeShadow(Transform pet, PetModelProfile profile,
                                                      SceneLayout lay, float groundY)
        {
            var go = new GameObject("PetFakeShadow");
            go.transform.SetParent(pet, false);

            var shadow = go.AddComponent<PetFakeShadow>();
            shadow.FitToBody(profile.bodyWidth, profile.bodyLength);
            shadow.Configure(lay.shadowOpacity);
            shadow.Attach(pet, groundY);

            Debug.Log($"[PetV1] 假阴影已创建：{profile.bodyWidth * PetFakeShadow.WidthFactor:F2} × " +
                      $"{profile.bodyLength * PetFakeShadow.LengthFactor:F2} m（按体型自动算）");
            return shadow;
        }

        // ------------------------------------------------------------------
        // 构图自检
        // ------------------------------------------------------------------

        /// <summary>
        /// 构图自检。判据：
        /// ① 狗占画面高度在 12%~25% 之间（太小看不见，太大撑满）
        /// ② 狗的脚在画面地面线附近（不悬空、不陷进背景的家具里）
        /// ③ 狗在相机视锥内
        ///
        /// 正交投影下这些都好算，不像透视要来回试。
        /// </summary>
        private static void VerifyComposition(PetController pet, PetFakeShadow shadow,
                                             MeshRenderer bg, Camera cam, SceneLayout lay)
        {
            // 取狗本体的 Renderer，必须排除假阴影。
            // 阴影 quad 是水平薄片，bounds.min.y 比狗脚低，
            // 混进来会让「占画面比例」和「脚底线」两项一起失真
            // （与 MeasureFootY 是同一个坑）。
            Renderer renderer = null;
            foreach (var r in pet.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r.GetComponentInParent<PetFakeShadow>() != null) continue;
                renderer = r;
                break;
            }
            if (renderer == null)
            {
                Debug.LogError("[PetV1] 狗没有 Renderer，无法校验构图。");
                return;
            }

            // ① 占画面比例
            var top = cam.WorldToViewportPoint(renderer.bounds.max);
            var bottom = cam.WorldToViewportPoint(renderer.bounds.min);
            float ratio = Mathf.Abs(top.y - bottom.y);
            if (ratio < 0.12f || ratio > 0.25f)
            {
                Debug.LogError($"[PetV1] 构图警告：狗占画面高度 {ratio:P1}，" +
                               $"建议 12%~25%。调整该场景 layout.orthographicSize（当前 {lay.orthographicSize:F2}）。");
            }

            // ② 脚要落在 dogGroundLine 附近（正交下地面是固定高度）
            //
            // 判定点必须与 AlignGroundLine 的 probe 完全一致：
            // 都是「狗正下方 x=0 / z=0，高度 = bounds.min.y」。
            // 早前这里用 bounds.min 整个向量，XZ 落在包围盒角上
            // （实测 x=-0.22 / z=-0.73），俯角下比 x=0/z=0 低 3.7%，
            // 于是脚底实测 33.7% 而对齐目标是 30% —— 自检永远在报错。
            float footViewportY = cam.WorldToViewportPoint(
                new Vector3(0f, renderer.bounds.min.y, 0f)).y;
            float expected = lay.dogGroundLine;
            if (Mathf.Abs(footViewportY - expected) > 0.02f)
            {
                Debug.LogError($"[PetV1] 狗脚在画面 {footViewportY:P1}，" +
                               $"目标 {expected:P0}。相机构图没对齐，重新构建场景。");
            }

            // ②b 狗必须站在【地板上】，而不是墙脚线上。
            // 这一项是本轮修的核心 bug：早前拿踢脚线（地板最远端）当对齐目标，
            // 等于强迫狗永远站在贴墙处，数值算得再准语义也是错的。
            // 判据：狗脚线必须明显低于地板最远端，否则视觉上就是「站在墙根」。
            if (footViewportY > lay.floorFarLine - 0.05f)
            {
                Debug.LogError(
                    $"[PetV1] 狗站到墙脚线上了：脚底线 {footViewportY:P1}，" +
                    $"地板最远端 {lay.floorFarLine:P0}。狗会显得贴着墙或陷进地板。" +
                    $"调低该场景的 dogGroundLine（当前 {lay.dogGroundLine:P0}），别去调 floorFarLine。");
            }

            // ③ 假阴影必须在狗脚下，且水平位置对齐
            if (shadow != null)
            {
                Vector3 sp = shadow.ShadowWorldPosition;
                Vector3 pp = pet.transform.position;
                float dxz = Mathf.Abs(sp.x - pp.x) + Mathf.Abs(sp.z - pp.z);
                if (dxz > 0.02f)
                {
                    Debug.LogWarning($"[PetV1] 假阴影偏离狗 {dxz:F3} m，" +
                                     "狗会像是站在影子旁边。");
                }
                if (shadow.CurrentOpacity <= 0.001f)
                {
                    Debug.LogWarning("[PetV1] 假阴影不透明度为 0，狗会显得漂浮。");
                }

                // ⑤ 阴影必须探出躯干投影，否则等于没有。
                // 这一项只看渲染参数是查不出来的 —— isVisible / alpha / Cull 全都正常，
                // 但椭圆严丝合缝藏在躯干下面时屏幕上零像素。
                // 判据用屏幕空间：俯角压缩下地面 quad 的世界 Z 跨度不能直接比，
                // 必须比 viewport 宽度。
                var sr = shadow.GetComponent<Renderer>();
                if (sr != null)
                {
                    Vector3 sb = sr.bounds.center;
                    float dogHalfW = renderer.bounds.extents.x;
                    float shadowHalfW = sr.bounds.extents.x;
                    if (shadowHalfW <= dogHalfW * 1.05f)
                    {
                        Debug.LogError(
                            $"[PetV1] 假阴影完全被躯干挡住：狗半宽 {dogHalfW:F3} m，" +
                            $"阴影半宽 {shadowHalfW:F3} m。画面上不会有任何阴影像素，" +
                            $"狗会显得悬空。调大 PetFakeShadow.WidthFactor。");
                    }
                    else
                    {
                        Debug.Log($"[PetV1] 假阴影探出躯干 {shadowHalfW - dogHalfW:F3} m/侧，" +
                                  $"屏幕中心 {cam.WorldToViewportPoint(sb).y:P1}");
                    }
                }
            }

            // ④ 背景必须比宠物远
            if (bg != null)
            {
                float bgZ = cam.WorldToViewportPoint(bg.transform.position).z;
                float petZ = cam.WorldToViewportPoint(pet.transform.position).z;
                if (bgZ < petZ)
                {
                    Debug.LogError($"[PetV1] 背景比宠物更近（bg {bgZ:F2} < pet {petZ:F2}），" +
                                   "宠物会被背景遮住。");
                }
            }

            Debug.Log($"[PetV1] 构图自检：狗占画面 {ratio:P1}（目标 12%~25%），" +
                      $"脚在 {footViewportY:P1}（目标 {expected:P0}，地板最远端 {lay.floorFarLine:P0}），" +
                      $"正交可视高 {cam.orthographicSize * 2f:F2} m");
        }

        // ------------------------------------------------------------------
        // 资产与辅助
        // ------------------------------------------------------------------

        private static void EnsureFolders()
        {
            PetAnimationBuilder.EnsureFolder("Assets/Scenes", "PetPrototype");
            PetAnimationBuilder.EnsureFolder("Assets/Scenes/PetPrototype", "Backgrounds");
        }

        /// <summary>背景材质。Unlit，保证不吃场景光。</summary>
        private static Material GetBackgroundMaterial()
        {
            const string matPath = BackgroundDir + "/Background_Unlit.mat";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat != null) return mat;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                Debug.LogError("[PetV1] 找不到 Unlit shader，背景无法显示。");
                return null;
            }

            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
        }

        /// <summary>检查三张背景图是否已复制进工程。</summary>
        private static void BuildBackgroundAssets()
        {
            var expected = new[] { "scene_home_day.jpg", "scene_home_night.jpg", "scene_park_day.jpg" };
            int missing = 0;
            foreach (var f in expected)
            {
                if (AssetDatabase.LoadAssetAtPath<Texture2D>($"{BackgroundDir}/{f}") == null)
                {
                    Debug.LogError($"[PetV1] 缺少背景图 {BackgroundDir}/{f}。" +
                                   "请把 art/scenes/final/ 下的同名文件复制进工程。");
                    missing++;
                }
            }
            if (missing == 0)
            {
                Debug.Log($"[PetV1] 三张背景图齐备（{expected.Length} 张，1080×1920）。");
            }

            // 导入设置：背景图有大量柔和渐变，压缩伪影在木地板上很明显，
            // 所以不压缩。另外 mipmap 在这种大图上是浪费（V1 不做缩放 LOD）。
            //
            // **不要动 maxTextureSize**：源图 1080×1920，最长边 1920 已在默认 2048 以内，
            // 不需要缩放。之前强行设成 2048 反而触发了 Unity 的最长边缩放，
            // 导入后变成 1024×2048 —— 宽被裁掉 56px，比例从 9:16 变成 1:2，
            // 宠物与背景透视直接对不上。
            foreach (var f in expected)
            {
                var importer = AssetImporter.GetAtPath($"{BackgroundDir}/{f}") as TextureImporter;
                if (importer == null) continue;

                bool needSave = false;
                if (importer.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    needSave = true;
                }
                if (importer.mipmapEnabled) { importer.mipmapEnabled = false; needSave = true; }
                if (importer.wrapMode != TextureWrapMode.Clamp)
                {
                    importer.wrapMode = TextureWrapMode.Clamp;
                    needSave = true;
                }
                if (importer.textureType != TextureImporterType.Default)
                {
                    importer.textureType = TextureImporterType.Default;
                    needSave = true;
                }
                // NPOT（1080×1920 不是 2 的幂）必须显式设 ToNone，
                // 否则 Unity 默认按最长边缩放到 2 的幂附近：
                // 1920 会变成 2048，1080 就被按同比例拉成 1152——
                // 实测出现过导入后变成 1024×2048，比例从 9:16 变成 1:2。
                if (importer.npotScale != TextureImporterNPOTScale.None)
                {
                    importer.npotScale = TextureImporterNPOTScale.None;
                    needSave = true;
                }
                // maxTextureSize 必须 >= 1920，否则长边会被压掉。
                // 之前误以为「不动最安全」，其实默认值 2048 就够。
                if (importer.maxTextureSize < 2048)
                {
                    importer.maxTextureSize = 2048;
                    needSave = true;
                }
                if (needSave)
                {
                    importer.SaveAndReimport();
                    var chk = AssetDatabase.LoadAssetAtPath<Texture2D>($"{BackgroundDir}/{f}");
                    Debug.Log($"[PetV1] 背景图导入设置已修正：{f} -> " +
                              $"{chk.width}x{chk.height}" +
                              (chk.width == 1080 && chk.height == 1920
                                  ? "（正确）" : "（**仍不等于 1080x1920**）"));
                }
            }
        }

        /// <summary>
        /// 1×1 quad，**正面朝本地 +Z**（即挂成相机子物体后正面朝向相机）。
        ///
        /// 绕序与法线方向是配套的，不能只改一个：
        /// - 顶点 0..3 按 (-,-) (+,-) (+,+) (-,+) 排列，即从 +Z 方向看是逆时针
        /// - 绕序 0,2,1 / 0,3,2 对应这个逆时针，正面朝 +Z
        /// - 法线必须是 +Z；只翻转绕序而法线还写 back 的话，
        ///   着色仍按背面处理，等于没改
        ///
        /// 之前这里是 Vector3.back + 绕序 0,1,2/0,2,3（正面朝 -Z），
        /// 为了让相机看到正面只能把整个 quad 转 180°，
        /// 代价是**背景图左右镜像**（原图沙发在左，游戏画面里跑到右）。
        /// 现在从网格层面解决，transform 保持 identity。
        /// </summary>
        private static Mesh BuildQuadMesh()
        {
            var m = new Mesh { name = "BackgroundQuad" };
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            });
            m.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            });
            m.SetNormals(new[]
            {
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            });
            // 翻转绕序，使正面朝 +Z（UV 不动，所以贴图方向也跟着对）
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateBounds();
            return m;
        }

        private static Scene NewSceneInternal() => PetSceneBuilder.NewScene(HomeScenePath);

        private static bool AssetExists<T>(string path) where T : UnityEngine.Object
            => AssetDatabase.LoadAssetAtPath<T>(path) != null;
    }
}