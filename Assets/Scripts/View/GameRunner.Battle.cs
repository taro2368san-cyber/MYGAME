using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// バトル画面（UI仕様書 7）：背景（草・海・洞窟・室内明・室内暗）に天気のアニメーション、左に自分・右に相手のポケモン（台座の上）
// 上に名前とHP（左 自分・右 相手）、上の中央に残りターン。コマンドはポケモンの下（攻撃側に □こうげき ○とくこう △必殺 R1どうぐ、防御側に □ぼうぎょ ○とくぼう △カウンター R1にげる）
// 攻撃側は大きく、防御側は小さく。攻守が交代するたびに 両方が横へ出て、横から入る
// ponytail: 背景・台座・オーラ・天気・カードは コードで描いた仮の絵。技ごとのエフェクトは まだ（Feel.cs の AttackAnim）
public partial class GameRunner
{
    // コマンドの色（□ うすい橙・○ 水色・△ うすい緑・R1 灰色）。並びは □ ○ △ R1（攻撃・防御の enum の順と同じ）
    static readonly Color[] CommandColors = { new Color(1f, 0.76f, 0.5f), new Color(0.55f, 0.85f, 1f), new Color(0.62f, 0.92f, 0.6f), new Color(0.82f, 0.82f, 0.82f) };
    const float BigScale = 1.15f, SmallScale = 0.85f; // 攻撃側・防御側の大きさ
    const float PixelScale = 3.2f;                    // バトルの絵の 1ドットの大きさ（画面の単位）

    Image battleBg;
    RectTransform weatherLayer, cardRow;
    Text turnText, cardHint;
    readonly CommandPad[] pads = new CommandPad[2];
    int shownAttacker = -1;          // 大きく見せている側（交代の演出に使う）。-1 ならまだ決まっていない
    int promptSide = -1;             // いまコマンドを選んでいる側（画面のボタンを押せる側）
    BattleKind shownKind;
    int cardCursor;                  // 選んでいるカード（混ぜたあとの並びで何枚目か）
    readonly List<RectTransform> cardAt = new List<RectTransform>(); // 混ぜたあとの位置ごとのカード
    Image menuPanel;                 // E メニュー（コマンドとカードは バトル画面で選ぶので、そのあいだは隠す）
    Image messageGold;               // G セリフ欄（バトル中は少し細くする）

