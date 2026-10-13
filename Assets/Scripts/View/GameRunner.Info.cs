using System.Linq;
using Sugoroku.Rules;
using UnityEngine;

// 「その他」メニュー（16.6）：手持ちの詳細・図鑑・順位・イベントルール。どれも見るだけ
public partial class GameRunner
{
    void OtherMenu()
    {
        var p = S.CurrentPlayer;
        for (int i = 0; i < p.Party.Count; i++) { int k = i; Add($"くわしく：{p.Party[i].Species}", () => ShowPokemon(p, k)); }
        Add("図鑑", () => ShowDex(p));
        Add("順位", ShowRanking);
        Add("イベントルール", ShowRule);
        Add("もどる", () => { CloseOverlay(); subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("その他");
    }

    // 手持ちの詳細（16.6 #41）：6ステータス、特性、技3つ、状態、レベル、コスト、弱点、出会い
    void ShowPokemon(PlayerState p, int slot)
    {
        var m = p.Party[slot];
        var sp = Pokemons.Of(data, m);
        var col = Overlay("Pokemon");
        Ui.Line(col, $"{m.Species}　Lv{m.Level}" + (m.Fainted ? "（たおれている）" : ""), 48, TypeColor(sp.Type));
        Ui.Line(col, $"タイプ {sp.Type}　弱点 {sp.Weakness}　コスト {Pokemons.Cost10(data, m) / 10.0:0.0}　必殺は{(Pokemons.Attack(data, m) >= Pokemons.SpAttack(data, m) ? "こうげき" : "とくこう")}で出す", 28);
        Ui.Line(col, $"HP {m.Hp}/{Pokemons.MaxHp(data, m)}　こうげき {Pokemons.Attack(data, m)}　ぼうぎょ {Pokemons.Defense(data, m)}　とくこう {Pokemons.SpAttack(data, m)}　とくぼう {Pokemons.SpDefense(data, m)}　すばやさ {Pokemons.Speed(data, m)}", 28);
        Ui.Line(col, $"技：{sp.PhysicalMove}（こうげき）／{sp.SpecialMove}（とくこう）／{sp.SuperMove}（必殺）", 26);
        if (sp.Ability != "") Ui.Line(col, "特性：" + sp.Ability, 24);
        Ui.Line(col, "もちもの：" + (m.HeldItem != "" ? $"{m.HeldItem}（{data.FindItem(m.HeldItem)?.Effect}）" : "なし"), 24);
        if (m.Status != Status.None) Ui.Line(col, "状態：" + Pokemons.StatusName(m.Status), 24);
        int next = Pokemons.ExpForLevel(m.Level + 1) - m.Exp;
        Ui.Line(col, $"つぎのLvまで {next}　先頭にしたターン {m.LeadTurns}", 24);
        Ui.Line(col, "出会い：" + MetText(m), 24);
        var evo = sp.Evolutions.Select(e => e.Kind == EvolutionKind.Level ? $"Lv{e.Value}で{e.Target}" : e.Kind == EvolutionKind.Item ? $"{e.Item}で{e.Target}" : e.Kind == EvolutionKind.LeadTurns ? $"先頭{e.Value}ターンで{e.Target}" : $"交換で{e.Target}").ToList();
        if (evo.Count > 0) Ui.Line(col, "進化：" + string.Join("、", evo), 24);
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // 図鑑（16.6 #44）
    void ShowDex(PlayerState p)
    {
        var col = Overlay("Dex");
        Ui.Line(col, $"{p.Name}の 図鑑　{p.Dex.Count}／{data.Species.Count}種類", 44, PlayerColor(p));
        Ui.Line(col, string.Join("　", data.Species.Where(s => p.Dex.Contains(s.Name)).OrderBy(s => s.Number).Select(s => $"{s.Number}.{s.Name}")), 24);
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // 順位（16.6 #46）
    void ShowRanking()
    {
        var col = Overlay("Ranking");
        Ui.Line(col, "順位（順位用ポイント、12章）", 44);
        int rank = 0;
        foreach (var p in S.Players.OrderByDescending(game.Score))
            Ui.Line(col, $"{++rank}位　{p.Name}　{game.Score(p):0.#}p　バッジ{p.Badges}　{p.Money:#,0}円　ファン{S.Owners.Count(o => o.Value == p.Id)}人　{board[p.Square].Place}" + (p.LastPlaceWeeks > 0 && rank == S.Players.Count ? $"（最下位 {p.LastPlaceWeeks}週）" : ""), 28, PlayerColor(p));
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // イベントルール（16.6 #47）
    void ShowRule()
    {
        var col = Overlay("Rule");
        var rule = data.EventRules.FirstOrDefault(r => r.Id == S.EventRule);
        Ui.Line(col, "今回のイベントルール", 44);
        Ui.Line(col, rule == null ? "なし" : $"{rule.Name}：{rule.Description}", 32);
        if (rule != null) foreach (var p in S.Players) Ui.Line(col, $"{p.Name}　いま {game.EventRulePoints(p):0.#}p", 28, PlayerColor(p));
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }
}
