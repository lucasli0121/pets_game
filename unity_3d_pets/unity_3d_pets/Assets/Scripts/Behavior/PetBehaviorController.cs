using System;
using System.Collections.Generic;
using PetV1.Animation;
using PetV1.Core;
using PetV1.Data;
using PetV1.Interaction;
using UnityEngine;

namespace PetV1.Behavior
{
    using PetV1.Pet;

    /// <summary>
    /// 行为控制器。需求文档第七节的唯一决策入口。
    ///
    /// 强制的调用方向（验收标准 B：不直接由业务代码操作 Animator）：
    ///   Touch / Voice / Food / AutoBehavior
    ///        -> Perception -> Emotion -> Personality
    ///        -> PetBehaviorController（本类）
    ///        -> AnimationOrchestrator -> Animator
    ///
    /// 本类唯一的输出通道是 <see cref="AnimationOrchestrator"/> 与 <see cref="PetController"/>，
    /// 全工程不允许在本类之外直接调用 animator.Play。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetBehaviorController : MonoBehaviour
    {
        [Header("Core References")]
        [SerializeField] private PetController petController;
        [SerializeField] private AnimationOrchestrator animationOrchestrator;
        [SerializeField] private TouchInputManager touchInputManager;
        [SerializeField] private PetAnimationMap animationMap;

        [Header("Personality / Emotion")]
        [SerializeField] private PetPersonalityProfile personalityProfile;

        [Tooltip("勾选后每次启动生成不同的个体性格。取消则固定使用品种值，便于 A/B 对比。")]
        [SerializeField] private bool randomizeIndividual = true;

        [Header("Server Config")]
        [Tooltip("后台下发的「互动产出」配置。留空则用 InteractionRewardConfig.Fallback()。\n"
               + "注意：这里只用来查「某手势对应多少数值」做提示与埋点，\n"
               + "真正的增减一律走服务端 feedPet，客户端不加算。")]
        [SerializeField] private InteractionRewardConfig rewardConfig;

        [Header("Autonomous Behavior")]
        [SerializeField] private AutonomousBehaviorDriver.Config autonomousConfig = new AutonomousBehaviorDriver.Config();

        [Tooltip("关闭后宠物完全静止，只响应触摸。用于对照验证「生命感」。")]
        [SerializeField] private bool autonomyEnabled = true;

        [Header("Interaction Reaction")]
        [Tooltip("Tap 后宠物持续 Interacting 的时长（秒）。")]
        [SerializeField] private float interactDuration = 1.2f;

        [Tooltip("FastSwipe 触发 Run 的最低速度（像素/秒）。")]
        [SerializeField] private float fastSwipeRunThreshold = 900f;

        [Tooltip("SCREEN_SCRATCH 触发 Excited 所需的最低兴奋度（0~100）。")]
        [SerializeField] private float scratchExcitementThreshold = 55f;

        // ---- 运行时 ----
        private PetEmotion _emotion;
        private PetPersonality _personality;
        private PetContext _context;
        private PetStateMachine _stateMachine;
        private BehaviorBlackboard _blackboard = new BehaviorBlackboard();
        private UtilityScorer _scorer = new UtilityScorer();
        private AutonomousBehaviorDriver _autonomous;
        private InteractionRewardConfig _rewardConfig;
        private PetStats _stats;

        private BehaviorPriority _currentPriority = BehaviorPriority.P5_AutonomousIdle;
        private float _behaviorStartTime;
        private float _decisionCooldown;
        private GestureEvent _lastGesture;
        private string _lastEventLabel = "-";
        private float _timeSinceInteraction;

        /// <summary>
        /// 从 Resources 目录读包内默认配置。
        /// 约定把后台下发的兜底配置放在
        /// <c>Assets/Resources/PetV1/DefaultInteractionReward.asset</c>。
        /// 读不到返回 null，由调用方退回 <c>InteractionRewardConfig.Fallback()</c>。
        /// </summary>
        private static InteractionRewardConfig LoadRewardConfig()
        {
            return Resources.Load<InteractionRewardConfig>(
                "PetV1/DefaultInteractionReward");
        }

        /// <summary>
        /// 服务端 getConfig 成功后调用，用下发配置覆盖包内默认值。
        /// 这是唯一允许替换 _rewardConfig 的入口，避免多处持有不同实例。
        /// </summary>
        public void ApplyServerConfig(InteractionRewardConfig cfg)
        {
            if (cfg == null) return;
            _rewardConfig = cfg;
            if (_emotion != null) _emotion.ApplyConfig(cfg);
            PetLog.Verb(PetLogChannel.System, "interaction reward config applied",
                        "source", "server",
                        "entries", (cfg.entries?.Count ?? 0).ToString());
        }

        /// <summary>当前生效的互动产出配置（只读）。UI 与埋点可读，用于显示预测值。</summary>
        public InteractionRewardConfig RewardConfig => _rewardConfig;

        /// <summary>
        /// 服务端权威的养成状态（只读镜像）。UI 显示饱腹/快乐/经验/等级从这里读。
        /// 想改数值必须调 <see cref="ApplyServerStats"/>，不要直接改字段。
        /// </summary>
        public PetStats Stats => _stats;

        /// <summary>
        /// 用云函数返回的权威数据覆盖本地缓存。
        /// 每次 login / syncState / feedPet 成功后调用。
        /// 这是客户端更新养成数值的**唯一**入口。
        /// </summary>
        public void ApplyServerStats(int satiety, int happiness, int level,
                                     int expInLevel, int expToNextLevel, int totalExp)
        {
            if (_stats == null) return;
            int min = _rewardConfig != null ? _rewardConfig.valueMin : 0;
            int max = _rewardConfig != null ? _rewardConfig.valueMax : 100;
            _stats.ApplyServerState(satiety, happiness, level,
                                    expInLevel, expToNextLevel, totalExp, min, max);
            PetLog.Verb(PetLogChannel.System, "stats synced from server",
                PetLog.Kv("satiety", _stats.satiety,
                          "happiness", _stats.happiness,
                          "level", _stats.level,
                          "exp", _stats.expInLevel + "/" + _stats.expToNextLevel));
        }

        /// <summary>状态切换事件（DebugPanel / VFX 挂钩）。</summary>
        public event Action<PetState, PetState> StateChanged;
        public event Action<PetBehavior, PetBehavior> BehaviorChanged;
        public event Action<GestureEvent> GestureHandled;

        public PetContext Context => _context;
        public PetEmotion Emotion => _emotion;
        public PetPersonality Personality => _personality;
        public PetStateMachine StateMachine => _stateMachine;
        public BehaviorBlackboard Blackboard => _blackboard;
        public PetBehavior CurrentBehavior => _blackboard.Current;
        public PetState CurrentState => _stateMachine != null ? _stateMachine.Current : PetState.Idle;
        public GestureEvent LastGesture => _lastGesture;
        public string LastEventLabel => _lastEventLabel;
        public bool AutonomyEnabled => autonomyEnabled;
        public UtilityScorer Scorer => _scorer;

        private void Awake()
        {
            if (petController == null) petController = GetComponent<PetController>();
            if (animationOrchestrator == null) animationOrchestrator = GetComponent<AnimationOrchestrator>();
            if (touchInputManager == null) touchInputManager = FindAnyObjectByType<TouchInputManager>();

            // 兜底自愈：场景构建时若引用没写进来（例如 asset 当时还没生成），
            // 运行期按类型补齐。否则性格/动画映射会静默退化成默认值，
            // 表现上就是「行为都在跑但看不到效果」。
            if (animationOrchestrator == null) animationOrchestrator = FindAnyObjectByType<AnimationOrchestrator>();
            if (animationMap == null) animationMap = FindAnyObjectByType<PetAnimationMap>();
            if (personalityProfile == null) personalityProfile = FindAnyObjectByType<PetPersonalityProfile>();

            _personality = personalityProfile != null
                ? personalityProfile.CreateIndividual(randomizeIndividual ? new System.Random() : new System.Random(0))
                : PetPersonality.CreateIndividual(PetPersonality.CreateBorderCollie(), 0.12f,
                    randomizeIndividual ? new System.Random() : new System.Random(0));

            _emotion = new PetEmotion();
            _stateMachine = new PetStateMachine();
            _autonomous = new AutonomousBehaviorDriver(autonomousConfig);
            _stats = new PetStats();

            // 配置三层兜底（后台文档 6.5）：服务端下发 -> 包内 asset -> 内置常量。
            //
            // 注意：ScriptableObject **不是场景对象**，
            // FindAnyObjectByType 对它无效（早先写成那样会永远返回 null
            // 然后静默退回 Fallback，线上配了也不生效）。
            // 必须走 Resources.Load 或 AssetDatabase。
            if (rewardConfig == null) rewardConfig = LoadRewardConfig();
            _rewardConfig = rewardConfig != null ? rewardConfig : InteractionRewardConfig.Fallback();
            _emotion.ApplyConfig(_rewardConfig);

            _context = new PetContext
            {
                Personality = _personality,
                Emotion = _emotion,
                Stats = _stats,
                Controller = petController,
                Animator = animationOrchestrator,
                CurrentBehavior = PetBehavior.Idle,
                State = PetState.Idle,
            };

            PetLogAnchor.Anchor = transform;

            _stateMachine.Initialize(PetState.Idle);
            _stateMachine.StateChanged += (from, to) =>
            {
                _context.State = to;
                StateChanged?.Invoke(from, to);
            };
        }

        private void OnEnable()
        {
            if (touchInputManager != null) touchInputManager.GestureDetected += HandleGesture;
        }

        private void OnDisable()
        {
            if (touchInputManager != null) touchInputManager.GestureDetected -= HandleGesture;
        }

        private void Start()
        {
            if (animationOrchestrator != null)
                animationOrchestrator.Initialize(animationOrchestrator.Animator, animationMap);

            PetLog.Verb(PetLogChannel.System, "pet behavior controller started",
                PetLog.Kv("breed", personalityProfile != null ? personalityProfile.breedId : "border_collie",
                          "map", animationMap != null ? animationMap.modelId : "null",
                          "autonomy", autonomyEnabled));
            PushBlackboard("init: " + _personality.Compact());
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _timeSinceInteraction += dt;

            _emotion.Tick(dt);
            _autonomous.Tick(dt);
            _decisionCooldown -= dt;

            UpdateContext();

            // 被触摸打断时不做自主决策
            if (_stateMachine.Current == PetState.Interacting && !_stateMachine.IsPlanComplete())
            {
                UpdateAnimationFromState();
                return;
            }

            if (_behaviorStartTime > 0f && _currentBehaviorDurationExpired())
            {
                FinishCurrentBehavior();
            }

            if (autonomyEnabled && _decisionCooldown <= 0f)
            {
                TickAutonomy();
            }

            UpdateAnimationFromState();
        }

        /// <summary>
        /// 判断当前行为是否该结束。
        ///
        /// 注意不能只看 BehaviorAction.maxDuration：自主行为里Idle / Walk 的
        /// maxDuration 故意设为 0（表示「由每次决策随机决定长度」），
        /// 所以真正的计时器是 PetStateMachine 里ApplyBehavior 写入的 plannedDuration。
        /// 只判 maxDuration 会导致宠物进入 Idle 后永远不再行动。
        /// </summary>
        private bool _currentBehaviorDurationExpired()
        {
            if (_behaviorStartTime <= 0f) return false;

            var action = BehaviorLibrary.Get(_blackboard.Current);
            float elapsed = Time.time - _behaviorStartTime;

            // 移动类行为以「到达目标点或超时」为准
            if (action.requiresMovement)
            {
                bool arrived = petController != null && !petController.HasMoveTarget;
                if (arrived || action.DurationExpired(elapsed) || _stateMachine.IsPlanComplete())
                    return true;
                return false;
            }

            // 非移动类行为以状态机计划时长为准（Idle 等待 / Turn 转向 / Recover 恢复）
            if (_stateMachine.PlannedDuration > 0f && elapsed >= _stateMachine.PlannedDuration)
                return true;

            return action.DurationExpired(elapsed);
        }

        private void UpdateContext()
        {
            if (petController == null) return;
            _context.State = _stateMachine.Current;
            _context.CurrentBehavior = _blackboard.Current;
            _context.TimeSinceInteraction = _timeSinceInteraction;
            _context.DistanceFromHome = Vector3.Distance(petController.transform.position, petController.HomePosition);
            _context.IsStuck = petController.IsStuck;
            _context.LastGesture = _lastGesture;
            _context.LastEventLabel = _lastEventLabel;
            _context.HasUserInteraction = _lastGesture != null && _timeSinceInteraction < 3f;
        }

        // ================= 自主行为循环 =================

        /// <summary>
        /// 决策节流间隔（秒）。每次决策后重新随机，避免每帧重算。
        /// </summary>
        [Tooltip("两次自主决策之间的最小间隔（秒）。")]
        [SerializeField] private Vector2 decisionInterval = new Vector2(0.35f, 1.1f);

        /// <summary>
        /// 自主决策。当前行为未结束时不重新决策，避免每帧抖动。
        /// </summary>
        private void TickAutonomy()
        {
            // 还有进行中的行为就让它跑完
            if (Time.time - _behaviorStartTime < BehaviorLibrary.Get(_blackboard.Current).minDuration)
                return;

            // 距离上次决策太近（例如刚 Idle 完）就再等等
            if (_autonomous.InCooldown && _blackboard.Current != PetBehavior.Idle) return;

            PetBehavior chosen = ScoreAndChoose();
            ExecuteBehavior(chosen, BehaviorPriority.P5_AutonomousIdle, "autonomous");

            // 节流：无论这次选了什么，都等一小段时间再决策下一次。
            // 不这样做的话，连续选中同一行为时会在每帧反复进入 ExecuteBehavior。
            _decisionCooldown = UnityEngine.Random.Range(decisionInterval.x, decisionInterval.y);
        }

        /// <summary>Utility 评分 + 抑制连续重复，返回本次要执行的行为。</summary>
        private PetBehavior ScoreAndChoose()
        {
            PetBehavior best = PetBehavior.Idle;
            float bestScore = float.MinValue;

            PetBehavior[] autonomousCandidates =
            {
                PetBehavior.Idle, PetBehavior.Walk, PetBehavior.Run,
                PetBehavior.Turn, PetBehavior.Look, PetBehavior.Return, PetBehavior.Recover,
            };

            _scorer.LastScoreDetail.Clear();
            foreach (var candidate in autonomousCandidates)
            {
                float score = _scorer.Score(candidate, _context);
                _scorer.LastScoreDetail.Add($"{candidate}={score:F2}");
                if (score > bestScore) { bestScore = score; best = candidate; }
            }

            // 抗重复：候选被否决时换一个
            if (_autonomous.WouldRepeat(best))
            {
                PetBehavior alt = _autonomous.PickAlternative(autonomousCandidates);
                PetLog.Behavior("suppress repeated behavior",
                    PetLog.Kv("tried", best, "picked", alt, "repeatLimit", autonomousConfig.maxConsecutiveRepeat));
                best = alt;
            }

            _blackboard.LastDecisionScore = bestScore;
            return best;
        }

        // ================= 行为执行 =================

        /// <summary>
        /// 执行一个行为。这是所有行为变更的唯一收口。
        /// </summary>
        public void ExecuteBehavior(PetBehavior behavior, BehaviorPriority priority, string reason)
        {
            // 打断判定：低优先级不能打断高优先级的进行中行为
            if (_behaviorStartTime > 0f && (int)priority > (int)_currentPriority)
            {
                var currentAction = BehaviorLibrary.Get(_blackboard.Current);
                if (!currentAction.interruptible)
                {
                    PetLog.Behavior("behavior rejected (not interruptible)",
                        PetLog.Kv("current", _blackboard.Current, "requested", behavior, "reason", reason));
                    return;
                }
            }

            PetBehavior previous = _blackboard.Current;
            // 目标行为与当前相同：不重置计时，但需要重新掷一次时长，
            // 否则 Idle 的等待时间会锁死在上一次的值。
            if (previous == behavior && _behaviorStartTime > 0f)
            {
                ApplyBehavior(behavior, reason + " (refresh)");
                return;
            }

            _blackboard.Previous = previous;
            _blackboard.Current = behavior;
            _blackboard.LastReason = reason;
            _blackboard.Push($"{previous}->{behavior} ({reason}) score={_blackboard.LastDecisionScore:F2}");

            _behaviorStartTime = Time.time;
            _currentPriority = priority;
            _autonomous.Commit(behavior);
            _autonomous.SetReason(reason);

            PetLog.Behavior("behavior",
                PetLog.Kv("from", previous, "to", behavior, "priority", priority, "reason", reason,
                          "score", _blackboard.LastDecisionScore));

            ApplyBehavior(behavior, reason);
            BehaviorChanged?.Invoke(previous, behavior);
        }

        private void ApplyBehavior(PetBehavior behavior, string reason)
        {
            if (petController == null || animationOrchestrator == null) return;

            switch (behavior)
            {
                case PetBehavior.Idle:
                {
                    petController.Stop();
                    _stateMachine.TransitionTo(PetState.Idle, true);
                    _stateMachine.SetPlannedDuration(_autonomous.NextIdleDuration(_context));
                    animationOrchestrator.PlayBehavior(PetBehavior.Idle, 0f);
                    break;
                }

                case PetBehavior.Walk:
                {
                    Vector3 target = petController.GetRandomWanderPoint();
                    float speed = ResolveSpeed(PetCapability.Walk);
                    petController.MoveTo(target, speed);
                    _stateMachine.TransitionTo(PetState.Walking, true);
                    _stateMachine.SetPlannedDuration(_autonomous.NextWalkDuration());
                    animationOrchestrator.PlayBehavior(PetBehavior.Walk, speed);
                    break;
                }

                case PetBehavior.Run:
                {
                    Vector3 target = petController.GetRandomWanderPoint();
                    float speed = ResolveSpeed(PetCapability.Run);
                    petController.MoveTo(target, speed);
                    _stateMachine.TransitionTo(PetState.Running, true);
                    _stateMachine.SetPlannedDuration(_autonomous.NextRunDuration());
                    animationOrchestrator.PlayBehavior(PetBehavior.Run, speed);
                    break;
                }

                case PetBehavior.Stop:
                {
                    petController.Stop();
                    _stateMachine.TransitionTo(PetState.Idle, true);
                    _stateMachine.SetPlannedDuration(UnityEngine.Random.Range(0.4f, 1.2f));
                    animationOrchestrator.PlayBehavior(PetBehavior.Stop, 0f);
                    break;
                }

                case PetBehavior.Turn:
                {
                    petController.TurnByAngle(_autonomous.NextTurnAngle());
                    _stateMachine.TransitionTo(PetState.Idle, true);
                    _stateMachine.SetPlannedDuration(UnityEngine.Random.Range(0.3f, 0.9f));
                    animationOrchestrator.PlayBehavior(PetBehavior.Turn, 0f);
                    break;
                }

                case PetBehavior.Look:
                {
                    // 转向用户方向看一眼，再回到 Idle（Look 不移动）
                    petController.Stop();
                    _stateMachine.TransitionTo(PetState.Idle, true);
                    _stateMachine.SetPlannedDuration(UnityEngine.Random.Range(0.6f, 1.5f));
                    animationOrchestrator.PlayBehavior(PetBehavior.Look, 0f);
                    break;
                }

                case PetBehavior.Recover:
                {
                    petController.Stop();
                    petController.RecoverFromStuck();
                    _stateMachine.TransitionTo(PetState.Recovering, true);
                    _stateMachine.SetPlannedDuration(0.8f);
                    animationOrchestrator.PlayBehavior(PetBehavior.Recover, 0f);
                    break;
                }

                case PetBehavior.Return:
                {
                    float speed = ResolveSpeed(PetCapability.Walk);
                    petController.ReturnHome(speed);
                    _stateMachine.TransitionTo(PetState.Walking, true);
                    _stateMachine.SetPlannedDuration(3f);
                    animationOrchestrator.PlayBehavior(PetBehavior.Return, speed);
                    break;
                }

                case PetBehavior.Excited:
                {
                    // 需求文档第九节：Excited -> Run -> Turn -> Return（模型没有 Bark/Jump 时的 fallback 链）
                    float speed = ResolveSpeed(PetCapability.Run);
                    Vector3 target = petController.GetRandomWanderPoint();
                    petController.MoveTo(target, speed);
                    _stateMachine.TransitionTo(PetState.Running, true);
                    _stateMachine.SetPlannedDuration(UnityEngine.Random.Range(0.8f, 1.6f));
                    animationOrchestrator.PlayCapability(PetCapability.Bark, speed);
                    break;
                }
            }
        }

        private float ResolveSpeed(PetCapability capability)
        {
            if (animationMap == null) return capability == PetCapability.Run ? 3f : 1.2f;
            float speed = animationMap.GetLocomotionSpeed(capability);
            if (speed > 0f) return speed;
            return capability == PetCapability.Run
                ? animationMap.defaultRunSpeed
                : animationMap.defaultWalkSpeed;
        }

        private void FinishCurrentBehavior()
        {
            _behaviorStartTime = 0f;
            // 计划时长到 -> 交回给自主决策循环。
            // 不在这里直接写死 Idle，否则会跳过 Utility 打分，
            // 宠物将永远在 Idle/Walk 之间循环，失去 Turn / Look / Return 等变化。
            _decisionCooldown = 0f;
        }

        /// <summary>把当前移动速度同步给动画编排器，保证 Idle/Walk/Run 与实际位移一致。</summary>
        private void UpdateAnimationFromState()
        {
            if (animationOrchestrator == null || petController == null) return;
            if (_blackboard.Current == PetBehavior.Idle ||
                _blackboard.Current == PetBehavior.Stop ||
                _blackboard.Current == PetBehavior.Look ||
                _blackboard.Current == PetBehavior.Turn ||
                _blackboard.Current == PetBehavior.Recover)
            {
                animationOrchestrator.PlayBehavior(PetBehavior.Idle, 0f);
            }
            else
            {
                animationOrchestrator.PlayBehavior(_blackboard.Current, petController.CurrentSpeed);
            }
        }

        // ================= 触摸 / 手势处理 =================

        /// <summary>
        /// 处理一个手势事件：先更新情绪，再决定行为。这体现需求文档第二十一节的那条链路。
        /// </summary>
        private void HandleGesture(GestureEvent ev)
        {
            if (ev == null) return;
            _lastGesture = ev;
            _lastEventLabel = ev.gesture.ToString();
            _timeSinceInteraction = 0f;

            PetLog.Behavior("gesture received",
                PetLog.Kv("gesture", ev.gesture, "region", ev.targetRegion, "hitPet", ev.hitPet));

            // 没点到宠物身上（点到墙/地板）就不该有情绪和动作响应。
            // 之前不区分，导致用户随手点屏幕任何地方，狗都会「有反应」。
            if (!ev.hitPet)
            {
                PetLog.Behavior("gesture ignored (missed pet)",
                    PetLog.Kv("gesture", ev.gesture));
                GestureHandled?.Invoke(ev);
                return;
            }

            // 1) Emotion更新（需求文档第十节）
            UpdateEmotionFromGesture(ev);

            // 2) Personality + Emotion 共同决定行为响应
            float drive = _personality.InteractionDrive;
            PetBehavior response = ChooseInteractionBehavior(ev, drive);

            if (response == PetBehavior.Idle)
            {
                // 点击/抚摸没有专门的动画时，进Interacting 状态原地响应（需求文档 6.2：
                // 不要求立即出现特殊动作，重点验证事件链路）。
                RespondInPlace(ev);
            }
            else
            {
                ExecuteBehavior(response, PriorityForGesture(ev), $"gesture:{ev.gesture}");
            }

            GestureHandled?.Invoke(ev);
        }

        /// <summary>
        /// 原地响应：进入 Interacting 状态并保持 interactDuration 秒，期间不跑自主决策。
        /// </summary>
        private void RespondInPlace(GestureEvent ev)
        {
            if (petController != null) petController.Stop();

            // 黑板同步成 Idle：此时宠物站着不动，只是状态机标记为 Interacting。
            // 不同步的话 DebugPanel 会显示上一次的 Behavior（比如 Walk），与实际不符。
            _blackboard.Previous = _blackboard.Current;
            _blackboard.Current = PetBehavior.Idle;
            _blackboard.LastReason = $"gesture:{ev.gesture} (in place)";

            _behaviorStartTime = Time.time;
            _currentPriority = PriorityForGesture(ev);
            _stateMachine.TransitionTo(PetState.Interacting, true, interactDuration);
            if (animationOrchestrator != null) animationOrchestrator.PlayBehavior(PetBehavior.Idle, 0f);

            _blackboard.Push($"Interacting ({ev.gesture}) for {interactDuration:F1}s");
            PetLog.Behavior("respond in place",
                PetLog.Kv("gesture", ev.gesture, "region", ev.targetRegion, "duration", interactDuration));
        }

        /// <summary>
        /// 手势 -> 表现层情绪。
        ///
        /// 这里**只动** excitement / sleepiness 两个不入库的临时量，
        /// 用来驱动行为决策（要不要跑、要不要扑）。
        ///
        /// 养成数值（happiness / satiety / exp）**不在这里加算** ——
        /// 那三个是服务端权威值，见 PetStats。
        /// 早先这里按手势调 ApplyTap(+2) / ApplyFastSweep(+5) 等方法，
        /// 那些常量等于客户端自算养成值，改包即可刷，已移除。
        /// 「某手势应该给多少」查 InteractionRewardConfig，只用于上报与提示。
        /// </summary>
        private void UpdateEmotionFromGesture(GestureEvent ev)
        {
            switch (ev.gesture)
            {
                // 高强度：多指挠屏 / 扑屏，可触发 Excited、Run、Jump
                case GestureType.MultiScratch:
                case GestureType.ScreenScratch:
                case GestureType.ScreenMultiTouch:
                    _emotion.ApplyHighExcitement(Mathf.Clamp01(ev.averageSpeed / 1200f));
                    break;

                // 中等强度：快划，按速度给增益
                case GestureType.FastSwipe:
                case GestureType.ScreenSwipe:
                    _emotion.ApplyInteraction(Mathf.Clamp01(ev.averageSpeed / 1200f));
                    break;

                // 抚摸：时长越长越兴奋，但有上限
                case GestureType.Stroke:
                case GestureType.LongPress:
                    _emotion.ApplyInteraction(Mathf.Clamp01(ev.durationMs / 2000f));
                    break;

                // 轻触：固定小增益
                case GestureType.Tap:
                case GestureType.DoubleTap:
                case GestureType.MultiTap:
                case GestureType.MultiHold:
                case GestureType.ScreenTap:
                default:
                    _emotion.ApplyInteraction(0f);
                    break;
            }

            // 养成数值的预测值仅用于日志与埋点，真正增减由服务端 feedPet 结算。
            // 合并进下面这条日志，不要每次手势打两条。
            int predictedHappy = 0;
            int predictedExpEvery = 0;
            if (_rewardConfig != null)
            {
                var reward = _rewardConfig.Find(ev.gesture);
                if (reward != null && reward.enabled)
                {
                    predictedHappy = reward.happinessDelta;
                    predictedExpEvery = reward.expPerCount;
                }
            }

            PetLog.Emotion("updated",
                PetLog.Kv("gesture", ev.gesture,
                          "excite", _emotion.excitement,
                          "sleepy", _emotion.sleepiness,
                          "predHappy", predictedHappy,
                          "predExpEvery", predictedExpEvery));
        }

        private PetBehavior ChooseInteractionBehavior(GestureEvent ev, float drive)
        {
            switch (ev.gesture)
            {
                // 快速划动 -> Run（需求文档 8.3：用于验证 Animator 状态切换）
                case GestureType.FastSwipe:
                case GestureType.ScreenSwipe:
                    if (ev.averageSpeed >= fastSwipeRunThreshold)
                        return PetBehavior.Run;
                    return PetBehavior.Excited;

                // 多指挠屏 -> Excited（需求文档第九节）
                case GestureType.MultiScratch:
                case GestureType.ScreenScratch:
                case GestureType.MultiTap:
                case GestureType.MultiHold:
                case GestureType.ScreenMultiTouch:
                    if (_emotion.excitement >= scratchExcitementThreshold && drive > 0.3f)
                        return PetBehavior.Excited;
                    return PetBehavior.Run;

                case GestureType.Tap:
                case GestureType.DoubleTap:
                case GestureType.LongPress:
                case GestureType.Stroke:
                case GestureType.ScreenTap:
                default:
                    // 点击/抚摸：Playfulness 高-> Excited，否则原地 Interacting 响应
                    if (drive > 0.75f && _emotion.NormalizedExcitement > 0.4f)
                        return PetBehavior.Excited;
                    return PetBehavior.Idle;   // 走 RespondInPlace 分支
            }
        }

        private static BehaviorPriority PriorityForGesture(GestureEvent ev)
        {
            switch (ev.gesture)
            {
                case GestureType.MultiScratch:
                case GestureType.ScreenScratch:
                case GestureType.MultiTap:
                case GestureType.ScreenMultiTouch:
                    return BehaviorPriority.P2_StrongInteraction;
                case GestureType.FastSwipe:
                case GestureType.ScreenSwipe:
                    return BehaviorPriority.P2_StrongInteraction;
                default:
                    return BehaviorPriority.P4_TouchReaction;
            }
        }

        // ================= 测试 / 调试接口 =================

        /// <summary>DebugPanel 按钮：强制某个行为。</summary>
        public void DebugForceBehavior(PetBehavior behavior)
        {
            _behaviorStartTime = 0f;
            ExecuteBehavior(behavior, BehaviorPriority.P1_EatingOrTraining, "debug button");
        }

        /// <summary>DebugPanel 按钮：重置情绪与状态。</summary>
        public void DebugReset()
        {
            _emotion.Reset();
            _timeSinceInteraction = 0f;
            _lastGesture = null;
            _lastEventLabel = "-";
            _autonomous.Reset();
            _blackboard.Reset();
            _stateMachine.TransitionTo(PetState.Idle, true);
            _behaviorStartTime = 0f;
            if (petController != null)
            {
                petController.Stop();
                petController.Teleport(petController.HomePosition);
            }
            _blackboard.Current = PetBehavior.Idle;
            PetLog.Behavior("debug reset");
        }

        /// <summary>DebugPanel 按钮：清空手势识别状态。</summary>
        public void DebugResetGestures()
        {
            if (touchInputManager != null && touchInputManager.Recognizer != null)
            {
                touchInputManager.Recognizer.Reset();
                PetLog.Touch("gesture recognizer reset");
            }
        }

        /// <summary>切换自主行为开关（验收标准 G：用户不操作时宠物仍会行动）。</summary>
        public void SetAutonomy(bool enabled)
        {
            autonomyEnabled = enabled;
            PetLog.Behavior("autonomy toggled", PetLog.Kv("enabled", enabled));
        }

        /// <summary>DebugPanel 展示用：本次决策的全部候选分数。</summary>
        public string ScoreDetail()
        {
            if (_scorer.LastScoreDetail.Count == 0) return "-";
            return string.Join(" ", _scorer.LastScoreDetail.ToArray());
        }

        private void PushBlackboard(string line)
        {
            _blackboard.Push(line);
            PetLog.Behavior("blackboard: " + line);
        }
    }
}