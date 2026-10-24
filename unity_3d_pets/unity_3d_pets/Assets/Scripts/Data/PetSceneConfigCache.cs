using System;
using System.Collections;
using System.IO;
using PetV1.Model;
using UnityEngine;
using UnityEngine.Networking;

namespace PetV1.Data
{
    /// <summary>
    /// 场景配置的服务端拉取与本地缓存。
    ///
    /// 流程（对应需求「每次启动或者换场景，如果本地没有就从服务端读取」）：
    ///   启动 →先读本地缓存（persistentDataPath/scene_config.json）
    ///        → 有缓存且版本号 ≥ 期望：用缓存，立刻可用（不等网络）
    ///        → 无缓存 / 版本落后 / 缓存损坏：请求服务端 config/get
    ///                                → 成功：写缓存 + 应用到场景库
    ///                                → 失败：沿用 asset 里的内置值（保证离线可玩）
    ///
    /// 为什么缓存要独立于 PetSceneLibrary.asset：
    /// asset 是**构建期产物**，每次跑 Build Home Scene 都会被重写。
    /// 把运行期下发的数据写进去，下次构建就冲掉了。
    ///
    /// 为什么不用 PlayerPrefs：小游戏有存储配额，且它是扁平的 key-value，
    /// 存嵌套 JSON 只能拼字符串，容易被截断且不好校验。
    ///
    /// 网络失败**不阻断启动** —— 首次安装离线、后台维护中都必须能进游戏。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetSceneConfigCache : MonoBehaviour
    {
        [Header("服务地址")]
        [Tooltip("后台根地址。本地联调填 http://127.0.0.1:8080；上线换成正式域名。\n" +
                 "**必须带http:// 或 https://**，UnityWebRequest 不接受裸域名。")]
        [SerializeField] private string serverBaseUrl = "http://127.0.0.1:8080";

        [Tooltip("请求超时（秒）。超时按「拉取失败」处理，沿用本地值，不阻塞启动。")]
        [SerializeField] private float timeoutSeconds = 5f;

        [Header("调试")]
        [Tooltip("勾上则每次启动都强制重新拉取，不读缓存（测后台改动时用）。")]
        [SerializeField] private bool forceRefresh;

        [Tooltip("勾上则把请求与响应的关键字段打进日志。线上关掉。")]
        [SerializeField] private bool verboseLog;

        /// <summary>缓存文件名（persistentDataPath 下）。</summary>
        public const string CacheFileName = "scene_config.json";

        /// <summary>
        /// 运行期覆盖配置。
        ///
        /// 存在的理由：这三个字段是 [SerializeField] private，构建器在 Editor 下
        /// 用 <c>SerializedObject</c> 写很方便，但本组件在 PetV1.Runtime 程序集里，
        /// **不能引用 UnityEditor**（编辑器 API 进不了运行时包，微信小游戏构建会炸）。
        /// 所以 Editor 侧的覆盖统一走这个公开方法。
        ///
        /// 空值一律视为「不覆盖」，避免构建器没填的字段把默认值清掉。
        /// </summary>
        public void Configure(string baseUrl, bool force, bool verbose)
        {
            if (!string.IsNullOrEmpty(baseUrl)) serverBaseUrl = baseUrl;
            if (force) forceRefresh = true;
            if (verbose) verboseLog = true;
        }

        /// <summary>缓存里存的 configVersion。与后台 config_meta.version 对齐。</summary>
        public long CachedVersion { get; private set; }

        /// <summary>本次启动实际用了哪份数据。诊断用。</summary>
        public string LastSource { get; private set; } = "none";

        /// <summary>
        /// 客户端 token。config/get 要鉴权，没有 token 时本组件不发请求。
        ///
        /// 由 <see cref="PetSession"/> 在登录成功后注入，并且它会把同一份响应直接
        /// 喂给 <see cref="TryApplyResponse"/> —— 本组件自己发请求只是兜底路径
        /// （单独跑本场景、没有 PetSession 时）。
        /// </summary>
        [NonSerialized] private string _token = "";

        public bool HasToken => !string.IsNullOrEmpty(_token);

        public void SetToken(string token)
        {
            _token = token ?? "";
        }

        /// <summary>上一次拉取是否**因为组件还没启用而被跳过**。
        ///
        /// 场景加载早期（Awake 阶段，尤其是 <c>[DefaultExecutionOrder(-100)]</c>
        /// 的引导组件里）<c>isActiveAndEnabled</c> 可能还是 false，
        /// 请求会被静默跳过 —— 表现是「后台配了 sceneUrl，客户端一直用包内图」，
        /// 而 Console 只有一句「组件未启用」。调用方（Bootstrap.Start）据此补拉一次。
        /// </summary>
        public bool LastFetchSkipped { get; private set; }

        /// <summary>
        /// 服务端配置应用完成事件。<paramref name="fromServer"/> 为 true 表示是网络数据，
        /// false 表示是读缓存。UI 之类的订阅方可以据此决定要不要刷新。
        /// </summary>
        public event System.Action<PetSceneLibrary, bool> ConfigApplied;

        /// <summary>
        /// 启动时调用。同步读缓存（文件小，很快），异步拉服务端。
        ///
        /// 返回值只表示「**此刻**本地有没有可用的数据」——
        /// 服务端拉取是异步的，结果通过 <see cref="ConfigApplied"/> 通知。
        /// 返回 false 不等于起不来：asset 里还有构建期写入的三张图数据点。
        /// </summary>
        public bool LoadFromCacheOrFetch(PetSceneLibrary library)
        {
            if (library == null)
            {
                Debug.LogError("[PetV1] 场景配置缓存：library 为空。");
                return false;
            }

            CachedVersion = ReadCachedVersion();

            bool usedCache = false;
            if (!forceRefresh && TryLoadCache(library))
            {
                usedCache = true;
                LastSource = $"cache(v{CachedVersion})";
                ConfigApplied?.Invoke(library, false);
            }

            if (usedCache && !forceRefresh)
            {
                // 本地有数据，先让游戏跑起来，再后台静默刷新。
                // 这样弱网 / 后台不可用时不影响进入速度。
                // 刷新成功的事件由 TryApplyResponse 内部抛，这里不再重复触发。
                RefreshFromServer(library, null);
                return true;
            }

            // 本地没有（或强制刷新）：必须走一次网络。
            // 用回调而不是阻塞等待 —— 阻塞主线程会让协程无法推进，
            // 那样等待的协程永远等不到自己。
            // 拉取失败会落到 asset 内置值，游戏仍能启动。
            RefreshFromServer(library, ok =>
            {
                // 成功路径的事件已由 TryApplyResponse 抛出（含 unchanged 时不抛）
                if (ok) return;
                FallbackToAsset(library, "服务端未返回可用配置");
                ConfigApplied?.Invoke(library, false);
            });
            // 「本地此刻没数据」。别让调用方误以为已就绪 ——
            // 它应该先拿 asset 里的内置值开局，服务端回来后再刷新。
            return false;
        }

        /// <summary>
        /// 只读本地缓存，不碰网络。返回是否命中。
        ///
        /// 单独暴露是因为「离线开局」是一个真实场景（首次装机、飞行模式、
        /// 后台维护中），而 <see cref="LoadFromCacheOrFetch"/> 会顺带发起网络请求。
        /// </summary>
        public bool LoadFromCacheOnly(PetSceneLibrary library)
        {
            if (library == null) return false;
            CachedVersion = ReadCachedVersion();
            bool ok = TryLoadCache(library);
            if (ok) LastSource = $"cache(v{CachedVersion})";
            return ok;
        }

        // ------------------------------------------------------------------
        // 服务端
        // ------------------------------------------------------------------

        /// <summary>异步拉服务端配置。失败只记日志，不抛异常 —— 拉不到就用本地那份。</summary>
        public void RefreshFromServer(PetSceneLibrary library)
        {
            RefreshFromServer(library, null);
        }

        public void RefreshFromServer(PetSceneLibrary library, System.Action<bool> done)
        {
            if (library == null) return;
            if (!Uri.IsWellFormedUriString(serverBaseUrl, UriKind.Absolute))
            {
                Debug.LogWarning($"[PetV1] 场景配置：服务地址不合法（{serverBaseUrl}），跳过拉取。" +
                                 "本机联调请填完整地址如 http://127.0.0.1:8080");
                done?.Invoke(false);
                return;
            }
            if (!HasToken)
            {
                // 没有 token 时**不发请求**。config/get 要鉴权，发了必然 401，
                // 而 401 的日志会让人以为「后台挂了」，实际只是还没登录。
                // 真正的数据由 PetSession 拿到后喂给 TryApplyResponse。
                if (verboseLog)
                    Debug.Log("[PetV1] 场景配置：还没有 token，等 PetSession 喂配置（不发请求）。");
                done?.Invoke(false);
                return;
            }
            if (!isActiveAndEnabled)
            {
                LastFetchSkipped = true;
                Debug.LogWarning("[PetV1] 场景配置：组件未启用，无法发起请求。" +
                                 "如果这是启动早期的一次调用，Bootstrap.Start 会补拉。");
                done?.Invoke(false);
                return;
            }
            LastFetchSkipped = false;
            StartCoroutine(FetchRoutine(library, done));
        }

        /// <summary>
        /// 真正的请求协程。
        ///
        /// 协议：POST /api/v1/config/get，体{"version": N}，
        /// 响应 {"unchanged":bool, "version":N, "config":{...}}。
        /// 该接口需要用户 token —— V1 还没做登录，所以这里先不带，
        /// 服务端放开鉴权前会拿到 401，此路径按「拉取失败」处理并给出明确提示。
        /// </summary>
        private IEnumerator FetchRoutine(PetSceneLibrary library, System.Action<bool> done)
        {
            string url = serverBaseUrl.TrimEnd('/') + "/api/v1/config/get";
            string body = "{\"version\":" + CachedVersion + "}";

            if (verboseLog) Debug.Log($"[PetV1] 场景配置：POST {url} body={body}");

            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_token)) req.SetRequestHeader("Authorization", "Bearer " + _token);
            req.timeout = Mathf.CeilToInt(timeoutSeconds);

