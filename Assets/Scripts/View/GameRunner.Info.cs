using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

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
        var evo = sp.Evolutions.Select(e => $"{EvolutionText(e)}で{e.Target}").ToList();
        if (evo.Count > 0) Ui.Line(col, "進化：" + string.Join("、", evo), 24);
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }

    // ---- 図鑑（UI仕様書 6）：一覧は全種類を番号順に10体ずつ。登録していない種類は黒い影と「？？？」。● 捕まえた ○ 倒しただけ ----
    // 操作：上下 1体、左右 10体、○ くわしく、× もどる、L1/R1（キーボードは Q/E）ほかの人の図鑑
    // ponytail: △のタイプでしぼりこみ（案）はまだ。出る場所は 未解放のエリアも出す（仕様書で未定）
    const int DexPage = 10;
    int dexPlayer, dexIndex; // だれの図鑑か、選んでいる種類（番号順の何番目か）
    bool dexDetail;          // くわしくを出しているか
    bool DexOpen => overlay != null && overlay.name == "Dex";
    List<Species> dexAll;    // 番号順の全種類

    void ShowDex(PlayerState p) { dexPlayer = p.Id; dexDetail = false; DrawDex(); }

    void DrawDex()
    {
        if (dexAll == null) dexAll = data.Species.OrderBy(s => s.Number).ToList();
        var p = S.Players[dexPlayer];
        dexIndex = Mathf.Clamp(dexIndex, 0, dexAll.Count - 1);
        if (dexDetail && p.Dex.Contains(dexAll[dexIndex].Name)) { DrawDexDetail(p, dexAll[dexIndex]); return; }
        dexDetail = false;
        var col = Overlay("Dex");
        int page = dexIndex / DexPage, pages = (dexAll.Count + DexPage - 1) / DexPage;
        Ui.Line(col, $"{p.Name}の ずかん　　登録 {p.Dex.Count}／{dexAll.Count}　　捕まえた {p.CaughtSpecies.Count}　　{page + 1}／{pages}ページ", 40, PlayerColor(p));
        for (int i = page * DexPage; i < Mathf.Min(dexAll.Count, (page + 1) * DexPage); i++)
        {
            var s = dexAll[i];
            bool known = p.Dex.Contains(s.Name);
            int k = i;
            var b = Ui.Button(col, $"{s.Number:000}　{(known ? s.Name : "？？？")}　{(p.CaughtSpecies.Contains(s.Name) ? "●" : known ? "○" : "")}", () => { dexIndex = k; dexDetail = true; DrawDex(); }, 1000, 60);
            if (i == dexIndex) b.image.color = new Color(1f, 0.85f, 0.4f);
            var text = b.GetComponentInChildren<Text>();
            text.alignment = TextAnchor.MiddleLeft; text.fontSize = 28; text.rectTransform.offsetMin = new Vector2(80, 0);
            if (i == dexIndex) text.color = Color.black;
            var icon = Ui.Panel(b.transform, "Mon", new Vector2(0, 0.5f), new Vector2(0, 0.5f), known ? Color.white : new Color(0, 0, 0, 0.85f)); // まだなら影
            icon.sprite = PokemonIcon(s.Name) ?? Ui.Circle; icon.preserveAspect = true; icon.raycastTarget = false;
            icon.rectTransform.sizeDelta = new Vector2(54, 54); icon.rectTransform.anchoredPosition = new Vector2(40, 0);
        }
        Ui.Line(col, "十字キー 上下：1体　左右：10体　○：くわしく　×：閉じる" + (S.Players.Count > 1 ? "　L1/R1（Q/E）：ほかの人の図鑑" : ""), 22, new Color(0.8f, 0.8f, 0.8f));
        var row = Row(col);
        Ui.Button(row, "まえのページ", () => DexInput(0, -1, false, false, 0), 220, 64).interactable = page > 0;
        Ui.Button(row, "つぎのページ", () => DexInput(0, 1, false, false, 0), 220, 64).interactable = page < pages - 1;
        if (S.Players.Count > 1) Ui.Button(row, "ほかの人の図鑑", () => DexInput(0, 0, false, false, 1), 260, 64);
        Ui.Button(row, "閉じる", CloseOverlay, 200, 64);
    }

    // くわしく（UI仕様書 6.2）：絵・タイプ・弱点・特性、種族値、技、進化、出る場所、旅の記録
    void DrawDexDetail(PlayerState p, Species s)
    {
        var col = Overlay("Dex");
        Ui.Line(col, $"{p.Name}の ずかん", 28, PlayerColor(p));
        var artRow = Row(col);
        artRow.GetComponent<LayoutElement>().preferredHeight = 150;
        MonIcon(artRow, s.Name, 150);
        Ui.Line(col, $"No.{s.Number:000}　{s.Name}", 48, TypeColor(s.Type));
        Ui.Line(col, $"タイプ {s.Type}　弱点 {(s.Weakness != "" ? s.Weakness : "なし")}", 28);
        if (s.Ability != "") Ui.Line(col, "特性：" + s.Ability, 24);
        Ui.Line(col, $"種族値　HP {s.Hp}　こうげき {s.Attack}　ぼうぎょ {s.Defense}　とくこう {s.SpAttack}　とくぼう {s.SpDefense}　すばやさ {s.Speed}　合計 {s.Total}", 26);
        string Eff(string e) => string.IsNullOrEmpty(e) || e == "-" ? "" : $"（{e}）";
        Ui.Line(col, $"技：こうげき技 {s.PhysicalMove}{Eff(s.PhysicalMoveEffect)}／とくこう技 {s.SpecialMove}{Eff(s.SpecialMoveEffect)}／必殺技 {s.SuperMove}", 24);
        var before = data.Species.SelectMany(x => x.Evolutions.Where(e => e.Target == s.Name).Select(e => $"{x.Name}から {EvolutionText(e)}")).ToList();
        var after = s.Evolutions.Select(e => $"{EvolutionText(e)}で {e.Target}に").ToList();
        if (before.Count + after.Count > 0) Ui.Line(col, "進化：" + string.Join("　", before.Select(t => "まえ " + t).Concat(after.Select(t => "あと " + t))), 24);
        var places = data.Encounters.Where(e => e.Pokemon == s.Name).Select(e => $"{e.Place}（{e.Method}）").Distinct().ToList();
        Ui.Line(col, "出る場所：" + (places.Count > 0 ? string.Join("、", places) : "野生では出ない"), 24);
        if (p.DexFirst.TryGetValue(s.Name, out var first)) Ui.Line(col, $"旅の記録：{first.Week}週目　{first.Place}で {first.Kind}", 28, new Color(1, 0.85f, 0.4f));
        Ui.Line(col, "○・×：一覧にもどる" + (S.Players.Count > 1 ? "　L1/R1（Q/E）：ほかの人の図鑑" : ""), 22, new Color(0.8f, 0.8f, 0.8f));
        var row = Row(col);
        Ui.Button(row, "一覧にもどる", () => DexInput(0, 0, false, true, 0), 260, 64);
        Ui.Button(row, "閉じる", CloseOverlay, 200, 64);
    }

    static string EvolutionText(Evolution e) =>
        e.Kind == EvolutionKind.Level ? $"Lv{e.Value}" : e.Kind == EvolutionKind.Item ? e.Item : e.Kind == EvolutionKind.LeadTurns ? $"先頭{e.Value}ターン" : "交換";

    // 図鑑の操作（キーボード・コントローラー・画面のボタン 共通）。dy 上下、dx 左右（ページ）、sw ほかの人へ（-1/1）
    void DexInput(int dy, int dx, bool ok, bool back, int sw)
    {
        if (sw != 0) dexPlayer = S.TurnOrder[(S.TurnOrder.IndexOf(dexPlayer) + sw + S.TurnOrder.Count) % S.TurnOrder.Count];
        else if (dexDetail) { if (!ok && !back) return; dexDetail = false; }
        else if (dy != 0) dexIndex += dy;
        else if (dx != 0) dexIndex = Mathf.Clamp(dexIndex + dx * DexPage, 0, dexAll.Count - 1);
        else if (ok) { if (!S.Players[dexPlayer].Dex.Contains(dexAll[dexIndex].Name)) return; dexDetail = true; }
        else if (back) { CloseOverlay(); return; }
        else return;
        DrawDex();
    }

    void DexKeys()
    {
        int dy = Input.GetKeyDown(KeyCode.DownArrow) ? 1 : Input.GetKeyDown(KeyCode.UpArrow) ? -1 : 0;
        int dx = Input.GetKeyDown(KeyCode.RightArrow) ? 1 : Input.GetKeyDown(KeyCode.LeftArrow) ? -1 : 0;
        int sw = Input.GetKeyDown(KeyCode.E) ? 1 : Input.GetKeyDown(KeyCode.Q) ? -1 : 0;
        bool ok = Input.GetKeyDown(KeyCode.Return), back = Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace);
        if (dy != 0 || dx != 0 || sw != 0 || ok || back) DexInput(dy, dx, ok, back, sw);
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
