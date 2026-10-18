using System.Collections.Generic;
using PetV1.Animation;
using PetV1.Behavior;
using PetV1.Diagnostics;
using PetV1.Interaction;
using PetV1.Pet;
using PetV1.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 主场景（首页）构建器。
    ///
    /// 依据《电子宠物 APP V1 设计展板》第 2 页「宠物主场景（首页）」原型，
    /// 需求文档 3.2 节：3D 场景里显示宠物，宠物主动提示、用户可触摸互动。
    ///
    /// 展板原型是**写实渲染的室内客厅**，本工程的模型资源是低多边形卡通
    /// （ithappy Animals FREE）。所以这里的做法是：用 Unity 内置几何体
    /// 搭出原型的**空间构成和配色**，狗沿用现有模型。
    ///
    /// 展板原型里的元素与本实现的对应关系：
    /// | 展板元素       | 本实现                                |
    /// |----------------|---------------------------------------|
    /// | 木地板         | 木色大平板Floor_Wood                 |
    /// | 草地地毯（绿）  | 绿色地毯 Floor_Grass                 |
    /// | 蓝色小方垫     | 狗脚下蓝垫 Mat_Pet                    |
    /// | 石板小路       | 灰石板 Path_Stone                     |
    /// | 木框落地窗     | 木框 + 半透明窗面 Wall_Window         |
    /// | 窗台绿植       | 球体灌木 Planter_A/B/C               |
    /// | 沙发           | 蓝色块 Sofa_Base / Sofa_Back         |
    /// | 挂画           | 墙上薄板 Picture_A                   |
    /// | 狗坐在垫子上   | PlaceDog + PetController（坐姿由 Idle 动画表现）|
    ///
    /// 本阶段**不做 UI**（顶部等级条、底部导航、食物/训练入口、气泡提示），
    /// UI 需要另开一轮，见需求文档 3.2。调试仍用 PetDebugPanel。
    /// </summary>
    public static class PetHomeSceneBuilder
    {
        public const string HomeScenePath = PetSceneBuilder.ScenesDir + "/PetHome.unity";
        private const string MatDir = PetSceneBuilder.ScenesDir + "/Materials";

        // 空间尺寸（米）。狗实测 0.42 × 0.76 × 1.27（宽×高×长），
        // 下面的尺寸按这个比例定的：狗在画面里占约 1/3 高度。
        // 房间尺寸按「狗在画面里占约 1/3 高度」倒推，不是照抄写实比例。
        // 展板里是写实室内，狗看起来很大；换成 0.76m 高的低模狗 + FOV 45，
        // 房间必须缩到 5×4.5m 才接近那个视觉比例。第一版用 9×7.5m，
        // 截出来狗只有一个小点，完全不对。
        private const float RoomWidth = 5.0f;      // X
        private const float RoomDepth = 4.5f;     // Z
        private const float WallHeight = 2.2f;
        private const float WindowBottom = 0.62f;  // 窗台离地高度

        // 狗的活动范围：比原型里的垫子大一些，让它能走动，
        // 但不能走到墙外或踩到窗台。
        private static readonly Vector2 WanderArea = new Vector2(1.15f, 1.0f);

        // 相机参数集中在这里，日志和注释都引用它 ——
        // 之前逐轮调参时硬编码的文案与实际值不一致（写着 18° 实际 9°），很难发现。
        private const float CamFov = 45f;
        private const float CamPitch = 9f;         // 俯角
        private static readonly Vector3 CamPosition = new Vector3(0f, 0.62f, -2.6f);

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

            // ---- 所有 AssetDatabase 写操作集中在前面 ----
            // 见 PetSceneBuilder.BuildAllScenes 的注释：AssetDatabase 写操作会
            // 触发资产重导入，销毁并重建 ScriptableObject 的 native 实例
            // （Unity fake null），之后再写入的 animationMap 引用会变成死对象。
            EnsureFolders();
            BuildMaterials();

            int wallsLayer = LayerMask.NameToLayer(PetSceneBuilder.WallsLayerName);
            if (wallsLayer < 0) wallsLayer = 0;

            // ---- 建场景 ----
            var scene = NewSceneInternal();

            var map = PetAnimationBuilder.LoadAnimationMap();
            var profile = PetAnimationBuilder.LoadModelProfile();
            var pet = PetSceneBuilder.PlaceDog(dogPrefab, out Transform rigRoot);
            // 活动区以 profile 为准（换模型时可调）。WanderArea 常量只作日志展示。
            pet.SetWanderArea(new Vector2(profile.wanderHalfX, profile.wanderHalfZ), Vector2.zero);

            // 狗坐在地毯中央的蓝垫上，面向镜头（-Z）。
            pet.transform.position = new Vector3(0f, 0.026f, 0.35f);
            // 实测（MCP 读 scull 骨骼）：模型默认头朝 +Z，相机在 -Z，
            // 所以必须转 180 度才是正面朝向镜头。
            // 反过来不转就是背对 —— 靠骨骼位置判断，不要凭感觉。
            pet.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var controller = PetSceneBuilder.CreateController(pet.gameObject);
            PetSceneBuilder.WireOrchestrator(controller, pet.gameObject, map, "PetHome");

            BuildRoom();
            BuildFurniture();
            BuildPetMat();

            // ---- 触摸输入 ----
            var touchGo = new GameObject("TouchInputManager");
            var touch = touchGo.AddComponent<TouchInputManager>();
            var cam = CreateCamera();
            PetSceneBuilder.SetPrivateField(touch, "targetCamera", cam);
            int petLayer = PetSceneBuilder.PetLayerIndex();
            if (petLayer >= 0)
                PetSceneBuilder.SetPrivateField(touch, "petLayers", 1 << petLayer);

            // ---- 身体区域 Collider（Pet 层）----
            // 骨骼名 / 体型 / 活动区全部从 PetModelProfile 读，换模型只改那个 asset。
            // AssetDatabase 写操作已全部前置（见类注释的 fake null 说明），
            // 这里加载是安全的。
            PetSceneBuilder.SetupRegionColliders(pet, rigRoot, profile);

            // ---- 墙体与避障 ----
            // obstacleMask 指向 Walls 层，宠物会在墙前提前转向。
            var so = new SerializedObject(pet);
            var maskProp = so.FindProperty("obstacleMask");
            if (maskProp != null) maskProp.intValue = 1 << wallsLayer;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pet);

            // ---- 行为控制器 ----
            var behavior = pet.gameObject.AddComponent<PetBehaviorController>();
            PetSceneBuilder.WireBehavior(behavior, pet, controller, touch);

            // ---- 调试面板 ----
            var debugGo = new GameObject("PetDebugPanel");
            var panel = debugGo.AddComponent<PetDebugPanel>();
            PetSceneBuilder.SetPrivateField(panel, "behaviorController", behavior);
            PetSceneBuilder.SetPrivateField(panel, "animationOrchestrator", controller);
            PetSceneBuilder.SetPrivateField(panel, "touchInputManager", touch);

            CreateInfoPanel();

            // 遮挡自检：狗在 x=0，相机也在 x=0。任何放在狗「身后」的
            // 家具都会挡住它，玩家只看到狗头从物体后面冒出来。
            // 这个 bug 第一次是靠肉眼看 SceneView 截图发现的，
            // 布局一改就可能复发，所以做成构建时自动校验。
            VerifyNoOcclusion(pet, cam);

            PetSceneBuilder.SaveScene(scene, HomeScenePath);

            Debug.Log("[PetV1] 主场景构建完成：\n" +
                      $"  {HomeScenePath}\n" +
                      $"  空间 {RoomWidth} × {RoomDepth} × {WallHeight} m，" +
                      $"活动区 ±{profile.wanderHalfX:F2} × ±{profile.wanderHalfZ:F2} m\n" +
                      $"  模型 {profile.bodyWidth:F2} × {profile.bodyHeight:F2} × {profile.bodyLength:F2} m" +
                      $"（{PetAnimationBuilder.ModelProfilePath}），朝向镜头（rotation 180°）\n" +
                      $"  速度 walk {profile.walkSpeed:F1} / run {profile.runSpeed:F1} m/s，转向 {profile.turnSpeed:F0}°/s\n" +
                      $"  相机 FOV {CamFov}，俯角 {CamPitch}°\n" +
                      "本阶段未做 UI（顶部等级条 / 底部导航 / 食物训练入口 / 气泡提示）。\n" +
                      "打开该场景按 Play 即可看到狗在客厅里活动。");
        }

        // ------------------------------------------------------------------
        // 遮挡自检
        // ------------------------------------------------------------------

        /// <summary>
        /// 检查狗是否被家具挡住。
        ///
        /// 判定方式改成**屏幕空间 AABB 重叠 + 深度比较**，不用世界空间的水平距离。
        ///
        /// 为什么换判据：第一版用「家具中心到相机→狗连线的水平距离」，
        /// 并且只检查「狗身后（z 更大）」的物体，结果漏报。
        /// 当时绿植的叶片因为坐标 bug 全被摆到了世界原点 (0, 0.4, 0)，
        /// z 比狗小，被「只检查狗身后」这条规则直接过滤掉，
        /// 而它其实正压在狗身上。自检报告「0 个遮挡」就是这么来的。
        ///
        /// 屏幕空间判定不依赖任何坐标假设：玩家在画面上看到家具盖住狗，就一定命中。
        /// </summary>
        private static void VerifyNoOcclusion(PetController pet, Camera cam)
        {
            var petRenderer = pet.GetComponentInChildren<Renderer>(true);
            if (petRenderer == null) return;

            Rect petRect = WorldToScreenRect(cam, petRenderer.bounds);
            float petDepth = petRenderer.bounds.center.z;

            int blockers = 0;
            int overlapCount = 0;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root == pet.gameObject) continue;
                // Room 是地板/墙/窗/挂画，属于背景，不参与遮挡判定
                if (root.name == "Room") continue;

                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    // 贴地物体（地毯/石板路/垫子）不挡
                    if (r.bounds.max.y < 0.30f) continue;

                    Rect rect = WorldToScreenRect(cam, r.bounds);
                    if (!rect.Overlaps(petRect)) continue;

                    overlapCount++;
                    // 狗前面（z 更小，靠近相机）才算遮挡
                    if (r.bounds.center.z >= petDepth) continue;

                    Debug.LogError($"[PetV1] 遮挡警告：{r.name}（父物体 {r.transform.parent?.name}）" +
                                   $" 在屏幕上盖住了狗。 z={r.bounds.center.z:F2} < dogZ={petDepth:F2}。" +
                                   " 请把它移离 x=0 中轴，或挪到狗前方。");
                    blockers++;
                }
            }

            if (blockers == 0)
            {
                Debug.Log($"[PetV1] 遮挡自检通过：{overlapCount} 个物体与狗屏幕投影重叠，但都在狗后面。");
            }
        }

        /// <summary>把世界空间包围盒投影成屏幕像素外接矩形（取 8 个角）。</summary>
        private static Rect WorldToScreenRect(Camera cam, Bounds b)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);

                Vector3 sp = cam.WorldToScreenPoint(corner);
                if (sp.z < 0f) continue; // 相机背后
                minX = Mathf.Min(minX, sp.x);
                minY = Mathf.Min(minY, sp.y);
                maxX = Mathf.Max(maxX, sp.x);
                maxY = Mathf.Max(maxY, sp.y);
            }

            if (minX > maxX) return new Rect(0f, 0f, 0f, 0f); // 整体在相机背后
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private static Scene NewSceneInternal() => PetSceneBuilder.NewScene(HomeScenePath);

        // ------------------------------------------------------------------
        // 材质
        // ------------------------------------------------------------------

        private static void EnsureFolders()
        {
            PetAnimationBuilder.EnsureFolder("Assets/Scenes", "PetPrototype");
            PetAnimationBuilder.EnsureFolder("Assets/Scenes/PetPrototype", "Materials");
        }

        private static void BuildMaterials()
        {
            MakeMaterial("Home_WoodFloor", new Color(0.42f, 0.30f, 0.21f), 0.15f, 0.05f);
            MakeMaterial("Home_GrassRug", new Color(0.47f, 0.65f, 0.34f), 0.02f, 0f);
            MakeMaterial("Home_PetMat", new Color(0.24f, 0.42f, 0.62f), 0.05f, 0f);
            MakeMaterial("Home_StonePath", new Color(0.58f, 0.56f, 0.52f), 0f, 0.2f);
            MakeMaterial("Home_WallPaint", new Color(0.88f, 0.83f, 0.75f), 0f, 0f);
            MakeMaterial("Home_WallAccent", new Color(0.62f, 0.48f, 0.36f), 0f, 0.1f);
            MakeMaterial("Home_WindowFrame", new Color(0.45f, 0.33f, 0.24f), 0f, 0.1f);
            MakeMaterial("Home_SofaFabric", new Color(0.30f, 0.44f, 0.58f), 0.05f, 0f);
            MakeMaterial("Home_PlanterLeaf", new Color(0.28f, 0.48f, 0.22f), 0.05f, 0f);
            MakeMaterial("Home_PlanterPot", new Color(0.55f, 0.36f, 0.26f), 0f, 0f);
            MakeMaterial("Home_PictureFrame", new Color(0.40f, 0.30f, 0.22f), 0f, 0.1f);
            MakeMaterial("Home_PictureArt", new Color(0.72f, 0.60f, 0.44f), 0f, 0f);
            MakeWindowGlass();
        }

        /// <summary>
        /// 窗玻璃：URP 的 Lit 走透明需要改渲染队列和混合模式，
        /// 不只是设 _Surface，否则不透明渲染会把它当实心板。
        /// </summary>
        private static void MakeWindowGlass()
        {
            string path = $"{MatDir}/Home_WindowGlass.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetFloat("_Smoothness", 0.95f);
            mat.SetFloat("_Metallic", 0.05f);
            // 半透明：窗外有光感，但不挡看狗
            mat.SetColor("_BaseColor", new Color(0.76f, 0.86f, 0.93f, 0.22f));
            mat.SetFloat("_Surface", 1f);        // 0=Opaque, 1=Transparent
            mat.SetFloat("_Blend", 0f);          // Alpha
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
        }

        private static Material MakeMaterial(string name, Color color, float smoothness, float metallic)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.shader == null) mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            mat.color = color;
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material Mat(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{name}.mat");

        // ------------------------------------------------------------------
        // 房间：地板 + 三面墙 + 落地窗
        // ------------------------------------------------------------------

        private static void BuildRoom()
        {
            var root = new GameObject("Room");

            // 木地板
            Box(root.transform, "Floor_Wood",
                new Vector3(0f, -0.05f, 0f),
                new Vector3(RoomWidth, 0.1f, RoomDepth),
                Mat("Home_WoodFloor"));

            // 后墙（Z 正方向，窗在这面）
            Box(root.transform, "Wall_Back",
                new Vector3(0f, WallHeight / 2f, RoomDepth / 2f),
                new Vector3(RoomWidth, WallHeight, 0.12f),
                Mat("Home_WallPaint"));

            // 左墙
            Box(root.transform, "Wall_Left",
                new Vector3(-RoomWidth / 2f, WallHeight / 2f, 0f),
                new Vector3(0.12f, WallHeight, RoomDepth),
                Mat("Home_WallPaint"));

            // 右墙
            Box(root.transform, "Wall_Right",
                new Vector3(RoomWidth / 2f, WallHeight / 2f, 0f),
                new Vector3(0.12f, WallHeight, RoomDepth),
                Mat("Home_WallPaint"));

            // 相机侧的墙省略（开放取景），但要在场景里留一个地面延伸，
            // 避免从俯角看到地板边缘之外是虚空。
            Box(root.transform, "Floor_Front",
                new Vector3(0f, -0.05f, -RoomDepth / 2f - 1.2f),
                new Vector3(RoomWidth + 2.5f, 0.1f, 2.4f),
                Mat("Home_WoodFloor"));

            BuildWindow(root.transform);
            BuildPicture(root.transform);
        }

        /// <summary>木框落地窗：贴在后墙上，占据中部，留出窗台。</summary>
        private static void BuildWindow(Transform parent)
        {
            var win = new GameObject("Wall_Window");
            win.transform.SetParent(parent, false);

            float wallZ = RoomDepth / 2f - 0.02f;
            float winW = 2.9f;
            float winH = WallHeight - WindowBottom - 0.28f;
            float cy = WindowBottom + winH / 2f;

            var frame = Mat("Home_WindowFrame");
            var glass = Mat("Home_WindowGlass");

            // 窗玻璃
            Box(win.transform, "Glass",
                new Vector3(0f, cy, wallZ),
                new Vector3(winW, winH, 0.04f),
                glass);

            // 窗框：外框四条 + 中间十字
            float t = 0.1f;
            Box(win.transform, "Frame_Bottom", new Vector3(0f, WindowBottom, wallZ), new Vector3(winW + t * 2, t, 0.1f), frame);
            Box(win.transform, "Frame_Top", new Vector3(0f, WindowBottom + winH, wallZ), new Vector3(winW + t * 2, t, 0.1f), frame);
            Box(win.transform, "Frame_Left", new Vector3(-winW / 2f, cy, wallZ), new Vector3(t, winH, 0.1f), frame);
            Box(win.transform, "Frame_Right", new Vector3(winW / 2f, cy, wallZ), new Vector3(t, winH, 0.1f), frame);
            Box(win.transform, "Frame_MidV", new Vector3(0f, cy, wallZ), new Vector3(0.06f, winH, 0.08f), frame);
            Box(win.transform, "Frame_MidH", new Vector3(0f, cy, wallZ), new Vector3(winW, 0.06f, 0.08f), frame);

            // 窗台
            Box(win.transform, "Sill", new Vector3(0f, WindowBottom - 0.06f, wallZ - 0.14f),
                new Vector3(winW + 0.3f, 0.12f, 0.34f), frame);
        }

        /// <summary>后墙左侧的挂画，对应展板里的墙上装饰。</summary>
        private static void BuildPicture(Transform parent)
        {
            var pic = new GameObject("Picture_A");
            pic.transform.SetParent(parent, false);
            Box(pic.transform, "Frame",
                new Vector3(-1.85f, 1.42f, RoomDepth / 2f - 0.09f),
                new Vector3(0.40f, 0.53f, 0.04f),
                Mat("Home_PictureFrame"));
            Box(pic.transform, "Canvas",
                new Vector3(-1.85f, 1.42f, RoomDepth / 2f - 0.12f),
                new Vector3(0.32f, 0.45f, 0.025f),
                Mat("Home_PictureArt"));
        }

        // ------------------------------------------------------------------
        // 家具与装饰
        // ------------------------------------------------------------------

        private static void BuildFurniture()
        {
            var root = new GameObject("Furniture");

            // 草地地毯（展板里狗脚下的绿色区域，占据房间中央）
            Box(root.transform, "Floor_Grass",
                new Vector3(0f, 0.012f, 0.25f),
                new Vector3(3.4f, 0.024f, 2.8f),
                Mat("Home_GrassRug"));

            // 石板小路（展板右下角那种浅色石板，斜向穿过草地）
            var stone = Mat("Home_StonePath");
            for (int i = 0; i < 5; i++)
            {
                float t = i / 4f;
                Box(root.transform, $"Path_Stone_{i}",
                    new Vector3(-1.15f + t * 1.9f, 0.032f, -1.05f + t * 0.75f),
                    new Vector3(0.34f, 0.018f, 0.28f),
                    stone);
            }

            // 沙发（展板左侧的蓝色布艺沙发）
            var sofa = new GameObject("Sofa");
            sofa.transform.SetParent(root.transform, false);
            var fabric = Mat("Home_SofaFabric");
            Box(sofa.transform, "Sofa_Base", new Vector3(-1.72f, 0.15f, 1.42f), new Vector3(1.35f, 0.30f, 0.55f), fabric);
            Box(sofa.transform, "Sofa_Back", new Vector3(-1.72f, 0.39f, 1.66f), new Vector3(1.35f, 0.34f, 0.12f), fabric);
            Box(sofa.transform, "Sofa_ArmL", new Vector3(-2.36f, 0.32f, 1.43f), new Vector3(0.11f, 0.20f, 0.54f), fabric);
            Box(sofa.transform, "Sofa_ArmR", new Vector3(-1.08f, 0.32f, 1.43f), new Vector3(0.11f, 0.20f, 0.54f), fabric);

            // 窗台绿植（三盆，对应展板窗台上的绿植）
            var leaf = Mat("Home_PlanterLeaf");
            var pot = Mat("Home_PlanterPot");
            var planters = new[]
            {
                // 三盆分列在窗台两侧，**中轴线（x=0）必须留空** ——
                // 狗在 x=0，任何放中轴的物体都会正好挡在它后面，
                // 玩家只看到狗头从物体后面冒出来。
                // （这个遮挡是从 SceneView 截图里发现的，不是算出来的。）
                (name: "Planter_A", pos: new Vector3(-1.62f, 0.56f, 2.04f), scale: 1.05f),
                (name: "Planter_B", pos: new Vector3(-2.05f, 0.56f, 2.04f), scale: 0.85f),
                (name: "Planter_C", pos: new Vector3(1.66f, 0.56f, 2.04f), scale: 1.15f),
            };
            foreach (var p in planters)
            {
                var pl = new GameObject(p.name);
                pl.transform.SetParent(root.transform, false);
                pl.transform.position = p.pos;
                float s = p.scale;
                Cylinder(pl.transform, "Pot", new Vector3(0f, 0.14f * s, 0f), new Vector3(0.19f * s, 0.14f * s, 0.19f * s), pot);
                Sphere(pl.transform, "Leaf_1", new Vector3(0f, 0.34f * s, 0f), 0.21f * s, leaf);
                Sphere(pl.transform, "Leaf_2", new Vector3(0.12f * s, 0.46f * s, 0.05f), 0.15f * s, leaf);
                Sphere(pl.transform, "Leaf_3", new Vector3(-0.1f * s, 0.44f * s, -0.07f), 0.13f * s, leaf);
            }
        }

        /// <summary>狗脚下那块蓝色小方垫（展板里狗正坐着的那块）。</summary>
        private static void BuildPetMat()
        {
            var go = new GameObject("Mat_Pet");
            go.transform.position = new Vector3(0f, 0.028f, 0.35f);
            // 略大于狗的俯视投影（1.27m 长 × 0.42m 宽），狗站在垫子上
            Box(go.transform, "Mat", Vector3.zero, new Vector3(0.82f, 0.014f, 1.42f), Mat("Home_PetMat"));
        }

        // ------------------------------------------------------------------
        // 相机与灯光
        // ------------------------------------------------------------------

        /// <summary>
        /// 相机：参数见类级常量 CamFov / CamPitch / CamPosition。
        /// 不跟随宠物——主场景是定格的「客厅全景」，狗在范围内活动即可。
        /// </summary>
        private static Camera CreateCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            // 从狗的正前方（-Z）略高处俯视。狗在 (0, 0, 0.4)。
            // 狗在 (0, 0, 0.35)，相机在 -Z 方向略高处俯视。
            // 这组数值下狗 0.76m 高约占画面 32%，接近展板原型的 1/3。
            go.transform.position = CamPosition;
            go.transform.rotation = Quaternion.Euler(CamPitch, 0f, 0f);

            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = CamFov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.82f, 0.80f, 0.76f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 100f;
            go.AddComponent<AudioListener>();

            // 主光：从窗口方向打进来（展板里窗外是主要光源）
            var sunGo = new GameObject("Directional Light");
            sunGo.transform.position = new Vector3(-1.0f, 3.0f, 3.4f);
            sunGo.transform.rotation = Quaternion.Euler(28f, 200f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.97f, 0.90f);
            sun.shadows = LightShadows.Soft;

            // 补光：从相机方向压低阴影，避免狗背光死黑
            var fillGo = new GameObject("Fill Light");
            fillGo.transform.position = new Vector3(0.9f, 1.5f, -2.4f);
            fillGo.transform.rotation = Quaternion.Euler(-12f, 15f, 0f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.42f;
            fill.color = new Color(0.88f, 0.92f, 1f);
            fill.shadows = LightShadows.None;

            return cam;
        }

        private static void CreateInfoPanel()
        {
            var info = new GameObject("SceneInfo");
            var label = info.AddComponent<PetSceneInfo>();
            label.SetText(
                "PET HOME — 主场景（展板第 2 页）\n" +
                "\n" +
                "对照展板还原的空间构成：\n" +
                "  木地板 + 草地地毯 + 蓝色宠物垫 + 石板路\n" +
                "  木框落地窗 + 窗台绿植 + 沙发 + 挂画\n" +
                "\n" +
                $"相机 FOV {CamFov} / 俯角 {CamPitch}°，狗在垫子附近活动\n" +
                "\n" +
                "本阶段未做 UI：\n" +
                "  顶部等级条 / 亲密度 / 设置齿轮\n" +
                "  底部导航（宠物·食品·商店·场景）\n" +
                "  食物与训练快捷入口 / 「来摸摸我呀~」气泡\n" +
                "  （UI 需要另开一轮，见需求文档 3.2）\n" +
                "\n" +
                "调试面板开关：反引号 `");
        }

        // ------------------------------------------------------------------
        // 几何体辅助
        // ------------------------------------------------------------------

        /// <summary>
        /// pos 是**局部坐标**（相对 parent）。
        ///
        /// 三个辅助函数必须统一用局部坐标。历史上这里写的是
        /// SetParent(parent, true) + transform.position = pos（世界坐标），
        /// 而绿植那几行传的是相对 Planter 的局部偏移，
        /// 结果所有叶片和花盆都被摆到世界原点 (0, 0.4, 0)——
        /// 正好压在狗身上（狗在 (0, 0.03, 0.35)）。
        /// 其余调用方因为父节点都在原点、世界坐标 == 局部坐标，才没暴露出来。
        /// </summary>
        private static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            StripCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            ApplyMat(go, mat);
            return go;
        }

        private static GameObject Sphere(Transform parent, string name, Vector3 pos, float radius, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            StripCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * (radius * 2f);
            ApplyMat(go, mat);
            return go;
        }

        private static GameObject Cylinder(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            StripCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            ApplyMat(go, mat);
            return go;
        }

        /// <summary>
        /// 去掉 Unity 自动加的 Collider。装饰物不该挡住触摸射线 ——
        /// 射线只测 Pet 层，但留着多余 Collider 会增加物理开销，
        /// 也可能在将来改了 mask 后误挡。
        /// </summary>
        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
        }

        private static void ApplyMat(GameObject go, Material mat)
        {
            if (mat == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
        }

        private static bool AssetExists<T>(string path) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(path) != null;
    }
}
