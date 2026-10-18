using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Model
{
    /// <summary>
    /// 场景定义：一张 2D 背景图 + 3D 宠物。PRD 4.5「场景表现形态（技术约束，2026-10-18 定）」。
    ///
    /// 为什么不做 3D 场景：小游戏首包上限 4MB，
    /// 完整 3D 房间（墙体/地板/家具/光照贴图）单场景就超过 1.5MB，三个场景不可行。
    /// 2D 背景单张可压到 200–500KB —— 实测三张合计 880KB。
    ///
    /// 背景图硬约束（PRD 4.5）：
    /// - 竖屏 1080×1920，压缩后单张 ≤ 500KB
    /// - 宠物为 3D，放在背景前方的地面活动区域
    /// - 相机固定，正交投影（避免远近变化导致与背景比例失调）
    /// - 宠物脚下要有跟随移动的椭圆半透明假阴影，否则会显得漂浮
    /// - 背景图光源统一左上，与场景主光方向一致
    /// - 宠物活动范围限定在地面区域，不进入家具位置；V1 不做背景拆层遮挡
    /// - 食盆、水碗等道具用 2D 图标或纯 UI 表现，不建 3D 模型
    /// </summary>
    [CreateAssetMenu(fileName = "PetSceneLibrary",
                     menuName = "PetV1/场景库（2D背景+3D宠物）")]
    public class PetSceneLibrary : ScriptableObject
    {
        [Header("相机（正交投影，全场景共用）")]
        [Tooltip("正交相机的可视高度（世界单位）。\n" +
                 "背景图 1920px 高，所以 1 世界单位 = orthoSize*2/1920 × 屏高。\n" +
                 "**这个值决定狗占画面多少**：狗 0.76m 高，orthoSize=1.9 时约占 20%。")]
        public float orthographicSize = 1.9f;

        [Tooltip("相机俯角（度）。0=水平看，正值=向下看。\n" +
                 "背景是俯视构图，需要俯视才能看到地面。")]
        [Range(0f, 45f)] public float cameraPitch = 12f;

        [Tooltip("相机与宠物的水平距离（米）。正交下不影响大小，只影响裁剪范围。")]
        public float cameraDistance = 3.2f;

        [Header("地面平面")]
        [Tooltip("地面在正交视图里的归一化纵向位置（0=画面底，1=画面顶）。\n" +
                 "三张背景图的地面线都在 0.42 附近（下方约 58% 是地板/草地）。\n" +
                 "**相机会按这个值自动反推高度**，不要单独设 cameraY。")]
        [Range(0.05f, 0.95f)] public float groundLine = 0.42f;

        [Tooltip("宠物可活动区域（世界单位）。宠物不进入家具位置。")]
        public Vector2 wanderArea = new Vector2(1.5f, 0.9f);

        /// <summary>
        /// 按地面线反推相机的世界高度与位置。
        ///
        /// 为什么不能写死相机位置：正交相机里，屏幕纵向位置只跟相机中心
        /// 的世界高度有关。地面线要落在 groundLine，就要求
        /// 相机中心 - 地面 = (0.5 - groundLine) × 可视高度。
        /// 写死位置的话，改 orthographicSize 或 groundLine 就会对不上。
        ///
        /// <paramref name="groundWorldY"/> 要传**宠物脚底的实际世界 Y**，
        /// 不能假设是 0 —— 模型 bounds.min.y 往往是 -0.01 这种小负值
        /// （蒙皮网格原点不在脚底），差 1cm 在画面上就是几个百分点。
        /// 实测第一版按 y=0 算，狗脚落在画面 11% 而地面线是 42%，差了 31%。
        /// </summary>
        public Vector3 ComputeCameraPosition(float groundWorldY)
        {
            // 目标：地面落在画面 groundLine 高度。
            // 正交相机俯视时，相机上移 dy，地面在画面里上移 dy*cos(pitch)/(2*orthoSize)。
            // 少了 cos(pitch) 这一项，俯角越大偏得越多
            //（实测俯角 12° 时地面落在画面 11.2%，而目标是 42%）。
            float cos = Mathf.Cos(cameraPitch * Mathf.Deg2Rad);
            float rise = (0.5f - groundLine) * orthographicSize * 2f;
            float camY = groundWorldY + rise / Mathf.Max(1e-4f, cos)
                       + Mathf.Tan(cameraPitch * Mathf.Deg2Rad) * cameraDistance;
            return new Vector3(0f, camY, -cameraDistance);
        }


        [Header("场景列表")]
        public List<SceneEntry> scenes = new List<SceneEntry>();

        [Serializable]
        public class SceneEntry
        {
            [Tooltip("场景 id，与后台 config_scene.id 一致。")]
            public string sceneId = "home_day";

            [Tooltip("后台下发的 displayNameKey。UI 用 key 取文案，不硬编码。")]
            public string displayNameKey = "scene_home_day";

            [Tooltip("背景图。必须 1080×1920，导入设置见 ImportSettings 说明。")]
            public Texture2D background;

            [Tooltip("解锁所需积分。0 = 默认拥有（免费）。")]
            public int price = 0;

            [Tooltip("主光方向（度）。必须与背景图的光源方向一致，否则宠物会有两套影子。\n" +
                     "三张背景图光源都在左上，所以默认 (40, -30, 0)。")]
            public Vector3 lightEuler = new Vector3(40f, -30f, 0f);

            [Tooltip("主光颜色。室内白天偏暖，夜晚偏暖黄，公园偏冷白。\n" +
                     "夜间场景要压暗，不然 3D 宠物会比背景亮得多，像贴纸。")]
            public Color lightColor = Color.white;

            [Tooltip("环境光强度。夜间要压低。")]
            public float ambientIntensity = 1f;

            [Tooltip("假阴影不透明度。0=不显示。")]
            public float shadowOpacity = 0.35f;

            [Tooltip("是否可解锁。免费场景填 false。")]
            public bool unlockable = false;
        }

        public SceneEntry Find(string sceneId)
        {
            foreach (var s in scenes)
            {
                if (s != null && s.sceneId == sceneId) return s;
            }
            return null;
        }

        /// <summary>取景比例：狗 0.76m 高约占画面多少（0~1）。用于验证构图。</summary>
        public float PetScreenRatio(float petHeight)
        {
            if (orthographicSize <= 0f) return 0f;
            return petHeight / (orthographicSize * 2f);
        }

        private void OnValidate()
        {
            if (orthographicSize <= 0f) orthographicSize = 1.9f;
            if (wanderArea.x <= 0f) wanderArea.x = 1.5f;
            if (wanderArea.y <= 0f) wanderArea.y = 0.9f;
            groundLine = Mathf.Clamp01(groundLine);
            foreach (var s in scenes)
            {
                if (s == null) continue;
                s.shadowOpacity = Mathf.Clamp01(s.shadowOpacity);
                if (s.price < 0) s.price = 0;
                if (s.ambientIntensity < 0f) s.ambientIntensity = 0f;
            }
        }
    }
}