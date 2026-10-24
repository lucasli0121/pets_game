using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 宠物主场景页（底导第一个页签）。
    ///
    /// **这一页刻意几乎什么都不画。** 展板第 2 页「主场景」的结构是：
    /// 3D 客厅占满整屏，头顶一条悬浮状态栏，底部是快捷入口。
    /// 顶栏与底导都由 <see cref="PetHomeUI"/> 统一建（跨页签共用一套），
    /// 所以这一页只负责一件事：**在场景底部放快捷入口**。
    ///
    /// 之前这一页把等级条、状态条、性格入口从顶栏一路铺到 320 高的立绘占位，
    /// 而宠物页的底板是全透明的（要露出 3D狗），
    /// 结果所有卡片直接浮在客厅木地板上：文字和地板纹路撞在一起，狗只露个头。
    /// 这些数据现在收在顶栏点开的两个浮层里（<see cref="PetLevelPopup"/> /
    /// <see cref="PetStatusPopup"/>）。
    ///
    /// **快捷入口按需显隐**：只在宠物饿的时候才浮出「食饱」入口。
    /// 判据是后台下发的 <c>satietyState</c>，不是客户端比阈值——
    /// 阈值是可配的（<c>config_state_threshold</c>），客户端抄一份必然不一致。
    /// 「训练」不做：后台没有独立的训练系统（只有 <c>pet/interaction</c> 互动）。
    /// </summary>
    public class PetGrowthPage : PetPageBase
    {
        private PetHomeUI _ui;
        private Button _feedBtn;

        public override void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;

            // 不用 BuildShell()：它会建标题与铺满的内容容器，
            // 而这一页要的就是「除了快捷入口什么都没有」。
            // Root 仍然要建 —— SwitchTab 靠它做显隐，RelayoutTree 也从它开始遍历。
            Root = PetUIFactory.NewPanel(GetType().Name, parent);

            // 「食饱」快捷入口。放在场景底部、底导之上，靠右——
            // 展板把它放在右下角，避开狗（狗在画面中央偏左）。
            _feedBtn = PetUIFactory.NewIconPillButton(
                Root.transform, "bowl", "食饱",
                132f, PetUITheme.QuickActionDiameter,
                PetUITheme.Glass, PetUITheme.SatietyBar, PetUITheme.TextPrimary,
                out _);

            var frt = (RectTransform)_feedBtn.transform;
            frt.anchorMin = frt.anchorMax = new Vector2(1f, 0f);
            frt.pivot = new Vector2(1f, 0f);
            frt.anchoredPosition = new Vector2(
                -PetUITheme.PageMargin,
                PetUITheme.SafeBottom + PetUITheme.NavBarHeight + 40f);

            _feedBtn.onClick.AddListener(OnFeedClicked);
            _feedBtn.gameObject.SetActive(false);
        }

        private void OnFeedClicked()
        {
            // 饱腹正常时入口是隐藏的，这里再判一次是为了防「喂食成功后
            // 按钮还没隐藏完就被点到」—— 那一次点击会把状态又改回去。
            if (!State.NeedsFood)
            {
                _feedBtn.gameObject.SetActive(false);
                return;
            }
            _ui.SwitchTab(HomeTab.Food);
        }

        /// <summary>
        /// 状态变了就重判显隐。
        /// 喂饱之后后台会把 satietyState 改回 normal，这里跟着把入口收起来。
        /// </summary>
        public override void Refresh()
        {
            if (_feedBtn == null || _ui == null) return;
            _feedBtn.gameObject.SetActive(State.NeedsFood);
        }

        private PetRuntimeState State => _ui.State;
    }
}
