using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using WeChatWASM;

namespace PetV1.WeChat
{
    /// <summary>
    /// 把微信转换插件的配置对象结构导出到文本文件。
    ///
    /// 为什么需要这个：配置类 WXEditorScriptObject / WXProjectConf 定义在预编译的
    /// wx-editor.dll 里，源码中找不到字段定义，MCP 的内联脚本又不允许用反射。
    /// 这里用普通 Editor 脚本（属于 Assembly-CSharp-Editor，自动引用该 dll）反射一次，
    /// 把字段名和默认值落盘，后续配置脚本按真实字段名写。
    /// </summary>
    public static class PetWeChatDump
    {
        private const string OutPath = "C:/dening_project/pets_game/unity_3d_pets/.cache/wxconf.txt";

        [MenuItem("Pet V1/10. Dump WeChat Config")]
        public static void Dump()
        {
            var sb = new StringBuilder();
            var conf = WXConvertCore.config;

            sb.AppendLine("root=" + conf.GetType().FullName);
            DumpFields(conf, sb, 1);

            File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[PetV1] 微信插件配置已导出：" + OutPath);
        }

        private static void DumpFields(object obj, StringBuilder sb, int depth)
        {
            string pad = new string(' ', depth * 2);
            var fields = obj.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.IsStatic) continue;

                object v = null;
                try { v = f.GetValue(obj); }
                catch { v = "<read-error>"; }

                bool isNested = v != null && !(v is string) && v.GetType().IsClass
                                && v.GetType().Namespace != null
                                && v.GetType().Namespace.StartsWith("WeChatWASM");

                if (isNested)
                {
                    sb.AppendLine(pad + f.Name + " : " + v.GetType().Name);
                    DumpFields(v, sb, depth + 1);
                }
                else
                {
                    string val = v == null ? "null" : v.ToString();
                    if (val.Length > 80) val = val.Substring(0, 80) + "...";
                    sb.AppendLine(pad + f.Name + " = " + val);
                }
            }
        }
    }
}
