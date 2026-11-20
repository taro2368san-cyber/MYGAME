using System;
using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 施設メニューの枠と ポケモン一覧（仕様書 16.10 の骨組み、相談28・29）
// 画面のまん中の大きな枠。上に見出し（施設名）と右上の文（所持金など）、左に一覧、右に 選んでいる行の説明
// ポケモンの行（AddCard）は 一覧ではなく カードで並べる：大きいカードは横に3枚（手持ち）、小さいカードは その下（控え）
// 中身は いつものメニューの行（menu）なので、キーボードの数字キー・コントローラーの十字キーと ○× は そのまま使える
// 使い方：メニューを作るところで OpenFrame を呼び、行を Add / AddCard で足す。行の説明は Detail。描くのは DrawMenu
public partial class GameRunner
{
    RectTransform frameRoot, frameList, frameBig, frameSmall;
    Text frameTitleText, frameSideText, framePromptText, frameDetailText;
    GameObject frameDetailBox;
    string frameTitle, frameSide;                                              // 見出しと 右上の文（frameTitle が null なら 枠は出さない）
    readonly Dictionary<int, string> menuDetail = new Dictionary<int, string>(); // 行の番号 → 右に出す説明
    readonly Dictionary<int, (PokemonState mon, bool small, string caption)> menuCards = new Dictionary<int, (PokemonState, bool, string)>();
    bool FrameOpen => frameTitle != null && !EventOpen && !ConfirmOpen;

    static readonly Color CardColor = new Color(0.1f, 0.35f, 0.3f, 0.95f);
    static readonly Color FaintedColor = new Color(0.3f, 0.3f, 0.32f, 0.95f);   // 戦闘不能はグレー（16.9）

    void BuildFrame()
    {
        frameRoot = Ui.Panel(canvas.transform, "Frame", Vector2.zero, Vector2.one, Ui.DimColor).rectTransform; // 後ろを暗くして、押せなくする
        var box = Ui.Frame(frameRoot, "Box", new Vector2(0.17f, 0.1f), new Vector2(0.83f, 0.9f)).transform;
        frameTitleText = Ui.Label(Ui.Rect(box, "Title", new Vector2(0.03f, 0.9f), new Vector2(0.6f, 0.99f)), "", 40, TextAnchor.MiddleLeft, new Color(1, 0.85f, 0.4f));
        frameSideText = Ui.Label(Ui.Rect(box, "Side", new Vector2(0.5f, 0.9f), new Vector2(0.97f, 0.99f)), "", 30, TextAnchor.MiddleRight);
        framePromptText = Ui.Label(Ui.Rect(box, "Prompt", new Vector2(0.03f, 0.82f), new Vector2(0.97f, 0.9f)), "", 24, TextAnchor.MiddleLeft, new Color(1, 0.9f, 0.5f));
        frameBig = Ui.Stack(box, "Cards", new Vector2(0.03f, 0.42f), new Vector2(0.97f, 0.82f), false, 16, 0);
        frameSmall = Ui.Stack(box, "SmallCards", new Vector2(0.03f, 0.25f), new Vector2(0.97f, 0.41f), false, 8, 0);
        foreach (var strip in new[] { frameBig, frameSmall })
        {
            var g = strip.GetComponent<HorizontalLayoutGroup>();
            g.childAlignment = TextAnchor.MiddleCenter; g.childForceExpandHeight = false; g.childForceExpandWidth = false;
        }
        frameList = Ui.Rect(box, "List", Vector2.zero, Vector2.one);
        frameList.gameObject.AddComponent<GridLayoutGroup>();
        var detail = Ui.Round(Ui.Panel(box, "Detail", new Vector2(0.54f, 0.03f), new Vector2(0.97f, 0.81f), new Color(0, 0, 0, 0.3f)));
        frameDetailBox = detail.gameObject;
        frameDetailText = Ui.Label(detail.transform, "", 26);
        frameDetailText.rectTransform.offsetMin = new Vector2(16, 12); frameDetailText.rectTransform.offsetMax = new Vector2(-16, -12);
        frameRoot.gameObject.SetActive(false);
    }

