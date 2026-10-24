using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Model
{
    /// <summary>
    /// 客户端侧的宠物状态视图。字段与后台 <c>domain.PetView</c> / <c>domain.Assets</c> 逐字对齐。
    ///
    /// **它只是显示用的快照，不是权威数据。**
    /// PRD 4.2：happiness / satiety / exp / points 归服务端，客户端只读不写。
    /// 原型阶段还没有登录与接口对接，所以有一份本地可写副本供交互演示；
    /// 接入 <c>/api/v1/pet/feed</c> 之后，所有写操作都要改成
    /// 「先发请求 → 用服务端返回值整份覆盖」。**不要在客户端算经验或扣金币。**
    ///
    /// 字段命名规则：json 名与后台一致，改一个名字就静默解析成 0。
    ///
    /// 状态只有饱腹（satiety）与快乐（happiness）两项 ——
    /// 后台 <c>domain.decay.go</c> 里 <c>Settle()</c> 只结算这两项，
    /// 且下限是 <see cref="MinStateValue"/>（1，不允许归零）。
    /// 展板上画了「亲密」「精力」两条，但后台没有对应字段，
    /// 按「一切以后台为准」的口径，本原型不画那两条。
    /// 后台加上之后，这里加两个字段 + 两条状态条即可，位置已经留好。
    /// </summary>
    [Serializable]
    public class PetRuntimeState
    {
        /// <summary>养成状态的下限。后台产品决策：可以掉到很低但不能归零。</summary>
        public const int MinStateValue = 1;

        /// <summary>养成状态的上限。</summary>
        public const int MaxStateValue = 100;

        [Header("身份")]
        [Tooltip("后台 pet.id。未登录时本地生成。")]
        public string petId = "";

        [Tooltip("后台 pet.petType。V1 只有 dog。")]
        public string petType = "dog";

        [Tooltip("宠物名。展板是Border Collie（品种），这里是用户起的名字。")]
        public string petName = "旺财";

        [Tooltip("品种名，走本地化。展板显示 Border Collie。")]
        public string breedName = "边境牧羊犬";

        [Header("成长（服务端权威，客户端只读）")]
        [Tooltip("后台 pet.level。")]
        public int level = 1;

        [Tooltip("后台 pet.exp，累计经验。客户端不据此算等级，只用来算进度条。")]
        public long exp = 0;

        [Tooltip("后台 petView.expInLevel，本级已积累的经验。")]
        public long expInLevel = 0;

        [Tooltip("后台 petView.expRequired，本级升下一级所需。由服务端按 config_level 下发。")]
        public long expRequired = 100;

        [Header("状态（服务端权威，客户端只读）")]
        [Tooltip("后台 pet.satiety，饱腹度 0~100。")]
        public int satiety = 80;

        [Tooltip("后台 pet.happiness，快乐度 0~100。")]
        public int happiness = 70;

        [Header("资产（服务端权威，客户端只读）")]
        [Tooltip("后台 assets.points.balance，积分余额。")]
        public int points = 500;

        // 运行时快照，不参与 Unity 序列化：字典在 Unity 序列化后端不被支持，
        // 加 [SerializeField] 也存不下来。要落盘走 JSON 序列化整个对象。
        [NonSerialized]
        [Tooltip("后台 assets.inventory，item_id -> 数量。")]
        public Dictionary<string, int> inventory = new Dictionary<string, int>();

        [Tooltip("后台 assets.scenes，已解锁的 scene_id 列表。")]
        public List<string> unlockedScenes = new List<string>();

        [Tooltip("后台 assets.activeScene，当前使用的场景 id。")]
        public string activeScene = "home_day";

        [Header("状态标签（服务端算好的，客户端只显示）")]
        [Tooltip("后台 petView.satietyState：normal / hungry / exhausted。")]
        public string satietyState = "normal";

        [Tooltip("后台 petView.happinessState：normal / sad / depressed。")]
        public string happinessState = "normal";

        [Header("性格四维（服务端权威，客户端只读）")]
        [Tooltip("后台 personality.lively，活泼 0~10。")]
        public int lively = 6;

        [Tooltip("后台 personality.curiosity，好奇 0~10。")]
        public int curiosity = 5;

        [Tooltip("后台 personality.affection，亲人 0~10。")]
        public int affection = 7;

        [Tooltip("后台 personality.independence，独立 0~10。")]
        public int independence = 3;

        [Header("签到（服务端权威，随 sync-state 下发）")]
        [Tooltip("后台 checkin.claimedToday：今天签过了没。")]
        public bool checkinClaimedToday = false;

        [Tooltip("后台 checkin.streak：含今天的连续签到天数。今天没签时为 0。")]
        public int checkinStreak = 0;

        [Tooltip("后台 checkin.reward：今天签到能拿多少（已签则是当时拿到的）。")]
        public int checkinReward = 100;

        [Tooltip("后台 checkin.recentDates：最近 7 天里签过的日期，yyyy-MM-dd，升序。")]
        public List<string> checkinRecentDates = new List<string>();

        [Tooltip("后台 checkin.canClaim：现在点签到能不能领到奖励。")]
        public bool checkinCanClaim = true;

        [Tooltip("后台 checkin.date：服务端认定的今天（UTC+8）。空表示还没拿到，用本地兜底算。")]
        public string checkinServerDate = "";

        [Header("广告位（config.ad，后台可配）")]
        [Tooltip("激励视频一次给多少积分。取 config_points.adReward，默认 100。")]
        public int adReward = 100;

        [Tooltip("激励视频广告位 id（微信后台申请的 adUnitId）。空表示后台没配。")]
        public string adRewardedUnitId = "";

        [Tooltip("插屏广告广告位 id。空表示后台没配。")]
        public string adInterstitialUnitId = "";

        [Tooltip("激励视频广告位是否启用。后台关掉时 UI 不放入口。")]
        public bool adRewardedEnabled = false;

        [Tooltip("插屏广告位是否启用。后台默认是关的。")]
        public bool adInterstitialEnabled = false;

        /// <summary>激励视频能不能放：后台配了、启用了、奖励大于 0。</summary>
        public bool AdRewardReady => adRewardedEnabled && adReward > 0;

        /// <summary>插屏能不能放。</summary>
        public bool AdInterstitialReady => adInterstitialEnabled;

        /// <summary>
        /// 饱腹是否低到「该喂了」。
        ///
        /// 判据用后台下发的 <see cref="satietyState"/> 而不是自己比阈值 ——
        /// 阈值是 <c>config_state_threshold</c> 可配的，客户端再抄一份就会不一致。
        /// 后台说 normal 就是 normal，哪怕数值看起来偏低（阈值可能刚被调过）。
        /// </summary>
        public bool NeedsFood => satietyState == "hungry" || satietyState == "exhausted";

        /// <summary>级内经验进度 0~1。分母为 0 时返回 0，避免 NaN 传到 UI 上。</summary>
        public float ExpProgress01
        {
            get
            {
                if (expRequired <= 0) return 0f;
                return Mathf.Clamp01((float)(expInLevel / (double)expRequired));
            }
        }

        public int SatietyPercent => satiety;

        public int HappinessPercent => happiness;

        /// <summary>
        /// 兜底初始化。
        ///
        /// 字段初始化器 <c>= new Dictionary&lt;...&gt;()</c> 在
        /// <c>CreateInstance()</c> 路径上有效，但**从场景反序列化出来的那份不会跑**
        /// —— Unity 对 [Serializable] 普通类不保证调构造/字段初始化器，
        /// 于是 inventory 是 null，CountOf 直接 NullReferenceException。
        ///
        /// 实际症状更隐蔽：<c>?? new Dictionary</c> 写在字段初始化器位置时，
        /// 得到的空字典让「背包全空」，看起来像数据没配上。
        /// </summary>
        private void EnsureInventory()
        {
            if (inventory == null) inventory = new Dictionary<string, int>();
            if (unlockedScenes == null) unlockedScenes = new List<string>();
            if (checkinRecentDates == null) checkinRecentDates = new List<string>();
        }

        /// <summary>
        /// 供外部（<c>PetHomeUI.State</c>）在拿到反序列化出来的实例后补齐集合。
        /// </summary>
        public void EnsureCollections() => EnsureInventory();

        /// <summary>
        /// 背包里某物品的数量。没有则 0。
        /// </summary>
        public int CountOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            EnsureInventory();
            return inventory.TryGetValue(itemId, out var n) ? n : 0;
        }

        public void AddItem(string itemId, int delta)
        {
            if (string.IsNullOrEmpty(itemId) || delta == 0) return;
            EnsureInventory();
            if (!inventory.TryGetValue(itemId, out var n)) n = 0;
            n += delta;
            // 数量不允许为负 —— 扣减时先判库存，判过了还出负数说明逻辑有 bug，
            // 夹到 0 会把 bug 藏起来，所以照样写进去由上层自检发现。
            inventory[itemId] = n;
        }

        public bool IsSceneUnlocked(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return false;
            EnsureInventory();
            // home_day 免费且默认解锁，不必等服务端下发列表。
            return unlockedScenes.Contains(sceneId);
        }

        /// <summary>
        /// 整份覆盖。接口返回后调用，用服务端数据替换本地快照。
        ///
        /// 为什么必须整份覆盖而不是逐字段判「服务端给了没有」：
        /// 服务端可能主动下调某个值（比如状态被衰减扣了），
        /// 逐字段合并会保留旧值，表现为「数值怎么不降」。
        /// </summary>
        public void OverwriteFrom(PetRuntimeState other)
        {
            if (other == null) return;
            petId = other.petId;
            petName = other.petName;
            breedName = other.breedName;
            level = other.level;
            exp = other.exp;
            expInLevel = other.expInLevel;
            expRequired = other.expRequired;
            satiety = other.satiety;
            happiness = other.happiness;
            points = other.points;
            activeScene = other.activeScene;
            satietyState = other.satietyState;
            happinessState = other.happinessState;
            lively = other.lively;
            curiosity = other.curiosity;
            affection = other.affection;
            independence = other.independence;
            checkinClaimedToday = other.checkinClaimedToday;
            checkinStreak = other.checkinStreak;
            checkinReward = other.checkinReward;
            checkinCanClaim = other.checkinCanClaim;
            checkinServerDate = other.checkinServerDate;
            adReward = other.adReward;
            adRewardedUnitId = other.adRewardedUnitId;
            adInterstitialUnitId = other.adInterstitialUnitId;
            adRewardedEnabled = other.adRewardedEnabled;
            adInterstitialEnabled = other.adInterstitialEnabled;

            EnsureInventory();
            inventory = new Dictionary<string, int>(other.inventory);
            unlockedScenes = new List<string>(other.unlockedScenes);
            checkinRecentDates = new List<string>(other.checkinRecentDates ?? new List<string>());
        }

        /// <summary>
        /// 构造一份可直接运行的初始状态。
        /// 数值取后台 <c>DefaultSnapshot()</c> 与展板的量级，不是设计结论。
        /// </summary>
        public static PetRuntimeState CreateDefault()
        {
            var s = new PetRuntimeState();
            s.EnsureInventory();
            s.level = 3;
            s.exp = 240;
            s.expInLevel = 72;
            s.expRequired = 100;      // → 72%，与展板成长页一致
            s.satiety = 65;
            s.happiness = 91;
            s.points = 500;

            s.inventory["food_normal"] = 3;
            s.inventory["food_beef"] = 2;
            s.inventory["food_biscuit"] = 5;
            s.inventory["food_chicken"] = 2;
            s.inventory["food_cake"] = 1;

            s.unlockedScenes.Add("home_day");
            s.activeScene = "home_day";
            return s;
        }
    }
}
