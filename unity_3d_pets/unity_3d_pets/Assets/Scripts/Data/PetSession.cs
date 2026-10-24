using System;
using System.Collections;
using System.Collections.Generic;
using PetV1.Model;
using PetV1.UI;
using UnityEngine;

namespace PetV1.Data
{
    /// <summary>
    /// 会话与数据编排。**整个工程唯一的数据来源。**
    ///
/// 职责边界：
/// - 启动时把「登录 → 拉配置 → 同步状态」串成一条链
/// - 没有宠物时通知 UI 走领养流程（<see cref="AdoptionRequired"/>），不静默代领
/// - 把服务端返回值写进 <see cref="PetRuntimeState"/> 与 <see cref="FoodCatalog"/>
/// - 对外提供买/喂两个动作，写操作一律「先请求、再用返回值覆盖」
    ///
    /// **不负责**：算经验、算余额、判升级、算衰减。这些全在服务端（PRD 4.2），
    /// 客户端算了迟早跟后台对不上，而且对不上的时候没人能发现。
    ///
    /// 离线策略：任一请求失败都**不阻断启动**，落到包内 asset + 本地 state 继续跑，
    /// 但把 <see cref="Online"/> 置 false —— 商城页据此禁用「购买」，
    /// 否则会出现「点了看着买成功、其实服务端没这笔账」的最坏情况。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetSession : MonoBehaviour
    {
        [Header("依赖")]
        [SerializeField] private PetApiClient api;

        [Header("登录")]
        [Tooltip("本地联调用的 openid。后台 DEBUG 且 ALLOW_DEV_LOGIN=true 时有效，生产必须换成 wx.login。")]
        [SerializeField] private string devOpenid = "unity_proto_01";

        [Tooltip("启动时自动走一遍登录-同步。关掉用于纯离线看 UI。")]
        [SerializeField] private bool autoLogin = true;

        [Tooltip("没有宠物时进入领养界面（启动页 + 选宠）。关掉用于调试主界面。")]
        [SerializeField] private bool autoAdopt = true;

        [Tooltip("领养用的宠物名。后台限制最多 12 个字。选宠页展板没有起名输入框，用这个默认名。")]
        [SerializeField] private string adoptName = "旺财";

        /// <summary>服务端可达且已登录。</summary>
        public bool Online { get; private set; }

        /// <summary>有请求在飞。UI 据此禁用按钮，防连点重复下单。</summary>
        public bool Busy { get; private set; }

        /// <summary>最近一次失败原因，给 UI 显示用。空表示没失败。</summary>
        public string LastError { get; private set; } = "";

        /// <summary>服务端宠物 id。喂食必须带上它。</summary>
        public string PetId { get; private set; } = "";

        /// <summary>配置版本。传给 config/get 做增量判断。</summary>
        public long ConfigVersion { get; private set; }

        /// <summary>
        /// 服务端已经下发过签到状态。false 时 UI 不该把「没签」当成真的没签 ——
        /// 那只是还不知道。
        /// </summary>
        public bool HasCheckInStatus { get; private set; }

        /// <summary>今天签过了没。</summary>
        public bool CheckinClaimedToday => _state != null && _state.checkinClaimedToday;

        /// <summary>连续签到天数（含今天）。今天没签时为 0。</summary>
        public int CheckinStreak => _state != null ? _state.checkinStreak : 0;

        /// <summary>
        /// 现在点签到能不能领到奖励。
        ///
        /// 与 <see cref="CheckinClaimedToday"/> 分开：服务端说能领就以它为准。
        /// </summary>
        public bool CheckinCanClaim => _state != null && _state.checkinCanClaim;

        /// <summary>
        /// 服务端已经下发过权威状态（sync-state 成功落库）。
        ///
        /// 用它关掉 <c>PetHomeUI.EnsureStateCollections</c> 的本地兜底 ——
        /// 那个兜底会在「背包为空」时塞进 CreateDefault 的 3/2/5/2/1，
        /// 而后台新用户背包本来就是空的，结果就是客户端显示有货、服务端一件都没有。
        /// </summary>
        public bool RemoteAuthoritative { get; private set; }

        /// <summary>数据变了（登录完成 / 同步完成 / 买完 / 喂完）。UI 订阅它刷新。</summary>
        public event Action Changed;

        /// <summary>
        /// 登录上了但账号里一只宠物都没有：该走领养界面了。
        ///
        /// 为什么不再是启动链里静默 auto-adopt：那样玩家看不到「选宠」这一步，
        /// 狗是凭空出现的，展板的「启动页 → 选宠」整段被跳过。
        /// 领养是**玩家的一个决定**（点「选择它」），不是客户端的后台动作。
        ///
        /// 只在 bootstrap 走完且在线时触发一次。离线不触发 ——
        /// 领养要发请求，离线触发只会得到一个点不动的按钮。
        /// </summary>
        public event Action AdoptionRequired;

        /// <summary>提示文案。UI 订阅后走 Toast。</summary>
        public event Action<string> Notice;

        private PetHomeUI _ui;
        private FoodCatalog _catalog;
        private PetRuntimeState _state;
        private bool _bootstrapDone;

        public PetApiClient Api => api;

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        /// <summary>
        /// 由 <see cref="PetHomeUI.Start"/> 在 UI 建好之后调用。
        ///
        /// 为什么是显式调用而不是自己在 Start 里找：
        /// 依赖注入写在明处，出问题时一眼能看出「谁先谁后」；
        /// 而且 <c>FindAnyObjectByType</c> 拿到的是不是刚重建的那份实例，
        /// 在「销毁旧树建新树」的路径上很难保证。
        /// </summary>
        public void Begin(PetHomeUI ui, FoodCatalog catalog, PetRuntimeState state)
        {
            _ui = ui;
            _catalog = catalog;
            _state = state;

            if (api == null) api = GetComponent<PetApiClient>();
            if (api == null) api = gameObject.AddComponent<PetApiClient>();

            if (_bootstrapDone) return;
            _bootstrapDone = true;

            if (autoLogin) StartCoroutine(BootstrapRoutine());
            else SetOffline("未开启自动登录");
        }

        /// <summary>手动重试。断网恢复后 UI 上给一个入口。</summary>
        public void Retry()
        {
            if (Busy || _ui == null) return;
            StartCoroutine(BootstrapRoutine());
        }

        // ------------------------------------------------------------------
        // 启动链
        // ------------------------------------------------------------------

        private IEnumerator BootstrapRoutine()
        {
            Busy = true;
            LastError = "";
            RaiseChanged();

            // ---- 1. 登录 ----
            PetApiResult login = null;
            yield return api.LoginRoutine(devOpenid, r => login = r);
            if (login == null || !login.Ok)
            {
                Busy = false;
                SetOffline("登录失败：" + (login != null ? login.ToString() : "无响应"));
                yield break;
            }

            string token = login.Data["token"].AsString("");
            if (string.IsNullOrEmpty(token))
            {
                Busy = false;
                SetOffline("登录响应里没有 token");
                yield break;
            }
            api.SetToken(token);
            // 场景配置缓存自己不会登录，把 token 给它，
            // 免得它单独跑时发一个必然 401 的请求。
            BindSceneCacheToken(token);

            // 登录响应已经带了 pets / points / assets，先落一次，
            // 这样即使后面 config/get 挂了，界面也是真实数据而不是编的。
            PetId = PickPetId(login.Data);
            ApplyLoginSnapshot(login.Data);

            // ---- 2. 没有宠物：通知 UI 走领养流程 ----
            // 不再在这里静默领养。初始积分（config_points.initial，默认 500）与
            // 首次领养赠送食品都在服务端 Adopt 里发，玩家点完「选择它」一样拿得到。
            // 提前静默领养会把展板的「启动页 → 选宠」整段跳过，狗凭空出现。

            // ---- 3. 拉配置（food / points / level） ----
            PetApiResult cfg = null;
            yield return api.GetConfigRoutine(ConfigVersion, r => cfg = r);
            if (cfg != null && cfg.Ok)
            {
                ConfigVersion = cfg.Data["version"].AsLong(ConfigVersion);
                bool unchanged = cfg.Data["unchanged"].AsBool(false);

                // unchanged 时服务端只回 {unchanged, version}，没有 config 也没有 scene。
                // 那时不能把这份响应喂给场景缓存 —— 它会报「响应里没有 scene 数组」，
                // 而实际上缓存里的配置就是最新的（Retry 路径上必现，已实测）。
                if (!unchanged)
                {
                    ApplyConfig(cfg.Data["config"]);

                    // 同一份响应里还有 scene。直接喂给场景配置缓存，
                    // 省掉它自己再发一次请求 —— 它没有登录态，发了也是 401。
                    ApplySceneConfig(cfg.RawText);
                }
            }
            else
            {
                // 配置拉不到不算致命：asset 里有兜底的五条。
                // 但必须写清楚，否则「后台改了价格界面没变」会被当成 bug 排查半天。
                Debug.LogWarning("[PetV1] 配置拉取失败，商品用包内 asset 兜底：" +
                                 (cfg != null ? cfg.ToString() : "无响应"));
            }

            // ---- 4. 同步权威状态 ----
            //
            // 没有宠物时**不能**去 sync：服务端会直接回
            // 「找不到宠物，请先领养」（code 2302），走 else 分支会把整个会话
            // 判成离线，领养按钮跟着变灰，新玩家永远进不了主场景。
            // 「还没领养」是正常状态不是故障，所以这里提前分流：
            // 标记在线（token 已拿到、配置已拉到，够领养用了），直接进领养流程。
            if (autoAdopt && string.IsNullOrEmpty(PetId))
            {
                Busy = false;
                Online = true;
                LastError = "";
                Debug.Log("[PetV1] 账号里没有宠物，跳过 sync-state，进入领养流程。");
                RaiseAdoptionRequired();
                RaiseChanged();
                yield break;
            }

            PetApiResult sync = null;
            yield return api.SyncStateRoutine(PetId, r => sync = r);
            if (sync != null && sync.Ok)
            {
                ApplySyncState(sync.Data);
                // 必须在 ApplySyncState 之后置位：它内部会 ApplyAssets 清空并重填 inventory，
                // 提前置位的话中间那一次 State 访问仍可能被本地兜底覆盖。
                RemoteAuthoritative = true;
                Online = true;
                LastError = "";
                if (sync.Data != null && sync.Data["configVersion"].AsLong(0) > ConfigVersion)
                    ConfigVersion = sync.Data["configVersion"].AsLong(ConfigVersion);
            }
            else
            {
                Busy = false;
                SetOffline("状态同步失败：" + (sync != null ? sync.ToString() : "无响应"));
                yield break;
            }

            Busy = false;
            Debug.Log($"[PetV1] 已连上服务端：petId={PetId} 积分={_state.points} " +
                      $"背包={_state.inventory.Count} 项 配置v{ConfigVersion}");
            RaiseChanged();
        }

        private void RaiseAdoptionRequired()
        {
            try { AdoptionRequired?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------------
        // 领养
        // ------------------------------------------------------------------

        /// <summary>
        /// 领养一只宠物（选宠页的「选择它」）。name 传空串用 <see cref="adoptName"/>。
        ///
        /// 为什么领养完必须补一次 sync-state：<c>pet/adopt</c> 的响应体只有
        /// <c>{"pet": ...}</c>（见 internal/api/client.go adoptPet），
        /// 初始积分和首次领养赠送的食品都在服务端另外入账，不在这个响应里。
        /// 不补sync 的话玩家领完看到的是 0 积分、空背包，而服务端明明发了。
        /// </summary>
        public void Adopt(string name, int lively, int curiosity, int affection,
                          int independence, Action<bool, string> done)
        {
            if (Busy) return;
            if (!Online)
            {
                done?.Invoke(false, "没连上服务端，暂时不能领养");
                return;
            }
            if (string.IsNullOrEmpty(name)) name = adoptName;
            StartCoroutine(AdoptRoutine(name, lively, curiosity, affection, independence, done));
        }

        private IEnumerator AdoptRoutine(string name, int lively, int curiosity,
                                         int affection, int independence,
                                         Action<bool, string> done)
        {
            Busy = true;
            RaiseChanged();

            PetApiResult adopt = null;
            yield return api.AdoptRoutine("dog", name, lively, curiosity, affection,
                                          independence, r => adopt = r);
            if (adopt == null || !adopt.Ok)
            {
                Busy = false;
                string msg = adopt != null ? adopt.ToString() : "无响应";
                Debug.LogWarning("[PetV1] 领养失败：" + msg);
                done?.Invoke(false, "领养失败");
                RaiseChanged();
                yield break;
            }

            PetId = adopt.Data["pet"]["id"].AsString("");
            if (_state != null) _state.petId = PetId;
            string petName = adopt.Data["pet"]["name"].AsString("");
            if (_state != null && !string.IsNullOrEmpty(petName)) _state.petName = petName;

            // 补一次 sync-state：初始积分 + 赠送食品 + 宠物四状态。
            PetApiResult sync = null;
            yield return api.SyncStateRoutine(PetId, r => sync = r);
            if (sync != null && sync.Ok)
            {
                ApplySyncState(sync.Data);
                RemoteAuthoritative = true;
                Online = true;
                LastError = "";
            }
            else
            {
                // 宠物已经领到了，只是积分/背包没同步下来。
                // 不当成失败：玩家已经有一只狗了，报错会让人以为要点第二次。
                Debug.LogWarning("[PetV1] 领养后同步失败：" +
                                 (sync != null ? sync.ToString() : "无响应"));
            }

            Busy = false;
            Debug.Log($"[PetV1] 领养完成：petId={PetId} name={_state.petName} " +
                      $"积分={_state.points} 背包={_state.inventory.Count} 项");
            RaiseChanged();
            done?.Invoke(true, "领养成功");
        }

        private void SetOffline(string reason)
        {
            Online = false;
            LastError = reason ?? "";
            Debug.LogWarning("[PetV1] 离线运行（商品/积分用本地数据，购买已禁用）：" + LastError);
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void RaiseNotice(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            try
            {
                if (Notice != null) Notice.Invoke(msg);
                else if (_ui != null) _ui.ShowToast(msg);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------------
        // 数据落位
        // ------------------------------------------------------------------

        /// <summary>登录响应：pets / points / assets。字段与 domain.LoginResult 对齐。</summary>
        private void ApplyLoginSnapshot(PetJsonValue data)
        {
            if (data == null) return;
            if (data["points"] != null) _state.points = data["points"]["balance"].AsInt(_state.points);
            ApplyAssets(data["assets"]);
        }

        /// <summary>sync-state 响应：pet + points + assets + configVersion。</summary>
        private void ApplySyncState(PetJsonValue data)
        {
            if (data == null) return;

            var pet = data["pet"];
            if (pet != null)
            {
                string id = pet["id"].AsString("");
                if (!string.IsNullOrEmpty(id)) PetId = id;

                _state.petId = PetId;
                _state.petType = pet["petType"].AsString(_state.petType);
                string nm = pet["name"].AsString("");
                if (!string.IsNullOrEmpty(nm)) _state.petName = nm;

                _state.level = pet["level"].AsInt(_state.level);
                _state.exp = pet["exp"].AsLong(_state.exp);
                _state.expInLevel = pet["expInLevel"].AsLong(_state.expInLevel);
                _state.expRequired = pet["expRequired"].AsLong(_state.expRequired);
                _state.satiety = pet["satiety"].AsInt(_state.satiety);
                _state.happiness = pet["happiness"].AsInt(_state.happiness);
                _state.satietyState = pet["satietyState"].AsString(_state.satietyState);
                _state.happinessState = pet["happinessState"].AsString(_state.happinessState);

                var p = pet["personality"];
                if (p != null)
                {
                    _state.lively = p["lively"].AsInt(_state.lively);
                    _state.curiosity = p["curiosity"].AsInt(_state.curiosity);
                    _state.affection = p["affection"].AsInt(_state.affection);
                    _state.independence = p["independence"].AsInt(_state.independence);
                }
            }

            if (data["points"] != null) _state.points = data["points"]["balance"].AsInt(_state.points);
            ApplyAssets(data["assets"]);
            ApplyCheckIn(data["checkin"]);
        }

        /// <summary>
        /// 签到状态（sync-state 的 <c>checkin</c>，或 checkin/status 的响应）。
        ///
        /// 为什么不让客户端用 claim 查：claim 会发奖，不能当只读查询用。
        /// 也不让客户端拿 streak 推算 7 天格子：streak 是「从今天往前连续」，
        /// 今天没签时它是 0，推不出「昨天签了、今天还没签」。
        ///
        /// sync-state 与 checkin/status 两个来源的字段结构一致，所以共用这一段。
        /// </summary>
        private void ApplyCheckIn(PetJsonValue c)
        {
            if (c == null || c.IsNull) return;

            _state.checkinClaimedToday = c["claimedToday"].AsBool(false);
            _state.checkinStreak = c["streak"].AsInt(0);
            _state.checkinReward = c["reward"].AsInt(_state.checkinReward);
            // canClaim 缺省时退回 !claimedToday：老版本后台没这个字段时行为一致
            _state.checkinCanClaim = c["canClaim"].AsBool(!_state.checkinClaimedToday);
            _state.checkinServerDate = c["date"].AsString(_state.checkinServerDate);

            _state.EnsureCollections();
            _state.checkinRecentDates.Clear();
            var arr = c["recentDates"];
            if (arr != null && arr.IsArray)
            {
                foreach (var d in arr.Items)
                {
                    string date = d.AsString("");
                    if (!string.IsNullOrEmpty(date)) _state.checkinRecentDates.Add(date);
                }
            }

            HasCheckInStatus = true;
        }

        /// <summary>assets：inventory 是动态键名的 map，这里整份替换而不是合并。</summary>
        private void ApplyAssets(PetJsonValue assets)
        {
            if (assets == null) return;
            _state.EnsureCollections();

            if (assets["points"] != null) _state.points = assets["points"]["balance"].AsInt(_state.points);

            var inv = assets["inventory"];
            if (inv != null && inv.IsObject)
            {
                _state.inventory.Clear();
                foreach (var key in inv.Keys) _state.inventory[key] = inv[key].AsInt(0);
            }

            var scenes = assets["scenes"];
            if (scenes != null && scenes.IsArray)
            {
                _state.unlockedScenes.Clear();
                foreach (var s in scenes.Items)
                {
                    string id = s.AsString("");
                    if (!string.IsNullOrEmpty(id)) _state.unlockedScenes.Add(id);
                }
            }

            string active = assets["activeScene"].AsString("");
            if (!string.IsNullOrEmpty(active)) _state.activeScene = active;
        }

        /// <summary>
        /// config.food → FoodCatalog.items。
        ///
        /// 字段映射（后台 domain.FoodItem → 客户端 FoodItemConfig）：
        ///   id→id / name→name / category→category / price→price /
        ///   satiety→satiety / happiness→happiness / exp→exp /
        ///   **icon→iconKey** / sort→sort / enabled→enabled / minLevel→minLevel
        /// category 是字符串（MAIN/SNACK/SPECIAL），要转枚举，转不出来按 MAIN 处理。
        /// </summary>
        private void ApplyConfig(PetJsonValue config)
        {
            if (config == null) return;

            var food = config["food"];
            if (food != null && food.IsArray && food.Count > 0)
            {
                var list = new List<FoodItemConfig>(food.Count);
                foreach (var f in food.Items)
                {
                    if (f == null || f.IsNull) continue;
                    string id = f["id"].AsString("");
                    if (string.IsNullOrEmpty(id)) continue;

                    list.Add(new FoodItemConfig
                    {
                        id = id,
                        name = f["name"].AsString(id),
                        category = ParseCategory(f["category"].AsString("MAIN")),
                        price = f["price"].AsInt(0),
                        satiety = f["satiety"].AsInt(0),
                        happiness = f["happiness"].AsInt(0),
                        exp = f["exp"].AsInt(0),
                        iconKey = f["icon"].AsString(""),
                        sort = f["sort"].AsInt(0),
                        enabled = f["enabled"].AsBool(true),
                        minLevel = f["minLevel"].AsInt(1),
                    });
                }

                if (list.Count > 0)
                {
                    _catalog.ReplaceItems(list);
                    Debug.Log($"[PetV1] 已应用服务端商品配置 v{ConfigVersion}：{list.Count} 条");
                }
            }
            else
            {
                Debug.LogWarning("[PetV1] config 里没有 food 数组，商品继续用包内 asset 兜底。");
            }

            // ---- 广告位与广告奖励 ----
            // 后台可配：ad[] 里每个广告位有 adUnitId / adType / placement / enabled，
            // points.adReward 是激励视频一次给多少。客户端不写死这两个值 ——
            // 写死的后果是后台换了广告位 id，真机上永远拉不到广告，而编辑器里看不出来。
            var points = config["points"];
            if (points != null)
                _state.adReward = points["adReward"].AsInt(_state.adReward);

            var ads = config["ad"];
            if (ads != null && ads.IsArray)
            {
                foreach (var a in ads.Items)
                {
                    if (a == null || a.IsNull) continue;
                    string type = a["adType"].AsString("");
                    bool enabled = a["enabled"].AsBool(false);
                    string unitId = a["adUnitId"].AsString("");
                    if (string.IsNullOrEmpty(unitId)) continue;

                    if (type == "rewarded")
                    {
                        _state.adRewardedUnitId = unitId;
                        _state.adRewardedEnabled = enabled;
                    }
                    else if (type == "interstitial")
                    {
                        _state.adInterstitialUnitId = unitId;
                        _state.adInterstitialEnabled = enabled;
                    }
                }
                Debug.Log($"[PetV1] 广告位配置：rewarded={_state.adRewardedUnitId}" +
                          $"(enabled={_state.adRewardedEnabled}) " +
                          $"interstitial={_state.adInterstitialUnitId}" +
                          $"(enabled={_state.adInterstitialEnabled}) " +
                          $"reward={_state.adReward}");
            }
        }

        private static FoodCategory ParseCategory(string code)
        {
            if (string.Equals(code, "SNACK", StringComparison.OrdinalIgnoreCase)) return FoodCategory.Snack;
            if (string.Equals(code, "SPECIAL", StringComparison.OrdinalIgnoreCase)) return FoodCategory.Special;
            return FoodCategory.Main;
        }

        // ------------------------------------------------------------------
        // 场景配置（把同一份 config/get 响应转交给 PetSceneConfigCache）
        // ------------------------------------------------------------------

        private PetSceneConfigCache _sceneCache;

        private PetSceneConfigCache FindSceneCache()
        {
            if (_sceneCache == null) _sceneCache = FindAnyObjectByType<PetSceneConfigCache>();
            return _sceneCache;
        }

        private void BindSceneCacheToken(string token)
        {
            var c = FindSceneCache();
            if (c != null) c.SetToken(token);
        }

        /// <summary>
        /// 把 config/get 的原始响应喂给 <see cref="PetSceneConfigCache"/>。
        ///
        /// 为什么不让它自己拉：它没有登录态，自己发请求必然 401，
        /// 而那份 401 的日志会把人引到「后台挂了」这个错误方向。
        /// 同一份响应里既有 food 又有 scene，一次请求两个消费者。
        /// </summary>
        private void ApplySceneConfig(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return;
            var c = FindSceneCache();
            if (c == null)
            {
                Debug.LogWarning("[PetV1] 场景里没有 PetSceneConfigCache，场景配置不会下发。");
                return;
            }
            var lib = _ui != null ? _ui.Library : null;
            if (lib == null)
            {
                Debug.LogWarning("[PetV1] 没有 PetSceneLibrary，场景配置无从应用。");
                return;
            }
            if (c.TryApplyResponse(lib, rawText))
                Debug.Log("[PetV1] 场景配置已应用（复用 config/get 的同一份响应）");
        }

        private static string PickPetId(PetJsonValue data)
        {
            if (data == null) return "";
            string active = data["activePetId"].AsString("");
            if (!string.IsNullOrEmpty(active)) return active;
            var pets = data["pets"];
            if (pets != null && pets.IsArray && pets.Count > 0)
                return pets[0]["id"].AsString("");
            return "";
        }

        // ------------------------------------------------------------------
        // 写操作
        // ------------------------------------------------------------------

        /// <summary>
        /// 购买。成功后用服务端返回的 balance + inventory 覆盖本地。
        ///
        /// 幂等键在**每次用户点击时**生成，重试复用同一个（这里没有自动重试，
        /// 由用户再点一次就算新的意图）。
        /// </summary>
        public void BuyFood(FoodItemConfig food, Action<bool, string> done)
        {
            if (food == null) { done?.Invoke(false, "商品不存在"); return; }
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能购买"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }

            StartCoroutine(BuyRoutine(food, done));
        }

        private IEnumerator BuyRoutine(FoodItemConfig food, Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            yield return api.BuyFoodRoutine(PetApiClient.NewRequestId(), food.id, 1, x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 购买失败 {food.id}：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "购买失败" : msg);
                yield break;
            }

            // 余额与库存都由服务端给，客户端不自己加
            _state.points = r.Data["balance"].AsInt(_state.points);
            var inv = r.Data["inventory"];
            if (inv != null && inv.IsObject)
            {
                _state.EnsureCollections();
                foreach (var key in inv.Keys) _state.inventory[key] = inv[key].AsInt(0);
            }

            Debug.Log($"[PetV1] 购买成功 {food.id} ×1 余额={_state.points}");
            RaiseChanged();
            done?.Invoke(true, $"买到 {food.name} ×1");
        }

        /// <summary>
        /// 喂食。服务端扣库存、加状态、判升级，客户端只落返回值。
        ///
        /// 等级/经验/饱腹/快乐全部以 <c>data.pet</c> 为准，
        /// **不要**拿 food.satiety 自己加 —— 服务端可能夹了上限（100）、
        /// 也可能命中每日上限，自己加一定对不上。
        /// </summary>
        public void Feed(FoodItemConfig food, Action<bool, string> done)
        {
            if (food == null) { done?.Invoke(false, "食品不存在"); return; }
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能喂食"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }
            if (string.IsNullOrEmpty(PetId)) { done?.Invoke(false, "还没有宠物"); return; }

            StartCoroutine(FeedRoutine(food, done));
        }

        private IEnumerator FeedRoutine(FoodItemConfig food, Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            yield return api.FeedRoutine(PetApiClient.NewRequestId(), PetId, food.id, x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 喂食失败 {food.id}：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "喂食失败" : msg);
                yield break;
            }

            var pet = r.Data["pet"];
            if (pet != null)
            {
                _state.level = pet["level"].AsInt(_state.level);
                _state.exp = pet["exp"].AsLong(_state.exp);
                _state.expInLevel = pet["expInLevel"].AsLong(_state.expInLevel);
                _state.expRequired = pet["expRequired"].AsLong(_state.expRequired);
                _state.satiety = pet["satiety"].AsInt(_state.satiety);
                _state.happiness = pet["happiness"].AsInt(_state.happiness);
                _state.satietyState = pet["satietyState"].AsString(_state.satietyState);
                _state.happinessState = pet["happinessState"].AsString(_state.happinessState);
            }

            _state.points = r.Data["balance"].AsInt(_state.points);
            var inv = r.Data["inventory"];
            if (inv != null && inv.IsObject)
            {
                _state.EnsureCollections();
                foreach (var key in inv.Keys) _state.inventory[key] = inv[key].AsInt(0);
            }

            bool levelUp = r.Data["levelUp"].AsBool(false);
            int reward = r.Data["levelupReward"].AsInt(0);
            string notice = levelUp
                ? $"{food.name} 吃完了，升到 Lv.{_state.level}" + (reward > 0 ? $"，奖励 {reward} 积分" : "")
                : $"{food.name} 吃完了，很满足";

            Debug.Log($"[PetV1] 喂食成功 {food.id} 饱腹={_state.satiety} 快乐={_state.happiness} " +
                      $"等级={_state.level} 余额={_state.points}");
            RaiseChanged();
            done?.Invoke(true, notice);
        }

        // ------------------------------------------------------------------
        // 场景
        // ------------------------------------------------------------------

        /// <summary>
        /// 解锁场景。服务端扣积分、把 sceneId 加进 assets.scenes。
        ///
        /// 成功后**不自动切过去** —— 由调用方决定要不要接着调 <see cref="UseScene"/>。
        /// 分成两步而不是在一次请求里做完：后台是两个独立接口（unlock / use），
        /// 而且切换失败不该让已经付了钱的解锁看起来失败。
        /// </summary>
        public void UnlockScene(PetSceneLibrary.SceneEntry entry, Action<bool, string> done)
        {
            if (entry == null) { done?.Invoke(false, "场景不存在"); return; }
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能解锁"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }

            StartCoroutine(UnlockSceneRoutine(entry, done));
        }

        private IEnumerator UnlockSceneRoutine(PetSceneLibrary.SceneEntry entry,
                                               Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            yield return api.UnlockSceneRoutine(PetApiClient.NewRequestId(), entry.sceneId, x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 解锁场景失败 {entry.sceneId}：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "解锁失败" : msg);
                yield break;
            }

            _state.points = r.Data["balance"].AsInt(_state.points);
            ApplySceneList(r.Data["scenes"]);
            Debug.Log($"[PetV1] 解锁场景成功 {entry.sceneId} 花费={(r.Data["cost"].AsInt(0))} " +
                      $"余额={_state.points} 已解锁={_state.unlockedScenes.Count}");
            RaiseChanged();
            done?.Invoke(true, $"已解锁 {entry.displayName}");
        }

        /// <summary>切换当前场景。</summary>
        public void UseScene(string sceneId, Action<bool, string> done)
        {
            if (string.IsNullOrEmpty(sceneId)) { done?.Invoke(false, "场景不存在"); return; }
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能切换场景"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }

            StartCoroutine(UseSceneRoutine(sceneId, done));
        }

        private IEnumerator UseSceneRoutine(string sceneId, Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            yield return api.UseSceneRoutine(sceneId, x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 切换场景失败 {sceneId}：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "切换失败" : msg);
                yield break;
            }

            string active = r.Data["activeSceneId"].AsString(sceneId);
            if (!string.IsNullOrEmpty(active)) _state.activeScene = active;
            Debug.Log($"[PetV1] 切换场景成功 {active}");
            RaiseChanged();
            done?.Invoke(true, "");
        }

        /// <summary>整份替换已解锁场景列表（服务端返回的是全量，不是增量）。</summary>
        private void ApplySceneList(PetJsonValue scenes)
        {
            if (scenes == null || !scenes.IsArray) return;
            _state.EnsureCollections();
            _state.unlockedScenes.Clear();
            foreach (var s in scenes.Items)
            {
                string id = s.AsString("");
                if (!string.IsNullOrEmpty(id)) _state.unlockedScenes.Add(id);
            }
        }

        // ------------------------------------------------------------------
        // 签到
        // ------------------------------------------------------------------

        /// <summary>
        /// 主动查一次签到状态（<c>POST /api/v1/checkin/status</c>）。
        ///
        /// 为什么需要它：sync-state 只在启动时拉一次。玩家把游戏挂在后台跨过零点，
        /// 或者在另一台设备上签了，回来看到的还是昨天的状态 ——
        /// 界面显示「今天已签到」，点下去服务端却说「今天还没签」。
        ///
        /// 这个接口是**只读**的，不发奖、不写库，弹窗打开时调一次很安全。
        /// </summary>
        /// <param name="done">ok=是否拉取成功，msg=失败原因（成功时为空串）。</param>
        public void RefreshCheckInStatus(Action<bool, string> done = null)
        {
            if (!Online) { done?.Invoke(false, "没连上服务端"); return; }

            StartCoroutine(RefreshCheckInStatusRoutine(done));
        }

        private IEnumerator RefreshCheckInStatusRoutine(Action<bool, string> done)
        {
            PetApiResult r = null;
            yield return api.CheckInStatusRoutine(x => r = x);

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 签到状态查询失败：{r}");
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "查询失败" : msg);
                yield break;
            }

            ApplyCheckIn(r.Data);
            // 余额一并覆盖：签到状态接口也返回 balance，不用再发一次 sync-state
            int bal = r.Data["balance"].AsInt(-1);
            if (bal >= 0) _state.points = bal;

            Debug.Log($"[PetV1] 签到状态已刷新：已签={_state.checkinClaimedToday} " +
                      $"可领={_state.checkinCanClaim} 连续={_state.checkinStreak} " +
                      $"余额={_state.points}");
            RaiseChanged();
            done?.Invoke(true, "");
        }

