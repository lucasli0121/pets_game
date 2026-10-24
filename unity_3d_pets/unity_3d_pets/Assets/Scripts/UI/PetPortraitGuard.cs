using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 横竖屏保护：把 UI 内容锁在居中的 9:16 框里，与相机的竖屏裁切对齐。
    ///
    /// 解决的问题（编辑器横屏 2560×1440 下实测）：
    /// <c>CanvasScaler</c> 用 <c>matchWidthOrHeight=0</c>（全按宽度）时，
    /// 画布本地尺寸会变成 1080×608 —— 比例 1.778，而设计是 0.563。
    /// 于是顶栏 140 + 底导 120 + 标题偏移 90 这套按 1920 排的常量
    /// 全挤在 608 高里，字叠字、页面内容互相压。
    /// 相机那边 <c>PetPortraitViewport</c> 已经把 3D 画面裁成居中 9:16、两侧留黑边，
    /// UI 却铺满全宽 —— 两边框不一致，任何「按全屏宽度算」的布局都是错的。
    ///
    /// 做法分两步，都必须做：
    /// ① <c>matchWidthOrHeight</c> 按屏幕比例动态切：比 9:16 更宽（含横屏）时
    ///    改成按高度匹配（本地高度恒为 1920），比 9:16 更高（长屏 20:9）时改成按宽度匹配。
    ///    这样画布本地尺寸在两个方向上都不小于 1080×1920。
    /// ② 内容不挂在画布根上，而是挂进居中的 <c>SafeFrame</c>（恒为 1080×1920）——
    ///    画布根的 rect 由 CanvasScaler 每帧覆写，不能直接改它的 sizeDelta。
    ///
    /// 结果：横屏时左右是黑边（与 3D 画面的黑边重合），UI 仍是竖屏版式；
    /// 竖屏与长屏时 SafeFrame 与画布等宽，上下多余的空间按顶栏/底导的锚点自然分配。
    ///
    /// 每帧重算是必要的：编辑器里拖 Game 窗口、手机上转屏都会改尺寸。
    /// 每次框真的变了才回调一次，让页面重排 —— 布局系统本来不会为隐藏页跑布局。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasScaler))]
    public class PetPortraitGuard : MonoBehaviour
    {
        public const float DesignWidth = 1080f;
        public const float DesignHeight = 1920f;
        public const float DesignAspect = DesignWidth / DesignHeight;

        /// <summary>尺寸真的变了才触发（单位：画布本地单位）。</summary>
        public event System.Action FrameResized;

        [Tooltip("画布缩放器。留空则取同物体上的 CanvasScaler。")]
        [SerializeField] private CanvasScaler scaler;

        [Tooltip("竖屏内容框。留空则按名字 SafeFrame 找。")]
        [SerializeField] private RectTransform frame;

        [Tooltip("与相机裁切框不一致时打一条警告（编辑器里排查用）。")]
        [SerializeField] private bool verifyCameraAlignment = true;

        private Vector2 lastFrameSize;
        private bool warned;

        public RectTransform Frame => frame;

        public void Configure(CanvasScaler uiScaler, RectTransform contentFrame)
        {
            scaler = uiScaler != null ? uiScaler : GetComponent<CanvasScaler>();
            frame = contentFrame;
            Apply(force: true);
        }

        private void Awake()
        {
            if (scaler == null) scaler = GetComponent<CanvasScaler>();
            if (frame == null)
            {
                var found = transform.Find("SafeFrame") as RectTransform;
                if (found != null) frame = found;
            }
            Apply(force: true);
        }

        private void OnEnable() => Apply(force: true);

        private void Update() => Apply(force: false);

        /// <summary>按当前屏幕尺寸摆好缩放方式与内容框。</summary>
        public void Apply(bool force)
        {
            if (scaler == null || frame == null) return;

            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0) return;

            float screenAspect = (float)w / h;

            // ① 缩放方式：宽屏按高、窄长屏按宽，保证画布本地尺寸容得下设计框
            float match = screenAspect >= DesignAspect ? 1f : 0f;
            if (!Mathf.Approximately(scaler.matchWidthOrHeight, match))
                scaler.matchWidthOrHeight = match;

            // ② 内容框：把设计比例的框等比缩到刚好放进画布，居中
            var parent = frame.parent as RectTransform;
            Vector2 canvas = parent != null ? parent.rect.size : new Vector2(DesignWidth, DesignHeight);
            if (canvas.x <= 0f || canvas.y <= 0f) return;

            float fit = Mathf.Min(canvas.x / DesignWidth, canvas.y / DesignHeight);
            var size = new Vector2(DesignWidth * fit, DesignHeight * fit);

            if (force || frame.sizeDelta != size)
            {
                frame.anchorMin = new Vector2(0.5f, 0.5f);
                frame.anchorMax = new Vector2(0.5f, 0.5f);
                frame.pivot = new Vector2(0.5f, 0.5f);
                frame.sizeDelta = size;
                frame.anchoredPosition = Vector2.zero;

                if (!force && size != lastFrameSize) FrameResized?.Invoke();
                lastFrameSize = size;
            }

            if (verifyCameraAlignment) VerifyCamera(screenAspect);
        }

        /// <summary>
        /// UI 框与相机裁切框必须是同一个矩形。
        /// 不一致的后果：UI 顶栏盖在 3D 画面外，黑边里浮着一排按钮。
        /// 这里只报警不自行改相机 —— 相机的 rect 归 <c>PetPortraitViewport</c> 管，
        /// 两边都写会出现互相抢写。
        ///
        /// 只在 Play 模式下比对 rect：编辑模式下 PetPortraitViewport 的 Apply()
        /// 不跑（没有 ExecuteAlways），cam.rect 恒为全屏，比对必然报错。
        /// 编辑期改成检查组件在不在、勾没勾。
        /// </summary>
        private void VerifyCamera(float screenAspect)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var viewport = cam.GetComponent<PetV1.Pet.PetPortraitViewport>();

            if (!Application.isPlaying)
            {
                if (viewport == null || viewport.TargetAspect <= 0f)
                {
                    WarnOnce("Main Camera 上没有启用 PetPortraitViewport。" +
                             "构建器会挂上，手搭场景时容易漏 —— 后果是横屏下 3D 画面不裁切，" +
                             "与 UI 的黑边对不上。");
                }
                return;
            }

            var expect = new Rect(0f, 0f, 1f, 1f);
            if (screenAspect > DesignAspect)
            {
                expect.width = DesignAspect / screenAspect;
                expect.x = (1f - expect.width) * 0.5f;
            }
            else if (screenAspect < DesignAspect)
            {
                expect.height = screenAspect / DesignAspect;
                expect.y = (1f - expect.height) * 0.5f;
            }

            if (Mathf.Abs(cam.rect.x - expect.x) < 0.002f &&
                Mathf.Abs(cam.rect.y - expect.y) < 0.002f &&
                Mathf.Abs(cam.rect.width - expect.width) < 0.002f &&
                Mathf.Abs(cam.rect.height - expect.height) < 0.002f) return;

            WarnOnce($"UI 框与相机裁切框不一致：cam.rect={cam.rect} 期望={expect}。" +
                      "画面会出现黑边里浮 UI。检查 Main Camera 上的 PetPortraitViewport 是否启用。");
        }

        private void WarnOnce(string msg)
        {
            if (warned) return;
            warned = true;
            Debug.LogWarning("[PetV1] " + msg);
        }
    }
}