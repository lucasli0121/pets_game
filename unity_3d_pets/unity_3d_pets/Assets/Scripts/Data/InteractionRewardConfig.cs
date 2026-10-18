using System;
using System.Collections.Generic;
using PetV1.Interaction;
using UnityEngine;

namespace PetV1.Data
{
    /// <summary>
    /// 「互动 → 养成产出」配置。对应后台管理端新增的 <c>config_interaction_reward</c> 表。
    ///
    /// 与 <c>config_food</c> / <c>config_scene</c> 同级，都是后台可编辑的下发配置。
    /// 包内这份 <c>DefaultInteractionReward.asset</c> 只是**兜底默认值**：
    /// 线上以服务端 getConfig 下发为准；拉不到配置时用包内这份；
    /// 连包内这份都没有时用 <see cref="Fallback"/> 里的硬编码常量，
    /// 保证编辑器里不配资产也能跑。
    ///
    /// 分类原则（PRD 4.2）：
    /// - happiness / satiety / exp 归服务端权威，客户端**只读不写**
    /// - 这里是客户端唯一能查到的「某手势对应多少数值」的映射，
    ///   仅用于**预测性显示**（例如飘字提示）与埋点上报，
    ///   真正的增减一律以服务端返回为准
    /// </summary>
    [CreateAssetMenu(fileName = "DefaultInteractionReward",
                     menuName = "PetV1/默认互动产出配置")]
    public class InteractionRewardConfig : ScriptableObject
    {
        [Header("互动产出（手势 -> 养成数值）")]
        [Tooltip("每条对应后台 config_interaction_reward 的一行。键是 GestureType 枚举名。")]
        public List<InteractionRewardEntry> entries = new List<InteractionRewardEntry>();

        [Header("状态衰减（对应后台 config_decay，单位：每小时）")]
        [Tooltip("饱腹每小时衰减量。后台文档 6.4：按整小时扣，离线超 24 小时封顶。")]
        public int satietyDecayPerHour = 10;

        [Tooltip("快乐值每小时衰减量。")]
        public int happinessDecayPerHour = 5;

        [Tooltip("兴奋度每小时衰减量。表现层用，不入库。默认 60/小时 ≈ 1.0/秒。")]
        public float excitementDecayPerHour = 60f;

        [Tooltip("离线衰减结算的封顶小时数。防止一周不上线回来状态全空。")]
        public int maxOfflineDecayHours = 24;

        /// <summary>
        /// 小时换算成秒的基数（3600）。抽成常量是因为后台的衰减配置是
        /// 「每小时 X 点」口径，而客户端 Tick 是「每秒」，单位不同必须显式换算。
        /// </summary>
        public const float secondsPerHour = 3600f;

        [Header("状态阈值（对应后台 config_state_threshold）")]
        [Tooltip("饱腹低于此值 = 饥饿。")]
        public int satietyHungryThreshold = 30;

        [Tooltip("饱腹低于此值 = 饥饿 + 疲惫。")]
        public int satietyExhaustedThreshold = 10;

        [Tooltip("快乐值低于此值 = 难过。")]
        public int happinessSadThreshold = 30;

        [Tooltip("快乐值低于此值 = 沮丧。")]
        public int happinessDepressedThreshold = 10;

        [Header("取值范围（对应后台 config_value_range）")]
        [Tooltip("所有养成数值的下限。")]
        public int valueMin = 0;

        [Tooltip("所有养成数值的上限。")]
        public int valueMax = 100;

        [Header("积分（对应后台 config_points）")]
        [Tooltip("注册完成时发放的初始积分。")]
        public int pointsInitial = 500;

        [Tooltip("每日签到奖励积分。")]
        public int pointsCheckinReward = 100;

        [Tooltip("每提升一级奖励积分。")]
        public int pointsLevelUpReward = 100;

        [Tooltip("观看一次激励视频奖励积分。")]
        public int pointsAdReward = 100;

        [Tooltip("激励视频每日发放上限。")]
        public int pointsAdDailyLimit = 5;

        [Tooltip("修改性格消耗积分。")]
        public int pointsPersonalityChangeCost = 200;

        private Dictionary<string, InteractionRewardEntry> _lookup;

        /// <summary>
        /// 按 GestureType 查产出规则。找不到返回 null，
        /// 调用方要能容忍（表示后台没配这条手势，或该手势不产出养成值）。
        /// </summary>
        public InteractionRewardEntry Find(GestureType gesture)
        {
            if (gesture == GestureType.None) return null;
            BuildLookup();
            return _lookup.TryGetValue(gesture.ToString(), out var e) ? e : null;
        }

        /// <summary>
        /// 预测性换算：某手势预计给多少经验。
        /// 仅用于 UI 提示与埋点，**不代表实际发放**，实际以服务端返回为准。
        /// </summary>
        public int PredictExpDelta(GestureType gesture, int occurrenceCount)
        {
            var e = Find(gesture);
            if (e == null || !e.enabled) return 0;
            int delta = e.expDelta;
            if (e.expPerCount > 0 && occurrenceCount > 0)
            {
                // occurrenceCount 是累计第几次（含本次）
                if (occurrenceCount % e.expPerCount == 0) delta += 1;
            }
            return delta;
        }

        public int PredictHappinessDelta(GestureType gesture)
        {
            var e = Find(gesture);
            if (e == null || !e.enabled) return 0;
            return e.happinessDelta;
        }

        private void BuildLookup()
        {
            if (_lookup != null && _lookup.Count == entries.Count) return;
            _lookup = new Dictionary<string, InteractionRewardEntry>(entries.Count);
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.gestureType)) continue;
                _lookup[e.gestureType.Trim()] = e;
            }
        }

        private void OnValidate()
        {
            _lookup = null;
            if (valueMax <= valueMin)
            {
                Debug.LogWarning($"[PetV1] {name}: valueMax({valueMax}) 必须大于 valueMin({valueMin})，已修正。");
                valueMax = valueMin + 1;
            }
            if (maxOfflineDecayHours < 0) maxOfflineDecayHours = 0;
        }

        /// <summary>
        /// 不依赖资产的兜底默认值。PRD 4.3 与后台 13.4 的示例数字，
        /// 仅在既没有服务端配置、也没有包内 asset 时使用。
        /// </summary>
        public static InteractionRewardConfig Fallback()
        {
            var c = CreateInstance<InteractionRewardConfig>();
            c.name = "InteractionRewardConfig(Fallback)";
            c.entries = new List<InteractionRewardEntry>
            {
                // PRD 4.3 示例：点击 1 次快乐 +1，点 5 次经验 +1
                new InteractionRewardEntry("Tap", 1, 0, 0, 5),
                new InteractionRewardEntry("DoubleTap", 1, 0, 0, 5),
                new InteractionRewardEntry("ScreenTap", 1, 0, 0, 5),
                // 抚摸 1 次快乐 +2，经验 +0
                new InteractionRewardEntry("Stroke", 2),
                new InteractionRewardEntry("LongPress", 2),
                // 多指挠动 / 快速划动 各 快乐 +5、经验 +1
                new InteractionRewardEntry("MultiScratch", 5, 0, 1),
                new InteractionRewardEntry("ScreenScratch", 5, 0, 1),
                new InteractionRewardEntry("FastSwipe", 5, 0, 1),
                new InteractionRewardEntry("ScreenSwipe", 5, 0, 1),
            };
            return c;
        }
    }
}