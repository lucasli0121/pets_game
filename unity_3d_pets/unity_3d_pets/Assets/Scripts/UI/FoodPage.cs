using System;
using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 食品背包页。展板第 4 页。
    ///
    /// 展板上的四种食品（牛肉狗粮 / 狗狗饼干 / 鸡肉干 / 生日蛋糕）**没有采用** ——
    /// 后台 config_food 是五条（普通狗粮 / 牛肉罐头 / 磨牙饼干 / 鸡胸肉 / 生日蛋糕），
    /// 名称、价格、效果都不同。按「一切以后台为准」的口径用后台那五条，
    /// 展板只用来定布局：横向列表项 = 图标 + 名称 + 效果摘要 + 右侧数量。
    /// </summary>
    public class FoodPage : PetPageBase
    {
        private static readonly string[] Tabs = { "全部", "主食", "零食", "特殊" };

        private PetHomeUI _ui;
        private int _tab;                 // 0=全部 1..3 对应 FoodCategory
        private VerticalLayoutGroup _list;
        private Text _emptyHint;

        public override void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;
            BuildShell("我的食品", parent);

            MakeTabRow(Content, Tabs,
                i => i == _tab,
                i => { _tab = i; RebuildList(); });

            _list = MakeScrollable(PetUITheme.TabHeight + 12f);

            // 空提示放在 Content 下而不是 list 里 ——
            // RebuildList 会 ClearChildren(list)，放里面会被一起删掉。
            _emptyHint = PetUIFactory.NewText("Empty", Root.transform, "背包里还没有这个分类的食品",
                                              PetUITheme.FontBody, PetUITheme.TextDisabled,
                                              TextAnchor.MiddleCenter);
            var ert = (RectTransform)_emptyHint.transform;
            ert.anchorMin = Vector2.zero;
            ert.anchorMax = Vector2.one;
            ert.offsetMin = new Vector2(PetUITheme.PageMargin, PetUITheme.NavBarHeight + PetUITheme.SafeBottom + 12f);
            ert.offsetMax = new Vector2(-PetUITheme.PageMargin, -PetUITheme.TopBarHeight - 100f);
            _emptyHint.gameObject.SetActive(false);
        }

        public override void Refresh() => RebuildList();

        private List<FoodItemConfig> CurrentItems()
        {
            var cat = _ui.Catalog;
            if (_tab == 0) return cat.AllSorted();
            return cat.ByCategory((FoodCategory)(_tab - 1));
        }

        private void RebuildList()
        {
            if (_list == null) return;
            _list.gameObject.SetActive(true);

            // 重建而不是复用：分类切换时列表内容完全变了，
            // 复用要处理「加了几项 / 删了几项 / 顺序变了」三种情况，
            // 比清空重建更容易出错。原型阶段列表不长，重建成本可忽略。
            //
            // 关键：**必须用 DestroyImmediate，不能用 Destroy**。
            // Object.Destroy 是帧末才生效，重建时上一帧的子节点还在 transform 里，
            // 于是「清空 → 添加」实际变成「清空（无效）→ 旧行 + 新行叠加」，
            // 表现为每次切分类行数翻倍。
            PetUIUtil.ClearChildren(_list.transform);

            var items = CurrentItems();
            bool any = false;
            foreach (var food in items)
            {
                int count = _ui.State.CountOf(food.id);
                // 「全部」分类下数量为 0 的也显示（灰显），
                // 让玩家知道存在这种食物；其他分类下不显示空条目，列表更干净。
                if (_tab != 0 && count <= 0) continue;
                BuildRow(food, count).transform.SetParent(_list.transform, false);
                any = true;
            }

            if (_emptyHint != null) _emptyHint.gameObject.SetActive(!any);
            RelayoutNow((RectTransform)_list.transform);
        }

        private GameObject BuildRow(FoodItemConfig food, int count)
        {
            var card = PetUIFactory.NewSolid("Row", _list.transform, PetUITheme.Card,
                                             PetUITheme.CardRadius);
            var le = card.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 140f;

            var inner = (RectTransform)card.transform;
            PetUIFactory.Fill(inner, PetUITheme.CardPadding * 0.5f);

            bool owned = count > 0;
            var body = PetUIFactory.NewSolid("Body", card.transform,
                                             new Color(0f, 0f, 0f, 0f), 0f);
            var brt = (RectTransform)body.transform;
            PetUIFactory.Fill(brt, 0f);

            // 图标：<see cref="PetFoodArt"/> 的真实插画，保留原色。
            // 缺图时退化成分类色块（IconColor），并打一条 Error。
            var icon = PetFoodArt.NewArt(food.iconKey, brt, new Vector2(96f, 96f),
                                         IconColor(food.category));
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
            irt.pivot = new Vector2(0f, 0.5f);
            irt.anchoredPosition = Vector2.zero;

            // 名称 + 效果
            var nameTx = PetUIFactory.NewText("Name", brt, food.name, PetUITheme.FontBody,
                                              PetUITheme.TextPrimary, TextAnchor.UpperLeft);
            var nrt = (RectTransform)nameTx.transform;
            nrt.anchorMin = new Vector2(0f, 0.5f);
            nrt.anchorMax = new Vector2(1f, 0.5f);
            nrt.pivot = new Vector2(0f, 0.5f);
            nrt.offsetMin = new Vector2(114f, 4f);
            nrt.offsetMax = new Vector2(-120f, 46f);

            var effTx = PetUIFactory.NewText("Eff", brt, food.EffectSummary(),
                                             PetUITheme.FontSmall,
                                             PetUITheme.TextSecondary, TextAnchor.UpperLeft);
            var ert = (RectTransform)effTx.transform;
            ert.anchorMin = new Vector2(0f, 0.5f);
            ert.anchorMax = new Vector2(1f, 0.5f);
            ert.pivot = new Vector2(0f, 0.5f);
            ert.offsetMin = new Vector2(114f, -42f);
            ert.offsetMax = new Vector2(-120f, 0f);

            // 右侧数量
            var countTx = PetUIFactory.NewText("Count", brt, $"x{count}",
                                               PetUITheme.FontBody,
                                               owned ? PetUITheme.Primary : PetUITheme.TextDisabled,
                                               TextAnchor.MiddleRight);
            var crt = (RectTransform)countTx.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 0.5f);
            crt.pivot = new Vector2(1f, 0.5f);
            crt.sizeDelta = new Vector2(100f, 44f);
            crt.anchoredPosition = Vector2.zero;

            // 点整行进详情。没库存也能进详情（只是不能喂），展板就是这么做的。
            var hit = PetUIFactory.NewHitArea("Hit", brt);
            PetUIFactory.Fill((RectTransform)hit.transform);
            var btn = hit.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            var captured = food;
            btn.onClick.AddListener(() => _ui.ShowFoodDetail(captured));

            return card.gameObject;
        }

        /// <summary>分类色。与展板一致：主食偏棕、零食偏黄、特殊偏粉。</summary>
        public static Color IconColor(FoodCategory cat)
        {
            switch (cat)
            {
                case FoodCategory.Main: return new Color(0.78f, 0.55f, 0.36f);
                case FoodCategory.Snack: return new Color(0.93f, 0.75f, 0.42f);
                default: return new Color(0.94f, 0.60f, 0.66f);
            }
        }

        /// <summary>
        /// 喂食。**唯一实现**，食品详情弹窗也调它 ——
        /// 两份实现迟早会走偏（一个加了上限另一个没加），
        /// 而且「从食品页喂」和「从详情页喂」的结果必须完全一致。
        ///
        /// 走 <c>/api/v1/pet/feed</c>：服务端扣库存、加饱腹/快乐、判断升级。
        /// 客户端**不自己算任何数值** —— 服务端会夹上限（100）、会命中每日上限、
        /// 会连带升级发奖，自己加一定对不上，而且对不上时没人能发现。
        ///
        /// <paramref name="done"/> 在请求结束后调用，true 表示成功。
        /// 弹窗根据它决定是关闭还是留在原地显示错误。
        /// </summary>
        public static void Feed(PetHomeUI ui, FoodItemConfig food, Action<bool> done = null)
        {
            if (ui == null || food == null) { done?.Invoke(false); return; }

            var s = ui.State;
            if (s.CountOf(food.id) <= 0)
            {
                ui.ShowToast("这个食品已经用完了，去商店看看");
                done?.Invoke(false);
                return;
            }

            var session = ui.Session;
            if (session == null || !session.Online)
            {
                ui.ShowToast("没连上服务端，暂时不能喂食");
                done?.Invoke(false);
                return;
            }

            // 弹窗/页面在请求飞行期间可能被关掉，回调里所有 ui 访问都要判空
            session.Feed(food, (ok, msg) =>
            {
                if (ui == null) { done?.Invoke(ok); return; }
                // 数据已由 session 写进 State，这里负责让界面重画 + 给反馈
                ui.NotifyStateChanged();
                if (!string.IsNullOrEmpty(msg)) ui.ShowToast(msg);
                done?.Invoke(ok);
            });
        }
    }
}
