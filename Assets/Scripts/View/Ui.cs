using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 仮の画面をコードで組み立てるための小さな部品集（uGUI）。見た目を作り込むときは、ここを差し替えるかプレハブにする
public static class Ui
{
    static Font font;
    public static Font Font => font != null ? font : font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

    // 丸い絵（ポケモンの仮の見た目に使う）
    static Sprite circle;
    public static Sprite Circle
    {
        get
        {
            if (circle != null) return circle;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f));
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(n / 2f - d)));
                }
            tex.Apply();
            return circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }
    }

    public static Canvas Canvas(string name)
    {
        var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; // 画面サイズに合わせて伸び縮み（仕様書1.3）
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        return canvas;
    }

    // 親の中の位置を、左下(0,0)〜右上(1,1)の割合で決めた四角
    public static RectTransform Rect(Transform parent, string name, Vector2 min, Vector2 max)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = min; rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    public static Image Panel(Transform parent, string name, Vector2 min, Vector2 max, Color color)
    {
        var img = Rect(parent, name, min, max).gameObject.AddComponent<Image>();
        img.color = color;
        return img;
    }

    public static Text Label(Transform parent, string text, int size, TextAnchor align = TextAnchor.UpperLeft, Color? color = null)
    {
        var rt = Rect(parent, "Text", Vector2.zero, Vector2.one);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font; t.fontSize = size; t.alignment = align; t.text = text;
        t.color = color ?? Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    // 縦か横に並べる箱
    public static RectTransform Stack(Transform parent, string name, Vector2 min, Vector2 max, bool vertical, float spacing = 8, int padding = 8)
    {
        var rt = Rect(parent, name, min, max);
        HorizontalOrVerticalLayoutGroup g = vertical ? rt.gameObject.AddComponent<VerticalLayoutGroup>() : (HorizontalOrVerticalLayoutGroup)rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        g.spacing = spacing;
        g.padding = new RectOffset(padding, padding, padding, padding);
        g.childControlWidth = g.childControlHeight = true;
        g.childForceExpandWidth = vertical; g.childForceExpandHeight = !vertical;
        g.childAlignment = vertical ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
        return rt;
    }

    public static Button Button(Transform parent, string text, Action onClick, float width = 220, float height = 56)
    {
        var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(0.2f, 0.25f, 0.35f, 0.95f);
        var le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width; le.preferredHeight = height; le.minHeight = height;
        var b = go.GetComponent<Button>();
        b.onClick.AddListener(() => onClick());
        Label(go.transform, text, 24, TextAnchor.MiddleCenter);
        return b;
    }

    // 1行の入力欄（名前など）。文字が変わるたびに onChange
    // allow：入力してよい文字か（null なら何でも）
    public static InputField Input(Transform parent, string text, int maxLength, float width, Action<string> onChange, Func<char, bool> allow = null)
    {
        var go = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(0.95f, 0.95f, 0.95f, 1);
        var le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width; le.preferredHeight = 56; le.minHeight = 56;
        var field = go.GetComponent<InputField>();
        var shown = Label(go.transform, "", 26, TextAnchor.MiddleLeft, Color.black);
        shown.rectTransform.offsetMin = new Vector2(10, 0); shown.rectTransform.offsetMax = new Vector2(-10, 0);
        shown.supportRichText = false;
        var hint = Label(go.transform, "", 26, TextAnchor.MiddleLeft, new Color(0.5f, 0.5f, 0.5f));
        hint.rectTransform.offsetMin = shown.rectTransform.offsetMin; hint.rectTransform.offsetMax = shown.rectTransform.offsetMax;
        hint.fontStyle = FontStyle.Italic;
        field.textComponent = shown;
        field.placeholder = hint;
        field.characterLimit = maxLength;
        if (allow != null) field.onValidateInput = (s, i, c) => allow(c) ? c : '\0';
        field.text = text;
        field.onValueChanged.AddListener(v => onChange(v));
        return field;
    }

    // 並べる箱の中に置く文字（高さは中身に合わせる）
    public static Text Line(Transform parent, string text, int size, Color? color = null)
    {
        var t = Label(parent, text, size, TextAnchor.UpperLeft, color);
        t.gameObject.AddComponent<LayoutElement>();
        t.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return t;
    }

    public static void Clear(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
    }
}
