using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// デバッグ画面（仕様書17.1）。F1 で開く。ボタンはすべてデバッグ命令（DebugCommand）として処理役に渡す
// 開発用ビルドとエディタだけで使える（GameProcessor.DebugEnabled）
public partial class GameRunner
{
    RectTransform debugPanel;
    bool debugWarp, seeAll;   // seeAll：見渡しの制限を外す（別マップの中も見える）
    int labelMode;            // マスの上の文字：0＝ふつう、1＝ID、2＝今いるマスからの距離（探索でマスを選ぶと道順も光る）
    static readonly string[] LabelModes = { "ふつう", "ID", "距離・道順" };
    int debugDice = 1;
    string debugItem = "キズくすり", debugPokemon = "ピカチュウ", debugLevel = "10";
    static readonly string[] Weathers = { "", "雨季", "乾季", "すなあらしの日" };
    static readonly string[] WeekEvents = { "", "セール", "ストライキ" };
    readonly Dictionary<SugorokuSquare, string> labelBackup = new Dictionary<SugorokuSquare, string>();

    void ToggleDebug()
    {
        if (debugPanel != null) { Destroy(debugPanel.gameObject); debugPanel = null; debugWarp = false; return; }
        BuildDebugPanel();
    }

    void Dbg(DebugAction action, int value = 0, string text = "") =>
        Send(new DebugCommand { Player = S.CurrentPlayer.Id, Action = action, Value = value, Text = text });

    void BuildDebugPanel()
    {
        if (debugPanel != null) Destroy(debugPanel.gameObject);
        debugPanel = Ui.Panel(canvas.transform, "Debug", new Vector2(0.74f, 0.31f), new Vector2(1, 0.935f), new Color(0.25f, 0.05f, 0.05f, 0.92f)).rectTransform;
        var col = Ui.Stack(debugPanel, "Rows", Vector2.zero, Vector2.one, true, 4, 8);
        Ui.Line(col, "デバッグ（F1で閉じる）", 24, new Color(1, 0.8f, 0.4f));

        var row = DebugRow(col);
        Ui.Button(row, debugWarp ? "ワープ：マスをクリック" : "ワープ", () => { debugWarp = !debugWarp; BuildDebugPanel(); }, 230, 40);
        Ui.Button(row, "マス：" + LabelModes[labelMode], () => { labelMode = (labelMode + 1) % LabelModes.Length; BuildDebugPanel(); }, 200, 40);
        ShowSquareLabels(); // 距離は 動くたびに変わるので、パネルを描くたびに出し直す
        row = DebugRow(col);
        Ui.Button(row, seeAll ? "見渡しの制限：なし" : "見渡しの制限：あり", () => { seeAll = !seeAll; BuildDebugPanel(); }, 300, 40);

        row = DebugRow(col);
        Ui.Button(row, "−", () => { debugDice = Mathf.Max(0, debugDice - 1); BuildDebugPanel(); }, 50, 40);
        Ui.Button(row, $"次の出目 {debugDice}", () => Dbg(DebugAction.SetDice, debugDice), 180, 40);
        Ui.Button(row, "＋", () => { debugDice = Mathf.Min(40, debugDice + 1); BuildDebugPanel(); }, 50, 40);

        row = DebugRow(col);
        Ui.Button(row, "お金＋1万", () => Dbg(DebugAction.Money, 10000), 140, 40);
        Ui.Button(row, "ボール＋5", () => Dbg(DebugAction.Balls, 5), 140, 40);
        Ui.Button(row, "全回復", () => Dbg(DebugAction.HealAll), 140, 40);

        row = DebugRow(col);
        Ui.Button(row, "バッジ＋1", () => Dbg(DebugAction.Badge), 140, 40);
        Ui.Button(row, "レベル＋5", () => Dbg(DebugAction.LevelUp, 5), 140, 40);
        Ui.Button(row, "図鑑を見る", () => AddLog("図鑑：" + string.Join("、", S.CurrentPlayer.Dex)), 140, 40);

        row = DebugRow(col);
        Ui.Input(row, debugItem, 20, 230, v => debugItem = v);
        Ui.Button(row, "道具をもらう", () => Dbg(DebugAction.Item, 0, debugItem.Trim()), 190, 40);

        row = DebugRow(col);
        Ui.Input(row, debugPokemon, 12, 170, v => debugPokemon = v);
        Ui.Input(row, debugLevel, 3, 70, v => debugLevel = v, char.IsDigit);
        Ui.Button(row, "ポケモン追加", () => Dbg(DebugAction.AddPokemon, int.TryParse(debugLevel, out int lv) ? lv : 5, debugPokemon.Trim()), 180, 40);
        row = DebugRow(col);
        Ui.Button(row, "↑のポケモンとバトル", () => Dbg(DebugAction.Battle, int.TryParse(debugLevel, out int lv2) ? lv2 : 5, debugPokemon.Trim()), 260, 40);
        Ui.Button(row, "週を進める", () => Dbg(DebugAction.NextWeek), 160, 40);
        row = DebugRow(col);
        foreach (var st in new[] { "やけど", "どく", "まひ", "ねむり" }) Ui.Button(row, st, () => Dbg(DebugAction.Status, 0, st), 95, 40);
        Ui.Button(row, "治す", () => Dbg(DebugAction.Status, 0, ""), 70, 40);

        row = DebugRow(col);
        Ui.Button(row, "即勝利", () => Dbg(DebugAction.WinBattle), 140, 40);
        Ui.Button(row, "即敗北", () => Dbg(DebugAction.LoseBattle), 140, 40);

        row = DebugRow(col);
        int w = System.Array.IndexOf(Weathers, S.Weather);
        Ui.Button(row, "天候：" + (S.Weather == "" ? "なし" : S.Weather), () => Dbg(DebugAction.Weather, 0, Weathers[(w + 1) % Weathers.Length]), 230, 40);
        int e = System.Array.IndexOf(WeekEvents, S.WeekEvent);
        Ui.Button(row, "週イベント：" + (S.WeekEvent == "" ? "なし" : S.WeekEvent), () => Dbg(DebugAction.WeekEvent, 0, WeekEvents[(e + 1) % WeekEvents.Length]), 200, 40);
    }