        /// <summary>
        /// 签到领奖。已签的调用不会报错：服务端返回 <c>alreadyClaimed=true</c>、余额不变。
        /// 所以「签到」按钮可以始终可点，不必先查状态 ——
        /// 后台的 <c>checkin/status</c> 是只读查询接口，专门用来查「今天签没签」。
        /// </summary>
        public void ClaimCheckIn(Action<bool, string> done)
        {
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能签到"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }

            StartCoroutine(ClaimCheckInRoutine(done));
        }

        private IEnumerator ClaimCheckInRoutine(Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            yield return api.CheckInRoutine(PetApiClient.NewRequestId(), x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 签到失败：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "签到失败" : msg);
                yield break;
            }

            bool already = r.Data["alreadyClaimed"].AsBool(false);
            int reward = r.Data["reward"].AsInt(0);
            int streak = r.Data["streak"].AsInt(0);
            string date = r.Data["date"].AsString("");

            // 余额与状态一律用服务端返回值覆盖
            _state.points = r.Data["balance"].AsInt(_state.points);
            _state.checkinClaimedToday = true;
            _state.checkinStreak = streak;
            if (reward > 0) _state.checkinReward = reward;
            // 签过之后就不能再领了。daily_checkin 上有 (user_id, date) 唯一索引，
            // 一天一条记录，所以 canClaim 一定是 false。
            _state.checkinCanClaim = false;
            _state.EnsureCollections();
            if (!string.IsNullOrEmpty(date) && !_state.checkinRecentDates.Contains(date))
                _state.checkinRecentDates.Add(date);
            if (!string.IsNullOrEmpty(date)) _state.checkinServerDate = date;
            HasCheckInStatus = true;

