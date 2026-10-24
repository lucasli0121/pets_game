using UnityEngine;

namespace PetV1.Pet
{
/// <summary>
    /// 步态姿态稳定器 —— <b>当前未挂载，实测证明它修不了目标现象。</b>
    ///
    /// <para><b>为什么留着</b></para>
    /// 组件本身的实现是对的（本地空间测量断开正反馈、EMA 基线、限速），
    /// 参数扫描也确有效（Walk 躯干 pitch 摆幅 6.52° → 1.38°，消除 79%）。
    /// 只是它解决的不是用户看到的那个问题。留作参考。
    ///
    /// <para><b>实测结论：走路时「前腿高/屁股高」不是躯干俯仰</b></para>
    /// Dog_Walk 一个周期（1.0s，60fps 采样）的分解数据：
    /// <code>
    /// 骨盆 spine.004 的 Y          恒定 0.504m      ← 全身最高点，全程不起伏
    /// 躯干 spine.001→spine.003pitch  仅摆 6.5°
    /// 前蹄 front_foot.L 的 Y  0.156 ↔ 0.227m
    /// 后蹄 foot.L 的 Y         0.117 ↔ 0.190m
    /// 前后蹄高差    +0.115 ↔ -0.037m（往复 15cm）
    /// </code>
    /// 也就是说<b>身体是平的，只有四肢在反相摆动</b>。
    /// 这是标准对角步态（trot）：对角的两条腿同相落地，所以前腿抬起时后腿正在支撑，
    /// 两者高度必然反相。真狗走路也是这样。
    ///
    /// <para><b>因此</b></para>
    /// 摆幅来自四肢摆动幅度与画面取景的组合，不是姿态 bug。
    /// 真要压掉只能改动画曲线本身（会损失步态辨识度）或换素材，
    /// 已在 PetHomeSceneBuilder 里摘掉挂载。
    ///
    /// <para><b>参数扫描留档</b>（消除的 pitch 摆幅，halfLife × strength）</para>
    /// <code>
    ///1.5s × 0.80 → Walk 59%  Run 69%
    ///           1.5s × 0.95 → Walk 69%  Run 78%
    ///           3.0s × 0.95 → Walk 79%  Run 85%   ← 原定的默认值
    ///           6.0s × 0.95 → Walk 86%  Run 92%
    /// </code>
    /// halfLife 越长效果越好，但太长会让 Idle↔Walk 切换时基线跟不上，
    /// 本该保留的静态俯仰差会被当成起伏吃掉，所以原定 3.0s。
/// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]   // 不出现在 Add Component 菜单里，避免误挂
    public class PetGaitStabilizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("躯干中段起点骨骼（实测用 spine.001）。留空则按名字在子树上查找。")]
        [SerializeField] private string torsoFromBone = "spine.001";

        [Tooltip("躯干中段终点骨骼（实测用 spine.003）。留空则按名字在子树上查找。")]
        [SerializeField] private string torsoToBone = "spine.003";

        [Header("Compensation")]
[Tooltip("俯仰补偿强度 0~1。1 = 完全消除俯仰摆动。\n"
            + "实测（3 秒时基线 / Walk 摆幅 6.52°）：0.80 → 2.19°（消除66%）、"
            + "0.95 → 1.38°（79%）、1.00 → 1.12°（83%）。\n"
            + "取0.95 而不是 1.0：留5% 的自然起伏，避免走姿像溜冰。")]
        [Range(0f, 1f)] [SerializeField] private float pitchStrength = 0.95f;

        [Tooltip("侧倾补偿强度 0~1。狗走路时躯干会左右倒，幅度比俯仰小，"
                 + "留多一点比俯仰更能保住步态的辨识度。")]
   [Range(0f, 1f)] [SerializeField] private float rollStrength = 0.5f;

        [Tooltip("基线 EMA 时间常数（秒）。必须远大于步态周期（Walk 1s / Run 0.57s），"
        + "否则基线会跟住起伏、补偿变成反向放大。\n"
       + "但也不能太长：太长则 Idle↔Walk 切换时基线跟不上，"
       + "本该保留的静态俯仰差会被当成起伏吃掉。\n"
    + "实测跨度（Walk/Run）：1.5s → 59%/69%、3.0s → 79%/85%、6.0s → 86%/92%。"
          + "取 3.0s —— 最短行为时长约 3 秒，此时基线刚好能跟上一个完整行为周期。")]
  [SerializeField] private float baselineHalfLife = 3.0f;

        [Tooltip("基线初始化后的预热时间（秒）。这段时间内只跟随不补偿，" +
                 "否则开局那一下会有跳变。")]
        [SerializeField] private float warmupSeconds = 0.4f;

        [Tooltip("单帧最大补偿角（度）。防止状态切换时残差很大、一次性甩过去。")]
        [SerializeField] private float maxCorrectionPerFrame = 6f;

        private Transform _torsoFrom;
        private Transform _torsoTo;
        private float _baselinePitch;
        private float _baselineRoll;
        private float _warmupTimer;
        private bool _initialized;

        /// <summary>当前已施加的俯仰补偿（度，正值=抬起前端）。</summary>
        public float CurrentPitchCorrection { get; private set; }

        /// <summary>当前已施加的侧倾补偿（度）。</summary>
        public float CurrentRollCorrection { get; private set; }

        /// <summary>是否已完成基线初始化（false 时不做补偿）。</summary>
        public bool IsReady => _initialized;

        /// <summary>
        /// 显式绑定骨骼。由场景构建器在生成场景时调用，
        /// 避免运行期按名字遍历整棵骨骼树。
        /// </summary>
