using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// イベント画面（UI仕様書 5）：人物が話しかけてくる場面。メイン画面をグレーにして、場所の背景・まんなかに立ち絵・下にセリフ欄（選択肢もここ）
// 使う場面：ライバル、拠点の人（ロケット団員・フジろうじん・シルフの社長・博物館の研究員・オーキド博士）、ランダムイベントの人（モミ・チャンピオンたちなど）、ジムリーダー、職業マスのトレーナー
// 一般のトレーナーとジムトレーナーは使わない（メイン画面のセリフ欄だけ）
// 立ち絵は Resources/Portraits/<話す人の名前>、背景は Resources/EventBackgrounds/<背景の名前> に絵を置けば それを使う。なければ コードで描いた仮の絵
public partial class GameRunner
{
    static readonly string[] BasePeople = { "ロケット団員", "フジろうじん", "フジ老人", "シルフの社長", "博物館の研究員", "オーキド博士" };
    const float EventLineSeconds = 1.2f; // 1行を出しきってから 見せておく時間（○・スペースで早送り）

    RectTransform eventRoot, eventChoices;
    Image eventBg, eventArt;
    Text eventName, eventText, eventBgLabel, eventPrompt;
    string eventPerson;          // 立ち絵の人（開いていなければ null）
    string eventFull = "";       // 出している1行
    float eventTyped;
    bool EventOpen => eventPerson != null;

