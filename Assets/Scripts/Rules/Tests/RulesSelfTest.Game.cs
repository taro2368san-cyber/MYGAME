using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 1・2・3・5・12章（はじめの状態・旅の記録・イベントルール・研究室・博物館・週の処理・下位救済）の 確定の決まりのテスト。数は調整値とデータから読む
    // マスに止まるテストは テスト用の小さな町で組む（ユーザーが盤面を作っている途中なので、本番の盤面のマスIDには たよらない）。本番の盤面は ランダムに遊ばせるときだけ使う
    public static partial class RulesSelfTest
    {
        static void GameRules(GameData data, BoardData board, List<string> log)
        {
            double T(string key) => data.Required(key);
            PokemonState Mon(string species, int level) => Pokemons.Create(data, species, level, "", "", 1);

            // テスト用の町（輪）：1スタート（研究室）─2センター─3ショップ─4ジム─5モンスターボール─6博物館─7トレーナー（エリア1）─8トレーナー（エリア2）─9ロケット団（エリア1）─1
            string shopPlace = data.Shops.Select(s => s.Place).FirstOrDefault(pl => !string.IsNullOrEmpty(pl)) ?? "";
            string gymPlace = data.Places.FirstOrDefault(pl => !string.IsNullOrEmpty(pl.Gym) && data.GymMembers.Any(m => m.Gym == pl.Gym))?.Name ?? "";
            var museumRow = data.EventSquares.FirstOrDefault(x => x.Effect.Kind == EffectKind.Museum);
            BoardSquare Sq(int id, string type, string place, string area = "エリア1", string name = null) => new BoardSquare { Id = id, Name = name ?? "g" + id, Type = type, Place = place, Area = area };
            var town = BoardData.Create(new[] {
                    Sq(1, "スタート", "マサラタウン"), Sq(2, "ポケモンセンター", "トキワシティ"), Sq(3, "ショップ", shopPlace), Sq(4, "ジム", gymPlace), Sq(5, "モンスターボール", "1番道路"),
                    Sq(6, "イベントマス", "ニビシティ", name: museumRow?.Square), Sq(7, "トレーナー", "1番道路"), Sq(8, "トレーナー", "2番道路", "エリア2"), Sq(9, "ロケット団", "1番道路") },
                new[] { (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7), (7, 8), (8, 9), (9, 1) }, new List<string>());
            GameProcessor Town(int players = 1, ulong seed = 5)
            {
                var g = GameProcessor.NewGame(town, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ゼニガメ"), ("C", MoveMode.Highlight, "フシギダネ") }.Take(players).ToList(), 200, seed, out _);
                g.State.NextEventWeek = 9999; // 週イベントで 結果がゆれないように
                g.DebugEnabled = true;
                return g;
            }
            // 手番の人を となりに置いて、1歩で target に止まる
            List<GameEvent> Go(GameProcessor g, int target)
            {
                var p = g.State.CurrentPlayer;
                p.Square = town[target].Neighbors[0];
                p.FixedDice = 1;
                Apply(g, new RollDice { Player = p.Id });
                Apply(g, new ChooseDestination { Player = p.Id, Square = target });
                return Apply(g, new ConfirmMove { Player = p.Id });
            }
            List<GameEvent> NextWeek(GameProcessor g) => Apply(g, new DebugCommand { Player = g.State.CurrentPlayer.Id, Action = DebugAction.NextWeek });

            // ---- 1.1 はじめの状態 ----
            {
                var g = GameProcessor.NewGame(town, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Step, "ゼニガメ"), ("C", MoveMode.Highlight, "フシギダネ") }, 50, 21, out var ev, null, "random");
                var s = g.State;
                bool each = s.Players.All(p => p.Money == (int)T("start_money") && p.Balls.TryGetValue("モンスターボール", out int b) && b == (int)T("start_balls") && p.BallCount == b
                    && p.Party.Count == 1 && p.Party[0].Level == (int)T("start_level") && p.Party[0].MetHow == "最初のポケモン" && p.Square == town.StartSquare && p.Box.Count == 0);
                bool order = s.TurnOrder.OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 }) && ev.OfType<TurnOrderDecided>().SingleOrDefault()?.Order.SequenceEqual(s.TurnOrder) == true;
                var orders = new HashSet<string>();
                for (ulong seed = 1; seed <= 30; seed++)
                    orders.Add(string.Join(",", GameProcessor.NewGame(town, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ゼニガメ"), ("C", MoveMode.Highlight, "フシギダネ") }, 50, seed, out _).State.TurnOrder));
                var rule = data.EventRules.FirstOrDefault(r => r.Id == s.EventRule);
                bool shown = rule != null && ev.OfType<EventRuleDecided>().Any(x => x.Name == rule.Name);
                Check(log, $"1.1 はじめ：全員 所持金{T("start_money")}円・モンスターボール{T("start_balls")}個・最初のポケモンLv{T("start_level")}で スタートのマスに置かれる。手番の順番はランダム（30回で{orders.Count}通り）で発表され、ランダムのイベントルールは はじめに全員に公開される（2.3）",
                    s.Week == 1 && each && order && orders.Count > 2 && shown);
            }

            // ---- 1.5 旅の年表：出来事から プレイヤー・週・場所・ポケモン名つきで残す ----
            {
                var g = Town(2);
                var a = g.State.Players[0]; var b = g.State.Players[1];
                g.State.Week = 4; a.Square = 2; b.Square = 3;
                var evs = new List<GameEvent> {
                    new Caught { Player = a.Id, Pokemon = "ポッポ" }, new Caught { Player = a.Id, Pokemon = "コラッタ" }, // 年表に残るのは はじめての捕獲だけ（1体ずつは 出会いの記録）
                    new BadgeEarned { Player = a.Id, Gym = "ニビジム", Badges = 1 },
                    new Evolved { Player = a.Id, From = "ヒトカゲ", To = "リザード" },
                    new JobChanged { Player = a.Id, Job = "やまおとこ" }, new JobChanged { Player = a.Id, Job = "" }, // 職業をやめたのは残さない
                    new RocketDefeated { Player = a.Id, Square = 9 },
                    new LevelCaughtUp { Player = a.Id, Pokemon = "リザード", Level = 20 },
                    new BattleStarted { Kind = BattleKind.Gym, Opponent = "タケシ", Players = new[] { a.Id, -1 } },
                    new RoundResolved { Bond = true, Attacker = 1, DefenderPokemon = "リザード", DefenderTrainer = "A" },
                    new BattleEnded { Winner = 0 },
                    new BattleStarted { Kind = BattleKind.Rival, Opponent = "グリーン", Players = new[] { a.Id, -1 } },
                    new BattleEnded { Winner = 0 },
                    new BattleStarted { Kind = BattleKind.Trainer, Opponent = "たんぱんこぞう", Players = new[] { a.Id, -1 } },
                    new BattleEnded { Winner = 0 }, // ふつうのトレーナーに勝ったのは残さない
                    new BattleStarted { Kind = BattleKind.Player, Players = new[] { a.Id, b.Id } },
                    new BattleEnded { Winner = -1, Draw = true },
                };
                Call(g, "Record", evs);
                var kinds = a.Journal.Select(j => j.Kind).ToList();
                bool all = kinds.SequenceEqual(new[] { "catch", "badge", "evolve", "job", "rocket", "catchup", "bond", "gym", "rival", "draw" });
                bool where = a.Journal.All(j => j.Week == 4 && j.Place == town[2].Place) && b.Journal.Count == 1 && b.Journal[0].Kind == "draw" && b.Journal[0].Place == town[3].Place && b.Journal[0].Text.Contains(a.Name);
                string Text(string kind) => a.Journal.FirstOrDefault(j => j.Kind == kind)?.Text ?? "";
                bool names = Text("catch").Contains("ポッポ") && Text("evolve").Contains("ヒトカゲ") && Text("evolve").Contains("リザード") && Text("catchup").Contains("リザード")
                    && Text("bond").Contains("リザード") && Text("gym").Contains("タケシ") && Text("rival").Contains("グリーン") && Text("draw").Contains(b.Name) && Text("job").Contains("やまおとこ");
                Check(log, "1.5 旅の年表：はじめての捕獲・バッジ・進化・職業への就任・ロケット団／ジム／ライバルの撃破・絆による回避・レベル追いつき・引き分けが、その人の年表に 週・場所・ポケモン名つきで残る（引き分けは両方に）",
                    all && where && names);
            }

            // ---- 1.5 出会いの記録：出会い方・場所・週・出会ったときの姿 ----
            {
                var g = Town(1);
                var p = g.State.CurrentPlayer;
                var starter = p.Party[0];
                bool first = starter.MetHow == "最初のポケモン" && starter.MetPlace == town[town.StartSquare].Place && starter.MetWeek == 1 && starter.MetSpecies == "ヒトカゲ";
                // 捕獲：3週目に トキワシティで
                g.State.Week = 3; p.Square = 2;
                p.Balls["マスターボール"] = 1;
                Apply(g, new DebugCommand { Player = p.Id, Action = DebugAction.Battle, Text = "コラッタ", Value = 3 });
                foreach (var s in g.State.Battle.Sides) if (s.NeedsSelect) Apply(g, new SelectPokemon { Player = s.Player, Slot = 0 });
                g.State.Battle.Step = BattleStep.Ball;
                Apply(g, new ThrowBall { Player = p.Id, Ball = "マスターボール" });
                var caught = p.Party.Concat(p.Box).FirstOrDefault(m => m.Species == "コラッタ");
                bool catchRec = caught != null && caught.MetHow == "捕獲" && caught.MetPlace == town[2].Place && caught.MetWeek == 3 && caught.MetSpecies == "コラッタ";
                // 進化しても 出会ったときの姿は残る
                Pokemons.EvolveTo(data, starter, "リザード");
                bool form = starter.Species == "リザード" && starter.MetSpecies == "ヒトカゲ";
                // プレイヤー間の交換：「〇〇から受け取った」
                var tg = Town(2, 3);
                var me = tg.State.CurrentPlayer; var you = tg.State.Players.First(x => x != me);
                me.Party[0] = Mon("リザードン", 36); you.Party[0] = Mon("リザードン", 36); you.Party.Add(Mon("ゼニガメ", 10)); // 1体だけだと 交換できない
                tg.State.Week = 6;
                Apply(tg, new DebugCommand { Player = me.Id, Action = DebugAction.SetDice, Value = 1 });
                int to = Apply(tg, new RollDice { Player = me.Id }).OfType<DiceRolled>().First().Reachable.First();
                you.Square = to;
                Apply(tg, new ChooseDestination { Player = me.Id, Square = to });
                Apply(tg, new ConfirmMove { Player = me.Id });
                foreach (var s in tg.State.Battle?.Sides ?? new BattleSide[0]) if (s.NeedsSelect) Apply(tg, new SelectPokemon { Player = s.Player, Slot = 0 });
                bool traded = false;
                if (tg.State.Battle?.Kind == BattleKind.Player)
                {
                    Apply(tg, new DebugCommand { Player = me.Id, Action = DebugAction.WinBattle });
                    var taken = you.Party[0]; var given = me.Party[0];
                    if (tg.State.Battle?.Step == BattleStep.Reward) Apply(tg, new ChooseReward { Player = me.Id, Reward = Reward.Trade, Take = 0, Give = 0 });
                    traded = me.Party[0] == taken && taken.MetHow == $"{you.Name}から受け取った" && taken.MetWeek == 6 && taken.MetPlace == town[to].Place
                        && you.Party[0] == given && given.MetHow == $"{me.Name}から受け取った";
                }
                Check(log, "1.5 出会いの記録：最初のポケモン・捕獲・プレイヤー間の交換（「〇〇から受け取った」）で、出会い方・場所・週・出会ったときの姿が残る（進化しても 姿は出会ったときのまま）",
                    first && catchRec && form && traded);
            }

            // ---- 1.5 手持ちにいた週数（「一番長く手持ちにいた」）----
            {
                var g = Town(1);
                var p = g.State.CurrentPlayer;
                p.Box.Add(Mon("コラッタ", 5));
                for (int i = 0; i < 3; i++) NextWeek(g);
                Check(log, "1.5 手持ちにいた週数：週が進むたびに 手持ちの子だけ1増える（控えは増えない）", p.Party[0].PartyWeeks == 3 && p.Box[0].PartyWeeks == 0);
            }

            // ---- 1.5・1.2 旅の記録は セーブを読み直しても同じ ----
            {
                string Journey(GameState s) => string.Join("|", s.Players.Select(p =>
                    string.Join(";", p.Journal.Select(j => $"{j.Week}/{j.Place}/{j.Kind}/{j.Text}")) + "#"
                    + string.Join(";", p.DexFirst.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}:{d.Value.Week}/{d.Value.Place}/{d.Value.Kind}")) + "#"
                    + string.Join(";", p.Party.Concat(p.Box).Select(m => $"{m.Species}<{m.MetSpecies}/{m.MetHow}/{m.MetPlace}/{m.MetWeek}/{m.PartyWeeks}/{m.LeadTurns}")) + "#"
                    + $"{p.StepsWalked}/{p.Battles}/{p.RocketDefeats}"));
                bool same = true; int lines = 0;
                foreach (ulong seed in new ulong[] { 11, 22 })
                {
                    var policy = new Random((int)seed);
                    var game = GameProcessor.NewGame(board, data, new[] { ("たろう", MoveMode.Highlight, "ヒトカゲ"), ("はなこ", MoveMode.Step, "ワニノコ") }, 30, seed, out _, new[] { "leaf", "gold" }, "random");
                    for (int n = 0, accepted = 0; accepted < 1500 && n < 60000 && game.State.Phase != Phase.GameOver; n++)
                        if (game.Apply(RandomCommand(game.State, board, policy), out string e).Count >= 0 && e == null) accepted++;
                    var loaded = GameProcessor.Load(board, data, SaveFile.Write(game.State), out _, out string error);
                    same &= error == null && loaded != null && Journey(loaded.State) == Journey(game.State);
                    lines += game.State.Players.Sum(p => p.Journal.Count);
                }
                Check(log, $"1.5・1.2 旅の記録（年表・図鑑の記録・出会いの記録・手持ちにいた週数・旅の数字）は セーブを読み直しても同じ（ランダムに遊んだ2回、年表{lines}行）", same && lines > 0);
            }

            // ---- 2.2 イベントルールの点 ----
            {
                var bad = new List<string>();
                foreach (var rule in data.EventRules)
                {
                    var g = Town(3);
                    g.State.EventRule = rule.Id;
                    var ps = g.State.Players;
                    int[] counts = { 5, 5, 2 }; // 1位が2人
                    void Clear(PlayerState p) { p.Dex.Clear(); p.CaughtSpecies.Clear(); p.Gyms.Clear(); } // 図鑑には はじめから最初のポケモンが入っているので 消してから数える
                    for (int i = 0; i < 3; i++)
                    {
                        Clear(ps[i]);
                        for (int k = 0; k < counts[i]; k++) { ps[i].Dex.Add("テスト" + k); ps[i].CaughtSpecies.Add("テスト" + k); ps[i].Gyms.Add("テスト" + k); }
                    }
                    bool known = rule.Target == "図鑑の登録数" || rule.Target == "捕まえた種類の数" || rule.Target == "バッジの数";
                    double Want(int i) => rule.Mode == "1位だけ" ? (counts[i] == counts.Max() ? rule.Coef : 0) : counts[i] * rule.Coef;
                    bool ok = known && (rule.Mode == "1位だけ" || rule.Mode == "数に応じて")
                        && Enumerable.Range(0, 3).All(i => Math.Abs(g.EventRulePoints(ps[i]) - Want(i)) < 1e-9 && Math.Abs(g.ScoreBreakdown(ps[i]).Rule - Want(i)) < 1e-9);
                    // だれも数がなければ 1位だけ でも点はない
                    var zero = Town(2); zero.State.EventRule = rule.Id;
                    foreach (var p in zero.State.Players) Clear(p);
                    ok &=zero.State.Players.All(p => zero.EventRulePoints(p) == 0);
                    if (!ok) bad.Add(rule.Name);
                }
                var none = Town(2); none.State.EventRule = "";
                Check(log, $"2.2 イベントルール：ルールの数え方（図鑑・捕まえた種類・バッジ）と方式（数に応じて＝数×係数、1位だけ＝同じ数なら全員に係数）のとおりに数え、結果発表の内訳に入る。ルールなしは0（{data.EventRules.Count}つのルール）"
                    + (bad.Count > 0 ? "。合わない：" + string.Join("、", bad) : ""),
                    data.EventRules.Count > 0 && bad.Count == 0 && none.State.Players.All(p => none.EventRulePoints(p) == 0));
            }

            // ---- 3.2.1 研究室の進化サービス ----
            {
                var itemSp = data.Species.FirstOrDefault(s => s.Evolutions.Any(e => e.Kind == EvolutionKind.Item) && s.Evolutions.All(e => e.Kind != EvolutionKind.Level));
                var tradeSp = data.Species.FirstOrDefault(s => s.Evolutions.Any(e => e.Kind == EvolutionKind.Rival));
                var levelSp = data.FindSpecies("ヒトカゲ");
                if (itemSp != null && tradeSp != null && levelSp != null)
                {
                    int price = (int)T("lab_evolve_price");
                    var g = Town(1);
                    var p = g.State.CurrentPlayer;
                    p.Party.Add(Mon(itemSp.Name, 20)); p.Party.Add(Mon(tradeSp.Name, 20));
                    string itemTo = itemSp.Evolutions.First(e => e.Kind == EvolutionKind.Item).Target, tradeTo = tradeSp.Evolutions.First(e => e.Kind == EvolutionKind.Rival).Target;
                    string levelTo = levelSp.Evolutions.First(e => e.Kind == EvolutionKind.Level).Target;
                    // センター（研究室でない）では使えない
                    p.Money = price * 2;
                    Go(g, 2);
                    bool notCenter = g.State.Facility?.Kind == FacilityKind.Center && g.Validate(new LabEvolve { Player = p.Id, Slot = 1, Target = itemTo }) != null;
                    Apply(g, new LeaveFacility { Player = p.Id });
                    // 研究室：お金が足りないと使えない → 足りれば アイテム進化・交換進化ができる。レベル進化は できない
                    p.Money = price - 1;
                    Go(g, 1);
                    bool lab = g.State.Facility?.Kind == FacilityKind.Center && g.State.Facility.IsLab;
                    bool poor = g.Validate(new LabEvolve { Player = p.Id, Slot = 1, Target = itemTo }) != null;
                    p.Money = price * 2;
                    var ev = Apply(g, new LabEvolve { Player = p.Id, Slot = 1, Target = itemTo });
                    bool itemOk = p.Party[1].Species == itemTo && p.Money == price && p.Dex.Contains(itemTo) && ev.OfType<Evolved>().Any(x => x.To == itemTo) && p.Journal.Any(j => j.Kind == "evolve");
                    Apply(g, new LabEvolve { Player = p.Id, Slot = 2, Target = tradeTo });
                    bool tradeOk = p.Party[2].Species == tradeTo && p.Money == 0;
                    p.Money = price;
                    bool noLevel = g.Validate(new LabEvolve { Player = p.Id, Slot = 0, Target = levelTo }) != null;
                    Check(log, $"3.2.1 研究室の進化サービス：{price}円で アイテム進化（{itemSp.Name}→{itemTo}）・交換進化（{tradeSp.Name}→{tradeTo}）ができる。レベル進化（{levelSp.Name}→{levelTo}）は対象外。お金が足りないと使えない。ふつうのセンターでは使えない",
                        notCenter && lab && poor && itemOk && tradeOk && noLevel);
                }
            }

            // ---- 3.2.2 博物館：Lv の下限、1人1個まで、孵る週 ----
            var fg = Town(1);
            string fossil = data.Items.Select(i => i.Name).FirstOrDefault(n => fg.FossilSpecies(n) != null);
            if (museumRow != null && fossil != null)
            {
                int hatch = (int)T("fossil_hatch_weeks"), offset = (int)T("fossil_level_offset");
                string species = fg.FossilSpecies(fossil);
                (GameProcessor g, PlayerState p) Museum(int depositedWeeksAgo, int topLevel, bool another)
                {
                    var g = Town(1);
                    var p = g.State.CurrentPlayer;
                    g.State.Week = 20;
                    p.Fossil = fossil; p.FossilWeek = g.State.Week - depositedWeeksAgo;
                    Pokemons.SetLevel(data, p.Party[0], topLevel);
                    if (another) p.PokemonBag.Add(fossil);
                    Go(g, 6);
                    return (g, p);
                }
                var (eg, ep) = Museum(hatch - 1, 30, true); // まだ孵らない。預けている間は もう1個は預けられない
                bool early = ep.Fossil == fossil && !ep.Party.Any(m => m.Species == species) && (eg.State.Facility == null || !eg.State.Facility.Options.Any(o => o.Contains("あずける")));
                int low = Math.Max(1, 4 - offset); // 最高Lv＋offset が 5 より下になる Lv
                var (hg, hp) = Museum(hatch, low, true);
                var mon = hp.Party.Concat(hp.Box).FirstOrDefault(m => m.Species == species);
                bool floor = hp.Fossil == "" && mon != null && mon.Level == 5;
                bool again = hg.State.Facility?.Options.Any(o => o.Contains("あずける")) == true; // 受け取ったら また預けられる
                Check(log, $"3.2.2 博物館：預けて{hatch}週で孵る（{hatch - 1}週では まだ）。孵ったLvは 最高Lv{offset:+0;-0}で 最低5（最高Lv{low}なら Lv5）。預けている間は もう1個は預けられない（1人1個）、受け取ったら また預けられる",
                    early && floor && again);
            }

            // ---- 5.3 ストライキとセール ----
            {
                bool Closed(Action<GameProcessor> setup, int square)
                {
                    var g = Town(1);
                    setup(g);
                    var ev = Go(g, square);
                    return g.State.Facility == null && ev.OfType<NothingHappened>().Any(x => x.Reason.Contains("ストライキ"));
                }
                Action<GameProcessor> strike = g => Apply(g, new DebugCommand { Player = g.State.CurrentPlayer.Id, Action = DebugAction.WeekEvent, Text = "ストライキ" });
                bool closed = Closed(strike, 2) && Closed(strike, 1) && Closed(strike, 3) && Closed(strike, 4);
                bool open = !Closed(g => { }, 2) && !Closed(g => { }, 3) && !Closed(g => { }, 4);
                Check(log, "5.3 ストライキ：ポケモンセンター（研究室も）・ショップ・ジムが使えない。ストライキでなければ使える", closed && open);

                // セール：買うときだけ sale_discount 引き。売値は変わらない
                var sg = Town(1);
                var sp = sg.State.CurrentPlayer;
                Go(sg, 3);
                string buy = sg.State.Facility?.Goods.FirstOrDefault(x => (data.FindItem(x)?.Price ?? 0) >= 100);
                var sell = data.Items.FirstOrDefault(i => i.Sellable && i.Price > 0 && !i.IsHeldItem && ItemEffects.BagOf(i) == Bag.Trainer);
                if (buy != null && sell != null)
                {
                    int normalBuy = sg.BuyPrice(buy), normalSell = sg.SellPrice(sell.Name);
                    Apply(sg, new DebugCommand { Player = sp.Id, Action = DebugAction.WeekEvent, Text = "セール" });
                    int list = data.FindItem(buy).Price.Value;
                    sp.Money = 100000; sp.TrainerBag.Clear(); sp.PokemonBag.Clear();
                    Apply(sg, new Buy { Player = sp.Id, Item = buy });
                    bool cheaper = normalBuy == list && 100000 - sp.Money == (int)(list * (1 - T("sale_discount")));
                    sp.TrainerBag.Add(sell.Name);
                    int before = sp.Money;
                    Apply(sg, new Sell { Player = sp.Id, Item = sell.Name });
                    bool sameSell = sp.Money - before == normalSell && normalSell == (int)(sell.Price.Value * T("sell_ratio"));
                    Check(log, $"5.3 セール：買うときだけ {T("sale_discount") * 100:0}%引き（{buy} {list}円→{(int)(list * (1 - T("sale_discount")))}円）。売るときは ふだんと同じ（{sell.Name} {normalSell}円）",
                        cheaper && sameSell);
                }
            }

            // ---- 5.3 ポケニュース：指定の道路で レア枠（いちばん出現率の低い行）が出やすい ----
            {
                var place = data.Encounters.Where(e => e.Method == "草むら").GroupBy(e => e.Place)
                    .FirstOrDefault(gr => gr.Count() >= 2 && gr.Count(e => e.Rate == gr.Min(x => x.Rate)) == 1);
                if (place != null)
                {
                    var rows = place.ToList();
                    var rare = rows.First(e => e.Rate == rows.Min(x => x.Rate));
                    var common = rows.First(e => e != rare);
                    var g = Town(1);
                    int W(Encounter e) => (int)Call(g, "NewsWeight", e, rows);
                    g.State.NewsKind = "ポケニュース"; g.State.NewsPlace = place.Key;
                    int boosted = W(rare);
                    bool up = boosted == (int)Math.Round(rare.Rate.Value * T("news_rare_mult")) && boosted > rare.Rate && W(common) == common.Rate;
                    g.State.NewsPlace = "テストの道路";
                    bool elsewhere = W(rare) == rare.Rate;
                    g.State.NewsKind = "ポケニュース特番"; g.State.NewsPlace = place.Key;
                    bool special = W(rare) == rare.Rate; // 特番は 準伝説のほう（レア枠は上げない）
                    Check(log, $"5.3 ポケニュース：指定の道路（{place.Key}）の草むらだけ、レア枠（{rare.Pokemon} {rare.Rate}）が news_rare_mult 倍（{boosted}）になり、ほかの行と ほかの場所は そのまま", up && elsewhere && special);
                }
            }

            // ---- 5.3 週イベントの抽選は weekly_event_min〜max 週おき ----
            {
                var g = Town(1);
                g.State.NextEventWeek = g.State.Week + 1;
                int min = (int)T("weekly_event_min"), max = (int)T("weekly_event_max");
                var gaps = new List<int>();
                bool inRange = true;
                for (int i = 0; i < 80 && g.State.Phase == Phase.Prepare; i++)
                {
                    int due = g.State.NextEventWeek;
                    NextWeek(g);
                    if (g.State.Week >= due) { int gap = g.State.NextEventWeek - g.State.Week; gaps.Add(gap); inRange &= gap >= min && gap <= max; }
                }
                Check(log, $"5.3 週イベント：抽選は {min}〜{max}週おき（{gaps.Count}回、間隔 {string.Join(",", gaps.Distinct().OrderBy(x => x))}）", gaps.Count >= 8 && inRange && gaps.Distinct().Count() > 1);
            }

            // ---- 12 順位用ポイントと 最下位の数え方 ----
            {
                var g = Town(2);
                var a = g.State.Players[0]; var b = g.State.Players[1];
                g.State.Owners[7] = a.Id; g.State.Owners[8] = a.Id; g.State.Owners[9] = a.Id;
                a.Money = 1234; a.Points = 3; a.Gyms.Add("ニビジム"); a.Gyms.Add("ハナダジム");
                double squares = T("trainer_value_area1") + T("trainer_value_area2") + T("rocket_value_area1");
                bool formula = Math.Abs(g.Score(a) - (3 + 2 * T("badge_point") + (1234 + squares) / T("yen_per_point"))) < 1e-9;
                // お金は b のほうが多いが、マスの換算額を入れると a のほうが上 → 最下位は b
                a.Points = 0; a.Gyms.Clear(); a.Money = 0;
                b.Money = (int)squares - 1;
                NextWeek(g);
                bool lastByScore = b.LastPlaceWeeks == 1 && a.LastPlaceWeeks == 0;
                NextWeek(g);
                bool counting = b.LastPlaceWeeks == 2;
                b.Money = (int)squares; // 同じ点なら 2人とも最下位
                NextWeek(g);
                bool tie = b.LastPlaceWeeks == 3 && a.LastPlaceWeeks == 1;
                b.Money = (int)squares + 1000;
                NextWeek(g);
                bool reset = b.LastPlaceWeeks == 0 && a.LastPlaceWeeks == 2;
                Check(log, "12 最下位の判定：順位用ポイント＝所持ポイント＋バッジ＋（所持金＋トレーナーマス・ロケット団マスの換算額）÷1,000。お金が多くても マスの分で抜かれれば最下位。最下位の週は続けて数え、同じ点なら全員、最下位でなくなると0に戻る",
                    formula && lastByScore && counting && tie && reset);
            }

            // ---- 8.8.1・12 モンスターボールマスの救済：10週連続最下位で 1段階上の表。bt8 は bt8 のまま ----
            if (data.BallTiers.Count >= 2)
            {
                int boost = (int)T("ballmass_boost_weeks");
                var tiers = data.BallTiers.OrderBy(t => t.MinBadges).ToList();
                BallTierUsed Ball(int lastWeeks, int badges)
                {
                    var g = Town(1);
                    var p = g.State.CurrentPlayer;
                    for (int i = 0; i < badges; i++) p.Gyms.Add("テスト" + i);
                    p.LastPlaceWeeks = lastWeeks;
                    return Go(g, 5).OfType<BallTierUsed>().FirstOrDefault();
                }
                var own = Ball(boost - 1, 0); var up = Ball(boost, 0); var top = Ball(boost, tiers.Last().MinBadges);
                bool rescue = own?.Tier == tiers[0].Name && !own.Rescue && up?.Tier == tiers[1].Name && up.Rescue && top?.Tier == tiers.Last().Name && !top.Rescue;
                // 実際に最下位を boost 週 続けると 1段階上
                var cg = Town(2);
                var poor = cg.State.CurrentPlayer; var rich = cg.State.Players.First(x => x != poor);
                rich.Money = 1000000; poor.Money = 0;
                for (int i = 0; i < boost; i++) NextWeek(cg);
                bool chain = poor.LastPlaceWeeks == boost && Go(cg, 5).OfType<BallTierUsed>().FirstOrDefault() is BallTierUsed used && used.Rescue && used.Tier == tiers[1].Name;
                Check(log, $"8.8.1・12 ボールマスの救済：最下位が{boost}週つづくと 1段階上の表（{tiers[0].Name}→{tiers[1].Name}）、{boost - 1}週では まだ。{tiers.Last().Name} は {tiers.Last().Name} のまま", rescue && chain);
            }

            // ---- 12 なぞのチケットの救済：バッジが足りない・トレーナーの道具に空きがないと 来ない ----
            {
                int badges = (int)T("ticket_rescue_badges"), weeks = (int)T("ticket_rescue_weeks");
                int Given(int myBadges, bool fullBag)
                {
                    var g = Town(2);
                    var poor = g.State.CurrentPlayer; var rich = g.State.Players.First(x => x != poor);
                    rich.Money = 1000000; poor.Money = 0;
                    for (int i = 0; i < myBadges; i++) poor.Gyms.Add("テスト" + i);
                    var filler = data.Items.First(i => ItemEffects.BagOf(i) == Bag.Trainer && i.Name != GameProcessor.MysteryTicket && i.Name != GameProcessor.WonderTicket);
                    if (fullBag) while (poor.TrainerBag.Count < g.BagLimit(poor, Bag.Trainer)) poor.TrainerBag.Add(filler.Name);
                    int n = 0;
                    for (int i = 0; i < weeks + 3; i++) n += NextWeek(g).OfType<RescueGiven>().Count();
                    return n;
                }
                Check(log, $"12 なぞのチケットの救済：最下位が{weeks}週つづいても、バッジが{badges - 1}個なら来ない。トレーナーの道具に空きがなければ来ない（{badges}個で空きがあれば来る）",
                    Given(badges - 1, false) == 0 && Given(badges, true) == 0 && Given(badges, false) == 1);
            }
        }
    }
}
