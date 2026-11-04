using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.EventSystems;

// 見渡しモード「マップ」（16.7、旧「探索」）。サイコロを振る前に盤面を見て回る
// コントローラー：左スティック・十字キーで動く、R1 で寄る・L1 で引く、まん中の目印の下のマスの情報と距離を出す、×でもどる（GameRunner.Pad.cs）
// キーボード・マウス（開発中の確認用）：WASD・矢印キーで動く、ホイールで寄る・引く、マスをクリックで情報と距離、Esc でもどる（回転はしない）
public partial class GameRunner
{
    bool exploring;
    Transform exploreFocus;
    int exploreSquare = -1;

    // 見渡しのカメラは 自分が今いるマップのマスを囲む範囲（exploreRect）の中だけで動かす（16.7）
    const float ExplorePad = 4f;              // マスの外側に 板の縁として残す幅
    Rect exploreRect;

    void StartExplore()
    {
        exploring = true;
        exploreSquare = -1;
        exploreFocus = new GameObject("ExploreFocus").transform;
        exploreFocus.position = pieces[S.CurrentPlayer.Id].position + camPan; // いま見ているところから
        camPan = Vector3.zero;
        cameraTarget = exploreFocus;
        subMenu = ExploreMenu;
        exploreRect = MapRect(board[S.CurrentPlayer.Square].MapId);
        aimAt = new Vector3(float.NaN, 0, 0);
        ShowAimMark(true);
        RefreshAll();
    }

    void EndExplore()
    {
        exploring = false;
        ShowAimMark(false);
        if (exploreSquare >= 0 && squares.TryGetValue(exploreSquare, out var lit)) lit.SetGlow(Color.white, 0);
        if (exploreFocus != null) Destroy(exploreFocus.gameObject);
        cameraTarget = pieces[S.CurrentPlayer.Id];
        subMenu = null;
        RefreshAll();
    }

    // ---- 映すのは 1つのマップだけ（3.4・16.7）----
    // 別マップの板は 同じシーンの離れた場所にある（3.4）が、となりのマップや 本マップの中にある板（サントアンヌ号の Deck）が 引くと画面に入る。
    // そこで ふだんも見渡しも、カメラが追っている人（見渡しなら手番の人）がいるマップ以外の絵（板・マス・道・コマ・シンボル・マーク）の表示を切る。
    // どのマップの絵かは 四角い範囲ではなく 絵ごとに決める（板・マス・道は持っているマップID、コマと ついて歩くポケモンは その人のマス、ほかは いちばん近いマス）
    string shownMap;                                                  // いま映しているマップ（null なら 全部出している）
    readonly System.Collections.Generic.List<Renderer> mapHidden = new System.Collections.Generic.List<Renderer>();
    readonly System.Collections.Generic.Dictionary<Renderer, string> rendererMap = new System.Collections.Generic.Dictionary<Renderer, string>(); // 動かない絵のマップ（覚えておく）

    string ViewMap()
    {
        int i = pieces.IndexOf(cameraTarget);
        var p = i >= 0 && i < S.Players.Count ? S.Players[i] : S.CurrentPlayer;
        return board[p.Square].MapId;
    }

    // rescan：マップが同じでも 新しくできた絵を見直す（RefreshAll から）。LateUpdate からは マップが変わったときだけ。デバッグの「見渡しの制限：なし」なら 全部出す
    void SyncShownMap(bool rescan)
    {
        string want = game == null || seeAll || pieces.Count == 0 ? null : ViewMap();
        if (want == shownMap && !rescan) return;
        if (want != shownMap) { ShowAllMaps(); shownMap = want; }
        if (want == null) return;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            if (r.enabled && MapOf(r) != want) { r.enabled = false; mapHidden.Add(r); }
    }

    void ShowAllMaps()
    {
        foreach (var r in mapHidden) if (r != null) r.enabled = true;
        mapHidden.Clear();
    }

    string MapOf(Renderer r)
    {
        for (int i = 0; i < pieces.Count && i < S.Players.Count; i++)
            if (r.transform.IsChildOf(pieces[i]) || (i < buddies.Count && r.transform.IsChildOf(buddies[i]))) return board[S.Players[i].Square].MapId;
        if (rendererMap.TryGetValue(r, out string known)) return known;
        string m;
        if (r.GetComponentInParent<SugorokuSquare>() is SugorokuSquare sq && board.Squares.ContainsKey(sq.id)) m = board[sq.id].MapId;
        else if (r.GetComponentInParent<SugorokuRoad>() is SugorokuRoad road && road.squareA != null && board.Squares.ContainsKey(road.squareA.id)) m = board[road.squareA.id].MapId;
        else if (r.GetComponentInParent<SugorokuBoard>() is SugorokuBoard bd) m = bd.mapId ?? "";
        else
        {
            // マークやシンボルなど：いちばん近いマスのマップ（動かないので 覚えておく）
            var c = r.bounds.center; float best = float.MaxValue; m = "";
            foreach (var kv in squares)
            {
                if (!board.Squares.ContainsKey(kv.Key)) continue;
                var d = kv.Value.transform.position - c; float dd = d.x * d.x + d.z * d.z;
                if (dd < best) { best = dd; m = board[kv.Key].MapId; }
            }
        }
        rendererMap[r] = m;
        return m;
    }