    void BuildEventScreen()
    {
        eventRoot = Ui.Panel(canvas.transform, "EventScreen", Vector2.zero, Vector2.one, new Color(0.1f, 0.1f, 0.12f, 0.65f)).rectTransform; // メイン画面をグレーに（押せない）
        var frame = Ui.Panel(eventRoot, "Frame", new Vector2(0.12f, 0.06f), new Vector2(0.88f, 0.94f), new Color(0.85f, 0.7f, 0.3f));
        eventBg = Ui.Panel(frame.transform, "Background", Vector2.zero, Vector2.one, Color.white);
        eventBg.rectTransform.offsetMin = new Vector2(4, 4); eventBg.rectTransform.offsetMax = new Vector2(-4, -4);
        eventBgLabel = Ui.Label(Ui.Rect(eventBg.transform, "BgLabel", new Vector2(0.01f, 0.93f), new Vector2(0.5f, 1)), "", 20, TextAnchor.MiddleLeft, new Color(1, 1, 1, 0.6f));
        eventArt = Ui.Panel(eventBg.transform, "Portrait", new Vector2(0.33f, 0.3f), new Vector2(0.67f, 0.97f), Color.white);
        eventArt.preserveAspect = true; eventArt.raycastTarget = false;
        // セリフ欄（メイン画面と同じ見た目：濃い緑に金のふち）。右に選択肢
        var gold = Ui.Round(Ui.Panel(eventBg.transform, "Talk", new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.3f), new Color(0.85f, 0.7f, 0.3f)));
        var box = Ui.Round(Ui.Panel(gold.transform, "Inner", Vector2.zero, Vector2.one, new Color(0.04f, 0.25f, 0.14f, 0.96f)));
        box.rectTransform.offsetMin = new Vector2(3, 3); box.rectTransform.offsetMax = new Vector2(-3, -3);
        eventText = Ui.Label(box.transform, "", 30, TextAnchor.UpperLeft);
        eventText.rectTransform.offsetMin = new Vector2(24, 14); eventText.rectTransform.offsetMax = new Vector2(-24, -14);
        var plate = Ui.Panel(eventBg.transform, "NamePlate", new Vector2(0.04f, 0.3f), new Vector2(0.3f, 0.37f), new Color(0.04f, 0.25f, 0.14f, 0.96f));
        plate.gameObject.AddComponent<Outline>().effectColor = new Color(0.85f, 0.7f, 0.3f);
        eventName = Ui.Label(plate.transform, "", 26, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.5f));
        eventChoices = Ui.Stack(box.transform, "Choices", new Vector2(0.62f, 0.04f), new Vector2(0.99f, 0.96f), true, 4, 4);
        eventChoices.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
        eventPrompt = Ui.Label(Ui.Rect(eventBg.transform, "Prompt", new Vector2(0.55f, 0.3f), new Vector2(0.96f, 0.36f)), "", 22, TextAnchor.LowerRight, new Color(1, 0.9f, 0.5f));
        eventPrompt.gameObject.AddComponent<Outline>().effectColor = Color.black;
        eventRoot.gameObject.SetActive(false);
    }

    // この会話は イベント画面で見せるか。見せるなら 立ち絵の人の名前（「???」で始まっても あとで名乗る人）
    string EventPersonOf(Dialog d, List<(string Speaker, string Text)> lines)
    {
        if (lines == null) return null;
        string named = lines.Select(l => l.Speaker).FirstOrDefault(s => s != "" && s != "???");
        if (named == null) return null;
        if (d.Key.StartsWith("rival_")) return named;                                  // ライバル
        if (BasePeople.Contains(named)) return named;                                   // 拠点の人
        if (data.RandomEvents.Any(e => !string.IsNullOrEmpty(e.TextKey) && (d.Key == e.TextKey || d.Key.StartsWith(e.TextKey + "_")))) return named; // ランダムイベントの人
        return null;
    }

    // ---- 開く：背景と立ち絵が出てくる（同じ人なら そのまま）----
    IEnumerator OpenEvent(string person, int player)
    {
        if (eventPerson == person && eventRoot.gameObject.activeSelf) yield break;
        bool fresh = !EventOpen;
        eventPerson = person;
        int square = player >= 0 && player < S.Players.Count ? S.Players[player].Square : S.CurrentPlayer.Square;
        string bg = EventBackgroundKind(square);
        eventBg.sprite = Resources.Load<Sprite>("EventBackgrounds/" + bg) ?? EventBackground(bg);
        eventBgLabel.text = eventBg.sprite.name.StartsWith("仮") ? $"（背景：{bg}）" : "";
        eventArt.sprite = Resources.Load<Sprite>("Portraits/" + person) ?? Portrait(person);
        eventArt.color = Color.white;
        eventName.text = person; eventText.text = ""; eventFull = ""; eventPrompt.text = "";
        Ui.Clear(eventChoices);
        eventRoot.gameObject.SetActive(true);
        eventRoot.SetAsLastSibling();
        banner.transform.parent.SetAsLastSibling(); // 大きな知らせは イベント画面の上に
        menuPanel.gameObject.SetActive(false);
        // 立ち絵が 下から すっと出てくる
        float total = shortBattle ? 0.15f : 0.3f;
        var rt = eventArt.rectTransform;
        for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0, 1, t / total);
            rt.anchoredPosition = new Vector2(0, Mathf.Lerp(fresh ? -120 : -40, 0, k));
            eventArt.color = new Color(1, 1, 1, k);
            yield return null;
        }
        rt.anchoredPosition = Vector2.zero; eventArt.color = Color.white;
    }

    void CloseEvent()
    {
        if (!EventOpen) return;
        eventPerson = null;
        eventRoot.gameObject.SetActive(false);
        Ui.Clear(eventChoices);
    }

    // セリフを1行ずつ（文言CSVの順番どおり）。1文字ずつ出して、出しきったら少し見せる。「???」の行は 立ち絵を影にする
    IEnumerator EventLines(List<(string Speaker, string Text)> lines)
    {
        foreach (var (speaker, line) in lines)
        {
            if (speaker != "") eventName.text = speaker;
            eventArt.color = speaker == "???" ? new Color(0, 0, 0, 0.9f) : Color.white;
            eventFull = line; eventTyped = 0;
            eventText.text = "";
            while (eventTyped < eventFull.Length) yield return null; // AnimateEvent が進める
            yield return Wait(EventLineSeconds);
        }
    }

    // LateUpdate から：セリフの1文字ずつ（○・スペースで全文）
    void AnimateEvent()
    {
        if (!EventOpen || eventTyped >= eventFull.Length) return;
        eventTyped = SkipHeld() ? eventFull.Length : eventTyped + TypeSpeed * Time.deltaTime;
        eventText.text = eventFull.Substring(0, Mathf.Min(eventFull.Length, (int)eventTyped));
    }

    // 選択肢（はい／いいえ など）はセリフ欄の右に。メニューと同じ行を並べる（キーとコントローラーは メニューのまま）
    void DrawEventChoices(string title)
    {
        Ui.Clear(eventChoices);
        eventPrompt.text = menu.Count > 0 ? title : "";
        for (int i = 0; i < menu.Count; i++)
        {
            var (label, action, _, enabled) = menu[i];
            var b = Ui.Button(eventChoices, label, action, 300, 44);
            b.interactable = enabled;
            var text = b.GetComponentInChildren<Text>();
            text.fontSize = 22; text.color = new Color(1, 1, 1, enabled ? 1 : 0.4f);
            if (i == menuCursor) { b.image.color = new Color(1f, 0.85f, 0.4f); text.color = Color.black; }
        }
        eventText.rectTransform.anchorMax = new Vector2(menu.Count > 0 ? 0.6f : 1, 1); // 選択肢があるときは 文を左に寄せる
    }

    // 命令を流し終わったとき：選択肢を待っている（イベント・拠点・ライバルマスなど）なら開いたまま、そうでなければ閉じる
    void AfterEventQueue()
    {
        if (!EventOpen) return;
        bool talking = S.Phase == Phase.Facility && S.Facility != null && (S.Facility.Kind == FacilityKind.Event || S.Facility.Kind == FacilityKind.RocketBase
            || S.Facility.Kind == FacilityKind.Trade || S.Facility.Kind == FacilityKind.Rival || S.Facility.Kind == FacilityKind.JobOffer);
        if (!talking) CloseEvent();
    }

    // ---- 背景（5章、12種類）：マスの種類 → 場所 → 場所の種別 の順に ----
    static readonly string[] EventCavePlaces = { "おつきみやま", "おつきみ山", "イワヤマトンネル", "ハナダの洞窟", "ディグダの穴", "ロケット団アジト", "ポケモン屋敷", "むじんはつでんしょ", "チャンピオンロード", "ふたご島" };

    string EventBackgroundKind(int square)
    {
        var sq = board[square];
        if (sq.Type == "ジム" || sq.Type == "ポケモンセンター" || sq.Type == "ショップ") return sq.Type;
        switch (sq.Place)
        {
            case "ポケモンタワー": case "シルフカンパニー": case "サントアンヌ号": return sq.Place;
            case "セキエイ高原": return "リーグ";
            case "シロガネ山": return "雪山";
        }
        if (sq.Sea || sq.Type == "船着場" || sq.Place.StartsWith("うずまき島")) return "水";
        if (EventCavePlaces.Contains(sq.Place)) return "洞窟";
        string kind = data.Places.FirstOrDefault(p => p.Name == sq.Place)?.Kind ?? "";
        return kind == "町" ? "町" : kind == "水道" ? "水" : kind == "ダンジョン" ? "洞窟" : kind == "山" ? "雪山" : "草"; // 道路・森・サファリ・地方の島は 草
    }

    // 仮の背景：上の色から下の色へのグラデーションに、背景ごとの簡単な模様
    readonly Dictionary<string, Sprite> eventBackgrounds = new Dictionary<string, Sprite>();
    Sprite EventBackground(string kind)
    {
        if (eventBackgrounds.TryGetValue(kind, out var s)) return s;
        Color top, bottom, mark;
        switch (kind)
        {
            case "ジム": top = new Color(0.75f, 0.72f, 0.85f); bottom = new Color(0.45f, 0.4f, 0.6f); mark = new Color(1f, 0.85f, 0.4f); break;
            case "ポケモンセンター": top = new Color(1f, 0.85f, 0.85f); bottom = new Color(0.95f, 0.65f, 0.65f); mark = Color.white; break;
            case "ショップ": top = new Color(0.8f, 0.9f, 1f); bottom = new Color(0.45f, 0.65f, 0.9f); mark = Color.white; break;
            case "ポケモンタワー": top = new Color(0.35f, 0.25f, 0.45f); bottom = new Color(0.15f, 0.1f, 0.2f); mark = new Color(0.6f, 0.5f, 0.75f); break;
            case "シルフカンパニー": top = new Color(0.85f, 0.85f, 0.9f); bottom = new Color(0.55f, 0.55f, 0.65f); mark = new Color(0.3f, 0.5f, 0.9f); break;
            case "サントアンヌ号": top = new Color(0.95f, 0.9f, 0.75f); bottom = new Color(0.7f, 0.55f, 0.35f); mark = new Color(0.3f, 0.5f, 0.85f); break;
            case "リーグ": top = new Color(0.3f, 0.2f, 0.5f); bottom = new Color(0.15f, 0.1f, 0.3f); mark = new Color(1f, 0.85f, 0.3f); break;
            case "水": top = new Color(0.6f, 0.85f, 1f); bottom = new Color(0.15f, 0.4f, 0.8f); mark = Color.white; break;
            case "町": top = new Color(0.65f, 0.85f, 1f); bottom = new Color(0.85f, 0.8f, 0.65f); mark = new Color(0.9f, 0.45f, 0.35f); break;
            case "洞窟": top = new Color(0.2f, 0.15f, 0.12f); bottom = new Color(0.45f, 0.35f, 0.25f); mark = new Color(0.3f, 0.24f, 0.18f); break;
            case "雪山": top = new Color(0.8f, 0.88f, 0.95f); bottom = Color.white; mark = new Color(0.7f, 0.78f, 0.88f); break;
            default: top = new Color(0.6f, 0.85f, 1f); bottom = new Color(0.4f, 0.75f, 0.35f); mark = new Color(0.3f, 0.6f, 0.25f); break; // 草
        }
        const int w = 160, h = 100;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var rnd = new System.Random(kind.GetHashCode());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = Color.Lerp(bottom, top, y / (float)h);
                bool pattern = kind == "水" ? (y + x / 10) % 11 == 0 && y < 60
                    : kind == "草" ? y < 30 && rnd.Next(9) == 0
                    : kind == "町" ? y < 45 && y > 20 && x % 40 < 22 && (x % 40 > 4)            // 家の並び
                    : kind == "洞窟" ? rnd.Next(12) == 0
                    : kind == "雪山" ? (y > 50 && Mathf.Abs(x - 80) < (100 - y) * 1.6f)          // 山の形
                    : kind == "ポケモンセンター" || kind == "ショップ" ? y > 60 && y < 66              // 横の帯
                    : kind == "ジム" || kind == "リーグ" ? x % 32 == 0 && y < 70                       // 柱
                    : kind == "シルフカンパニー" ? x % 16 < 6 && y % 14 < 6 && y > 30               // 窓
                    : kind == "サントアンヌ号" ? y % 8 == 0 && y < 40
                    : kind == "ポケモンタワー" ? rnd.Next(40) == 0 : false;
                if (pattern) c = Color.Lerp(c, mark, 0.6f);
                c.a = 1;
                tex.SetPixel(x, y, c);
            }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        sprite.name = "仮" + kind;
        return eventBackgrounds[kind] = sprite;
    }

    // 仮の立ち絵：人の形（頭と体）を 名前ごとの色で。名前は下の札に出る
    readonly Dictionary<string, Sprite> portraits = new Dictionary<string, Sprite>();
    Sprite Portrait(string name)
    {
        if (portraits.TryGetValue(name, out var s)) return s;
        const int w = 64, h = 96;
        float hue = Mathf.Abs(name.GetHashCode() % 360) / 360f;
        Color body = Color.HSVToRGB(hue, 0.55f, 0.85f), dark = Color.HSVToRGB(hue, 0.6f, 0.45f), skin = new Color(1f, 0.85f, 0.7f), hair = Color.HSVToRGB((hue + 0.5f) % 1, 0.4f, 0.35f);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        bool Head(int x, int y) => (x - 32) * (x - 32) + (y - 74) * (y - 74) <= 13 * 13;
        bool Hair(int x, int y) => Head(x, y) && y > 76;
        bool Body(int x, int y) => y < 64 && y > 2 && Mathf.Abs(x - 32) < 10 + (60 - y) * 0.25f && !(y < 26 && Mathf.Abs(x - 32) < 3); // 胴と足
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = Head(x, y) || Body(x, y);
                bool edge = inside && !(Head(x - 1, y) || Body(x - 1, y)) | !(Head(x + 1, y) || Body(x + 1, y)) | !(Head(x, y + 1) || Body(x, y + 1)) | !(Head(x, y - 1) || Body(x, y - 1));
                tex.SetPixel(x, y, !inside ? Color.clear : edge ? dark : Hair(x, y) ? hair : Head(x, y) ? skin : body);
            }
        tex.Apply();
        return portraits[name] = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0));
    }
}
