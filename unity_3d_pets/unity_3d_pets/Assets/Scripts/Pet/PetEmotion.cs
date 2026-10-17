using System;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 第一阶段最小情绪模型：Happiness / Excitement / Sleepiness，取值 0~100。
    /// 需求文档第十节。注意「情绪不是动画映射表，而是行为决策输入」——
    /// 这里不存任何 AnimationClip，只提供数值给 BehaviorController。
    /// </summary>
    [Serializable]
    public class PetEmotion
    {
        public const float Min = 0f;
        public const float Max = 100f;

        [Range(Min, Max)] public float happiness = 60f;

        [Range(Min, Max)] public float excitement = 30f;

        [Range(Min, Max)] public float sleepiness = 20f;

        [Header("Decay (per second)")]
        [Tooltip("长时间无互动时 Excitement 的衰减速度。需求文档 10.3。")]
        public float excitementDecayPerSecond = 2.0f;

        [Tooltip("Happiness 的缓慢回落速度。")]
        public float happinessDecayPerSecond = 0.15f;

        [Tooltip("Sleepiness 的自然累积速度。")]
        public float sleepinessRisePerSecond = 0.35f;

        [Header("Movement Cost")]
        [Tooltip("每移动 1 米消耗的 Sleepiness。")]
        public float sleepinessPerMeter = 0.5f;

        [Tooltip("走路的瞬时速度（m/s），用于把距离换算成时间。")]
        public float locomotionSpeed = 1.2f;

        public PetEmotion Clone()
        {
            return new PetEmotion
            {
                happiness = happiness,
                excitement = excitement,
                sleepiness = sleepiness,
                excitementDecayPerSecond = excitementDecayPerSecond,
                happinessDecayPerSecond = happinessDecayPerSecond,
                sleepinessRisePerSecond = sleepinessRisePerSecond,
                sleepinessPerMeter = sleepinessPerMeter,
                locomotionSpeed = locomotionSpeed,
            };
        }

        public void CopyFrom(PetEmotion other)
        {
            if (other == null) return;
            happiness = other.happiness;
            excitement = other.excitement;
            sleepiness = other.sleepiness;
            excitementDecayPerSecond = other.excitementDecayPerSecond;
            happinessDecayPerSecond = other.happinessDecayPerSecond;
            sleepinessRisePerSecond = other.sleepinessRisePerSecond;
            sleepinessPerMeter = other.sleepinessPerMeter;
            locomotionSpeed = other.locomotionSpeed;
        }

        // ---------- 情绪变更接口：所有变更都从这里过，保证 Clamp 与日志不被绕过 ----------

        /// <summary>触摸：Happiness +2、Excitement +1（需求文档 10.1）。</summary>
        public void ApplyTap()
        {
            happiness += 2f;
            excitement += 1f;
            ClampAll();
        }

        /// <summary>抚摸（Drag / Stroke）：比点击更强，按触碰时长给增量。</summary>
        public void ApplyStroke(float durationSeconds)
        {
            float bonus = Mathf.Clamp(durationSeconds * 2.5f, 1f, 6f);
            happiness += bonus;
            excitement += bonus * 0.6f;
            ClampAll();
        }

        /// <summary>多指触摸：Excitement +5（需求文档 10.2）。</summary>
        public void ApplyMultiTouch(int fingerCount)
        {
            excitement += 5f * Mathf.Max(1, fingerCount);
            happiness += 1f;
            ClampAll();
        }

        /// <summary>快速划动：速度越快兴奋度越高。</summary>
        public void ApplyFastSweep(float speed)
        {
            float bonus = Mathf.Clamp(speed * 0.02f, 1f, 8f);
            excitement += bonus;
            happiness += bonus * 0.4f;
            ClampAll();
        }

        /// <summary>扑屏类事件（SCREEN_SCRATCH 等）直接拉满兴奋度，用于触发 Excited 行为。</summary>
        public void ApplyScreenScratch(float intensity = 1f)
        {
            excitement += 10f * Mathf.Clamp01(intensity);
            happiness += 3f;
            ClampAll();
        }

        /// <summary>移动消耗。</summary>
        public void ApplyMoveDistance(float meters)
        {
            sleepiness += meters * sleepinessPerMeter;
            excitement = Mathf.Max(Min, excitement - meters * 0.4f);
            ClampAll();
        }

        /// <summary>每秒的被动衰减（需求文档 10.3：长时间无互动 Excitement 缓慢下降）。</summary>
        public void Tick(float deltaTime)
        {
            excitement = Mathf.Max(Min, excitement - excitementDecayPerSecond * deltaTime);
            happiness = Mathf.Max(Min, happiness - happinessDecayPerSecond * deltaTime);
            sleepiness = Mathf.Min(Max, sleepiness + sleepinessRisePerSecond * deltaTime);
        }

        public void Reset()
        {
            happiness = 60f;
            excitement = 30f;
            sleepiness = 20f;
        }

        public void ClampAll()
        {
            happiness = Mathf.Clamp(happiness, Min, Max);
            excitement = Mathf.Clamp(excitement, Min, Max);
            sleepiness = Mathf.Clamp(sleepiness, Min, Max);
        }

        /// <summary>归一化后的 0~1 值，供 UtilityScorer 使用（Utility 用 0~1 而非 0~100 便于加权）。</summary>
        public float NormalizedHappiness => happiness / Max;
        public float NormalizedExcitement => excitement / Max;
        public float NormalizedSleepiness => sleepiness / Max;

        public string Compact() => $"Happy {happiness:F0} Excite {excitement:F0} Sleepy {sleepiness:F0}";
    }
}