            string notice = already
                ? "今天已经签过了，明天再来"
                : $"签到成功，+{reward} 积分" + (streak > 1 ? $"，连续 {streak} 天" : "");

            Debug.Log($"[PetV1] 签到{(already ? "已签过" : "成功")} date={date} reward={reward} " +
                      $"streak={streak} 余额={_state.points}");
            RaiseChanged();
            done?.Invoke(true, notice);
        }

        // ------------------------------------------------------------------
        // 广告
        // ------------------------------------------------------------------

        /// <summary>
        /// 激励视频发奖。播完广告之后由 <see cref="PetV1.Ads.PetAdService"/> 调这里。
        ///
        /// 三个「不报错但要提示」的分支（服务端 200 + rejectedReason）：
        /// 广告位未启用 / 今日次数用完 / 播放过于频繁。这些不是故障，
        /// 客户端要把 rejectedReason 原样给玩家看，别显示「请求失败」。
        /// </summary>
        public void ClaimAdReward(string adUnit, string placement,
                                  Action<bool, string> done)
        {
            if (!Online) { done?.Invoke(false, "没连上服务端，暂时不能领奖励"); return; }
            if (Busy) { done?.Invoke(false, "正在处理，请稍候"); return; }

            StartCoroutine(ClaimAdRewardRoutine(adUnit, placement, done));
        }

        private IEnumerator ClaimAdRewardRoutine(string adUnit, string placement,
                                                 Action<bool, string> done)
        {
            Busy = true;
            PetApiResult r = null;
            // requestId 每次点击一个新值：同一次点击的重试要复用同一个，
            // 这里没有自动重试，玩家重点一次就是一次新意图。
            yield return api.AdRewardRoutine(PetApiClient.NewRequestId(), adUnit, placement,
                                             true, x => r = x);
            Busy = false;

            if (r == null || !r.Ok)
            {
                string msg = r != null ? r.Msg : "无响应";
                if (r != null && r.Unauthorized) Online = false;
                Debug.LogWarning($"[PetV1] 广告发奖失败：{r}");
                RaiseChanged();
                done?.Invoke(false, string.IsNullOrEmpty(msg) ? "领奖失败" : msg);
                yield break;
            }

            string rejected = r.Data["rejectedReason"].AsString("");
            int reward = r.Data["reward"].AsInt(0);
            int remain = r.Data["remainToday"].AsInt(-1);
            _state.points = r.Data["balance"].AsInt(_state.points);

            if (!string.IsNullOrEmpty(rejected))
            {
                // 被服务端拒了（没开/上限/太频繁）。余额可能没变，照抄返回值。
                Debug.Log($"[PetV1] 广告奖励被拒：{rejected} remainToday={remain}");
                RaiseChanged();
                done?.Invoke(false, rejected);
                yield break;
            }

            Debug.Log($"[PetV1] 广告奖励已发：+{reward} 余额={_state.points} " +
                      $"今日剩余={remain}");
            RaiseChanged();
            done?.Invoke(true, $"广告奖励 +{reward} 积分");
        }

        /// <summary>
        /// 插屏曝光埋点。**只记录不发奖**，失败不影响玩家，所以不弹 toast。
        /// </summary>
        public void ReportAdImpression(string adUnit, string placement, bool completed)
        {
            if (!Online) return;
            StartCoroutine(ReportAdImpressionRoutine(adUnit, placement, completed));
        }

        private IEnumerator ReportAdImpressionRoutine(string adUnit, string placement,
                                                      bool completed)
        {
            PetApiResult r = null;
            yield return api.AdImpressionRoutine(PetApiClient.NewRequestId(), adUnit,
                                                 placement, completed, x => r = x);
            if (r == null || !r.Ok)
            {
                // 埋点失败不打扰玩家：它只是看板统计数据。
                Debug.LogWarning($"[PetV1] 插屏曝光上报失败：{r}");
                yield break;
            }
            bool accepted = r.Data["accepted"].AsBool(false);
            Debug.Log($"[PetV1] 插屏曝光已记录 placement={placement} accepted={accepted}");
        }
    }
}
