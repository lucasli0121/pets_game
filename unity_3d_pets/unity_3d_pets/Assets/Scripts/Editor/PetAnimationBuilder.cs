using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetV1.Animation;
using PetV1.Core;
using PetV1.Model;
using PetV1.Pet;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 动画资源构建器。一次性把 Animals FREE 的 Dog 资源整理成 Pet V1 需要的三件东西：
    ///   1. 开启循环的 Idle / Walk / Run Clip（源Clip 的 LoopTime 是 0，直接用会停在最后一帧）
    ///   2. 一个由 Speed 参数驱动的 AnimatorController（状态：Idle / Walk / Run + OneShot 触发态）
    ///   3. PetAnimationMap 资产（记录模型具备哪些能力、各能力的移动速度）
    ///
    /// 执行方式：菜单 Pet V1 / 1. Build Animation Assets
    /// </summary>
    public static class PetAnimationBuilder
    {
        public const string SourceAnimDir = "Assets/ithappy/Animals_FREE/Animations/Other_Animations";
        public const string OutputDir = "Assets/Animations/Dog";
        public const string ControllerPath = "Assets/Animations/Dog/PetDog.controller";
        public const string AnimationMapPath = "Assets/Animations/Dog/PetDogAnimationMap.asset";
        public const string PersonalityProfilePath = "Assets/Animations/Dog/BorderColliePersonality.asset";

        public const string SpeedParameter = "Speed";
        public const string OneShotTrigger = "OneShot";

        /// <summary>OneShot 层名。与 AnimationOrchestrator.OneShotLayerName 必须一致。</summary>
        public const string OneShotLayerName = "OneShot";

        // 需求文档 4.3 的阈值：Speed > 0.1 -> Walk，Speed > 2.0 -> Run
        public const float WalkThreshold = 0.1f;
        public const float RunThreshold = 2.0f;

        private const string IdleSource = "Dog_001_idle.anim";
        private const string WalkSource = "Dog_001_walk.anim";
        private const string RunSource = "Dog_001_run.anim";

        /// <summary>
        /// 稳健加载 AnimationMap。
        ///
        /// 为什么不用 LoadAssetAtPath&lt;PetAnimationMap&gt;：
        /// 实测在同一工程里 LoadAssetAtPath&lt;Object&gt;(path) 能正常返回
        /// PetAnimationMap 实例，但泛型版本 LoadAssetAtPath&lt;PetAnimationMap&gt;(path)
        /// 返回 null。原因是磁盘上的 .asset 是在**脚本重新编译前**创建的，
        /// Unity 的类型缓存里它绑定的脚本与当前域不一致，泛型版本会做类型检查并拒绝返回。
        /// 非泛型版本不做类型检查，所以能拿到对象。
        ///
        /// 修法：非泛型加载 + as 转换，并在必要时重建资产。
        /// </summary>
        public static PetAnimationMap LoadAnimationMap()
        {
            string path = AnimationMapPath;

            // 1) 先试泛型（正常情况下走这条）
            var typed = AssetDatabase.LoadAssetAtPath<PetAnimationMap>(path);
            if (typed != null) return typed;

            // 2) 泛型失败 -> 非泛型加载后转换。
            //    用 Unity 重载的 == 而不是 C# 的 is null：AssetDatabase 写操作会销毁并
            //    重建 native 实例，此时 C# 引用还在但 == null 为 true（Unity fake null）。
            var loose = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (loose == null)
            {
                Debug.LogWarning($"[PetV1] 找不到 AnimationMap：{path}");
                return null;
            }

            if (loose is PetAnimationMap map) return map;

            // 3) 文件在但类型对不上 -> 删掉重建。这是脚本改名/拆文件后的典型症状。
            Debug.LogWarning($"[PetV1] AnimationMap 类型不匹配，重建：" +
                             $"实际={loose.GetType().FullName}, 期望={nameof(PetAnimationMap)}");

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.Refresh();
            var recreated = ScriptableObject.CreateInstance<PetAnimationMap>();
            AssetDatabase.CreateAsset(recreated, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // 重建后必须重新加载，否则返回的是未持久化的临时实例
            return AssetDatabase.LoadAssetAtPath<PetAnimationMap>(path) ?? recreated;
        }

        /// <summary>
        /// 可靠加载性格配置资产。与 <see cref="LoadAnimationMap"/> 同构：
        /// 泛型 -> 非泛型 + as -> 必要时删掉重建。
        /// </summary>
        public static PetPersonalityProfile LoadPersonalityProfile()
        {
            string path = PersonalityProfilePath;

            var typed = AssetDatabase.LoadAssetAtPath<PetPersonalityProfile>(path);
            if (typed != null) return typed;

            var loose = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (loose == null)
            {
                Debug.LogWarning($"[PetV1] 找不到性格配置：{path}");
                return null;
            }

            if (loose is PetPersonalityProfile profile) return profile;

            Debug.LogWarning($"[PetV1] 性格配置类型不匹配，重建：" +
                             $"实际={loose.GetType().FullName}, 期望={nameof(PetPersonalityProfile)}");

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.Refresh();
            var recreated = ScriptableObject.CreateInstance<PetPersonalityProfile>();
            recreated.breedId = "border_collie";
            AssetDatabase.CreateAsset(recreated, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return AssetDatabase.LoadAssetAtPath<PetPersonalityProfile>(path) ?? recreated;
        }

        /// <summary>模型描述资产路径。换模型时改这一个文件即可。</summary>
        internal const string ModelProfilePath = "Assets/Animations/Dog/DogModelProfile.asset";

        /// <summary>
        /// 加载模型描述（骨骼名、体型、速度、活动区）。
        /// 找不到就按当前免费模型的实测值现场建一个并落盘 ——
        /// 保证「换模型后忘了填 profile」时场景仍能构建出来。
        /// </summary>
        public static PetModelProfile LoadModelProfile()
        {
            EnsureFolder("Assets/Animations", "Dog");

            var typed = AssetDatabase.LoadAssetAtPath<PetModelProfile>(ModelProfilePath);
            if (typed != null) return typed;

            Debug.LogWarning($"[PetV1] 找不到模型描述 {ModelProfilePath}，用内置默认值重建。" +
                             "换模型后请在 asset 里填真实骨骼名与体型。");
            var created = ScriptableObject.CreateInstance<PetModelProfile>();
            AssetDatabase.CreateAsset(created, ModelProfilePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ModelProfilePath, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<PetModelProfile>(ModelProfilePath) ?? created;
        }

        [MenuItem("Pet V1/1. Build Animation Assets", false, 1)]
        public static void BuildAll()
        {
            EnsureFolder("Assets/Animations", "Dog");

            AnimationClip idle = CreateLoopingClip(IdleSource, "Dog_Idle");
            AnimationClip walk = CreateLoopingClip(WalkSource, "Dog_Walk");
            AnimationClip run = CreateLoopingClip(RunSource, "Dog_Run");

            if (idle == null || walk == null || run == null)
            {
                Debug.LogError("[PetV1] 构建失败：源动画 Clip 未找到，请确认 Animals_FREE 已导入。");
                return;
            }

            AnimatorController controller = BuildController(idle, walk, run);
            PetAnimationMap map = BuildAnimationMap(idle, walk, run);
            BuildPersonalityProfile();

            AssetDatabase.SaveAssets();
            // 强制重导入：脚本刚编译过，不Refresh 的话磁盘上的 .asset 仍绑着旧类型，
            // 后面 LoadAssetAtPath<T> 会返回 null。
            AssetDatabase.ImportAsset(AnimationMapPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(PersonalityProfilePath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            Debug.Log($"[PetV1] 动画资源构建完成。\n" +
                      $"  Idle: {idle.name} (len={idle.length:F2}s, loop={idle.isLooping})\n" +
                      $"  Walk: {walk.name} (len={walk.length:F2}s, loop={walk.isLooping})\n" +
                      $"  Run : {run.name} (len={run.length:F2}s, loop={run.isLooping})\n" +
                      $"  Controller: {AssetDatabase.GetAssetPath(controller)}\n" +
                      $"  AnimationMap: {AssetDatabase.GetAssetPath(map)}\n" +
                      $"  默认速度：Walk={map.defaultWalkSpeed} m/s, Run={map.defaultRunSpeed} m/s（可在 Map 上调）");
        }

        /// <summary>
        /// 确保 <paramref name="parent"/>/<paramref name="child"/> 这条路径存在。
        /// 支持多级：Assets/Animations/Dog 会逐级补齐缺的父目录。
        /// </summary>
        public static void EnsureFolder(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return;

            // 父目录不存在时，先逐级补齐父目录，再继续建当前层。
            // 这里不能 return —— 否则 Assets/Animations 不存在时只会建出 Assets/Animations
            // 而不会建 Dog，后续 File.Copy 会抛 DirectoryNotFoundException。
            if (!AssetDatabase.IsValidFolder(parent))
            {
                int lastSlash = parent.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    string grand = parent.Substring(0, lastSlash);
                    string name = parent.Substring(lastSlash + 1);
                    EnsureFolder(grand, name);
                }
            }

            string full = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(full)) AssetDatabase.CreateFolder(parent, child);
        }

        /// <summary>
        /// 确保磁盘上的物理目录存在（File.Copy / CreateAsset 的目标必须真实存在）。
        /// AssetDatabase.CreateFolder 只负责 Asset 管线，这一步不能省。
        /// </summary>
        public static string EnsurePhysicalFolder(string assetFolderPath)
        {
            string abs = Path.GetFullPath(assetFolderPath);
            if (!Directory.Exists(abs)) Directory.CreateDirectory(abs);
            return abs;
        }

        /// <summary>
        /// 复制源 Clip 并开启循环。
        /// 源 .anim 的 m_AnimationClipSettings.m_LoopTime = 0，若不处理，
        /// Idle 播完会停在最后一帧、Walk 会走一步就卡住。
        /// </summary>
        private static AnimationClip CreateLoopingClip(string sourceFileName, string outputName)
        {
            string sourcePath = $"{SourceAnimDir}/{sourceFileName}";
            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(sourcePath);
            if (source == null)
            {
                Debug.LogError($"[PetV1] 找不到源动画：{sourcePath}");
                return null;
            }

            string targetPath = $"{OutputDir}/{outputName}.anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath) != null)
                AssetDatabase.DeleteAsset(targetPath);

            // 目标目录必须真实存在，否则 File.Copy 会抛 DirectoryNotFoundException。
            // AssetDatabase.CreateFolder 建的目录在磁盘上是延迟落盘的，这里再确认一次。
            EnsurePhysicalFolder(OutputDir);

            // 用 AssetDatabase.CopyAsset 而不是 File.Copy：
            // 前者会走 Unity 的导入管线并生成 .meta，避免手工复制后 GUID 缺失或导入时机不对。
            if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
            {
                Debug.LogError($"[PetV1] 复制动画失败：{sourcePath} -> {targetPath}");
                return null;
            }
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
            if (clip == null) return null;

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);

            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
            Debug.Log($"[PetV1] 生成循环 Clip：{outputName}  len={clip.length:F2}s  loop={clip.isLooping}");
            return clip;
        }

        /// <summary>
        /// 构建 AnimatorController。
        /// 结构（对应需求文档 4.3 的状态图）：
        ///   Idle --(Speed &gt; 0.1)--> Walk --(Speed &gt; 2.0)--> Run
        ///   Walk --(Speed &lt; 0.1)--> Idle
        ///   Run --(Speed &lt; 2.0)--> Walk
        /// 另有一个 OneShot 层承载 Bark / Jump 等一次性动作（当前模型无此Clip，保留触发口）。
        /// </summary>
        private static AnimatorController BuildController(AnimationClip idle, AnimationClip walk, AnimationClip run)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            // Speed 参数：驱动 Idle/Walk/Run
            controller.AddParameter(SpeedParameter, AnimatorControllerParameterType.Float);

            var rootStateMachine = controller.layers[0].stateMachine;

            var idleState = rootStateMachine.AddState("Idle", new Vector3(320f, 60f, 0f));
            var walkState = rootStateMachine.AddState("Walk", new Vector3(320f, 160f, 0f));
            var runState = rootStateMachine.AddState("Run", new Vector3(320f, 260f, 0f));

            idleState.motion = idle;
            walkState.motion = walk;
            runState.motion = run;

            foreach (var state in new[] { idleState, walkState, runState })
            {
                state.writeDefaultValues = true;
            }

            rootStateMachine.defaultState = idleState;

            // Idle -> Walk : Speed > 0.1
            var idleToWalk = idleState.AddTransition(walkState);
            idleToWalk.AddCondition(AnimatorConditionMode.Greater, WalkThreshold, SpeedParameter);
            idleToWalk.hasExitTime = false;
            idleToWalk.duration = 0.10f;

            // Walk -> Idle : Speed < 0.1
            var walkToIdle = walkState.AddTransition(idleState);
            walkToIdle.AddCondition(AnimatorConditionMode.Less, WalkThreshold, SpeedParameter);
            walkToIdle.hasExitTime = false;
            walkToIdle.duration = 0.12f;

            // Walk -> Run : Speed > 2.0
            var walkToRun = walkState.AddTransition(runState);
            walkToRun.AddCondition(AnimatorConditionMode.Greater, RunThreshold, SpeedParameter);
            walkToRun.hasExitTime = false;
            walkToRun.duration = 0.12f;

            // Run -> Walk : Speed < 2.0
            var runToWalk = runState.AddTransition(walkState);
            runToWalk.AddCondition(AnimatorConditionMode.Less, RunThreshold, SpeedParameter);
            runToWalk.hasExitTime = false;
            runToWalk.duration = 0.15f;

            // ---- 第二层：OneShot ----
            // 未来换 Border Collie 有 Bark / Jump 时，把对应 Clip 挂到这一层的 Motion 上即可，
            // AnimationOrchestrator 只发 OneShot Trigger，不关心具体是哪个动作。
            controller.AddParameter(OneShotTrigger, AnimatorControllerParameterType.Trigger);

            // 用 AddLayer(string) 而不是 new AnimatorLayer{...}：
            // AnimatorLayer 是结构体，stateMachine 需要由 AddLayer 内部创建并挂到资产上，
            // 手工 new 出来再AddObjectToAsset 会导致层引用断裂。
            controller.AddLayer(OneShotLayerName);

            int oneShotLayerIndex = controller.layers.Length - 1;
            var oneShotSm = controller.layers[oneShotLayerIndex].stateMachine;
            oneShotSm.name = OneShotLayerName;
            controller.layers[oneShotLayerIndex].defaultWeight = 1f;

            // 占位状态，等待真实的一次性 Clip。
            // 用 Idle 作为占位 Motion，这样误触发 OneShot 时至少不会报错。
            var placeholder = oneShotSm.AddState("NoOneShot", new Vector3(300f, 60f, 0f));
            placeholder.motion = idle;
            oneShotSm.defaultState = placeholder;

            // 未来有 Bark Clip 时的接线位置（先注释，避免误导）：
            // var barkState = oneShotSm.AddState("Bark");
            // barkState.motion = barkClip;
            // oneShotSm.defaultState = barkState;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// 构建 PetAnimationMap。
        /// 只登记模型真实具备的能力：Idle / Walk / Run。
        /// Sit / Lie / Jump / Bark / Eat / Sleep / Attack 一律不登记，
        /// 由PetAnimationMap 的 fallback 链在运行时代替（需求文档第十四节）。
        /// </summary>
        private static PetAnimationMap BuildAnimationMap(AnimationClip idle, AnimationClip walk, AnimationClip run)
        {
            var map = LoadAnimationMap();
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<PetAnimationMap>();
                AssetDatabase.CreateAsset(map, AnimationMapPath);
                AssetDatabase.SaveAssets();
                // CreateAsset 之后原对象是临时实例，必须重新加载才能拿到持久化的那个
                map = LoadAnimationMap() ?? map;
            }

            map.modelId = "AnimalsFree_Dog_001";
            map.speedParameter = SpeedParameter;
            map.defaultWalkSpeed = 1.2f;
            map.defaultRunSpeed = 3.0f;
            map.entries = new List<PetAnimationEntry>
            {
                new PetAnimationEntry(PetCapability.Idle, idle, 0f),
                new PetAnimationEntry(PetCapability.Walk, walk, 1.2f),
                new PetAnimationEntry(PetCapability.Run,  run,  3.0f),
            };

            EditorUtility.SetDirty(map);
            return map;
        }

        private static void BuildPersonalityProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<PetPersonalityProfile>(PersonalityProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<PetPersonalityProfile>();
                AssetDatabase.CreateAsset(profile, PersonalityProfilePath);
            }
            profile.breedId = "border_collie";
            profile.basePersonality = PetPersonality.CreateBorderCollie();
            profile.individualVariance = 0.12f;
            EditorUtility.SetDirty(profile);
        }

        /// <summary>检查当前 Map 里缺哪些能力。用于第四节「模型能力抽象」的验收记录。</summary>
        [MenuItem("Pet V1/Report Capabilities", false, 20)]
        public static void ReportCapabilities()
        {
            var map = LoadAnimationMap();
            if (map == null)
            {
                Debug.LogWarning("[PetV1] 尚未生成 AnimationMap，请先执行 Pet V1 / 1. Build Animation Assets。");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Animals FREE 实际能力（第四/ 十四节验收用） ===");
            foreach (PetCapability capability in System.Enum.GetValues(typeof(PetCapability)))
            {
                bool has = map.Has(capability);
                string clipName = has ? map.GetClip(capability).name : "-";
                string fallback = "";
                if (!has && map.ResolveWithFallback(capability, out var resolved, out _))
                {
                    fallback = $" -> fallback: {resolved}";
                }
                sb.AppendLine($"  {capability,-8} : {(has ? "OK" : "--")}  clip={clipName}{fallback}");
            }
            Debug.Log(sb.ToString());
        }
    }
}