using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetV1.Model
{
    /// <summary>食品分类。与后台 <c>config_food.category</c> 的取值逐字一致。</summary>
    public enum FoodCategory
    {
        /// <summary>主食。后台 category = "MAIN"。</summary>
        Main = 0,
        /// <summary>零食。后台 category = "SNACK"。</summary>
        Snack = 1,
        /// <summary>特殊。后台 category = "SPECIAL"。</summary>
        Special = 2,
    }

    /// <summary>
    /// 一条食品配置。字段与后台 <c>domain.FoodItem</c> 逐字对齐。
    ///
    /// 名称/价格/效果**全部来自后台**，包内这份是兜底默认值。
    /// 展板上的四种食品（牛肉狗粮 / 狗狗饼干 / 鸡肉干 / 生日蛋糕）与后台
    /// <c>DefaultSnapshot()</c> 的五条不一致，按「一切以后台为准」的口径，
    /// 这里用后台那五条。展板只用来定视觉规格（卡片布局、图标位置、按钮样式）。
    ///
    /// <see cref="iconKey"/> 用 string 而不是贴图引用：
    /// 后台下发的 icon 是资源 key（后台 seed 是 "icon_food_beef"），
    /// 要走 Addressables 或远端下载，编在包里的是同一批 key 的本地兜底。
    /// 直接存 Texture 会让后台改图必须重发版。
    /// </summary>
    [Serializable]
    public class FoodItemConfig
    {
        [Tooltip("后台 food.id。背包与商店都靠它对齐，改名等于换一种食物。")]
        public string id = "";

        [Tooltip("后台 food.name，直出中文。V1 只做简体中文，多一层 i18n key 运营改不动。")]
        public string name = "";

        [Tooltip("后台 food.category。")]
        public FoodCategory category = FoodCategory.Main;

        [Tooltip("后台 food.price，积分价格。")]
        public int price = 10;

        [Tooltip("后台 food.satiety，饱腹增量。")]
        public int satiety = 0;

        [Tooltip("后台 food.happiness，快乐增量。")]
        public int happiness = 0;

        [Tooltip("后台 food.exp，成长经验增量。")]
        public int exp = 0;

        [Tooltip("后台 food.icon，资源 key。UI 据此选图标；取不到时画占位色块。")]
        public string iconKey = "";

        [Tooltip("后台 food.sort，列表排序，小的在前。")]
        public int sort = 1;

        [Tooltip("后台 food.enabled。false 的不显示、不许买。")]
        public bool enabled = true;

        [Tooltip("后台 food.minLevel。等级门槛，不足时商店里置灰。")]
        public int minLevel = 1;

        /// <summary>分类的中文名，直接给 Tab 用。避免 UI 层到处写 switch。</summary>
        public string CategoryName
        {
            get
            {
                switch (category)
                {
                    case FoodCategory.Main: return "主食";
                    case FoodCategory.Snack: return "零食";
                    default: return "特殊";
                }
            }
        }

        /// <summary>给 /api/v1/shop/buy 与 /api/v1/pet/feed 用的分类串。后台按这个字符串匹配。</summary>
        public string CategoryCode
        {
            get
            {
                switch (category)
                {
                    case FoodCategory.Main: return "MAIN";
                    case FoodCategory.Snack: return "SNACK";
                    default: return "SPECIAL";
                }
            }
        }

        /// <summary>
        /// 效果摘要，按「饱腹 → 快乐 → 成长经验」固定顺序拼。
        /// 顺序固定是为了让同一批卡片每次渲染的文案一致 —— 按字典序拼会
        /// 出现「有的卡片先显示经验有的先显示快乐」，列表看着像随机。
        /// </summary>
        public string EffectSummary()
        {
            var parts = new List<string>(3);
            if (satiety != 0) parts.Add($"饱腹 +{satiety}");
            if (happiness != 0) parts.Add($"快乐 +{happiness}");
            if (exp != 0) parts.Add($"成长 +{exp}");
            return parts.Count == 0 ? "无效果" : string.Join("　", parts);
        }

        /// <summary>这个食物有没有实际效果。用于「立即喂食」按钮是否可点。</summary>
        public bool HasEffect =>
            satiety != 0 || happiness != 0 || exp != 0;
    }

    /// <summary>
    /// 食品 / 道具 / 特殊 三类商品的配置集合。
    ///
    /// 为什么和 <see cref="PetSceneLibrary"/> 分开而不是塞进去：
    /// 场景库对应后台 <c>config_scene</c>（有缩略图路径、有构图参数、走运营加图流程），
    /// 商品配置对应 <c>config_food</c>（纯数值 + 图标 key）。
    /// 两者下发节奏不同：场景加图要走美术审核，商品改价是运营日常操作。
    /// 合在一个 asset 里会导致改价格也要重新走一遍场景的构建流程。
    /// </summary>
    [CreateAssetMenu(fileName = "DefaultFoodCatalog",
                     menuName = "PetV1/默认食品商城配置")]
    public class FoodCatalog : ScriptableObject
    {
        [Header("商品列表")]
        [Tooltip("对应后台 config_food。字段名与 domain.FoodItem 一致。")]
        public List<FoodItemConfig> items = new List<FoodItemConfig>();

        [Header("初始资产")]
        [Tooltip("首次进入的积分余额。展板示意 500，后台 config_points.initial 也是 500。")]
        public int initialPoints = 500;

        [Tooltip("首次进入时白送的食品，itemId -> 数量。让原型一进来背包不是空的。")]
        public List<InitialItem> initialItems = new List<InitialItem>();

        /// <summary>配置项。用于「白送初始物品」，字段名与后台无对应（纯客户端便利项）。</summary>
        [Serializable]
        public struct InitialItem
        {
            public string itemId;
            public int count;

            public InitialItem(string id, int n)
            {
                itemId = id;
                count = n;
            }
        }

        private Dictionary<string, FoodItemConfig> _lookup;

        public FoodItemConfig Find(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            BuildLookup();
            return _lookup.TryGetValue(itemId, out var c) ? c : null;
        }

        /// <summary>
        /// 某个分类下的可售商品，按 sort 升序。
        /// 传 <see cref="FoodCategory"/> 之外的值用 <see cref="AllSorted"/>。
        /// </summary>
        public List<FoodItemConfig> ByCategory(FoodCategory cat)
        {
            var list = new List<FoodItemConfig>();
            foreach (var it in items)
            {
                if (it == null || !it.enabled) continue;
                if (it.category == cat) list.Add(it);
            }
            list.Sort((a, b) => a.sort.CompareTo(b.sort));
            return list;
        }

        /// <summary>「全部」分类。商店与背包都用它。</summary>
        public List<FoodItemConfig> AllSorted()
        {
            var list = new List<FoodItemConfig>();
            foreach (var it in items)
            {
                if (it != null && it.enabled) list.Add(it);
            }
            list.Sort((a, b) => a.sort.CompareTo(b.sort));
            return list;
        }

        /// <summary>
        /// 用服务端下发的商品整份替换本地列表（config/get 的 <c>config.food</c>）。
        ///
        /// 为什么是整份替换而不是逐条合并：
        /// 运营在后台把某个商品 enabled 改成 false、或者删掉一条，
        /// 逐条合并会把旧的那条留下来 —— 表现是「后台下架了客户端还在卖」。
        ///
        /// 排序不在这里做：后台按 sort 排好再下发，客户端再排一次会让
        /// 「sort 相同的并列项」按不稳定的比较函数重排，每次进游戏顺序都可能不一样。
        ///
        /// 只在内存里替换，**不写回 asset**：asset 是构建期产物，
        /// 每次跑 Build Home Scene 都会被 FoodCatalog.Fallback() 重写，
        /// 运行期的数据写进去下一次构建就冲掉了。
        /// </summary>
        public void ReplaceItems(List<FoodItemConfig> newItems)
        {
            items = newItems ?? new List<FoodItemConfig>();
            _lookup = null;   // 缓存必须失效，否则 Find/ByCategory 还是旧数据
        }

        private void BuildLookup()
        {
            // 缓存失效判据用「数量没变」：编辑器里改了某条的价格不会改变数量，
            // 所以 OnValidate 里显式置空。这里再叠一个数量判据防运行期热改。
            if (_lookup != null && _lookup.Count == items.Count) return;
            _lookup = new Dictionary<string, FoodItemConfig>(items.Count);
            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.id)) continue;
                _lookup[it.id] = it;
            }
        }

        private void OnValidate()
        {
            _lookup = null;
        }

        /// <summary>
        /// 不依赖资产的兜底默认配置。后台 <c>DefaultSnapshot()</c> 里那五条。
        /// 连包内 asset 都没有时用它，保证编辑器里不配任何东西也能跑。
        /// </summary>
        public static FoodCatalog Fallback()
        {
            var c = CreateInstance<FoodCatalog>();
            c.name = "FoodCatalog(Fallback)";
            c.items = new List<FoodItemConfig>
            {
                new FoodItemConfig { id = "food_normal",  name = "普通狗粮", category = FoodCategory.Main,
                    price = 5,  satiety = 50, happiness = 0,  exp = 5, iconKey = "icon_food_normal",  sort = 1 },
                new FoodItemConfig { id = "food_beef",name = "牛肉罐头", category = FoodCategory.Main,
                    price = 10, satiety = 100, happiness = 10, exp = 10, iconKey = "icon_food_beef",  sort = 2 },
                new FoodItemConfig { id = "food_biscuit", name = "磨牙饼干", category = FoodCategory.Snack,
                    price = 15, satiety = 5, happiness = 5,  exp = 5, iconKey = "icon_food_biscuit", sort = 3 },
                new FoodItemConfig { id = "food_chicken", name = "鸡胸肉", category = FoodCategory.Snack,
                    price = 20, satiety = 5, happiness = 10, exp = 8, iconKey = "icon_food_chicken", sort = 4 },
                new FoodItemConfig { id = "food_cake",   name = "生日蛋糕", category = FoodCategory.Special,
                    price = 50, satiety = 30, happiness = 50, exp = 20, iconKey = "icon_food_cake",   sort = 5 },
            };
            c.initialItems = new List<InitialItem>
            {
                new InitialItem("food_normal", 3),
                new InitialItem("food_beef", 2),
                new InitialItem("food_biscuit", 5),
                new InitialItem("food_chicken", 2),
                new InitialItem("food_cake", 1),
            };
            return c;
        }
    }
}
