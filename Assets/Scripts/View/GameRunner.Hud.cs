using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// メイン画面の部品（設計メモ/新設定追加項目/UI仕様書_メイン画面.md）
// A 週と天気（左上）、B 場所名（上の中央）、C 手番のプレイヤー（右上）、D ほかのプレイヤー（右側）、E メニュー（左下）、G メッセージ（下の中央）
// F 盤面：プレイヤーのコマの横に、先頭のポケモンがついて歩く
// ponytail: 角丸・アイコン・C/Dの入れ替わりのアニメーションはまだ。中身（C・D）は仕様書の候補から仮に選んだ
public partial class GameRunner
{
    static readonly Color HudColor = new Color(0.05f, 0.28f, 0.32f, 0.85f); // 濃い青緑、半透明（4. 見た目）
    static readonly Color SkyColor = new Color(0.62f, 0.85f, 0.95f);         // 背景の明るい水色

    GameObject hudRoot;                 // A〜D（バトル中は隠す）
    Text weekText, placeText;
    Image currentFrame;
    Text currentText;
    RectTransform othersCol;
    GameObject messagePanel;
    readonly List<Transform> buddies = new List<Transform>();

    void BuildHud()
    {
        hudRoot = new GameObject("Hud", typeof(RectTransform));
        var root = (RectTransform)hudRoot.transform;
        root.SetParent(canvas.transform, false);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;

        var a = Ui.Panel(root, "A_Week", new Vector2(0.005f, 0.88f), new Vector2(0.2f, 0.99f), HudColor);
        weekText = Ui.Label(a.transform, "", 28, TextAnchor.MiddleLeft);
        weekText.rectTransform.offsetMin = new Vector2(16, 4); weekText.rectTransform.offsetMax = new Vector2(-8, -4);

        var b = Ui.Panel(root, "B_Place", new Vector2(0.36f, 0.93f), new Vector2(0.64f, 0.99f), HudColor);
        placeText = Ui.Label(b.transform, "", 34, TextAnchor.MiddleCenter);

        currentFrame = Ui.Panel(root, "C_Current", new Vector2(0.7f, 0.80f), new Vector2(0.995f, 0.99f), Color.white);
        var inner = Ui.Panel(currentFrame.transform, "Inner", Vector2.zero, Vector2.one, HudColor);
        inner.rectTransform.offsetMin = new Vector2(6, 6); inner.rectTransform.offsetMax = new Vector2(-6, -6);
        currentText = Ui.Label(inner.transform, "", 26, TextAnchor.MiddleLeft);
        currentText.rectTransform.offsetMin = new Vector2(16, 6); currentText.rectTransform.offsetMax = new Vector2(-10, -6);

        othersCol = Ui.Stack(root, "D_Others", new Vector2(0.76f, 0.44f), new Vector2(0.995f, 0.79f), true, 8, 0);
        othersCol.GetComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
    }

    // 順位用ポイント（12章）の順位
    int RankOf(PlayerState p) => 1 + S.Players.Count(o => game.Score(o) > game.Score(p) + 1e-9);

    string StateText(PlayerState p) =>
        (p.Has("1回休み") ? "　1回休み" : "") + (p.Has("さいみん") ? "　さいみん" : "") + (p.PendingBattle != null ? $"　バトルの途中（{p.PendingBattle.Sides[1].Name}）" : "") + (p.Job != "" ? $"　{p.Job}" : "");

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

        // C 手番のプレイヤー（枠はその人の色）
        currentFrame.color = PlayerColor(cur);
        var lead = cur.Party.FirstOrDefault();
        currentText.text = $"<size=34>{cur.Name}</size>{CharacterLabel(cur)}　{RankOf(cur)}位\n{cur.Money:#,0}円　バッジ{cur.Badges}　ボール{cur.BallCount}" +
            (lead != null ? $"\n先頭：{lead.Species} Lv{lead.Level}　HP {lead.Hp}/{Pokemons.MaxHp(data, lead)}" : "") + (StateText(cur) != "" ? $"\n<size=22>{StateText(cur).Trim()}</size>" : "");

        // D ほかのプレイヤー：次の番の人から順に
        Ui.Clear(othersCol);
        int n = S.TurnOrder.Count;
        for (int k = 1; k < n; k++)
        {
            var p = S.Players[S.TurnOrder[(S.TurnIndex + k) % n]];
            var frame = Ui.Panel(othersCol, "Other", Vector2.zero, Vector2.one, PlayerColor(p));
            frame.gameObject.AddComponent<LayoutElement>().preferredHeight = 92;
            var box = Ui.Panel(frame.transform, "Inner", Vector2.zero, Vector2.one, HudColor);
            box.rectTransform.offsetMin = new Vector2(5, 5); box.rectTransform.offsetMax = new Vector2(-5, -5);
            var t = Ui.Label(box.transform, $"{p.Name}　{RankOf(p)}位\n{p.Money:#,0}円　バッジ{p.Badges}" + (StateText(p) != "" ? $"\n<size=18>{StateText(p).Trim()}</size>" : ""), 22, TextAnchor.MiddleLeft);
            t.rectTransform.offsetMin = new Vector2(12, 2); t.rectTransform.offsetMax = new Vector2(-6, -2);
        }

        // G メッセージ：出すものがあるときだけ
        if (messagePanel != null) messagePanel.SetActive(logLines.Count > 0);
        SyncBuddies();
    }

    // ---- F：コマの横についてくる先頭のポケモン（タイプの色の玉と名前） ----
    // ponytail: 絵（Resources/Pokemon）があってもまだ玉のまま。絵ができたら SpriteRenderer に差し替える

    void SyncBuddies()
    {
        for (int i = 0; i < S.Players.Count && i < pieces.Count; i++)
        {
            if (buddies.Count <= i)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>()); // マスのクリックのじゃまをしない
                go.name = "Buddy";
                go.transform.localScale = Vector3.one * 0.32f;
                var label = new GameObject("Label", typeof(TextMesh)).GetComponent<TextMesh>();
                label.transform.SetParent(go.transform, false);
                label.transform.localPosition = new Vector3(0, 1.4f, 0);
                label.font = Ui.Font; label.GetComponent<MeshRenderer>().material = Ui.Font.material;
                label.fontSize = 40; label.characterSize = 0.09f; label.anchor = TextAnchor.MiddleCenter; label.color = Color.black;
                buddies.Add(go.transform);
            }
            var lead = S.Players[i].Party.FirstOrDefault();
            var b = buddies[i];
            b.gameObject.SetActive(lead != null);
            if (lead == null) continue;
            b.GetComponent<Renderer>().material.color = TypeColor(Pokemons.Of(data, lead).Type);
            b.GetComponentInChildren<TextMesh>().text = lead.Species;
        }
    }

    // コマについていく（LateUpdate から）
    void MoveBuddies()
    {
        var cam = Camera.main;
        for (int i = 0; i < buddies.Count && i < pieces.Count; i++)
        {
            buddies[i].position = pieces[i].position + new Vector3(0.42f, -0.4f, -0.12f);
            if (cam != null) buddies[i].GetComponentInChildren<TextMesh>().transform.rotation = cam.transform.rotation; // 名前はいつもこちらを向く
        }
    }
}
