using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 场景选择页。展板第 7 页。
    ///
    /// 展板是四张缩略图卡片 + 徽标（使用中 / 购买 / 价格）。本实现用
    /// <see cref="PetSceneLibrary"/> 里的实际条目渲染，缩略图用背景图本身裁切 ——
    /// asset 里有 scene_home_day.jpg 等原图，直接当缩略图用比另做一套 UI 图真实。
    ///
    /// 「解锁」走后台 /api/v1/scene/unlock（花积分），「切换」走 /api/v1/scene/use。
    /// 原型阶段在本地直接改 state.unlockedScenes 与 activeScene，
    /// 接接口后改成发请求 + 用返回值覆盖。
    /// </summary>
    public class ScenePage : PetPageBase
    {
        private PetHomeUI _ui;
        private RectTransform _grid;
        private Text _emptyHint;
        private ConfirmPopup _unlockConfirm;

        public override void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;
            BuildShell("更换场景", parent);

            _grid = (RectTransform)MakeScrollable().transform;

            _emptyHint = PetUIFactory.NewText("Empty", Root.transform, "场景库为空",
                                              PetUITheme.FontBody, PetUITheme.TextDisabled,
                                              TextAnchor.MiddleCenter);
            var ert = (RectTransform)_emptyHint.transform;
            ert.anchorMin = Vector2.zero;
            ert.anchorMax = Vector2.one;
            ert.offsetMin = new Vector2(PetUITheme.PageMargin, PetUITheme.NavBarHeight + PetUITheme.SafeBottom + 12f);
            ert.offsetMax = new Vector2(-PetUITheme.PageMargin, -PetUITheme.TopBarHeight - 100f);
            _emptyHint.gameObject.SetActive(false);

            _unlockConfirm = new ConfirmPopup();
            _unlockConfirm.Build(Root.transform, ui);
        }

        public override void Refresh()
        {
            if (_grid == null) return;
            PetUIUtil.ClearChildren(_grid);

            var lib = _ui.Library;
            if (lib == null || lib.scenes == null || lib.scenes.Count == 0)
            {
                if (_emptyHint != null) _emptyHint.gameObject.SetActive(true);
                return;
            }

            // 有内容就必须把提示关回去。只在空分支里 SetActive(true) 是不够的：
            // 启动时 library 还没赋值，Refresh 先跑一轮空判把提示打开，
            // 之后库到了也不会有第二次「空」分支来关它 —— 表现是列表正常、
            // 「场景库为空」却一直挂在屏幕上。
            if (_emptyHint != null) _emptyHint.gameObject.SetActive(false);

            foreach (var entry in lib.scenes)
            {
                if (entry == null) continue;
                BuildCard(entry).transform.SetParent(_grid.transform, false);
            }

            RelayoutNow(_grid);
        }

        private GameObject BuildCard(PetSceneLibrary.SceneEntry entry)
        {
            var state = _ui.State;
            bool unlocked = state.IsSceneUnlocked(entry.sceneId);
            bool inUse = state.activeScene == entry.sceneId;

            var card = PetUIFactory.NewSolid("Card", _grid.transform, PetUITheme.Card,
                                              PetUITheme.CardRadius);
            var le = card.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 220f;

            var crt = (RectTransform)card.transform;
            PetUIFactory.Fill(crt, PetUITheme.CardPadding * 0.5f);

            // 缩略图：用背景图本体
            var thumb = PetUIFactory.NewSolid("Thumb", crt, new Color(0.88f, 0.92f, 0.96f),
                                               PetUITheme.CardRadius);
            var trt = (RectTransform)thumb.transform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(0f, 1f);
            trt.pivot = new Vector2(0f, 0.5f);
            trt.sizeDelta = new Vector2(190f, 0f);
            trt.anchoredPosition = Vector2.zero;
            thumb.raycastTarget = false;

            var thumbImg = thumb.GetComponent<Image>();
            if (entry.background != null)
            {
                thumbImg.sprite = Sprite.Create(entry.background, new Rect(0, 0, entry.background.width, entry.background.height),
                                                new Vector2(0.5f, 0.5f), 100f);
                thumbImg.type = Image.Type.Simple;
                thumbImg.preserveAspect = true;
            }
            else
            {
                // 后台有这张场景但本地没图（sceneUrl 未下载完）。
                // 画占位色块 + 文案说明，而不是留空白或崩掉。
                var ph = PetUIFactory.NewText("Ph", thumb.transform, "待下载", PetUITheme.FontSmall,
                                               PetUITheme.TextSecondary, TextAnchor.MiddleCenter);
                PetUIFactory.Fill((RectTransform)ph.transform);
            }

            // 右侧文字与按钮
            var info = PetUIFactory.NewPanel("Info", crt);
            var irt = (RectTransform)info.transform;
            PetUIFactory.Fill(irt);
            irt.offsetMin = new Vector2(210f, 0f);
            irt.offsetMax = new Vector2(0f, 0f);

            var nameTx = PetUIFactory.NewText("Name", irt, entry.displayName,
                                              PetUITheme.FontBody, PetUITheme.TextPrimary,
                                              TextAnchor.UpperLeft);
            var nrt = (RectTransform)nameTx.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0f, 1f);
            nrt.offsetMin = new Vector2(0f, -44f);
            nrt.offsetMax = new Vector2(0f, 0f);

            // 布局实测备注。放在这里是有意的：换图的人需要知道
            // 这张图的地面线是怎么量的。展板上没有这个信息，
            // 但它是「换模型/换图」时最容易出错的地方。
            var lay = entry.ResolveLayout();
            var noteTx = PetUIFactory.NewText("Note", irt,
                $"可视半高 {lay.orthographicSize:F1} m　俯角 {lay.cameraPitch:F0}°\n" +
                $"狗脚线 {lay.dogGroundLine:P0}　地板最远端 {lay.floorFarLine:P0}",
                PetUITheme.FontSmall, PetUITheme.TextSecondary, TextAnchor.UpperLeft);
            var ort = (RectTransform)noteTx.transform;
            ort.anchorMin = new Vector2(0f, 1f);
            ort.anchorMax = new Vector2(1f, 1f);
            ort.pivot = new Vector2(0f, 1f);
            ort.offsetMin = new Vector2(0f, -110f);
            ort.offsetMax = new Vector2(0f, -50f);

            // 状态徽标 + 按钮
            var badgeText = inUse ? "使用中"
                          : unlocked ? "进入"
                          : $"{entry.price} 积分解锁";
            Color badgeFill = inUse ? PetUITheme.Success
                                : unlocked ? PetUITheme.Primary
                                : PetUITheme.Coin;

            var badge = PetUIFactory.NewPillButton(irt, badgeText, badgeFill,
                                                   PetUITheme.FontSmall, out _);
            var brt = (RectTransform)badge.transform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(0f, 0f);
            brt.pivot = new Vector2(0f, 0f);      // 贴在卡片左下，不是居中
            brt.sizeDelta = new Vector2(220f, 62f);
            brt.anchoredPosition = Vector2.zero;
            badge.interactable = !inUse;

            var captured = entry;
            badge.onClick.AddListener(() => OnBadgeClick(captured, unlocked, inUse));
            return card.gameObject;
        }

        private void OnBadgeClick(PetSceneLibrary.SceneEntry entry, bool unlocked, bool inUse)
        {
            if (inUse) return;

            // 解锁与切换都要发请求，离线时直接拦掉。
            // 不拦的话解锁会走本地扣积分，下次 sync-state 被服务端覆盖回来 ——
            // 表现是「解锁了但重进游戏又锁上了」，而且积分也退回来了，看起来像灵异事件。
            var session = _ui.Session;
            bool online = session != null && session.Online;

            if (unlocked)
            {
                if (!online)
                {
                    _ui.ShowToast("没连上服务端，暂时不能切换场景");
                    return;
                }
                // 成功提示由 SwitchScene 内部按服务端结果决定，这里不再重复弹
                _ui.SwitchScene(entry.sceneId, () => { });
                return;
            }

            var state = _ui.State;
            if (state.points < entry.price)
            {
                _ui.ShowToast($"积分不足，还差 {entry.price - state.points}");
                return;
            }
            if (!online)
            {
                _ui.ShowToast("没连上服务端，暂时不能解锁");
                return;
            }

            // 解锁需要确认 —— 扣积分是不可逆的。
            // 用 Toast 级的提示不够（点一下就没了，来不及看清），
            // 所以复用确认弹窗。
            _unlockConfirm.ShowUnlockScene(entry);
        }
    }
}
