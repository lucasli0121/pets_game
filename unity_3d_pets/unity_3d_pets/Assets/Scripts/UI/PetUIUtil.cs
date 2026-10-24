using UnityEngine;

namespace PetV1.UI
{
    /// <summary>UI 构建过程中的通用小工具。</summary>
    public static class PetUIUtil
    {
        /// <summary>
        /// 立刻销毁并清空所有子节点。
        ///
        /// **必须用 DestroyImmediate，不能用 Object.Destroy。**
        /// Object.Destroy 在帧末才生效，紧接着重建列表的话，
        /// 上一批子节点还挂在 transform 上，表现为「切一次分类行数翻一倍」。
        /// 这个坑只在运行时点几下才复现，读代码看不出来。
        ///
        /// Edit 模式下 Object.Destroy 本来就不该用（会报「不在 Play 模式」）。
        /// </summary>
        public static void ClearChildren(Transform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                Object.DestroyImmediate(child.gameObject);
            }
        }
    }
}
