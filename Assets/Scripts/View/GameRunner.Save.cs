using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;

// セーブ・ロード（1.2）と、結果発表・エンドロール（1.5、16.1 #11）
// セーブ枠は 0＝オートセーブ（週の終わり）、1〜3＝手動
public partial class GameRunner
{
    int lastAutoSaveWeek;
    RectTransform overlay;

    static string SaveDir => Path.Combine(Application.persistentDataPath, "saves");
    static string SlotPath(int slot) => Path.Combine(SaveDir, $"slot{slot}.txt");
    static string SlotName(int slot) => slot == 0 ? "オートセーブ" : $"スロット{slot}";

    // 各枠の中身（読めなければ summary は null）
    IEnumerable<(int slot, SaveFile.Summary summary)> SaveSlots()
    {
        for (int slot = 0; slot <= 3; slot++)
        {
            SaveFile.Summary summary = null;
            try { if (File.Exists(SlotPath(slot))) summary = SaveFile.Read(File.ReadAllText(SlotPath(slot))).summary; } catch { }
            yield return (slot, summary);
        }
    }

    static string SlotLabel(int slot, SaveFile.Summary s) =>
        s == null ? $"{SlotName(slot)}：（空き）" : $"{SlotName(slot)}：{s.SavedAt:M/d HH:mm}　{s.Week}／{s.MaxWeeks}週　次は {s.NextPlayer}";

