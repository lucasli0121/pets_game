using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Interaction
{
    /// <summary>
    /// 标记 Collider 属于哪个身体区域。挂在头部/身体等 Collider 的 GameObject 上，
    /// GestureRecognizer 通过它把命中点翻译成 <see cref="PetRegion"/>。
    /// 换模型时只要重新挂这些标记，触摸系统无需改动。
    /// </summary>
    public class PetRegionCollider : MonoBehaviour
    {
        [SerializeField] private PetRegion region = PetRegion.Body;
        public PetRegion Region => region;
    }
}
