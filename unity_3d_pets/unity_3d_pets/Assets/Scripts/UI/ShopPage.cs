using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 商店页。展板第 6 页。
    ///
    /// 展板是 2×2 网格，购物车式卡片（大插画 / 名称 / 价格 / 购买按钮）。
    /// 本实现做成**两列等宽的自适应高度网格**，卡片高度按内容定 ——
    /// GridLayoutGroup 强制所有格子等高，名称换行时会留空白或截断。
    ///
    /// 卡片里的插画与金币是 <see cref="PetFoodArt"/> 的真实美术资源，
    /// 不再是首字色块；价格行用 HorizontalLayoutGroup 让「金币 + 数字」
    /// 按内容宽度居中（早先价格容器宽 0、数字文本宽 -6，价格数字根本不显示）。
    ///
    /// 「道具」这一类后台暂时没有对应配置（PRD 3.4 标了【待确认：道具具体内容】），
    /// 所以只给食品 + 特殊两个 Tab。道具类的位置留好了，
    /// 后台加了 item 表填进来就能显示 —— 不擅自编道具内容。
    /// </summary>
    public class ShopPage : PetPageBase
    {
        private static readonly string[] Tabs = { "食品", "道具", "特殊" };

        private PetHomeUI _ui;
        private int _tab;
        private RectTransform _grid;
        private Text _emptyHint;

        /// <summary>购买确认弹窗。复用同一个实例，不要每次购买都建一个。</summary>
        private ConfirmPopup _confirm;

        public override void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;
            BuildShell("商店", parent);

            MakeTabRow(Content, Tabs,
                i => i == _tab,
                i => { _tab = i; RebuildGrid(); });

            BuildGrid();

            _emptyHint = PetUIFactory.NewText("Empty", Root.transform, "这一类暂时没有商品",
                                              PetUITheme.FontBody, PetUITheme.TextDisabled,
                                              TextAnchor.MiddleCenter);
            var ert = (RectTransform)_emptyHint.transform;
            ert.anchorMin = Vector2.zero;
            ert.anchorMax = Vector2.one;
            ert.offsetMin = new Vector2(PetUITheme.PageMargin, PetUITheme.NavBarHeight + PetUITheme.SafeBottom + 12f);
            ert.offsetMax = new Vector2(-PetUITheme.PageMargin, -PetUITheme.TopBarHeight - 100f);
            _emptyHint.gameObject.SetActive(false);

            _confirm = new ConfirmPopup();
            _confirm.Build(Root.transform, ui);
        }

        public override void Refresh()
        {
            RebuildGrid();
            _confirm?.Refresh();
        }

        /// <summary>
        /// 两列网格。实现方式是「一个竖排，每行内放两个等宽子项」，
        /// 这样卡片高度可以按内容自适应，不像 GridLayoutGroup 那样被锁死。
        /// </summary>
        private void BuildGrid()
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform));
            scrollGo.layer = LayerMask.NameToLayer("UI");
            var srt = (RectTransform)scrollGo.transform;
            srt.SetParent(Content, false);
            srt.anchorMin = Vector2.zero;
            srt.anchorMax = Vector2.one;
            srt.offsetMin = Vector2.zero;
            // 让开顶上的 Tab 行（否则滚动区和 Tab 叠在一起）
            srt.offsetMax = new Vector2(0f, -(PetUITheme.TabHeight + 12f));

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            var viewport = PetUIFactory.NewPanel("Viewport", scrollGo.transform);
            var vrt = (RectTransform)viewport.transform;
            PetUIFactory.Fill(vrt);
            var maskImg = viewport.AddComponent<Image>();
            maskImg.sprite = PetUIFactory.WhiteSprite;
            // alpha 必须是 1：Mask 的模板是这张图自己画出来的，alpha 为 0 时
            // CanvasRenderer 整条绘制被跳过、模板没写上，滚动区会一片空白。
            // showMaskGraphic=false 已经让它只写模板不上屏，详见 PetPageBase.MakeScrollable。
            maskImg.color = Color.white;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vrt;

            var inner = PetUIFactory.NewPanel("Grid", viewport.transform);
            _grid = (RectTransform)inner.transform;
            _grid.anchorMin = new Vector2(0f, 1f);
            _grid.anchorMax = new Vector2(1f, 1f);
            _grid.pivot = new Vector2(0.5f, 1f);
            _grid.offsetMin = Vector2.zero;
            _grid.offsetMax = Vector2.zero;

            var list = inner.AddComponent<VerticalLayoutGroup>();
            list.spacing = PetUITheme.CardGap;
            list.padding = new RectOffset(0, 0, 0, 24);
            list.childAlignment = TextAnchor.UpperLeft;
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;

            var fitter = inner.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _grid;
        }

        private void RebuildGrid()
        {
            if (_grid == null) return;
            PetUIUtil.ClearChildren(_grid);

            var cat = _ui.Catalog;
            // Tab 0=食品 2=特殊 映射到 FoodCategory；Tab 1=道具后台还没有配置。
            List<FoodItemConfig> items = _tab == 1
                ? new List<FoodItemConfig>()
                : cat.ByCategory(_tab == 0 ? FoodCategory.Main : FoodCategory.Special);

            // 「食品」这一页要同时展示 Main 与 Snack —— 后台分类是三档，
            // 展板商店页把主粮和零食放在同一页按价格排。特殊单独一页。
            if (_tab == 0)
            {
                items = cat.AllSorted();
                items.RemoveAll(f => f.category == FoodCategory.Special);
            }

            if (_emptyHint != null) _emptyHint.gameObject.SetActive(items.Count == 0);
            if (items.Count == 0) return;

            // 每两个一行
            for (int i = 0; i < items.Count; i += 2)
            {
                var row = PetUIFactory.NewPanel("Row", _grid.transform);
                var rrt = (RectTransform)row.transform;
                var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = PetUITheme.CardGap;
                rowLayout.childAlignment = TextAnchor.UpperLeft;
                rowLayout.childControlWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandWidth = true;
                rowLayout.childForceExpandHeight = false;

                BuildCell(items[i], rrt);
                // 奇数个时第二个格子留空 —— 不给占位卡片，
                // 空的半个格子比一张空白卡片自然。
                if (i + 1 < items.Count) BuildCell(items[i + 1], rrt);
            }

            RelayoutNow(_grid);
        }

        private void BuildCell(FoodItemConfig food, Transform parent)
        {
            var card = PetUIFactory.NewSolid("Cell", parent, PetUITheme.Card, PetUITheme.CardRadius);
            var le = card.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            // 卡片高度 400：插画 180 + 名称 30 + 价格 42 + 按钮 66 + 四段间距。
            // 三行 400 + 两个 16 间距 = 1232，小于内容区净高 1440，一屏放得下不用滚。
            le.preferredHeight = 400f;

            var crt = (RectTransform)card.transform;

            // ---- 插画：真实美术资源，**保留原色**（PetFoodArt 建的 Image 恒为白，
            // 调用方不要再改它的 color —— 改了就是给插画蒙一层色） ----
            var art = PetFoodArt.NewArt(food.iconKey, crt, new Vector2(180f, 180f),
                                        FoodPage.IconColor(food.category));
            var artRt = (RectTransform)art.transform;
            artRt.anchorMin = new Vector2(0.5f, 1f);
            artRt.anchorMax = new Vector2(0.5f, 1f);
            artRt.pivot = new Vector2(0.5f, 1f);
            artRt.anchoredPosition = new Vector2(0f, -14f);

            // ---- 名称 ----
            var nameTx = PetUIFactory.NewText("Name", crt, food.name, PetUITheme.FontBody,
                                              PetUITheme.TextPrimary, TextAnchor.UpperCenter);
            var nrt = (RectTransform)nameTx.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0.5f, 1f);
            nrt.offsetMin = new Vector2(8f, -236f);
            nrt.offsetMax = new Vector2(-8f, -206f);

            // ---- 价格：金币插画 + 数字 ----
            // 用 HorizontalLayoutGroup 而不是手摆：数字是一位还是两位宽度差一倍，
            // 手摆要么写死偏移（一位数时整体偏左），要么把容器宽设成 0 ——
            // 早先就是容器 sizeDelta.x=0、数字文本宽 -6，价格数字根本不显示。
            // 布局组按「金币 preferred 38 + 间距 8 + 数字 preferred」算总宽，
            // childAlignment=MiddleCenter 把这一组在容器里居中。
            var priceBox = PetUIFactory.NewPanel("Price", crt);
            var prt = (RectTransform)priceBox.transform;
            prt.anchorMin = new Vector2(0.5f, 0f);
            prt.anchorMax = new Vector2(0.5f, 0f);
            prt.pivot = new Vector2(0.5f, 0f);
            prt.sizeDelta = new Vector2(240f, 42f);
            prt.anchoredPosition = new Vector2(0f, 106f);

            var priceRow = priceBox.AddComponent<HorizontalLayoutGroup>();
            priceRow.spacing = 8f;
            priceRow.childAlignment = TextAnchor.MiddleCenter;
            priceRow.childControlWidth = true;
            priceRow.childControlHeight = true;
            priceRow.childForceExpandWidth = false;
            priceRow.childForceExpandHeight = false;

            var coin = PetFoodArt.NewArt("coin", prt, new Vector2(38f, 38f), PetUITheme.Coin);
            // Image 不实现 ILayoutElement，布局组算不出它的尺寸，
            // 必须补一个 LayoutElement 把 38×38 报上去（否则金币宽 0）。
            var coinLe = coin.gameObject.AddComponent<LayoutElement>();
            coinLe.preferredWidth = 38f;
            coinLe.preferredHeight = 38f;
            coinLe.flexibleWidth = 0f;
            coinLe.flexibleHeight = 0f;

            // Text 自带 ILayoutElement（preferredWidth 就是文字宽度），
            // 布局组会把它的宽度收成内容宽，不用补 LayoutElement。
            var priceTx = PetUIFactory.NewText("V", prt, food.price.ToString(),
                                               PetUITheme.FontBody, PetUITheme.TextPrimary,
                                               TextAnchor.MiddleLeft);

            // 购买按钮。买不起时置灰并改文案（比单纯置灰更容易理解）。
            var state = _ui.State;
            var session = _ui.Session;

            // session 为 null 表示还没初始化完（Build 期间），此时不拦 ——
            // Begin 完成后会发 Changed，这一页会按真实登录态重建成正确状态。
            bool online = session == null || session.Online;

            bool afford = state.points >= food.price;
            bool levelOk = state.level >= food.minLevel;
            bool canBuy = afford && online;
            string label = !online ? "未联网" : (afford ? "购买" : "积分不足");

            var btn = PetUIFactory.NewPillButton(crt, label,
                                                  canBuy ? PetUITheme.Primary : PetUITheme.DisabledFill,
                                                  PetUITheme.FontHeading, out _);
            var brt = (RectTransform)btn.transform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(1f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.offsetMin = new Vector2(10f, 20f);
            brt.offsetMax = new Vector2(-10f, 86f);

            btn.interactable = canBuy;
            var captured = food;
            btn.onClick.AddListener(() =>
            {
                if (!levelOk)
                {
                    _ui.ShowToast($"需要 Lv.{food.minLevel} 才能购买");
                    return;
                }
                _confirm.Show(captured);
            });
        }
    }
}
