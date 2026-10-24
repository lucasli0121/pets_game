using System;
using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 每日签到弹窗。7 天格子 + 一个签到按钮。
    ///
    /// 格子的日期是**最近 7 天（含今天）**，不是「本周一到周日」：
    /// 连续签到是跨周的，按自然周切会把「前天签的」切到上一屏去，
    /// 玩家看到的是「我只签了一天」，实际已经连续三天。
    ///
    /// 已签哪几天由服务端 <c>sync-state</c> 的 <c>checkin.recentDates</c> 下发，
    /// 客户端**不用 streak 推算** —— streak 的定义是「从今天往前连续」，
    /// 今天没签时它是 0，推不出「昨天签了、今天还没签」这种最常见的状态。
    ///
    /// 日期一律按 UTC+8 算，与后台 <c>timeutil.Today()</c> 同一口径。
    /// 拿本地时区算的话，跨时区用户的「今天」会和服务端错开一天，
    /// 表现为「明明签了，格子还是空的」。
    /// </summary>
    public class CheckInPopup
    {
        private PetHomeUI _ui;
        private Image _root;
        private Image _panel;
        private Text _streakText;
        private Text _rewardHint;
        private Button _claimBtn;
        private Text _claimLabel;

        /// <summary>7 个格子的视觉元素，按日期从早到晚。</summary>
        private readonly List<Cell> _cells = new List<Cell>();

        /// <summary>一个格子里需要按状态改色的部分。</summary>
        private class Cell
        {
            public Image Bg;
            public Text Weekday;
            public Text Day;
            public Text Tail;      // 已签显示「已签」，今天未签显示「+100」，其余留空
            public Image Star;     // 已签时显示的星，未签时隐藏
        }

        private const int Days = 7;
        private const float CellW = 84f;
        private const float CellH = 110f;
        private const float CellGap = 8f;
        private const float PanelW = 700f;
        private const float PanelH = 400f;

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        public void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;

            _root = PetUIFactory.NewSolid("CheckInMask", parent, PetUITheme.Scrim, 0f);
            _root.raycastTarget = true;   // 点遮罩关闭
            PetUIFactory.Fill((RectTransform)_root.transform);
            _root.gameObject.SetActive(false);
            var closeOnMask = _root.gameObject.AddComponent<Button>();
            closeOnMask.targetGraphic = _root;
            closeOnMask.onClick.AddListener(Hide);

            _panel = PetUIFactory.NewSolid("Panel", _root.transform, PetUITheme.Card,
                                            PetUITheme.CardRadius);
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(PanelW, PanelH);
            prt.anchoredPosition = Vector2.zero;

            // 挡一层：点面板本身不该穿透到遮罩去关窗
            var blocker = PetUIFactory.NewHitArea("Block", prt);
            PetUIFactory.Fill((RectTransform)blocker.transform);
            blocker.gameObject.AddComponent<Button>();

            var close = PetUIFactory.NewCircleButton(prt, "×", 64f,
                                                      PetUITheme.Line, out var closeTx);
            var crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-18f, -18f);
            closeTx.color = PetUITheme.TextSecondary;
            close.onClick.AddListener(Hide);

            // 标题
            var title = PetUIFactory.NewText("Title", prt, "每日签到", PetUITheme.FontHeading,
                                              PetUITheme.TextPrimary, TextAnchor.MiddleCenter);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(PanelW - 160f, 44f);
            trt.anchoredPosition = new Vector2(0f, -30f);

            // 副标题：连续天数
            _streakText = PetUIFactory.NewText("Streak", prt, "", PetUITheme.FontCaption,
                                                PetUITheme.TextSecondary, TextAnchor.MiddleCenter);
            var srt = (RectTransform)_streakText.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.sizeDelta = new Vector2(PanelW - 160f, 32f);
            srt.anchoredPosition = new Vector2(0f, -80f);

            // 7 个格子
            float totalW = Days * CellW + (Days - 1) * CellGap;
            float leftCenter = -totalW * 0.5f + CellW * 0.5f;
            for (int i = 0; i < Days; i++)
            {
                _cells.Add(BuildCell(prt, leftCenter + i * (CellW + CellGap)));
            }

            // 底部按钮
            _claimBtn = PetUIFactory.NewPillButton(prt, "签到", PetUITheme.Primary,
                                                    PetUITheme.FontHeading, out _claimLabel);
            var brt = (RectTransform)_claimBtn.transform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(1f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.offsetMin = new Vector2(60f, 40f);
            brt.offsetMax = new Vector2(-60f, 40f + PetUITheme.PrimaryButtonHeight);
            _claimBtn.onClick.AddListener(OnClaim);

            // 底部提示：今天没签时说明能拿多少，签过就说下次什么时候来。
            //
            // 用 TextSecondary 不用 TextDisabled（#B8C2CC）：后者压白卡只有 1.81:1，
            // 是一段几乎读不出来的浅灰。TextSecondary（#7B8A9C）是 5.4:1。
            _rewardHint = PetUIFactory.NewText("Hint", prt, "", PetUITheme.FontSmall,
                                                PetUITheme.TextSecondary, TextAnchor.MiddleCenter);
            var hrt = (RectTransform)_rewardHint.transform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0f);
            hrt.pivot = new Vector2(0.5f, 0f);
            hrt.sizeDelta = new Vector2(PanelW - 120f, 28f);
            hrt.anchoredPosition = new Vector2(0f, 12f);
        }

        private Cell BuildCell(RectTransform parent, float centerX)
        {
            var bg = PetUIFactory.NewSolid("Cell", parent, PetUITheme.Line, 14f);
            var rt = (RectTransform)bg.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(CellW, CellH);
            rt.anchoredPosition = new Vector2(centerX, -132f);
            bg.raycastTarget = false;

            var weekday = PetUIFactory.NewText("Weekday", rt, "", PetUITheme.FontSmall,
                                                PetUITheme.TextSecondary, TextAnchor.MiddleCenter);
            var wrt = (RectTransform)weekday.transform;
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 1f);
            wrt.pivot = new Vector2(0.5f, 1f);
            wrt.sizeDelta = new Vector2(CellW - 8f, 26f);
            wrt.anchoredPosition = new Vector2(0f, -10f);

            var day = PetUIFactory.NewText("Day", rt, "", PetUITheme.FontBody,
                                            PetUITheme.TextPrimary, TextAnchor.MiddleCenter);
            var drt = (RectTransform)day.transform;
            drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 1f);
            drt.pivot = new Vector2(0.5f, 1f);
            drt.sizeDelta = new Vector2(CellW - 8f, 34f);
            drt.anchoredPosition = new Vector2(0f, -38f);

            var star = PetIcons.NewIcon("star", rt, new Vector2(30f, 30f), Color.white);
            var stt = (RectTransform)star.transform;
            stt.anchorMin = stt.anchorMax = new Vector2(0.5f, 0f);
            stt.pivot = new Vector2(0.5f, 0f);
            stt.anchoredPosition = new Vector2(0f, 10f);

            var tail = PetUIFactory.NewText("Tail", rt, "", PetUITheme.FontSmall,
                                             PetUITheme.TextSecondary, TextAnchor.MiddleCenter);
            var trt = (RectTransform)tail.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0f);
            trt.pivot = new Vector2(0.5f, 0f);
            trt.sizeDelta = new Vector2(CellW - 6f, 26f);
            trt.anchoredPosition = new Vector2(0f, 10f);

            return new Cell { Bg = bg, Weekday = weekday, Day = day, Tail = tail, Star = star };
        }

        // ------------------------------------------------------------------
        // 显示
        // ------------------------------------------------------------------

        public void Show()
        {
            if (_root == null) return;
            Refresh();
            _root.gameObject.SetActive(true);

            // 打开时先按本地状态画一版，再向后台查一次。
            //
            // 为什么要多这一次：本地状态是启动时 sync-state 带下来的，
            // 玩家把游戏挂在后台跨过零点、或者在别的设备上签过，
            // 打开弹窗看到的还是旧状态 —— 显示「今天已签到」，
            // 点下去服务端却说「今天还没签」。
            //
            // 先画一版再刷新，是为了不让玩家对着空白等网络。
            RefreshFromServer();
        }

        /// <summary>向后台查一次签到状态，回来重画。</summary>
        private void RefreshFromServer()
        {
            var s = _ui != null ? _ui.Session : null;
            if (s == null || !s.Online) return;

            s.RefreshCheckInStatus((ok, msg) =>
            {
                if (!ok)
                {
                    // 查失败不该打扰玩家：本地状态仍然可用，
                    // 只是可能不是最新的。签到按钮自己会兜底。
                    return;
                }
                Refresh();
            });
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        public void Refresh()
        {
            if (_root == null || _ui == null) return;
            var st = _ui.State;

            var today = TodayForGrid(st);
            var signed = new HashSet<string>(st.checkinRecentDates ?? new List<string>());

            // ---- 7 个格子 ----
            for (int i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                var date = today.AddDays(i - (Days - 1));   // 最后一格是今天
                string key = DateKey(date);

                bool isToday = i == Days - 1;
                bool done = signed.Contains(key);

                cell.Weekday.text = WeekdayCn(date);
                cell.Day.text = date.Day.ToString();

                if (done)
                {
                    // 已签：主色底 + 白字 + 星
                    cell.Bg.color = PetUITheme.Primary;
                    cell.Weekday.color = Color.white;
                    cell.Day.color = Color.white;
                    cell.Star.gameObject.SetActive(true);
                    cell.Tail.gameObject.SetActive(false);
                }
                else
                {
                    // 未签：七天每格都标出「+100」。
                    //
                    // 为什么每一格都要写：只标今天那格的话，玩家看到的是
                    // 「只有今天有钱拿」，前几天漏掉的空档读起来像作废了，
                    // 而不是「我少拿了 6 天」。每格都标，漏签的成本才是可见的。
                    //
                    // 今天那格用金黄底 + 白字（最醒目，是当下要点的目标），
                    // 前面漏掉的格子是浅灰底 + 灰字（已成事实，不抢注意力）。
                    // 同一句「+100」靠底色的亮暗分主次，不靠字号。
                    if (isToday)
                    {
                        cell.Bg.color = PetUITheme.Coin;
                        cell.Weekday.color = Color.white;
                        cell.Day.color = Color.white;
                        cell.Tail.color = Color.white;
                    }
                    else
                    {
                        cell.Bg.color = PetUITheme.Line;
                        cell.Weekday.color = PetUITheme.TextSecondary;
                        cell.Day.color = PetUITheme.TextSecondary;
                        cell.Tail.color = PetUITheme.TextSecondary;
                    }

                    cell.Star.gameObject.SetActive(false);
                    cell.Tail.gameObject.SetActive(true);
                    cell.Tail.text = "+" + st.checkinReward;
                }
            }

            // ---- 副标题 ----
            if (st.checkinClaimedToday)
            {
                _streakText.text = st.checkinStreak > 1
                    ? $"已连续签到 {st.checkinStreak} 天"
                    : "今天已签到";
            }
            else
            {
                // 今天没签时 streak 恒为 0（后台定义），只能提「昨天之前」的连续性
                _streakText.text = "今天还没签到";
            }

            // ---- 按钮 ----
            //
            // 用 canClaim 而不是 claimedToday：两者在当前配置下等价
            // （daily_checkin 有 (user_id, date) 唯一索引，一天一条），
            // 但后台把每日上限配成 >1 时两者会分开 —— 「今天签过了」不等于
            // 「今天不能再领」。以服务端给的 canClaim 为准。
            bool online = _ui.Session != null && _ui.Session.Online;
            bool canClaim = st.checkinCanClaim;
            _claimBtn.interactable = online && canClaim;
            _claimBtn.image.color = (online && canClaim) ? PetUITheme.Primary : PetUITheme.DisabledFill;
            _claimLabel.text = !online ? "未联网"
                             : canClaim ? $"签到 +{st.checkinReward}"
                             : "今天已签到";

            _rewardHint.text = !online
                ? "连上服务端才能签到"
                : canClaim
                    ? $"签到可得 {st.checkinReward} 积分"
                    : "明天再来，连续签到更有仪式感";
        }

        private void OnClaim()
        {
            if (_ui == null) return;
            var s = _ui.Session;
            if (s == null || !s.Online)
            {
                _ui.ShowToast("没连上服务端，暂时不能签到");
                return;
            }

            // 防连点：同一次点击只发一个 requestId
            _claimBtn.interactable = false;
            _claimLabel.text = "签到中…";

            s.ClaimCheckIn((ok, msg) =>
            {
                if (_root == null) return;
                if (!ok)
                {
                    _ui.ShowToast(string.IsNullOrEmpty(msg) ? "签到失败" : msg);
                    Refresh();
                    return;
                }
                _ui.NotifyStateChanged();
                _ui.ShowToast(msg);
                Refresh();
            });
        }

        // ------------------------------------------------------------------
        // 日期工具（UTC+8，与后台 timeutil 同口径）
        // ------------------------------------------------------------------

        public static DateTime TodayCst()
        {
            // 不用 DateTime.Now：它跟设备时区走，跨时区用户会和服务端错开一天。
            return DateTime.UtcNow.AddHours(8).Date;
        }

        /// <summary>
        /// 画格子用的「今天」。
        ///
        /// 优先用服务端给的日期（<c>checkin.date</c>），拿不到才退回本地算 UTC+8。
        ///
        /// 为什么优先服务端：设备时间可被用户改。改到明天，客户端就以为
        /// 「今天没签」而多弹一次窗，还会把格子画错一天。
        /// </summary>
        private static DateTime TodayForGrid(PetRuntimeState st)
        {
            if (st != null && !string.IsNullOrEmpty(st.checkinServerDate))
            {
                if (DateTime.TryParseExact(st.checkinServerDate, "yyyy-MM-dd",
                                           System.Globalization.CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.None,
                                           out var d))
                {
                    return d.Date;
                }
            }
            return TodayCst();
        }

        public static string DateKey(DateTime d) => d.ToString("yyyy-MM-dd");

        private static string WeekdayCn(DateTime d)
        {
            switch (d.DayOfWeek)
            {
                case DayOfWeek.Monday: return "一";
                case DayOfWeek.Tuesday: return "二";
                case DayOfWeek.Wednesday: return "三";
                case DayOfWeek.Thursday: return "四";
                case DayOfWeek.Friday: return "五";
                case DayOfWeek.Saturday: return "六";
                default: return "日";
            }
        }
    }
}
