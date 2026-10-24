using System;
using UnityEngine;

namespace PetV1.Model
{
    /// <summary>
    /// 一张背景图的构图数据点。与后台 config_scene.layout 一一对应。
    ///
    /// 为什么要从 PetSceneLibrary 的全局字段下沉到每个场景：
    /// 原本 orthographicSize / cameraPitch / floorFarLine / dogGroundLine / wanderArea
    /// 都是全局的，换一张背景图就得改代码发一次版。而这些值恰恰是**每张图各不相同的实测值**
    /// （地面线取决于图画了多远的地板）。三张图实测：
    ///   home_day  踢脚线 0.433（图像 y≈1085~1090）
    ///   home_night 踢脚线 0.435（图像 y≈1067~1085）
    ///   park_day   无踢脚线，取远处草地与灌木交界 0.549（图像 y≈865）
    /// 户外那张的可视范围还必须更大（草地没有墙做纵深参照），
    /// 用同一个 orthoSize 狗会显得过大、贴在画面上。
    ///
    /// 字段与后台一一对应，改名会直接导致 JSON 解析不到（值退回0 后由 FillDefaults 补默认）。
    /// </summary>
    [Serializable]
    public class SceneLayout
    {
        [Header("取景（正交投影）")]
        [Tooltip("正交相机的可视半高（世界单位，米）。\n" +
                 "**这个值决定狗占画面多少**：狗 0.76m 高，1.9 时约占 20%。\n" +
                 "户外场景要更大（2.2），草地没有墙做纵深参照。")]
        public float orthographicSize = 1.9f;

        [Tooltip("相机俯角（度）。0=水平看，正值=向下看。俯视构图才看得到地面。")]
        [Range(0f, 45f)] public float cameraPitch = 12f;

        [Tooltip("相机与宠物的水平距离（米）。正交下不影响大小，只影响裁剪范围。")]
        public float cameraDistance = 3.2f;

        [Header("地面位置")]
        [Tooltip("地板【最远端】在画面里的归一化纵向位置（0=画面底，1=画面顶）。\n" +
                 "**只作构图自检基准，不参与相机计算。**\n" +
                 "室内量踢脚线暗缝（亮度局部最暗点），户外量地面与远处植被的交界。\n" +
                 "量法见 PetSceneLibrary 旧字段的注释（已迁到本类）。\n" +
                 "⚠ 别用「最大亮度跳变」找交界，会选中地板上的阳光斑。")]
        [Range(0.05f, 0.95f)] public float floorFarLine = 0.433f;

        [Tooltip("狗【脚底】在画面里的归一化纵向位置。**这才是决定构图的参数**，相机会按它反推高度。\n" +
                 "必须小于 floorFarLine —— 比地板最远端高就是悬空 / 贴墙。\n" +
                 "scene_home_day 取 0.30（约地毯中部）；scene_park_day 取 0.32（中景草地）。")]
        [Range(0.05f, 0.95f)] public float dogGroundLine = 0.30f;

        [Header("活动区域（世界单位，米）")]
        [Tooltip("活动区半宽 / 半深。太小会让狗走起来目标点几乎都在原地（日志刷 stuck）。")]
        public float wanderHalfX = 0.95f;
        public float wanderHalfZ = 0.70f;

        [Tooltip("活动区中心相对狗初始站位的偏移。\n" +
                 "scene_home_day 沙发与边桌占画面右侧，所以 X 要往左偏。\n" +
                 "scene_park_day 画面里没有家具，保持 0 即可。")]
        public float wanderCenterX = -0.30f;
        public float wanderCenterZ = -0.10f;

        [Header("光照（必须与背景图实际光源一致）")]
        [Tooltip("主光方向（度）。三张背景图光源都在左上。方向不一致会有两套影子。")]
        public float lightEulerX = 42f;
        public float lightEulerY = -35f;
        public float lightEulerZ = 0f;

        [Tooltip("主光颜色。夜间要压暖（0.82 / 0.60），否则宠物比背景亮得多，像贴纸。")]
        public float lightColorR = 1f;
        public float lightColorG = 0.97f;
        public float lightColorB = 0.90f;

        [Tooltip("环境光强度。夜间要压低（0.42）。")]
        public float ambientIntensity = 1.15f;

