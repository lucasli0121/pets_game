using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 页面基类。所有页面都是**纯代码构建的普通类**，不是 MonoBehaviour。
    ///
    /// 为什么不做成 MonoBehaviour：页面自己没有 Update/LateUpdate 逻辑
    /// （只有状态变化时重绘一次），做成 MonoBehaviour 只会带来生命周期顺序问题 ——
    /// 比如 PetSceneStage.Start 先跑还是页面 Awake 先跑，会不会拿到还没建好的引用。
    /// 纯类的生命周期由 <see cref="PetHomeUI"/> 显式控制，顺序写在代码里而不是靠 Unity 猜。
    ///
    /// 需要挂脚本的只有「根 GameObject 上有一个用于显示/隐藏的 RectTransform」，
    /// 那部分由 Root 提供。
    /// </summary>
    public abstract class PetPageBase
    {
        /// <summary>页面根节点。切换页签时只改它的 activeSelf。</summary>
        public GameObject Root { get; protected set; }

        /// <summary>页面标题。展板上的居中标题，如「我的食品」。</summary>
        protected Text TitleText;

        /// <summary>页面内容容器（标题以下的区域）。</summary>
        protected RectTransform Content;

        public abstract void Build(Transform parent, PetHomeUI ui);

        /// <summary>数据变化后重绘。实现里只更新文本与显隐，不重建节点。</summary>
        public abstract void Refresh();

        /// <summary>
        /// 建标题 + 内容区。所有页面布局一致，抽到基类。
        ///
        /// 纵向三段：顶部留白给顶栏 → 标题 → 内容（可滚动）。
        /// 内容区固定减去底导高度，否则最后一张卡片会被导航条盖住。
        /// </summary>
        protected void BuildShell(string title, Transform parent)
        {
            Root = PetUIFactory.NewPanel(GetType().Name, parent);

            float top = PetUITheme.TopBarHeight;
            float bottom = PetUITheme.NavBarHeight + PetUITheme.SafeBottom;

            // 标题
            TitleText = PetUIFactory.NewText("Title", Root.transform, title,
                                             PetUITheme.FontTitle, PetUITheme.TextPrimary,
                                             TextAnchor.UpperCenter);
            var trt = (RectTransform)TitleText.transform;
            trt.anchorMin = new Vector2(0f, 1f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(PetUITheme.PageMargin, -top - 90f);
            trt.offsetMax = new Vector2(-PetUITheme.PageMargin, -top - 10f);

            // 内容容器
            var content = PetUIFactory.NewPanel("Content", Root.transform);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.one;
            crt.offsetMin = new Vector2(PetUITheme.PageMargin, bottom + 12f);
            crt.offsetMax = new Vector2(-PetUITheme.PageMargin, -top - 100f);
            Content = crt;
        }

        /// <summary>
        /// 给内容容器套上滚动视图与自适应高度。
        /// <paramref name="topInset"/> 是顶部避让：页面在 Content 顶上还放了东西
        /// （Tab 行）时，滚动区要从那个高度下面开始，否则两个叠在一起。
        ///
        /// uGUI 的滚动有两个坑：
        /// ① ScrollRect 要 ScrollRect + Viewport + Content 三层，Viewport 要 Mask 才能裁剪
        /// ② Content 必须挂 ContentSizeFitter(PreferredSize) 才能随子项增长，
        ///    少一个就表现为「页面只有第一屏能看，往下滑是空的」
        /// </summary>
        protected VerticalLayoutGroup MakeScrollable(float topInset = 0f)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform));
            scrollGo.layer = LayerMask.NameToLayer("UI");
            var srt = (RectTransform)scrollGo.transform;
            srt.SetParent(Content, false);
            srt.anchorMin = Vector2.zero;
            srt.anchorMax = Vector2.one;
            srt.offsetMin = Vector2.zero;
            srt.offsetMax = new Vector2(0f, -topInset);

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            // Viewport
            var viewport = PetUIFactory.NewPanel("Viewport", scrollGo.transform);
            var vrt = (RectTransform)viewport.transform;
            PetUIFactory.Fill(vrt);
            var mask = viewport.AddComponent<Image>();
            mask.sprite = PetUIFactory.WhiteSprite;
            // alpha 必须是 1，不能图省事写 0.001「几乎透明」。
            // Mask 的模板是这张图自己画出来的：alpha 转成字节后为 0 时
            // CanvasRenderer 整条绘制被跳过，模板缓冲一个字都没写，
            // 于是所有子项都过不了 NotEqual 测试 —— 表现是滚动区一片空白，
            // 而 rect、文本、fillAmount 全部量得到，Console 零报错。
            // showMaskGraphic=false 已经让这张图只写模板、不上屏，不需要靠 alpha 藏。
            mask.color = Color.white;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vrt;

            // Content：撑满 viewport 的宽度，高度自适应
            var contentGo = PetUIFactory.NewPanel("Inner", viewport.transform);
            var irt = (RectTransform)contentGo.transform;
            irt.anchorMin = new Vector2(0f, 1f);
            irt.anchorMax = new Vector2(1f, 1f);
            irt.pivot = new Vector2(0.5f, 1f);
            irt.offsetMin = Vector2.zero;
            irt.offsetMax = Vector2.zero;

            var list = irt.gameObject.AddComponent<VerticalLayoutGroup>();
            list.spacing = PetUITheme.CardGap;
            list.padding = new RectOffset(0, 0, 0, 24);
            list.childAlignment = TextAnchor.UpperLeft;
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;

            var fitter = irt.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = irt;
            return list;
        }

        /// <summary>
        /// 不滚动的竖排容器（成长页用）。
        ///
        /// **不要把宠物页也做成可滚动的**：滚动视图为了能拖动，
        /// Viewport 上挂的 Mask 必须 raycastTarget = true（否则滚不动）。
        /// 那一层 Mask 会铺满整个内容区，把宠物页压在它下面的
        /// 3D 场景触摸全部吃掉 —— 表现为「点狗没反应」，看起来像手势功能坏了。
        ///
        /// 成长页是固定六块内容，不超出一屏，本来就不需要滚。
        /// 真要加内容（比如性格面板展开）也是加按钮触发弹窗，不是加长列表。
        /// </summary>
        protected VerticalLayoutGroup MakeStaticList()
        {
            var go = PetUIFactory.NewPanel("Content", Content);
            var rt = (RectTransform)go.transform;
            PetUIFactory.Fill(rt);

            var list = go.AddComponent<VerticalLayoutGroup>();
            list.spacing = PetUITheme.CardGap;
            list.padding = new RectOffset(0, 0, 0, 0);
            list.childAlignment = TextAnchor.UpperLeft;
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;
            return list;
        }

        /// <summary>
        /// 列表重建后立即跑一次布局。
        ///
        /// uGUI 的布局在**帧末**统一重建。列表清空重建的那一帧，
        /// 新建的行 rect 高度还是 0（父容器高度未定），
        /// 此刻做射线检测会一个都命不中 ——
        /// 症状是「刚切到食品页，第一帧点列表没反应，再点就好了」；
        /// 用 rect 尺寸做自检则会读到 -20 这种反直觉值，以为是算错了。
        ///
        /// 所以凡是「重建完立刻要用」的场景（点击测试、截图取值），
        /// 都要显式 <see cref="LayoutRebuilder.ForceRebuildLayoutImmediate"/>。
        /// </summary>
        protected static void RelayoutNow(RectTransform listRoot)
        {
            if (listRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(listRoot);
        }

        /// <summary>
        /// 自顶向下把一棵子树里所有布局节点重排一遍。
        ///
        /// 为什么<see cref="LayoutRebuilder.ForceRebuildLayoutImmediate"/> 不够：
        /// 它以传入的 rect 为 root，而 **ContentSizeFitter 与 VerticalLayoutGroup
        /// 挂在同一个物体（Scroll/Inner）上时，从祖先节点发起的重建到这一层就停了** ——
        /// 实测从 FoodPage 根force 完，5 行的高度全是 -20；
        /// 直接 force Scroll/Inner 才是 140。
        /// 症状是「页面能显示、但整页点不动」，因为行高为负、射线一个都命不中。
        ///
        /// 顺序必须父先子后：父节点（Content/Inner）的尺寸是子节点布局的前提，
        /// 反过来排会算出0 高度的行。
        /// </summary>
        public static void RelayoutTree(RectTransform root)
        {
            if (root == null) return;

            bool self = false;
            var groups = root.GetComponents<LayoutGroup>();
            if (groups != null && groups.Length > 0) self = true;
            if (!self && root.GetComponent<ContentSizeFitter>() != null) self = true;
            if (!self && root.GetComponent<LayoutElement>() != null) self = true;

            if (self) LayoutRebuilder.ForceRebuildLayoutImmediate(root);

            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i) as RectTransform;
                if (child != null) RelayoutTree(child);
            }
        }

        /// <summary>
        /// 分类 Tab 行（全部 / 主食 / 零食 / 特殊）。展板的胶囊形分段控件。
        ///
        /// 选中态用主蓝底 + 白字，未选中用浅蓝底 + 深字 ——
        /// 浅蓝底而不是透明，是为了让「这里有一组可切换的东西」这件事在视觉上成立。
        ///
        /// **锚点必须是上下都拉到 1 的顶部通栏**：早先只写 sizeDelta=(0, TabHeight)
        /// 而 anchor 用默认的 (0.5,0.5) —— 那意味着「宽 0 高 72、居中在 Content 里」，
        /// HorizontalLayoutGroup 从 0 宽的父容器里分宽度，三个 Tab 全是 0 宽：
        /// 看不见也点不到，而且 Console 零报错（实测 Tab0/1/2 的 rect.width 全是 0.0）。
        /// </summary>
        protected static HorizontalLayoutGroup MakeTabRow(Transform parent, string[] labels,
                                                          System.Func<int, bool> isOn,
                                                          System.Action<int> onClick)
        {
            var go = new GameObject("Tabs", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -PetUITheme.TabHeight);
            rt.offsetMax = Vector2.zero;

            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 12f;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = true;

            for (int i = 0; i < labels.Length; i++)
            {
                int captured = i;
                bool on = isOn(i);
                var fill = PetUIFactory.NewSolid("Tab", go.transform,
                                                 on ? PetUITheme.Primary : PetUITheme.PrimaryLight,
                                                 PetUITheme.TabRadius);
                var frt = (RectTransform)fill.transform;
                var le = frt.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = PetUITheme.TabHeight;
                le.flexibleWidth = 1f;

                var btn = fill.gameObject.AddComponent<Button>();
                btn.targetGraphic = fill;
                btn.onClick.AddListener(() => onClick(captured));

                var tx = PetUIFactory.NewText("Label", fill.transform, labels[i],
                                              PetUITheme.FontBody,
                                              on ? Color.white : PetUITheme.TextPrimary,
                                              TextAnchor.MiddleCenter);
                PetUIFactory.Fill((RectTransform)tx.transform);
            }
            return row;
        }
    }

    /// <summary>
    /// 轻提示。展板里没有，是原型期补的 —— 缺反馈的操作（点设置、购买失败）
    /// 在手机上会让人以为按钮坏了。
    /// </summary>
    public class ToastWidget
    {
        private RectTransform _root;
        private Text _text;
        private CanvasGroup _group;
        private float _until;

        public void Build(Transform parent)
        {
            var go = PetUIFactory.NewSolid("Toast", parent, new Color(0f, 0f, 0f, 0.82f),
                                           PetUITheme.CardRadius);
            _root = (RectTransform)go.transform;
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.sizeDelta = new Vector2(720f, 96f);
            _root.anchoredPosition = new Vector2(0f, 120f);
            go.raycastTarget = false;      // 不能挡住下面的点击

            _group = go.gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _text = PetUIFactory.NewText("Label", go.transform, "", PetUITheme.FontBody,
                                          Color.white, TextAnchor.MiddleCenter);
            PetUIFactory.Fill((RectTransform)_text.transform, 20f);

            _root.gameObject.SetActive(false);
        }

        public void Show(string msg)
        {
            if (_root == null) return;
            _text.text = msg;
            _root.gameObject.SetActive(true);
            _group.alpha = 1f;
            _until = Time.unscaledTime + 2f;
        }

        /// <summary>
        /// 用 unscaledTime 而不是 Time.time ——
        /// 打开弹窗时若把 timeScale 置 0，Time.time 不走，提示会永远挂着不消失。
        /// </summary>
        public void Tick()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;
            if (Time.unscaledTime < _until) return;
            _root.gameObject.SetActive(false);
        }
    }
}
