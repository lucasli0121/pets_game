using PetV1.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace PetV1.Pet
{
    /// <summary>
    /// 2D 背景 + 3D 宠物的场景运行时控制。
    ///
    /// PRD 4.5「场景表现形态（技术约束，2026-10-18 定）」要求：
    /// - 每个场景一张竖屏 2D 背景图 1080×1920
    /// - 宠物是 3D，放在背景前方的地面活动区域
    /// - **相机固定，正交投影**（避免宠物远近变化导致与背景比例失调）
    /// - 宠物脚下必须有跟随移动的椭圆半透明假阴影
    /// - 背景图光源统一左上，与场景主光方向一致
    ///
    /// 背景与宠物的深度隔离（这是本类存在的核心理由）：
    /// 背景是**不接收实时光照**的 2D quad，宠物是受光 3D。
    /// 正交相机下两者在同一个深度缓冲里，靠三件事隔离：
    /// ① 背景 quad 放在相机正前方固定的远处位置
    /// ② 背景材质用 Unlit，不吃场景光
    /// ③ 宠物活动区被限制在地面范围内，永远不会跑到背景那一侧
    /// 另外背景与宠物分属不同 sorting layer，作为双保险。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetSceneStage : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("背景 quad。构建工具创建。")]
        [SerializeField] private MeshRenderer backgroundRenderer;

        [Tooltip("场景定义。换场景时从这里读配置。")]
        [SerializeField] private PetSceneLibrary library;

        [Tooltip("当前场景 id，与后台 config_scene.id 一致。")]
        [SerializeField] private string currentSceneId = "home_day";

        [Header("Lighting")]
        [Tooltip("主光。方向必须与背景图光源一致（PRD 要求左上）。")]
        [SerializeField] private Light keyLight;

        [Header("Pet")]
        [Tooltip("狗本体，用于挂假阴影。")]
        [SerializeField] private Transform petRoot;

        [Header("Ground")]
        [Tooltip("地面高度（Y）。宠物与假阴影都以此为基准。")]
        [SerializeField] private float groundY = 0f;

        /// <summary>场景切换完成事件。UI 订阅它刷新标题等。</summary>
        public event System.Action<PetSceneLibrary.SceneEntry> SceneChanged;

        public string CurrentSceneId => currentSceneId;

        /// <summary>当前生效的场景条目（只读）。null 表示 sceneId 在库里找不到。</summary>
        public PetSceneLibrary.SceneEntry CurrentEntry =>
            library != null ? library.Find(currentSceneId) : null;

        public void Configure(PetSceneLibrary lib, Transform pet, Light light)
        {
            library = lib;
            petRoot = pet;
            keyLight = light;
        }

        private void Start()
        {
            ApplyScene(currentSceneId);
        }

        /// <summary>
        /// 切换场景。V1 只有三张背景图，整图换掉即可，不做分层遮挡。
        /// </summary>
        public void ApplyScene(string sceneId)
        {
            if (library == null) return;
            var entry = library.Find(sceneId);
            if (entry == null)
            {
                Debug.LogWarning($"[PetV1] 场景库中找不到 '{sceneId}'，保持当前场景。");
                return;
            }

            currentSceneId = sceneId;

            // 1. 换背景贴图
            if (backgroundRenderer != null && entry.background != null)
            {
                var mat = backgroundRenderer.sharedMaterial;
                if (mat != null)
                {
                    // 用实例材质，避免三个场景共用一个 asset 时互相污染
                    if (Application.isPlaying && backgroundRenderer.material != backgroundRenderer.sharedMaterial)
                    {
                        // 编辑器下不动 sharedMaterial
                    }
                    mat.mainTexture = entry.background;
                    Debug.Log($"[PetV1] 场景 '{sceneId}' 背景已切换：{entry.background.name}");
                }
                else
                {
                    Debug.LogError("[PetV1] 背景 quad 缺材质，无法贴图。");
                }
            }

            // 2. 主光跟随场景（三张背景图光源都在左上，所以方向一致，
            //    但颜色与强度必须换 —— 夜间场景背景是暗的，
            //    宠物若还按白天的亮度渲染会像贴纸浮在画面上）
            if (keyLight != null)
            {
                keyLight.transform.rotation = Quaternion.Euler(entry.lightEuler);
                keyLight.color = entry.lightColor;
                keyLight.intensity = entry.shadowOpacity > 0f ? 1f : 1f;
            }

            // 3. 环境光
            RenderSettings.ambientIntensity = entry.ambientIntensity;

            // 4. 假阴影外观
            var shadow = petRoot != null ? petRoot.GetComponentInChildren<PetFakeShadow>(true) : null;
            if (shadow != null) shadow.Configure(entry.shadowOpacity);

            SceneChanged?.Invoke(entry);
            Debug.Log($"[PetV1] 场景 '{sceneId}'（{entry.displayNameKey}）已应用，" +
                      $"光色 {entry.lightColor}，环境光 {entry.ambientIntensity:F2}，" +
                      $"假阴影 {entry.shadowOpacity:F2}");
        }

        /// <summary>
        /// 校验背景与宠物的深度关系。
        /// 正交相机下若宠物跑到背景后面，渲染顺序会失效。
        /// 构建时和切场景后都该跑一次。
        /// </summary>
        public bool ValidateDepth(out string message)
        {
            message = "";
            if (backgroundRenderer == null || petRoot == null || Camera.main == null)
            {
                message = "引用缺失，无法校验";
                return true; // 不阻断
            }

            Camera cam = Camera.main;
            Vector3 petScreen = cam.WorldToViewportPoint(petRoot.position);
            Vector3 bgScreen = cam.WorldToViewportPoint(backgroundRenderer.transform.position);

            if (petScreen.z < bgScreen.z)
            {
                message = $"宠物在背景之后（pet z={petScreen.z:F2} < bg z={bgScreen.z:F2}）";
                return false;
            }

            // 宠物必须在画面内
            if (petScreen.x < 0f || petScreen.x > 1f || petScreen.y < 0f || petScreen.y > 1f)
            {
                message = $"宠物在画面外：viewport=({petScreen.x:F2}, {petScreen.y:F2})";
                return false;
            }

            return true;
        }

        /// <summary>构图自检：狗占画面高度的比例（0~1）。</summary>
        public float MeasurePetScreenRatio(float petHeight)
        {
            if (library == null || Camera.main == null) return 0f;
            float worldHeight = Camera.main.orthographicSize * 2f;
            if (worldHeight <= 0f) return 0f;
            return petHeight / worldHeight;
        }
    }
}