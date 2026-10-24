using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 通用确认弹窗。用于购买确认（PRD 3.4「购买前确认」）与解锁场景。
    ///
    /// 为什么带半透明遮罩：点遮罩关闭是移动端的标准预期，
    /// 而且没有遮罩的话弹窗后面还能点，误触率高。
    ///
    /// 实例复用：整页共用一个，不每次显示都新建 ——
    /// 频繁 new GameObject 会让 Canvas 下的节点数持续增长（每次 rebuild 加几个），
    /// 跑几分钟后 Hierarchy 会变得没法看。
    /// </summary>
    public class ConfirmPopup
    {
        private PetHomeUI _ui;
        private Image _root;
        private Image _panel;
        private Text _title;
        private Text _body;
        private Text _balance;
        private Button _ok;
        private Text _okLabel;
        private Button _ad;
        private Text _adLabel;
        private FoodItemConfig _pendingFood;
        private PetSceneLibrary.SceneEntry _pendingScene;

        /// <summary>当前弹窗在问什么。两种模式共用一套控件，但确认动作完全不同。</summary>
        private enum Mode { None, BuyFood, UnlockScene }

        private Mode _mode = Mode.None;

        public void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;

            // 遮罩：铺满，点它等于取消。
            // 必须显式开 raycastTarget：NewSolid(radius:0) 默认关射线，
            // 关掉的话遮罩变成「看得见但点不到」，点外面不消失。
            _root = PetUIFactory.NewSolid("ConfirmMask", parent, PetUITheme.Scrim, 0f);
            _root.raycastTarget = true;
            var rrt = (RectTransform)_root.transform;
            PetUIFactory.Fill(rrt);
            _root.gameObject.SetActive(false);

            var cancel = _root.gameObject.AddComponent<Button>();
            cancel.targetGraphic = _root;
            cancel.onClick.AddListener(Hide);

            // 白色面板
            _panel = PetUIFactory.NewSolid("Panel", _root.transform, PetUITheme.Card,
                                            PetUITheme.CardRadius);
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(760f, 520f);
            prt.anchoredPosition = Vector2.zero;
            // 面板上的点击不能冒到遮罩上（否则点「确认」等于点「取消」）
            var blocker = PetUIFactory.NewHitArea("Block", prt);
            PetUIFactory.Fill((RectTransform)blocker.transform);
            blocker.gameObject.AddComponent<Button>();

            _title = PetUIFactory.NewText("Title", prt, "确认购买", PetUITheme.FontHeading,
                                           PetUITheme.TextPrimary, TextAnchor.UpperCenter);
            var trt = (RectTransform)_title.transform;
            trt.anchorMin = new Vector2(0f, 1f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(30f, -110f);
            trt.offsetMax = new Vector2(-30f, -30f);

            // 商品名 + 效果
            _body = PetUIFactory.NewText("Body", prt, "", PetUITheme.FontBody,
                                          PetUITheme.TextPrimary, TextAnchor.UpperCenter);
            var brt = (RectTransform)_body.transform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(0.5f, 1f);
            brt.offsetMin = new Vector2(30f, -250f);
            brt.offsetMax = new Vector2(-30f, -120f);

            // 余额
            _balance = PetUIFactory.NewText("Balance", prt, "", PetUITheme.FontBody,
                                             PetUITheme.TextSecondary, TextAnchor.UpperCenter);
            var lrt = (RectTransform)_balance.transform;
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.offsetMin = new Vector2(30f, -310f);
            lrt.offsetMax = new Vector2(-30f, -260f);

            _ok = PetUIFactory.NewPillButton(prt, "确认", PetUITheme.Primary,
                                              PetUITheme.FontHeading, out _okLabel);
            var okRt = (RectTransform)_ok.transform;
            okRt.anchorMin = new Vector2(0f, 0f);
            okRt.anchorMax = new Vector2(1f, 0f);
            okRt.pivot = new Vector2(0.5f, 0f);
            okRt.offsetMin = new Vector2(40f, 60f);
            okRt.offsetMax = new Vector2(-40f, 60f + PetUITheme.PrimaryButtonHeight);
            _ok.onClick.AddListener(OnConfirm);

            // 「看广告得积分」。**只在积分不够时出现** ——
            // 需求口径：积分不足时提示用户看 15 秒广告获取积分。
            // 放在确认键上方，用描边空心样式，不抢主操作的注意力。
            _ad = PetUIFactory.NewPillButton(prt, "", PetUITheme.Coin,
                                              PetUITheme.FontBody, out _adLabel);
            var adRt = (RectTransform)_ad.transform;
            adRt.anchorMin = new Vector2(0f, 0f);
            adRt.anchorMax = new Vector2(1f, 0f);
            adRt.pivot = new Vector2(0.5f, 0f);
            adRt.offsetMin = new Vector2(40f, 60f + PetUITheme.PrimaryButtonHeight + 16f);
            adRt.offsetMax = new Vector2(-40f, 60f + PetUITheme.PrimaryButtonHeight * 2f + 16f);
            _ad.onClick.AddListener(OnWatchAd);
            _ad.gameObject.SetActive(false);
        }

        /// <summary>
        /// 看广告拿积分。播完再由 <see cref="PetSession.ClaimAdReward"/> 发奖。
        ///
        /// 为什么发奖放在会话层：整个工程只有 PetSession 写状态，
        /// 这里只负责「放广告 + 把结果转成一次请求」。
        /// </summary>
        private void OnWatchAd()
        {
            var st = _ui.State;
            if (!st.AdRewardReady)
            {
                _ui.ShowToast("广告还没准备好，稍后再试");
                return;
            }

            var ads = _ui.Ads;
            if (ads == null || !ads.Available)
            {
                _ui.ShowToast("当前环境不支持广告");
                return;
            }

            _ad.interactable = false;
            _adLabel.text = "广告播放中…";

            ads.PlayRewardedThenClaim(
                "insufficient_points",
                // 看完 → 发奖。completed 恒 true（没看完不会走到这里）。
                (completed, done) => _ui.Session.ClaimAdReward(
                    st.adRewardedUnitId, "insufficient_points", done),
                (ok, msg) =>
                {
                    if (_root == null) return;
                    _ad.interactable = true;
                    _adLabel.text = $"看广告 +{st.adReward} 积分";
                    _ui.ShowToast(string.IsNullOrEmpty(msg) ? "领奖失败" : msg);
                    // 无论成功失败都刷一次：失败时余额可能已经变了（比如太频繁），
                    // 而且成功后要立刻把「积分不足」变成「可以买」。
                    Refresh();
                });
        }

        public void Show(FoodItemConfig food)
        {
            _pendingFood = food;
            _pendingScene = null;
            _mode = Mode.BuyFood;

            _title.text = "确认购买";
            _body.text = $"{food.name}\n<size=26>{food.EffectSummary()}</size>";
            Refresh();
            _root.gameObject.SetActive(true);
        }

        /// <summary>解锁场景的确认。扣积分不可逆，必须让玩家看清扣多少。</summary>
        public void ShowUnlockScene(PetSceneLibrary.SceneEntry entry)
        {
            if (entry == null) return;
            _pendingScene = entry;
            _pendingFood = null;
            _mode = Mode.UnlockScene;

            _title.text = "解锁场景";
            _body.text = $"{entry.displayName}\n<size=26>解锁后可以随时切换使用</size>";
            Refresh();
            _root.gameObject.SetActive(true);
        }

        /// <summary>刷新余额文案。余额在弹窗打开期间可能因别处操作变了。</summary>
        public void Refresh()
        {
            if (_root == null) return;
            var s = _ui.State;

            int cost = _mode switch
            {
                Mode.BuyFood => _pendingFood?.price ?? 0,
                Mode.UnlockScene => _pendingScene?.price ?? 0,
                _ => 0,
            };
            if (_mode == Mode.None) return;

            bool afford = s.points >= cost;
            _balance.text = afford
                ? $"当前积分 {s.points}"
                : $"当前积分 {s.points}，还差 {cost - s.points}";
            _okLabel.text = afford ? "确认" : "积分不足";
            _ok.interactable = afford;
            _ok.image.color = afford ? PetUITheme.Primary : PetUITheme.DisabledFill;

            // 积分不够时才放广告入口。够积分的时候放上去，
            // 会诱导本来要花钱的人先去看广告 —— 那是另一种骗法，不做。
            bool showAd = !afford && s.AdRewardReady
                          && _ui.Ads != null && _ui.Ads.Available;
            if (_ad != null)
            {
                _ad.gameObject.SetActive(showAd);
                if (showAd && _adLabel != null)
                    _adLabel.text = $"看广告 +{s.adReward} 积分";
            }
        }

        public void Hide()
        {
            _mode = Mode.None;
            _pendingFood = null;
            _pendingScene = null;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void OnConfirm()
        {
            if (_mode == Mode.BuyFood) ConfirmBuy();
            else if (_mode == Mode.UnlockScene) ConfirmUnlock();
        }

        private void ConfirmBuy()
        {
            var food = _pendingFood;
            if (food == null) return;
            var ui = _ui;

            // 本地余额只用来拦「明显买不起」，真正的校验在服务端：
            // 余额可能已被别处扣掉，价格也可能后台刚改过。
            if (ui.State.points < food.price)
            {
                Hide();
                ui.ShowToast("积分不足");
                return;
            }

            var session = ui.Session;
            if (session == null || !session.Online)
            {
                Hide();
                ui.ShowToast("没连上服务端，暂时不能购买");
                return;
            }

            var name = food.name;

            // 先关弹窗再发请求：留着的话玩家会重复点确认，
            // 每次点击都是新的 requestId，服务端会真的买两次。
            Hide();

            // 余额与库存一律用服务端返回值覆盖（PetSession 里做），
            // 客户端不自己扣 —— 自己扣迟早和服务端对不上（PRD 4.2）。
            session.BuyFood(food, (ok, msg) =>
            {
                if (ui == null) return;
                if (!ok)
                {
                    ui.ShowToast(string.IsNullOrEmpty(msg) ? "购买失败" : msg);
                    return;
                }
                ui.NotifyStateChanged();
                ui.ShowToast(string.IsNullOrEmpty(msg) ? $"买到 {name} ×1" : msg);
            });
        }

        private void ConfirmUnlock()
        {
            var entry = _pendingScene;
            if (entry == null) return;
            var ui = _ui;

            // 本地余额只用来拦「明显买不起」，真正的校验在服务端。
            if (ui.State.points < entry.price)
            {
                Hide();
                ui.ShowToast("积分不足");
                return;
            }

            var session = ui.Session;
            if (session == null || !session.Online)
            {
                Hide();
                ui.ShowToast("没连上服务端，暂时不能解锁");
                return;
            }

            var id = entry.sceneId;
            var name = entry.displayName;

            // 先关弹窗：留着会被重复点，每次都是一个新 requestId，服务端会真的扣两次。
            Hide();

            session.UnlockScene(entry, (ok, msg) =>
            {
                if (ui == null) return;
                if (!ok)
                {
                    ui.ShowToast(string.IsNullOrEmpty(msg) ? "解锁失败" : msg);
                    return;
                }

                // 解锁成功后直接切过去 —— 玩家刚解锁就是想看，
                // 多点一次「进入」会让人以为没生效。
                // 切换失败不回滚解锁（钱已经花了），只提示，场景列表里仍是已解锁状态。
                ui.SwitchScene(id, () =>
                {
                    if (ui == null) return;
                    ui.ShowToast(string.IsNullOrEmpty(msg) ? $"已解锁 {name}" : msg);
                });
            });
        }
    }

    /// <summary>
    /// 食品详情弹窗。展板第 5 页。
    ///
    /// 展板布局：大图标 → 名称 → 效果列表 → 「当前拥有 N」→ 「立即喂食」按钮。
    /// 点喂食后关闭弹窗，食物出现在 3D 场景里。
    ///
    /// 关于「食物出现在 3D 场景」：那是喂食动画的表现层（<c>PetFeedPresenter</c>），
    /// 本原型先把流程打通 —— 扣库存、加状态、给提示。
    /// 3D 里的食盆与走过去吃的动作留接口，见 <see cref="OnFeedRequested"/>。
    /// </summary>
    public class FoodDetailPopup
    {
        private PetHomeUI _ui;
        private Image _root;
        private FoodItemConfig _food;
        private Text _name;
        private RectTransform _effectBox;
        private readonly List<EffectRow> _effectRows = new List<EffectRow>();
        private Text _effectNone;
        private Text _owned;
        private Button _feedBtn;
        private Text _feedLabel;
        private Image _icon;

        /// <summary>点了「立即喂食」。宿主（场景侧）订阅它来播放喂食表现。</summary>
        public event System.Action<FoodItemConfig> OnFeedRequested;

        public void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;

            _root = PetUIFactory.NewSolid("FoodDetailMask", parent, PetUITheme.Scrim, 0f);
            _root.raycastTarget = true;   // 同 ConfirmMask：点遮罩要能关闭
            var rrt = (RectTransform)_root.transform;
            PetUIFactory.Fill(rrt);
            _root.gameObject.SetActive(false);

            var closeOnMask = _root.gameObject.AddComponent<Button>();
            closeOnMask.targetGraphic = _root;
            closeOnMask.onClick.AddListener(Hide);

            var panel = PetUIFactory.NewSolid("Panel", _root.transform, PetUITheme.Card,
                                               PetUITheme.CardRadius);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(720f, 820f);
            prt.anchoredPosition = Vector2.zero;

            var blocker = PetUIFactory.NewHitArea("Block", prt);
            PetUIFactory.Fill((RectTransform)blocker.transform);
            blocker.gameObject.AddComponent<Button>();

            // 左上角返回箭头（展板 s5 有：< 与 × 两个关闭方式）。
            // 「返回」和「关闭」在这里行为相同 —— 食品详情是从食品页点进来的，
            // 关掉就回到食品页，没有更深的层级可退。
            var back = PetUIFactory.NewCircleButton(prt, "<", 72f,
                                                    PetUITheme.Line, out var backTx);
            var bkt = (RectTransform)back.transform;
            bkt.anchorMin = bkt.anchorMax = new Vector2(0f, 1f);
            bkt.pivot = new Vector2(0f, 1f);
            bkt.anchoredPosition = new Vector2(24f, -24f);
            backTx.color = PetUITheme.TextSecondary;
            back.onClick.AddListener(Hide);

            // 关闭按钮（展板右上角的 X）
            var close = PetUIFactory.NewCircleButton(prt, "×", 72f,
                                                     PetUITheme.Line, out var closeTx);
            var crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-24f, -24f);
            closeTx.color = PetUITheme.TextSecondary;
            close.onClick.AddListener(Hide);

            // 大图标：真实插画。只建一次，Show 时按食物换内容（见 PetFoodArt.Apply）。
            // 保留原色 —— 不要再去改 _icon.color，改了就是给插画蒙一层色。
            // 节点名固定叫 Icon：NewArt 的名字里带 iconKey，而建的时候还不知道是哪张图。
            var iconGo = new GameObject("Icon", typeof(RectTransform));
            iconGo.layer = LayerMask.NameToLayer("UI");
            _icon = iconGo.AddComponent<Image>();
            PetFoodArt.Apply(_icon, "", PetUITheme.Card);
            _icon.preserveAspect = true;
            _icon.raycastTarget = false;
            var irt = (RectTransform)iconGo.transform;
            irt.SetParent(prt, false);
            irt.anchorMin = new Vector2(0.5f, 1f);
            irt.anchorMax = new Vector2(0.5f, 1f);
            irt.pivot = new Vector2(0.5f, 1f);
            irt.sizeDelta = new Vector2(220f, 220f);
            irt.anchoredPosition = new Vector2(0f, -60f);

            _name = PetUIFactory.NewText("Name", prt, "", PetUITheme.FontHeading,
                                         PetUITheme.TextPrimary, TextAnchor.UpperCenter);
            var nrt = (RectTransform)_name.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0.5f, 1f);
            nrt.offsetMin = new Vector2(30f, -340f);
            nrt.offsetMax = new Vector2(-30f, -300f);

            // 效果列表容器。行在 Show 里按食物动态建（见 RebuildEffects）——
            // uGUI 富文本不支持 <align>（标签会被原样打出来），所以每行用两个 Text：左名称 + 右数值。
            var effectBox = PetUIFactory.NewSolid("Effects", prt, PetUITheme.PageBackground,
                                                   PetUITheme.CardRadius);
            var ert = (RectTransform)effectBox.transform;
            ert.anchorMin = new Vector2(0f, 1f);
            ert.anchorMax = new Vector2(1f, 1f);
            ert.pivot = new Vector2(0.5f, 1f);
            ert.offsetMin = new Vector2(40f, -540f);
            ert.offsetMax = new Vector2(-40f, -360f);
            _effectBox = ert;

            // 效果行池：最多三条（饱腹 / 快乐 / 成长经验），一次建好复用。
            // 不每次 Show 都新建销毁 —— Object.Destroy 要到帧末才生效，
            // 同一帧内连开两次弹窗会看到旧行叠在新行上面。
            for (int i = 0; i < MaxEffectRows; i++)
            {
                var rowGo = new GameObject("EffRow", typeof(RectTransform));
                rowGo.layer = LayerMask.NameToLayer("UI");
                var rowRt = (RectTransform)rowGo.transform;
                rowRt.SetParent(_effectBox, false);
                rowRt.anchorMin = new Vector2(0f, 1f);
                rowRt.anchorMax = new Vector2(1f, 1f);
                rowRt.pivot = new Vector2(0.5f, 1f);
                rowRt.offsetMin = new Vector2(24f, -EffectRowHeight);
                rowRt.offsetMax = new Vector2(-24f, 0f);

                var label = PetUIFactory.NewText("Label", rowGo.transform, "",
                                                 PetUITheme.FontBody, PetUITheme.TextSecondary,
                                                 TextAnchor.MiddleLeft);
                var lrt2 = (RectTransform)label.transform;
                lrt2.anchorMin = new Vector2(0f, 0.5f);
                lrt2.anchorMax = new Vector2(0.62f, 0.5f);
                lrt2.pivot = new Vector2(0f, 0.5f);
                lrt2.offsetMin = new Vector2(IconSize + IconGap, -20f);
                lrt2.offsetMax = new Vector2(0f, 20f);

                var value = PetUIFactory.NewText("Value", rowGo.transform, "",
                                                 PetUITheme.FontBody, PetUITheme.TextPrimary,
                                                 TextAnchor.MiddleRight);
                PetUIFactory.Fill((RectTransform)value.transform, 0f);

                // 效果行的小图标（展板 s5：饱腹/成长经验 各有一个图标）。
                // 图标在 Show 里按效果类型换，建的时候还不知道是哪种。
                // 变量名不能叫 iconGo —— FoodDetailPopup.Build 里已经有一个
                // 大图标用了这个名字，同作用域重名直接 CS0136。
                var effIconGo = new GameObject("Icon", typeof(RectTransform));
                effIconGo.layer = LayerMask.NameToLayer("UI");
                var iconImg = effIconGo.AddComponent<UnityEngine.UI.Image>();
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                var icRt = (RectTransform)effIconGo.transform;
                icRt.SetParent(rowGo.transform, false);
                icRt.anchorMin = icRt.anchorMax = new Vector2(0f, 0.5f);
                icRt.pivot = new Vector2(0f, 0.5f);
                icRt.sizeDelta = new Vector2(IconSize, IconSize);
                icRt.anchoredPosition = new Vector2(0f, 0f);

                rowGo.SetActive(false);
                _effectRows.Add(new EffectRow { Rect = rowRt, Label = label, Value = value, Icon = iconImg });
            }

            // 三种效果全为 0 时的占位文案。
            _effectNone = PetUIFactory.NewText("EffNone", _effectBox, "无效果",
                                               PetUITheme.FontBody, PetUITheme.TextSecondary,
                                               TextAnchor.MiddleCenter);
            PetUIFactory.Fill((RectTransform)_effectNone.transform, 0f);
            _effectNone.gameObject.SetActive(false);

            _owned = PetUIFactory.NewText("Owned", prt, "", PetUITheme.FontBody,
                                           PetUITheme.TextSecondary, TextAnchor.UpperCenter);
            var ort = (RectTransform)_owned.transform;
            ort.anchorMin = new Vector2(0f, 1f);
            ort.anchorMax = new Vector2(1f, 1f);
            ort.pivot = new Vector2(0.5f, 1f);
            ort.offsetMin = new Vector2(30f, -600f);
            ort.offsetMax = new Vector2(-30f, -560f);

            _feedBtn = PetUIFactory.NewPillButton(prt, "立即喂食", PetUITheme.Primary,
                                                   PetUITheme.FontHeading, out _feedLabel);
            var frt = (RectTransform)_feedBtn.transform;
            frt.anchorMin = new Vector2(0f, 0f);
            frt.anchorMax = new Vector2(1f, 0f);
            frt.pivot = new Vector2(0.5f, 0f);
            frt.offsetMin = new Vector2(40f, 60f);
            frt.offsetMax = new Vector2(-40f, 60f + PetUITheme.PrimaryButtonHeight);
            _feedBtn.onClick.AddListener(OnFeed);
        }

        public void Show(FoodItemConfig food)
        {
            if (food == null) return;
            _food = food;

            _name.text = food.name;
            PetFoodArt.Apply(_icon, food.iconKey, FoodPage.IconColor(food.category));

            // 效果按「饱腹 / 快乐 / 成长经验」逐行列出，数值右对齐 ——
            // 与展板一致，比拼成一行更容易核对。
            RebuildEffects(food);

            Refresh();
            _root.gameObject.SetActive(true);
        }

        /// <summary>效果框的固定高度（局部坐标）。</summary>
        private const float EffectBoxHeight = 180f;

        /// <summary>单行高度。</summary>
        private const float EffectRowHeight = 54f;

        /// <summary>行间距。</summary>
        private const float EffectRowGap = 9f;

        /// <summary>效果行池容量。效果只有饱腹/快乐/成长经验三种，三条就是上限。</summary>
        private const int MaxEffectRows = 3;

        /// <summary>效果行小图标边长与图标和文字的间距。</summary>
        private const float IconSize = 30f;
        private const float IconGap = 12f;

        /// <summary>
        /// 重建效果列表：左名称 + 右数值，每行两个 Text。
        ///
        /// 不用富文本 align 标签的原因是 uGUI 富文本不支持它，标签会被当普通字符
        /// 原样打出来（已实测：单引号/双引号、color、size 都正常，只有 align 原样显示）。
        /// </summary>
        private void RebuildEffects(FoodItemConfig food)
        {
            if (_effectBox == null) return;

            var rows = new List<KeyValuePair<string, string>>();
            if (food.satiety != 0) rows.Add(new KeyValuePair<string, string>("饱腹", "+" + food.satiety));
            if (food.happiness != 0) rows.Add(new KeyValuePair<string, string>("快乐", "+" + food.happiness));
            if (food.exp != 0) rows.Add(new KeyValuePair<string, string>("成长经验", "+" + food.exp));

            if (_effectNone != null) _effectNone.gameObject.SetActive(rows.Count == 0);

            // 行数不足三条时整体垂直居中，不要贴顶留一片空白。
            float contentH = rows.Count == 0 ? 0f
                : rows.Count * EffectRowHeight + (rows.Count - 1) * EffectRowGap;
            float topPad = (EffectBoxHeight - contentH) * 0.5f;

            for (int i = 0; i < _effectRows.Count; i++)
            {
                var row = _effectRows[i];
                if (i >= rows.Count)
                {
                    row.Rect.gameObject.SetActive(false);
                    continue;
                }

                float top = topPad + i * (EffectRowHeight + EffectRowGap);
                float bottom = top + EffectRowHeight;
                row.Rect.offsetMin = new Vector2(24f, -bottom);
                row.Rect.offsetMax = new Vector2(-24f, -top);
                row.Label.text = rows[i].Key;
                row.Value.text = rows[i].Value;
                ApplyEffectIcon(row.Icon, rows[i].Key);
                row.Rect.gameObject.SetActive(true);
            }
        }

        /// <summary>效果列表的一行：名称在左、数值在右，两个 Text 各占满行宽、用对齐分工。</summary>
        private class EffectRow
        {
            public RectTransform Rect;
            public Text Label;
            public Text Value;
            public UnityEngine.UI.Image Icon;
        }

        /// <summary>
        /// 效果类型 → 行首小图标。图标必须是纯白 RGB + alpha（见 <see cref="PetIcons"/>），
        /// 这里按效果染色：饱腹橙、快乐青、成长经验金。
        /// </summary>
        private static void ApplyEffectIcon(UnityEngine.UI.Image img, string effect)
        {
            if (img == null) return;
            switch (effect)
            {
                case "饱腹":
                    img.sprite = PetIcons.Get("bowl");
                    img.color = PetUITheme.SatietyBar;
                    break;
                case "快乐":
                    img.sprite = PetIcons.Get("heart");
                    img.color = PetUITheme.HappinessBar;
                    break;
                default:
                    img.sprite = PetIcons.Get("star");
                    img.color = PetUITheme.Coin;
                    break;
            }
        }

        public void Refresh()
        {
            if (_food == null || _root == null) return;
            int count = _ui.State.CountOf(_food.id);
            _owned.text = $"当前拥有：{count}";
            bool canFeed = count > 0;
            _feedBtn.interactable = canFeed;
            _feedLabel.text = canFeed ? "立即喂食" : "已用完";
            _feedBtn.image.color = canFeed ? PetUITheme.Primary : PetUITheme.DisabledFill;
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void OnFeed()
        {
            if (_food == null) return;
            var food = _food;
            var ui = _ui;

            // 先通知表现层播动画，再改数据 —— 反过来的话动画开始时库存已经扣了，
            // 表现层读到的数量对不上。
            OnFeedRequested?.Invoke(food);

            // 请求飞行期间禁用按钮。不禁的话连点两下会发两个 requestId，
            // 服务端各算一次 —— 表现是一次点击吃掉两份口粮。
            _feedBtn.interactable = false;
            _feedLabel.text = "喂食中…";

            // 喂食逻辑只在 FoodPage.Feed 一处实现，这里转调，
            // 避免两条路径（食品页直接喂 / 详情页喂）结果不一致。
            FoodPage.Feed(ui, food, ok =>
            {
                if (_root == null) return;
                if (ok) { Hide(); return; }

                // 失败（库存为 0 / 没联网 / 服务端拒绝）留在弹窗里，
                // 让玩家同时看到「当前拥有 0」和 toast 的原因。
                // 直接关掉的话玩家只会看到一句"用完了"，不知道为什么。
                Refresh();
            });
        }
    }
}
