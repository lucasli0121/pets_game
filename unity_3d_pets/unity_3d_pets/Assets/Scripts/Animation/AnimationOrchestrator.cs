using System.Collections.Generic;
using PetV1.Core;
using PetV1.Pet;
using UnityEngine;

namespace PetV1.Animation
{
    /// <summary>
    /// 动画编排器。需求文档第十二节：这是全工程唯一允许触碰 Animator 的类。
    /// 业务代码一律调用 <see cref="PlayBehavior"/> / <see cref="PlayCapability"/>，
    /// 不允许出现 animator.Play("Walk") 这类硬编码字符串。
    /// 具体 Clip 由 <see cref="PetAnimationMap"/> 提供，换模型只换 Map。
    /// </summary>
    [DisallowMultipleComponent]
    public class AnimationOrchestrator : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("被驱动的 Animator。若为空会自动向父级/子级查找。")]
        [SerializeField] private Animator animator;

        [Tooltip("动画映射表。为空时退化为「无映射」模式，只打日志不播动画。")]
        [SerializeField] private PetAnimationMap animationMap;

        [Header("Locomotion")]
        [Tooltip("把米/秒换算到 Animator Speed 参数时使用的缩放。1 表示 1 m/s = 1 参数单位。")]
        [SerializeField] private float speedScale = 1f;

        [Tooltip("速度低于该值时视为静止，强制 Idle，避免浮点残留导致 Idle/Walk 抖动。")]
        [SerializeField] private float idleEpsilon = 0.05f;

        [Header("Transition")]
        [Tooltip("一次性能力（如 Bark / Jump）作为 Trigger 播放时的默认过渡时长。")]
        [SerializeField] private float crossFadeDuration = 0.12f;

        private int _speedHash;
        private float _currentSpeed;
        private PetCapability _currentCapability = PetCapability.Idle;
        private AnimationClip _currentClip;
        private bool _initialized;

        /// <summary>DebugPanel 读取用。</summary>
        public PetCapability CurrentCapability => _currentCapability;
        public string CurrentClipName => _currentClip != null ? _currentClip.name : "-";
        public float CurrentSpeed => _currentSpeed;
        public PetAnimationMap Map => animationMap;
        public Animator Animator => animator;

        /// <summary>由 PetController 在 Awake 阶段注入。</summary>
        public void Initialize(Animator targetAnimator, PetAnimationMap map)
        {
            if (targetAnimator != null) animator = targetAnimator;
            if (map != null) animationMap = map;
            _speedHash = BuildSpeedHash();
            _initialized = true;
            PetLog.Animation("orchestrator initialized",
                PetLog.Kv("animator", animator != null ? animator.name : "null",
                          "map", animationMap != null ? animationMap.modelId : "null"));
        }

        private int BuildSpeedHash()
        {
            string param = animationMap != null && !string.IsNullOrEmpty(animationMap.speedParameter)
                ? animationMap.speedParameter
                : "Speed";
            return Animator.StringToHash(param);
        }

        /// <summary>
        /// 直接注入引用。Editor 构建工具在序列化写入失效时兜底调用，
        /// 也可在运行期由装配代码使用。
        /// </summary>
        public void SetWiredReferences(Animator targetAnimator, PetAnimationMap map)
        {
            if (targetAnimator != null) animator = targetAnimator;
            if (map != null) animationMap = map;
            _speedHash = BuildSpeedHash();
            _initialized = true;
        }

        /// <summary>
        /// 运行时兜底：确保 animationMap / animator 都已就位。
        ///
        /// 为什么需要兜底：本组件挂在场景里的 "PetRuntime" 空物体上，而 Animator 在狗prefab 内部，
        /// 两者不是父子关系，所以 Awake 里的 GetComponentInChildren 找不到 Animator。
        /// 结果是 Speed 参数永远写不进去，动画停在 Idle，宠物表现为「四肢不动地滑行」。
        /// </summary>
        private bool EnsureWired()
        {
            if (animationMap == null)
            {
                var map = FindAnyObjectByType<PetAnimationMap>();
                if (map != null)
                {
                    animationMap = map;
                    _speedHash = BuildSpeedHash();
                    PetLog.Animation("auto-wired animation map", PetLog.Kv("map", map.modelId));
                }
            }

            if (animator == null)
            {
                // 先在本物体及子级找，再全场景找——狗 prefab 是场景里另一个独立实例，
                // 两者不是父子关系，只能靠全场景查找。
                animator = GetComponentInChildren<Animator>(true);
                if (animator == null) animator = FindAnyObjectByType<Animator>();
                if (animator != null)
                    PetLog.Animation("auto-wired animator", PetLog.Kv("animator", animator.name));
            }

            return animator != null;
        }

        private void Awake()
        {
            _speedHash = BuildSpeedHash();
            _initialized = true;
            EnsureWired();
        }

        private void Update()
        {
            // Speed 参数每帧同步真实移动速度，保证 Idle/Walk/Run 的切换与实际位移一致。
            // 这里不用 Animator.Play，而是靠参数驱动状态机 —— 换模型时 Controller 名字变了只需改 Map。
            if (!EnsureWired()) return;
            if (animator.runtimeAnimatorController == null) return;

            float target = _currentSpeed <= idleEpsilon ? 0f : _currentSpeed * speedScale;
            if (!Mathf.Approximately(animator.GetFloat(_speedHash), target))
            {
                animator.SetFloat(_speedHash, target);
            }
        }

        /// <summary>
        /// 播放一个行为。这是 BehaviorController 唯一能影响动画表现的入口。
        /// </summary>
        public void PlayBehavior(PetBehavior behavior, float moveSpeed = 0f)
        {
            PetCapability preferred = PetAnimationMap.BehaviorToCapability(behavior);
            PlayCapability(preferred, moveSpeed);
        }

        /// <summary>
        /// 播放一个模型能力，内部做 fallback 解析。
        /// </summary>
        public void PlayCapability(PetCapability preferred, float moveSpeed = 0f)
        {
            if (!_initialized) Awake();

            // 移动速度必须在这里记录：即使 Map 缺失导致播不了动画，
            // Update 也要靠 _currentSpeed 把 Speed 参数写进 Animator，
            // 否则 Speed 恒为 0，宠物会保持 Idle 姿态滑行。
            _currentSpeed = Mathf.Max(0f, moveSpeed);

            EnsureWired();

            if (animationMap == null)
            {
                // 没有映射时不能猜：只记录行为，避免写出依赖具体模型名字的临时代码。
                PetLog.Animation("no animation map, skip playback",
                    PetLog.Kv("behavior", preferred, "speed", _currentSpeed));
                return;
            }

            if (!animationMap.ResolveWithFallback(preferred, out var resolved, out bool usedFallback))
            {
                PetLog.Animation("capability unresolved", PetLog.Kv("requested", preferred, "model", animationMap.modelId));
                return;
            }

            AnimationClip clip = animationMap.GetClip(resolved);
            if (clip == null) return;

            bool capabilityChanged = resolved != _currentCapability;
            _currentCapability = resolved;
            _currentClip = clip;

            // 一次性动作（Bark / Jump）用 Trigger 触发，走 Controller 里预置的过渡。
            // Idle/Walk/Run 走 Speed 参数，由 Update 每帧同步，不在这里切状态。
            if (IsOneShot(resolved) && animator != null && animator.runtimeAnimatorController != null)
            {
                int trigger = OneShotTriggerHash;
                animator.ResetTrigger(trigger);
                animator.SetTrigger(trigger);
                // 一次性动作用固定时长过渡，避免 Walk->Bark 时出现明显跳变
                if (crossFadeDuration > 0f)
                {
                    int layer = animator.GetLayerIndex(OneShotLayerName);
                    if (layer >= 0) animator.CrossFadeInFixedTime(layer, crossFadeDuration);
                }
                PetLog.Animation("one-shot",
                    PetLog.Kv("capability", resolved, "clip", clip.name, "fallback", usedFallback));
                return;
            }

            if (capabilityChanged)
            {
                PetLog.Animation("locomotion",
                    PetLog.Kv("capability", resolved, "clip", clip.name, "speed", _currentSpeed, "fallback", usedFallback));
            }
        }

        /// <summary>OneShot 层名称。AnimatorController 里约定叫这个名字。</summary>
        public const string OneShotLayerName = "OneShot";

        private int _oneShotTrigger;

        /// <summary>一次性动作统一走这个 Trigger，避免为每个 Clip 生成状态名。</summary>
        private int OneShotTriggerHash => _oneShotTrigger != 0
            ? _oneShotTrigger
            : (_oneShotTrigger = Animator.StringToHash("OneShot"));

        private static bool IsOneShot(PetCapability capability)
        {
            switch (capability)
            {
                case PetCapability.Bark:
                case PetCapability.Jump:
                case PetCapability.Attack:
                case PetCapability.Eat:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 仅在测试按钮里使用：直接播一个映射里的 Clip，绕过行为决策。
        /// 之所以保留它，是为了验收标准 B 里「状态切换没有明显卡顿」能手动逐个验证；
        /// 业务链路不会调用。
        /// </summary>
        public void DebugPlayClip(PetCapability capability)
        {
            if (animationMap == null) return;
            AnimationClip clip = animationMap.GetClip(capability);
            if (clip == null)
            {
                PetLog.Animation("debug play skipped (missing clip)", PetLog.Kv("capability", capability));
                return;
            }
            _currentClip = clip;
            _currentCapability = capability;
            PetLog.Animation("debug play clip", PetLog.Kv("capability", capability, "clip", clip.name));
        }

        /// <summary>停止移动，回到 Idle 表现。</summary>
        public void StopLocomotion()
        {
            _currentSpeed = 0f;
            _currentCapability = PetCapability.Idle;
            _currentClip = animationMap != null ? animationMap.GetClip(PetCapability.Idle) : null;
        }

        /// <summary>获取全部可用能力，供 DebugPanel 与能力校验使用。</summary>
        public List<PetCapability> GetAvailableCapabilities()
        {
            var result = new List<PetCapability>();
            if (animationMap == null) return result;
            foreach (PetCapability capability in System.Enum.GetValues(typeof(PetCapability)))
            {
                if (animationMap.Has(capability)) result.Add(capability);
            }
            return result;
        }
    }
}