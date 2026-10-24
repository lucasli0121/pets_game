using PetV1.Model;
using PetV1.Pet;
using UnityEngine;

namespace PetV1.Data
{
    /// <summary>
    /// 场景配置的启动引导。
    ///
    /// 存在的理由：<see cref="PetSceneConfigCache"/> 只负责「拿到配置」，
    /// 全工程原本**没有任何地方调用它** —— 后台 config_scene 下发的构图参数
    /// 在运行期永远不会被应用，画面永远用构建期写进 asset 的那套数值。
    /// 本组件是唯一的调用点。
    ///
    /// 时序（对应需求「每次启动读服务端；没有就用本地」）：
    ///   Awake  读本地缓存并立即生效（弱网 / 首次装机离线时不等网络）
    ///   Start  后台静默拉服务端；回来后重摆取景
    ///
    /// 配置生效后必须调 <see cref="PetSceneStage.ApplyScene"/>：
    /// orthographicSize / 俯角 / 相机高度都是按 layout 算出来的，
    /// 只改数据不重摆相机会出现「后台把 orthoSize 改成 2.2，画面还是 1.9 的取景」。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]   // 要早于 PetSceneStage.Start 里的首次 ApplyScene
    public class PetSceneConfigBootstrap : MonoBehaviour
    {
        [Tooltip("要覆盖的场景库。主场景里由构建器写入。")]
        [SerializeField] private PetSceneLibrary library;

        [Tooltip("场景控制。配置生效后由它重摆相机与灯光。")]
        [SerializeField] private PetSceneStage stage;

        [Tooltip("场景配置拉取与缓存组件。同场景内应只有一个。")]
        [SerializeField] private PetSceneConfigCache cache;

        [Tooltip("场景背景图下载与本地缓存。留空则不补图，只用包内资源。")]
        [SerializeField] private PetSceneAssetCache assetCache;

        [Tooltip("勾上则启动时强制重新拉服务端，不读本地缓存（测后台改动时用）。")]
        [SerializeField] private bool forceRefresh;

        [Tooltip("后台根地址。留空则用 PetSceneConfigCache 自己的默认值。\n" +
                 "真机调试要写成局域网 IP（http://192.168.x.x:8080），" +
                 "127.0.0.1 在手机上指的是手机自己。")]
        [SerializeField] private string serverBaseUrl = "";

        [Tooltip("勾上则把请求 / 响应关键字段打进日志。线上关掉。")]
        [SerializeField] private bool verboseLog;

        private bool m_hooked;

        /// <summary>
        /// 构建期注入依赖。**必须用这个方法而不是 SetPrivateField**：
        /// library 是 AssetDatabase 加载出来的实例，构建过程中的其他写操作
        /// 可能让它变成 fake null（C# 引用还在但 == null 为真），
        /// 那样 objectReferenceValue 会被写成 null，引用静默丢失。
        /// 直接赋值不走序列化，fake null 的值也会照原样传过去，由下面的判空兜住。
        /// </summary>
        public void Configure(PetSceneLibrary lib, PetSceneStage sceneStage,
                              PetSceneConfigCache sceneCache)
        {
            library = lib;
            stage = sceneStage;
            cache = sceneCache;
        }

        private void Awake()
        {
            if (library == null) library = Resources.Load<PetSceneLibrary>("PetV1/PetSceneLibrary");
            if (cache == null) cache = GetComponent<PetSceneConfigCache>();
            if (cache == null) cache = FindAnyObjectByType<PetSceneConfigCache>();
            if (stage == null) stage = FindAnyObjectByType<PetSceneStage>();
            if (assetCache == null) assetCache = GetComponent<PetSceneAssetCache>();
            if (assetCache == null) assetCache = FindAnyObjectByType<PetSceneAssetCache>();

            if (cache == null)
            {
                Debug.LogError("[PetV1] 场景配置引导：场景里没有 PetSceneConfigCache，" +
                               "后台构图参数不会生效。");
                return;
            }
            if (library == null)
            {
                Debug.LogError("[PetV1] 场景配置引导：找不到 PetSceneLibrary，" +
                               "后台构图参数不会生效。");
                return;
            }
            if (stage == null)
            {
                // 不 return：配置仍然要应用，只是不能重摆取景。
                // 缺 stage 的症状是「后台改了 orthoSize 画面不动」，不能静默。
                Debug.LogWarning("[PetV1] 场景配置引导：找不到 PetSceneStage，" +
                                 "配置会应用但取景不会被重摆。");
            }

            ApplyOverrides();

            // 配置一到位就重摆取景 —— 从缓存读出来的数据同样要走这一步，
            // 否则「命中缓存」的表现是「画面没变」，分不清是缓存不生效还是改动没配。
            cache.ConfigApplied += OnConfigApplied;
            m_hooked = true;

            // 同步读缓存：有就立刻生效，不等网络。失败也不阻断 ——
            // asset 里还有构建期写入的三张图数据点。
            cache.LoadFromCacheOrFetch(library);
        }