    // そのマップのマスを囲む範囲（x・z）。縁を少し足す
    Rect MapRect(string mapId)
    {
        var pts = board.Squares.Values.Where(s => s.MapId == mapId).Select(s => SquarePosition(s.Id)).ToList();
        if (pts.Count == 0) return new Rect(-1e5f, -1e5f, 2e5f, 2e5f);
        return Rect.MinMaxRect(pts.Min(p => p.x) - ExplorePad, pts.Min(p => p.z) - ExplorePad, pts.Max(p => p.x) + ExplorePad, pts.Max(p => p.z) + ExplorePad);
    }

    // 見渡しのカメラ（LateUpdate から）：引きすぎると範囲の外が入るので 縮小に上限、見ているところは 画面の端が範囲に収まるところまで
    // ponytail: 画面に映る地面の広さは 真ん中の高さで見積もる（斜め見下ろしの手前と奥の差は 縁の幅で吸収）
    void ClampExploreCamera(Camera cam)
    {
        if (seeAll || exploreFocus == null) return;
        float dist = cameraOffset.magnitude, sin = cameraOffset.y / dist, tan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float perZoomW = dist * tan * cam.aspect, perZoomH = dist * tan / sin; // zoom 1 のときの 画面の半分の幅・奥行き
        float maxZoom = Mathf.Clamp(Mathf.Min(exploreRect.width / (2 * perZoomW), exploreRect.height / (2 * perZoomH)), 0.3f, 3f);
        zoom = Mathf.Min(zoom, maxZoom);
        float hw = perZoomW * zoom, hh = perZoomH * zoom;
        var p = exploreFocus.position;
        p.x = exploreRect.width > 2 * hw ? Mathf.Clamp(p.x, exploreRect.xMin + hw, exploreRect.xMax - hw) : exploreRect.center.x;
        p.z = exploreRect.height > 2 * hh ? Mathf.Clamp(p.z, exploreRect.yMin + hh, exploreRect.yMax - hh) : exploreRect.center.y;
        exploreFocus.position = p;
    }

    void ExploreMenu()
    {
        Add("もどる", EndExplore, KeyCode.Escape);
        DrawMenu(exploreSquare < 0 ? "マップ：左スティックで うごく　R1・L1で 寄る・引く\nまん中の目印の マスを くわしく　×で もどる" : SquareInfo(exploreSquare));
    }

    // マスの名前・説明・持ち主・解放・今いるマスからの距離（16.7）
    string SquareInfo(int id)
    {
        var p = S.CurrentPlayer;
        var sq = board[id];
        if (!seeAll && sq.MapId != board[p.Square].MapId) return "別のマップ（建物や洞窟の中など）は ここからは 見えない";
        string text = $"{sq.Place}の {sq.Type}";
        if (S.JobSquares.TryGetValue(id, out string job)) text += $"　{job}の 職業マス" + (game.JobChallengeOpen(p, id) != null ? "（いま 挑める）" : "");
        if (S.Owners.TryGetValue(id, out int owner)) text += $"　{S.Players[owner].Name}の" + (sq.Type == "ライバル" ? "ライバルマス" : "ファン");
        string fish = GymLine(p, sq) + (board.CanFish(id) ? "\nつりができる" : ""); // ジムトレーナーの人数（3.3.1）、つり（4.1.2）
        if (sq.RequiredBadges > p.Badges) return text + $"　まだ入れない（バッジ{sq.RequiredBadges}個で解放）" + fish;
        if (!Movement.CanEnter(board, id, p.Badges, game.TopBadges)) return text + $"　まだ通れない（バッジ{sq.GateBadges}個で通れる" + (sq.GateShared ? "。だれか1人が そろえれば 全員" : "") + "）" + fish;
        if (!Movement.CanEnter(board, id, p.Badges, game.TopBadges, p.Surf)) return text + "　なみのりを 覚えると 入れる" + fish;
        int d = game.DistanceFor(p, id);
        return text + (d < 0 ? "　いまは いけない" : d == 0 ? "　今いるマス" : $"　ここから {d}マス") + fish; // 行けないのは カビゴンでふさがれた先・ワープの先など（未解放・なみのりは 上で先に言う）
    }

