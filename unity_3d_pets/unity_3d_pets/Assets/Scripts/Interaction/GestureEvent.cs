using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Interaction
{
    using PetV1.Pet;

    /// <summary>宠物身体区域。需求文档 6.1：第一阶段先整体 Collider，后续按骨骼拆分。</summary>
    public enum PetRegion
    {
        Unknown = 0,
        Head = 1,
        Nose = 2,
        Body = 3,
        Back = 4,
        Tail = 5,
        Leg = 6,
    }

    /// <summary>
    /// 手势类型。需求文档 2.3 要求的第一阶段手势集合。
    /// 屏幕级手势（SCREEN_*）与宠物级手势分开，避免后续扑屏逻辑污染触摸判定。
    /// </summary>
    public enum GestureType
    {
        None = 0,

        // ---- 单指 ----
        Tap = 1,
        DoubleTap = 2,
        LongPress = 3,
        Stroke = 4,       // 慢速拖动（抚摸）
        FastSwipe = 5,    // 快速划动

        // ---- 多指 ----
        MultiTap = 6,
        MultiScratch = 7, // 2+ 指短距离高速反复移动（挠屏）
        MultiHold = 8,

        // ---- 屏幕级（不要求命中宠物）----
        ScreenTap = 20,
        ScreenSwipe = 21,
        ScreenScratch = 22,
        ScreenMultiTouch = 23,
    }

    /// <summary>手指阶段。统一封装 Touch 与 Mouse，便于在编辑器里用鼠标模拟单指。</summary>
    public enum PointerPhase
    {
        Began = 0,
        Moved = 1,
        Stationary = 2,
        Ended = 3,
        Canceled = 4,
    }

    /// <summary>结构化的手势事件。需求文档 2.3 的 JSON 输出即由此对象序列化而来。</summary>
    [Serializable]
    public class GestureEvent
    {
        public GestureType gesture = GestureType.None;
        public PetRegion targetRegion = PetRegion.Unknown;
        public int fingerCount = 1;
        public int durationMs;
        public float distance;          // 屏幕像素
        public float speed;             // 像素/秒
        public float averageSpeed;
        public float peakSpeed;
        public bool hitPet;
        public Vector2 startPosition;
        public Vector2 currentPosition;
        public Vector2 delta;
        public List<Vector2> trajectory = new List<Vector2>();
        public int reversalCount;        // 方向反转次数，用于区分「抚摸」与「来回挠」

        /// <summary>DebugPanel 显示用的一行摘要。</summary>
        public override string ToString()
        {
            return $"{gesture} region={targetRegion} fingers={fingerCount} " +
                   $"dur={durationMs}ms dist={distance:F0}px speed={averageSpeed:F0}px/s";
        }

        /// <summary>需求文档 2.3 / 6.3 的 JSON 结构。</summary>
        public string ToJson()
        {
            var sb = new System.Text.StringBuilder(192);
            sb.Append("{\"gesture\":\"").Append(gesture).Append('"');
            sb.Append(",\"target_region\":\"").Append(targetRegion).Append('"');
            sb.Append(",\"finger_count\":").Append(fingerCount);
            sb.Append(",\"duration_ms\":").Append(durationMs);
            sb.Append(",\"distance\":").Append(Mathf.RoundToInt(distance));
            sb.Append(",\"speed\":").Append(Mathf.Round(averageSpeed * 1000f) / 1000f);
            sb.Append(",\"peak_speed\":").Append(Mathf.Round(peakSpeed * 1000f) / 1000f);
            sb.Append(",\"hit_pet\":").Append(hitPet ? "true" : "false");
            sb.Append(",\"reversals\":").Append(reversalCount);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
