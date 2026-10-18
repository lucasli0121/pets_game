using System;
using UnityEngine;

namespace PetV1.Data
{
    /// <summary>
    /// 单条「手势 → 养成产出」的映射。对应后台管理端新增的
    /// <c>config_interaction_reward</c> 表，一行一条记录。
    ///
    /// PRD 4.3 那张表（点击+1、抚摸+2、多指+5、快划+5）只是**默认值示例**，
    /// 实际数值全部由后台下发，客户端不得硬编码。
    ///
    /// 为什么要 <see cref="expPerCount"/> 而不是只配一个 expDelta：
    /// 「点击 5 次 +1 经验」是**计数型**规则，不是每次给 0.2。
    /// 用浮点增量累加会有精度漂移，而且重试/重复提交时不好幂等。
    /// 服务端按 (user_id, gesture_type) 记累计次数，满 N 次发 1 经验。
    /// </summary>
    [Serializable]
    public class InteractionRewardEntry
    {
        [Tooltip("对应的手势类型（与 GestureType 枚举名一致，如 Tap / Stroke / FastSwipe）")]
        public string gestureType = "Tap";

        [Tooltip("快乐值增量。整数，可为 0 或负数（负数表示该行为消耗快乐值）。")]
        public int happinessDelta;

        [Tooltip("饱腹增量。一般只有进食类为正，互动类填 0。")]
        public int satietyDelta;

        [Tooltip("经验增量。整数，可为 0 或负数。与 expPerCount 同时生效。")]
        public int expDelta;

        [Tooltip("每 N 次该手势发 1 点经验。0 = 不按次数给；1 = 每次都给。默认示例里「点击 5 次 +1」即填 5。")]
        public int expPerCount;

        [Tooltip("是否启用。关掉相当于运营下架这条规则。")]
        public bool enabled = true;

        public InteractionRewardEntry() { }

        public InteractionRewardEntry(string gestureType, int happinessDelta,
                                     int satietyDelta = 0, int expDelta = 0, int expPerCount = 0)
        {
            this.gestureType = gestureType;
            this.happinessDelta = happinessDelta;
            this.satietyDelta = satietyDelta;
            this.expDelta = expDelta;
            this.expPerCount = expPerCount;
        }

        public string Compact()
        {
            string s = $"{gestureType}: happy{happinessDelta:+#;-#;0}";
            if (satietyDelta != 0) s += $" sat{satietyDelta:+#;-#;0}";
            if (expDelta != 0) s += $" exp{expDelta:+#;-#;0}";
            if (expPerCount > 0) s += $" exp/每{expPerCount}次";
            return s;
        }
    }
}