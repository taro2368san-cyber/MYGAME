using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sugoroku.Rules
{
    public sealed class BoardSquare
    {
        public int Id;
        public string Name, Type, Place, Area;
        public string ShopId = "";          // ショップマスのとき、ショップ.csv の id（空なら場所で探す）
        public string MapId = "";
        public string GrassKind = "";       // 草むらマスの中の種類：草むら／海上／洞窟
        public bool Sea => GrassKind == "海上"; // 海上（出現表は「水上」、2マス以内で つり。4.1.2）           // メインマップ以外の板（洞窟・建物・島）の id。メインマップは ""
        public int WarpId;
        public string Symbol = "";       // シンボルのマスにいる伝説（空なら その場所の出現表「シンボル」）。島のシンボルは チケットで決まる
        public float X, Z;               // 表示用の座標（ルールでは使わない）
        public int RequiredBadges;       // この場所に入るのに必要なバッジの数
        public int GateBadges;           // このマスを通るのに必要なバッジの数（マスの通行条件。0 なら条件なし）
        public bool GateShared;          // true：だれか1人が条件を満たせば全員が通れる。false：満たした人だけ
        public int Map;                  // 道でつながったかたまりの番号（ワープでしか行けない所は別のかたまりになる）
        public int WarpPartner = -1;     // 同じワープ番号の相手のマス（いなければ -1）
        public readonly List<int> Neighbors = new List<int>();
    }

    // 盤面（マスと道）。座標とつながりは分けて持ち、ルールはつながりだけを見る（仕様書3.1）
    public sealed class BoardData
    {
        public readonly Dictionary<int, BoardSquare> Squares = new Dictionary<int, BoardSquare>();
        public int StartSquare = -1;

        public BoardSquare this[int id] => Squares[id];

        // Unity で書き出した 盤面_マス.csv / 盤面_道.csv を読む。場所からエリアと解放条件を引く
        public static BoardData Load(string dir, GameData data, List<string> problems)
        {
            var squares = new List<BoardSquare>();
            var sq = new CsvTable("盤面_マス", File.ReadAllText(Path.Combine(dir, "盤面_マス.csv")), problems);
            foreach (var r in sq.Rows)
            {
                var place = data.Places.FirstOrDefault(p => p.Name == sq.Get(r, "場所"));
                var s = new BoardSquare
                {
                    Id = int.Parse(sq.Get(r, "ID")), Name = sq.Get(r, "名前"), Type = sq.Get(r, "種類"), Place = sq.Get(r, "場所"),
                    WarpId = int.TryParse(sq.Get(r, "ワープ番号"), out int w) ? w : 0, ShopId = sq.Get(r, "ショップ"), MapId = sq.Get(r, "マップID"), GrassKind = sq.Get(r, "種類") == "草むら" ? sq.Get(r, "草むらの種類") : "",
                    GateBadges = int.TryParse(sq.Get(r, "通行バッジ"), out int gate) ? gate : 0, GateShared = sq.Get(r, "通行は全員") == "はい", Symbol = sq.Get(r, "シンボル"),
                    X = float.Parse(sq.Get(r, "X"), CultureInfo.InvariantCulture), Z = float.Parse(sq.Get(r, "Z"), CultureInfo.InvariantCulture),
                };
                if (place == null) problems.Add($"[エラー] 盤面: マス「{s.Name}」の場所「{s.Place}」が xlsx のエリアシートにありません");
                else { s.Area = place.Area; s.RequiredBadges = ParseUnlock(place.Unlock); }
                squares.Add(s);
            }
            var rd = new CsvTable("盤面_道", File.ReadAllText(Path.Combine(dir, "盤面_道.csv")), problems);
            var roads = rd.Rows.Select(r => (int.Parse(rd.Get(r, "A")), int.Parse(rd.Get(r, "B"))));
            var board = Create(squares, roads, problems);
            CheckSquareData(board, data, problems);
            return board;
        }

        // マスの種類に必要なデータがそろっているか（足りなくても遊べるので警告）
        static void CheckSquareData(BoardData board, GameData data, List<string> problems)
        {
            var all = board.Squares.Values.ToList();
            var names = new HashSet<string>(all.Select(s => s.Name));
            foreach (var g in all.Where(s => s.Sea && !data.Encounters.Any(e => e.Place == s.Place && e.Method == "水上")).GroupBy(s => s.Place))
                problems.Add($"[警告] 盤面: {g.Key}の海上{g.Count()}マスは、出現シートに{g.Key}の水上がないので野生が出ません");
            foreach (var g in all.Where(s => s.Type == "草むら" && !s.Sea && !data.Encounters.Any(e => e.Place == s.Place && e.Method == "草むら")).GroupBy(s => s.Place))
                problems.Add($"[警告] 盤面: {g.Key}の草むら{g.Count()}マスは、出現シートに{g.Key}の草むらがないので野生が出ません");
            foreach (var s in all.Where(s => s.GateBadges > 8)) // バッジは8個まで（3.3）
                problems.Add($"[警告] 盤面: マス「{s.Name}」（{s.Place}）の通行バッジが{s.GateBadges}個です。バッジは8個までなので、だれも通れません");
            foreach (var s in all.Where(s => s.GateBadges > 0 && s.GateBadges <= s.RequiredBadges))
                problems.Add($"[警告] 盤面: マス「{s.Name}」（{s.Place}）の通行バッジ{s.GateBadges}個は、場所の解放条件（バッジ{s.RequiredBadges}個）より多くないので、意味がありません");
            foreach (var s in all.Where(s => s.Type == "ショップ"))
            {
                if (s.ShopId != "" && data.FindShop(s.ShopId) == null) problems.Add($"[エラー] 盤面: ショップ「{s.Name}」のショップid「{s.ShopId}」がショップ.csvにありません");
                else if (s.ShopId == "" && !data.Shops.Any(x => x.Place == s.Place)) problems.Add($"[警告] 盤面: ショップ「{s.Name}」（{s.Place}）に合うショップがショップ.csvにありません（マスに「ショップid」を入れるか、場所の行を足す）");
            }
            foreach (var s in all.Where(s => s.Type == "ライバル" && !data.Rivals.Any(r => r.Place == s.Place)))
                problems.Add($"[警告] 盤面: ライバルマス「{s.Name}」（{s.Place}）で戦うライバルがライバル表にありません");
            foreach (var s in all.Where(s => s.Type == "ジム" && !data.GymMembers.Any(m => m.Gym == data.Places.FirstOrDefault(p => p.Name == s.Place)?.Gym)))
                problems.Add($"[警告] 盤面: ジム「{s.Name}」（{s.Place}）のジムがエリアシート・ジムシートにありません");
            foreach (var g in all.Where(s => s.Type == "ジム").Select(s => s.Place).Distinct().Where(p => !data.Trainers.Any(t => t.IsGymTrainer && t.Place == p)))
                problems.Add($"[警告] 盤面: {g}にジムトレーナーがいません（トレーナー.csv の種類「ジムトレーナー」）。ジムにはすぐ挑めます");
            foreach (var t in data.Trainers.Where(t => board.Squares.Values.FirstOrDefault(s => s.Name == t.Square) is BoardSquare s && (s.Type != "トレーナー" || s.Place != t.Place)))
                problems.Add($"[警告] トレーナー.csv: {t.Id} のマス「{t.Square}」は盤面では {board.Squares.Values.First(s => s.Name == t.Square).Place}の{board.Squares.Values.First(s => s.Name == t.Square).Type}です");
            // 伝説のシンボル（4.x）：島の外のシンボルのマスには、マスに書いたポケモンか、その場所の出現表「シンボル」が要る
            foreach (var s in all.Where(s => s.Type == "シンボル" && !data.IsIslandMap(s.MapId)))
            {
                if (s.Symbol != "" && data.FindSpecies(s.Symbol) == null) problems.Add($"[エラー] 盤面: シンボルのマス「{s.Name}」のポケモン「{s.Symbol}」がポケモンシートにありません");
                else if (s.Symbol == "" && !data.Encounters.Any(e => e.Place == s.Place && e.Method == "シンボル")) problems.Add($"[警告] 盤面: シンボルのマス「{s.Name}」（{s.Place}）に ポケモンの名前がなく、出現シートにも {s.Place}の シンボルがありません（何も出ません）");
            }
            // 島マップ（3.4）：港（船着場）、伝説の島ならシンボル。島があるのに本マップの船着場がなければ渡れない
            foreach (var i in data.IslandMaps)
            {
                var onIsland = all.Where(s => s.Place == i.Name).ToList();
                if (onIsland.Count == 0) { problems.Add($"[警告] 盤面: 島マップ「{i.Name}」のマスがまだありません（チケットで ここには渡れません）"); continue; }
                if (!onIsland.Any(s => s.Type == "船着場")) problems.Add($"[エラー] 盤面: 島マップ「{i.Name}」に港（船着場）のマスがありません");
                if (i.Kind == "伝説の島" && !onIsland.Any(s => s.Type == "シンボル")) problems.Add($"[警告] 盤面: 伝説の島「{i.Name}」にシンボルのマスがありません（伝説が出ません）");
                if (onIsland.Any(s => s.MapId != i.MapId)) problems.Add($"[警告] 盤面: 島マップ「{i.Name}」のマスのマップIDが「{i.MapId}」になっていません（板の mapId を確かめてください）");
            }
            if (data.IslandMaps.Any(i => all.Any(s => s.Place == i.Name)) && !all.Any(s => s.Type == "船着場" && !data.IsIslandMap(s.MapId)))
                problems.Add("[警告] 盤面: クチバシティに船着場のマスがありません。チケットを使えません（3.4）");
            if (!all.Any(s => s.Type == "ポイント交換NPC"))
                problems.Add("[警告] 盤面: ポイント交換NPC（換金所）のマスがありません。お金を勝利ポイントに換えられないので、最終得点はバッジとイベントルールだけになります（2.1）");
            foreach (var s in all.Where(s => s.Type == "ゴール"))
                problems.Add($"[警告] 盤面: 「{s.Name}」の種類が「ゴール」です。今のルールでは何も起きないので、種類を変えてください");
            foreach (var n in data.Trainers.Select(t => t.Square).Concat(data.EventSquares.Select(e => e.Square)).Where(n => n != "" && !names.Contains(n)))
                problems.Add($"[警告] 表に書かれたマス「{n}」が盤面にありません");
        }

        // 「最初から」→0、「バッジ○個」→○。それ以外（チケットなど）は普通には入れない
        public static int ParseUnlock(string unlock)
        {
            if (unlock == "最初から" || unlock == "チケット") return 0; // 島はチケットの船でしか行けない（3.4）
            var m = Regex.Match(unlock, @"バッジ(\d+)個");
            return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
        }

        public static BoardData Create(IEnumerable<BoardSquare> squares, IEnumerable<(int a, int b)> roads, List<string> problems)
        {
            var board = new BoardData();
            foreach (var s in squares)
            {
                if (board.Squares.ContainsKey(s.Id)) problems.Add($"[エラー] 盤面: マスの番号 {s.Id} が重複しています");
                else board.Squares[s.Id] = s;
            }
            foreach (var (a, b) in roads)
            {
                if (!board.Squares.ContainsKey(a) || !board.Squares.ContainsKey(b) || a == b) { problems.Add($"[エラー] 盤面: 道 {a}－{b} のマスがありません"); continue; }
                if (!board[a].Neighbors.Contains(b)) { board[a].Neighbors.Add(b); board[b].Neighbors.Add(a); }
            }

            // ワープ番号は種類が「ワープ」のマスだけ見る（ワープマスを複製して種類を変えたマスに番号が残っていても無視）
            foreach (var g in board.Squares.Values.Where(s => s.WarpId > 0 && s.Type == "ワープ").GroupBy(s => s.WarpId))
            {
                var pair = g.ToList();
                if (pair.Count == 2) { pair[0].WarpPartner = pair[1].Id; pair[1].WarpPartner = pair[0].Id; }
                else problems.Add($"[警告] 盤面: ワープ{g.Key}は{pair.Count}マスあります（2つで対になります。対になるまで、止まっても何も起きません）");
            }

            int map = 0;
            foreach (var s in board.Squares.Values.Where(s => s.Map == 0))
            {
                map++;
                var stack = new Stack<int>(); stack.Push(s.Id); s.Map = map;
                while (stack.Count > 0)
                    foreach (int n in board[stack.Pop()].Neighbors)
                        if (board[n].Map == 0) { board[n].Map = map; stack.Push(n); }
            }

            var starts = board.Squares.Values.Where(s => s.Type == "スタート").ToList();
            if (starts.Count == 1) board.StartSquare = starts[0].Id;
            else problems.Add($"[エラー] 盤面: スタートのマスが{starts.Count}個あります（1個にしてください）");
            foreach (var s in board.Squares.Values.Where(s => s.Neighbors.Count == 0 && s.WarpPartner < 0))
                problems.Add($"[警告] 盤面: マス「{s.Name}」はどこにもつながっていません");
            return board;
        }
    }
}
