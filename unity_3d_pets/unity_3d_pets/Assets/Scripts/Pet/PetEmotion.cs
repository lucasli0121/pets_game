using System;
using PetV1.Data;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 宠物**内在状态**。三个数值（饱腹 / 快乐 / 经验）全部由服务端权威下发。
    ///
    /// 重要：这三个值是**只读镜像**。
    /// 客户端不得自行增减 —— 否则改包即可刷经验/刷饱腹，
    /// 与「改包刷积分」是同一个漏洞。后台文档 1.3 那条硬原则
    /// 「客户端的余额只能用于显示，不能作为扣减依据」同样适用。
    ///
    /// 真正的增减走云函数 <c>feedPet</c> / <c>claimCheckIn</c> /
    /// <c>claimAdReward</c> 等接口，全部带 <c>request_id</c> 幂等。
    ///
    /// 「互动给多少经验」查<see cref="InteractionRewardConfig"/>，
    /// 那是预测值，不是在这里加算。
    /// </summary>
    [Serializable]
    public class PetStats
    {
        [Tooltip("饱腹度 0~100。后台文档 6.4：每小时 -10，<30 饥饿，<10 饥饿+疲惫。")]
        public int satiety = 100;

        [Tooltip("快乐值 0~100。每小时 -5，<30 难过，<10 沮丧。")]
        public int happiness = 60;

        [Tooltip("当前等级，由服务端按 config_level 表计算下发。客户端不做等级计算。")]
        public int level = 1;

        [Tooltip("本级内已累积经验（服务端已扣掉前面各级的门槛）。")]
        public int expInLevel;

        [Tooltip("到下一级所需经验，来自 config_level。客户端只展示，不计算。")]
        public int expToNextLevel = 100;

        [Tooltip("累计经验总额，服务端下发，仅用于统计与埋点。")]
        public int totalExp;

        [Tooltip("宠物名字，注册或领养时文本输入（V1 不做语音）。")]
        public string petName = "";

        /// <summary>
        /// 用服务端返回的权威数据整体覆盖本地缓存。
        /// 每次 login / syncState / feedPet 成功后调用。
        /// </summary>
        public void ApplyServerState(int satiety, int happiness, int level,
                                     int expInLevel, int expToNextLevel, int totalExp,
                                     int min, int max)
        {
            this.satiety = Mathf.Clamp(satiety, min, max);
            this.happiness = Mathf.Clamp(happiness, min, max);
            this.level = Mathf.Max(1, level);
            this.expInLevel = Mathf.Max(0, expInLevel);
            this.expToNextLevel = Mathf.Max(1, expToNextLevel);
            this.totalExp = Mathf.Max(0, totalExp);
        }

        /// <summary>本级经验进度 0~1，供进度条用。</summary>
        public float LevelProgress => Mathf.Clamp01(expInLevel / (float)expToNextLevel);

        /// <summary>按后台配置判断宠物的复合状态，用于表情/立绘/UI 提示。</summary>
        public PetCondition EvaluateCondition(InteractionRewardConfig cfg)
        {
            if (cfg == null) return PetCondition.Normal;

            if (satiety < cfg.satietyExhaustedThreshold) return PetCondition.HungryExhausted;
            if (satiety < cfg.satietyHungryThreshold) return PetCondition.Hungry;
            if (happiness < cfg.happinessDepressedThreshold) return PetCondition.Depressed;
            if (happiness < cfg.happinessSadThreshold) return PetCondition.Sad;
            return PetCondition.Normal;
        }

        public string Compact()
        {
            return $"Lv{level} exp{expInLevel}/{expToNextLevel} sat{satiety} happy{happiness}";
        }
    }

    /// <summary>
    /// 宠物状态的复合判定。PRD 4.3 要求的「用进度条颜色表现」即依此取色。
    /// 注意顺序：先判饱腹再判快乐，饱腹恶化优先级更高。
    /// </summary>
    public enum PetCondition
    {
        Normal = 0,
        Sad = 1,               // 难过：快乐 < 30
        Depressed = 2,         // 沮丧：快乐 < 10
        Hungry = 3,            // 饥饿：饱腹 < 30
        HungryExhausted = 4,   // 饥饿 + 疲惫：饱腹 < 10
    }

    /// <summary>
    /// 客户端**表现层**的临时情绪量：兴奋度 / 困意。取值 0~100。
    ///
    /// 与<see cref="PetStats"/> 的区别（PRD 4.2 明确写了）：
    /// 「欢快度/兴奋度不是独立存储的数值，它是快乐值的外在表现。
    ///   活泼是性格维度中的独立数值（0~10），由宠物自带、不随快乐值变化。」
    ///
    /// 所以：
    /// - 这两个值**不入库、不存档、不同步**，关掉游戏就没了
    /// - 它们只喂给 BehaviorController 做行为决策，不写进 SaveData
    /// - 但衰减/增长系数仍从 <see cref="InteractionRewardConfig"/> 读，
    ///   运营要能调，不能留 2.0f 这种裸常量
    ///
    /// 旧版本把这些系数写死在字段初始值里（excitementDecayPerSecond = 2.0f 等），
    /// 现在统一改为由 <see cref="ApplyConfig"/> 从配置注入。
    /// </summary>
    [Serializable]
    public class PetEmotion
    {
        public const float Min = 0f;
        public const float Max = 100f;

        [Range(Min, Max)] public float excitement = 30f;
        [Range(Min, Max)] public float sleepiness = 20f;

        [Header("Decay (per second) — 由 InteractionRewardConfig 注入")]
        [Tooltip("Excitement 每秒衰减速度。V1 不入库，纯表现层。")]
        public float excitementDecayPerSecond = 2.0f;

        [Tooltip("Sleepiness 每秒自然增长速度。")]
        public float sleepinessRisePerSecond = 0.35f;

        [Header("Movement Cost")]
        [Tooltip("每移动 1 米消耗的 Sleepiness。")]
        public float sleepinessPerMeter = 0.5f;

        [Tooltip("互动瞬间的兴奋度增量基准值。V1 不入库。")]
        public float interactExcitementGain = 1f;

        [Tooltip("跑动需要的最低兴奋度。低于此值不触发 Run 行为。")]
        public float runExcitementThreshold = 60f;

        public PetEmotion Clone()
        {
            return new PetEmotion
            {
                excitement = excitement,
                sleepiness = sleepiness,
                excitementDecayPerSecond = excitementDecayPerSecond,
                sleepinessRisePerSecond = sleepinessRisePerSecond,
                sleepinessPerMeter = sleepinessPerMeter,
                interactExcitementGain = interactExcitementGain,
                runExcitementThreshold = runExcitementThreshold,
            };
        }

        public void CopyFrom(PetEmotion other)
        {
            if (other == null) return;
            excitement = other.excitement;
            sleepiness = other.sleepiness;
            excitementDecayPerSecond = other.excitementDecayPerSecond;
            sleepinessRisePerSecond = other.sleepinessRisePerSecond;
            sleepinessPerMeter = other.sleepinessPerMeter;
            interactExcitementGain = other.interactExcitementGain;
            runExcitementThreshold = other.runExcitementThreshold;
        }

        // ---------- 表现层：只有 excitement / sleepiness 在这里变 ----------

        /// <summary>
        /// 通用互动反馈。增益量来自配置，不带业务含义。
        /// 不再按手势种类分方法 —— 原来的 ApplyTap/ApplyStroke/ApplyFastSweep
        /// 里那些 2f / 5f / 0.02f 常量是养成数值，属于服务端职责。
        /// </summary>
        public void ApplyInteraction(float intensity = 1f)
        {
            float i = Mathf.Clamp01(intensity);
            excitement += interactExcitementGain * (1f + i * 2f);
            ClampAll();
        }

        /// <summary>高强度互动（多指挠屏、扑屏），直接把兴奋度推高以触发 Excited 行为。</summary>
        public void ApplyHighExcitement(float intensity = 1f)
        {
            excitement += 10f * Mathf.Clamp01(intensity);
            ClampAll();
        }

        /// <summary>移动消耗：累加困意，同时按距离磨掉兴奋度。</summary>
        public void ApplyMoveDistance(float meters)
        {
            sleepiness += meters * sleepinessPerMeter;
            excitement = Mathf.Max(Min, excitement - meters * 0.4f);
            ClampAll();
        }

        /// <summary>
        /// 每秒被动衰减。
        /// 注意这里的衰减是**表现层**的（兴奋度自然消退），
        /// 服务端那套「饱腹每小时 -10、快乐每小时 -5」由 syncState 在登录时结算，
        /// 不在这里做 —— 两套衰减周期不同，混在一起会算错。
        /// </summary>
        public void Tick(float deltaTime)
        {
            excitement = Mathf.Max(Min, excitement - excitementDecayPerSecond * deltaTime);
            sleepiness = Mathf.Min(Max, sleepiness + sleepinessRisePerSecond * deltaTime);
        }

        /// <summary>
        /// 把服务端下发的配置注入表现层系数。
        /// 由 GameState 在 getConfig 成功后调用。
        /// </summary>
        public void ApplyConfig(InteractionRewardConfig cfg)
        {
            if (cfg == null) return;
            // 表现层系数暂由后台 config_interaction_reward 的扩展段下发；
            // 若后台未配则保留当前值，不强制覆盖成 0。
            if (cfg.excitementDecayPerHour > 0f)
            {
                // 后台的衰减是「每小时」口径，这里是「每秒」，做单位换算。
                // secondsPerHour 是 const，必须用类型名访问，不能写 cfg.secondsPerHour（CS0176）。
                excitementDecayPerSecond =
                    cfg.excitementDecayPerHour / InteractionRewardConfig.secondsPerHour;
            }
            ClampAll();
        }

        public void Reset()
        {
            excitement = 30f;
            sleepiness = 20f;
        }

        public void ClampAll()
        {
            excitement = Mathf.Clamp(excitement, Min, Max);
            sleepiness = Mathf.Clamp(sleepiness, Min, Max);
        }

        public float NormalizedExcitement => excitement / Max;
        public float NormalizedSleepiness => sleepiness / Max;

        public string Compact() => $"Excite {excitement:F0} Sleepy {sleepiness:F0}";
    }
}