public void BindBones(Transform torsoFrom, Transform torsoTo)
      {
         // null 意味着「没查到」，不能覆盖 Awake 里已按名字解析好的引用
            if (torsoFrom != null) _torsoFrom = torsoFrom;
            if (torsoTo != null) _torsoTo = torsoTo;
            _initialized = false;
    _warmupTimer = 0f;
        }

        private void Awake()
        {
            if (_torsoFrom == null || _torsoTo == null) ResolveBones();
        }

        private void ResolveBones()
        {
            if (!string.IsNullOrEmpty(torsoFromBone))
                _torsoFrom = FindBone(transform, torsoFromBone);
            if (!string.IsNullOrEmpty(torsoToBone))
                _torsoTo = FindBone(transform, torsoToBone);
        }

        private static Transform FindBone(Transform root, string boneName)
        {
            if (root.name == boneName) return root;
            foreach (Transform child in root)
            {
                Transform found = FindBone(child, boneName);
                if (found != null) return found;
            }
            return null;
        }

        private void LateUpdate()
        {
            if (_torsoFrom == null || _torsoTo == null) return;

            // 本地空间测量：把躯干轴转到 rig 本地系，剔除本组件自身的旋转影响。
            // 局部 Z 指向狗的前方，局部 X 指向狗的右侧。
            Vector3 axis = transform.InverseTransformDirection(_torsoTo.position - _torsoFrom.position);
            if (axis.sqrMagnitude < 1e-6f) return;

            // pitch：绕本地 X（狗的横轴）。正值 = 头高于臀 = 前腿高。
            float pitch = Mathf.Atan2(axis.y, new Vector2(axis.x, axis.z).magnitude) * Mathf.Rad2Deg;
            // roll：绕本地 Z（狗的前后轴）。正值 = 右侧高于左侧。
            float roll = Mathf.Atan2(axis.x, axis.y) * Mathf.Rad2Deg;

            if (!_initialized)
            {
                // 预热期只跟随基线，不施加补偿，避免开局跳变
                _baselinePitch = pitch;
                _baselineRoll = roll;
                _warmupTimer += Time.deltaTime;
                if (_warmupTimer >= warmupSeconds) _initialized = true;
                return;
            }

            // EMA 系数：halfLife 秒内衰减到一半。
       // 注意 Mathf 没有 LN2 常量（只有 Log），这里用 0.693147 直接写死。
            float k = 1f - Mathf.Exp(-0.693147f * Time.deltaTime / Mathf.Max(0.01f, baselineHalfLife));
            _baselinePitch = Mathf.Lerp(_baselinePitch, pitch, k);
            _baselineRoll = Mathf.Lerp(_baselineRoll, roll, k);

            // 本地姿态与基线的残差即为要抵消的部分。
            // strength<1 时保留 (1-strength) 的自然摆动。
            float pitchCorrection = (_baselinePitch - pitch) * pitchStrength;
            float rollCorrection = (_baselineRoll - roll) * rollStrength;

            pitchCorrection = Mathf.Clamp(pitchCorrection, -maxCorrectionPerFrame, maxCorrectionPerFrame);
            rollCorrection = Mathf.Clamp(rollCorrection, -maxCorrectionPerFrame, maxCorrectionPerFrame);

// 用 *= 而不是赋值：PetController 在 Update 里写 transform.rotation，
   // 赋值会把这帧累积的补偿抹掉。
    //
    // 累积量天然有界：因为测量在本地空间进行（已扣除本组件自己的旋转），
 // 残差永远围绕基线，不会逐帧放大成筛子。
            transform.rotation = transform.rotation
      * Quaternion.Euler(0f, 0f, -rollCorrection)
        * Quaternion.Euler(pitchCorrection, 0f, 0f);

            CurrentPitchCorrection = pitchCorrection;
            CurrentRollCorrection = rollCorrection;
        }
    }
}