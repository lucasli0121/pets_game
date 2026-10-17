using System;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 性格配置资产。把数值从 MonoBehaviour 里挪出来（项目规范：关键数值不散落在组件中）。
    /// </summary>
    [CreateAssetMenu(menuName = "Pet V1/Pet Personality Profile", fileName = "PetPersonalityProfile")]
    public class PetPersonalityProfile : ScriptableObject
    {
        [Tooltip("品种 ID，仅用于日志与后续多品种扩展。")]
        public string breedId = "border_collie";

        [Tooltip("该品种的先天性格偏置。")]
        public PetPersonality basePersonality = PetPersonality.CreateBorderCollie();

        [Tooltip("个体差异幅度，0 表示同品种个体完全一致。")]
        [Range(0f, 0.3f)] public float individualVariance = 0.12f;

        public PetPersonality CreateIndividual(System.Random rng = null)
        {
            rng ??= new System.Random();
            PetPersonality result = basePersonality.Clone();
            if (individualVariance <= 0f) return result;
            result.energy = Mathf.Clamp01(result.energy + Jitter(rng));
            result.sociability = Mathf.Clamp01(result.sociability + Jitter(rng));
            result.affection = Mathf.Clamp01(result.affection + Jitter(rng));
            result.curiosity = Mathf.Clamp01(result.curiosity + Jitter(rng));
            result.courage = Mathf.Clamp01(result.courage + Jitter(rng));
            result.impulsiveness = Mathf.Clamp01(result.impulsiveness + Jitter(rng));
            result.obedience = Mathf.Clamp01(result.obedience + Jitter(rng));
            result.playfulness = Mathf.Clamp01(result.playfulness + Jitter(rng));
            result.independence = Mathf.Clamp01(result.independence + Jitter(rng));
            result.sensitivity = Mathf.Clamp01(result.sensitivity + Jitter(rng));
            result.learningAbility = Mathf.Clamp01(result.learningAbility + Jitter(rng));
            return result;
        }

        private float Jitter(System.Random rng)
        {
            return ((float)rng.NextDouble() * 2f - 1f) * individualVariance;
        }
    }
}