    // 一時ファイルに書いてから置きかえる。前のセーブは .bak に残す（17.4）
    void SaveTo(int slot)
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            string path = SlotPath(slot), tmp = path + ".tmp";
            File.WriteAllText(tmp, SaveFile.Write(S));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
            if (slot != 0) AddLog($"{SlotName(slot)}に セーブしました");
        }
        catch (Exception e) { AddLog("× セーブできませんでした：" + e.Message); }
    }

    void SaveMenu()
    {
        foreach (var (slot, summary) in SaveSlots().Where(x => x.slot > 0))
        {
            int k = slot;
            if (summary == null) Add(SlotLabel(slot, null), () => { SaveTo(k); subMenu = null; RefreshAll(); });
            else Add(SlotLabel(slot, summary), () => { subMenu = () => OverwriteMenu(k); RefreshAll(); });
        }
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("どこに セーブする？");
    }

    // 上書きの確認（16.9）
    void OverwriteMenu(int slot)
    {
        Add("上書きする", () => { SaveTo(slot); subMenu = null; RefreshAll(); }, KeyCode.Return);
        Add("やめる", () => { subMenu = SaveMenu; RefreshAll(); }, KeyCode.Escape);
        DrawMenu($"{SlotName(slot)}を 上書きしますか？");
    }

    // ---- つづきから ----

    void ShowLoad()
    {
        var bg = FullScreen("Load");
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.2f, 0.1f), new Vector2(0.8f, 0.9f), true, 16, 0);
        Ui.Line(col, "つづきから", 56);
        foreach (var (slot, summary) in SaveSlots())
        {
            int k = slot;
            Ui.Button(col, SlotLabel(slot, summary), () => LoadFrom(k), 900, 70).interactable = summary != null;
        }
        Ui.Button(col, "もどる", ShowTitle, 900, 70);
    }

    void LoadFrom(int slot)
    {
        // 読めなければ、ひとつ前のセーブ（.bak）を試す（17.4）
        string error = null;
        foreach (var path in new[] { SlotPath(slot), SlotPath(slot) + ".bak" }.Where(File.Exists))
        {
            game = GameProcessor.Load(board, data, File.ReadAllText(path), out _, out error);
            if (game == null) continue;
            BeginPlay(new List<GameEvent>());
            cameraTarget = pieces[S.CurrentPlayer.Id];
            AddLog(path.EndsWith(".bak") ? $"{SlotName(slot)}が読めなかったので、ひとつ前のセーブから はじめます" : $"{SlotName(slot)}から はじめます");
            AddLog($"{S.Week}週目　{S.CurrentPlayer.Name}の ばん");
            return;
        }
        var bg = FullScreen("LoadError");
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.15f, 0.2f), new Vector2(0.85f, 0.8f), true, 16, 0);
        Ui.Line(col, "このセーブは 読み込めません", 44, new Color(1, 0.5f, 0.5f));
        Ui.Line(col, error ?? "", 24);
        Ui.Line(col, "（データ（xlsx・CSV）を変えると、前のセーブが読めなくなることがあります）", 22, new Color(0.8f, 0.8f, 0.8f));
        Ui.Button(col, "もどる", ShowLoad, 400, 70);
    }

    // ---- 結果発表（16.1 #11）とエンドロール（1.5）----

    RectTransform Overlay(string name)
    {
        if (overlay != null) Destroy(overlay.gameObject);
        overlay = Ui.Panel(canvas.transform, name, Vector2.zero, Vector2.one, new Color(0.03f, 0.05f, 0.1f, 0.96f)).rectTransform;
        return Ui.Stack(overlay, "Rows", new Vector2(0.12f, 0.06f), new Vector2(0.88f, 0.94f), true, 10, 0);
    }

    void CloseOverlay() { if (overlay != null) Destroy(overlay.gameObject); overlay = null; }

    void ShowResults()
    {
        var col = Overlay("Results");
        Ui.Line(col, "結果発表", 56, new Color(1, 0.85f, 0.4f));
        var rule = data.EventRules.FirstOrDefault(r => r.Id == S.EventRule);
        int rank = 0;
        foreach (var p in S.Players.OrderByDescending(game.FinalScore))
        {
            var (points, badges, money, fans, ruleP) = game.ScoreBreakdown(p);
            Ui.Line(col, $"{++rank}位　{p.Name}{CharacterLabel(p)}　{game.FinalScore(p):0.#}p", 36, PlayerColor(p));
            Ui.Line(col, $"　バッジ {badges:0.#}p　お金 {money:0.#}p（{p.Money:#,0}円）　ファン {fans:0.#}p" + (points != 0 ? $"　ポイント {points:0.#}p" : "") + (rule != null ? $"　{rule.Name} {ruleP:0.#}p" : ""), 24);
        }
        var row = Row(col);
        Ui.Button(row, "エンドロールへ", () => ShowEndroll(0), 300, 70);
        Ui.Button(row, "閉じる", CloseOverlay, 200, 70);
    }

    // プレイヤーごとの旅を1ページずつ（1.5）
    // ponytail: ページを手で送る形。流れるエンドロール（1人1分、○で早送り）にするときは、ここを時間で進める
    void ShowEndroll(int index)
    {
        sound.PlayBgm("endroll");
        var players = S.TurnOrder.Select(id => S.Players[id]).ToList();
        if (index >= players.Count) { ShowResults(); return; }
        var p = players[index];
        var col = Overlay("Endroll");
        Ui.Line(col, narrator.Line("endroll_start", "{トレーナー}の たび", ("トレーナー", p.Name)), 52, PlayerColor(p));
        var c = data.Characters.FirstOrDefault(x => x.Id == p.Character);
        if (c != null) Ui.Line(col, $"キャラクター：{c.Name}", 26);
        var first = p.Party.Concat(p.Box).FirstOrDefault(m => m.MetHow == "最初のポケモン");
        if (first != null) Ui.Line(col, narrator.Line("endroll_first", "さいしょの パートナー") + $"：{first.Species}" + (first.MetSpecies != first.Species ? $"（出会ったときは {first.MetSpecies}）" : ""), 28);
        // 旅の年表（週と場所つき）。多いときは はじめと終わりを見せる
        if (p.Journal.Count > 0)
        {
            Ui.Line(col, narrator.Line("endroll_journey", "たびの きろく"), 30, new Color(1, 0.85f, 0.4f));
            var lines = p.Journal.Count <= 10 ? p.Journal : p.Journal.Take(5).Concat(p.Journal.Skip(p.Journal.Count - 5)).ToList();
            foreach (var j in lines) Ui.Line(col, $"　{j.Week}週目　{j.Place}　{j.Text}", 22);
        }
        Ui.Line(col, "さいごの 手持ち", 30, new Color(1, 0.85f, 0.4f));
        foreach (var m in p.Party)
            Ui.Line(col, $"　{m.Species} Lv{m.Level}" + (m.HeldItem != "" ? $"（{m.HeldItem}）" : "") + "　" + MetText(m), 24);
        var longest = p.Party.Concat(p.Box).OrderByDescending(m => m.PartyWeeks).FirstOrDefault();
        if (longest != null && longest.PartyWeeks > 0) Ui.Line(col, narrator.Line("endroll_longest", "いちばん長く いっしょだった ポケモン") + $"：{longest.Species}（{longest.PartyWeeks}週）", 24);
        Ui.Line(col, $"歩いたマス {p.StepsWalked}　バトル {p.Battles}回　図鑑 {p.Dex.Count}種類　捕まえた種類 {p.CaughtSpecies.Count}　バッジ {p.Badges}個　ファン {S.Owners.Count(o => o.Value == p.Id)}人", 24);
        var row = Row(col);
        Ui.Button(row, index + 1 < players.Count ? "つぎの人へ" : "結果発表へ", () => ShowEndroll(index + 1), 300, 70);
        Ui.Button(row, "とばす", ShowResults, 200, 70);
    }

    string MetText(PokemonState m)
    {
        string how = m.MetHow ?? "";
        if (how.EndsWith("で目覚めた")) return narrator.Line("endroll_revive", "{週}週目、{どこ}", ("週", m.MetWeek), ("どこ", how)); // 化石（3.2.2）
        if (how.EndsWith("から受け取った"))
            return narrator.Line("endroll_trade", "{週}週目、{相手}から うけとった", ("週", m.MetWeek), ("相手", how.Substring(0, how.Length - "から受け取った".Length)));
        string text = narrator.Line("endroll_meet", "{週}週目、{場所}で であった", ("週", m.MetWeek), ("場所", m.MetPlace));
        return (how == "捕獲" || how == "" ? text : $"{text}（{how}）") + (m.MetSpecies != null && m.MetSpecies != m.Species ? $"　出会ったときは {m.MetSpecies}" : "");
    }
}
