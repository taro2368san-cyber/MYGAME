using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.EventSystems;

// 見渡しモード「マップ」（16.7、旧「探索」）。サイコロを振る前に盤面を見て回る
// WASD・矢印キーで動く、ホイールで寄る・引く、マスをクリックで情報と距離、Esc でもどる（回転はしない）
public partial class GameRunner
{
    bool exploring;
    Transform exploreFocus;
    int exploreSquare = -1;

    // 見渡せるのは 自分が今いるマップだけ（16.7）。別マップの板は 同じシーンの離れた場所にある（3.4）ので、
    // そのマップのマスを囲む範囲（exploreRect）の外にある絵（板・マス・道・コマ・シンボル・マーク）を 見渡しの間だけ消し、カメラも その範囲の中だけで動かす
    const float ExplorePad = 4f;              // マスの外側に 板の縁として残す幅
    Rect exploreRect;
    readonly System.Collections.Generic.List<Renderer> exploreHidden = new System.Collections.Generic.List<Renderer>();

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
        RefreshAll(); // 最後に HideOtherMaps
    }

    void EndExplore()
    {
        exploring = false;
        ShowOtherMaps();
        if (exploreFocus != null) Destroy(exploreFocus.gameObject);
        cameraTarget = pieces[S.CurrentPlayer.Id];
        subMenu = null;
        RefreshAll();
    }

    // そのマップのマスを囲む範囲（x・z）。縁を少し足す
    Rect MapRect(string mapId)
    {
        var pts = board.Squares.Values.Where(s => s.MapId == mapId).Select(s => SquarePosition(s.Id)).ToList();
        if (pts.Count == 0) return new Rect(-1e5f, -1e5f, 2e5f, 2e5f);
        return Rect.MinMaxRect(pts.Min(p => p.x) - ExplorePad, pts.Min(p => p.z) - ExplorePad, pts.Max(p => p.x) + ExplorePad, pts.Max(p => p.z) + ExplorePad);
    }

    // 見渡しの間：範囲の外にある絵を消す（消したものだけ覚えて、出るときに戻す）。RefreshAll のたびに呼ぶ（あとから作られたマークも消す）。デバッグの「見渡しの制限：なし」なら消さない
    void HideOtherMaps()
    {
        if (!exploring || seeAll) return;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled) continue;
            var c = r.bounds.center;
            if (exploreRect.Contains(new Vector2(c.x, c.z))) continue;
            r.enabled = false;
            exploreHidden.Add(r);
        }
    }

    void ShowOtherMaps()
    {
        foreach (var r in exploreHidden) if (r != null) r.enabled = true;
        exploreHidden.Clear();
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
        DrawMenu(exploreSquare < 0 ? "マップ：WASD・矢印で移動、マスをクリックで くわしく（Escで もどる）" : SquareInfo(exploreSquare));
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
        if (exploreSquare >= 0 && squares.TryGetValue(exploreSquare, out var old)) old.SetGlow(Color.white, 0);
        exploreSquare = sq.id;
        sq.SetGlow(new Color(1, 0.9f, 0.3f), 0.8f);
        RefreshAll();
        sq.SetGlow(new Color(1, 0.9f, 0.3f), 0.8f); // RefreshAll が盤面の光を消すので、もう一度
        if (debugPanel != null && labelMode == 2) ShowRoute(sq.id); // デバッグ：道順（17.1）
    }
}
