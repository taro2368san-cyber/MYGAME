using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// メイン画面の部品（設計メモ/新設定追加項目/UI仕様書.md）
// A 週と天気（左上）、B 場所名（上の中央）、C 手番のプレイヤー（右上）、D ほかのプレイヤー（右側）、E メニュー（左下）、G メッセージ（下の中央）
// F 盤面：プレイヤーのコマの横に、先頭のポケモンがついて歩く
// ponytail: メニューの絵・バッジ・メダルは Ui で描いた仮の絵
public partial class GameRunner
{
    static readonly Color HudColor = new Color(0.05f, 0.28f, 0.32f, 0.85f); // 濃い青緑、半透明（4. 見た目）
    static readonly Color SkyColor = new Color(0.62f, 0.85f, 0.95f);         // 背景の明るい水色

    GameObject hudRoot;                 // A〜D（バトル中は隠す）
    Text weekText, placeText;
    Image currentFrame, currentIcon;
    Text currentText;
    readonly System.Collections.Generic.Dictionary<string, Sprite> characterSprites = new System.Collections.Generic.Dictionary<string, Sprite>();

    // キャラクターの絵（キャラクター.csv の「絵」＝ Resources/Characters/<番号>）。なければ null
    Sprite CharacterSprite(Character c)
    {
        if (c == null || string.IsNullOrEmpty(c.Art)) return null;
        if (!characterSprites.TryGetValue(c.Art, out var s)) characterSprites[c.Art] = s = Resources.Load<Sprite>("Characters/" + c.Art);
        return s;
    }