        [Tooltip("脚下椭圆假阴影的不透明度。0=不显示。")]
        [Range(0f, 1f)] public float shadowOpacity = 0.32f;

        [Tooltip("实测记录：量的是什么东西、怎么量的、哪几列一致。纯文本，不参与逻辑。")]
        public string layoutNote = "";

        public Vector3 LightEuler => new Vector3(lightEulerX, lightEulerY, lightEulerZ);
        public Color LightColor => new Color(lightColorR, lightColorG, lightColorB);
        public Vector2 WanderHalf => new Vector2(wanderHalfX, wanderHalfZ);
        public Vector2 WanderCenter => new Vector2(wanderCenterX, wanderCenterZ);

        /// <summary>
        /// 按狗的落脚线反推相机的世界高度与位置。
        ///
        /// 为什么不能写死相机位置：正交相机里，屏幕纵向位置只跟相机中心的世界高度有关。
        /// 脚底线要落在 dogGroundLine，就要求相机中心 - 脚底 = (0.5 - dogGroundLine) × 可视高度。
        /// 写死位置的话，改 orthographicSize 或 dogGroundLine 就会对不上。
        ///
        /// <paramref name="groundWorldY"/> 要传**宠物脚底的实际世界 Y**，
        /// 不能假设是 0 —— 模型 bounds.min.y 往往是 -0.01 这种小负值
        /// （蒙皮网格原点不在脚底），差 1cm 在画面上就是几个百分点。
        /// </summary>
        public Vector3 ComputeCameraPosition(float groundWorldY)
        {
            // 目标：脚底落在画面 dogGroundLine 高度。
            // 正交相机俯视时，相机上移 dy，地面在画面里上移 dy*cos(pitch)/(2*orthoSize)。
            //少了 cos(pitch) 这一项，俯角越大偏得越多。
            float cos = Mathf.Cos(cameraPitch * Mathf.Deg2Rad);
            float rise = (0.5f - dogGroundLine) * orthographicSize * 2f;
            float camY = groundWorldY + rise / Mathf.Max(1e-4f, cos)
                       + Mathf.Tan(cameraPitch * Mathf.Deg2Rad) * cameraDistance;
            return new Vector3(0f, camY, -cameraDistance);
        }

        /// <summary>
        /// 把非法 / 0 值补成默认值。
        ///
        /// 判据用「0 视为未填」而不是显式的 hasXxx 标记：少一个标记位就少一处可能漏掉的同步点。
        /// 构图参数没有合法的 0（orthoSize=0 会让画面全空，floorFarLine=0 会让狗站到画面底外）。
        /// 后台漏配字段时走的就是这条路径 —— JsonUtility 不会给缺失字段填默认值，会留 0。
        /// </summary>
        public void FillDefaults()
        {
            if (orthographicSize <= 0f) orthographicSize = 1.9f;
            if (cameraPitch <= 0f) cameraPitch = 12f;
            if (cameraDistance <= 0f) cameraDistance = 3.2f;
            if (floorFarLine <= 0f) floorFarLine = 0.433f;
            if (dogGroundLine <= 0f) dogGroundLine = 0.30f;
            if (wanderHalfX <= 0f) wanderHalfX = 0.95f;
            if (wanderHalfZ <= 0f) wanderHalfZ = 0.70f;
            if (lightColorR == 0f && lightColorG == 0f && lightColorB == 0f)
            {
                lightColorR = 1f; lightColorG = 0.97f; lightColorB = 0.90f;
            }
            if (ambientIntensity <= 0f) ambientIntensity = 1.15f;
            if (shadowOpacity <= 0f) shadowOpacity = 0.32f;

            floorFarLine = Mathf.Clamp01(floorFarLine);
            dogGroundLine = Mathf.Clamp01(dogGroundLine);
            ambientIntensity = Mathf.Max(0f, ambientIntensity);
            shadowOpacity = Mathf.Clamp01(shadowOpacity);
            // 狗不能站得比地板最远端还高 —— 那是悬空 / 贴墙的信号。
            if (dogGroundLine > floorFarLine) dogGroundLine = floorFarLine;
        }

        public SceneLayout Clone()
        {
            return (SceneLayout)MemberwiseClone();
        }
    }
}