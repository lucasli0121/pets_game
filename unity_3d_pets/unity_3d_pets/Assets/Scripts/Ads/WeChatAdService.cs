using System;
using UnityEngine;

namespace PetV1.Ads
{
    /// <summary>
    /// 微信小游戏的广告实现。**现在是壳** —— 等小游戏转换（装
    /// <c>com.tencent.wechat.minigame</c> 插件）之后填 .jslib 桥。
    ///
    /// 要接的三件事（真机联调时逐个验）：
    /// ① <c>wx.createRewardedVideoAd({adUnitId})</c> 拿实例，
    ///    <c>ad.onClose(res => res.isEnded)</c> 才是「看完」的判据 ——
    ///    提前关 isEnded=false，**不能发奖**；
    /// ② <c>ad.onLoad</c> / <c>ad.onError</c>：拉取失败要让 UI 走失败分支，
    ///    不能卡在一个永远不回调的播放中；
    /// ③ 插屏用 <c>wx.createInterstitialAd</c>，onClose 同样有 isEnded。
    ///
    /// 为什么现在只留壳：manifest.json 里没有微信小游戏插件，
    /// 硬写 <c>[DllImport("__Internal")]</c> 在 WebGL 之外的平台会直接崩。
    /// 由 <see cref="PetAdService"/> 按平台选实现，编辑器永远走假实现。
    /// </summary>
    public class WeChatAdService : IAdService
    {
        public bool Available => Application.platform == RuntimePlatform.WebGLPlayer;

        public void PlayInterstitial(Action<bool> onClosed)
        {
            Debug.LogWarning("[PetV1] 微信插屏广告尚未接入（等小游戏转换时补 .jslib 桥）。");
            onClosed?.Invoke(false);
        }

        public void PlayRewarded(string placement, Action<bool> onClosed)
        {
            Debug.LogWarning("[PetV1] 微信激励视频尚未接入（等小游戏转换时补 .jslib 桥）。");
            onClosed?.Invoke(false);
        }
    }
}