    // 並べる箱の中に置く キャラクターの絵
    static Image IconIn(Transform parent, Sprite sprite, float height)
    {
        var img = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement)).GetComponent<Image>();
        img.transform.SetParent(parent, false);
        img.sprite = sprite; img.preserveAspect = true; img.raycastTarget = false;
        var le = img.GetComponent<LayoutElement>(); le.preferredHeight = height; le.preferredWidth = height * 0.7f; le.minWidth = height * 0.7f;
        return img;
    }
    RectTransform othersCol;
    GameObject messagePanel;
    readonly List<Transform> buddies = new List<Transform>();
    readonly List<string> buddyWalk = new List<string>(); // 歩く絵で出している子の名前（2コマを交互に出す）。バトル用の絵や玉なら null

    void BuildHud()
    {
        hudRoot = new GameObject("Hud", typeof(RectTransform));
        var root = (RectTransform)hudRoot.transform;
        root.SetParent(canvas.transform, false);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;

        var a = Ui.Round(Ui.Panel(root, "A_Week", new Vector2(0.005f, 0.88f), new Vector2(0.2f, 0.99f), HudColor));
        weekText = Ui.Label(a.transform, "", 28, TextAnchor.MiddleLeft);
        weekText.rectTransform.offsetMin = new Vector2(16, 4); weekText.rectTransform.offsetMax = new Vector2(-8, -4);

        var b = Ui.Round(Ui.Panel(root, "B_Place", new Vector2(0.36f, 0.93f), new Vector2(0.64f, 0.99f), HudColor));
        placeText = Ui.Label(b.transform, "", 34, TextAnchor.MiddleCenter);

        // C：名前とお金（お金は手番の人だけ）・先頭のポケモン・バッジ8個
        currentFrame = Ui.Round(Ui.Panel(root, "C_Current", new Vector2(0.62f, 0.85f), new Vector2(0.995f, 0.99f), Color.white));
        var inner = Ui.Round(Ui.Panel(currentFrame.transform, "Inner", Vector2.zero, Vector2.one, HudColor));
        inner.rectTransform.offsetMin = new Vector2(6, 6); inner.rectTransform.offsetMax = new Vector2(-6, -6);
        currentText = Ui.Label(inner.transform, "", 26, TextAnchor.MiddleLeft);
        currentText.rectTransform.anchorMax = new Vector2(0.34f, 1);
        currentText.rectTransform.offsetMin = new Vector2(14, 4); currentText.rectTransform.offsetMax = new Vector2(0, -4);
        currentIcon = Ui.Panel(inner.transform, "Lead", new Vector2(0.34f, 0.06f), new Vector2(0.47f, 0.94f), Color.white);
        currentIcon.preserveAspect = true;
        var row = Ui.Stack(inner.transform, "Badges", new Vector2(0.48f, 0.15f), new Vector2(0.99f, 0.85f), false, 4, 0);
        row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
        for (int i = 0; i < 8; i++)
        {
            var img = new GameObject("Badge", typeof(RectTransform), typeof(Image), typeof(LayoutElement)).GetComponent<Image>();
            img.transform.SetParent(row, false);
            img.preserveAspect = true; img.raycastTarget = false;
            badgeIcons[i] = img;
        }

        othersCol = Ui.Stack(root, "D_Others", new Vector2(0.80f, 0.52f), new Vector2(0.995f, 0.84f), true, 8, 0);
        othersCol.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
    }

    readonly Image[] badgeIcons = new Image[8];

    // 一覧の小さい絵（歩く絵）。なければ、そのタイプの色の玉
    void SetLead(Image img, PlayerState p)
    {
        var lead = p.Party.FirstOrDefault();
        img.enabled = lead != null;
        img.preserveAspect = true;
        if (lead == null) return;
        img.sprite = PokemonIcon(lead.Species) ?? Ui.Circle;
        img.color = img.sprite == Ui.Circle ? TypeColor(Pokemons.Of(data, lead).Type) : Color.white;
    }

    void RefreshHud()
    {
        if (hudRoot == null) return;
        hudRoot.SetActive(S.Battle == null); // バトル中は A〜D を隠す（3. 場面ごとの表示）
        var cur = S.CurrentPlayer;

        // A 週と天気。最後の10週（換金所が閉まる）は 色を変える
        int week = System.Math.Min(S.Week, S.MaxWeeks);
        string weather = S.FluteWeeks > 0 ? $"{S.FluteWeather}（ふえ） あと{S.FluteWeeks}週" : S.Weather != "" ? $"{S.Weather} あと{S.WeatherWeeks}週" : "天気：ふつう";
        string ev = S.WeekEvent != "" ? $"{S.WeekEvent}中" : S.NewsKind != "" ? $"{S.NewsKind}：{S.NewsPlace}" : "";
        weekText.text = $"{week}／{S.MaxWeeks}週\n{weather}" + (ev != "" ? $"\n<size=20>{ev}</size>" : "");
        weekText.color = game.ExchangeClosed ? new Color(1f, 0.8f, 0.35f) : Color.white;

        // B 場所名：手番の人のいる場所。探索中は選んでいるマスの場所
        int placeSquare = exploring && exploreSquare >= 0 ? exploreSquare : cur.Square;
        placeText.text = board[placeSquare].Place;

        // C 手番のプレイヤー（枠はその人の色）：名前・お金、先頭のポケモン、バッジ8個（取っていないものは影）
        currentFrame.color = PlayerColor(cur);
        currentText.text = $"<size=34>{cur.Name}</size>\n{cur.Money:#,0}円";
        SetLead(currentIcon, cur);
        for (int i = 0; i < 8; i++) badgeIcons[i].sprite = Ui.Badge(i, cur.Gyms.Contains(Ui.BadgeGyms[i]));

        // D ほかのプレイヤー：次の番の人から順に。名前・先頭のポケモン・メダルにバッジの数
        Ui.Clear(othersCol);
        int n = S.TurnOrder.Count;
        for (int k = 1; k < n; k++)
        {
            var p = S.Players[S.TurnOrder[(S.TurnIndex + k) % n]];
            var frame = Ui.Round(Ui.Panel(othersCol, "Other", Vector2.zero, Vector2.one, PlayerColor(p)));
            frame.gameObject.AddComponent<LayoutElement>().preferredHeight = 80;
            var box = Ui.Round(Ui.Panel(frame.transform, "Inner", Vector2.zero, Vector2.one, HudColor));
            box.rectTransform.offsetMin = new Vector2(5, 5); box.rectTransform.offsetMax = new Vector2(-5, -5);
            var t = Ui.Label(box.transform, p.Name, 26, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMax = new Vector2(0.5f, 1);
            t.rectTransform.offsetMin = new Vector2(12, 0);
            SetLead(Ui.Panel(box.transform, "Lead", new Vector2(0.5f, 0.06f), new Vector2(0.72f, 0.94f), Color.white), p);
            var medal = Ui.Panel(box.transform, "Medal", new Vector2(0.76f, 0.1f), new Vector2(0.97f, 0.9f), Color.white);
            medal.sprite = Ui.Medal; medal.preserveAspect = true;
            Ui.Label(medal.transform, p.Badges.ToString(), 26, TextAnchor.MiddleCenter, new Color(0.35f, 0.2f, 0f));
        }

        SyncBuddies();
        SyncSymbols();
    }

    // ---- 手番が替わる：次の人の D の欄が C の位置へ上がって、中身が入れかわる（UI仕様書 D の案）----
    IEnumerator TurnSwapAnim(int player)
    {
        string name = S.Players[player].Name;
        RectTransform row = null;
        foreach (RectTransform child in othersCol)
            if (child.GetComponentInChildren<Text>()?.text == name) { row = child; break; }
        if (row == null || !hudRoot.activeInHierarchy) { RefreshHud(); yield break; }
        var ghost = (RectTransform)Instantiate(row.gameObject, hudRoot.transform).transform; // 上に重ねた写しを動かす
        ghost.anchorMin = ghost.anchorMax = ghost.pivot = new Vector2(0.5f, 0.5f);
        ghost.position = row.position;
        ghost.sizeDelta = row.rect.size;
        var rowGroup = row.gameObject.AddComponent<CanvasGroup>(); rowGroup.alpha = 0;
        if (!currentFrame.TryGetComponent<CanvasGroup>(out var cGroup)) cGroup = currentFrame.gameObject.AddComponent<CanvasGroup>();
        Vector3 from = ghost.position, to = currentFrame.rectTransform.position;
        Vector2 size0 = ghost.sizeDelta, size1 = currentFrame.rectTransform.rect.size;
        float total = 0.35f;
        for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0, 1, t / total);
            ghost.position = Vector3.Lerp(from, to, k);
            ghost.sizeDelta = Vector2.Lerp(size0, size1, k);
            cGroup.alpha = 1 - k;
            yield return null;
        }
        Destroy(ghost.gameObject);
        cGroup.alpha = 1;
        RefreshHud(); // C と D を 新しい手番で描き直す（D の欄は作り直されるので 透明にしたものも消える）
    }

    // ---- G セリフ欄：出すときは下からスライド、読み終わったら下へ引っこむ。新しい文は1文字ずつ（○・スペースで全文）----
    const float TypeSpeed = 60f;   // 1秒に出す文字数（仕様書1.3：速さは固定）
    const float MessageHold = 4f;  // 最後の文から この秒数は出しておく
    string typingText = "";
    float typed, lastLogTime, messageShown; // messageShown：0 引っこんでいる 〜 1 出ている
    bool Typing => typed < typingText.Length;

    void RenderLog()
    {
        int fresh = typingText.Split('\n').Length, old = Mathf.Max(0, logLines.Count - fresh);
        string shown = typingText.Substring(0, Mathf.Min((int)typed, typingText.Length));
        var lines = (string.Join("\n", logLines.Take(old)) + (old > 0 ? "\n" : "") + shown).Split('\n');
        int fit = Mathf.Max(1, Mathf.FloorToInt(log.rectTransform.rect.height / (log.fontSize * 1.2f))); // 欄に入る行数（新しい行が下にはみ出さないように 古い行から消す）
        log.text = string.Join("\n", lines.Skip(Mathf.Max(0, lines.Length - fit)));
    }

    // 場面ごとの表示（UI仕様書 3）：移動中・見渡し中は隠す。準備フェーズは 新しい文があるときだけ
    bool MessageWanted()
    {
        if (logLines.Count == 0 || exploring || EventOpen) return false; // イベント画面は 自分のセリフ欄を使う
        if (Typing || Time.time - lastLogTime < MessageHold) return true;
        return S.Phase != Phase.Moving && (playing || S.Battle != null || S.Phase == Phase.Facility);
    }

    // LateUpdate から
    void SlideMessage()
    {
        if (messagePanel == null || game == null) return;
        if (Typing) { typed = SkipHeld() ? typingText.Length : typed + TypeSpeed * Time.deltaTime; RenderLog(); }
        bool want = MessageWanted();
        messageShown = Mathf.MoveTowards(messageShown, want ? 1 : 0, Time.deltaTime * 5);
        var rt = (RectTransform)messagePanel.transform;
        rt.anchoredPosition = new Vector2(0, -(1 - Mathf.SmoothStep(0, 1, messageShown)) * (rt.rect.height + 30));
        if (!want && messageShown <= 0 && logLines.Count > 0) { logLines.Clear(); typingText = ""; log.text = ""; } // 引っこんだら 次は新しい文から
    }

    // ---- 盤面の伝説のシンボル：マスの上に その伝説の絵を立てる（だれかが倒すか捕まえたら消す）----
    readonly Dictionary<int, SpriteRenderer> symbolArt = new Dictionary<int, SpriteRenderer>();

    void SyncSymbols()
    {
        foreach (var sq in board.Squares.Values.Where(s => s.Type == "シンボル"))
        {
            string legend = game.SymbolHere(sq.Id);
            if (!symbolArt.TryGetValue(sq.Id, out var sr))
            {
                if (legend == null || !squares.ContainsKey(sq.Id)) continue;
                sr = symbolArt[sq.Id] = new GameObject("Legend", typeof(SpriteRenderer)).GetComponent<SpriteRenderer>();
                sr.transform.localScale = Vector3.one * 1.6f;
            }
            sr.gameObject.SetActive(legend != null);
            if (legend == null) continue;
            sr.sprite = WalkSprite(legend, 1) ?? PokemonSprite(legend);
            float foot = sr.sprite != null ? -sr.sprite.bounds.min.y * sr.transform.localScale.y : 0; // 絵の下の端を マスの上に
            sr.transform.position = squares[sq.Id].transform.position + Vector3.up * (foot + 0.05f);
        }
    }

    // ---- F：コマの横についてくる先頭のポケモン（絵と名前。絵がなければタイプの色の玉） ----

    void SyncBuddies()
    {
        for (int i = 0; i < S.Players.Count && i < pieces.Count; i++)
        {
            if (buddies.Count <= i)
            {
                var go = new GameObject("Buddy");
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(ball.GetComponent<Collider>()); // マスのクリックのじゃまをしない
                ball.name = "Ball";
                ball.transform.SetParent(go.transform, false);
                ball.transform.localScale = Vector3.one * 0.32f;
                var art = new GameObject("Art", typeof(SpriteRenderer));
                art.transform.SetParent(go.transform, false);
                art.transform.localPosition = new Vector3(0, 0.6f, 0); // 絵（96px）の下の端を コマの足もとあたりに
                art.transform.localScale = Vector3.one * 1.5f;
                var label = new GameObject("Label", typeof(TextMesh)).GetComponent<TextMesh>();
                label.transform.SetParent(go.transform, false);
                label.transform.localPosition = new Vector3(0, 1.1f, 0);
                label.font = Ui.Font; label.GetComponent<MeshRenderer>().material = Ui.Font.material;
                label.fontSize = 40; label.characterSize = 0.029f; label.anchor = TextAnchor.MiddleCenter; label.color = Color.black;
                buddies.Add(go.transform);
            }
            var lead = S.Players[i].Party.FirstOrDefault();
            var b = buddies[i];
            b.gameObject.SetActive(lead != null);
            if (lead == null) continue;
            // 歩く絵（Resources/PokemonIcons）があれば それを2コマで。なければ バトル用の絵、それもなければ玉
            var walk = WalkSprite(lead.Species, 1);
            var sprite = walk ?? PokemonSprite(lead.Species);
            var sr = b.Find("Art").GetComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.transform.localPosition = walk != null ? new Vector3(0, -0.2f, 0) : new Vector3(0, 0.6f, 0); // 歩く絵は 足もと（下の中央）を盤面に
            sr.transform.localScale = Vector3.one * (walk != null ? 1.1f : 1.5f);
            b.GetComponentInChildren<TextMesh>().transform.localPosition = new Vector3(0, walk != null ? (walk.rect.height > 40 ? 1.95f : 1.0f) : 1.1f, 0);
            while (buddyWalk.Count <= i) buddyWalk.Add(null);
            buddyWalk[i] = walk != null ? lead.Species : null;
            var ballGo = b.Find("Ball").gameObject;
            ballGo.SetActive(sprite == null);
            ballGo.GetComponent<Renderer>().material.color = TypeColor(Pokemons.Of(data, lead).Type);
            b.GetComponentInChildren<TextMesh>().text = lead.Species;
        }
    }

    // コマについていく（LateUpdate から）
    void MoveBuddies()
    {
        var cam = Camera.main;
        for (int i = 0; i < buddies.Count && i < pieces.Count; i++)
        {
            buddies[i].position = pieces[i].position + new Vector3(0.6f, -0.4f, -0.12f);
            var art = pieces[i].Find("Sprite");
            if (art != null && cam != null) art.rotation = cam.transform.rotation; // キャラクターの絵は いつもこちらを向く
            if (cam != null) buddies[i].rotation = cam.transform.rotation; // ポケモンの絵と名前は いつもこちらを向く
            // 歩く絵は 2コマを交互に（1秒に3回）
            if (i < buddyWalk.Count && buddyWalk[i] != null && WalkSprite(buddyWalk[i], 1 + (int)(Time.time * 3) % 2) is Sprite frame)
                buddies[i].Find("Art").GetComponent<SpriteRenderer>().sprite = frame;
        }
        // 伝説のシンボルも こちらを向いて、歩く絵なら 2コマを交互に（ゆっくり）
        foreach (var kv in symbolArt)
        {
            if (!kv.Value.gameObject.activeSelf) continue;
            if (cam != null) kv.Value.transform.rotation = cam.transform.rotation;
            if (game.SymbolHere(kv.Key) is string legend && WalkSprite(legend, 1 + (int)(Time.time * 1.5f) % 2) is Sprite f) kv.Value.sprite = f;
        }
    }
}
