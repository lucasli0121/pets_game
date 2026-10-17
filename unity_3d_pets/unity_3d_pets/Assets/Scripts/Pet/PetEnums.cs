using System;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 宠物的粗粒度状态。需求文档 5.1 定义的第一阶段最小集合。
    /// 后续扩展（Eating / Training / Sleeping / Excited / Growing）在此追加，不要另起一套并行枚举。
    /// </summary>
    public enum PetState
    {
        Idle = 0,
        Walking = 1,
        Running = 2,
        Interacting = 3,
        Recovering = 4,
    }

    /// <summary>
    /// 行为决策的输出。BehaviorController 决定「做什么」，PetState 决定「现在处于哪种状态」。
    /// 两者不是一一对应：<see cref="Stop"/> 与 <see cref="Look"/> 都停留在 PetState.Idle。
    /// </summary>
    public enum PetBehavior
    {
        Idle = 0,
        Walk = 1,
        Run = 2,
        Stop = 3,
        Look = 4,
        Turn = 5,
        Recover = 6,
        Return = 7,
        Excited = 8,
    }

    /// <summary>
    /// 模型能力抽象。需求文档第十四节：行为系统不能假设所有模型都有这些能力。
    /// 声明顺序即优先级，fallback 时按同一顺序向下查找。
    /// </summary>
    public enum PetCapability
    {
        Idle = 0,
        Walk = 1,
        Run = 2,
        Sit = 3,
        Lie = 4,
        Jump = 5,
        Bark = 6,
        Eat = 7,
        Sleep = 8,
        Attack = 9,
    }

    /// <summary>模型实际具备的能力位。由 PetAnimationMap 在导入后填写真实值。</summary>
    [Flags]
    public enum PetCapabilities
    {
        None = 0,
        Idle = 1 << PetCapability.Idle,
        Walk = 1 << PetCapability.Walk,
        Run = 1 << PetCapability.Run,
        Sit = 1 << PetCapability.Sit,
        Lie = 1 << PetCapability.Lie,
        Jump = 1 << PetCapability.Jump,
        Bark = 1 << PetCapability.Bark,
        Eat = 1 << PetCapability.Eat,
        Sleep = 1 << PetCapability.Sleep,
        Attack = 1 << PetCapability.Attack,

        /// <summary>Animals FREE 当前已验证具备的能力：Idle / Walk / Run。</summary>
        AnimalsFreeV1 = Idle | Walk | Run,
    }

    /// <summary>单条能力映射：能力枚举 + 实际 Clip + 该能力对应的移动速度（m/s）。</summary>
    [Serializable]
    public class PetAnimationEntry
    {
        public PetCapability capability = PetCapability.Idle;

        [Tooltip("该能力对应的 AnimationClip。留空表示当前模型不具备该能力，行为系统会走 fallback。")]
        public AnimationClip clip;

        [Tooltip("该能力驱动移动时的目标速度（米/秒）。作为 Animator 的 Speed 参数值。")]
        public float locomotionSpeed = 0f;

        [Tooltip("该 Clip 是否自带位移（Root Motion）。为 true 时移动由动画驱动，代码不再手动平移。")]
        public bool rootMotion = false;

        public PetAnimationEntry() { }

        public PetAnimationEntry(PetCapability capability, AnimationClip clip, float locomotionSpeed, bool rootMotion = false)
        {
            this.capability = capability;
            this.clip = clip;
            this.locomotionSpeed = locomotionSpeed;
            this.rootMotion = rootMotion;
        }

        public bool IsValid => clip != null;
    }
}