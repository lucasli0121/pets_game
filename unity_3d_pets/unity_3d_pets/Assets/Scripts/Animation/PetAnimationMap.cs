using System;
using System.Collections.Generic;
using PetV1.Core;
using UnityEngine;

namespace PetV1.Animation
{
    using PetV1.Pet;

    /// <summary>
    /// 动画映射表。需求文档第十三节：换模型时只改这里的映射，不动 PetController / BehaviorController / Touch。
    /// 一份Map 描述「某个模型的哪些 Clip 对应哪些能力，以及各能力的移动速度」。
    /// </summary>
    [CreateAssetMenu(menuName = "Pet V1/Pet Animation Map", fileName = "PetAnimationMap")]
    public class PetAnimationMap : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("这份映射描述的模型标识，仅用于日志与 DebugPanel。")]
        public string modelId = "AnimalsFree_Dog_001";

        [Header("Capabilities")]
        [Tooltip("每个能力对应一条记录。Clip 留空 = 该模型不具备此能力，行为系统走 fallback。")]
        public List<PetAnimationEntry> entries = new List<PetAnimationEntry>();

        [Header("Defaults")]
        [Tooltip("未在 entries 中显式配置的行为，默认按此速度移动（m/s）。")]
        public float defaultWalkSpeed = 1.2f;
        public float defaultRunSpeed = 3.0f;

        [Header("Animator")]
        [Tooltip("Speed 参数名。Animator Controller 里的浮动参数，由 AnimationOrchestrator 独占写入。")]
        public string speedParameter = "Speed";

        private Dictionary<PetCapability, PetAnimationEntry> _lookup;

        /// <summary>当前模型声明具备的能力位。</summary>
        public PetCapabilities DeclaredCapabilities
        {
            get
            {
                PetCapabilities flags = PetCapabilities.None;
                EnsureLookup();
                foreach (var kv in _lookup)
                {
                    if (kv.Value != null && kv.Value.IsValid) flags |= CapabilityToFlag(kv.Key);
                }
                return flags;
            }
        }

        /// <summary>能力枚举 -> 能力位。位运算必须显式转int，枚举不能直接移位。</summary>
        public static PetCapabilities CapabilityToFlag(PetCapability capability)
        {
            return (PetCapabilities)(1 << (int)capability);
        }

        public bool Has(PetCapability capability)
        {
            EnsureLookup();
            return _lookup.TryGetValue(capability, out var entry) && entry != null && entry.IsValid;
        }

        public AnimationClip GetClip(PetCapability capability)
        {
            EnsureLookup();
            return _lookup.TryGetValue(capability, out var entry) && entry != null ? entry.clip : null;
        }

        public bool UsesRootMotion(PetCapability capability)
        {
            EnsureLookup();
            return _lookup.TryGetValue(capability, out var entry) && entry != null && entry.rootMotion;
        }

        /// <summary>取该能力对应的移动速度；未配置时回退到 Walk / Run 默认值。</summary>
        public float GetLocomotionSpeed(PetCapability capability)
        {
            EnsureLookup();
            if (_lookup.TryGetValue(capability, out var entry) && entry != null && entry.locomotionSpeed > 0f)
                return entry.locomotionSpeed;

            switch (capability)
            {
                case PetCapability.Run: return defaultRunSpeed;
                case PetCapability.Walk: return defaultWalkSpeed;
                default: return 0f;
            }
        }

        /// <summary>
        /// fallback 链。需求文档第十四节：要求的能力模型没有时，按链向下找替代。
        /// 例：Bark -> Jump -> Sit -> Run。找不到任何可用 Clip 时返回 Idle。
        /// </summary>
        private static readonly Dictionary<PetCapability, PetCapability[]> FallbackChains =
            new Dictionary<PetCapability, PetCapability[]>
            {
                { PetCapability.Bark,   new[] { PetCapability.Jump, PetCapability.Sit, PetCapability.Run, PetCapability.Walk, PetCapability.Idle } },
                { PetCapability.Jump,   new[] { PetCapability.Bark,   PetCapability.Run, PetCapability.Walk, PetCapability.Idle } },
                { PetCapability.Sit,    new[] { PetCapability.Lie,    PetCapability.Idle } },
                { PetCapability.Lie,    new[] { PetCapability.Sit,    PetCapability.Sleep, PetCapability.Idle } },
                { PetCapability.Sleep,  new[] { PetCapability.Lie,    PetCapability.Idle } },
                { PetCapability.Eat,    new[] { PetCapability.Sit,    PetCapability.Idle } },
                { PetCapability.Attack, new[] { PetCapability.Bark,   PetCapability.Run, PetCapability.Walk, PetCapability.Idle } },
                { PetCapability.Run,    new[] { PetCapability.Walk,   PetCapability.Idle } },
                { PetCapability.Walk,   new[] { PetCapability.Idle } },
                { PetCapability.Idle,   new[] { PetCapability.Idle } },
            };

        /// <summary>
        /// 按 fallback 链解析出一个当前模型真正具备的能力。
        /// 返回 false 表示整条链都不可用（Map 为空时）。
        /// </summary>
        public bool ResolveWithFallback(PetCapability preferred, out PetCapability resolved, out bool usedFallback)
        {
            EnsureLookup();
            if (Has(preferred))
            {
                resolved = preferred;
                usedFallback = false;
                return true;
            }

            if (FallbackChains.TryGetValue(preferred, out var chain))
            {
                for (int i = 0; i < chain.Length; i++)
                {
                    if (Has(chain[i]))
                    {
                        resolved = chain[i];
                        usedFallback = true;
                        PetLog.Animation("capability fallback",
                            PetLog.Kv("requested", preferred, "resolved", resolved, "model", modelId));
                        return true;
                    }
                }
            }

            // 链上都没有时，最后退到 Idle（再不行就真的没有了）。
            if (Has(PetCapability.Idle))
            {
                resolved = PetCapability.Idle;
                usedFallback = true;
                PetLog.Animation("capability fallback -> Idle",
                    PetLog.Kv("requested", preferred, "model", modelId));
                return true;
            }

            resolved = preferred;
            usedFallback = false;
            return false;
        }

        /// <summary>把 PetBehavior 映射到模型能力。这是「行为 -> 表现」的唯一转换点。</summary>
        public static PetCapability BehaviorToCapability(PetBehavior behavior)
        {
            switch (behavior)
            {
                case PetBehavior.Idle: return PetCapability.Idle;
                case PetBehavior.Walk: return PetCapability.Walk;
                case PetBehavior.Run: return PetCapability.Run;
                case PetBehavior.Excited: return PetCapability.Run;
                case PetBehavior.Stop: return PetCapability.Idle;
                case PetBehavior.Look: return PetCapability.Idle;
                case PetBehavior.Turn: return PetCapability.Idle;
                case PetBehavior.Recover: return PetCapability.Idle;
                case PetBehavior.Return: return PetCapability.Walk;
                default: return PetCapability.Idle;
            }
        }

        /// <summary>PetState 映射到模型能力（供状态机直接驱动动画时使用）。</summary>
        public static PetCapability StateToCapability(PetState state)
        {
            switch (state)
            {
                case PetState.Idle: return PetCapability.Idle;
                case PetState.Walking: return PetCapability.Walk;
                case PetState.Running: return PetCapability.Run;
                case PetState.Interacting: return PetCapability.Idle;
                case PetState.Recovering: return PetCapability.Idle;
                default: return PetCapability.Idle;
            }
        }

        private void EnsureLookup()
        {
            if (_lookup != null) return;
            _lookup = new Dictionary<PetCapability, PetAnimationEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                _lookup[entry.capability] = entry;
            }
        }

        private void OnValidate()
        {
            _lookup = null;
        }

        /// <summary>DebugPanel 用：列出全部能力与是否具备。</summary>
        public string CapabilitySummary()
        {
            EnsureLookup();
            var sb = new System.Text.StringBuilder(64);
            foreach (PetCapability capability in Enum.GetValues(typeof(PetCapability)))
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(capability).Append(Has(capability) ? "✓" : "·");
            }
            return sb.ToString();
        }
    }
}