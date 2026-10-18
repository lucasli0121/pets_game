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
        private const float BackgroundDistance = 40f;

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

            // ---- 建场景 ----
            var scene = NewSceneInternal();

            var map = PetAnimationBuilder.LoadAnimationMap();
            var pet = PetSceneBuilder.PlaceDog(dogPrefab, out Transform rigRoot);
            pet.SetWanderArea(new Vector2(library.wanderArea.x, library.wanderArea.y), Vector2.zero);

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
            var cam = CreateOrthographicCamera(library, petFootY);

            // 构图迭代校正：正交俯视下「地面线」与 camY 的关系式推导容易出错，
            // 改成实测 —— 移动相机量到地面线对齐为止。
            // 最多迭代 4 次，每次按实测误差的 90% 修正，足够收敛。
            int iter = AlignGroundLine(cam, petFootY, library);
            if (iter < 0)
            {
                Debug.LogError("[PetV1] 地面线对齐失败，已放弃迭代（相机位置可能不准）。");
            }

            // ---- 背景 quad ----
            var bgRenderer = CreateBackgroundQuad(library);

            // ---- 灯光 ----
            var keyLight = CreateKeyLight(library);

            PetSceneBuilder.SetPrivateField(touch, "targetCamera", cam);
            int petLayer = PetSceneBuilder.PetLayerIndex();
            if (petLayer >= 0)
                PetSceneBuilder.SetPrivateField(touch, "petLayers", 1 << petLayer);

            // ---- 身体区域 Collider（Pet 层）----
            // 骨骼名 / 体型 / 活动区全部从 PetModelProfile 读，换模型只改那个 asset。
            PetSceneBuilder.SetupRegionColliders(pet, rigRoot, modelProfile);

            // ---- 椭圆假阴影（PRD 4.5 硬性要求）----
            var shadow = CreateFakeShadow(pet.transform, modelProfile, library, petFootY);

            // ---- 场景控制（切场景 / 改光）----
            var stageGo = new GameObject("PetSceneStage");
            var stage = stageGo.AddComponent<PetSceneStage>();
            // stage 有专门的 Configure 方法，优先用它；
            // 剩下的引用走 SetPrivateField（Unity 不知道对象变脏时 SaveScene 会丢改动）
            stage.Configure(library, pet.transform, keyLight);
            PetSceneBuilder.SetPrivateField(stage, "backgroundRenderer", bgRenderer);
            PetSceneBuilder.SetPrivateField(stage, "groundY", petFootY);
            PetSceneBuilder.SetPrivateField(stage, "currentSceneId",
                library.scenes.Count > 0 ? library.scenes[0].sceneId : "home_day");

            // ---- 行为控制器 ----
            var behavior = pet.gameObject.AddComponent<PetBehaviorController>();
            PetSceneBuilder.WireBehavior(behavior, pet, controller, touch);
            PetSceneBuilder.SetPrivateField(behavior, "rewardConfig",
                Resources.Load<InteractionRewardConfig>("PetV1/DefaultInteractionReward"));

            // ---- 调试面板 ----
            var panelGo = new GameObject("PetDebugPanel");
            var panel = panelGo.AddComponent<PetDebugPanel>();
            PetSceneBuilder.SetPrivateField(panel, "touchInputManager", touch);
            PetSceneBuilder.SetPrivateField(panel, "behaviorController", behavior);

            var infoGo = new GameObject("SceneInfo");
            var info = infoGo.AddComponent<PetSceneInfo>();
            info.SetText(
                $"场景：{library.scenes.Count} 个背景（2D 背景 + 3D 宠物）\n" +
                $"正交相机 orthoSize={library.orthographicSize:F2}" +
                $"（可视高 {library.orthographicSize * 2f:F2} m）\n" +
                $"模型 {modelProfile.bodyWidth:F2}×{modelProfile.bodyHeight:F2}×{modelProfile.bodyLength:F2} m\n" +
                $"活动区 ±{library.wanderArea.x:F2}×±{library.wanderArea.y:F2} m\n" +
                $"背景：{BackgroundDir}/（1080×1920）\n" +
                $"未做 UI（等级条/导航/食物入口），调试用下方面板");

            // ---- 自检 ----
            VerifyComposition(pet, shadow, bgRenderer, cam, library);

            PetSceneBuilder.SaveScene(scene, HomeScenePath);

            Debug.Log("[PetV1] 主场景构建完成（2D 背景 + 3D 宠物）：\n" +
                      $"  {HomeScenePath}\n" +
                      $"  背景库 {SceneLibraryPath}，共 {library.scenes.Count} 个场景\n" +
                      $"  正交相机 orthoSize={library.orthographicSize:F2}（可视高 {library.orthographicSize * 2f:F2}m），" +
                      $"俯角 {library.cameraPitch:F0}°\n" +
                      $"  模型 {modelProfile.bodyWidth:F2} × {modelProfile.bodyHeight:F2} × {modelProfile.bodyLength:F2} m" +
                      $"（{PetAnimationBuilder.ModelProfilePath}）\n" +
                      $"  活动区 ±{library.wanderArea.x:F2} × ±{library.wanderArea.y:F2} m\n" +
                      "  已删除的 3D 房间元素（地板/墙/窗/沙发/绿植/挂画/石板路/蓝垫）\n" +
                      "    —— 全部已画进背景图，建 3D 版既超首包又与背景重复。\n" +
                      "本阶段未做 UI（顶部等级条 / 底部导航 / 食物训练入口 / 气泡提示）。\n" +
                      "打开该场景按 Play 即可看到狗在客厅里活动。");
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

            // 相机与地面参数每次都重置。
            // 原因：改这些数值时如果保留旧值，改了不会生效 ——
            // asset 存在就会跳过创建，而字段初始值只在首次 CreateInstance 时起作用。
            // 这个坑和 ScriptableObject 的 fake null 是同一类问题。
            lib.orthographicSize = 1.9f;
            lib.cameraPitch = 12f;
            lib.cameraDistance = 3.2f;
            lib.groundLine = 0.42f;
            lib.wanderArea = new Vector2(1.5f, 0.9f);

            // 场景 id 与后台 config_scene 一致，价格与后台文档 13.4 对齐。
            var defs = new[]
            {
                new
                {
                    id = "home_day", key = "scene_home_day", price = 0, unlockable = false,
                    file = "scene_home_day.jpg",
                    light = new Color(1f, 0.97f, 0.90f),
                    ambient = 1.15f, shadow = 0.32f,
                    note = "室内白天，光从左上的窗进来"
                },
                new
                {
                    id = "home_night", key = "scene_home_night", price = 100, unlockable = true,
                    file = "scene_home_night.jpg",
                    light = new Color(1f, 0.82f, 0.60f),
                    ambient = 0.42f, shadow = 0.22f,
                    note = "室内夜晚，暖黄台灯；宠物亮度必须跟着压暗，否则像贴纸"
                },
                new
                {
                    id = "park_day", key = "scene_park_day", price = 100, unlockable = true,
                    file = "scene_park_day.jpg",
                    light = new Color(1f, 1f, 0.98f),
                    ambient = 1.30f, shadow = 0.28f,
                    note = "公园白天，草地反光强，环境光可以更高"
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

                lib.scenes.Add(new PetSceneLibrary.SceneEntry
                {
                    sceneId = d.id,
                    displayNameKey = d.key,
                    background = tex,
                    price = d.price,
                    unlockable = d.unlockable,
                    // 三张背景图光源都在左上，方向统一；颜色/强度按场景调
                    lightEuler = new Vector3(42f, -35f, 0f),
                    lightColor = d.light,
                    ambientIntensity = d.ambient,
                    shadowOpacity = d.shadow,
                });
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
        private static Camera CreateOrthographicCamera(PetSceneLibrary lib, float groundWorldY)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            // 相机高度由地面线反推，保证狗脚精确落在背景的地面线上。
            // 见PetSceneLibrary.ComputeCameraPosition 的注释。
            go.transform.position = lib.ComputeCameraPosition(groundWorldY);
            go.transform.rotation = Quaternion.Euler(-lib.cameraPitch, 0f, 0f);

            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;                       // 正交，不用 FOV
            cam.orthographicSize = lib.orthographicSize;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f);
            cam.cullingMask = ~0;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = BackgroundDistance + 10f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            Debug.Log($"[PetV1] 正交相机：orthoSize={lib.orthographicSize:F2}" +
                      $"（可视高 {lib.orthographicSize * 2f:F2}m），俯角 {lib.cameraPitch:F0}°，" +
                      $"位置 {go.transform.position}，地面线 {lib.groundLine:P0}，" +
                      $"狗脚 y={groundWorldY:F3}");
            return cam;
        }

        /// <summary>
        /// 量宠物脚底的实际世界 Y。
        /// 用 Renderer bounds.min.y 而不是 transform.position.y ——
        /// 模型原点通常在身体中心或略微下沉，不是脚底。
        /// </summary>
        private static float MeasureFootY(PetController pet)
        {
            var renderers = pet.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return GroundWorldY;

            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b.min.y;
        }

        /// <summary>
        /// 迭代校正相机高度，让地面精确落在 <c>groundLine</c>。
        ///
        /// 为什么不用闭式公式：正交俯视相机里，地面在画面中的位置受
        /// 俯角、正交尺寸、模型原点三者影响，闭式解很容易漏项
        ///（第一版就因为漏了 cos(pitch)，狗脚落在画面 11% 而目标是 42%）。
        /// 实测校正没有这个风险 —— 直接量 WorldToViewportPoint 的结果。
        ///
        /// 收敛：每步按误差的 90% 修正，残余误差按 0.1 倍递减，
        /// 4 次后为初始的 1e-4，对画面完全不可见。
        /// 返回迭代次数，-1 表示不收敛。
        /// </summary>
        private static int AlignGroundLine(Camera cam, float footWorldY, PetSceneLibrary lib)
        {
            // 地面判定点：狗的正下方，靠近画面中心
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
                float err = lib.groundLine - actual;
                if (Mathf.Abs(err) <= tolerance)
                {
                    cam.transform.position = pos;
                    Debug.Log($"[PetV1] 地面线对齐：{i - 1} 次迭代收敛，" +
                              $"地面在画面 {actual:P1}（目标 {lib.groundLine:P0}），" +
                              $"相机 Y={pos.y:F3}");
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
                    Debug.LogError("[PetV1] 地面线对齐失败：相机高度对地面位置无影响" +
                                   "（slope≈0），检查是否真用了正交投影。");
                    return -1;
                }

                pos.y = currentY + err / slope * 0.9f;
            }


            cam.transform.position = pos;
            float finalV = cam.WorldToViewportPoint(probe).y;
            Debug.LogWarning($"[PetV1] 地面线对齐：{maxIter} 次迭代后仍未完全收敛，" +
                             $"地面在画面 {finalV:P1}（目标 {lib.groundLine:P0}），建议看截图确认。");
            return maxIter;
        }

        /// <summary>
        /// 背景 quad。Unlit 材质不吃场景光，
        /// 这样换场景时只改贴图与灯光，背景本身始终保持原图亮度。
        /// </summary>
        private static MeshRenderer CreateBackgroundQuad(PetSceneLibrary lib)
        {
            var go = new GameObject("SceneBackground");

            // quad 要与正交画面的宽高比一致。
            // 正交可视区：宽 = orthoSize*2*aspect，高 = orthoSize*2。
            var cam = Object.FindAnyObjectByType<Camera>();
            float aspect = cam != null ? cam.aspect : 9f / 16f;
            float viewH = lib.orthographicSize * 2f;
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
                      $"正交视野 {viewW:F2} × {viewH:F2} m（比例 {aspect:F3}）" +
                      (Mathf.Abs(aspect - imgAspect) > 0.05f
                          ? "—— **视野比例与背景不一致，当前视图会裁切左右**"
                          : "（比例匹配）"));

            go.transform.position = new Vector3(0f, 0f, BackgroundDistance);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // 面向相机（相机在 -Z 看 +Z）

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildQuadMesh();

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetBackgroundMaterial();
            // 构建时就贴上当前场景的背景图。
            // PetSceneStage.Start() 只在 Play 模式跑，编辑器里不触发 ——
            // 不在这里贴的话，编辑器 SceneView 里背景是纯白的，看不出构图对不对。
            var first = lib.scenes.Count > 0 ? lib.scenes[0] : null;
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

            return mr;
        }

        /// <summary>
        /// 主光。方向必须与背景图光源一致（PRD：统一左上），
        /// 否则宠物身上会有与背景矛盾的第二套影子。
        /// </summary>
        private static Light CreateKeyLight(PetSceneLibrary lib)
        {
            var entry = lib.scenes.Count > 0 ? lib.scenes[0] : null;

            var go = new GameObject("Key Light");
            go.transform.rotation = Quaternion.Euler(
                entry != null ? entry.lightEuler : new Vector3(42f, -35f, 0f));

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;   // 背景不吃阴影，投影没有接收面
            light.color = entry != null ? entry.lightColor : Color.white;
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

            if (entry != null) RenderSettings.ambientIntensity = entry.ambientIntensity;
            return light;
        }

        /// <summary>
        /// 椭圆半透明假阴影。PRD 4.5 明确要求 ——
        /// 2D 背景上的阴影是画出来的，3D 宠物没有接收阴影的地面，
        /// 没有这个假阴影玩家会觉得狗浮在半空。
        /// </summary>
        private static PetFakeShadow CreateFakeShadow(Transform pet, PetModelProfile profile,
                                                      PetSceneLibrary lib, float groundY)
        {
            var go = new GameObject("PetFakeShadow");
            go.transform.SetParent(pet, false);

            var shadow = go.AddComponent<PetFakeShadow>();
            shadow.FitToBody(profile.bodyWidth, profile.bodyLength);
            var entry = lib.scenes.Count > 0 ? lib.scenes[0] : null;
            shadow.Configure(entry != null ? entry.shadowOpacity : 0.32f);
            shadow.Attach(pet, groundY);

            Debug.Log($"[PetV1] 假阴影已创建：{profile.bodyWidth * 1.35f:F2} × " +
                      $"{profile.bodyLength * 0.55f:F2} m（按体型自动算）");
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
                                             MeshRenderer bg, Camera cam, PetSceneLibrary lib)
        {
            var renderer = pet.GetComponentInChildren<Renderer>(true);
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
                               "建议 12%~25%。调整 PetSceneLibrary.orthographicSize。");
            }

            // ② 脚要落在地面线附近（正交下地面是固定高度）
            float footViewportY = bottom.y;
            float expected = lib.groundLine;
            if (Mathf.Abs(footViewportY - expected) > 0.12f)
            {
                Debug.LogWarning($"[PetV1] 狗脚在画面 {footViewportY:P1} 高度，" +
                                 $"背景地面线约在 {expected:P1}。狗会显得悬空或陷进地板。");
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
                      $"脚在 {footViewportY:P1}（地面线 {expected:P0}），" +
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

        /// <summary>1×1 quad，法线朝 +Z（配合旋转面向相机）。</summary>
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
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateBounds();
            return m;
        }

        private static Scene NewSceneInternal() => PetSceneBuilder.NewScene(HomeScenePath);

        private static bool AssetExists<T>(string path) where T : UnityEngine.Object
            => AssetDatabase.LoadAssetAtPath<T>(path) != null;
    }
}