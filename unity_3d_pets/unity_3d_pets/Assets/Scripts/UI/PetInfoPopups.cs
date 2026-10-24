using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 宠物信息浮层的公共骨架：遮罩 + 挡板 + 关闭按钮 + 标题，
    /// 以及状态/属性进度条行的统一排版。
    ///
    /// V1 只有一个浮层（<see cref="PetStatusPopup"/>，顶栏齿轮点开）。
    /// 骨架仍然抽象出来而不是直接写死在那个类里：食品详情弹窗
    /// （<c>FoodDetailPopup</c>，在 <c>PetPopups.cs</c>）用的是另一套，
    /// 等它也要复用这套行排版时就不用再抽一次。
    ///
    /// **这里曾经有个等级浮层，已经删掉**：等级胶囊与常驻经验条
    /// 直接显示在顶栏左半（用户口径），不需要再点开。
    /// </summary>
    public abstract class PetPopupBase
    {
        protected PetHomeUI _ui;
        protected Image _root;
        protected RectTransform _panel;

        private PetHomeUI _pendingUi;

        /// <summary>子类实现：往面板里加自己的控件。</summary>
        protected abstract void BuildContent();

        protected abstract string Title { get; }
        protected abstract Vector2 PanelSize { get; }

        public void Build(Transform parent, PetHomeUI ui)
        {
            _pendingUi = ui;

            // 遮罩：**模态** —— 点屏幕其他位置不关闭，只能点右上角叉号
            // （用户口径：设置对话框要做成模态）。
            // 用 NewSolid(radius:0) 建完必须显式开 raycastTarget——
            // 那个工厂在 radius==0 时默认关射线（绝大多数方形是纯装饰）。
            // raycastTarget 保留：遮罩要吃掉点击，避免穿透到下面的页面/3D 场景交互。
            // 遮罩名带类名后缀：两个浮层都建遮罩，都叫 "Mask" 的话
            // transform.Find("Mask") 只会返回第一个，按名字找就会找错浮层。
            _root = PetUIFactory.NewSolid(GetType().Name + "Mask", parent, PetUITheme.Scrim, 0f);
            _root.raycastTarget = true;
            PetUIFactory.Fill((RectTransform)_root.transform);
            _root.gameObject.SetActive(false);

            var panel = PetUIFactory.NewSolid("Panel", _root.transform, PetUITheme.Card,
                                               PetUITheme.CardRadius);
            _panel = (RectTransform)panel.transform;
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.sizeDelta = PanelSize;
            _panel.anchoredPosition = Vector2.zero;

            // 面板本身要吃掉点击，否则点进度条等于点遮罩关闭。
            // 注意这个挡板是面板的第一个子节点 —— 「面板里第一个 Button」
            // 拿到的是它而不是关闭按钮，按名字或 Label 筛，别按顺序取。
            var blocker = PetUIFactory.NewHitArea("Block", _panel);
            PetUIFactory.Fill((RectTransform)blocker.transform);
            blocker.gameObject.AddComponent<Button>();

            // 关闭按钮用叉号图标，不再用「×」文字。
            var close = PetUIFactory.NewIconButton(_panel, "close", 64f,
                                                    PetUITheme.Line, out var closeIcon);
            closeIcon.color = PetUITheme.TextSecondary;
            var crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-24f, -24f);
            close.onClick.AddListener(Hide);

            var head = PetUIFactory.NewText("Title", _panel, Title, PetUITheme.FontHeading,
                                             PetUITheme.TextPrimary, TextAnchor.UpperLeft);
            PetUIFactory.TopLeft((RectTransform)head.transform,
                                 new Vector2(280f, 44f), new Vector2(40f, 36f));

            _ui = _pendingUi;
            BuildContent();
        }

        public void Show()
        {
            if (_root == null) return;
            Refresh();
            _root.gameObject.SetActive(true);
        }

        public abstract void Refresh();

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        // ---------------------------------------------------------------
        // 进度条行（子类共用）
        // ---------------------------------------------------------------

        /// <summary>
        /// 一行：左标题、右数值、中间进度条。
        /// <paramref name="topOffset"/> 是距卡片顶部的距离（**正值向下**，
        /// 内部转成 <see cref="PetUIFactory.Box"/> 要的负偏移）。
        /// 返回填充条 Image 供 Refresh 改 fillAmount。
        /// </summary>
        protected static Image BuildBarRow(Transform parent, string label, Color color,
                                           float topOffset, out Text valueText)
        {
            const float RowH = 48f;
            var row = PetUIFactory.NewPanel("Bar", parent);
            var rrt = (RectTransform)row.transform;
            // **宽度必须横向拉伸**：写anchorMin=anchorMax=(0,1) + sizeDelta.x=0
            // 的话行宽恒为 0，标题被挤成竖排一列、进度条 Track 宽度也是 0
            // （Image 宽度 0 = 完全看不见）。这类问题只在实际渲染时暴露，
            // 验 fillAmount 数值是查不出来的。
            PetUIFactory.Box(rrt,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -(topOffset + RowH)), new Vector2(-24f, -topOffset));

            var nameTx = PetUIFactory.NewText("Name", rrt, label, PetUITheme.FontBody,
                                              PetUITheme.TextPrimary, TextAnchor.MiddleLeft);
            PetUIFactory.Box((RectTransform)nameTx.transform,
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(150f, 0f));

            valueText = PetUIFactory.NewText("Value", rrt, "0", PetUITheme.FontBody,
                                              PetUITheme.TextPrimary, TextAnchor.MiddleRight);
            PetUIFactory.Box((RectTransform)valueText.transform,
                new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(-96f, 0f), new Vector2(0f, 0f));

            // NewProgressBar 返回 Fill，out 给 Track；要摆尺寸的是 Track。
            var fill = PetUIFactory.NewProgressBar(rrt, color, out var track);
            PetUIFactory.Box((RectTransform)track.transform,
                new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(160f, -11f), new Vector2(-104f, 11f));

            return fill;
        }

        /// <summary>
        /// 状态文案映射。
        /// 后台的 state 码是配置驱动的（<c>config_state_threshold</c> 定义阈值与文案），
        /// 这里只兜住已知码；遇到不认识的统一显示「状态良好」而不是留空——
        /// 空白会让玩家以为功能没做完。
        /// </summary>
        protected static string StateLabel(string code)
        {
            switch (code)
            {
                case "hungry": return "有点饿";
                case "exhausted": return "很疲惫";
                case "sad": return "有点难过";
                case "depressed": return "很沮丧";
                default: return "状态良好";
            }
        }
    }

    // ==================================================================
    /// <summary>
    /// 状态与属性浮层。顶栏右上角的齿轮点开。
    ///
    /// 分两段：
    /// - **状态**：饱腹、快乐两条。后台 <c>decay.go</c> 只结算这两项；
    ///   展板画了「亲密」「精力」但后台没有对应字段，不编。
    /// - **属性**：性格四维（活泼 / 好奇 / 亲人 / 独立），各 0~10。
    ///   后台 <c>domain.Personality</c> 就是这四个字段，V1 只读
    ///   （修改走 <c>pet/personality</c>，要扣积分且有 24 小时冷却）。
    /// </summary>
    public class PetStatusPopup : PetPopupBase
    {
        private Text _stateHint;
        private Image _satietyFill;
        private Image _happinessFill;
        private Text _satietyValue;
        private Text _happinessValue;
        private Image _livelyFill;
        private Image _curiosityFill;
        private Image _affectionFill;
        private Image _independenceFill;
        private Text _livelyValue;
        private Text _curiosityValue;
        private Text _affectionValue;
        private Text _independenceValue;

        protected override string Title => "状态与属性";

        // 高度按内容定：属性卡底 -706 + 底部留白 ≈ 760。
        // 原先底部有 note 文字区（内容已删），面板随之收矮，不留大片空白。
        protected override Vector2 PanelSize => new Vector2(880f, 760f);

        protected override void BuildContent()
        {
            // ---- 状态段 ----
            var stateTitle = PetUIFactory.NewText("StateTitle", _panel, "状态",
                                                   PetUITheme.FontBody, PetUITheme.TextPrimary,
                                                   TextAnchor.UpperLeft);
            PetUIFactory.TopLeft((RectTransform)stateTitle.transform,
                                 new Vector2(120f, 32f), new Vector2(40f, 96f));

            var stateCard = PetUIFactory.NewSolid("States", _panel, PetUITheme.PageBackground,
                                                   PetUITheme.CardRadius);
            // 高度按内容定：提示行 ~44 + 两条进度行 (78..182) + 上下留白 ≈ 240。
            // 之前给到 444，条下面空一大片，看起来像内容没加载出来。
            PetUIFactory.Box((RectTransform)stateCard.transform,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(40f, -376f), new Vector2(-40f, -136f));
            var scrt = (RectTransform)stateCard.transform;

            _stateHint = PetUIFactory.NewText("Hint", scrt, "", PetUITheme.FontCaption,
                                              PetUITheme.TextSecondary, TextAnchor.UpperLeft);
            PetUIFactory.Box((RectTransform)_stateHint.transform,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -62f), new Vector2(-24f, -18f));

            _satietyFill = BuildBarRow(scrt, "饱腹", PetUITheme.SatietyBar, 78f,
                                        out _satietyValue);
            _happinessFill = BuildBarRow(scrt, "快乐", PetUITheme.HappinessBar, 134f,
                                          out _happinessValue);

            // ---- 属性段 ----
            var attrTitle = PetUIFactory.NewText("AttrTitle", _panel, "属性（性格）",
                                                   PetUITheme.FontBody, PetUITheme.TextPrimary,
                                                   TextAnchor.UpperLeft);
            PetUIFactory.TopLeft((RectTransform)attrTitle.transform,
                                 new Vector2(240f, 32f), new Vector2(40f, 404f));

            var attrCard = PetUIFactory.NewSolid("Attrs", _panel, PetUITheme.PageBackground,
                                                  PetUITheme.CardRadius);
            // 原先卡里有段头（星形 + 「性格倾向（0–10）」），已按用户口径删除，
            // 卡片高度随之收紧：四条行 (22..238) + 上下留白 ≈ 262。
            PetUIFactory.Box((RectTransform)attrCard.transform,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(40f, -706f), new Vector2(-40f, -444f));
            var art = (RectTransform)attrCard.transform;

            // 四维同一色系、明度递减 —— 看得出是一组，又能逐条区分
            _livelyFill = BuildBarRow(art, "活泼", AttrColor(0), 22f, out _livelyValue);
            _curiosityFill = BuildBarRow(art, "好奇", AttrColor(1), 78f, out _curiosityValue);
            _affectionFill = BuildBarRow(art, "亲人", AttrColor(2), 134f, out _affectionValue);
            _independenceFill = BuildBarRow(art, "独立", AttrColor(3), 190f,
                                             out _independenceValue);
        }
        /// <summary>四维配色：同一色相、明度递减。</summary>
        private static Color AttrColor(int index)
        {
            var c = PetUITheme.PrimaryLight;
            float[] mul = { 1.00f, 0.85f, 0.70f, 0.55f };
            return new Color(c.r * mul[index], c.g * mul[index], c.b * mul[index], 1f);
        }

        public override void Refresh()
        {
            if (_stateHint == null) return;
            var s = _ui.State;

            _stateHint.text = $"饱腹：{StateLabel(s.satietyState)}　快乐：{StateLabel(s.happinessState)}";

            _satietyFill.fillAmount = s.SatietyPercent / 100f;
            _satietyValue.text = s.SatietyPercent.ToString();
            _happinessFill.fillAmount = s.HappinessPercent / 100f;
            _happinessValue.text = s.HappinessPercent.ToString();

            // 性格四维是 0~10，进度条要除以 10
            SetAttr(_livelyFill, _livelyValue, s.lively);
            SetAttr(_curiosityFill, _curiosityValue, s.curiosity);
            SetAttr(_affectionFill, _affectionValue, s.affection);
            SetAttr(_independenceFill, _independenceValue, s.independence);
        }

        private static void SetAttr(Image fill, Text value, int raw)
        {
            fill.fillAmount = Mathf.Clamp01(raw / 10f);
            value.text = $"{raw}/10";
        }
    }
}
