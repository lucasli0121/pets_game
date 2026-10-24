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
    ///
    /// **构图参数（orthographicSize / floorFarLine / dogGroundLine / wanderArea…）
    /// 现在挂在每个 SceneEntry 自己的 <see cref="SceneLayout"/> 上，不在全局。**
    /// 见 SceneLayout 的注释：这些是每张图各不相同的实测值，写死全局等于每加一张图发一次版。
    /// 顶部的 defaultLayout 只是「还没填时的模板」，运行时读的是 entry.layout。
    /// </summary>
    [CreateAssetMenu(fileName = "PetSceneLibrary",
                     menuName = "PetV1/场景库（2D背景+3D宠物）")]
    public class PetSceneLibrary : ScriptableObject
    {
        [Header("默认构图模板（新场景的初始值，运行时不读）")]
        [Tooltip("新场景的初始构图参数模板。运行时读的是每个 SceneEntry 自己的 layout，"+
                 "这里只给「还没填 layout 的条目」兜底。")]
        public SceneLayout defaultLayout = new SceneLayout();

        [Header("场景列表")]
        public List<SceneEntry> scenes = new List<SceneEntry>();

        [Serializable]
        public class SceneEntry
        {
            [Tooltip("场景 id，与后台 config_scene.id 一致。")]
            public string sceneId = "home_day";

            [Tooltip("后台下发的显示名。UI 直接用它，不做 i18n key 二次映射（V1 只有简体中文）。")]
            public string displayName = "温馨小家（白天）";

            [Tooltip("背景图。必须 1080×1920，导入设置见 ImportSettings 说明。\n" +
                     "运行时可以由后台 sceneUrl 下载后覆盖这个引用。")]
            public Texture2D background;

            [Tooltip("解锁所需积分。0 = 默认拥有（免费）。与后台 price 同步。")]
            public int price = 0;

            [Tooltip("这张背景图配套的实测构图数据点。与后台 config_scene.layout 同构。")]
            public SceneLayout layout = new SceneLayout();

            [Tooltip("是否可解锁。免费场景填 false。")]
            public bool unlockable = false;

            // 下面两个由 PetSceneLibrary.ApplyRemoteLayout 填，不在编辑器里手改。
            [Tooltip("后台下发的原图地址（空=走包内 background）。")]
            public string sceneUrl = "";

            [Tooltip("后台下发的缩略图地址。")]
            public string thumbUrl = "";

            /// <summary>取该场景的构图数据点，为空时回落到默认模板。绝不返回 null。</summary>
            public SceneLayout ResolveLayout()
            {
                if (layout == null) layout = new SceneLayout();
                return layout;
            }
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
        public float PetScreenRatio(float petHeight, SceneLayout lay)
        {
            if (lay == null || lay.orthographicSize <= 0f) return 0f;
            return petHeight / (lay.orthographicSize * 2f);
        }

        private void OnValidate()
        {
            if (defaultLayout == null) defaultLayout = new SceneLayout();
            defaultLayout.FillDefaults();
            foreach (var s in scenes)
            {
                if (s == null) continue;
                if (s.layout == null) s.layout = new SceneLayout();
                s.layout.FillDefaults();
                if (s.price < 0) s.price = 0;
            }
        }

        // ------------------------------------------------------------------
        // 后台配置覆盖
        // ------------------------------------------------------------------

        /// <summary>
        /// 用后台下发的 config_scene 覆盖本地场景库。
        ///
        /// 为什么要后台可配：构图参数是**每张图各不相同的实测值**。
        /// 写进安装包意味着每换一张背景图都要改代码发版，而运营加图是常态。
        ///
        /// 覆盖粒度是**整条 layout**，不是逐字段。
        /// 原本想逐字段判「后台给了没有」，但 Unity 的 JsonUtility 不支持 float?，
        /// 而0 对构图参数又是合法值（wanderCenterX 完全可以是0），没法区分
        /// 「没配」和「配成 0」。服务端 normalizeSceneLayout 已经保证下发的每个字段都是有效值，
        /// 所以直接全量覆盖 + FillDefaults 兜底即可，语义反而更清楚。
        ///
        /// 后台有但本地没有的场景会被追加进来（background 为空，靠 sceneUrl 运行时下载）。
        /// 本地有但后台没有的**保留不删**：离线态与后台不可用时仍要能玩。
        /// </summary>
        /// <param name="sceneJsonArray">
        /// **裸 JSON 数组**，如 [{"id":"home_day",...},...]，不要带外层{"items":...}。
        /// Unity 的 JsonUtility 不能解析顶层数组，所以本方法内部自己装箱。
        /// 调用方若已经有一层items 包装，套两次会解析出 null 且**不报错** ——
        /// 实测过一次：函数返回 true 但一个字段都没应用上。
        /// </param>
        public int ApplyRemoteConfig(string sceneJsonArray, out int addedCount)
        {
            addedCount = 0;
            if (string.IsNullOrEmpty(sceneJsonArray)) return 0;

            RemoteScene[] remotes;
            try
            {
                // JsonUtility 不支持顶层数组，包一层对象再取。
                remotes = JsonUtility.FromJson<SceneArrayWrapper>(
                    "{\"items\":" + sceneJsonArray + "}")?.items;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PetV1] 后台场景配置解析失败，沿用本地场景库：{e.Message}");
                return 0;
            }
            if (remotes == null || remotes.Length == 0)
            {
                // 到这里通常是传进来的是 {"items":[...]} 而不是裸数组。
                // 不报警的话症状是「配置没生效」但没有任何线索。
                Debug.LogWarning($"[PetV1] 后台场景配置：解析出 0 个场景。" +
                                 $"入参必须是裸数组 [..]，不能带外层 items 包装。" +
                                 $"入参前 40 字符：{sceneJsonArray.Substring(0, Mathf.Min(40, sceneJsonArray.Length))}");
                return 0;
            }

            int changed = 0;
            foreach (var r in remotes)
            {
                if (r == null || string.IsNullOrEmpty(r.id)) continue;
                var entry = Find(r.id);
                if (entry == null)
                {
                    entry = new SceneEntry
                    {
                        sceneId = r.id,
                        displayName = r.name,
                        price = r.price,
                        // 本地没这张图：靠 sceneUrl 运行时下载，
                        // background 留空，由 PetSceneStage 判空后跳过贴图切换。
                        background = null,
                        unlockable = r.price > 0,
                        layout = new SceneLayout(),
                    };
                    scenes.Add(entry);
                    addedCount++;
                    changed++;
                }
                if (ApplyRemoteLayout(entry, r)) changed++;
            }
            if (changed > 0)
            {
                // 运行期改的是内存里的 ScriptableObject 实例，不落盘。
                // 落盘的是 Editor 构建期生成的那份，由 PetHomeSceneBuilder 负责。
                Debug.Log($"[PetV1] 已应用后台场景配置：{changed} 处改动（新增 {addedCount} 个场景）");
            }
            return changed;
        }

        private static bool ApplyRemoteLayout(SceneEntry entry, RemoteScene r)
        {
            var t = r.layout;
            if (t == null) return false;

            var lay = entry.ResolveLayout();
            bool changed = false;

            changed |= Copy(ref lay.orthographicSize, t.orthographicSize);
            changed |= Copy(ref lay.cameraPitch, t.cameraPitch);
            changed |= Copy(ref lay.cameraDistance, t.cameraDistance);
            changed |= Copy(ref lay.floorFarLine, t.floorFarLine);
            changed |= Copy(ref lay.dogGroundLine, t.dogGroundLine);
            changed |= Copy(ref lay.wanderHalfX, t.wanderHalfX);
            changed |= Copy(ref lay.wanderHalfZ, t.wanderHalfZ);
            changed |= Copy(ref lay.wanderCenterX, t.wanderCenterX);
            changed |= Copy(ref lay.wanderCenterZ, t.wanderCenterZ);
            changed |= Copy(ref lay.lightEulerX, t.lightEulerX);
            changed |= Copy(ref lay.lightEulerY, t.lightEulerY);
            changed |= Copy(ref lay.lightEulerZ, t.lightEulerZ);
            changed |= Copy(ref lay.lightColorR, t.lightColorR);
            changed |= Copy(ref lay.lightColorG, t.lightColorG);
            changed |= Copy(ref lay.lightColorB, t.lightColorB);
            changed |= Copy(ref lay.ambientIntensity, t.ambientIntensity);
            changed |= Copy(ref lay.shadowOpacity, t.shadowOpacity);

            if (!string.IsNullOrEmpty(t.layoutNote) && lay.layoutNote != t.layoutNote)
            {
                lay.layoutNote = t.layoutNote;
                changed = true;
            }
            if (!string.IsNullOrEmpty(r.sceneUrl) && entry.sceneUrl != r.sceneUrl)
            {
                entry.sceneUrl = r.sceneUrl;
                changed = true;
            }
            if (!string.IsNullOrEmpty(r.thumbUrl) && entry.thumbUrl != r.thumbUrl)
            {
                entry.thumbUrl = r.thumbUrl;
                changed = true;
            }
            if (!string.IsNullOrEmpty(r.name) && entry.displayName != r.name)
            {
                entry.displayName = r.name;
                changed = true;
            }
            if (entry.price != r.price)
            {
                entry.price = r.price;
                entry.unlockable = r.price > 0;
                changed = true;
            }
            if (changed) lay.FillDefaults();
            return changed;
        }

        private static bool Copy(ref float slot, float v)
        {
            if (Mathf.Approximately(slot, v)) return false;
            slot = v;
            return true;
        }

        /// <summary>
        /// 后台 scene 条目的 JSON 形态。字段名必须与 Go 侧 json tag 一致，
        /// 改一个名字就静默解析成 0（然后被 FillDefaults 补成默认值，看起来「配置没生效」）。
        /// </summary>
        [Serializable]
        public class RemoteScene
        {
            public string id;
            public string name;
            public int price;
            public string sceneUrl;
            public string thumbUrl;
            public RemoteLayout layout;
        }

        [Serializable]
        public class RemoteLayout
        {
            public float orthographicSize;
            public float cameraPitch;
            public float cameraDistance;
            public float floorFarLine;
            public float dogGroundLine;
            public float wanderHalfX;
            public float wanderHalfZ;
            public float wanderCenterX;
            public float wanderCenterZ;
            public float lightEulerX;
            public float lightEulerY;
            public float lightEulerZ;
            public float lightColorR;
            public float lightColorG;
            public float lightColorB;
            public float ambientIntensity;
            public float shadowOpacity;
            public string layoutNote;
        }

        [Serializable]
        private class SceneArrayWrapper
        {
            public RemoteScene[] items;
        }
    }
}