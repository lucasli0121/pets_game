using System;
using PetV1.Core;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 宠物状态机。需求文档交付物 04。
    /// 负责 PetState（Idle/Walking/Running/Interacting/Recovering）的迁移与持续时间管理，
    /// 不负责「要不要迁移」的决策 —— 那是 BehaviorController 的职责。
    /// </summary>
    public class PetStateMachine
    {
        /// <summary>状态切换事件：(from, to)。</summary>
        public event Action<PetState, PetState> StateChanged;

        private PetState _current = PetState.Idle;
        private float _enteredAt;
        private float _plannedDuration;
        private bool _initialized;

        public PetState Current => _current;
        public float TimeInState => Time.time - _enteredAt;
        public float PlannedDuration => _plannedDuration;

        /// <summary>是否处于可被新交互打断的状态。</summary>
        public bool IsInterruptible =>
            _current == PetState.Idle ||
            _current == PetState.Walking ||
            _current == PetState.Recovering;

        public void Initialize(PetState initial = PetState.Idle)
        {
            _current = initial;
            _enteredAt = Time.time;
            _plannedDuration = 0f;
            _initialized = true;
        }

        /// <summary>迁移到新状态。返回 false 表示被拒绝（例如正在 Interacting）。</summary>
        public bool TransitionTo(PetState next, bool force = false, float plannedDuration = 0f)
        {
            if (!_initialized) Initialize(next);
            if (next == _current) return false;
            if (!force && !IsInterruptible && _current == PetState.Interacting)
            {
                PetLog.State("transition rejected (not interruptible)",
                    PetLog.Kv("from", _current, "to", next));
                return false;
            }

            var from = _current;
            _current = next;
            _enteredAt = Time.time;
            _plannedDuration = plannedDuration;
            PetLog.State("transition", PetLog.Kv("from", from, "to", next, "plannedDuration", plannedDuration));
            StateChanged?.Invoke(from, next);
            return true;
        }

        /// <summary>设置当前状态的计划时长（行为控制器用它表达「走 1~3 秒」）。</summary>
        public void SetPlannedDuration(float seconds)
        {
            _plannedDuration = seconds;
            // 单独打一行：TransitionTo 里那条日志打的是「设值之前」的 plannedDuration，
            // 永远是调用方没传参时的默认 0，很容易被误读成「时长没生效」。
            PetLog.State("planned duration set",
                PetLog.Kv("state", _current, "seconds", seconds));
        }

        /// <summary>计划时长是否已耗尽。没设置时长时永远返回 false。</summary>
        public bool IsPlanComplete()
        {
            if (_plannedDuration <= 0f) return false;
            return TimeInState >= _plannedDuration;
        }

        /// <summary>DebugPanel 用。</summary>
        public string Describe() => $"{_current} ({TimeInState:F1}s)";
    }
}