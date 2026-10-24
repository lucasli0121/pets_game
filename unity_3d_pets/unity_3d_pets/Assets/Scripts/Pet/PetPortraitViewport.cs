using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 竖屏取景裁切（PRD 4.5：竖屏 1080×1920）。
    ///
    /// 为什么需要它：相机是正交的，可视高度固定，于是**可视宽度随窗口比例变化**。
    /// 编辑器 Game 窗口是 16:9 时可视宽 6.76m，而背景图只有 2.14m 宽（9:16），
    /// 左右各空出 2.3m —— 表现就是「背景好小」。这不是背景缩错了，
    /// 而是宽屏窗口下裁切区域本身比背景宽。
    ///
    /// 做法：把相机的渲染 rect 限制成居中的 9:16。
    /// 宽屏窗口里两侧留黑边（与真机一致），竖屏设备下rect 铺满整屏、不留边。
    ///
    /// 为什么放在运行时而不是改相机投影：
    /// 改 orthographicSize 会连带改变背景尺寸与地面线，两个都得重算；
    /// 改rect 只影响渲染区域，取景参数全部不动。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class PetPortraitViewport : MonoBehaviour
    {
        [Tooltip("目标画面比例（宽/高）。竖屏小游戏是 9/16 = 0.5625。\n" +
                 "填 0 表示关闭裁切，按窗口原始比例渲染。")]
        [SerializeField] private float _targetAspect = 9f / 16f;

        [Tooltip("勾掉可在编辑器里临时看宽屏全貌（背景左右会留白）。")]
        [SerializeField] private bool _enabled = true;

        private Camera _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            Apply();
        }

        private void Update()
        {
            Apply();
        }

        /// <summary>
        /// 窗口尺寸在编辑器里可以被拖动，Play 模式下也会变，
        /// 所以每帧重算是必要的。计算只有几次乘除，开销可忽略。
        /// </summary>
        private void Apply()
        {
            if (_cam == null) return;

            if (!_enabled || _targetAspect <= 0f)
            {
                if (_cam.rect != new Rect(0f, 0f, 1f, 1f)) _cam.rect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0) return;

            float screenAspect = (float)w / h;
            Rect r = new Rect(0f, 0f, 1f, 1f);

            if (screenAspect > _targetAspect)
            {
                // 窗口比目标更宽：占满高度，左右留边
                r.width = _targetAspect / screenAspect;
                r.x = (1f - r.width) * 0.5f;
            }
            else if (screenAspect < _targetAspect)
            {
                // 窗口比目标更窄（少见）：占满宽度，上下留边
                r.height = screenAspect / _targetAspect;
                r.y = (1f - r.height) * 0.5f;
            }

            if (_cam.rect != r) _cam.rect = r;
        }

        /// <summary>供构建器/校验脚本使用：当前生效的目标比例，0 表示未启用。</summary>
        public float TargetAspect => _enabled ? _targetAspect : 0f;
    }
}
