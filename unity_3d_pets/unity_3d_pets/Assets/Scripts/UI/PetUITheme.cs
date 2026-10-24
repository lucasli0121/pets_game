using UnityEngine;

namespace PetV1.UI
{
    /// <summary>
    /// UI 的视觉常量。展板视觉规格的唯一来源。
    ///
    /// 为什么是常量而不是 Prefab 上的组件属性：
    /// 圆角、字号、间距这三类值会被十几个控件反复引用（卡片圆角、按钮圆角、
    /// Tab 圆角都是同一个值），做成 Prefab 属性就得每处手填，填歪了看不出来。
    /// 集中成常量后，「所有卡片的圆角应该一致」是语言层面保证的。
    ///
    /// 取值来自展板截图目视比对（不是设计规范文档 —— 展板本身没有标注色值）：
    /// 主蓝偏亮、卡片纯白、页面底色是极浅的冷蓝灰、圆角很大（玩具感）。
    /// 尺寸按 1080×1920 竖屏基准。
    ///
    /// 要改整体风格只改这里，不要在各个控件里写死数字。
    /// </summary>
    public static class PetUITheme
    {
        // ---------------------------------------------------------------
        // 配色
        // ---------------------------------------------------------------

        /// <summary>主蓝。展板的按钮、选中Tab、进度条都是这个色。</summary>
        public static readonly Color Primary = Hex(0x2F7FF0);

        /// <summary>主蓝的深色，按钮按下态。</summary>
        public static readonly Color PrimaryDark = Hex(0x2468CC);

        /// <summary>主蓝的浅色，进度条未填充段、Tab 未选中底。</summary>
        public static readonly Color PrimaryLight = Hex(0x8FBBF7);

        /// <summary>积分/金币色。展板商店页右上角那个金币图标就是这个偏橙的色。</summary>
        public static readonly Color Coin = Hex(0xF5A623);

        /// <summary>状态条-饱腹。展板是橙色。</summary>
        public static readonly Color SatietyBar = Hex(0xFF9A4D);

        /// <summary>状态条-快乐。展板是青绿。</summary>
        public static readonly Color HappinessBar = Hex(0x3FC1A5);

        /// <summary>文字主色。展板深蓝灰，不是纯黑 —— 纯黑在大面积白底上显得脏。</summary>
        public static readonly Color TextPrimary = Hex(0x2B3A4B);

        /// <summary>文字次要色。价格、数量、说明文字。</summary>
        public static readonly Color TextSecondary = Hex(0x7B8A9C);

        /// <summary>文字禁用色。买不起、等级不足时用。</summary>
        public static readonly Color TextDisabled = Hex(0xB8C2CC);

        /// <summary>
        /// 卡片底。按用户口径也是 80% 半透明白，不用实色 ——
        /// 弹窗浮在3D 场景上，实色卡片会像贴纸；半透才能看出底下的场景。
        /// </summary>
        public static readonly Color Card = new Color(1f, 1f, 1f, 0.80f);

        /// <summary>
        /// 卡片内层底（浮层里再套一层的那张）。比 <see cref="Card"/> 略淡，
        /// 靠透明度差两档做层次，而不是靠色相。
        /// </summary>
        public static readonly Color PageBackground = new Color(1f, 1f, 1f, 0.62f);

        /// <summary>
        /// 整屏页面底（食品/商店/场景三页的背景板）。必须是不透明的 ——
        /// 这三页要在3D 场景之上做列表，半透明会让狗和客厅从文字后面透出来。
        /// 所以它不走「半透白」这条线，单独给一个实色。
        /// </summary>
        public static readonly Color PageFill = Hex(0xF0F5FB);

        /// <summary>描边/分隔线。</summary>
        public static readonly Color Line = Hex(0xDDE6F0);

        /// <summary>已解锁场景的绿标。展板「使用中」徽标是绿色。</summary>
        public static readonly Color Success = Hex(0x2FBF71);

