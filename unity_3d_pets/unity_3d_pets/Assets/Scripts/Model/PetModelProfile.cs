using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Model
{
    /// <summary>
    /// 模型骨架描述。**换模型时唯一需要改的东西。**
    ///
    /// PRD 4.1 PET-04 要求「模型路径、动作能力与业务逻辑分离」，
    /// 这条需求在工程上就落在这个类：
    /// 触摸分区、动画映射、速度参数全部从这里读，业务代码不认识骨骼名。
    ///
    /// 为什么不用骨骼名直接分区：
    /// 现在用的 ithappy Animals FREE 的头骨叫 <c>scull</c>（拼写就是这样，不是 skull），
    /// 而已定的 Cartoon Dog - Border Collie 命名完全不同。
    /// 骨骼名一变，<c>FindDeep(rigRoot, "scull")</c> 返回 null，
    /// 头部 Collider 静默消失，触摸只剩 Body —— 这种失败不报错，最难查。
    ///
    /// 所以做成两级：
    /// ① <see cref="PetBoneNames"/> 配了骨骼名就用骨骼（精确，跟随骨骼动画）
    /// ② 没配或找不到，就退回 <see cref="PetRegionRatios"/> 的包围盒比例（永不失效）
    /// </summary>
    [CreateAssetMenu(fileName = "DogModelProfile",
                     menuName = "PetV1/模型描述（换模型改这里）")]
    public class PetModelProfile : ScriptableObject
    {
        // ------------------------------------------------------------------
        // 骨骼名映射
        // ------------------------------------------------------------------

        [Header("骨骼名（按当前模型的真实骨骼填；留空则该区域退回包围盒比例）")]
        [Tooltip("头骨名。当前免费模型是 scull（注意拼写），新模型需按实际填。")]
        public string headBone = "scull";

        [Tooltip("背骨名。留空则背部按比例分区。")]
        public string backBone = "spine.004";

        [Tooltip("尾巴骨名。留空则不单独分区。")]
        public string tailBone = "";

        [Tooltip("前腿骨名（可选）。留空则不单独分区。")]
        public string frontLegBone = "";

        // ------------------------------------------------------------------
        // 体型与速度（换模型后按实际重测）
        // ------------------------------------------------------------------

        [Header("体型（MCP 实测值，不要照抄写实尺寸）")]
        [Tooltip("模型身高（米），用于 Collider 半径换算与比例分区。")]
        public float bodyHeight = 0.76f;

        [Tooltip("模型宽度（米），最宽处。")]
        public float bodyWidth = 0.42f;

        [Tooltip("模型长度（米），鼻尖到尾根。")]
        public float bodyLength = 1.27f;

        [Header("移动速度（米/秒，新模型步幅不同需重测）")]
        [Tooltip("走。")]
        public float walkSpeed = 1.2f;

        [Tooltip("跑。")]
        public float runSpeed = 3.0f;

        [Tooltip("转向速度（度/秒）。太快会像漂移，太慢会显得迟钝。")]
        public float turnSpeed = 150f;

        // ------------------------------------------------------------------
        // 区域比例（骨骼缺失时的兜底，按包围盒归一化坐标）
        // ------------------------------------------------------------------

        [Header("区域比例（骨骼缺失时按包围盒切分，0~1）")]
        [Tooltip("头部：包围盒归一化中心 + 半径比例。\n" +
                 "注意 z=0.5 是身体中点，**大于 0.5 才靠头侧**。\n" +
                 "模型本地 +Z 是头侧（当前免费模型的 scull 在 +Z）。")]
        public PetRegionRatio head = new PetRegionRatio(new Vector3(0f, 0.82f, 0.82f), 0.30f);

        [Tooltip("背部：归一化中心 + 半径比例。z 接近 0.5（身体中段）。")]
        public PetRegionRatio back = new PetRegionRatio(new Vector3(0f, 0.62f, 0.52f), 0.34f);

        [Tooltip("尾部：归一化中心 + 半径比例。z 要明显小于 0.5（模型本地 -Z 是尾侧）。")]
        public PetRegionRatio tail = new PetRegionRatio(new Vector3(0f, 0.55f, 0.16f), 0.22f);

        [Header("活动范围（地面活动区，米）")]
        [Tooltip("X 方向半径。")]
        public float wanderHalfX = 1.15f;

        [Tooltip("Z 方向半径。")]
        public float wanderHalfZ = 1.0f;

        // ------------------------------------------------------------------

        /// <summary>
        /// 按骨骼名找 Transform，找不到返回 null（调用方需有兜底）。
        /// 找不到时不报警告 —— 没配骨骼名是合法的，走比例分区即可。
        /// </summary>
        public Transform FindBone(Transform rigRoot, string boneName)
        {
            if (rigRoot == null || string.IsNullOrWhiteSpace(boneName)) return null;
            return FindDeep(rigRoot, boneName);
        }

        /// <summary>把包围盒归一化坐标换算成世界坐标。</summary>
        public Vector3 RatioToWorld(Bounds worldBounds, Vector3 normalized)
        {
            return new Vector3(
                worldBounds.min.x + worldBounds.size.x * normalized.x,
                worldBounds.min.y + worldBounds.size.y * normalized.y,
                worldBounds.min.z + worldBounds.size.z * normalized.z);
        }

        /// <summary>按比例算区域半径：横向取宽度比例，纵向取高度比例，取较大者。</summary>
        public float RatioToRadius(Bounds worldBounds, float ratio)
        {
            float rx = worldBounds.size.x * ratio;
            float ry = worldBounds.size.y * ratio;
            return Mathf.Max(rx, ry);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void OnValidate()
        {
            if (bodyHeight <= 0f) bodyHeight = 0.76f;
            if (bodyWidth <= 0f) bodyWidth = 0.42f;
            if (bodyLength <= 0f) bodyLength = 1.27f;
            if (walkSpeed <= 0f) walkSpeed = 1.2f;
            if (runSpeed < walkSpeed) runSpeed = walkSpeed * 2.5f;
            if (turnSpeed <= 0f) turnSpeed = 150f;
            if (wanderHalfX <= 0f) wanderHalfX = 1.15f;
            if (wanderHalfZ <= 0f) wanderHalfZ = 1.0f;
        }

        /// <summary>
        /// 内置兜底。等价于当前免费模型的实测值。
        /// 连 asset 都没配时用它，保证换模型时不至于直接崩。
        /// </summary>
        public static PetModelProfile Fallback()
        {
            var p = CreateInstance<PetModelProfile>();
            p.name = "PetModelProfile(Fallback)";
            return p;
        }
    }

    /// <summary>
    /// 区域在模型包围盒里的归一化位置 + 半径比例。
    /// 换骨骼命名时靠它保证触摸分区仍然可用。
    /// </summary>
    [Serializable]
    public struct PetRegionRatio
    {
        [Tooltip("包围盒归一化中心，0=最小角 1=最大角。")]
        public Vector3 center;

        [Tooltip("半径比例（相对包围盒宽/高）。")]
        public float radius;

        public PetRegionRatio(Vector3 center, float radius)
        {
            this.center = center;
            this.radius = radius;
        }
    }
}