using PetV1.Animation;
using PetV1.Interaction;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 决策上下文。技术方案「运行时核心对象模型」：Behavior AI 每次决策读取的只读快照。
    /// 把「决策需要的所有输入」聚合成一个对象，行为评分器因此不直接依赖各个子系统，
    /// 也让 DebugPanel 可以直接打印完整上下文。
    /// </summary>
    public class PetContext
    {
        // ---- 身体状态 ----
        public PetState State = PetState.Idle;
        public PetBehavior CurrentBehavior = PetBehavior.Idle;
        public PetController Controller;
        public AnimationOrchestrator Animator;

        // ---- 内在状态 ----
        public PetPersonality Personality;
        public PetEmotion Emotion;

        // ---- 环境与时间 ----
        public float TimeSinceInteraction;
        public float DistanceFromHome;
        public bool IsStuck;
        public bool HasUserInteraction;

        // ---- 最近事件 ----
        public GestureEvent LastGesture;
        public string LastEventLabel = "-";

        // ---- 决策输出缓存 ----
        public Vector3 MoveTarget;
        public bool HasMoveTarget;
        public float PlannedDuration;

        public PetPersonality P => Personality;
        public PetEmotion E => Emotion;

        public bool HasGesture => LastGesture != null;

        /// <summary>DebugPanel 一行摘要。</summary>
        public string Compact()
        {
            return $"state={State} behavior={CurrentBehavior} " +
                   $"idle={TimeSinceInteraction:F1}s fromHome={DistanceFromHome:F2}m " +
                   $"stuck={(IsStuck ? "Y" : "N")}";
        }
    }
}