        /// <summary>半透明遮罩，弹窗背景。</summary>
        public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>
        /// 悬浮在 3D 场景之上的控件底色（顶栏胶囊、圆形图标按钮、底导衬底、
        /// 弹窗面板与其内层卡片）。
        ///
        /// 为什么不用纯白：宠物页底下是 3D 客厅背景图，白色方块会显得是「贴上去的 UI」。
        /// 半透明白 + 一点描边，既能和背景分层，又不破坏沉浸感。
        ///
        /// 80% 是用户定的口径：比原来 0.82 更透一点，让背景木纹透出来更明显。
        /// </summary>
        public static readonly Color Glass = new Color(1f, 1f, 1f, 0.80f);

        /// <summary>玻璃底上的描边，用来在浅色背景（草地、雪地）上勾出轮廓。</summary>
        public static readonly Color GlassEdge = new Color(1f, 1f, 1f, 0.55f);

        /// <summary>心形（亲密度/积分）色。展板右上角那个红心。</summary>
        public static readonly Color Heart = Hex(0xF2555A);

        /// <summary>经验条色。展板左上角那条黄色进度条。</summary>
        public static readonly Color ExpBar = Hex(0xFFC53D);

        // ---------------------------------------------------------------
        // 圆角
        // ---------------------------------------------------------------

        /// <summary>卡片圆角。展板是很大的圆角（约 24px @1080 宽）。</summary>
        public const float CardRadius = 24f;

        /// <summary>按钮圆角。展板是胶囊形，接近高度的一半。</summary>
        public const float ButtonRadius = 28f;

        /// <summary>Tab 圆角。</summary>
        public const float TabRadius = 22f;

        /// <summary>状态条圆角。细条，两端全圆。</summary>
        public const float BarRadius = 8f;

        // ---------------------------------------------------------------
        // 间距（1080×1920 基准）
        // ---------------------------------------------------------------

        /// <summary>页面左右边距。展板卡片都是这个左右留白。</summary>
        public const float PageMargin = 24f;

        /// <summary>卡片内边距。</summary>
        public const float CardPadding = 20f;

        /// <summary>卡片之间的纵向间距。</summary>
        public const float CardGap = 16f;

        // ---------------------------------------------------------------
        // 字号
        // ---------------------------------------------------------------

        public const int FontTitle = 40;      // 页面大标题「我的食品」
        public const int FontHeading = 32;    // 卡片标题、弹窗标题
        public const int FontBody = 28;       // 正文、列表项名称
        public const int FontCaption = 24;    // 说明、数量
        public const int FontSmall = 22;      // 标签、次要信息

        // ---------------------------------------------------------------
        // 控件高度
        // ---------------------------------------------------------------

        /// <summary>底部导航条高度。</summary>
        public const float NavBarHeight = 120f;

        /// <summary>顶部状态条区域高度。</summary>
        public const float TopBarHeight = 140f;

        /// <summary>主按钮高度。</summary>
        public const float PrimaryButtonHeight = 88f;

        /// <summary>Tab 高度。</summary>
        public const float TabHeight = 72f;

        /// <summary>状态条厚度。</summary>
        public const float StateBarHeight = 22f;

        // ---------------------------------------------------------------
        // 图标尺寸
        // ---------------------------------------------------------------

        /// <summary>顶栏圆形图标按钮直径。</summary>
        public const float TopIconButton = 76f;

        /// <summary>顶栏图标本身边长（比按钮小一圈，四周留白）。</summary>
        public const float TopIconGlyph = 40f;

        /// <summary>底导图标边长。</summary>
        public const float NavIcon = 48f;

        /// <summary>底导每格宽度。1080 / 4。</summary>
        public const float NavSlot = 270f;

        /// <summary>快捷入口圆形按钮直径（场景底部「食饱」）。</summary>
        public const float QuickActionDiameter = 116f;

        /// <summary>
        /// 安全边距。展板没有刘海，但真机有。
        /// 不留的话顶栏在 iPhone X 类机型上会被刘海压住。
        /// </summary>
        public static float SafeTop => 44f;
        public static float SafeBottom => 24f;

        /// <summary>
        /// 主按钮的禁用底色。用灰蓝而不是降低主蓝透明度 ——
        /// 半透明的主蓝叠在白卡片上仍然看得出是按钮，点了没反应会更困惑。
        /// </summary>
        public static readonly Color DisabledFill = Hex(0xC9D3DE);

        private static Color Hex(uint rgb)
        {
            return new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f,
                1f);
        }
    }
}
