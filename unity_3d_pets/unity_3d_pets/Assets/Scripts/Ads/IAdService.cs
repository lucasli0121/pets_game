using System;

namespace PetV1.Ads
{
    /// <summary>
    /// 广告播放能力。客户端只依赖这个接口，不依赖任何平台的 SDK。
    ///
    /// 为什么要抽象：激励视频只有微信小游戏里有（<c>wx.createRewardedVideoAd</c>），
    /// Unity 编辑器里调它直接抛异常。抽象之后：
    /// - 编辑器/PC 用 <see cref="EditorAdService"/> 假实现，业务闭环（发奖、余额刷新、
    ///   次数上限提示）现在就能开发和自测；
    /// - 微信小游戏用 <see cref="WeChatAdService"/>（走 .jslib 桥），等小游戏转换时再填；
    /// - 两端共用同一套业务代码，不会出现「编辑器能跑、真机跑不通」。
    /// </summary>
    public interface IAdService
    {
        /// <summary>当前环境能不能放广告。false 时 UI 不该放广告入口。</summary>
        bool Available { get; }

        /// <summary>
        /// 播插屏广告（可跳过，无奖励）。
        /// </summary>
        /// <param name="onClosed">true = 看到底，false = 跳过或播放失败。</param>
        void PlayInterstitial(Action<bool> onClosed);

        /// <summary>
        /// 播激励视频。看完才发奖，提前关不给。
        /// </summary>
        /// <param name="placement">广告位标识，给后台对账用。</param>
        /// <param name="onClosed">true = 看完（isEnded），false = 提前关闭/加载失败。</param>
        void PlayRewarded(string placement, Action<bool> onClosed);
    }
}