    void BuildBattleScene()
    {
        battlePanel = Ui.Rect(canvas.transform, "Battle", Vector2.zero, Vector2.one);
        battleBg = Ui.Panel(battlePanel, "Background", Vector2.zero, Vector2.one, Color.white);
        weatherLayer = Ui.Rect(battlePanel, "Weather", Vector2.zero, Vector2.one);
        for (int i = 0; i < 2; i++)
        {
            float x0 = i == 0 ? 0.02f : 0.62f, cx = i == 0 ? 0.27f : 0.73f;
            var info = Ui.Panel(battlePanel, $"Info{i}", new Vector2(x0, 0.83f), new Vector2(x0 + 0.36f, 0.98f), HudColor).rectTransform;
            var plate = Ui.Panel(battlePanel, $"Plate{i}", new Vector2(cx - 0.13f, 0.43f), new Vector2(cx + 0.13f, 0.51f), new Color(0.15f, 0.12f, 0.1f, 0.35f)); // 台座（つぶれた丸）
            plate.sprite = Ui.Circle; plate.raycastTarget = false;
            var stage = Ui.Rect(battlePanel, $"Stage{i}", new Vector2(cx - 0.12f, 0.46f), new Vector2(cx + 0.12f, 0.8f));
            stage.pivot = new Vector2(0.5f, 0); // 大きさは 台座のところから変える
            cards[i] = new BattleCard(info, stage, this);
            pads[i] = new CommandPad(Ui.Rect(battlePanel, $"Commands{i}", new Vector2(cx - 0.15f, 0.23f), new Vector2(cx + 0.15f, 0.42f)), this);
        }
        turnText = Ui.Label(Ui.Rect(battlePanel, "Turns", new Vector2(0.4f, 0.84f), new Vector2(0.6f, 0.98f)), "", 26, TextAnchor.MiddleCenter);
        turnText.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.7f);
        cardRow = Ui.Rect(battlePanel, "Cards", new Vector2(0.15f, 0.45f), new Vector2(0.85f, 0.78f));
        cardHint = Ui.Label(Ui.Rect(battlePanel, "CardHint", new Vector2(0.15f, 0.78f), new Vector2(0.85f, 0.83f)), "", 28, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.5f));
        cardHint.gameObject.AddComponent<Outline>().effectColor = Color.black;
        battlePanel.gameObject.SetActive(false);
    }

    // バトル画面を出す・しまう。メイン画面の部品（A〜D）は隠し、セリフ欄は R1 のボタンのあいだに入るよう少し細くする
    void ShowBattle(bool on)
    {
        battlePanel.gameObject.SetActive(on);
        hudRoot.SetActive(!on);
        var rt = messageGold.rectTransform;
        rt.anchorMin = on ? new Vector2(0.33f, 0.01f) : new Vector2(0.32f, 0.01f);
        rt.anchorMax = on ? new Vector2(0.67f, 0.22f) : new Vector2(0.75f, 0.25f);
        if (!on) { Ui.Clear(cardRow); cardAt.Clear(); cardHint.text = ""; }
    }

    // バトルが始まった・続きから：背景を決めて、前の子・カードを消す
    void StartBattleScene(BattleKind kind)
    {
        ShowBattle(true);
        menuPanel.gameObject.SetActive(false); // 演出のあいだ メニューは出さない（選ぶときに DrawMenu が出す）
        shownKind = kind; shownAttacker = -1;
        battleBg.sprite = BattleBackground(BattleBackgroundKind());
        cards[0].Clear(); cards[1].Clear();
        foreach (var p in pads) p.Show(false);
        Ui.Clear(cardRow); cardAt.Clear(); cardHint.text = "";
        UpdateTurns();
    }

    void UpdateTurns()
    {
        var b = S.Battle;
        if (b == null) return;
        int limit = (int)data.Required(b.Full ? "round_limit_full" : "round_limit_quick");
        turnText.text = $"残りターン\n<size=60>{Mathf.Max(0, limit - b.Rounds)}</size>";
    }

    // 画面を描き直すとき（RefreshAll）：大きさ、コマンド（押せる側だけ押せる）、カードのカーソル
    void RefreshBattleScene()
    {
        var b = S.Battle;
        if (b == null || !battlePanel.gameObject.activeSelf) return;
        UpdateTurns();
        bool commands = b.Step == BattleStep.Commands && b.Sides.All(s => s.Active >= 0);
        if (commands) { shownAttacker = b.Attacker; for (int i = 0; i < 2; i++) cards[i].SetScale(i == b.Attacker ? BigScale : SmallScale); }
        string flee = b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament ? "降参" : "にげる";
        for (int i = 0; i < 2; i++)
        {
            var pad = pads[i];
            pad.Show(commands);
            if (!commands) continue;
            bool attack = i == b.Attacker;
            pad.SetRole(attack, flee);
            bool mine = i == promptSide && subMenu == null && menu.Count == 4;
            pad.SetEnabled(k => mine && menu[k].enabled);
            bool chosen = b.Sides[i].Player >= 0 && (attack ? b.AttackCmd != null : b.DefenseCmd != null);
            pad.SetState(mine ? "えらんでください" : chosen ? "選択済み" : "");
        }
        // カード：選ぶのを待っているなら、カーソルのカードを少し持ち上げる（読み込んだ直後などで並んでいなければ 裏のまま並べる）
        if (b.Step == BattleStep.Initiative)
        {
            if (cardAt.Count != b.Cards.Count) LayCardsDown(b.Cards.Count);
            cardCursor = Mathf.Clamp(cardCursor, 0, cardAt.Count - 1);
            bool human = !IsCpu(b.Sides[0].Player);
            for (int j = 0; j < cardAt.Count; j++)
                cardAt[j].anchoredPosition = new Vector2(CardX(j, cardAt.Count), j == cardCursor && human ? 30 : 0);
            cardHint.text = human ? $"{b.Sides[0].Name}さん、カードを 1枚 えらんでください（←→ で えらんで ○）" : "";
        }
    }

    // コマンドとカードは バトル画面で選ぶ（E メニューは隠す）
    bool SceneInput => S != null && S.Phase == Phase.Battle && S.Battle != null && subMenu == null
        && (S.Battle.Step == BattleStep.Commands || S.Battle.Step == BattleStep.Initiative);

    // 画面のコマンドを押した（□ ○ △ R1 = 0〜3。メニューの行と同じ並び）
    void PressCommand(int k)
    {
        if (playing || !SceneInput || S.Battle.Step != BattleStep.Commands || menu.Count != 4 || !menu[k].enabled) return;
        menu[k].action();
    }

    // ---- ④ 結果発表：選ばなかったコマンドをグレーに。選んだ側が ぐっと縮み、コマンドの色のオーラ（攻撃側、防御側の順）----
    IEnumerator RevealCommands(RoundResolved r)
    {
        int a = r.Attacker, d = 1 - a;
        string flee = shownKind == BattleKind.Player || shownKind == BattleKind.Tournament ? "降参" : "にげる";
        pads[a].Show(true); pads[d].Show(true);
        pads[a].SetRole(true, flee); pads[d].SetRole(false, flee);
        pads[a].SetState(""); pads[d].SetState("");
        int ai = (int)r.Attack, di = (int)r.Defense;
        pads[a].Reveal(ai);
        yield return cards[a].PowerUp(CommandColors[ai]);
        pads[d].Reveal(di);
        yield return cards[d].PowerUp(CommandColors[di]);
    }

    // ---- ⑥ 攻守の交代：両方が横へ出ていき、大きさを入れかえて 横から入ってくる ----
    IEnumerator SwapSides(int attacker)
    {
        if (shownAttacker == attacker) yield break;
        if (shownAttacker < 0) { shownAttacker = attacker; for (int i = 0; i < 2; i++) cards[i].SetScale(i == attacker ? BigScale : SmallScale); yield break; } // はじめは そのまま
        float t = shortBattle ? 0.2f : 0.4f;
        foreach (var p in pads) p.Show(false);
        yield return Together(cards[0].Slide(-1, true, t), cards[1].Slide(1, true, t));
        shownAttacker = attacker;
        for (int i = 0; i < 2; i++) { cards[i].SetScale(i == attacker ? BigScale : SmallScale); cards[i].ClearAura(); }
        UpdateTurns();
        yield return Together(cards[0].Slide(-1, false, t), cards[1].Slide(1, false, t));
    }

    // ---- ① 登場：野生は横からスライド、ほかはモンスターボールから ----
    IEnumerator Enter(int side)
    {
        if (side == 1 && shownKind == BattleKind.Wild) yield return cards[side].Slide(1, false, shortBattle ? 0.25f : 0.5f);
        else yield return cards[side].FromBall();
    }

    // ---- ② 先攻・後攻のカード：表で並べて 枚数を見せる → 裏返す → 混ぜる → 仕掛けた側が選ぶ（PickCard）→ めくる ----
    static float CardX(int slot, int count) => (slot - (count - 1) / 2f) * 190;

    RectTransform MakeCard(int index)
    {
        var img = Ui.Panel(cardRow, $"Card{index}", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Color.white);
        img.rectTransform.sizeDelta = new Vector2(150, 210);
        img.gameObject.AddComponent<Outline>().effectColor = new Color(0.1f, 0.1f, 0.1f);
        Ui.Label(img.transform, "", 26, TextAnchor.MiddleCenter, Color.black);
        img.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;
        return img.rectTransform;
    }

    void CardFace(RectTransform card, string text, bool front)
    {
        card.GetComponent<Image>().color = front ? new Color(1f, 0.96f, 0.8f) : new Color(0.15f, 0.25f, 0.55f);
        var t = card.GetComponentInChildren<Text>();
        t.text = front ? text : "？"; t.color = front ? Color.black : Color.white; t.fontSize = front ? 26 : 64;
    }

    string CardText(int side) => $"{cards[side].Species}\nの\n<size=34>先攻</size>";

    // 裏のまま すぐ並べる（読み込んだ直後など）
    void LayCardsDown(int count)
    {
        Ui.Clear(cardRow); cardAt.Clear();
        for (int j = 0; j < count; j++) { var c = MakeCard(j); CardFace(c, "", false); c.anchoredPosition = new Vector2(CardX(j, count), 0); cardAt.Add(c); }
        BindCards();
    }

    void BindCards()
    {
        for (int j = 0; j < cardAt.Count; j++)
        {
            int k = j;
            var btn = cardAt[j].GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => { if (!playing && SceneInput && S.Battle.Step == BattleStep.Initiative && !IsCpu(S.Battle.Sides[0].Player)) { cardCursor = k; Send(new PickCard { Player = S.Battle.Sides[0].Player, Card = k }); } });
        }
    }

    IEnumerator DealCards(CardsDealt cd)
    {
        float s = shortBattle ? 0.5f : 1f;
        Ui.Clear(cardRow); cardAt.Clear();
        int n = cd.Cards.Count;
        // 表で見せる並び：枚数の多い側（すばやい側）を左から
        var shown = cd.Cards.OrderByDescending(v => cd.Cards.Count(x => x == v)).ThenBy(v => v).ToList();
        // shown の i 枚目が、混ぜたあと何枚目に行くか（同じ側のカードどうしは どれでもよい）
        var used = new bool[n];
        var to = new int[n];
        for (int i = 0; i < n; i++) { int j = Enumerable.Range(0, n).First(k => !used[k] && cd.Cards[k] == shown[i]); used[j] = true; to[i] = j; }
        var made = new List<RectTransform>();
        for (int i = 0; i < n; i++) { var c = MakeCard(i); CardFace(c, CardText(shown[i]), true); c.anchoredPosition = new Vector2(CardX(i, n), -500); made.Add(c); }
        // 下から出てくる
        for (float t = 0; t < 0.35f * s && !SkipHeld(); t += Time.deltaTime)
        {
            for (int i = 0; i < n; i++) made[i].anchoredPosition = new Vector2(CardX(i, n), Mathf.Lerp(-500, 0, Mathf.SmoothStep(0, 1, t / (0.35f * s))));
            yield return null;
        }
        for (int i = 0; i < n; i++) made[i].anchoredPosition = new Vector2(CardX(i, n), 0);
        cardHint.text = string.Join("・", shown.Distinct().Select(v => $"{cards[v].Species}の 先攻 {cd.Cards.Count(x => x == v)}枚"));
        yield return Wait(1.0f * s);
        // 裏返す
        yield return Flip(made, null);
        cardHint.text = "まぜます…";
        // 混ぜる：真ん中の1か所に ぴったり重ねて集め、重ねたまま ゆすってから、1枚ずつ配る（目で追えないように）
        for (float t = 0; t < 0.3f * s && !SkipHeld(); t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0, 1, t / (0.3f * s));
            for (int i = 0; i < n; i++) made[i].anchoredPosition = new Vector2(Mathf.Lerp(CardX(i, n), 0, k), 0);
            yield return null;
        }
        foreach (var c in made) c.anchoredPosition = Vector2.zero;
        foreach (var c in made.OrderBy(_ => UnityEngine.Random.value).ToList()) c.SetAsLastSibling(); // 重なりの順も 入れかえる
        for (float t = 0; t < 0.5f * s && !SkipHeld(); t += Time.deltaTime) // 重ねたまま ゆする
        {
            foreach (var c in made) c.localRotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(-8f, 8f));
            yield return null;
        }
        foreach (var c in made) c.localRotation = Quaternion.identity;
        // 上のカードから 1枚ずつ、置く場所へ（置く順番は ばらばら）
        var dealOrder = Enumerable.Range(0, n).OrderBy(_ => UnityEngine.Random.value).ToList();
        foreach (int slot in dealOrder)
        {
            var c = made[Array.IndexOf(to, slot)];
            c.SetAsLastSibling();
            for (float t = 0; t < 0.15f * s && !SkipHeld(); t += Time.deltaTime)
            {
                c.anchoredPosition = new Vector2(Mathf.Lerp(0, CardX(slot, n), Mathf.SmoothStep(0, 1, t / (0.15f * s))), 0);
                yield return null;
            }
            c.anchoredPosition = new Vector2(CardX(slot, n), 0);
        }
        cardAt.Clear();
        for (int j = 0; j < n; j++) cardAt.Add(made[Array.IndexOf(to, j)]);
        BindCards();
        cardCursor = 0;
        cardHint.text = "";
    }

    // カードを裏返す（横に縮めて 面をかえて 広げる）。faces が null なら裏に、あれば その文字の表に
    IEnumerator Flip(List<RectTransform> list, List<string> faces)
    {
        float half = shortBattle ? 0.08f : 0.15f;
        for (int pass = 0; pass < 2; pass++)
        {
            for (float t = 0; t < half && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / half;
                foreach (var c in list) c.localScale = new Vector3(pass == 0 ? 1 - k : k, 1, 1);
                yield return null;
            }
            if (pass == 0) for (int i = 0; i < list.Count; i++) CardFace(list[i], faces?[i] ?? "", faces != null);
        }
        foreach (var c in list) c.localScale = Vector3.one;
    }

    // 選んだカードをめくって、先攻を見せる。ほかのカードも表にして、下へ消える
    IEnumerator RevealCard(InitiativeDrawn x)
    {
        float s = shortBattle ? 0.5f : 1f;
        cardHint.text = "";
        if (cardAt.Count != x.Cards.Count) // カードを選ばずに決まった（へんしん・せんせいのツメ）
        {
            yield return ShowBig($"{x.FirstPokemon}の 先攻！", new Color(1f, 0.85f, 0.4f));
            yield return Wait(1f * s);
            banner.transform.parent.gameObject.SetActive(false);
            yield break;
        }
        var picked = cardAt[x.Picked];
        picked.anchoredPosition = new Vector2(CardX(x.Picked, cardAt.Count), 30);
        yield return Flip(new List<RectTransform> { picked }, new List<string> { CardText(x.Cards[x.Picked]) });
        picked.GetComponent<Image>().color = new Color(1f, 0.85f, 0.4f);
        cardHint.text = $"{x.FirstPokemon}の 先攻！";
        yield return Wait(0.6f * s);
        var others = Enumerable.Range(0, cardAt.Count).Where(j => j != x.Picked).ToList();
        yield return Flip(others.Select(j => cardAt[j]).ToList(), others.Select(j => CardText(x.Cards[j])).ToList());
        yield return Wait(0.6f * s);
        for (float t = 0; t < 0.3f * s && !SkipHeld(); t += Time.deltaTime)
        {
            foreach (var c in cardAt) c.anchoredPosition += new Vector2(0, -2400 * Time.deltaTime);
            yield return null;
        }
        Ui.Clear(cardRow); cardAt.Clear(); cardHint.text = "";
    }

    void MoveCardCursor(int d)
    {
        if (cardAt.Count == 0) return;
        cardCursor = Mathf.Clamp(cardCursor + d, 0, cardAt.Count - 1);
        RefreshBattleScene();
    }

    // ---- 背景（7.4）：海上マス・つりは 海、ジムは 室内明。あとは場所の名前と種別で ----
    static readonly string[] CavePlaces = { "おつきみやま", "おつきみ山", "ディグダの穴", "イワヤマトンネル", "ハナダの洞窟", "チャンピオンロード", "ふたご島", "シロガネ山" };
    static readonly string[] BrightPlaces = { "シルフカンパニー", "サントアンヌ号", "セキエイ高原" };
    static readonly string[] DarkPlaces = { "ポケモンタワー", "ロケット団アジト", "ポケモン屋敷", "むじんはつでんしょ" };

    string BattleBackgroundKind()
    {
        var b = S.Battle;
        var sq = board[b.Square];
        bool fishing = S.History.Count > 0 && S.History[S.History.Count - 1] is Fish;
        if (sq.Sea || fishing) return "海";
        if (b.Kind == BattleKind.Gym || sq.Type == "ジム" || b.Square == board.StartSquare) return "室内明"; // 研究室はスタートのマス
        if (BrightPlaces.Contains(sq.Place)) return "室内明";
        if (DarkPlaces.Contains(sq.Place)) return "室内暗";
        if (CavePlaces.Contains(sq.Place)) return "洞窟";
        if (sq.Place.StartsWith("うずまき島")) return "海";
        string kind = data.Places.FirstOrDefault(p => p.Name == sq.Place)?.Kind ?? "";
        return kind == "水道" ? "海" : kind == "ダンジョン" || kind == "山" ? "洞窟" : "草"; // 道路・森・町・地方の島は 草
    }

    // 仮の背景：上が空や壁、下が地面や床のグラデーション（つぶつぶを少し）
    readonly Dictionary<string, Sprite> backgrounds = new Dictionary<string, Sprite>();
    Sprite BattleBackground(string kind)
    {
        if (backgrounds.TryGetValue(kind, out var s)) return s;
        Color top, horizon, ground, ground2;
        switch (kind)
        {
            case "海": top = new Color(0.55f, 0.8f, 0.98f); horizon = new Color(0.85f, 0.95f, 1f); ground = new Color(0.25f, 0.55f, 0.9f); ground2 = new Color(0.15f, 0.38f, 0.75f); break;
            case "洞窟": top = new Color(0.18f, 0.13f, 0.1f); horizon = new Color(0.35f, 0.27f, 0.2f); ground = new Color(0.5f, 0.4f, 0.3f); ground2 = new Color(0.38f, 0.3f, 0.22f); break;
            case "室内明": top = new Color(0.95f, 0.92f, 0.82f); horizon = new Color(0.85f, 0.8f, 0.68f); ground = new Color(0.82f, 0.68f, 0.48f); ground2 = new Color(0.7f, 0.56f, 0.38f); break;
            case "室内暗": top = new Color(0.12f, 0.08f, 0.18f); horizon = new Color(0.25f, 0.18f, 0.32f); ground = new Color(0.28f, 0.22f, 0.3f); ground2 = new Color(0.18f, 0.14f, 0.2f); break;
            default: top = new Color(0.5f, 0.78f, 0.98f); horizon = new Color(0.85f, 0.95f, 1f); ground = new Color(0.5f, 0.8f, 0.4f); ground2 = new Color(0.35f, 0.65f, 0.3f); break;
        }
        const int w = 160, h = 90, line = 52; // 地平線（下から）
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var rnd = new System.Random(kind.GetHashCode());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = y >= line ? Color.Lerp(horizon, top, (y - line) / (float)(h - line)) : Color.Lerp(ground2, ground, y / (float)line);
                if (y < line && rnd.Next(14) == 0) c *= 0.93f; // 地面のつぶつぶ
                if (kind == "海" && y < line && (y + x / 12) % 9 == 0) c = Color.Lerp(c, Color.white, 0.25f); // 波
                c.a = 1;
                tex.SetPixel(x, y, c);
            }
        tex.Apply();
        return backgrounds[kind] = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
    }

    // ---- 天気のアニメーション（LateUpdate から）：雨は 斜めに降る線、砂あらしは 横に舞う粒、晴れは 光がゆらぐ ----
    string weatherShown = "";
    readonly List<(RectTransform rt, Vector2 v)> particles = new List<(RectTransform, Vector2)>();
    Image sunGlow;

    void AnimateWeather()
    {
        if (battlePanel == null || !battlePanel.gameObject.activeSelf || S?.Battle == null) return;
        string w = S.Battle.Weather ?? "";
        var size = weatherLayer.rect.size;
        if (w != weatherShown)
        {
            weatherShown = w;
            Ui.Clear(weatherLayer); particles.Clear(); sunGlow = null;
            var rnd = new System.Random();
            int n = w == "雨" ? 70 : w == "砂あらし" ? 90 : 0;
            for (int i = 0; i < n; i++)
            {
                bool rain = w == "雨";
                var img = Ui.Panel(weatherLayer, "P", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), rain ? new Color(0.8f, 0.9f, 1f, 0.6f) : new Color(0.85f, 0.7f, 0.45f, 0.7f));
                img.raycastTarget = false;
                img.rectTransform.sizeDelta = rain ? new Vector2(3, 34) : new Vector2(7, 7);
                if (rain) img.rectTransform.localRotation = Quaternion.Euler(0, 0, 15);
                else img.sprite = Ui.Circle;
                img.rectTransform.anchoredPosition = new Vector2((float)(rnd.NextDouble() - 0.5) * size.x, (float)(rnd.NextDouble() - 0.5) * size.y);
                var v = rain ? new Vector2(-260, -1300) * (0.8f + (float)rnd.NextDouble() * 0.4f) : new Vector2(900, 0) * (0.6f + (float)rnd.NextDouble() * 0.8f);
                particles.Add((img.rectTransform, v));
            }
            if (w == "晴れ") { sunGlow = Ui.Panel(weatherLayer, "Sun", Vector2.zero, Vector2.one, new Color(1f, 0.9f, 0.5f, 0.15f)); sunGlow.raycastTarget = false; }
        }
        foreach (var (rt, v) in particles)
        {
            var p = rt.anchoredPosition + v * Time.deltaTime;
            if (w == "砂あらし") p.y += Mathf.Sin(Time.time * 3 + v.x * 0.02f) * 40 * Time.deltaTime;
            if (p.y < -size.y / 2) p.y += size.y;
            if (p.x < -size.x / 2) p.x += size.x;
            if (p.x > size.x / 2) p.x -= size.x;
            rt.anchoredPosition = p;
        }
        if (sunGlow != null) sunGlow.color = new Color(1f, 0.9f, 0.5f, 0.1f + 0.06f * Mathf.Sin(Time.time * 1.5f));
    }

    // ---- ポケモンの下のコマンド：△ 上、□ 左下、○ 右下、R1 その下に小さく ----
    sealed class CommandPad
    {
        readonly RectTransform root;
        readonly Image[] buttons = new Image[4];
        readonly Text[] labels = new Text[4];
        readonly Text state;
        bool[] enabled = new bool[4];

        public CommandPad(RectTransform root, GameRunner runner)
        {
            this.root = root;
            var places = new[]
            {
                (new Vector2(0.02f, 0.34f), new Vector2(0.49f, 0.64f)), // □
                (new Vector2(0.51f, 0.34f), new Vector2(0.98f, 0.64f)), // ○
                (new Vector2(0.26f, 0.68f), new Vector2(0.74f, 0.98f)), // △
                (new Vector2(0.3f, 0.02f), new Vector2(0.7f, 0.3f)),    // R1
            };
            for (int i = 0; i < 4; i++)
            {
                var img = Ui.Panel(root, $"Command{i}", places[i].Item1, places[i].Item2, CommandColors[i]);
                img.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.6f);
                var b = img.gameObject.AddComponent<Button>();
                b.transition = Selectable.Transition.None;
                int k = i;
                b.onClick.AddListener(() => runner.PressCommand(k));
                buttons[i] = img;
                labels[i] = Ui.Label(img.transform, "", i == 3 ? 22 : 26, TextAnchor.MiddleCenter, Color.black);
            }
            state = Ui.Label(Ui.Rect(root, "State", new Vector2(0, -0.22f), new Vector2(1, 0)), "", 22, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.5f));
            state.gameObject.AddComponent<Outline>().effectColor = Color.black;
        }

        public void Show(bool on) => root.gameObject.SetActive(on);

        public void SetRole(bool attack, string flee)
        {
            var names = attack ? new[] { "こうげき", "とくこう", "必殺", "どうぐ" } : new[] { "ぼうぎょ", "とくぼう", "カウンター", flee };
            var marks = new[] { "□", "○", "△", "R1" };
            for (int i = 0; i < 4; i++) labels[i].text = i == 3 ? $"R1 {names[i]}" : $"<size=36><b>{marks[i]}</b></size> {names[i]}"; // 記号は大きく
        }

        // 押せるボタンは はっきり、押せないボタン（相手の番・クールダウン中の必殺など）は うすく
        public void SetEnabled(Func<int, bool> can)
        {
            for (int i = 0; i < 4; i++)
            {
                enabled[i] = can(i);
                buttons[i].color = enabled[i] ? CommandColors[i] : Color.Lerp(CommandColors[i], new Color(0.45f, 0.45f, 0.45f), 0.55f);
                labels[i].color = enabled[i] ? Color.black : new Color(0, 0, 0, 0.55f);
            }
        }

        // 結果発表：選んだコマンドだけ色を残し、ほかはグレー
        public void Reveal(int chosen)
        {
            for (int i = 0; i < 4; i++)
            {
                buttons[i].color = i == chosen ? CommandColors[i] : new Color(0.4f, 0.4f, 0.4f, 0.8f);
                labels[i].color = i == chosen ? Color.black : new Color(0, 0, 0, 0.5f);
            }
        }

        public void SetState(string s) => state.text = s;
    }

    // ---- 1体ぶんの表示：名前とHPの欄（info）と、台座の上のポケモン（stage）----
    sealed class BattleCard
    {
        readonly Text title, hpText, flash;
        readonly Image circle, hpBar, expBack, expBar, ball, aura;
        readonly RectTransform stage;
        readonly GameRunner runner;
        int maxHp = 1, hp;
        float shownHp;     // バーに出しているHP（ゆっくり近づける）
        string status = "", owner = "";
        public int Player = -1;      // このカードのプレイヤー（野生・NPC なら -1。経験値バーと警告音は プレイヤーのポケモンだけ）
        string species = "", type = "", weakness = "";
        int level;
        float shownExp;    // 経験値バーに出している経験値の合計
        float scale = 1;   // 攻撃側・防御側の大きさ

        public BattleCard(RectTransform info, RectTransform stage, GameRunner runner)
        {
            this.runner = runner; this.stage = stage;
            title = Ui.Label(Ui.Rect(info, "Title", new Vector2(0.04f, 0.5f), new Vector2(0.96f, 0.98f)), "", 28, TextAnchor.MiddleLeft);
            Ui.Panel(info, "HpBack", new Vector2(0.04f, 0.34f), new Vector2(0.96f, 0.46f), new Color(0.15f, 0.15f, 0.15f));
            hpBar = Ui.Panel(info, "Hp", new Vector2(0.04f, 0.34f), new Vector2(0.96f, 0.46f), new Color(0.3f, 0.9f, 0.4f));
            expBack = Ui.Panel(info, "ExpBack", new Vector2(0.04f, 0.27f), new Vector2(0.96f, 0.31f), new Color(0.1f, 0.1f, 0.15f)); // 経験値バー（HPバーの下の細い青）
            expBar = Ui.Panel(expBack.transform, "Exp", Vector2.zero, new Vector2(0, 1), new Color(0.3f, 0.65f, 1f));
            hpText = Ui.Label(Ui.Rect(info, "HpText", new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.26f)), "", 22, TextAnchor.MiddleLeft);
            aura = Ui.Panel(stage, "Aura", new Vector2(0.1f, 0f), new Vector2(0.9f, 0.85f), Color.clear); // 結果発表のオーラ（ポケモンの後ろ）
            aura.sprite = Ui.Circle; aura.raycastTarget = false; aura.preserveAspect = true;
            circle = Ui.Panel(stage, "Mon", new Vector2(0.5f, 0), new Vector2(0.5f, 0), Color.gray); // 足もとを 台座（舞台の下の中央）に。大きさは Show で絵に合わせる
            circle.rectTransform.pivot = new Vector2(0.5f, 0);
            circle.preserveAspect = true; circle.sprite = Ui.Circle; circle.raycastTarget = false;
            ball = Ui.Panel(stage, "Ball", new Vector2(0.4f, 0.05f), new Vector2(0.6f, 0.25f), Color.white); // 投げたボール・出てくるボール
            ball.preserveAspect = true; ball.enabled = false; ball.raycastTarget = false;
            flash = Ui.Label(Ui.Rect(stage, "Flash", new Vector2(-0.3f, 0.85f), new Vector2(1.3f, 1.05f)), "", 30, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.4f));
            flash.gameObject.AddComponent<Outline>().effectColor = Color.black;
        }

        public string Species => species;
        public void SetOwner(string name, int player) { owner = name; Player = player; DrawTitle(); }
        public void SetStatus(string s) { status = s; Draw(); }
        public void Clear()
        {
            status = ""; title.text = hpText.text = flash.text = ""; circle.color = Color.clear; ball.enabled = false; species = ""; hp = 0; shownHp = 0;
            expBack.gameObject.SetActive(false); hpBar.rectTransform.anchorMax = new Vector2(0.96f, 0.46f);
            SetScale(1); ClearAura(); stage.anchoredPosition = Vector2.zero;
        }

        public void Show(string species, int level, int hp, int max, string type, string weakness, int exp)
        {
            maxHp = Math.Max(1, max);
            this.species = species; this.level = level; this.type = type; this.weakness = weakness;
            var sprite = runner.BattleSprite(species);
            circle.sprite = sprite != null ? sprite : Ui.Circle;
            circle.color = sprite != null ? Color.white : TypeColor(type);
            // 絵の1ドットを 同じ大きさで（大きいポケモンは大きく）。高すぎるときは 舞台に入るまで縮める
            float h = stage.rect.height > 0 ? stage.rect.height : 360;
            Vector2 size = sprite != null ? sprite.rect.size * PixelScale : new Vector2(150, 150);
            if (size.y > h) size *= h / size.y;
            circle.rectTransform.sizeDelta = size;
            circle.enabled = true;
            ball.enabled = false;
            circle.rectTransform.anchoredPosition = Vector2.zero; circle.rectTransform.localScale = Vector3.one; // たおれた・出てくる演出のあと
            stage.anchoredPosition = Vector2.zero;
            DrawTitle();
            flash.text = "";
            SetHp(hp);
            expBack.gameObject.SetActive(Player >= 0);
            shownExp = exp; DrawExp();
        }

        void DrawTitle() => title.text = (owner != "" ? $"<size=20>{owner}</size>\n" : "") + (species != "" ? $"{species}　Lv{level}　<size=20>{type}・弱点 {weakness}</size>" : "");

        // 経験値バー：今のレベルの はじめ〜次のレベルまでの どこにいるか
        void DrawExp()
        {
            float from = Pokemons.ExpForLevel(level), to = Pokemons.ExpForLevel(level + 1);
            expBar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01((shownExp - from) / Mathf.Max(1, to - from)), 1);
        }

        public bool Owns(int player, string mon) => Player >= 0 && player == Player && mon == species;

        // 経験値バーが ゆっくり伸びる。いっぱいになったら レベルアップして、0から また伸びる
        public IEnumerator ExpTo(int from, int to, int maxLevel)
        {
            shownExp = from; DrawExp();
            float seconds = runner.shortBattle ? 0.5f : 1f; // バー1本ぶんの時間
            while (shownExp < to && !SkipHeld())
            {
                float next = Mathf.Min(to, level < maxLevel ? Pokemons.ExpForLevel(level + 1) : to);
                float speed = (Pokemons.ExpForLevel(level + 1) - Pokemons.ExpForLevel(level)) / seconds;
                shownExp = Mathf.MoveTowards(shownExp, next, speed * Time.deltaTime);
                if (level < maxLevel && shownExp >= Pokemons.ExpForLevel(level + 1)) { level++; DrawTitle(); Flash("レベルアップ！"); }
                DrawExp();
                yield return null;
            }
            shownExp = to;
            while (level < maxLevel && to >= Pokemons.ExpForLevel(level + 1)) { level++; DrawTitle(); } // 早送りしたとき
            DrawExp();
        }

        // HPが赤い（2割以下）。プレイヤーのポケモンだけ（警告音）
        public bool Danger => Player >= 0 && species != "" && shownHp > 0 && shownHp <= maxHp * 0.2f;

        // 攻撃側は大きく、防御側は小さく（台座のところから）
        public void SetScale(float s) { scale = s; stage.localScale = Vector3.one * s; }

        // 横へ出ていく（out）・横から入ってくる（dir：-1 左、1 右）
        public IEnumerator Slide(int dir, bool goOut, float seconds)
        {
            float far = dir * 1100;
            for (float t = 0; t < seconds && !SkipHeld(); t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0, 1, t / seconds);
                stage.anchoredPosition = new Vector2(goOut ? Mathf.Lerp(0, far, k) : Mathf.Lerp(far, 0, k), 0);
                yield return null;
            }
            stage.anchoredPosition = new Vector2(goOut ? far : 0, 0);
        }

        // モンスターボールから出てくる：ボールが光って開き、ポケモンがポンと出る
        public IEnumerator FromBall()
        {
            float s = runner.shortBattle ? 0.5f : 1f;
            circle.enabled = false;
            ball.sprite = Ui.Ball(new Color(0.9f, 0.15f, 0.15f)); ball.color = Color.white; ball.enabled = true;
            for (float t = 0; t < 0.3f * s && !SkipHeld(); t += Time.deltaTime) // ぽんと はねて
            {
                ball.rectTransform.anchoredPosition = new Vector2(0, Mathf.Sin(t / (0.3f * s) * Mathf.PI) * 40);
                yield return null;
            }
            ball.rectTransform.anchoredPosition = Vector2.zero;
            for (float t = 0; t < 0.15f * s && !SkipHeld(); t += Time.deltaTime) { ball.color = Color.Lerp(Color.white, new Color(1, 1, 1, 0), t / (0.15f * s)); yield return null; } // 光って消える
            ball.enabled = false;
            circle.enabled = true;
            yield return Appear();
        }

        // ④ ぐっと力をためる（縦に縮む）＋ コマンドの色のオーラ
        public IEnumerator PowerUp(Color color)
        {
            float total = runner.shortBattle ? 0.25f : 0.5f;
            for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / total;
                stage.localScale = new Vector3(scale * (1 + 0.06f * Mathf.Sin(k * Mathf.PI)), scale * (1 - 0.15f * Mathf.Sin(k * Mathf.PI)), 1);
                aura.color = new Color(color.r, color.g, color.b, 0.55f * Mathf.Clamp01(k * 2));
                aura.rectTransform.localScale = Vector3.one * (0.9f + 0.1f * Mathf.Sin(k * Mathf.PI * 2));
                yield return null;
            }
            stage.localScale = Vector3.one * scale;
            aura.color = new Color(color.r, color.g, color.b, 0.5f);
        }

        public void ClearAura() { aura.color = Color.clear; aura.rectTransform.localScale = Vector3.one; }

        // ボールを投げる：ボールが飛んできて ポケモンが吸いこまれ、ゆれる。捕まえたら そのまま、逃げられたら ポケモンが出てくる
        public IEnumerator Catch(Sprite ballSprite, bool caught)
        {
            float s = runner.shortBattle ? 0.5f : 1f;
            Color baseColor = circle.color;
            ball.sprite = ballSprite; ball.color = Color.white; ball.enabled = true;
            ball.rectTransform.localRotation = Quaternion.identity;
            for (float t = 0; t < 0.35f * s && !SkipHeld(); t += Time.deltaTime) // 左下から 弧をえがいて飛んでくる
            {
                float k = t / (0.35f * s);
                ball.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(-260, 0, k), Mathf.Sin(k * Mathf.PI) * 120 + Mathf.Lerp(-80, 0, k));
                ball.rectTransform.localRotation = Quaternion.Euler(0, 0, -720 * k);
                yield return null;
            }
            ball.rectTransform.anchoredPosition = Vector2.zero; ball.rectTransform.localRotation = Quaternion.identity;
            for (float t = 0; t < 0.3f * s && !SkipHeld(); t += Time.deltaTime) // 白く光って 吸いこまれる
            {
                float k = t / (0.3f * s);
                circle.rectTransform.localScale = Vector3.one * (1 - k);
                circle.color = Color.Lerp(baseColor, new Color(1, 1, 1, 0.6f), k);
                yield return null;
            }
            circle.enabled = false;
            int wobbles = caught ? 3 : UnityEngine.Random.Range(1, 3); // 逃げられるときは 1〜2回で
            for (int w = 0; w < wobbles; w++)
            {
                yield return runner.Wait(0.35f * s);
                for (float t = 0; t < 0.4f * s && !SkipHeld(); t += Time.deltaTime) // 左右に ゆらっ
                {
                    ball.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t / (0.4f * s) * Mathf.PI * 2) * 25);
                    yield return null;
                }
                ball.rectTransform.localRotation = Quaternion.identity;
            }
            yield return runner.Wait(0.3f * s);
            circle.rectTransform.localScale = Vector3.one; circle.color = baseColor;
            if (caught)
            {
                ball.color = new Color(0.75f, 0.75f, 0.75f); // カチッ：少し暗くなって 止まる
                Flash("つかまえた！");
                yield return runner.Wait(0.5f * s);
            }
            else
            {
                ball.enabled = false;
                circle.enabled = true;
                Flash("あっ！ でてきた");
                yield return Appear();
            }
        }

        // すぐに変える（出したとき）
        public void SetHp(int hp) { this.hp = hp; shownHp = hp; Draw(); }

        // HPを ゆっくり変える（本家のように、満タンから0まで およそ1.5秒。早送りなら すぐ）
        public IEnumerator HpTo(int hp)
        {
            this.hp = hp;
            float speed = maxHp / (runner.shortBattle ? 0.8f : 1.5f);
            while (!Mathf.Approximately(shownHp, hp))
            {
                shownHp = SkipHeld() ? hp : Mathf.MoveTowards(shownHp, hp, speed * Time.deltaTime);
                Draw();
                yield return null;
            }
        }

        void Draw()
        {
            float r = Mathf.Clamp01(shownHp / maxHp);
            hpBar.rectTransform.anchorMax = new Vector2(0.04f + 0.92f * r, 0.46f);
            hpBar.color = r > 0.5f ? new Color(0.3f, 0.9f, 0.4f) : r > 0.2f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.95f, 0.3f, 0.25f);
            hpText.text = species == "" ? "" : $"HP {Mathf.CeilToInt(shownHp)} / {maxHp}" + (status != "" ? $"　【{status}】" : "");
        }

        // 出てくる：小さい所から ポンと大きくなる
        public IEnumerator Appear()
        {
            float total = runner.shortBattle ? 0.2f : 0.35f;
            for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / total;
                circle.rectTransform.localScale = Vector3.one * (k < 0.8f ? Mathf.Lerp(0.1f, 1.1f, k / 0.8f) : Mathf.Lerp(1.1f, 1f, (k - 0.8f) / 0.2f));
                yield return null;
            }
            circle.rectTransform.localScale = Vector3.one;
        }

        // たおれる：下に沈みながら 消える
        public IEnumerator Faint()
        {
            float total = runner.shortBattle ? 0.35f : 0.7f;
            Color c = circle.color;
            for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / total;
                circle.rectTransform.anchoredPosition = new Vector2(0, -140 * k * k);
                circle.color = new Color(c.r, c.g, c.b, 1 - k);
                yield return null;
            }
            circle.rectTransform.anchoredPosition = Vector2.zero;
            circle.color = new Color(c.r, c.g, c.b, 0);
        }

        public void Flash(string text) => flash.text = text;
        public Image Body => circle; // 技の演出で動かす
    }
}
