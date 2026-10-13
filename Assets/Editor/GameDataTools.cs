using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Sugoroku.Rules;
using UnityEditor;
using UnityEngine;

// メニュー「Tools > すごろく」で、xlsx のデータを CSV に書き出してチェックする。
// xlsx はプロジェクトのフォルダにある「ポケモンゲームデータ.xlsx」、なければ「ポケモンゲームデータ_v○○.xlsx」のうち番号が一番大きいもの。
public static class GameDataTools
{
    public static string DataDir => Path.Combine(Application.streamingAssetsPath, "GameData");

    [MenuItem("Tools/すごろく/データをチェック（xlsxを読み直す）")]
    static void ExportAndCheck() => LoadAll(log: true);

    // xlsx と盤面を書き出して読み込む。問題があれば Console に出す
    static (GameData data, BoardData board) LoadAll(bool log)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new System.InvalidOperationException("プレイ中は書き出せません。▶を止めてから実行してください");
        string xlsx = Export();
        var problems = new List<string>();
        var data = GameData.Load(DataDir, problems);

        // 盤面の場所名が、xlsx のエリアシートにあるか
        var places = new HashSet<string>(data.Places.Select(p => p.Name));
        foreach (var b in Object.FindObjectsByType<SugorokuBoard>())
            if (!places.Contains(b.place)) problems.Add($"[エラー] 地図の板「{b.name}」の場所「{b.place}」が xlsx のエリアシートにありません");

        int numbered = ExportBoard(problems);
        var board = BoardData.Load(DataDir, data, problems);