    static RectTransform DebugRow(Transform parent)
    {
        var row = Ui.Stack(parent, "Row", Vector2.zero, Vector2.one, false, 6, 0);
        var le = row.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 44;
        le.flexibleHeight = 0;
        return row;
    }

    void DebugWarpClick()
    {
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(Input.mousePosition), out var hit)) return;
        var sq = hit.collider.GetComponentInParent<SugorokuSquare>();
        if (sq == null) return;
        debugWarp = false;
        Dbg(DebugAction.Warp, sq.id);
        if (debugPanel != null) BuildDebugPanel();
    }

    // 全マスの番号か、今いるマスからの距離を表示する（マスの上の文字を一時的に差し替える）
    void ShowSquareLabels()
    {
        var p = S.CurrentPlayer;
        foreach (var kv in squares)
        {
            var label = kv.Value.GetComponentInChildren<TextMesh>();
            if (label == null) continue;
            if (!labelBackup.ContainsKey(kv.Value)) labelBackup[kv.Value] = label.text;
            if (labelMode == 0) { label.text = labelBackup[kv.Value]; continue; }
            if (labelMode == 1 || !board.Squares.ContainsKey(kv.Key)) { label.text = kv.Key.ToString(); continue; }
            int d = Movement.Distance(board, p.Square, kv.Key, p.Badges, game.TopBadges);
            label.text = !Movement.CanEnter(board, kv.Key, p.Badges, game.TopBadges) ? "×" : d < 0 ? "−" : d.ToString();
        }
    }

    // 経路探索の結果（17.1）：今いるマスから そのマスまでの いちばん短い道順を光らせる
    void ShowRoute(int goal)
    {
        var p = S.CurrentPlayer;
        int d = Movement.Distance(board, p.Square, goal, p.Badges, game.TopBadges);
        var path = d > 0 ? Movement.FindPath(board, p.Square, d, p.Badges, false, goal, null, game.TopBadges) : null;
        if (path == null) return;
        foreach (int id in path) if (id != goal && squares.TryGetValue(id, out var s)) s.SetGlow(new Color(1, 0.55f, 0.1f), 0.9f);
    }
}
