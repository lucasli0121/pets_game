using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Core
{
    /// <summary>
    /// 开发期分类日志通道。需求文档第十六节定义的五个通道原样保留，
    /// 另外补了 System / State 两个便于排查启动与状态迁移问题。
    /// 发布前把 <see cref="PetLog.Channels"/> 置为 <see cref="PetLogChannel.None"/> 即可整体静默。
    /// </summary>
    [Flags]
    public enum PetLogChannel
    {
        None = 0,
        System = 1 << 0,
        Touch = 1 << 1,
        Gesture = 1 << 2,
        Emotion = 1 << 3,
        State = 1 << 4,
        Behavior = 1 << 5,
        Animation = 1 << 6,
        All = ~0,
    }

    /// <summary>
    /// 唯一允许打印运行期日志的入口。所有子系统只调用这里，不直接用 <see cref="Debug"/>。
    /// 好处是「哪类日志可以关」这件事集中在一处，DebugPanel 也能读到同一份历史。
    /// </summary>
    public static class PetLog
    {
        public const int HistoryCapacity = 24;

        public static PetLogChannel Channels = PetLogChannel.All;

        /// <summary>是否打印 key-value 明细（distance / speed / trajectory 等）。关掉后只留结论行。</summary>
        public static bool Verbose = true;

        private static readonly Queue<string> s_History = new Queue<string>(HistoryCapacity);
        private static readonly List<string> s_HistorySnapshot = new List<string>(HistoryCapacity);

        /// <summary>任何一条日志落盘后触发，DebugPanel 用它刷新「Last Event」。</summary>
        public static event Action<PetLogChannel, string> OnLogged;

        public static IReadOnlyList<string> History
        {
            get
            {
                lock (s_History)
                {
                    s_HistorySnapshot.Clear();
                    foreach (var line in s_History) s_HistorySnapshot.Add(line);
                    return s_HistorySnapshot;
                }
            }
        }

        public static void ClearHistory()
        {
            lock (s_History) s_History.Clear();
        }

        public static bool Enabled(PetLogChannel channel) => (Channels & channel) != 0;

        public static void System(string message) => Write(PetLogChannel.System, message);
        public static void Touch(string message) => Write(PetLogChannel.Touch, message);
        public static void Gesture(string message) => Write(PetLogChannel.Gesture, message);
        public static void Emotion(string message) => Write(PetLogChannel.Emotion, message);
        public static void State(string message) => Write(PetLogChannel.State, message);
        public static void Behavior(string message) => Write(PetLogChannel.Behavior, message);
        public static void Animation(string message) => Write(PetLogChannel.Animation, message);

        /// <summary>
        /// 带 key-value 明细的一行日志，例如
        /// <c>PetLog.Gesture("recognized", PetLog.Kv("gesture", "HEAD_TAP", "fingerCount", 1))</c>
        /// 输出形如 <c>[Gesture] recognized gesture=HEAD_TAP, fingerCount=1</c>。
        /// </summary>
        public static void Verb(PetLogChannel channel, string message, params string[] keyValues)
        {
            if (!Enabled(channel)) return;
            Write(channel, Append(message, keyValues));
        }

        public static void Gesture(string message, params string[] keyValues) => Verb(PetLogChannel.Gesture, message, keyValues);
        public static void Touch(string message, params string[] keyValues) => Verb(PetLogChannel.Touch, message, keyValues);
        public static void Emotion(string message, params string[] keyValues) => Verb(PetLogChannel.Emotion, message, keyValues);
        public static void Behavior(string message, params string[] keyValues) => Verb(PetLogChannel.Behavior, message, keyValues);
        public static void Animation(string message, params string[] keyValues) => Verb(PetLogChannel.Animation, message, keyValues);
        public static void State(string message, params string[] keyValues) => Verb(PetLogChannel.State, message, keyValues);

        /// <summary>把 key, value, key, value 序列拼成 <c>k=v, k=v</c>。value 为 null 时输出 -。</summary>
        public static string[] Kv(params object[] keyThenValue)
        {
            if (keyThenValue == null || keyThenValue.Length == 0) return Array.Empty<string>();
            var pairs = new List<string>((keyThenValue.Length + 1) / 2);
            for (int i = 0; i + 1 < keyThenValue.Length; i += 2)
            {
                string key = Convert.ToString(keyThenValue[i]);
                object value = keyThenValue[i + 1];
                pairs.Add(key + "=" + (value == null ? "-" : Convert.ToString(value)));
            }
            return pairs.ToArray();
        }

        private static string Append(string message, string[] keyValues)
        {
            if (!Verbose || keyValues == null || keyValues.Length == 0) return message;
            return message + " " + string.Join(", ", keyValues);
        }

        private static void Write(PetLogChannel channel, string message)
        {
            if (!Enabled(channel)) return;
            string line = "[" + channel + "] " + message;
            Debug.Log(line, PetLogAnchor.Anchor);
            lock (s_History)
            {
                if (s_History.Count >= HistoryCapacity) s_History.Dequeue();
                s_History.Enqueue(line);
            }
            OnLogged?.Invoke(channel, line);
        }
    }

    /// <summary>日志锚点，方便在 Console 里点开定位到场景里的宠物对象。</summary>
    public static class PetLogAnchor
    {
        public static Transform Anchor { get; set; }
    }
}