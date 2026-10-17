using System;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 11 维性格向量，取值 0~1。技术方案文档第七节。
    /// 第一阶段只有 Energy / Curiosity / Playfulness / Independence 真正参与行为计算，
    /// 其余维度先保留数据结构（见验收标准 F：至少 4 个属性实际参与计算）。
    /// </summary>
    [Serializable]
    public class PetPersonality
    {
        public const int DimensionCount = 11;

        [Range(0f, 1f)] public float energy = 0.80f;
        [Range(0f, 1f)] public float sociability = 0.72f;
        [Range(0f, 1f)] public float affection = 0.68f;
        [Range(0f, 1f)] public float curiosity = 0.82f;
        [Range(0f, 1f)] public float courage = 0.62f;
        [Range(0f, 1f)] public float impulsiveness = 0.55f;
        [Range(0f, 1f)] public float obedience = 0.78f;
        [Range(0f, 1f)] public float playfulness = 0.85f;
        [Range(0f, 1f)] public float independence = 0.42f;
        [Range(0f, 1f)] public float sensitivity = 0.55f;
        [Range(0f, 1f)] public float learningAbility = 0.82f;

        /// <summary>
        /// Border Collie 的先天偏置。技术方案第七节给出的初始值。
        /// 注意：这是游戏调参值，不宣称是真实犬种统计。
        /// </summary>
        public static PetPersonality CreateBorderCollie()
        {
            return new PetPersonality
            {
                energy = 0.80f,
                sociability = 0.72f,
                affection = 0.68f,
                curiosity = 0.82f,
                courage = 0.62f,
                impulsiveness = 0.55f,
                obedience = 0.78f,
                playfulness = 0.85f,
                independence = 0.42f,
                sensitivity = 0.55f,
                learningAbility = 0.82f,
            };
        }

        public static PetPersonality CreateDefault() => CreateBorderCollie();

        /// <summary>
        /// 个体差异：在品种偏置上叠加随机扰动，形成每只宠物自己的性格。
        /// </summary>
        public static PetPersonality CreateIndividual(PetPersonality breed, float variance = 0.12f, System.Random rng = null)
        {
            rng ??= new System.Random();
            PetPersonality result = breed?.Clone() ?? CreateDefault();
            result.energy = Clamp01(result.energy + Jitter(rng, variance));
            result.sociability = Clamp01(result.sociability + Jitter(rng, variance));
            result.affection = Clamp01(result.affection + Jitter(rng, variance));
            result.curiosity = Clamp01(result.curiosity + Jitter(rng, variance));
            result.courage = Clamp01(result.courage + Jitter(rng, variance));
            result.impulsiveness = Clamp01(result.impulsiveness + Jitter(rng, variance));
            result.obedience = Clamp01(result.obedience + Jitter(rng, variance));
            result.playfulness = Clamp01(result.playfulness + Jitter(rng, variance));
            result.independence = Clamp01(result.independence + Jitter(rng, variance));
            result.sensitivity = Clamp01(result.sensitivity + Jitter(rng, variance));
            result.learningAbility = Clamp01(result.learningAbility + Jitter(rng, variance));
            return result;
        }

        /// <summary>
        /// 自主性总开关。Independence 高 → 用户不操作时也会自己动（需求文档 5.2）。
        /// </summary>
        public float AutonomousDrive => Mathf.Clamp01(independence * 0.65f + curiosity * 0.35f);

        /// <summary>运动意愿。Energy 高 → Idle 等待时间更短、Walk 概率更高（需求文档 5.2 / 8.1）。</summary>
        public float Restlessness => Mathf.Clamp01(energy * 0.7f + curiosity * 0.3f);

        /// <summary>互动响应意愿。Playfulness 高 → 更容易响应触摸（需求文档 5.2）。</summary>
        public float InteractionDrive => Mathf.Clamp01(playfulness * 0.6f + sociability * 0.4f);

        public PetPersonality Clone()
        {
            return new PetPersonality
            {
                energy = energy,
                sociability = sociability,
                affection = affection,
                curiosity = curiosity,
                courage = courage,
                impulsiveness = impulsiveness,
                obedience = obedience,
                playfulness = playfulness,
                independence = independence,
                sensitivity = sensitivity,
                learningAbility = learningAbility,
            };
        }

        public void CopyFrom(PetPersonality other)
        {
            if (other == null) return;
            energy = other.energy;
            sociability = other.sociability;
            affection = other.affection;
            curiosity = other.curiosity;
            courage = other.courage;
            impulsiveness = other.impulsiveness;
            obedience = other.obedience;
            playfulness = other.playfulness;
            independence = other.independence;
            sensitivity = other.sensitivity;
            learningAbility = other.learningAbility;
        }

        /// <summary>DebugPanel 用的紧凑摘要。</summary>
        public string Compact()
        {
            return $"Ene {energy:F2} Soc {sociability:F2} Aff {affection:F2} Cur {curiosity:F2} " +
                   $"Cau {courage:F2} Imp {impulsiveness:F2} Obe {obedience:F2} Pla {playfulness:F2} " +
                   $"Ind {independence:F2} Sen {sensitivity:F2} Lrn {learningAbility:F2}";
        }

        private static float Jitter(System.Random rng, float variance)
        {
            return ((float)rng.NextDouble() * 2f - 1f) * variance;
        }

        private static float Clamp01(float v) => Mathf.Clamp01(v);
    }
}
