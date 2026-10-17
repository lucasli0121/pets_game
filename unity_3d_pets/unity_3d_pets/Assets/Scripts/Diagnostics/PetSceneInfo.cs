using PetV1.Animation;
using PetV1.Core;
using PetV1.Pet;
using UnityEngine;

namespace PetV1.Diagnostics
{
    /// <summary>
    /// 场景说明文字（IMGUI 实现）。挂在场景里的一个空物体上，
    /// 用来显示当前场景要验证什么，避免和美术资产强耦合。
    /// </summary>
    public class PetSceneInfo : MonoBehaviour
    {
        [SerializeField, TextArea(3, 20)] private string text = "";
        [SerializeField] private Vector2 position = new Vector2(12f, 12f);
        [SerializeField] private float width = 470f;
        [SerializeField] private float height = 240f;
        [SerializeField] private bool visible = true;

        private GUIStyle _style;
        private Texture2D _bg;

        public void SetText(string value) => text = value;

        private void OnGUI()
        {
            if (!visible || string.IsNullOrEmpty(text)) return;
            EnsureStyle();
            GUILayout.BeginArea(new Rect(position.x, position.y, width, height), _style);
            GUILayout.Label(text, _style);
            GUILayout.EndArea();
        }

        private void EnsureStyle()
        {
            if (_style != null) return;
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
            _bg.Apply();
            _style = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10),
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                fontSize = 12,
            };
            _style.normal.background = _bg;
            _style.normal.textColor = Color.white;
        }
    }
}