    // 枠を出す（メニューを作るところから呼ぶ）。title＝見出し、side＝右上の文
    void OpenFrame(string title, string side = "") { frameTitle = title; frameSide = side; }

    // 直前に足した行の説明（枠の右に出す）
    void Detail(string text) { if (!string.IsNullOrEmpty(text)) menuDetail[menu.Count - 1] = text; }

    // ポケモンのカードの行。small＝小さいカード（控え）。caption＝カードの下の ひとこと（「先頭」「あずける」など）
    void AddCard(PokemonState m, string caption, Action action, KeyCode key = KeyCode.None, bool enabled = true, bool small = false)
    {
        menuCards[menu.Count] = (m, small, caption);
        Add($"{m.Species} Lv{m.Level}", action, key, enabled);
    }

    // DrawMenu から：枠の中に メニューの行を描く。作ったボタンを 行の順で返す
    List<Button> DrawFrame(string prompt)
    {
        frameTitleText.text = frameTitle; frameSideText.text = frameSide; framePromptText.text = prompt;
        Ui.Clear(frameList); Ui.Clear(frameBig); Ui.Clear(frameSmall);
        bool cards = menuCards.Count > 0;
        int rows = menu.Count - menuCards.Count;
        frameBig.gameObject.SetActive(cards); frameSmall.gameObject.SetActive(cards);
        frameDetailBox.SetActive(!cards);
        // 一覧：カードがあれば 下に横3列、なければ 左半分に縦1列（多いときは 行を低くする）
        frameList.anchorMin = new Vector2(0.03f, 0.03f);
        frameList.anchorMax = cards ? new Vector2(0.97f, menuCards.Values.Any(c => c.small) ? 0.24f : 0.4f) : new Vector2(0.52f, 0.81f);
        var grid = frameList.GetComponent<GridLayoutGroup>();
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = cards ? 3 : 1;
        grid.spacing = new Vector2(10, 6);
        float rowHeight = cards || rows <= 11 ? 50 : Mathf.Max(26, 620f / rows - 6);
        Canvas.ForceUpdateCanvases(); // 一覧の幅は 画面の形で変わるので、置いたあとの幅から決める
        float width = frameList.rect.width;
        grid.cellSize = new Vector2(cards ? (width - 20) / 3 : width - 8, rowHeight);

        var made = new List<Button>();
        for (int i = 0; i < menu.Count; i++)
        {
            var (label, action, _, enabled) = menu[i];
            Button b;
            if (menuCards.TryGetValue(i, out var card)) b = card.small ? SmallCard(card.mon, card.caption, action) : BigCard(card.mon, card.caption, action);
            else
            {
                b = Ui.Button(frameList, i < 9 ? $"{i + 1}. {label}" : label, action);
                var t = b.GetComponentInChildren<Text>();
                t.color = new Color(1, 1, 1, enabled ? 1 : 0.35f);
                t.alignment = TextAnchor.MiddleLeft; t.rectTransform.offsetMin = new Vector2(14, 0);
                t.resizeTextForBestFit = true; t.resizeTextMinSize = 12; t.resizeTextMaxSize = 24; t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            }
            b.interactable = enabled;
            if (!enabled && card.mon != null) b.GetComponent<CanvasGroup>().alpha = 0.55f;
            // マウスを のせた行の説明を出す
            int k = i;
            var trigger = b.gameObject.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => ShowDetail(k));
            trigger.triggers.Add(enter);
            made.Add(b);
        }
        return made;
    }

    void ShowDetail(int row) => frameDetailText.text = menuDetail.TryGetValue(row, out var d) ? d : "";

    // 大きいカード（手持ち）：絵・名前・Lv・HP の棒・状態・もちもの（相談29）
    Button BigCard(PokemonState m, string caption, Action action)
    {
        var b = Ui.Button(frameBig, "", action, 350, 320);
        b.gameObject.AddComponent<CanvasGroup>();
        b.image.color = m.Fainted ? FaintedColor : CardColor;
        var t = b.transform;
        var art = Ui.Panel(t, "Art", new Vector2(0.08f, 0.5f), new Vector2(0.92f, 0.97f), m.Fainted ? new Color(0.55f, 0.55f, 0.55f) : Color.white);
        art.sprite = BattleSprite(m.Species) ?? PokemonIcon(m.Species); art.preserveAspect = true; art.raycastTarget = false; art.enabled = art.sprite != null;
        int max = Pokemons.MaxHp(data, m);
        Ui.Label(Ui.Rect(t, "Name", new Vector2(0.05f, 0.38f), new Vector2(0.95f, 0.5f)), $"{m.Species}　Lv{m.Level}", 26, TextAnchor.MiddleCenter);
        HpBar(Ui.Rect(t, "Hp", new Vector2(0.1f, 0.31f), new Vector2(0.9f, 0.36f)), m.Hp, max);
        Ui.Label(Ui.Rect(t, "HpText", new Vector2(0.05f, 0.21f), new Vector2(0.95f, 0.31f)), m.Fainted ? "たおれている" : $"HP {m.Hp}/{max}", 22, TextAnchor.MiddleCenter);
        string status = Pokemons.StatusName(m.Status);
        Ui.Label(Ui.Rect(t, "State", new Vector2(0.05f, 0.11f), new Vector2(0.95f, 0.21f)),
            (status != "" ? $"［{status}］" : "") + (m.HeldItem != "" ? $"もちもの：{m.HeldItem}" : status != "" ? "" : "もちもの なし"), 20, TextAnchor.MiddleCenter, new Color(0.85f, 0.95f, 0.9f));
        Ui.Label(Ui.Rect(t, "Caption", new Vector2(0.05f, 0.01f), new Vector2(0.95f, 0.11f)), caption, 20, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.5f));
        return b;
    }

    // 小さいカード（控え）：絵・名前・Lv
    Button SmallCard(PokemonState m, string caption, Action action)
    {
        var b = Ui.Button(frameSmall, "", action, 108, 118);
        b.gameObject.AddComponent<CanvasGroup>();
        b.image.color = m.Fainted ? FaintedColor : CardColor;
        var art = Ui.Panel(b.transform, "Art", new Vector2(0.15f, 0.42f), new Vector2(0.85f, 0.98f), Color.white);
        art.sprite = PokemonIcon(m.Species); art.preserveAspect = true; art.raycastTarget = false; art.enabled = art.sprite != null;
        var name = Ui.Label(Ui.Rect(b.transform, "Name", new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.42f)), $"{m.Species}\nLv{m.Level}" + (caption != "" ? " " + caption : ""), 15, TextAnchor.MiddleCenter);
        name.resizeTextForBestFit = true; name.resizeTextMinSize = 9; name.resizeTextMaxSize = 15;
        return b;
    }

    // HP の棒：半分より上は緑、2割より上は黄、それより下は赤
    static void HpBar(RectTransform at, int hp, int max)
    {
        Ui.Panel(at, "Back", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0.6f)).raycastTarget = false;
        float r = max > 0 ? Mathf.Clamp01((float)hp / max) : 0;
        Ui.Panel(at, "Fill", Vector2.zero, new Vector2(r, 1), r > 0.5f ? new Color(0.3f, 0.85f, 0.35f) : r > 0.2f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.9f, 0.25f, 0.2f)).raycastTarget = false;
    }

    // ポケモン1体の説明（枠の右に出す）
    string MonDetail(PokemonState m)
    {
        var sp = Pokemons.Of(data, m);
        string status = Pokemons.StatusName(m.Status);
        return $"{m.Species}　Lv{m.Level}\nタイプ：{sp.Type}　じゃくてん：{(sp.Weakness != "" ? sp.Weakness : "なし")}\nHP {m.Hp}/{Pokemons.MaxHp(data, m)}" + (status != "" ? $"　［{status}］" : "")
            + $"\nコスト：{Pokemons.Cost10(data, m) / 10.0:0.0}\nもちもの：{(m.HeldItem != "" ? m.HeldItem : "なし")}";
    }

    // 道具の説明（枠の右に出す）：効果・値段・持っている数
    string ItemDetail(PlayerState p, string item)
    {
        var info = data.FindItem(item);
        int have = p.Balls.TryGetValue(item, out int balls) ? balls : p.PokemonBag.Concat(p.TrainerBag).Count(x => x == item);
        return $"{item}\n\n{info?.Effect}\n\n持っている数：{have}";
    }
}
