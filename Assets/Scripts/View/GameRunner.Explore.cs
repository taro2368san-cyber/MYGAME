using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.EventSystems;

// 見渡しモード「探索」（16.7）。サイコロを振る前に盤面を見て回る
// WASD・矢印キーで動く、ホイールで寄る・引く、マスをクリックで情報と距離、Esc でもどる（回転はしない）
public partial class GameRunner
{
    bool exploring;
    Transform exploreFocus;
    int exploreSquare = -1;

    void StartExplore()
    {
        exploring = true;
        exploreSquare = -1;
        exploreFocus = new GameObject("ExploreFocus").transform;
        exploreFocus.position = pieces[S.CurrentPlayer.Id].position;
        cameraTarget = exploreFocus;
        subMenu = ExploreMenu;
        RefreshAll();
    }

    void EndExplore()
    {
        exploring = false;
        if (exploreFocus != null) Destroy(exploreFocus.gameObject);
        cameraTarget = pieces[S.CurrentPlayer.Id];
        subMenu = null;
        RefreshAll();
    }

    void ExploreMenu()
    {
        Add("もどる", EndExplore, KeyCode.Escape);
        DrawMenu(exploreSquare < 0 ? "探索：WASD・矢印で移動、マスをクリックで くわしく（Escで もどる）" : SquareInfo(exploreSquare));
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
        if (sq.RequiredBadges > p.Badges) return text + $"　まだ入れない（バッジ{sq.RequiredBadges}個で解放）";
        if (!Movement.CanEnter(board, id, p.Badges, game.TopBadges)) return text + $"　まだ通れない（バッジ{sq.GateBadges}個で通れる" + (sq.GateShared ? "。だれか1人が そろえれば 全員" : "") + "）";
        int d = Movement.Distance(board, p.Square, id, p.Badges, game.TopBadges);
        return text + (d < 0 ? "　道では 行けない（ワープの先）" : d == 0 ? "　今いるマス" : $"　ここから {d}マス");
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
