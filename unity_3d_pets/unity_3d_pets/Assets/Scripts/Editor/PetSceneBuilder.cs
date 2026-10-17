using System;
using System.Collections.Generic;
using System.IO;
using PetV1.Animation;
using PetV1.Behavior;
using PetV1.Core;
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
    /// 场景构建器。需求文档第十八节要求三个场景：
    ///   Scene 1ModelTest      —— 只验证 Model / Rig / Material / Animation
    ///   Scene 2 PetPrototype  —— 核心开发场景（Pet / Camera / Floor / Touch / Behavior / Debug UI）
    ///   Scene 3 PerformanceTest —— FPS / Memory / Rendering
    ///
    /// 全部用代码程序化生成，避免手工拖拽导致的引用丢失，也便于 CI 复现。
    /// 执行方式：菜单 Pet V1 / 2. Build Scenes
    /// </summary>
    public static class PetSceneBuilder
    {
        public const string ScenesDir = "Assets/Scenes/PetPrototype";
        public const string ModelTestScenePath = ScenesDir + "/ModelTest.unity";
        public const string PrototypeScenePath = ScenesDir + "/PetPrototype.unity";
        public const string PerfTestScenePath = ScenesDir + "/PerformanceTest.unity";

        internal const string DogPrefabPath = "Assets/ithappy/Animals_FREE/Prefabs/Dog_001.prefab";
        private const string FloorMaterialPath = "Assets/Scenes/PetPrototype/Materials/PetFloor.mat";

        internal const float DogHeight = 0.5f;      // 狗模型在原型中的世界高度
        /// <summary>
        /// 只判断资产是否存在，不持有对象引用。
        /// 用于规避 Unity fake null：AssetDatabase 的写操作会销毁并重建
        /// ScriptableObject 的 native 实例，跨过这类操作传递对象引用必然拿到死对象。
        /// </summary>
        private static bool AssetContains<T>(string assetPath) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(assetPath) != null
                   || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null;
        }

        [MenuItem("Pet V1/2. Build Scenes", false, 2)]
        public static void BuildAllScenes()
        {
            // 只在这里校验文件在不在，**不要把 map 对象一路传下去**。
            //
            // 原因（Unity fake null）：AssetDatabase.CreateFolder / CreateAsset
            // （下面的 EnsureFolder、BuildFloorMaterial 都会调）会触发资产重新导入，
            // Unity 在这个过程中会销毁并重建 ScriptableObject 的 native 实例。
            // C# 侧的引用还在（`is null` 为 false），但 `== null` 判断为 true。
            // 于是：
            //   - 开头 `map == null` 检查能通过（假 null 骗过了它）
            //   - 但 `map ?? fallback` 不会触发（C# 层面它不是真 null）
            //   - 到 WireOrchestrator 时 map 已经是死对象，写进场景就是空的
            // 所以每个场景构建时重新加载一次，而不是传递对象引用。
            if (!AssetContains<PetAnimationMap>(PetAnimationBuilder.AnimationMapPath))
            {
                Debug.LogError("[PetV1] 未找到 AnimationMap，请先执行 Pet V1 / 1. Build Animation Assets");
                return;
            }

            PetAnimationBuilder.EnsureFolder("Assets/Scenes", "PetPrototype");
            PetAnimationBuilder.EnsureFolder("Assets/Scenes/PetPrototype", "Materials");

            // 所有会写 AssetDatabase 的操作必须集中在接线之前。
            // CreateAsset / SaveAssets 会触发资产重导入，销毁并重建 ScriptableObject 的
            // native 实例（Unity fake null），之后再写入的 animationMap 引用会变成死对象。
            BuildFloorMaterial();
            CreateInvisibleMaterial();
            PrepareWallsLayer();

            var dogPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DogPrefabPath);
            if (dogPrefab == null)
            {
                Debug.LogError($"[PetV1] 未找到 Dog Prefab：{DogPrefabPath}");
                return;
            }

            BuildModelTestScene(dogPrefab);
            // 每个场景之间清缓存：NewScene 会销毁上一批对象，
            // 留着旧 SerializedObject 只会让 instanceID 复用时写到错误对象上。
            ClearSerializedCache();
            BuildPrototypeScene(dogPrefab);
            ClearSerializedCache();
            BuildPerformanceScene(dogPrefab);
            ClearSerializedCache();
            // 主场景放在最后：它自己会重建一套材质（Home_*），不影响前三个。
            PetHomeSceneBuilder.BuildHomeScene();
            ClearSerializedCache();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[PetV1] 四个场景构建完成：\n" +
                      $"  {PetHomeSceneBuilder.HomeScenePath}   <- 主场景（展板第 2 页）\n" +
                      $"  {PrototypeScenePath}\n  {PerfTestScenePath}\n  {ModelTestScenePath}\n" +
                      "打开 PetHome 场景即可看到狗在客厅里活动。");
        }

        // ============================================================
        // Scene 1: ModelTest
        // ============================================================
        private static void BuildModelTestScene(GameObject dogPrefab)
        {
            var scene = NewScene(ModelTestScenePath);

            // 每个场景内部重新加载：见 BuildAllScenes 注释里的 Unity fake null 说明
            var map = PetAnimationBuilder.LoadAnimationMap();
            var pet = PlaceDog(dogPrefab, out _);
            var controller = CreateController(pet.gameObject);
            WireOrchestrator(controller, pet.gameObject, map, "ModelTest");
            CreateCamera(new Vector3(0f, DogHeight + 0.6f, -3.2f), Quaternion.Euler(6f, 0f, 0f), 45f, pet.transform);
            CreateFloor();
            CreateLights();

            // ModelTest 场景不跑行为系统，只看动画能不能播：自动循环演示 Idle/Walk/Run
            var demo = pet.gameObject.AddComponent<PetModelTestDemo>();
            demo.Configure(controller, pet);

            CreateInfoPanel(scene, "MODEL TEST\n\n" +
                "只验证：Model / Rig / Material / Animation\n\n" +
                "自动循环演示 Idle -> Walk -> Run\n" +
                "（等待切回 Idle 后按空格可手动测试下一个 Clip）\n\n" +
                $"AnimationMap: {map.modelId}\n" +
                $"Capabilities: {map.CapabilitySummary()}");

            SaveScene(scene, ModelTestScenePath);
        }

        // ============================================================
        // Scene 2: PetPrototype
        // ============================================================
        private static void BuildPrototypeScene(GameObject dogPrefab)
        {
            var scene = NewScene(PrototypeScenePath);

            var map = PetAnimationBuilder.LoadAnimationMap();
            var pet = PlaceDog(dogPrefab, out Transform rigRoot);
            var controller = CreateController(pet.gameObject);
            WireOrchestrator(controller, pet.gameObject, map, "PetPrototype");
            CreateCamera(new Vector3(0f, DogHeight + 1.4f, -4.2f), Quaternion.Euler(12f, 0f, 0f), 42f, pet.transform);
            CreateFloor();
            CreateLights();
            int wallsLayer = PrepareWallsLayer();

            // ---- 触摸输入 ----
            var touchGo = new GameObject("TouchInputManager");
            var touch = touchGo.AddComponent<TouchInputManager>();
            // 触摸射线要相机做 ScreenPointToRay。留空时代码会 fallback 到 Camera.main，
            // 但显式写入更可靠（避免主相机被换掉后静默失效）。
            SetPrivateField(touch, "targetCamera", Camera.main);
            // 射线只测 Pet 层：狗身上的 Collider 都在这一层，墙和地板不参与。
            int petLayerIdx = PetLayerIndex();
            if (petLayerIdx >= 0) SetPrivateField(touch, "petLayers", 1 << petLayerIdx);

            // ---- 身体区域 Collider ----
            //需求文档 6.1：先建整体 Pet Collider，再按骨骼能力拆分区。
            // Animals FREE 的骨骼里 scull 是头、spine 链是背、foot/toe 是腿。
            SetupRegionColliders(pet, rigRoot);

            // 建墙放在接线之后：CreateWanderWalls 只创建 GameObject，不碰 AssetDatabase，
            // 不会破坏刚写入的 animationMap 引用。
            CreateWanderWalls(wallsLayer);

            // 把避障层写进 PetController，宠物才会在墙前提前转向。
            // 不做这一步的话 obstacleMask 为空，射线避障整个失效（只剩矩形边界钳制）。
            ConfigureObstacleMask(pet, wallsLayer);

            // ---- 行为控制器 ----
            var behavior = pet.gameObject.AddComponent<PetBehaviorController>();
            WireBehavior(behavior, pet, controller, touch);

            // ---- Debug 面板 ----
            var debugGo = new GameObject("PetDebugPanel");
            var panel = debugGo.AddComponent<PetDebugPanel>();
            SetPrivateField(panel, "behaviorController", behavior);
            SetPrivateField(panel, "animationOrchestrator", controller);
            SetPrivateField(panel, "touchInputManager", touch);

            CreateInfoPanel(scene, "PET PROTOTYPE\n\n" +
                "验证两条主链路：\n" +
                "  1)触摸 -> Gesture -> Emotion -> Personality -> Behavior -> Animation\n" +
                "  2) 无操作 -> Autonomous Behavior -> 宠物自主行动\n\n" +
                "鼠标点击= 单指 Tap；按住拖动 = Stroke；\n" +
                "真实多指请在手机上测试（编辑器无法模拟多指）。\n" +
                "面板开关：反引号 `");

            SaveScene(scene, PrototypeScenePath);
        }

        // ============================================================
        // Scene 3: PerformanceTest
        // ============================================================
        private static void BuildPerformanceScene(GameObject dogPrefab)
        {
            var scene = NewScene(PerfTestScenePath);

            var map = PetAnimationBuilder.LoadAnimationMap();
            var pet = PlaceDog(dogPrefab, out Transform rigRoot);
            var controller = CreateController(pet.gameObject);
            WireOrchestrator(controller, pet.gameObject, map, "PerformanceTest");
            CreateCamera(new Vector3(0f, DogHeight + 1.2f, -4.5f), Quaternion.Euler(10f, 0f, 0f), 45f, pet.transform);

            // 性能场景也建 TouchInputManager：面板的五个手势按钮直接用它注入合成事件，
            // 引用为空时按钮是静默失效的（代码里 `&& touchInputManager != null` 短路）。
            var touchGo = new GameObject("TouchInputManager");
            var touch = touchGo.AddComponent<TouchInputManager>();
            SetPrivateField(touch, "targetCamera", Camera.main);
            int perfPetLayer = PetLayerIndex();
            if (perfPetLayer >= 0) SetPrivateField(touch, "petLayers", 1 << perfPetLayer);
            CreateFloor();
            CreateLights();
            int perfWallsLayer = PrepareWallsLayer();
            CreateWanderWalls(perfWallsLayer);
            SetupRegionColliders(pet, rigRoot);
            ConfigureObstacleMask(pet, perfWallsLayer);

            // 性能场景不要触摸输入（避免手势计算污染帧率测量），但保留自主行为，
            // 因为「持续移动」才是移动端性能的真实负载。
            var behavior = pet.gameObject.AddComponent<PetBehaviorController>();
            // 统一走 WireBehavior：这里原来手写了一套，漏掉了 personalityProfile，
            // 导致性能场景的宠物没有性格配置（BehaviorController.Awake 会自己造默认）。
            WireBehavior(behavior, pet, controller, touch);

            var probe = pet.gameObject.AddComponent<PetPerformanceProbe>();
            probe.Configure(true);

            var debugGo = new GameObject("PetDebugPanel");
            var panel = debugGo.AddComponent<PetDebugPanel>();
            SetPrivateField(panel, "behaviorController", behavior);
            SetPrivateField(panel, "animationOrchestrator", controller);
            SetPrivateField(panel, "touchInputManager", touch);

            CreateInfoPanel(scene, "PERFORMANCE TEST\n\n" +
                "需求文档第十七节的目标：稳定 60 FPS（低端机最低 30）\n\n" +
                "记录项：FPS / DrawCalls / Batches / Triangles / Memory\n" +
                "面板开关：反引号 `\n\n" +
                "建议分别在 PC、Mobile 平台构建后用真机测量。");

            SaveScene(scene, PerfTestScenePath);
        }

        // ============================================================
        // 场景组件构建
        // ============================================================

        internal static Scene NewScene(string path)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            //清掉默认的空对象，改为显式创建，避免重复命名
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "Main Camera" || root.name == "Directional Light")
                    UnityEngine.Object.DestroyImmediate(root);
            }
            return scene;
        }

        /// <summary>
        /// 在宠物本体上创建 AnimationOrchestrator。
        ///
        /// 为什么挂在宠物身上而不是独立的 "PetRuntime" 空物体：
        ///挂在 new GameObject() 上的组件，对它做 SerializedObject 引用写入
        /// （animationMap / animator）会被静默丢弃—— 字段在Inspector 里看着设了，
        /// 但场景文件序列化出来是 {fileID: 0}。而挂在已落盘的 prefab 实例上的组件
        /// （PetBehaviorController 就是）写入完全正常。
        ///
        /// 挂在宠物身上同时还消除了一个真实 bug：原方案下 Animator 在狗 prefab 内部、
        /// Orchestrator 在另一个物体上，两者不是父子关系，
        /// GetComponentInChildren 找不到 Animator -> Speed 参数写不进去 -> 宠物滑行。
        /// 同物体后 GetComponentInChildren 一定能找到。
        /// </summary>
        internal static AnimationOrchestrator CreateController(GameObject petInstance)
        {
            if (petInstance == null) return null;
            var controller = petInstance.GetComponent<AnimationOrchestrator>();
            if (controller == null)
                controller = petInstance.AddComponent<AnimationOrchestrator>();
            Undo.RegisterCreatedObjectUndo(controller, "Create AnimationOrchestrator");
            return controller;
        }

        /// <summary>
        /// 把 Animator 与 AnimationMap 一起接到 AnimationOrchestrator 上。
        ///
        /// 两个坑（都踩过）：
        /// 1. Animator 之前是空的：Orchestrator 挂在场景的 "PetRuntime" 空物体上，
        ///    Animator 在狗 prefab 内部，两者不是父子关系，Awake 的
        ///    GetComponentInChildren 找不到 -> Speed 参数永远写不进去 ->
        ///    动画停在 Idle -> 宠物「四肢不动地滑行」，交互看起来也没反应。
        /// 2. 必须两个字段一起写、分两次写会丢：PetRuntime 是本帧新建的物体，
        ///    分两次 SetPrivateField 时后一次会覆盖前一次的结果，
        ///    最终 animationMap 序列化成 {fileID: 0}。
        ///    所以这里合并成一次调用，并且放在 PlaceDog 之后（Animator 存在之后）。
        /// </summary>
        internal static void WireOrchestrator(AnimationOrchestrator controller, GameObject petInstance,
            PetAnimationMap map, string sceneLabel)
        {
            if (controller == null) return;

            var animator = petInstance != null ? petInstance.GetComponentInChildren<Animator>(true) : null;
            if (animator == null)
                Debug.LogError($"[PetV1] 狗实例上找不到 Animator，动画无法驱动。（{sceneLabel}）");

            var finalMap = map ?? PetAnimationBuilder.LoadAnimationMap();

            // 两个字段都用 SetPrivateField 写入 —— 本工程唯一被验证可靠的路径
            // （PetBehaviorController 的 petController / animationMap / touchInputManager
            // 都靠它正确序列化进了场景文件）。
            SetPrivateField(controller, "animator", animator);
            SetPrivateField(controller, "animationMap", finalMap);

            // 复核：用独立的 SerializedObject 读回，确认真的写进去了
            var verify = new SerializedObject(controller);
            bool animOk = verify.FindProperty("animator")?.objectReferenceValue != null;
            bool mapOk = verify.FindProperty("animationMap")?.objectReferenceValue != null;
            Debug.Log($"[PetV1] Orchestrator 接线复核：animator={(animOk ? "OK" : "空")}, " +
                      $"animationMap={(mapOk ? "OK" : "空")}");

            if (!animOk || !mapOk)
            {
                Debug.LogError($"[PetV1] 接线仍不完整，动画会退化为滑行。" +
                               $"animator={(animator != null ? animator.name : "null")}, " +
                               $"map={(finalMap != null ? finalMap.modelId : "null")}");
            }
            else
            {
                // 场景保存前显式标脏，避免改动只存在于内存
                EditorUtility.SetDirty(controller);
            }
        }

        internal static PetController PlaceDog(GameObject dogPrefab, out Transform rigRoot)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(dogPrefab);
            instance.name = "Pet_Dog_001";
            instance.transform.position = new Vector3(0f, 0f, 0f);
            instance.transform.rotation = Quaternion.identity;

            // ---- 剥离资产商自带的 demo 控制器 ----
            // Dog_001.prefab 上挂着 ithappy.Animals_FREE.MovePlayerInput / CreatureMover，
            // 它们是给Demo 场景用的：MovePlayerInput.Update 每帧调Input.GetAxis / GetKey / GetButton，
            // 而本工程 activeInputHandler=1（仅新 Input System），调用旧版 Input 会直接抛
            // InvalidOperationException，且每帧抛一次，日志刷屏且宠物无法被正常驱动。
            // 我们的移动由 PetController 全权负责，所以这里直接销毁它们。
            //注意：不能去改资产里的 prefab，本项目所有模型都要能正常复用。
            RemoveAssetDemoComponents(instance);

            // 模型是 Generic（无 Avatar），动画走 Transform 曲线，必须关掉 Root Motion，
            // 否则宠物会被动画里Root 骨骼的位移推着走，与 PetController 的移动打架。
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
            {
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    PetAnimationBuilder.ControllerPath);
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            rigRoot = instance.transform;

            var pet = instance.AddComponent<PetController>();
            // 活动区半尺寸（米）。相机 FOV 42-45、距离约 4.2m 时，
            // ±2.2m 能完整落在画面内；再大宠物就会走出画面（用户实测问题 5）。
            pet.SetWanderArea(new Vector2(2.2f, 2.2f), Vector2.zero);

            return pet;
        }

        /// <summary>
        /// 移除 ithappy.Animals_FREE 自带的输入/移动组件。
        /// 用类型名匹配而不是写死程序集名，这样换别的资产包也能复用。
        /// </summary>
        private static void RemoveAssetDemoComponents(GameObject instance)
        {
            // 销毁顺序必须遵循 [RequireComponent] 依赖方向：先销毁依赖方，再销毁被依赖方。
            //
            // MovePlayerInput 声明了 [RequireComponent(typeof(CreatureMover))]，所以
            // CreatureMover 被依赖，必须最后销毁。反过来先销毁 CreatureMover 会被 Unity 拒绝：
            //   "Can't remove CreatureMover (Script) because MovePlayerInput (Script) depends on it"
            //
            // GetComponentsInChildren 的返回顺序不保证依赖关系，所以按下面的显式顺序处理。
            // 新增资产脚本时要确认它有没有 [RequireComponent]，有就往依赖链上游排。
            var destroyOrder = new[]
            {
                "MovePlayerInput",     // 依赖 CreatureMover，必须先走
                "ThirdPersonCamera",   // 依赖 PlayerCamera，先走
                "PlayerCamera",
                "CreatureMover",       // 被 MovePlayerInput 依赖，最后走
            };

            var doomed = new List<Component>();
            foreach (var component in instance.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                string ns = component.GetType().Namespace ?? "";
                if (!ns.StartsWith("ithappy.Animals_FREE", StringComparison.Ordinal)) continue;

                string typeName = component.GetType().Name;
                if (Array.IndexOf(destroyOrder, typeName) >= 0) doomed.Add(component);
            }

            // 先全部禁用再销毁：避免某个组件在 OnDisable/OnDestroy 里访问到已被销毁的兄弟组件。
            foreach (var component in doomed)
            {
                if (component is Behaviour behaviour) behaviour.enabled = false;
            }

            foreach (string typeName in destroyOrder)
            {
                foreach (var component in doomed)
                {
                    if (component == null || component.GetType().Name != typeName) continue;
                    Debug.Log($"[PetV1] 移除资产自带 demo 组件：{component.GetType().FullName}");
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }

            RemoveLeftoverRequiredComponents(instance);
        }

        /// <summary>
        /// 清掉 [RequireComponent] 连带生成、但不属于资产脚本的残留组件。
        /// Animator 不动——它是我们要用的。
        /// </summary>
        private static void RemoveLeftoverRequiredComponents(GameObject instance)
        {
            foreach (var child in instance.GetComponentsInChildren<Transform>(true))
            {
                foreach (var component in child.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (component is Animator or Transform) continue;

                    // CharacterController 是 CreatureMover 的 [RequireComponent] 连带产物，
                    // 销毁 CreatureMover 后它会留下。它会跟我们的 PetController
                    // 抢移动控制权（位移冲突），必须一并移除。
                    if (component.GetType().Name == "CharacterController")
                    {
                        Debug.Log($"[PetV1] 移除资产 RequireComponent 残留：{component.GetType().Name}");
                        UnityEngine.Object.DestroyImmediate(component);
                    }
                }
            }
        }

        /// <summary>
        /// 建立身体区域 Collider（需求文档 6.1）。
        /// 整体一个 Capsule 作为 Body，再按骨骼加头部与背部球体，
        /// 让射线能区分 HEAD / BODY。腿部先不做，等真实模型骨骼确认后再加。
        /// </summary>
        internal static void SetupRegionColliders(PetController pet, Transform rigRoot)
        {
            // 宠物碰撞体放独立的 Pet 层，触摸射线只测这一层。
            // 全放 Default 层时射线（mask=~0）会先打到墙 / 地板，
            // 导致「点到墙上也算点到了狗」。
            int petLayer = PetLayerIndex();
            if (petLayer < 0) petLayer = 0;

            // 整体身体
            var body = new GameObject("Collider_Body");
            body.layer = petLayer;
            body.transform.SetParent(rigRoot, false);
            body.transform.localPosition = new Vector3(0f, DogHeight * 0.55f, 0f);
            var bodyCollider = body.AddComponent<CapsuleCollider>();
            bodyCollider.direction = 2;              // 沿 Z轴
            bodyCollider.radius = 0.22f;
            bodyCollider.height = 0.75f;
            bodyCollider.center = Vector3.zero;
            var bodyTag = body.AddComponent<PetRegionCollider>();
            SetPrivateField(bodyTag, "region", PetRegion.Body);

            // 头部：跟着 scull 骨骼走
            Transform skull = FindDeep(rigRoot, "scull");
            if (skull != null)
            {
                var head = new GameObject("Collider_Head");
                head.layer = petLayer;
                head.transform.SetParent(skull, false);
                head.transform.localPosition = new Vector3(0f, 0.05f, 0.02f);
                var headCollider = head.AddComponent<SphereCollider>();
                headCollider.radius = 0.14f;
                var headTag = head.AddComponent<PetRegionCollider>();
                SetPrivateField(headTag, "region", PetRegion.Head);
            }
            else
            {
                // 找不到 scull 就退回整体 Body，保证触摸仍然可用
                Debug.LogWarning("[PetV1] 未找到 scull 骨骼，头部 Collider 退回 Body。");
            }

            // 背部：spine.004 附近
            Transform spine = FindDeep(rigRoot, "spine.004");
            if (spine != null)
            {
                var back = new GameObject("Collider_Back");
                back.layer = petLayer;
                back.transform.SetParent(spine, false);
                var backCollider = back.AddComponent<BoxCollider>();
                backCollider.size = new Vector3(0.28f, 0.12f, 0.5f);
                backCollider.center = new Vector3(0f, 0.14f, 0f);
                var backTag = back.AddComponent<PetRegionCollider>();
                SetPrivateField(backTag, "region", PetRegion.Back);
            }

            _ = pet;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }

        internal static void WireBehavior(PetBehaviorController behavior, PetController pet,
            AnimationOrchestrator controller, TouchInputManager touch)
        {
            SetPrivateField(behavior, "petController", pet);
            SetPrivateField(behavior, "animationOrchestrator", controller);
            SetPrivateField(behavior, "touchInputManager", touch);
            // 用 PetAnimationBuilder 的可靠加载（非泛型 + as 转换 + 必要时重建），
            // 不要直接AssetDatabase.LoadAssetAtPath<T>：AssetDatabase 写操作之后
            // 泛型加载可能返回 null（Unity fake null）。
            SetPrivateField(behavior, "animationMap", PetAnimationBuilder.LoadAnimationMap());
            SetPrivateField(behavior, "personalityProfile", PetAnimationBuilder.LoadPersonalityProfile());
            if (behavior != null) EditorUtility.SetDirty(behavior);
            SetPrivateField(behavior, "randomizeIndividual", true);
            SetPrivateField(behavior, "autonomyEnabled", true);
        }

        /// <summary>
        /// 创建相机并挂上跟随。
        /// 跟随是必需的：活动区是 ±WanderHalf 米，固定相机必然会让宠物走出画面
        /// （需求文档 8.2「不能穿过摄像机或走出场景」）。
        /// </summary>
        internal static Camera CreateCamera(Vector3 position, Quaternion rotation, float fov, Transform followTarget)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetPositionAndRotation(position, rotation);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.20f, 0.24f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            camGo.AddComponent<AudioListener>();

            if (followTarget != null)
            {
                var follow = camGo.AddComponent<PetCameraFollow>();
                // offset 是「相机相对宠物」的位移，期望 = 相机位置 - 宠物位置。
                // 写成 followTarget.position - position 会把相机放到宠物对面（反了）。
                follow.Configure(followTarget, position - followTarget.position);
            }

            return cam;
        }

        internal static void CreateLights()
        {
            var dirGo = new GameObject("Directional Light");
            dirGo.transform.SetPositionAndRotation(new Vector3(0f, 5f, -2f), Quaternion.Euler(50f, -30f, 0f));
            var dir = dirGo.AddComponent<Light>();
            dir.type = LightType.Directional;
            dir.intensity = 1.35f;
            dir.color = new Color(1f, 0.96f, 0.90f);
            dir.shadows = LightShadows.Soft;

            var fillGo = new GameObject("Fill Light");
            fillGo.transform.SetPositionAndRotation(new Vector3(0f, 3f, 3f), Quaternion.Euler(20f, 150f, 0f));
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fill.color = new Color(0.80f, 0.86f, 1f);
            fill.shadows = LightShadows.None;
        }

        private static void CreateFloor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            // Plane 原生 10m。墙体半尺寸 3.0m -> 围墙 6m，
            // 地板给到 8m（scale 0.8）就能覆盖活动区并留一点边缘，
            // 不再是原来 scale=3 的 30m —— 那比围墙大5 倍，视觉上很怪。
            floor.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            var renderer = floor.GetComponent<MeshRenderer>();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterialPath);
            if (mat != null) renderer.sharedMaterial = mat;
        }

        /// <summary>
        /// 确保避障层存在。这个函数会调 AssetDatabase.SaveAssets()，
        /// 会销毁并重建 ScriptableObject 的 native 实例（Unity fake null），
        /// 因此**必须在场景接线（WireOrchestrator）之前调用**。
        /// </summary>
        private static int PrepareWallsLayer()
        {
            // Walls 占第 8 层，Pet 占第 9 层。两个都要在接线之前建好。
            EnsureLayerExists(WallsLayerName, 8);
            EnsureLayerExists(PetLayerName, 9);
            return LayerMask.NameToLayer(WallsLayerName);
        }

        /// <summary>
        /// 宠物碰撞层。给狗单独一层，触摸射线只测这一层，
        /// 就不会打到墙 / 地板上（原来全在Default 层，射线用~0 会先命中墙）。
        /// </summary>
        internal static int PetLayerIndex() => LayerMask.NameToLayer(PetLayerName);

        /// <summary>
        /// 建四面墙。纯 GameObject 创建 +材质，不碰 AssetDatabase 写操作，
        /// 所以放在接线之后是安全的。
        /// </summary>
        internal static void CreateWanderWalls(int wallsLayer)
        {
// 墙体半尺寸要比活动区（±2.2m）略大，宠物能先撞到「探测范围边缘」
            // 而不是直接被边界钳制硬拽回来，这样 lookAheadDistance 内的
            // 扇形避障才有意义。留0.8m 余量给 lookAheadDistance(1.1m) 的一半。
            float half = 3.0f;
            float height = 2f;
            var specs = new[]
            {
                (name: "Wall_North", pos: new Vector3(0f, height / 2f, half), scale: new Vector3(half * 2f, height, 0.2f)),
                (name: "Wall_South", pos: new Vector3(0f, height / 2f, -half), scale: new Vector3(half * 2f, height, 0.2f)),
                (name: "Wall_East",  pos: new Vector3(half, height / 2f, 0f),  scale: new Vector3(0.2f, height, half * 2f)),
                (name: "Wall_West",  pos: new Vector3(-half, height / 2f, 0f), scale: new Vector3(0.2f, height, half * 2f)),
            };

            foreach (var spec in specs)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = spec.name;
                wall.transform.position = spec.pos;
                wall.transform.localScale = spec.scale;
                wall.layer = wallsLayer;
                // 墙不可见但可碰撞：用半透明材质太干扰视觉，这里直接隐藏渲染但保留 Collider
                var renderer = wall.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = CreateInvisibleMaterial();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        /// <summary>避障层名称。与 <see cref="PetController"/> 的 obstacleMask 对应。</summary>
        public const string WallsLayerName = "Walls";

        /// <summary>宠物碰撞层名。触摸射线只测这一层。</summary>
        public const string PetLayerName = "Pet";

        /// <summary>
        /// 把避障层写进 PetController.obstacleMask。
        /// 避障墙本身是靠 SphereCast 探测的，这个 mask 决定探测哪些层——
        /// 不配的话射线全部落空，避障逻辑等于没开。
        /// </summary>
        private static void ConfigureObstacleMask(PetController pet, int wallsLayer)
        {
            if (pet == null) return;

            int mask = 1 << wallsLayer;
            SetPrivateField(pet, "obstacleMask", mask);

            // BodyHeight 参与避障射线起点的高度计算（transform.position + up * (BodyHeight * probeHeight + 0.1)），
            // 设为狗的实际身高约 0.5m，射线会打在狗身中部，既不打到地面也不打到天花板。
            pet.BodyHeight = DogHeight;

            Debug.Log($"[PetV1] PetController.obstacleMask = {mask}（层 {wallsLayer} = {WallsLayerName}），射线避障已启用");
        }

        /// <summary>
        /// 确保某个具名 Layer 存在，返回它的索引。
        /// 不存在就在 TagManager.asset 里新建一个空闲层。
        /// 用SerializedObject 改 ProjectSettings 而不是让用户手动去Layers 下拉框里加，
        /// 这样「点一次菜单就能跑通」，不用手工步骤。
        /// </summary>
        private static int EnsureLayerExists(string layerName, int preferredIndex)
        {
            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0) return existing;

            // Layer 0-7 是 Unity 内置的（Default / Ignore Raycast / Water / UI 等），不能占用。
            const int firstUserLayer = 8;
            int maxLayers = 32;

            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
            {
                Debug.LogWarning($"[PetV1] 读不到 TagManager.asset，无法自动创建 {layerName} 层。" +
                                 $"避障墙将落在 Default 层，PetController.obstacleMask 需手动配置。");
                return 0;
            }

            var tagManager = new SerializedObject(asset[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null || !layers.isArray)
            {
                Debug.LogWarning($"[PetV1] TagManager.asset 结构异常，无法自动创建 {layerName} 层。");
                return 0;
            }

            // 优先用 preferredIndex，不行就找第一个空位
            int target = -1;
            if (preferredIndex >= firstUserLayer && preferredIndex < maxLayers &&
                string.IsNullOrEmpty(layers.GetArrayElementAtIndex(preferredIndex).stringValue))
            {
                target = preferredIndex;
            }
            else
            {
                for (int i = firstUserLayer; i < maxLayers; i++)
                {
                    if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                    {
                        target = i;
                        break;
                    }
                }
            }

            if (target < 0)
            {
                Debug.LogWarning($"[PetV1] 没有空闲的 Layer 槽位，无法创建 {layerName}。" +
                                 $"避障墙将落在 Default 层。");
                return 0;
            }

            layers.GetArrayElementAtIndex(target).stringValue = layerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            Debug.Log($"[PetV1] 已自动创建 Layer [{target}] = {layerName}");
            return target;
        }

        private static Material _invisibleMaterial;

        private static Material CreateInvisibleMaterial()
        {
            if (_invisibleMaterial != null) return _invisibleMaterial;
            string path = "Assets/Scenes/PetPrototype/Materials/PetWalls.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                _invisibleMaterial = existing;
                return existing;
            }

            // URP Unlit + 透明，只为让碰撞体在 Scene 视图里可见但运行时不挡镜头
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", new Color(0.4f, 0.7f, 1f, 0.06f));
            mat.SetFloat("_Surface", 1f);          // Transparent
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            AssetDatabase.CreateAsset(mat, path);
            _invisibleMaterial = mat;
            return mat;
        }

        private static void BuildFloorMaterial()
        {
            string path = FloorMaterialPath;
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", new Color(0.32f, 0.35f, 0.40f));
            mat.SetFloat("_Smoothness", 0.12f);
            AssetDatabase.CreateAsset(mat, path);
        }

        /// <summary>场景左上角的说明文字。用 IMGUI，不依赖 UI 资源。</summary>
        private static void CreateInfoPanel(Scene scene, string text)
        {
            var go = new GameObject("SceneInfo");
            var info = go.AddComponent<PetSceneInfo>();
            info.SetText(text);
            SceneManager.MoveGameObjectToScene(go, scene);
        }

        internal static void SaveScene(Scene scene, string path)
        {
            // SaveScene 要求磁盘目录真实存在，所以这里用 EnsurePhysicalFolder 而不是只建 Asset 目录。
            PetAnimationBuilder.EnsurePhysicalFolder(Path.GetDirectoryName(path).Replace('\\', '/'));

            // 保存前把所有对象标记为脏。前面用 SetPrivateField 写入的引用如果对象本身
            // 不脏，SaveScene 可能跳过它们，场景文件里就写成 {fileID: 0}。
            // 显式标脏 + 落盘是确保引用真的写进 .unity 文件的最后一道保险。
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component != null) EditorUtility.SetDirty(component);
                }
            }
            EditorSceneManager.MarkSceneDirty(scene);

            bool ok = EditorSceneManager.SaveScene(scene, path);
            if (!ok) Debug.LogError($"[PetV1] 场景保存失败：{path}");
            else PetLog.Animation("scene saved", PetLog.Kv("path", path));
        }

        /// <summary>同帧复用的 SerializedObject 缓存，避免多次写入互相覆盖。</summary>
        // 用对象引用本身做 key，不用 GetInstanceID() —— Unity 6 里它已过时（CS0619）。
        private static readonly Dictionary<UnityEngine.Object, SerializedObject> s_serializedCache =
            new Dictionary<UnityEngine.Object, SerializedObject>();

        /// <summary>通过反射写入 [SerializeField] 私有字段。Editor 工具的常规做法。</summary>
        // 注意用 UnityEngine.Object 而不是 Object：本文件已 using System，
        // 不限定的话 Object 在 object 与 UnityEngine.Object 之间歧义（CS0104）。
        public static void SetPrivateField(UnityEngine.Object target, string fieldName, object value)
        {
            if (target == null) return;

            // 同一帧内对同一对象必须复用同一个 SerializedObject。
            // 每次 new 一个新的，第二次写入会从组件的原始快照重新读，
            // 把第一次的写入覆盖掉—— 表现为「先写的字段保留、后写的字段丢失」，
            // 而且不报任何错。这就是 animationMap 一直为 null 而 animator 正常的原因。
            if (!s_serializedCache.TryGetValue(target, out var so))
            {
                so = new SerializedObject(target);
                s_serializedCache[target] = so;
            }

            var prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                Debug.LogError($"[PetV1] 字段不存在：{target.GetType().Name}.{fieldName}");
                return;
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = value as UnityEngine.Object;
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = value is bool b && b;
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = value is float f ? f : 0f;
                    break;
                case SerializedPropertyType.Integer:
                    prop.intValue = value is int i ? i : 0;
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = value is int mask ? mask : 0;
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value as string ?? "";
                    break;
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = value is int idx ? idx : 0;
                    break;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = (Vector2)value;
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = (Vector3)value;
                    break;
                default:
                    Debug.LogWarning($"[PetV1] 未处理的属性类型 {prop.propertyType}：{target.GetType().Name}.{fieldName}");
                    break;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>清掉 SerializedObject 缓存，避免跨场景构建复用脏状态。</summary>
        public static void ClearSerializedCache()
        {
            s_serializedCache.Clear();
        }
    }
}
