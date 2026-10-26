using System.Linq;
using Sugoroku.Rules;
using UnityEngine;

// 「せってい」メニュー（16.6、UI仕様書 E）：職業・順位・イベントルール・あそびかた・設定・セーブ。図鑑は メニューの「ずかん」
public partial class GameRunner
{
    void SystemMenu()
    {
        var p = S.CurrentPlayer;
        Add("職業", () => ShowJobs(p));
        Add("順位", ShowRanking);
        Add("イベントルール", ShowRule);
        Add("あそびかた", () => ShowHelp(0));
        Add("設定", ShowGameSettings);
        Add("セーブ", () => { CloseOverlay(); subMenu = SaveMenu; RefreshAll(); });
        Add("もどる", () => { CloseOverlay(); subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("せってい");
    }

    // 手持ちの詳細（16.6 #41）：6ステータス、特性、技3つ、状態、レベル、コスト、弱点、出会い
    void ShowPokemon(PlayerState p, int slot)
    {
        var m = p.Party[slot];
        var sp = Pokemons.Of(data, m);
        var col = Overlay("Pokemon");
        if (PokemonIcon(m.Species) != null)
        {
            var artRow = Row(col);
            artRow.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 180;
            MonIcon(artRow, m.Species, 180);
        }
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
    // 1ページに 8列×4段。絵・番号・名前
    void ShowDex(PlayerState p, int page = 0)
    {
        const int cols = 8, perPage = cols * 4;
        var seen = data.Species.Where(s => p.Dex.Contains(s.Name)).OrderBy(s => s.Number).ToList();
        int pages = Mathf.Max(1, (seen.Count + perPage - 1) / perPage);
        page = Mathf.Clamp(page, 0, pages - 1);
        var col = Overlay("Dex");
        Ui.Line(col, $"{p.Name}の 図鑑　{p.Dex.Count}／{data.Species.Count}種類" + (pages > 1 ? $"　{page + 1}／{pages}ページ" : ""), 44, PlayerColor(p));
        var grid = Ui.Rect(col, "Grid", Vector2.zero, Vector2.one);
        grid.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 4 * 150;
        var g = grid.gameObject.AddComponent<UnityEngine.UI.GridLayoutGroup>();
        g.cellSize = new Vector2(170, 140); g.spacing = new Vector2(6, 10);
        g.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = cols;
        foreach (var s in seen.Skip(page * perPage).Take(perPage))
        {
            var cell = Ui.Stack(grid, "Mon", Vector2.zero, Vector2.one, true, 2, 0);
            cell.GetComponent<UnityEngine.UI.VerticalLayoutGroup>().childAlignment = TextAnchor.UpperCenter;
            cell.GetComponent<UnityEngine.UI.VerticalLayoutGroup>().childForceExpandWidth = false;
            MonIcon(cell, s.Name, 96);
            Ui.Line(cell, $"{s.Number}.{s.Name}", 20).alignment = TextAnchor.UpperCenter;
        }
        var row = Row(col);
        if (pages > 1)
        {
            Ui.Button(row, "まえのページ", () => ShowDex(p, page - 1), 240, 64).interactable = page > 0;
            Ui.Button(row, "つぎのページ", () => ShowDex(p, page + 1), 240, 64).interactable = page < pages - 1;
        }
        Ui.Button(row, "閉じる", CloseOverlay, 200, 64);
    }

    // 順位（16.6 #46）
    void ShowRanking()
    {
        var col = Overlay("Ranking");
        Ui.Line(col, "順位（順位用ポイント：お金とファンも含めた いまの豊かさ、12章）", 40);
        int rank = 0;
        foreach (var p in S.Players.OrderByDescending(game.Score))
            Ui.Line(col, $"{++rank}位　{p.Name}　{game.Score(p):0.#}p　バッジ{p.Badges}　{p.Money:#,0}円　ファン{S.Owners.Count(o => o.Value == p.Id && board[o.Key].Type == "トレーナー")}人　所有マス{S.Owners.Count(o => o.Value == p.Id)}　{board[p.Square].Place}" + (board[p.Square].MapId != "" ? "（中）" : "") + (p.LastPlaceWeeks > 0 && rank == S.Players.Count ? $"（最下位 {p.LastPlaceWeeks}週）" : ""), 28, PlayerColor(p));
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // 職業（16.6 #45）：今の職業、解放済みの一覧と効果
    void ShowJobs(PlayerState p)
    {
        var col = Overlay("Jobs");
        Ui.Line(col, $"{p.Name}の 職業：" + (p.Job == "" ? "なし" : p.Job), 44, PlayerColor(p));
        if (p.UnlockedJobs.Count == 0) Ui.Line(col, "まだ 解放した職業は ありません。職業はポケモンセンターで 就いたり 変えたりできます", 26);
        foreach (var name in p.UnlockedJobs)
            Ui.Line(col, $"{(name == p.Job ? "★" : "　")}{name}：{data.Jobs.FirstOrDefault(j => j.Name == name)?.Effect}", 26);
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // ゲーム中の設定（16.6 #40a）：タイトルの設定と同じ（音量、バトル演出、ヒント）
    void ShowGameSettings()
    {
        var col = Overlay("GameSettings");
        Ui.Line(col, "設定", 48);
        Ui.Button(col, "バトル演出を短くする：" + (shortBattle ? "オン" : "オフ"), () => { shortBattle = !shortBattle; PlayerPrefs.SetInt("short_battle", shortBattle ? 1 : 0); ShowGameSettings(); }, 600, 64);
        VolumeRow(col, "マスター", () => Sound.Master, v => Sound.Master = v, ShowGameSettings);
        VolumeRow(col, "BGM", () => Sound.Bgm, v => Sound.Bgm = v, ShowGameSettings);
        VolumeRow(col, "効果音", () => Sound.Se, v => { Sound.Se = v; sound.PlaySe("item"); }, ShowGameSettings);
        Ui.Button(col, "はじめての場面のヒント：" + (HintsOn ? "オン" : "オフ"), () => { HintsOn = !HintsOn; ShowGameSettings(); }, 600, 64);
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
