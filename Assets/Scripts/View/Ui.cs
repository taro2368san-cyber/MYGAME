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

    // モンスターボールの絵（上が top の色、下が白、まんなかに黒い帯とボタン）。色ごとに1回だけ作る
    static readonly System.Collections.Generic.Dictionary<Color, Sprite> balls = new System.Collections.Generic.Dictionary<Color, Sprite>();
    public static Sprite Ball(Color top)
    {
        if (balls.TryGetValue(top, out var s)) return s;
        const int n = 64; float c = n / 2f, r = n / 2f - 1;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                Color col = d > r ? Color.clear
                    : d > r - 3 || Mathf.Abs(dy) < 3 && d > 10 ? Color.black      // ふち・帯
                    : d < 6 ? Color.white : d <= 10 ? Color.black                   // まんなかのボタン
                    : dy > 0 ? top : Color.white;
                tex.SetPixel(x, y, col);
            }
        tex.Apply();
        return balls[top] = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    // ドット絵を文字の表から作る（1文字＝1ドット、上の行が絵の上）。表にない文字・足りない所は透明
    public static Sprite Pixels(string[] rows, System.Collections.Generic.Dictionary<char, Color> palette)
    {
        int h = rows.Length, w = 0;
        foreach (var r in rows) w = Mathf.Max(w, r.Length);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                tex.SetPixel(x, h - 1 - y, x < rows[y].Length && palette.TryGetValue(rows[y][x], out var c) ? c : Color.clear);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
    }

    // メニューの絵（UI仕様書 E）：いどう くつ／どうぐ つりざお／マップ 地図／ずかん 本／せってい スパナ。ポケモンは Ball。仮の絵
    static readonly System.Collections.Generic.Dictionary<char, Color> IconColors = new System.Collections.Generic.Dictionary<char, Color>
    {
        ['k'] = new Color(0.12f, 0.1f, 0.1f), ['b'] = new Color(0.6f, 0.36f, 0.16f), ['w'] = Color.white, ['s'] = new Color(0.85f, 0.85f, 0.85f),
        ['r'] = new Color(0.85f, 0.2f, 0.2f), ['R'] = new Color(0.55f, 0.1f, 0.1f), ['y'] = new Color(0.96f, 0.88f, 0.6f), ['Y'] = new Color(0.7f, 0.5f, 0.25f),
        ['u'] = new Color(0.35f, 0.6f, 0.95f), ['g'] = new Color(0.4f, 0.75f, 0.35f), ['m'] = new Color(0.75f, 0.78f, 0.82f), ['M'] = new Color(0.45f, 0.48f, 0.52f),
    };
    static readonly System.Collections.Generic.Dictionary<string, string[]> IconArt = new System.Collections.Generic.Dictionary<string, string[]>
    {
        ["くつ"] = new[] { "", "", "", "....kkkkk", "....kbbbk", "....kbbbk", "....kbbbk", "....kbbbbkk", "....kbbbbbbkk", "...kbbbbbbbbbk", "..kbbbbbbbbbbbk", "..kbbbbbbbbbbbk", "..kkkkkkkkkkkkk", "..kwwwwwwwwwwwk", "..kkkkkkkkkkkkk" },
        ["つりざお"] = new[] { "..............kk", ".............kbk", "............kbks", "...........kbk.s", "..........kbk..s", ".........kbk...s", "........kbk....s", ".......kbk.....s", "......kbk......s", ".....kbk.......s", "....kbk........s", "...kRk......s..s", "..kRRk......ssss", ".kRRk", ".kkk" },
        ["地図"] = new[] { "", ".kkkkkkkkkkkkkk", ".kyyyyyyyyyyyyk", ".kyuuyyyyyyyyyk", ".kyuuuyyyygggyk", ".kyyuuyyyygggyk", ".kyyyyyyyyyggyk", ".kyyYYYYYyyyyyk", ".kyyyyyyYyyyyyk", ".kyyyyyyyYryryk", ".kyggyyyyyYrryk", ".kgggyyyyyyryrk", ".kyyyyyyyyyyyyk", ".kkkkkkkkkkkkkk" },
        ["本"] = new[] { "", "...kkkkkkkkkkk", "...kRrrrrrrrrk", "...kRrrrrrrrrk", "...kRrwwwwwrrk", "...kRrwkkkwrrk", "...kRrwwwwwrrk", "...kRrrrrrrrrk", "...kRrrrrrrrrk", "...kRrrrrrrrrk", "...kRrrrrrrrrk", "...kRrrrrrrrrk", "...kRwwwwwwwwk", "...kkkkkkkkkkk" },
        ["スパナ"] = new[] { "...........mm.m", "..........mm..mm", "..........mm..mm", "..........mmmmmM", "...........mmmM", "..........mmM", ".........mmM", "........mmM", ".......mmM", "......mmM", ".....mmM", "....mmM", "...mmM", "..mmM", "..mM" },
    };
    static readonly System.Collections.Generic.Dictionary<string, Sprite> icons = new System.Collections.Generic.Dictionary<string, Sprite>();
    public static Sprite Icon(string name)
    {
        if (!icons.TryGetValue(name, out var s)) icons[name] = s = IconArt.TryGetValue(name, out var art) ? Pixels(art, IconColors) : null;
        return s;
    }

    // バッジ（仮の絵）：ジムごとの形と色。取っていなければ黒い影（UI仕様書 C）
    public static readonly string[] BadgeGyms = { "ニビジム", "ハナダジム", "クチバジム", "タマムシジム", "セキチクジム", "ヤマブキジム", "グレンジム", "トキワジム" };
    static readonly Color[] BadgeColors =
    {
        new Color(0.62f, 0.62f, 0.66f), new Color(0.3f, 0.55f, 0.95f), new Color(1f, 0.6f, 0.15f), new Color(0.5f, 0.85f, 0.4f),
        new Color(0.95f, 0.45f, 0.7f), new Color(0.95f, 0.8f, 0.25f), new Color(0.9f, 0.25f, 0.2f), new Color(0.3f, 0.7f, 0.35f),
    };
    static readonly System.Collections.Generic.Dictionary<int, Sprite> badges = new System.Collections.Generic.Dictionary<int, Sprite>();
    public static Sprite Badge(int index, bool owned)
    {
        int key = index * 2 + (owned ? 1 : 0);
        if (badges.TryGetValue(key, out var s)) return s;
        const int n = 32; const float r = 14f;
        bool Inside(int px, int py)
        {
            float x = px + 0.5f - n / 2f, y = py + 0.5f - n / 2f, d = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
            switch (index)
            {
                case 0: return Mathf.Abs(x) <= r * 0.9f && Mathf.Abs(y) <= r * 0.9f && Mathf.Abs(x) + Mathf.Abs(y) <= r * 1.3f;   // 八角形
                case 1: return Vector2.Distance(new Vector2(x, y), new Vector2(0, -4)) <= r * 0.65f || (y >= -4 && Mathf.Abs(x) <= r * 0.65f * (1 - (y + 4) / 17f)); // しずく
                case 2: return d <= r * (0.55f + 0.45f * Mathf.Pow(Mathf.Abs(Mathf.Cos(4 * a)), 3));                           // 8本のとげ
                case 3: return d <= r * (0.7f + 0.3f * Mathf.Cos(5 * a));                                                       // 花
                case 4: { float u = x / r * 1.25f, v = y / r * 1.25f + 0.2f; float q = u * u + v * v - 1; return q * q * q - u * u * v * v * v <= 0; } // ハート
                case 5: return d <= r && d >= r * 0.45f;                                                                        // 輪
                case 6: return y >= -r && y <= r * 0.8f && Mathf.Abs(x) <= (r * 0.8f - y) * 0.6f;                               // 三角（火山）
                default: { float c = 0.7071f, u = (x + y) * c, v = (y - x) * c; return Vector2.Distance(new Vector2(u, v), new Vector2(-r * 0.55f, 0)) <= r && Vector2.Distance(new Vector2(u, v), new Vector2(r * 0.55f, 0)) <= r; } // 葉
            }
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var col = BadgeColors[index];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!Inside(x, y)) { tex.SetPixel(x, y, Color.clear); continue; }
                bool edge = !Inside(x - 1, y) || !Inside(x + 1, y) || !Inside(x, y - 1) || !Inside(x, y + 1);
                var c = index == 3 && !edge ? Color.HSVToRGB(Mathf.Repeat(Mathf.Atan2(y - n / 2f, x - n / 2f) / (2 * Mathf.PI), 1), 0.55f, 1f) : col; // レインボーは虹色
                tex.SetPixel(x, y, !owned ? new Color(0.05f, 0.05f, 0.08f, 0.85f) : edge ? c * 0.55f + new Color(0, 0, 0, 0.45f) : c);
            }
        tex.Apply();
        return badges[key] = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    // メダル（D のバッジの数の下地）：金の丸に こい金のふち
    static Sprite medal;
    public static Sprite Medal
    {
        get
        {
            if (medal != null) return medal;
            const int n = 32; float c = n / 2f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                    tex.SetPixel(x, y, d > 15 ? Color.clear : d > 13 ? new Color(0.6f, 0.42f, 0.08f) : new Color(0.98f, 0.8f, 0.25f));
                }
            tex.Apply();
            return medal = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }
    }

    // 角の丸い四角（UI仕様書 4 の案）。9スライスなので どの大きさでも角の丸さは同じ
    static Sprite rounded;
    public static Sprite Rounded
    {
        get
        {
            if (rounded != null) return rounded;
            const int n = 32; const float r = 10;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(r - (x + 0.5f), x + 0.5f - (n - r))), dy = Mathf.Max(0, Mathf.Max(r - (y + 0.5f), y + 0.5f - (n - r)));
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)));
                }
            tex.Apply();
            return rounded = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(12, 12, 12, 12));
        }
    }

    // 部品の角を丸くする（絵を出す Image には使わない：絵の縦横の比が保てなくなる）
    public static Image Round(Image img) { img.sprite = Rounded; img.type = Image.Type.Sliced; return img; }

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
        Round(go.GetComponent<Image>()).color = new Color(0.2f, 0.25f, 0.35f, 0.95f);
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
