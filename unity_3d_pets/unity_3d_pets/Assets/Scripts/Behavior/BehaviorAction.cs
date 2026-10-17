using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Behavior
{
    using PetV1.Pet;

    /// <summary>
    /// 行为优先级。技术方案文档第九节的 P0~P5。
    /// 数字越小优先级越高。BehaviorController 用它做打断判定。
    /// </summary>
    public enum BehaviorPriority
    {
        P0_GrowthOrSceneCritical = 0,
        P1_EatingOrTraining = 1,
        P2_StrongInteraction = 2,
        P3_VoiceCommand = 3,
        P4_TouchReaction = 4,
        P5_AutonomousIdle = 5,
    }

    /// <summary>
    /// 统一的行为动作描述。技术方案「运行时核心对象模型」里的 BehaviorAction。
    /// 第一阶段只有 6 种行为（需求文档第八节），但结构按可扩展设计。
    /// </summary>
    [Serializable]
    public class BehaviorAction
    {
        public PetBehavior behavior = PetBehavior.Idle;

        [Tooltip("P0~P5，越小越优先。")]
        public BehaviorPriority priority = BehaviorPriority.P5_AutonomousIdle;

        [Tooltip("是否可被更高优先级行为打断。")]
        public bool interruptible = true;

        [Tooltip("是否需要移动。")]
        public bool requiresMovement = false;

        [Tooltip("最短持续时间（秒）。防止行为一闪而过。")]
        public float minDuration = 0.2f;

        [Tooltip("最长持续时间（秒）。0 表示由状态机计划时长决定。")]
        public float maxDuration = 0f;

        [Tooltip("期望的模型能力，缺能力时由 AnimationMap 走 fallback。")]
        public PetCapability capability = PetCapability.Idle;

        [Tooltip("此行为需要的最低兴奋度（0~1）。")]
        public float requiredExcitement = 0f;

        [Tooltip("此行为需要的最低亲密度/ 互动意愿（0~1）。")]
        public float requiredDrive = 0f;

        public BehaviorAction() { }

        public BehaviorAction(PetBehavior behavior, BehaviorPriority priority, bool interruptible,
            bool requiresMovement, float minDuration, float maxDuration, PetCapability capability)
        {
            this.behavior = behavior;
            this.priority = priority;
            this.interruptible = interruptible;
            this.requiresMovement = requiresMovement;
            this.minDuration = minDuration;
            this.maxDuration = maxDuration;
            this.capability = capability;
        }

        public bool DurationExpired(float elapsed) => maxDuration > 0f && elapsed >= maxDuration;
    }

    /// <summary>
    /// 候选行为集合。需求文档第八节：第一阶段只实现 Idle / Walk / Run / Stop / Look(Turn) / Recover。
    /// Excited 是扑屏链路的中间态（需求文档第九节：Excited -> Run -> Turn -> Return）。
    /// </summary>
    public static class BehaviorLibrary
    {
        private static BehaviorAction[] s_actions;

        public static BehaviorAction[] All
        {
            get
            {
                if (s_actions != null) return s_actions;
                s_actions = new[]
                {
                    new BehaviorAction(PetBehavior.Idle,   BehaviorPriority.P5_AutonomousIdle, true,  false, 0.5f, 0f,   PetCapability.Idle),
                    new BehaviorAction(PetBehavior.Walk,   BehaviorPriority.P5_AutonomousIdle, true,  true,  0.6f, 0f,   PetCapability.Walk),
                    new BehaviorAction(PetBehavior.Run,    BehaviorPriority.P2_StrongInteraction, true, true, 0.5f, 0f, PetCapability.Run),
                    new BehaviorAction(PetBehavior.Stop,   BehaviorPriority.P5_AutonomousIdle, true,  false, 0.3f, 0f,   PetCapability.Idle),
                    new BehaviorAction(PetBehavior.Turn,   BehaviorPriority.P5_AutonomousIdle, true,  false, 0.4f, 0f,   PetCapability.Idle),
                    new BehaviorAction(PetBehavior.Look,   BehaviorPriority.P5_AutonomousIdle, true,  false, 0.5f, 0f,   PetCapability.Idle),
                    new BehaviorAction(PetBehavior.Recover, BehaviorPriority.P5_AutonomousIdle, true,  false, 0.6f, 0f,   PetCapability.Idle),
                    new BehaviorAction(PetBehavior.Return, BehaviorPriority.P5_AutonomousIdle, true,  true,  0.5f, 0f,   PetCapability.Walk),
                    new BehaviorAction(PetBehavior.Excited,BehaviorPriority.P2_StrongInteraction, true, true, 0.4f, 0f,  PetCapability.Run),
                };
                return s_actions;
            }
        }

        public static BehaviorAction Get(PetBehavior behavior)
        {
            var all = All;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].behavior == behavior) return all[i];
            }
            return all[0];
        }
    }

    /// <summary>
    /// 行为黑板：保存最近事件与当前决策中间结果，供 DebugPanel 与后续系统读取。
    /// 技术方案「运行时核心对象模型」里列的 BehaviorBlackboard。
    /// </summary>
    public class BehaviorBlackboard
    {
        private readonly Queue<string> _recent = new Queue<string>(12);

        public PetBehavior Current { get; internal set; } = PetBehavior.Idle;
        public PetBehavior Previous { get; internal set; } = PetBehavior.Idle;
        public string LastReason { get; internal set; } = "-";
        public float LastDecisionScore { get; internal set; }
        public float TimeSinceInteraction { get; internal set; }

        public IReadOnlyCollection<string> Recent => _recent;

        internal void Push(string line)
        {
            if (_recent.Count >= 12) _recent.Dequeue();
            _recent.Enqueue(line);
        }

        public void Reset()
        {
            _recent.Clear();
            Current = PetBehavior.Idle;
            Previous = PetBehavior.Idle;
            LastReason = "-";
            LastDecisionScore = 0f;
            TimeSinceInteraction = 0f;
        }
    }

    /// <summary>
    /// Utility 评分器。技术方案第九节第7 步：Candidate Actions -> Utility/Priority。
    /// 性格（Energy / Curiosity / Playfulness / Independence）与情绪（Excitement / Sleepiness）
    /// 共同决定各候选行为的分数，因此不同性格的宠物行为频率明显不同（验收标准 F / G）。
    /// </summary>
    public class UtilityScorer
    {
        /// <summary>调试用：上一次评分明细。</summary>
        public readonly List<string> LastScoreDetail = new List<string>(8);

        public float Score(PetBehavior behavior, PetContext context)
        {
            if (context == null) return 0f;

            float score = behavior switch
            {
                PetBehavior.Idle => ScoreIdle(context),
                PetBehavior.Walk => ScoreWalk(context),
                PetBehavior.Run => ScoreRun(context),
                PetBehavior.Turn => ScoreTurn(context),
                PetBehavior.Look => ScoreLook(context),
                PetBehavior.Stop => ScoreStop(context),
                PetBehavior.Recover => ScoreRecover(context),
                PetBehavior.Return => ScoreReturn(context),
                PetBehavior.Excited => ScoreExcited(context),
                _ => 0f,
            };

            return Mathf.Max(0f, Mathf.Min(1f, score));
        }

        // Energy 高 -> 更频繁 Walk（需求文档 5.2 / 8.1）
        private float ScoreIdle(PetContext c)
        {
            // 越有精力越不想Idle；兴奋度高时也不太想Idle
            float energyFactor = 1f - c.Personality.Restlessness;      // 高精力 -> 低Idle分
            float excitementFactor = 1f - c.Emotion.NormalizedExcitement;
            float baseScore = 0.30f + energyFactor * 0.30f + excitementFactor * 0.25f;
            baseScore += (1f - c.TimeSinceInteraction / 15f) * 0.10f;   // 刚被互动过 -> 倾向Idle
            return baseScore;
        }

        // Curiosity 高 -> 探索概率增加；Energy 高 -> Walk 概率增加
        private float ScoreWalk(PetContext c)
        {
            float score = 0.20f;
            score += c.Personality.curiosity * 0.35f;
            score += c.Personality.energy * 0.25f;
            score += c.Emotion.NormalizedExcitement * 0.15f;
            score -= c.Emotion.NormalizedSleepiness * 0.30f;
            return score;
        }

        // Run 概率整体较低，需求文档 8.3：Run 暂不用于普通 Idle
        private float ScoreRun(PetContext c)
        {
            float score = 0.04f;
            score += c.Emotion.NormalizedExcitement * 0.30f;
            score += c.Personality.impulsiveness * 0.15f;
            score -= c.Emotion.NormalizedSleepiness * 0.25f;
            return score;
        }

        private float ScoreTurn(PetContext c)
        {
            float score = 0.10f;
            score += c.Personality.curiosity * 0.20f;
            score += (1f - c.Personality.energy) * 0.15f;
            return score;
        }

        private float ScoreLook(PetContext c)
        {
            float score = 0.08f;
            score += c.Personality.sociability * 0.20f;
            score += c.Emotion.NormalizedExcitement * 0.10f;
            return score;
        }

        private float ScoreStop(PetContext c)
        {
            return 0.05f + c.Emotion.NormalizedSleepiness * 0.20f;
        }

        private float ScoreRecover(PetContext c)
        {
            // 卡住或走太远时 Recover 应该压过其他自主行为
            float score = 0.02f;
            if (c.IsStuck) score += 0.90f;
            if (c.DistanceFromHome > 4f) score += 0.30f;
            if (c.Emotion.NormalizedSleepiness > 0.8f) score += 0.20f;
            return score;
        }

        private float ScoreReturn(PetContext c)
        {
            float score = 0.02f;
            score += Mathf.Clamp01(c.DistanceFromHome / 5f) * 0.40f;
            return score;
        }

        private float ScoreExcited(PetContext c)
        {
            // Playfulness 高 -> 更容易响应互动（需求文档 5.2）
            float score = 0.05f;
            score += c.Emotion.NormalizedExcitement * 0.45f;
            score += c.Personality.playfulness * 0.20f;
            return score;
        }
    }
}