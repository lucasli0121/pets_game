using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PetV1.Data
{
    /// <summary>
    /// 一次接口调用的结果。**不要用异常汇报错误**：
    /// 协程里抛异常会中断整个协程链，登录失败就等于客户端彻底哑掉，
    /// 而网络失败在小游戏里是常态，必须能落到「用本地数据继续跑」这条路上。
    /// </summary>
    public class PetApiResult
    {
        /// <summary>HTTP 通了，且业务 code==0。</summary>
        public bool Ok;

        /// <summary>业务码。0 成功，非 0 见 serv</summary>
        public int Code;

        public string Msg = "";
        public int HttpStatus;

        /// <summary>解出来的 data 节点。失败时为 null。</summary>
        public PetJsonValue Data;

        /// <summary>
        /// 原始响应文本。给「同一份响应有第二个消费者」用 ——
        /// 比如 config/get 里既有 food 又有 scene，
        /// <see cref="PetSceneConfigCache"/> 需要原始 JSON 才能自己剥壳解析。
        /// 不保留的话就得再发一次请求，而它没有 token，发了也是 401。
        /// </summary>
        public string RawText = "";

        /// <summary>鉴权失败（401/403）。这类失败要触发重新登录，而不是当网络错误重试。</summary>
        public bool Unauthorized;

        /// <summary>网络层错误（连不上、超时、DNS）。用它区分「服务不可达」与「服务回了错误」。</summary>
        public bool NetworkError;

        public override string ToString()
        {
            if (Ok) return "ok";
            if (NetworkError) return $"network({Msg})";
            return $"code={Code} http={HttpStatus} msg={Msg}";
        }
    }

    /// <summary>
    /// 客户端接口封装。每个接口一个 <c>*Routine</c>，配合 <c>yield return</c> 串流程。
    ///
    /// 为什么是协程而不是 async/await：
    /// Unity 的 UnityWebRequest 原生就是协程模型，async 需要在主线程同步上下文里 await，
    /// 在小游戏（WASM）下容易出现死锁。工程已有的 <see cref="PetSceneConfigCache"/>
    /// 也是协程，保持一致。
    ///
    /// 协议（实测后台 internal/api）：
    ///   响应统一 <c>{"code":0,"msg":"ok","data":{...}}</c>，code=0 才算成功；
    ///   鉴权走 <c>Authorization: Bearer &lt;token&gt;</c>（也兼容 X-Token / ?token=）；
    ///   **请求体不能带未知字段** —— 后台 decode 开了 DisallowUnknownFields，多一个就 400。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetApiClient : MonoBehaviour
    {
        [Header("服务地址")]
        [Tooltip("后台根地址。本地联调 http://127.0.0.1:8080；真机调试要写局域网 IP。\n" +
                 "必须带 http:// 或 https://，UnityWebRequest 不接受裸域名。")]
        [SerializeField] private string serverBaseUrl = "http://127.0.0.1:8080";

        [Tooltip("请求超时（秒）。超时按网络失败处理，业务层会落到本地数据。")]
        [SerializeField] private float timeoutSeconds = 8f;

        [Header("调试")]
        [SerializeField] private bool verboseLog;

        /// <summary>当前 token。空表示未登录。</summary>
        public string Token { get; private set; } = "";

        public string BaseUrl => serverBaseUrl;

        public bool HasToken => !string.IsNullOrEmpty(Token);

        public void Configure(string baseUrl, bool verbose)
        {
            if (!string.IsNullOrEmpty(baseUrl)) serverBaseUrl = baseUrl;
            if (verbose) verboseLog = true;
        }

        public void SetToken(string token) => Token = token ?? "";

        // ------------------------------------------------------------------
        // 底层请求
        // ------------------------------------------------------------------

        /// <summary>
        /// POST 一个 JSON 体并解出 <c>{code,msg,data}</c>。
        /// 调用方：<c>PetApiResult r = null; yield return api.PostRoutine(path, body, x => r = x);</c>
        /// </summary>
        public IEnumerator PostRoutine(string path, string jsonBody, Action<PetApiResult> done)
        {
            string url = serverBaseUrl.TrimEnd('/') + path;
            if (string.IsNullOrEmpty(jsonBody)) jsonBody = "{}";

            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(Token)) req.SetRequestHeader("Authorization", "Bearer " + Token);
            req.timeout = Mathf.CeilToInt(timeoutSeconds);

            if (verboseLog) Debug.Log($"[PetV1] → POST {url} {jsonBody}");

            yield return req.SendWebRequest();

            var result = new PetApiResult { HttpStatus = (int)req.responseCode };
            string text = req.downloadHandler != null ? req.downloadHandler.text : "";

            if (req.result != UnityWebRequest.Result.Success)
            {
                // 4xx/5xx 也可能带业务 JSON，先尝试解析再判类型
                if (req.responseCode == 401 || req.responseCode == 403)
                {
                    result.Unauthorized = true;
                    result.Msg = "登录已失效";
                }
                else if (req.responseCode == 0)
                {
                    result.NetworkError = true;
                    result.Msg = req.error ?? "无法连接服务端";
                }
                else
                {
                    result.Msg = req.error ?? ("HTTP " + req.responseCode);
                }

                var errored = PetJson.Parse(text);
                if (errored != null)
                {
                    result.Code = errored["code"].AsInt(-1);
                    result.Msg = errored["msg"].AsString(result.Msg);
                }

                if (verboseLog) Debug.LogWarning($"[PetV1] ← {path} 失败：{result}");
                req.Dispose();
                done?.Invoke(result);
                yield break;
            }

            var root = PetJson.Parse(text);
            if (root == null)
            {
                result.Msg = "响应不是合法 JSON";
                if (verboseLog) Debug.LogWarning($"[PetV1] ← {path} 原始响应：{text}");
                req.Dispose();
                done?.Invoke(result);
                yield break;
            }

            result.Code = root["code"].AsInt(-1);
            result.Msg = root["msg"].AsString("");
            result.Data = root["data"];
            result.RawText = text;
            result.Ok = result.Code == 0;

            if (verboseLog)
            {
                string preview = text.Length > 600 ? text.Substring(0, 600) + "..." : text;
                Debug.Log($"[PetV1] ← {path} {(result.Ok ? "ok" : "code=" + result.Code)} {preview}");
            }

            req.Dispose();
            done?.Invoke(result);
        }

        // ------------------------------------------------------------------
        // 业务接口
        // 请求体字段必须与后台 struct 完全一致（多了就 400）。
        // ------------------------------------------------------------------

        /// <summary>
        /// 登录。<paramref name="devOpenid"/> 只在后台 DEBUG 且开启 ALLOW_DEV_LOGIN 时有效，
        /// 生产必须换成 wx.login 的 code。
        /// </summary>
        public IEnumerator LoginRoutine(string devOpenid, Action<PetApiResult> done)
        {
            string body = "{\"devOpenid\":" + Quote(devOpenid) + ",\"nickname\":\"\"," +
                          "\"avatar\":\"\"}";
            yield return PostRoutine("/api/v1/login", body, done);
        }

        /// <summary>配置快照。<paramref name="version"/> 与服务端一致时只回 unchanged。</summary>
        public IEnumerator GetConfigRoutine(long version, Action<PetApiResult> done)
        {
            yield return PostRoutine("/api/v1/config/get", "{\"version\":" + version + "}", done);
        }

        /// <summary>同步服务端权威状态（宠物 + 积分 + 背包 + 已解锁场景）。</summary>
        public IEnumerator SyncStateRoutine(string petId, Action<PetApiResult> done)
        {
            string body = "{\"petId\":" + Quote(petId) + ",\"clientTs\":0}";
            yield return PostRoutine("/api/v1/sync-state", body, done);
        }

        /// <summary>领养宠物。首次领养时服务端会发初始积分（config_points.initial）。</summary>
        public IEnumerator AdoptRoutine(string petType, string name,
                                        int lively, int curiosity, int affection, int independence,
                                        Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"petType\":").Append(Quote(petType));
            sb.Append(",\"name\":").Append(Quote(name));
            sb.Append(",\"personality\":{");
            sb.Append("\"lively\":").Append(lively);
            sb.Append(",\"curiosity\":").Append(curiosity);
            sb.Append(",\"affection\":").Append(affection);
            sb.Append(",\"independence\":").Append(independence);
            sb.Append("}}");
            yield return PostRoutine("/api/v1/pet/adopt", sb.ToString(), done);
        }

        /// <summary>买食品。requestId 是幂等键，同一次点击重试必须复用同一个值。</summary>
        public IEnumerator BuyFoodRoutine(string requestId, string foodId, int count,
                                          Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestId\":").Append(Quote(requestId));
            sb.Append(",\"foodId\":").Append(Quote(foodId));
            sb.Append(",\"count\":").Append(count).Append("}");
            yield return PostRoutine("/api/v1/shop/buy-food", sb.ToString(), done);
        }

        /// <summary>喂食。服务端扣库存、加状态、算升级。</summary>
        public IEnumerator FeedRoutine(string requestId, string petId, string foodId,
                                       Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestId\":").Append(Quote(requestId));
            sb.Append(",\"petId\":").Append(Quote(petId));
            sb.Append(",\"foodId\":").Append(Quote(foodId)).Append("}");
            yield return PostRoutine("/api/v1/pet/feed", sb.ToString(), done);
        }

        /// <summary>
        /// 签到领奖。**已签不报错**：服务端返回 <c>alreadyClaimed=true</c> 与当前余额，
        /// 所以客户端可以放心地把它当「签到按钮」调，不用先查状态。
        /// </summary>
        public IEnumerator CheckInRoutine(string requestId, Action<PetApiResult> done)
        {
            yield return PostRoutine("/api/v1/checkin/claim",
                                     "{\"requestId\":" + Quote(requestId) + "}", done);
        }

        /// <summary>
        /// 查签到状态。**只读，不发奖**。
        ///
        /// 响应：<c>claimedToday</c> / <c>canClaim</c> / <c>streak</c> / <c>reward</c> /
        /// <c>recentDates</c> / <c>date</c> / <c>balance</c> / <c>dailyLimit</c>。
        ///
        /// 与 sync-state 里那个 <c>checkin</c> 字段的区别：sync-state 是全量同步
        /// （宠物 + 积分 + 背包 + 场景），为了看一眼签到状态发一次太重，
        /// 而且会顺带触发离线衰减结算。
        /// </summary>
        public IEnumerator CheckInStatusRoutine(Action<PetApiResult> done)
        {
            yield return PostRoutine("/api/v1/checkin/status", "{}", done);
        }

        /// <summary>
        /// 激励视频发奖。响应：<c>reward</c> / <c>balance</c> /
        /// <c>rejectedReason</c> / <c>remainToday</c>。
        ///
        /// rejectedReason 不是错误：广告位没开、今日次数用完、播放太频繁
        /// 都走这个字段正常返回（http 200），客户端要按它提示，不能当失败。
        ///
        /// clientCompleted 只作参考记进 ad_events，真正的兜底是服务端的
        /// 每日上限 + 最小间隔 + requestId 幂等（见 AdService.ClaimReward）。
        /// </summary>
        public IEnumerator AdRewardRoutine(string requestId, string adUnit,
                                           string placement, bool clientCompleted,
                                           Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestId\":").Append(Quote(requestId));
            sb.Append(",\"adUnit\":").Append(Quote(adUnit));
            sb.Append(",\"placement\":").Append(Quote(placement));
            sb.Append(",\"clientCompleted\":").Append(clientCompleted ? "true" : "false");
            sb.Append("}");
            yield return PostRoutine("/api/v1/ad/reward", sb.ToString(), done);
        }

        /// <summary>
        /// 插屏曝光埋点。**只记录不发奖**（文档 13.8 ②）。
        /// 响应只有 <c>accepted</c> —— 广告位没开时也是 200 + accepted=false。
        /// </summary>
        public IEnumerator AdImpressionRoutine(string requestId, string adUnit,
                                               string placement, bool completed,
                                               Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestId\":").Append(Quote(requestId));
            sb.Append(",\"adUnit\":").Append(Quote(adUnit));
            sb.Append(",\"placement\":").Append(Quote(placement));
            sb.Append(",\"completed\":").Append(completed ? "true" : "false");
            sb.Append("}");
            yield return PostRoutine("/api/v1/ad/impression", sb.ToString(), done);
        }

        /// <summary>解锁场景。响应带 <c>scenes</c>（解锁后的全量列表）与 <c>balance</c>。</summary>
        public IEnumerator UnlockSceneRoutine(string requestId, string sceneId,
                                              Action<PetApiResult> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestId\":").Append(Quote(requestId));
            sb.Append(",\"sceneId\":").Append(Quote(sceneId)).Append("}");
            yield return PostRoutine("/api/v1/scene/unlock", sb.ToString(), done);
        }

        /// <summary>切换当前场景。响应只有 <c>activeSceneId</c>。</summary>
        public IEnumerator UseSceneRoutine(string sceneId, Action<PetApiResult> done)
        {
            yield return PostRoutine("/api/v1/scene/use",
                                     "{\"sceneId\":" + Quote(sceneId) + "}", done);
        }

        /// <summary>
        /// 幂等键。每次「用户意图」生成一个，重试沿用同一个。
        ///
        /// 不能用时间戳：同一毫秒内连点两下会撞键，后台会把第二次当重放直接返回第一次的结果
        /// （看起来像「买了没扣钱」）。
        /// </summary>
        public static string NewRequestId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>JSON 字符串字面量。只用到这几个转义，够覆盖 openid / id / 名字。</summary>
        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
