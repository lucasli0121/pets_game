using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PetV1.Model;
using UnityEngine;
using UnityEngine.Networking;

namespace PetV1.Data
{
    /// <summary>
    /// 场景背景图的下载与本地缓存。
    ///
    /// 流程（对应「场景资源从后台接口取，本地缓存一份」）：
    ///   启动 → 遍历场景，<c>sceneUrl</c> 是 http(s) 的才处理
    ///        → 缓存文件存在：直接读到 Texture2D，不等网络
    ///        → 缓存缺失：下载 → 落盘 → 生效
    ///        → 下载失败 / URL 不是 http(s)：保留包内 <c>background</c>
    ///
    /// 为什么缓存文件名带 URL 的哈希：
    /// 运营换了同一场景的图但 sceneId 没变，只按 sceneId 命名会一直用旧图，
    /// 而且没有任何报错。带上 URL 哈希后，URL 一变缓存自然失效。
    ///
    /// 为什么不用 <see cref="UnityWebRequestTexture"/>：
    /// 它直接返回 Texture2D 但不给原始字节，没法落盘。
    /// 用 DownloadHandlerBuffer 拿字节，既能存盘也能自己 LoadImage。
    ///
    /// 内存：1080×1920 RGBA 一张约 8MB，三个场景 25MB。目前够用；
    /// 场景数上到十几个就要加 LRU 卸载或落成压缩纹理，别让它无限涨。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetSceneAssetCache : MonoBehaviour
    {
        [Header("依赖")]
        [Tooltip("要补图的场景库。主场景里由 PetSceneConfigBootstrap 注入。")]
        [SerializeField] private PetSceneLibrary library;

        [Header("网络")]
        [Tooltip("单张图的下载超时（秒）。超时按失败处理，继续用包内图。")]
        [SerializeField] private float timeoutSeconds = 15f;

        [Header("调试")]
        [SerializeField] private bool verboseLog;

        /// <summary>本次启动命中缓存的张数。</summary>
        public int CacheHits { get; private set; }

        /// <summary>本次启动实际下载成功的张数。</summary>
        public int Downloaded { get; private set; }

        /// <summary>失败张数（超时 / 404 / 不是图片）。</summary>
        public int Failed { get; private set; }

        private readonly HashSet<string> _handled = new HashSet<string>();
        private bool _running;

        /// <summary>缓存目录。persistentDataPath 在微信小游戏里是持久化的用户目录。</summary>
        public static string CacheDir => Path.Combine(Application.persistentDataPath, "scene_assets");

        public void Configure(PetSceneLibrary lib)
        {
            if (lib != null) library = lib;
        }

        /// <summary>
        /// 判断一个地址是不是「客户端能自己下载的」。
        ///
        /// 后台在没有配 ASSET_BASE_URL 时会把相对路径原样下发
        ///（比如 <c>scenes/scene_home_day.jpg</c>）—— 那不是 URL，下载必然失败。
        /// 所以判据必须是「协议前缀」而不是「非空」，
        /// 否则会拿相对路径去发请求，报一个和配置毫无关系的网络错误。
        /// </summary>
        public static bool IsDownloadable(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 给所有可下载的场景补图。同步读缓存，异步补下载。
        ///
        /// <paramref name="onApplied"/> 每成功应用一张就调一次（含命中和下载），
        /// 调用方据此重贴当前场景 —— 否则图下好了但画面还是旧的那张。
        /// </summary>
        public void EnsureAll(Action onApplied)
        {
            if (library == null || library.scenes == null) return;
            if (_running)
            {
                if (verboseLog) Debug.Log("[PetV1] 场景图：上一轮补图还没结束，本次跳过。");
                return;
            }
            StartCoroutine(EnsureRoutine(onApplied));
        }

        private IEnumerator EnsureRoutine(Action onApplied)
        {
            _running = true;

            var pending = new List<PetSceneLibrary.SceneEntry>();
            foreach (var e in library.scenes)
            {
                if (e == null || string.IsNullOrEmpty(e.sceneId)) continue;
                if (!IsDownloadable(e.sceneUrl)) continue;   // 走包内图

                string key = CacheKey(e.sceneId, e.sceneUrl);
                string path = Path.Combine(CacheDir, key + ".jpg");

                if (File.Exists(path))
                {
                    // 同一张图已经加载过就直接跳过，不要重复 new Texture2D。
                    // EnsureAll 一次启动会被调好几遍（Start 一次 + 每次配置变更一次），
                    // 每遍都重新加载的话 1080×1920 RGBA 一张 8MB，旧纹理还没人释放。
                    if (_handled.Contains(key) && e.background != null) continue;

                    var tex = LoadTexture(path, e.sceneId + "_cache");
                    if (tex != null)
                    {
                        e.background = tex;
                        if (!_handled.Contains(key))
                        {
                            CacheHits++;
                            _handled.Add(key);
                            if (verboseLog) Debug.Log($"[PetV1] 场景图命中缓存：{e.sceneId}");
                        }
                        onApplied?.Invoke();
                        continue;
                    }
                    // 文件坏了：删掉重下，不要一直卡在坏文件上
                    Debug.LogWarning($"[PetV1] 场景图缓存损坏，重新下载：{path}");
                    TryDelete(path);
                }

                if (!_handled.Contains(key)) pending.Add(e);
            }

            if (pending.Count == 0)
            {
                _running = false;
                if (verboseLog)
                    Debug.Log($"[PetV1] 场景图：无需下载（缓存命中 {CacheHits}，失败 {Failed}）");
                yield break;
            }

            if (verboseLog) Debug.Log($"[PetV1] 场景图：待下载 {pending.Count} 张");

            foreach (var e in pending)
            {
                string key = CacheKey(e.sceneId, e.sceneUrl);
                _handled.Add(key);   // 先登记，失败也不重复炸网络

                byte[] bytes = null;
                yield return DownloadRoutine(e.sceneUrl, b => bytes = b);

                if (bytes == null || bytes.Length == 0)
                {
                    Failed++;
                    Debug.LogWarning($"[PetV1] 场景图下载失败，继续用包内图：{e.sceneId} ← {e.sceneUrl}");
                    continue;
                }

                string path = Path.Combine(CacheDir, key + ".jpg");
                if (!WriteCache(path, bytes))
                {
                    // 存盘失败不影响本次显示，下次启动会重新下
                    Debug.LogWarning($"[PetV1] 场景图缓存写入失败（本次仍会显示）：{path}");
                }

                var tex = Decode(bytes, e.sceneId + "_remote");
                if (tex == null)
                {
                    Failed++;
                    Debug.LogWarning($"[PetV1] 场景图不是可解析的图片：{e.sceneUrl}");
                    continue;
                }

                e.background = tex;
                Downloaded++;
                Debug.Log($"[PetV1] 场景图已更新：{e.sceneId} {tex.width}×{tex.height} " +
                          $"{bytes.Length / 1024}KB");
                onApplied?.Invoke();
            }

            _running = false;
            Debug.Log($"[PetV1] 场景图补图完成：缓存命中 {CacheHits}，下载 {Downloaded}，失败 {Failed}");
        }

        private IEnumerator DownloadRoutine(string url, Action<byte[]> done)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = Mathf.CeilToInt(timeoutSeconds);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (verboseLog) Debug.LogWarning($"[PetV1] GET {url} 失败：{req.error}");
                    done?.Invoke(null);
                    yield break;
                }
                done?.Invoke(req.downloadHandler.data);
            }
        }

        // ------------------------------------------------------------------
        // 缓存读写
        // ------------------------------------------------------------------

        /// <summary>缓存文件名：sceneId + URL 哈希。URL 变了缓存自然失效。</summary>
        private static string CacheKey(string sceneId, string url)
        {
            return sceneId + "_" + StableHash(url).ToString("x8");
        }

        /// <summary>
        /// FNV-1a 32 位。不用 <c>string.GetHashCode()</c>：
        /// .NET 对它的实现不保证跨进程/跨版本稳定，拿它当文件名意味着
        /// 每次重启都可能算出一个新名字，缓存永远不命中。
        /// </summary>
        private static uint StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return h;
            }
        }

        private static Texture2D LoadTexture(string path, string name)
        {
            try
            {
                return Decode(File.ReadAllBytes(path), name);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PetV1] 读场景图缓存失败：{path} {e.Message}");
                return null;
            }
        }

        private static Texture2D Decode(byte[] bytes, string name)
        {
            if (bytes == null || bytes.Length < 16) return null;

            // JPG/PNG 魔数先挡一道：404 的 HTML 页面也会返回 200 字节，
            // 直接丢给 LoadImage 会打一串底层报错，看不出是服务端返回了网页。
            bool jpg = bytes[0] == 0xFF && bytes[1] == 0xD8;
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50
                       && bytes[2] == 0x4E && bytes[3] == 0x47;
            if (!jpg && !png) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                Destroy(tex);
                return null;
            }
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        private static bool WriteCache(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PetV1] 写场景图缓存失败：{path} {e.Message}");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 删不掉就算了，下次会重新写 */ }
        }

        /// <summary>清空全部缓存。排查「图换了但界面没变」时手工调一次。</summary>
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
                _handled.Clear();
                CacheHits = Downloaded = Failed = 0;
                Debug.Log("[PetV1] 场景图缓存已清空：" + CacheDir);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PetV1] 清空场景图缓存失败：" + e.Message);
            }
        }
    }
}