    // ジムのマス（3.3.1）：「ジムトレーナー 2/3」。挑めるなら「（挑戦できる）」、カギで挑めるなら「（ひみつのカギで 挑戦できる）」。トキワジムが まだ出ていない人には 出さない
    // 挑めるかは ジムに止まったときと同じ GymStatusFor（バッジを持っている・ストライキ中も 挑めない扱い）
    string GymLine(PlayerState p, BoardSquare sq)
    {
        if (sq.Type != "ジム") return "";
        var st = game.GymStatusFor(p, sq);
        if (st.Gym == "" || st.Closed) return "";
        string count = st.Total == 0 ? "ジムトレーナー なし" : $"ジムトレーナー {st.Beaten}/{st.Total}";
        string can = !st.CanChallenge ? "" : st.Remaining.Count > 0 && st.HasKey ? "（ひみつのカギで 挑戦できる）" : "（挑戦できる）";
        return "\n" + count + can;
    }

    void ExploreUpdate()
    {
        var move = new Vector3(Input.GetAxisRaw("Horizontal"), 0, Input.GetAxisRaw("Vertical"));
        if (move != Vector3.zero) exploreFocus.position += move.normalized * 15f * zoom * Time.deltaTime;
        if (!Input.GetMouseButtonDown(0) || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())) return;
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(Input.mousePosition), out var hit)) return;
        var sq = hit.collider.GetComponentInParent<SugorokuSquare>();
        if (sq == null || !board.Squares.ContainsKey(sq.id)) return;
        SelectExploreSquare(sq.id);
    }

    // 選ぶマスを変える（-1 なら 選んでいない）。光らせて、説明（SquareInfo）を出す
    void SelectExploreSquare(int id)
    {
        if (exploreSquare >= 0 && squares.TryGetValue(exploreSquare, out var old)) old.SetGlow(Color.white, 0);
        exploreSquare = id;
        RefreshAll();
        if (id >= 0 && squares.TryGetValue(id, out var sq)) sq.SetGlow(new Color(1, 0.9f, 0.3f), 0.8f); // RefreshAll が盤面の光を消すので、そのあとで
        if (id >= 0 && debugPanel != null && labelMode == 2) ShowRoute(id); // デバッグ：道順（17.1）
    }

    // ---- まん中の目印（コントローラーでマスを選ぶ、16.7）----
    // カメラは いつも exploreFocus を画面のまん中に映すので、そこから 今いるマップの いちばん近いマス（マスの半径くらいまで）を選ぶ。なければ 選ばない
    // カメラが動いていないあいだは 選び直さない（マウスで選んだマスが そのまま残る）
    const float AimRadius = 1f;  // マスの円柱は 直径1.6
    Vector3 aimAt;
    GameObject aimMark;

    void AimSquare()
    {
        if ((exploreFocus.position - aimAt).sqrMagnitude < 1e-4f) return;
        aimAt = exploreFocus.position;
        // exploreFocus はコマの高さにあるので、画面のまん中の線が 地面（マスの高さ 0）と交わるところで選ぶ
        var f = aimAt - cameraOffset * (aimAt.y / cameraOffset.y);
        string map = board[S.CurrentPlayer.Square].MapId;
        int best = -1; float bestD = AimRadius * AimRadius;
        foreach (var s in board.Squares.Values)
        {
            if (s.MapId != map) continue;
            var d = SquarePosition(s.Id) - f; float dd = d.x * d.x + d.z * d.z;
            if (dd < bestD) { bestD = dd; best = s.Id; }
        }
        if (best != exploreSquare) SelectExploreSquare(best);
    }

    // 画面のまん中の 小さな十字（こい色、白いふち。明るい地面でも 木の上でも 見えるように）。ponytail: 2本の棒だけ。絵ができたら差し替える
    void ShowAimMark(bool on)
    {
        if (aimMark == null)
        {
            if (!on) return;
            var root = Ui.Rect(canvas.transform, "AimMark", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            root.sizeDelta = new Vector2(48, 48);
            foreach (var (min, max) in new[] { (new Vector2(0.42f, 0), new Vector2(0.58f, 1)), (new Vector2(0, 0.42f), new Vector2(1, 0.58f)) })
            {
                var bar = Ui.Panel(root, "Bar", min, max, new Color(0.15f, 0.15f, 0.2f));
                bar.raycastTarget = false;
                var line = bar.gameObject.AddComponent<UnityEngine.UI.Outline>();
                line.effectColor = Color.white; line.effectDistance = new Vector2(2, 2);
            }
            aimMark = root.gameObject;
        }
        aimMark.SetActive(on);
        if (on) aimMark.transform.SetAsFirstSibling(); // メニューや知らせの下
    }
}
