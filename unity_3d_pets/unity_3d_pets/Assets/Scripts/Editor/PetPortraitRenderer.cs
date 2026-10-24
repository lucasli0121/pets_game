using System.IO;
using PetV1.Pet;
using UnityEditor;
using UnityEngine;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 把场景里的 3D 狗渲成一张方形肖像 PNG，供「选宠页」的宠物卡片用。
    ///
    /// 为什么用静态图而不是在卡片里挂 RenderTexture 实时渲染：
    /// ① RenderTexture 常驻显存，之前场景图缓存已经踩过一次「同一张图被创建多遍、
    ///    白占 8MB」的坑；
    /// ② 卡片里的狗只需要一张正面头像，不会动，实时渲染是纯浪费；
    /// ③ 静态图可以进 Resources，换模型时重跑一次菜单即可，链路和
    ///    <see cref="PetIconImporter"/> 一致。
    ///
    /// **透明背景**：渲成带 alpha 的 PNG，卡片底色由 UI 决定（浅蓝），
    /// 以后换卡片底色不用重渲图。
    ///
    /// **取景用正交相机 + 包围盒反算**：透视相机要反复试距离，而狗一换模型、
    /// 动画一换姿态，试出来的距离全部作废。正交下把狗包围盒的 8 个角投到相机
    /// 空间，直接算出「刚好装下还要留 12% 余量」的 orthographicSize，
    /// 换任何模型都不会切头切耳。
    /// </summary>
    public static class PetPortraitRenderer
    {
        /// <summary>输出目录。Resources 下，运行期用 Resources.Load&lt;Texture2D&gt; 取。</summary>
        public const string PortraitDir = "Assets/Resources/PetPortraits";

        /// <summary>默认肖像文件名（不含扩展名）。</summary>
        public const string DefaultPortrait = "border_collie";

        /// <summary>狗根节点在场景里的名字，由 <c>PetSceneBuilder.PlaceDog</c> 写死。</summary>
        private const string DogRootName = "Pet_Dog_001";

        /// <summary>假阴影节点名（狗根的子节点）。它是贴地四边形，透明背景下会显得浮空。</summary>
        private const string FakeShadowName = "PetFakeShadow";

        /// <summary>输出边长。卡片里显示约 220×220，512 足够（2.3 倍余量）。</summary>
        private const int Size = 512;

        /// <summary>取景余量。1.12 = 四周多留 12%，狗不会顶到边框。</summary>
        private const float FrameMargin = 1.12f;

        /// <summary>从狗包围盒哪个高度开始算「上半身」。0.45 = 从 45% 高度往上（头 + 胸口）。</summary>
        private const float UpperBodyFrom = 0.45f;

        [MenuItem("Pet V1 / 7. Render Pet Portrait")]
        public static void RenderDefault()
        {
            Render(DefaultPortrait);
        }

        /// <summary>渲染并保存。name 不含扩展名与目录。</summary>
        public static void Render(string name)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[PetV1] 请在退出 Play 后再渲肖像：Play 下新建的临时相机会进运行期场景。");
                return;
            }

            var dog = GameObject.Find(DogRootName);
            if (dog == null)
            {
                Debug.LogError($"[PetV1] 场景里找不到 {DogRootName}，请先执行 Pet V1 / 3. Build Home Scene。");
                return;
            }

            var renderers = dog.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogError("[PetV1] 狗身上没有 SkinnedMeshRenderer，取不到包围盒。");
                return;
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            // ---- 关掉除狗以外的渲染器 ----
            // 为什么必须关：背景图（客厅）是场景里的 SpriteRenderer，不关的话
            // 透明底会被房间填满，之前第一版就栽在这 —— alpha 65% 不透明，
            // 全是墙和地板，狗反而看不出来。
            //
            // 灯不是 Renderer，不受影响，狗照样有光照。
            //
            // 走 Undo 而不是手动 SetActive(false)/true：在编辑器里改场景对象
            // 会把场景标脏（标题栏出现 *），玩家随手一存就把「临时隐藏」存进了场景。
            // Undo.RecordObject 记的是序列化属性，Revert 之后场景干净如初。
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Render Pet Portrait");

            var hidden = new System.Collections.Generic.List<Renderer>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r.GetComponentInParent<PetController>() != null) continue;   // 狗自己的
                if (r.name.Contains(FakeShadowName)) continue;                    // 狗根的假阴影，下面单独关
                Undo.RecordObject(r, "hide");
                r.enabled = false;
                hidden.Add(r);
            }
            foreach (var r in dog.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.Contains(FakeShadowName)) continue;
                Undo.RecordObject(r, "hide");
                r.enabled = false;
                hidden.Add(r);
            }

            // ---- 正交相机按包围盒取景 ----
            //
            // 相机不带俯仰（正对 -Z 看），所以世界 Y 就是相机 Y，不需要投角。
            //
            // 要框住的世界区间：
            //   上 = 包围盒顶（耳朵最高点）
            //   下 = min.y + 45% 高度（胸口，不把腿框进来，否则头像会很小）
            // 横向用包围盒全宽（耳朵是最宽的部分）。
            //
            // 第一版在这里摔过：拿包围盒 8 个角筛「上半身」，而角只存在于
            // min.y / max.y 两个高度上，筛完之后垂直范围退化成 0，
            // 相机对准了狗头顶，狗整个掉到画面下半部还被底边裁掉。
            float top = bounds.max.y;
            float bottom = bounds.min.y + (bounds.max.y - bounds.min.y) * UpperBodyFrom;
            float half = Mathf.Max(bounds.extents.x, (top - bottom) * 0.5f) * FrameMargin;

            var camGo = new GameObject("__PortraitCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.aspect = 1f;
            cam.orthographicSize = half;

            Vector3 aim = new Vector3(bounds.center.x, (top + bottom) * 0.5f, bounds.center.z);
            camGo.transform.position = aim + new Vector3(0f, 0f, -2f);
            camGo.transform.rotation = Quaternion.LookRotation(aim - camGo.transform.position, Vector3.up);

            var rt = new RenderTexture(Size, Size, 32, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            var bytes = tex.EncodeToPNG();

            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);

            Undo.RevertAllDownToGroup(undoGroup);

            if (!AssetDatabase.IsValidFolder(PortraitDir))
            {
                Directory.CreateDirectory(PortraitDir);
                AssetDatabase.Refresh();
            }

            string path = $"{PortraitDir}/{name}.png";
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path);
            Debug.Log($"[PetV1] 宠物肖像已保存：{path}（{bytes.Length} 字节，{Size}×{Size}，" +
                      $"透明底，取景半高 {half:F3}m）");
        }
    }
}