        if (log)
        {
            foreach (var p in problems)
            {
                if (p.StartsWith("[エラー]")) Debug.LogError(p);
                else Debug.LogWarning(p);
            }
            Debug.Log($"データのチェック完了（{Path.GetFileName(xlsx)}）: ポケモン{data.Species.Count}体、アイテム{data.Items.Count}個、場所{data.Places.Count}か所、出現{data.Encounters.Count}行、調整値{data.Tuning.Count}個、盤面{board.Squares.Count}マス・道{board.Squares.Values.Sum(s => s.Neighbors.Count) / 2}本"
                + (numbered > 0 ? $"（新しく番号をつけたマス{numbered}個。シーンを保存してください）" : "")
                + $" ／ エラー{problems.Count(p => p.StartsWith("[エラー]"))}件、警告{problems.Count(p => p.StartsWith("[警告]"))}件");
        }
        return (data, board);
    }

    [MenuItem("Tools/すごろく/ルールのテスト")]
    static void RunRulesTest()
    {
        foreach (var line in RulesSelfTest.Run()) Debug.Log(line);
        var (data, board) = LoadAll(log: false);
        int maxDice = (int)data.Required("dice_max");
        foreach (int badges in new[] { 0, 8 })
        {
            var mismatches = RulesSelfTest.CompareModes(board, maxDice, badges);
            if (mismatches.Count == 0) Debug.Log($"OK 実際の盤面（バッジ{badges}個）：全{board.Squares.Count}マス×出目1〜{maxDice}で、ハイライトと1マスずつの止まれるマスが一致");
            else Debug.LogError($"実際の盤面（バッジ{badges}個）で、ハイライトと1マスずつの結果が違う: {string.Join("、", mismatches.Take(10))}");
        }
        var stuck = Enumerable.Range(1, maxDice).Where(d => Movement.Reachable(board, board.StartSquare, d, 0, false).Count == 0 && Movement.Reachable(board, board.StartSquare, d, 0, true).Count == 0).ToList();
        if (stuck.Count == 0) Debug.Log($"OK スタートから出目1〜{maxDice}のどれでも、止まれるマスがある");
        else Debug.LogError($"スタートから止まれるマスがない出目: {string.Join(",", stuck)}");
        foreach (var line in RulesSelfTest.RunWithData(data, board)) Debug.Log(line);
        Debug.Log("ルールのテスト完了");
    }

    [MenuItem("Tools/すごろく/バランスを見る（CPU 4人×50週×5回）")]
    static void RunBalance()
    {
        var (data, board) = LoadAll(log: false);
        foreach (var line in RulesSelfTest.BalanceReport(data, board, 5, 50)) Debug.Log(line);
    }

    // シーンのマスと道を 盤面_マス.csv / 盤面_道.csv に書き出す。番号のないマスには番号をつけ、つけた数を返す
    static int ExportBoard(List<string> problems)
    {
        var squares = Object.FindObjectsByType<SugorokuSquare>().OrderBy(s => s.name).ToList();
        var used = new HashSet<int>();
        int next = squares.Count == 0 ? 1 : squares.Max(s => s.id) + 1, numbered = 0;
        foreach (var s in squares)
        {
            if (s.id > 0 && used.Add(s.id)) continue;
            Undo.RecordObject(s, "マスに番号をつける");
            s.id = next++;
            used.Add(s.id);
            PrefabUtility.RecordPrefabInstancePropertyModifications(s);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(s.gameObject.scene);
            numbered++;
        }

        string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string Q(object v) => "\"" + v.ToString().Replace("\"", "\"\"") + "\"";
        var sq = new StringBuilder("\"ID\",\"名前\",\"種類\",\"場所\",\"ワープ番号\",\"ショップ\",\"マップID\",\"X\",\"Z\"\n");
        foreach (var s in squares.OrderBy(s => s.id))
            sq.Append(string.Join(",", Q(s.id), Q(s.name), Q(s.type), Q(s.Place ?? ""), Q(s.warpId), Q(s.shopId ?? ""), Q(s.MapId), Q(F(s.transform.position.x)), Q(F(s.transform.position.z)))).Append('\n');
        var rd = new StringBuilder("\"A\",\"B\"\n");
        foreach (var r in Object.FindObjectsByType<SugorokuRoad>())
        {
            if (r.squareA == null || r.squareB == null) { problems.Add($"[エラー] 盤面: 道「{r.name}」の片方のマスがありません（道を消してください）"); continue; }
            rd.Append(Q(r.squareA.id)).Append(',').Append(Q(r.squareB.id)).Append('\n');
        }
        File.WriteAllText(Path.Combine(DataDir, "盤面_マス.csv"), sq.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(DataDir, "盤面_道.csv"), rd.ToString(), new UTF8Encoding(true));
        return numbered;
    }

    // xlsx の全シートを StreamingAssets/GameData/<シート名>.csv に書き出し、使った xlsx のパスを返す
    public static string Export()
    {
        string xlsx = FindXlsx();
        Directory.CreateDirectory(DataDir);
        var sheets = new HashSet<string>();
        using (var zip = new ZipArchive(File.OpenRead(xlsx), ZipArchiveMode.Read))
        {
            var shared = ReadSharedStrings(zip);
            var workbook = LoadXml(zip, "xl/workbook.xml", out var ns);
            var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels", out _);
            foreach (XmlElement sheet in workbook.SelectNodes("//m:sheets/m:sheet", ns))
            {
                string rid = sheet.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                string target = rels.DocumentElement.ChildNodes.Cast<XmlElement>().First(r => r.GetAttribute("Id") == rid).GetAttribute("Target");
                var doc = LoadXml(zip, "xl/" + target.TrimStart('/').Replace("xl/", ""), out var sns);
                File.WriteAllText(Path.Combine(DataDir, sheet.GetAttribute("name") + ".csv"), ToCsv(doc, sns, shared), new UTF8Encoding(true));
                sheets.Add(sheet.GetAttribute("name"));
            }
        }
        // 設計メモ の仮の表（トレーナー.csv など）は、xlsx に同じ名前のシートがなければそのまま使う
        string memo = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "設計メモ");
        foreach (var csv in Directory.Exists(memo) ? Directory.GetFiles(memo, "*.csv") : new string[0])
            if (!sheets.Contains(Path.GetFileNameWithoutExtension(csv)))
                File.Copy(csv, Path.Combine(DataDir, Path.GetFileName(csv)), true);
        AssetDatabase.Refresh();
        return xlsx;
    }

    static string FindXlsx()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string fixedName = Path.Combine(root, "ポケモンゲームデータ.xlsx");
        if (File.Exists(fixedName)) return fixedName;
        var versioned = Directory.GetFiles(root, "ポケモンゲームデータ_v*.xlsx")
            .Select(f => (path: f, m: Regex.Match(Path.GetFileName(f), @"_v(\d+)\.xlsx$")))
            .Where(x => x.m.Success).OrderByDescending(x => int.Parse(x.m.Groups[1].Value)).FirstOrDefault();
        if (versioned.path == null) throw new FileNotFoundException($"{root} に ポケモンゲームデータ.xlsx がありません");
        return versioned.path;
    }

    static XmlDocument LoadXml(ZipArchive zip, string entry, out XmlNamespaceManager ns)
    {
        var doc = new XmlDocument();
        using (var s = zip.GetEntry(entry).Open()) doc.Load(s);
        ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        return doc;
    }

    // 共有文字列。ふりがな（rPh）は除く
    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") == null) return list;
        var doc = LoadXml(zip, "xl/sharedStrings.xml", out var ns);
        foreach (XmlElement si in doc.SelectNodes("//m:si", ns))
            list.Add(string.Concat(si.SelectNodes("m:t | m:r/m:t", ns).Cast<XmlNode>().Select(t => t.InnerText)));
        return list;
    }

    static string ToCsv(XmlDocument sheet, XmlNamespaceManager ns, List<string> shared)
    {
        var sb = new StringBuilder();
        foreach (XmlElement row in sheet.SelectNodes("//m:sheetData/m:row", ns))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (XmlElement c in row.SelectNodes("m:c", ns))
            {
                string type = c.GetAttribute("t"), v = c.SelectSingleNode("m:v", ns)?.InnerText ?? "";
                string value = type == "s" ? shared[int.Parse(v)] : type == "inlineStr" ? c.SelectSingleNode("m:is", ns)?.InnerText ?? "" : v;
                cells[ColumnIndex(c.GetAttribute("r"))] = value;
            }
            if (cells.Count == 0 || cells.Values.All(x => x.Trim() == "")) continue;
            int last = cells.Keys.Max();
            sb.AppendLine(string.Join(",", Enumerable.Range(0, last + 1).Select(i => "\"" + (cells.TryGetValue(i, out var x) ? x : "").Replace("\"", "\"\"") + "\"")));
        }
        return sb.ToString();
    }

    static int ColumnIndex(string cellRef)
    {
        int n = 0;
        foreach (char ch in cellRef.TakeWhile(char.IsLetter)) n = n * 26 + (ch - 'A' + 1);
        return n - 1;
    }
}