            yield return req.SendWebRequest();

            bool ok = false;
            if (req.result != UnityWebRequest.Result.Success)
            {
                // 401 要单独提示 —— 那不是「网络不通」，是「还没做登录」。
                // 混成网络错误会让人往错的方向排查。
                if (req.responseCode == 401 || req.responseCode == 403)
                {
                    Debug.LogWarning($"[PetV1] 场景配置：服务端返回 {req.responseCode}，" +
                                     "config/get 需要用户 token。V1 登录未接入前，" +
                                     "场景构图参数只能靠asset 内置值 + 本次手工同步。" +
                                     "把 PetSceneConfigCache.verboseLog 打开可以看到完整响应。");
                }
                else
                {
                    Debug.LogWarning($"[PetV1] 场景配置拉取失败：{req.error} " +
                                     $"(HTTP {req.responseCode})，沿用本地数据。");
                }
            }
            else
            {
                ok = TryApplyResponse(library, req.downloadHandler.text);
            }

            req.Dispose();
            LastSource = ok ? "server" : (LastSource == "none" ? "asset" : LastSource);
            done?.Invoke(ok);
        }

        /// <summary>
        /// 应用一份服务端 config/get 响应。成功返回 true。
        ///
        /// 公开而不是私有：网络层与测试都要能直接喂一份响应进来，
        /// 不必真的发一次 HTTP 才能验证解析路径。
        /// </summary>
        public bool TryApplyResponse(PetSceneLibrary library, string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            if (verboseLog) Debug.Log($"[PetV1] 场景配置响应：{json}");

            var resp = ParseResponse(json);
            if (resp == null)
            {
                Debug.LogWarning("[PetV1] 场景配置：响应解析失败，沿用本地数据。");
                return false;
            }
            if (resp.unchanged)
            {
                // 服务端说没变。缓存就是最新的，什么都不用做。
                if (verboseLog)
                    Debug.Log($"[PetV1] 场景配置：服务端版本未变（v{resp.version}），沿用缓存。");
                CachedVersion = resp.version;
                return true;
            }
            if (resp.scene == null || resp.scene.Length == 0)
            {
                // 走到这里说明 JSON 结构跟预期不符（多半是 scene 那一层没解析出来）。
                // 必须报错而不是静默返回 true —— 返回 true 会让调用方以为配置已就绪，
                // 然后带着旧数据继续跑，看起来像「配置没生效」。
                Debug.LogWarning($"[PetV1] 场景配置：响应里没有 scene 数组" +
                                 $"（unchanged={resp.unchanged}, version={resp.version}），沿用本地数据。" +
                                 "打开 verboseLog 看原始响应核对 JSON 结构。");
                return false;
            }

            int added;
            int changed = library.ApplyRemoteConfig(JsonHelperToArray(resp.scene), out added);

            CachedVersion = resp.version;
            WriteCache(library, resp.version);
            LastSource = "server";
            Debug.Log($"[PetV1] 已应用服务端场景配置 v{resp.version}：" +
                      $"{changed} 处改动，新增 {added} 个场景");

            // 通知订阅方（重摆取景 / 补背景图）。
            // 事件放在这里而不是调用方，是因为 TryApplyResponse 有两个入口：
            // 本组件自己发请求，以及 PetSession 把同一份响应喂进来。
            // 放在调用方会漏掉后者，表现是「配置变了但相机没重摆」。
            ConfigApplied?.Invoke(library, true);
            return true;
        }

        /// <summary>
        /// 从 config/get 响应里取出 unchanged / version / scene。
        ///
        /// 为什么要自己剥一层：真实响应是 {"unchanged":..,"version":..,"config":{"scene":[..]}}，
        /// 而 **Unity 的 JsonUtility 解析不了嵌套层级**（实测 {"config":{...}} 会让整个
        /// config 变 null，无论内层类是 public 还是 private）。
        /// 它能用的只有「顶层直接是目标字段」这种扁平结构。
        ///
        /// 所以分两次解：先在扁平结构里拿 unchanged / version，
        /// 再手写括号配平截出 config 对象单独解一次（那里面字段就叫 scene，正好对上）。
        /// **两次的结果必须合并** —— 只返回内层的话 version 会丢成 0，
        /// 缓存就永远记着「服务端版本 0」，每次启动都会重新拉。
        ///
        /// 只支持一层 config，不做通用 JSON 解析 —— 配置响应的结构是本工程自己定的，
        /// 值得为了确定性与零依赖写这点手工代码。
        /// </summary>
        private static ConfigResponse ParseResponse(string json)
        {
            var outer = JsonHelperFrom<ConfigResponse>(json);
            var resp = new ConfigResponse
            {
                unchanged = outer != null && outer.unchanged,
                version = outer != null ? outer.version : 0,
                scene = outer?.scene,
            };
            if (resp.scene != null && resp.scene.Length > 0) return resp;

            int start = json.IndexOf("\"config\"", System.StringComparison.Ordinal);
            if (start < 0) return resp;
            start = json.IndexOf('{', start);
            if (start < 0) return resp;

            int depth = 0, end = -1;
            bool inStr = false, esc = false;
            for (int i = start; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr)
                {
                    // 必须处理转义：layoutNote 里可能有 \" 、\\ ，甚至裸的 { }
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) return resp;

            var inner = JsonHelperFrom<ConfigResponse>(json.Substring(start, end - start + 1));
            if (inner != null) resp.scene = inner.scene;
            return resp;
        }

        // ------------------------------------------------------------------
        // 缓存读写
        // ------------------------------------------------------------------

        private static string CachePath => Path.Combine(Application.persistentDataPath, CacheFileName);

        /// <summary>
        /// 读缓存并应用到场景库。缓存不存在/损坏返回 false。
        ///
        /// 注意 cacheMeta 那层嵌套同样解析不出来（见 ParseResponse 的注释），
        /// 所以 meta 的字段是**平铺**在 CacheWrapper 里的，不套一层。
        /// </summary>
        private bool TryLoadCache(PetSceneLibrary library)
        {
            string path = CachePath;
            if (!File.Exists(path)) return false;
            try
            {
                string json = File.ReadAllText(path);
                var wrapper = JsonHelperFrom<CacheWrapper>(json);
                if (wrapper == null || string.IsNullOrEmpty(wrapper.sceneJson))
                {
                    Debug.LogWarning("[PetV1] 场景配置缓存损坏，按无缓存处理。");
                    return false;
                }
                int added;
                int changed = library.ApplyRemoteConfig(wrapper.sceneJson, out added);
                CachedVersion = wrapper.version;
                if (verboseLog)
                    Debug.Log($"[PetV1] 场景配置缓存命中：v{CachedVersion}，{changed} 处应用");
                return true;
            }
            catch (System.Exception e)
            {
                // 缓存文件损坏不该让游戏起不来。
                Debug.LogWarning($"[PetV1] 场景配置缓存读取失败（{e.Message}），按无缓存处理。");
                return false;
            }
        }

        private void WriteCache(PetSceneLibrary library, long version)
        {
            try
            {
                var wrapper = new CacheWrapper
                {
                    version = version,
                    cachedAtMs = NowMs(),
                    sceneCount = library.scenes.Count,
                    // 只缓存场景条目。食物 / 积分那些客户端 V1 用不到，
                    // 缓存整个 config 只会让文件大一半然后过期。
                    sceneJson = BuildSceneArrayJson(library),
                };
                File.WriteAllText(CachePath, JsonHelperTo(wrapper));
                if (verboseLog) Debug.Log($"[PetV1] 场景配置已缓存：{CachePath}（v{version}）");
            }
            catch (System.Exception e)
            {
                // 缓存写失败只影响下次离线体验，不影响本次运行。
                Debug.LogWarning($"[PetV1] 场景配置缓存写入失败：{e.Message}");
            }
        }

        private static long ReadCachedVersion()
        {
            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return 0;
                var wrapper = JsonHelperFrom<CacheWrapper>(File.ReadAllText(path));
                return wrapper?.version ?? 0;
            }
            catch
            {
                // 读不出来就当没有缓存，让上层走服务端。
                return 0;
            }
        }

        /// <summary>
        /// 手动清缓存。改了后台想验证效果、又不想改版本号时用。
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (File.Exists(CachePath)) File.Delete(CachePath);
                CachedVersion = 0;
                Debug.Log($"[PetV1] 场景配置缓存已清除：{CachePath}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PetV1] 清缓存失败：{e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // JSON工具
        // ------------------------------------------------------------------
        // Unity 内置的 JsonUtility 不支持：顶层数组、可空类型、字典。
        // 所以这里包一层 object 字段再取，并且不用任何第三方库。

        private static T JsonHelperFrom<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<T>(json); }
            catch (System.Exception) { return null; }
        }

        private static string JsonHelperTo<T>(T obj) where T : class
        {
            return JsonUtility.ToJson(obj);
        }

        /// <summary>
        /// 把 RemoteScene[] 转成**裸** JSON 数组（无 items 包装）。
        /// ApplyRemoteConfig 内部会自己装箱 —— 两边都装箱会套出
        /// {"items":{"items":[...]}}，解析出 null 且不报错。
        /// </summary>
        private static string JsonHelperToArray(PetSceneLibrary.RemoteScene[] arr)
        {
            // JsonUtility 只能输出对象不能输出裸数组，
            // 所以先装箱再截出 [...] 那一段。
            string boxed = JsonUtility.ToJson(new SceneArrayBox { items = arr });
            int start = boxed.IndexOf('[');
            int end = boxed.LastIndexOf('}');
            if (start < 0 || end < start) return "[]";
            return boxed.Substring(start, end - start);
        }

        private static string BuildSceneArrayJson(PetSceneLibrary library)
        {
            var arr = new PetSceneLibrary.RemoteScene[library.scenes.Count];
            for (int i = 0; i < library.scenes.Count; i++)
            {
                var e = library.scenes[i];
                var l = e.ResolveLayout();
                arr[i] = new PetSceneLibrary.RemoteScene
                {
                    id = e.sceneId,
                    name = e.displayName,
                    price = e.price,
                    sceneUrl = e.sceneUrl,
                    thumbUrl = e.thumbUrl,
                    layout = new PetSceneLibrary.RemoteLayout
                    {
                        orthographicSize = l.orthographicSize,
                        cameraPitch = l.cameraPitch,
                        cameraDistance = l.cameraDistance,
                        floorFarLine = l.floorFarLine,
                        dogGroundLine = l.dogGroundLine,
                        wanderHalfX = l.wanderHalfX,
                        wanderHalfZ = l.wanderHalfZ,
                        wanderCenterX = l.wanderCenterX,
                        wanderCenterZ = l.wanderCenterZ,
                        lightEulerX = l.lightEulerX,
                        lightEulerY = l.lightEulerY,
                        lightEulerZ = l.lightEulerZ,
                        lightColorR = l.lightColorR,
                        lightColorG = l.lightColorG,
                        lightColorB = l.lightColorB,
                        ambientIntensity = l.ambientIntensity,
                        shadowOpacity = l.shadowOpacity,
                        layoutNote = l.layoutNote,
                    },
                };
            }
            return JsonHelperToArray(arr);
        }

        private static long NowMs()
        {
            // 服务端 timeutil 用的也是 UTC 毫秒。
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private bool FallbackToAsset(PetSceneLibrary library, string why)
        {
            // asset 里已有构建期写入的三张图数据点，直接可用。
            LastSource = "asset";
            Debug.Log($"[PetV1] 场景配置：{why}，使用安装包内置数据点" +
                      $"（{library.scenes.Count} 个场景）。");
            return library.scenes.Count > 0;
        }

        // ------------------------------------------------------------------
        // 响应结构
        // ------------------------------------------------------------------

        /// <summary>
        /// config/get 的响应。
        ///
        /// 结构必须**扁平到一层**：Unity 的 JsonUtility 对
        /// 「嵌套 [Serializable] 类里再套数组」这种结构解析不可靠，
        /// 实测 private嵌套类 ConfigBody 里的 scene 数组会静默变成 null。
        /// 所以 config 直接摊平进本类（Go 侧就是 {"config":{"scene":[...]}}，
        /// 这里把中间的 config 那一层省掉，靠字段名 scene 对上即可 ——
        /// JsonUtility 按字段名匹配，不要求层级一致）。
        ///
        /// **静默变 null 是最难查的一类问题**：
        /// 上面那次实测 ok=True 却一个字段都没应用上，
        /// 而代码里所有错误分支都打了警告日志，只有这条没有 ——
        /// 因为它走的是「成功」分支。所以必须在这里显式判空并报警。
        /// </summary>
        [Serializable]
        public class ConfigResponse
        {
            public bool unchanged;
            public long version;
            public PetSceneLibrary.RemoteScene[] scene;
        }

        [Serializable]
        private class CacheWrapper
        {
            // 平铺，不嵌套独立 meta 类 —— 见 TryLoadCache 的注释。
            public long version;
            public long cachedAtMs;
            public int sceneCount;
            // 存成字符串而不是数组：嵌套数组在缓存文件的读写往返里
            // 同样不可靠，用原始 JSON 字符串可以二次 FromJson 解析。
            public string sceneJson;
        }

        [Serializable]
        private class SceneArrayBox
        {
            public PetSceneLibrary.RemoteScene[] items;
        }
    }
}