        private void Start()
        {
            // 补拉：Awake 里那次可能因为组件还没启用被静默跳过（见 LastFetchSkipped）。
            // 只补真正被跳过的情况 —— 网络失败不在这里重试，
            // 那种情况重试也只是再失败一次，白增一个请求。
            if (cache != null && cache.LastFetchSkipped && library != null)
            {
                Debug.Log("[PetV1] 场景配置引导：Awake 时组件未启用，Start 里补一次拉取。");
                cache.LoadFromCacheOrFetch(library);
            }

            // 首帧之后确认一次当前场景已按生效配置摆好。
            // PetSceneStage.Start 也会调 ApplyScene，但它读的是同一份 layout，
            // 这里只是把「配置来源」打进日志，便于排查「为什么没生效」。
            if (library != null && stage != null)
            {
                var entry = stage.CurrentEntry;
                Debug.Log($"[PetV1] 场景配置引导：数据来源 {cache.LastSource}" +
                          $"（缓存版本 v{cache.CachedVersion}）" +
                          (entry == null ? "，当前场景未在库中"
                                         : $"，当前场景 {entry.sceneId}" +
                                           $" 构图 orthoSize={entry.ResolveLayout().orthographicSize:F2}"));
            }

            // 场景图补一把：不依赖 ConfigApplied 事件 ——
            // 万一配置没从后台拉到（走 asset），asset 里可能已经带了 sceneUrl，
            // 这条路径照样能把图下下来。sceneUrl 为空的场景会被自动跳过。
            if (assetCache != null && library != null)
            {
                assetCache.Configure(library);
                assetCache.EnsureAll(() =>
                {
                    if (stage != null) stage.ApplyScene(stage.CurrentSceneId);
                });
            }
        }

        private void OnDestroy()
        {
            // 场景重载时必须解绑，否则旧实例的回调会打到已销毁的 stage 上。
            if (m_hooked && cache != null) cache.ConfigApplied -= OnConfigApplied;
            m_hooked = false;
        }

        private void ApplyOverrides()
        {
            // 不能用 SerializedObject 写：这是 PetV1.Runtime 程序集，
            // UnityEditor 在运行时程序集里不可用（编辑器 API 进不了运行时包）。
            if (cache == null) return;
            cache.Configure(serverBaseUrl, forceRefresh, verboseLog);
        }

        private void OnConfigApplied(PetSceneLibrary lib, bool fromServer)
        {
            if (stage == null) return;
            // 用当前场景 id 重摆 —— layout 可能已经变了（orthoSize / 地面线 / 活动区）。
            stage.ApplyScene(stage.CurrentSceneId);
            Debug.Log($"[PetV1] 场景配置已{(fromServer ? "从服务端" : "从缓存")}生效，" +
                      $"已按新数据点重摆取景（场景 {stage.CurrentSceneId}）。");

            // 配置里的 sceneUrl 可能是刚下的新地址，背景图要跟着补。
            // 每补好一张就重贴当前场景 —— 否则图下好了画面还停在旧的那张。
            if (assetCache != null)
            {
                assetCache.Configure(lib);
                assetCache.EnsureAll(() => stage.ApplyScene(stage.CurrentSceneId));
            }
        }
    }
}
