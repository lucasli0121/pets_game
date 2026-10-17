using PetV1.Core;
using PetV1.Interaction;
using UnityEngine;

namespace PetV1.Behavior
{
    using PetV1.Animation;
    using PetV1.Pet;

    /// <summary>
    /// 自主行为驱动（需求文档第十一节）。
    /// 「宠物即使用户什么都不做，也必须会 Idle -> 等待 -> Walk -> Turn -> Idle」。
    ///
    /// 随机性不能纯随机：等待时长、Walk / Turn / Idle 的选择概率都受 Personality 与 Emotion 影响
    /// （Independence 越高越容易自主行动，Energy 越高 Idle 越短，Curiosity 越高越爱探索）。
    /// 另外保证「不会频繁重复同一个动作」（验收标准 G）——同一动作有连续重复上限。
    /// </summary>
    public class AutonomousBehaviorDriver
    {
        [System.Serializable]
        public class Config
        {
            [Header("Idle Duration")]
            [Tooltip("Idle 最短等待（秒）。高 Energy 会把实际时长压到下限附近。")]
            public float idleMinSeconds = 2f;

            [Tooltip("Idle 最长等待（秒）。")]
            public float idleMaxSeconds = 8f;

            [Header("Walk")]
            [Tooltip("单次 Walk 最短时长（秒）。需求文档 8.2：Walk 1~3 秒。")]
            public float walkMinSeconds = 1f;

            [Tooltip("单次 Walk 最长时长（秒）。")]
            public float walkMaxSeconds = 3f;

            [Header("Run")]
            [Tooltip("单次 Run 最短时长（秒）。需求文档 8.3：1~2 秒。")]
            public float runMinSeconds = 1f;

            [Tooltip("单次 Run 最长时长（秒）。")]
            public float runMaxSeconds = 2f;

            [Header("Anti Repeat")]
            [Tooltip("同一行为连续重复次数上限，超过后强制换动作。")]
            public int maxConsecutiveRepeat = 2;

            [Tooltip("Walk 之间的最小间隔（秒），避免连续走动显得机械。")]
            public float cooldownBetweenWalks = 0.8f;

            [Header("Turn")]
            [Tooltip("Turn 的随机角度范围（度）。")]
            public float turnMinAngle = 45f;
            public float turnMaxAngle = 180f;
        }

        private readonly Config _config;
        private PetBehavior _lastBehavior = PetBehavior.Idle;
        private int _consecutiveCount;
        private float _cooldownRemaining;

        /// <summary>最近一次决策的原因，写入黑板供 DebugPanel 显示。</summary>
        public string LastDecisionReason { get; private set; } = "-";

        public AutonomousBehaviorDriver(Config config)
        {
            _config = config ?? new Config();
        }

        public void Reset()
        {
            _lastBehavior = PetBehavior.Idle;
            _consecutiveCount = 0;
            _cooldownRemaining = 0f;
            LastDecisionReason = "-";
        }

        public void Tick(float deltaTime)
        {
            if (_cooldownRemaining > 0f) _cooldownRemaining -= deltaTime;
        }

        /// <summary>当前是否处于冷却中（不产生新的移动行为）。</summary>
        public bool InCooldown => _cooldownRemaining > 0f;

        /// <summary>
        /// 计算下一次 Idle 时长。Energy 越高等待越短（需求文档 8.1）。
        /// </summary>
        public float NextIdleDuration(PetContext context)
        {
            float restlessness = context.Personality.Restlessness;      // 0~1
            float t = 1f - restlessness;                                // 高精力 -> t 小-> 时长短
            float duration = Mathf.Lerp(_config.idleMaxSeconds, _config.idleMinSeconds, t);
            duration *= Random.Range(0.85f, 1.15f);
            return Mathf.Max(_config.idleMinSeconds * 0.5f, duration);
        }

        public float NextWalkDuration()
        {
            return Random.Range(_config.walkMinSeconds, _config.walkMaxSeconds);
        }

        public float NextRunDuration()
        {
            return Random.Range(_config.runMinSeconds, _config.runMaxSeconds);
        }

        public float NextTurnAngle()
        {
            return Random.Range(_config.turnMinAngle, _config.turnMaxAngle) * (Random.value < 0.5f ? -1f : 1f);
        }

        /// <summary>
        /// 记一次决策，用于抑制连续重复。返回 true 表示本次选择因重复而被否决。
        /// </summary>
        public bool WouldRepeat(PetBehavior behavior)
        {
            if (behavior != _lastBehavior) return false;
            return _consecutiveCount >= _config.maxConsecutiveRepeat;
        }

        public void Commit(PetBehavior behavior)
        {
            if (behavior == _lastBehavior)
            {
                _consecutiveCount++;
            }
            else
            {
                _lastBehavior = behavior;
                _consecutiveCount = 1;
            }

            if (behavior == PetBehavior.Walk || behavior == PetBehavior.Run)
            {
                _cooldownRemaining = _config.cooldownBetweenWalks;
            }
        }

        /// <summary>在候选被否决时，从候选列表里取下一个。</summary>
        public PetBehavior PickAlternative(PetBehavior[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return PetBehavior.Idle;
            int offset = Random.Range(0, candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[(i + offset) % candidates.Length];
                if (!WouldRepeat(candidate)) return candidate;
            }
            return PetBehavior.Idle;
        }

        public void SetReason(string reason) => LastDecisionReason = reason;
    }
}