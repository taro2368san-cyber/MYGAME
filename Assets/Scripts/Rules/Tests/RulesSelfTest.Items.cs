using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 8〜11章（アイテム・職業・所有権）の 確定の決まりのテスト。数は調整値とデータから読む
    // 相談.md で答えを待っているもの（バトルサーチャーの届く範囲＝食い違い12、職業マスの細かい流れ＝相談16）は、今の動きを決めつけない形で試す
    public static partial class RulesSelfTest
    {
        static void ItemRules(GameData data, BoardData board, List<string> log)
        {
            double T(string key) => data.Required(key);
            GameProcessor Make(int players = 1, ulong seed = 5) =>
                GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ヒトカゲ") }.Take(players).ToList(), 50, seed, out _);
            void Open(GameProcessor g, FacilityKind kind, int owner = -1, int square = -1) =>
                Call(g, "OpenFacility", new FacilityState { Kind = kind, Player = g.State.CurrentPlayer.Id, Square = square >= 0 ? square : g.State.CurrentPlayer.Square, Owner = owner }, new List<GameEvent>());
            // 手番の人を target のとなりに置いて、1歩で target に止まる
            List<GameEvent> Land(GameProcessor g, BoardSquare target)
            {
                var p = g.State.CurrentPlayer;
                p.Square = target.Neighbors[0];
                while (p.Badges < Math.Max(target.RequiredBadges, board[p.Square].RequiredBadges)) p.Gyms.Add("テスト" + p.Badges);
                p.FixedDice = 1;
                Apply(g, new RollDice { Player = p.Id });
                Apply(g, new ChooseDestination { Player = p.Id, Square = target.Id });
                return Apply(g, new ConfirmMove { Player = p.Id });
            }
            // プレイヤー戦をして、仕掛けた側（winner）が勝ち、報酬を選ぶところまで。setup で 勝つ人・負ける人の手持ちや袋を整える
            (GameProcessor g, PlayerState win, PlayerState lose) Beat(Action<PlayerState, PlayerState> setup)
            {
                var g = Make(2, 3);
                var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                setup(me, you);
                g.DebugEnabled = true;
                Apply(g, new DebugCommand { Player = me.Id, Action = DebugAction.SetDice, Value = 1 });
                int to = Apply(g, new RollDice { Player = me.Id }).OfType<DiceRolled>().First().Reachable.First();
                you.Square = to;
                Apply(g, new ChooseDestination { Player = me.Id, Square = to });
                Apply(g, new ConfirmMove { Player = me.Id });
                foreach (var s in g.State.Battle?.Sides ?? new BattleSide[0]) if (s.NeedsSelect) Apply(g, new SelectPokemon { Player = s.Player, Slot = 0 });
                if (g.State.Battle?.Kind != BattleKind.Player) return (null, me, you);
                Apply(g, new DebugCommand { Player = me.Id, Action = DebugAction.WinBattle });
                return (g.State.Battle?.Step == BattleStep.Reward ? g : null, me, you);
            }
            PokemonState Mon(string species, int level) => Pokemons.Create(data, species, level, "", "", 1);

            // ---- 8.1 枠 ----
            {
                int tMax = (int)T("trainer_bag"), pMax = (int)T("pokemon_bag");
                bool Fill(string item, int max, Func<PlayerState, List<string>> bag)
                {
                    var g = Make(); var p = g.State.CurrentPlayer;
                    p.TrainerBag.Clear(); p.PokemonBag.Clear();
                    for (int i = 0; i < max - 1; i++) bag(p).Add(item);
                    bool lastFits = !(bool)Call(g, "GiveItem", p, item, new List<GameEvent>()) && bag(p).Count == max;
                    bool ball = !(bool)Call(g, "GiveItem", p, "モンスターボール", new List<GameEvent>()) && bag(p).Count == max; // ボールは枠を使わない
                    bool full = (bool)Call(g, "GiveItem", p, item, new List<GameEvent>()) && bag(p).Count == max && g.State.Facility?.Kind == FacilityKind.BagFull;
                    return lastFits && ball && full;
                }
                Check(log, $"8.1 枠：トレーナーの道具は{tMax}個、ポケモンの道具は{pMax}個まで（そこで いっぱい）。袋がいっぱいでも ボールは別のカウンターに入る",
                    Fill("ダート自転車", tMax, p => p.TrainerBag) && Fill("キズくすり", pMax, p => p.PokemonBag));
            }

            // ---- 8.7 ルーレットは roulette_max 個まで（相談18）：どの自転車も 同じ上限を通る ----
            {
                // 自転車を使って（ダート自転車は乗って）サイコロを振ったときの ルーレットの数
                int Roulette(string bike, ulong seed)
                {
                    var g = Make(1, seed); var p = g.State.CurrentPlayer;
                    p.TrainerBag.Add(bike);
                    Apply(g, new UseItem { Player = p.Id, Item = bike });
                    return Apply(g, new RollDice { Player = p.Id }).OfType<DiceRolled>().First().Dice.Count;
                }
                int DartMax() => Enumerable.Range(1, 40).Max(s => Roulette("ダート自転車", (ulong)s));
                bool now = Roulette("自転車", 1) == 2 && Roulette("マッハ自転車", 1) == 3 && Roulette("ロトム自転車", 1) == Math.Min(4, (int)T("roulette_max"));
                double keep = data.Tuning["roulette_max"];
                bool capped;
                try
                {
                    data.Tuning["roulette_max"] = 3; // テストの中だけ 上限を3に
                    capped = Roulette("ロトム自転車", 1) == 3 && Roulette("マッハ自転車", 1) == 3 && Roulette("自転車", 1) == 2 && DartMax() == Math.Min(3, (int)T("dart_bike_max"));
                }
                finally { data.Tuning["roulette_max"] = keep; }
                Check(log, $"8.7 ルーレット：自転車2個・マッハ自転車3個・ロトム自転車4個（今の上限 {keep}）。上限を3にすると ロトム自転車は3個、ダート自転車も3個を超えない", now && capped && data.Tuning["roulette_max"] == keep);
            }

            // ---- 8.8.1 袋がいっぱいのとき ----
            {
                (PlayerState p, List<string> options) Full(int choose)
                {
                    var g = Make(); var p = g.State.CurrentPlayer;
                    int pMax = (int)T("pokemon_bag");
                    p.PokemonBag.Clear();
                    p.PokemonBag.Add("モーモーミルク");
                    while (p.PokemonBag.Count < pMax) p.PokemonBag.Add("キズくすり");
                    Call(g, "GiveItem", p, "なんでもなおし", new List<GameEvent>());
                    var options = g.State.Facility?.Options.ToList();
                    if (options == null) return (p, null);
                    Apply(g, new ChooseOption { Index = choose < 0 ? options.Count - 1 : choose });
                    return (p, options);
                }
                var (kept, opts) = Full(0);      // 1つ目（モーモーミルク）を捨てて入れる
                var (skipped, _) = Full(-1);     // 拾わない
                Check(log, "8.8.1 袋がいっぱい：袋の中身から1つ選んで捨てて入れる。「拾わない」も選べる（拾ったものを捨てるのと同じ）",
                    opts != null && opts.Count == (int)T("pokemon_bag") + 1 && opts.Last() == "拾わない"
                    && kept.PokemonBag.Contains("なんでもなおし") && !kept.PokemonBag.Contains("モーモーミルク") && kept.PokemonBag.Count == (int)T("pokemon_bag")
                    && !skipped.PokemonBag.Contains("なんでもなおし") && skipped.PokemonBag.Contains("モーモーミルク"));
            }

            // ---- 8.2 もちもの ----
            {
                var g = Make(); var p = g.State.CurrentPlayer;
                p.PokemonBag.Add("いのちのたま"); p.PokemonBag.Add("たべのこし");
                bool notPrepare = g.Validate(new HoldItem { Player = p.Id, Slot = 0, Item = "いのちのたま" }) != null; // 準備フェーズでは着けられない
                Open(g, FacilityKind.Shop);
                bool notShop = g.Validate(new HoldItem { Player = p.Id, Slot = 0, Item = "いのちのたま" }) != null;
                Open(g, FacilityKind.Center);
                Apply(g, new HoldItem { Player = p.Id, Slot = 0, Item = "いのちのたま" });
                Apply(g, new HoldItem { Player = p.Id, Slot = 0, Item = "たべのこし" }); // 着けかえると、前のものは袋へ（1体に1つ）
                bool one = p.Party[0].HeldItem == "たべのこし" && p.PokemonBag.Contains("いのちのたま") && !p.PokemonBag.Contains("たべのこし");
                // 交換で奪われたら、もちものは外れて 元の持ち主の袋に残る（渡した側も同じ）
                var (tg, win, lose) = Beat((w, l) =>
                {
                    w.Party[0] = Mon("リザードン", 36); w.Party[0].HeldItem = "たべのこし";
                    l.Party[0] = Mon("リザードン", 36); l.Party[0].HeldItem = "いのちのたま"; l.Party.Add(Mon("ゼニガメ", 10));
                });
                bool traded = false;
                if (tg != null)
                {
                    var taken = lose.Party[0]; var given = win.Party[0];
                    Apply(tg, new ChooseReward { Player = win.Id, Reward = Reward.Trade, Take = 0, Give = 0 });
                    traded = win.Party[0] == taken && lose.Party[0] == given && taken.HeldItem == "" && given.HeldItem == ""
                        && lose.PokemonBag.Contains("いのちのたま") && win.PokemonBag.Contains("たべのこし");
                }
                Check(log, "8.2 もちもの：着けはずしは ポケモンセンターだけ（準備フェーズ・ショップでは できない）、1体に1つ（着けかえると前のものは袋へ）。交換で渡った子のもちものは外れて 元の持ち主に残る",
                    notPrepare && notShop && one && traded);
            }

            // ---- 8.4 アイテムの奪取 ----
            {
                var (g, win, lose) = Beat((w, l) =>
                {
                    l.TrainerBag.Add("ダート自転車"); l.PokemonBag.Add("キズくすり"); l.Party[0].HeldItem = "いのちのたま";
                });
                bool ok = false;
                if (g != null)
                {
                    bool trainer = g.Validate(new ChooseReward { Player = win.Id, Reward = Reward.Item, Item = "ダート自転車" }) == null;
                    bool pokemon = g.Validate(new ChooseReward { Player = win.Id, Reward = Reward.Item, Item = "キズくすり" }) == null;
                    bool held = g.Validate(new ChooseReward { Player = win.Id, Reward = Reward.Item, Item = "いのちのたま" }) != null; // 着けているもちものは だめ
                    Apply(g, new ChooseReward { Player = win.Id, Reward = Reward.Item, Item = "キズくすり" });
                    ok = trainer && pokemon && held && win.PokemonBag.Contains("キズくすり") && !lose.PokemonBag.Contains("キズくすり") && lose.Party[0].HeldItem == "いのちのたま";
                }
                Check(log, "8.4 奪取：勝った人が 2つの袋のどちらからでも1つ選んで奪う。着けているもちものは選べない", ok);
            }

            // ---- 8.5 使用制限 ----
            {
                // 準備フェーズで使える道具は1個まで
                var g = Make(); var p = g.State.CurrentPlayer;
                p.PokemonBag.Add("キズくすり"); p.PokemonBag.Add("キズくすり");
                p.Party[0].Hp = 1;
                Apply(g, new UseItem { Player = p.Id, Item = "キズくすり", Target = 0 });
                p.Party[0].Hp = 1;
                string second = g.Validate(new UseItem { Player = p.Id, Item = "キズくすり", Target = 0 });
                // 1回休みの人は その番が とばされるので、道具を使う場面がない
                var rg = Make(2); var a = rg.State.CurrentPlayer; var b = rg.State.Players.First(x => x != a);
                b.Ailments.Add(new PlayerAilment { Name = "1回休み", Weeks = 1 });
                b.PokemonBag.Add("キズくすり"); b.Party[0].Hp = 1;
                var ev = new List<GameEvent>();
                Call(rg, "EndTurn", ev);
                bool skipped = ev.OfType<TurnSkipped>().Any(x => x.Player == b.Id) && rg.State.CurrentPlayer != b && rg.Validate(new UseItem { Player = b.Id, Item = "キズくすり", Target = 0 }) != null;
                Check(log, "8.5 使用制限：準備フェーズで使える道具は1個まで。1回休みの人は 番がとばされて 道具を使えない", second != null && second.Contains("1個") && skipped);
            }

            // ---- 8.9 売り買い ----
            {
                var g = Make(); var p = g.State.CurrentPlayer;
                var odd = data.Items.FirstOrDefault(i => i.Sellable && i.Price is int pr && pr % 2 == 1);
                bool half = data.Items.Where(i => i.Price != null).All(i => g.SellPrice(i.Name) == (int)(i.Price.Value * T("sell_ratio")))
                    && (odd == null || g.SellPrice(odd.Name) == odd.Price.Value / 2);
                var unsellable = new[] { "不思議なチケット", "謎のチケット", "ポケベル", "金の入れ歯", "マスターボール" }.Where(n => data.FindItem(n) != null).ToList();
                foreach (var n in unsellable) Call(g, "PutItem", p, n);
                p.PokemonBag.Add("キズくすり");
                Open(g, FacilityKind.Shop);
                var refused = unsellable.Where(n => g.Validate(new Sell { Player = p.Id, Item = n }) != null).ToList();
                int before = p.Money;
                Apply(g, new Sell { Player = p.Id, Item = "キズくすり" });
                Check(log, $"8.9 売り買い：売値は価格×sell_ratio（端数切り捨て）。売れないもの（{string.Join("・", unsellable)}）は売れない",
                    half && unsellable.Count >= 3 && refused.Count == unsellable.Count && p.Money == before + g.SellPrice("キズくすり") && !p.PokemonBag.Contains("キズくすり"));
            }

            // ---- 8.10 お金の出入り ----
            {
                // 年貢は dividend_interval 週ごと。ライバルマスは年貢なし
                var trainerSq = board.Squares.Values.First(s => s.Type == "トレーナー" && s.Area == "エリア2");
                var rivalSq = board.Squares.Values.FirstOrDefault(s => s.Type == "ライバル");
                var rocketSq = board.Squares.Values.FirstOrDefault(s => s.Type == "ロケット団");
                var g = Make();
                g.DebugEnabled = true;
                g.State.Owners[trainerSq.Id] = 0;
                if (rivalSq != null) g.State.Owners[rivalSq.Id] = 0;
                if (rocketSq != null) g.State.Owners[rocketSq.Id] = 0; // ロケット団マスは年貢あり（11.3）
                var weeks = new List<(int week, int amount)>();
                for (int i = 0; i < 16; i++)
                {
                    var ev = Apply(g, new DebugCommand { Action = DebugAction.NextWeek });
                    int week = g.State.Week;
                    foreach (var d in ev.OfType<Dividend>()) weeks.Add((week, d.Amount));
                }
                int interval = (int)T("dividend_interval"), tribute = (int)(T("trainer_value_area2") * T("tribute_ratio"));
                if (rocketSq != null) tribute += (int)(T("rocket_value_area" + new string(rocketSq.Area.Where(char.IsDigit).ToArray())) * T("tribute_ratio"));
                bool every = weeks.Count >= 2 && weeks.Zip(weeks.Skip(1), (x, y) => y.week - x.week).All(d => d == interval) && weeks.All(w => w.amount == tribute);
                // 他人のファンで回復すると、回復代が持ち主に入る。バトルにはならない
                var hg = Make(2);
                var a = hg.State.CurrentPlayer; var owner = hg.State.Players.First(x => x != a);
                hg.State.Owners[trainerSq.Id] = owner.Id;
                a.Money = 5000; owner.Money = 0; a.Party[0].Hp = 1;
                var lev = Land(hg, trainerSq);
                if (hg.State.Facility?.Kind == FacilityKind.FanHeal) lev.AddRange(Apply(hg, new AcceptHeal { Player = a.Id }));
                int heal = (int)(T("trainer_value_area2") * T("fan_heal_ratio"));
                bool paid = !lev.OfType<BattleStarted>().Any() && a.Money == 5000 - heal && owner.Money == heal && a.Party[0].Hp == Pokemons.MaxHp(data, a.Party[0]);
                // 賞金：トレーナーは 相手のLv×trainer_win_money、ジムは エース（いちばん高いLv）×gym_win_money
                int Prize(BattleKind kind, int[] levels)
                {
                    var bg = Make(); var bp = bg.State.CurrentPlayer;
                    bp.Party[0] = Mon("カメックス", 60);
                    bg.DebugEnabled = true;
                    Apply(bg, new DebugCommand { Action = DebugAction.Battle, Text = "コラッタ", Value = levels[0] });
                    var b = bg.State.Battle;
                    b.Kind = kind; if (kind == BattleKind.Gym) b.Gym = "ニビジム";
                    foreach (int lv in levels.Skip(1)) b.Sides[1].Team.Add(Mon("コラッタ", lv));
                    var ev = Apply(bg, new DebugCommand { Action = DebugAction.WinBattle });
                    return ev.OfType<MoneyChanged>().Where(m => m.Player == bp.Id && m.Reason == "賞金").Sum(m => m.Amount);
                }
                bool prizes = Prize(BattleKind.Trainer, new[] { 5 }) == (int)T("trainer_win_money") * 5 && Prize(BattleKind.Gym, new[] { 10, 14 }) == (int)T("gym_win_money") * 14;
                Check(log, $"8.10 お金：年貢は{interval}週ごとに ファン1人につき換算額×tribute_ratio（ロケット団マスも入り、ライバルマスは入らない）。他人のファンでの回復代は 持ち主に入り、バトルにはならない。賞金は トレーナー Lv×trainer_win_money、ジム エースのLv×gym_win_money",
                    every && paid && prizes);
            }

            // ---- 9.1 職業の補正 ----
            {
                var g = Make(); var p = g.State.CurrentPlayer;
                (double atk, double def, double spa, double spd, double hp, double super) Bonus(PokemonState m) =>
                    ((double, double, double, double, double, double))Call(g, "JobBonusFor", p.Id, m);
                var bad = new List<string>();
                foreach (var row in data.JobBonuses.Where(b => b.Type != ""))
                {
                    var mine = data.Species.FirstOrDefault(s => s.Type == row.Type); var other = data.Species.FirstOrDefault(s => s.Type != row.Type);
                    if (mine == null || other == null) continue;
                    p.Job = row.Job;
                    var on = Bonus(Mon(mine.Name, 30)); var off = Bonus(Mon(other.Name, 30));
                    bool ok = on.atk == row.Attack && on.def == row.Defense && on.spa == row.SpAttack && on.spd == row.SpDefense && on.atk > 1 && on.atk == on.def && on.def == on.spa && on.spa == on.spd
                        && off.atk == 1 && off.def == 1 && off.spa == 1 && off.spd == 1;
                    if (!ok) bad.Add(row.Job);
                }
                // ステータス職（タイプなし）は 手持ち全員に同じ倍率
                foreach (var row in data.JobBonuses.Where(b => b.Type == ""))
                {
                    p.Job = row.Job;
                    var x = Bonus(Mon("ヒトカゲ", 30)); var y = Bonus(Mon("ゼニガメ", 30));
                    if (!(x.Equals(y) && x.atk == row.Attack && x.def == row.Defense && x.spa == row.SpAttack && x.spd == row.SpDefense && x.hp == row.Hp)) bad.Add(row.Job);
                }
                Check(log, "9.1 職業の補正：タイプ職は そのタイプの子だけ こうげき・ぼうぎょ・とくこう・とくぼうが同じ倍率で上がり、ほかのタイプには かからない。ステータス職は 手持ち全員に同じ倍率" + (bad.Count > 0 ? "（合わない：" + string.Join("、", bad) + "）" : ""),
                    bad.Count == 0 && data.JobBonuses.Any(b => b.Type != "") && data.JobBonuses.Any(b => b.Type == ""));
            }

            // ---- 9.2 職業の選択 ----
            {
                var jobs = data.JobBonuses.Where(b => b.Type == "").Select(b => b.Job).Distinct().Take(2).ToList();
                var locked = data.Jobs.Select(j => j.Name).FirstOrDefault(n => !jobs.Contains(n));
                bool ok = false;
                if (jobs.Count == 2 && locked != null)
                {
                    var g = Make(); var p = g.State.CurrentPlayer;
                    p.UnlockedJobs.AddRange(jobs);
                    Open(g, FacilityKind.Shop);
                    bool notShop = g.Validate(new ChangeJob { Player = p.Id, Job = jobs[0] }) != null;
                    Open(g, FacilityKind.Center);
                    Apply(g, new ChangeJob { Player = p.Id, Job = jobs[0] });
                    bool first = p.Job == jobs[0];
                    Apply(g, new ChangeJob { Player = p.Id, Job = jobs[1] });
                    var b = ((double, double, double, double, double, double))Call(g, "JobBonusFor", p.Id, p.Party[0]);
                    var row1 = data.JobBonuses.First(r => r.Job == jobs[1] && r.Type == "");
                    bool replaced = p.Job == jobs[1] && b.Item1 == row1.Attack && b.Item3 == row1.SpAttack; // 前の職業の効果は残らない（重ならない）
                    Apply(g, new ChangeJob { Player = p.Id, Job = jobs[0] }); // 前の職業に戻れる
                    bool back = p.Job == jobs[0];
                    bool lockedRefused = g.Validate(new ChangeJob { Player = p.Id, Job = locked }) != null;
                    ok = notShop && first && replaced && back && lockedRefused;
                }
                Check(log, "9.2 職業：就けるのは1つだけで、切り替えると前の効果は消える。解放した職業には何度でも戻れる。まだ解放していない職業には就けない。切り替えは ポケモンセンターで", ok);
            }

            // ---- 9.4 ロケット団職 ----
            if (data.JobBonuses.Any(b => b.Job == "ロケット団" && b.NoCenterHeal))
            {
                var g = Make(); var p = g.State.CurrentPlayer;
                p.Job = "ロケット団"; p.UnlockedJobs.Add("ロケット団");
                string other = data.JobBonuses.Select(b => b.Job).First(j => j != "ロケット団");
                p.UnlockedJobs.Add(other);
                p.Box.Add(Mon("ゼニガメ", 10)); p.PokemonBag.Add("いのちのたま");
                Open(g, FacilityKind.Center);
                bool center = g.Validate(new CenterSwap { Player = p.Id, PartySlot = 0, BoxIndex = 0 }) == null
                    && g.Validate(new HoldItem { Player = p.Id, Slot = 0, Item = "いのちのたま" }) == null
                    && g.Validate(new ChangeJob { Player = p.Id, Job = other }) == null;
                // コストに関係なく 交換で奪える
                var (tg, win, lose) = Beat((w, l) =>
                {
                    w.Job = "ロケット団"; w.Party[0] = Mon("キャタピー", 5);
                    l.Party[0] = Mon("リザードン", 50); l.Party.Add(Mon("ゼニガメ", 10));
                });
                bool free = tg != null && tg.Validate(new ChooseReward { Player = win.Id, Reward = Reward.Trade, Take = 0, Give = 0 }) == null;
                Check(log, "9.4 ロケット団職：センターでも 入れ替え・もちもの・転職は できる（回復だけ できない）。コストに関係なく 交換で奪える", center && free);
            }

            // ---- 11.2 自分のファン ----
            {
                var trainerSq = board.Squares.Values.First(s => s.Type == "トレーナー" && s.Area == "エリア2");
                var g = Make(); var p = g.State.CurrentPlayer;
                g.State.Owners[trainerSq.Id] = p.Id;
                p.Party[0].Hp = 1;
                var ev = Land(g, trainerSq);
                Check(log, "11.2 自分のファン：止まると 回復と アイテム（ボールマスのいちばん下の表から）。バトルにはならない",
                    !ev.OfType<BattleStarted>().Any() && ev.OfType<PartyHealed>().Any() && p.Party[0].Hp == Pokemons.MaxHp(data, p.Party[0]) && ev.OfType<ItemGained>().Any(x => x.Player == p.Id));
            }

            // ---- 11.2.1・11.5・11.6 バトルサーチャーで奪えるマス ----
            {
                // 同じエリアの 他人のファン（トレーナーマス）は 自分のものにできる。ロケット団マス・ライバルマスは アイテムでは奪えない
                var fan = board.Squares.Values.First(s => s.Type == "トレーナー" && s.MapId == "" && s.Neighbors.Any(n => board[n].MapId == "" && board[n].Area == s.Area));
                var rocket = board.Squares.Values.FirstOrDefault(s => s.Type == "ロケット団");
                var rival = board.Squares.Values.FirstOrDefault(s => s.Type == "ライバル");
                var g = Make(2); var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                me.Square = fan.Neighbors.First(n => board[n].MapId == "" && board[n].Area == fan.Area);
                me.TrainerBag.Add("バトルサーチャー");
                g.State.Owners[fan.Id] = you.Id;
                if (rocket != null) g.State.Owners[rocket.Id] = you.Id;
                if (rival != null) g.State.Owners[rival.Id] = you.Id;
                bool rocketRefused = rocket == null || g.Validate(new UseItem { Player = me.Id, Item = "バトルサーチャー", Target = rocket.Id }) != null;
                bool rivalRefused = rival == null || g.Validate(new UseItem { Player = me.Id, Item = "バトルサーチャー", Target = rival.Id }) != null;
                Apply(g, new UseItem { Player = me.Id, Item = "バトルサーチャー", Target = fan.Id });
                Check(log, "11.2.1・11.6 バトルサーチャー：他人のファン（同じエリアのトレーナーマス）を自分のものにできる。ロケット団マス・ライバルマスは アイテムでは奪えない",
                    g.State.Owners[fan.Id] == me.Id && rocketRefused && rivalRefused && rocket != null && rival != null);
            }

            // ---- 8.3 バトルサーチャーは 同じエリアのファンにだけ（食い違い12）----
            {
                // 本マップの別のエリアのファンには使えない。本マップから別マップの中のファンにも使えない。別マップの中にいる人は その中のファンに使える
                var fanA = board.Squares.Values.First(s => s.Type == "トレーナー" && s.MapId == "" && s.Area != null);
                var otherArea = board.Squares.Values.First(s => s.MapId == "" && s.Area != null && s.Area != fanA.Area);
                var subFan = board.Squares.Values.FirstOrDefault(s => s.Type == "トレーナー" && s.MapId != "" && !data.IsIslandMap(s.MapId)
                    && board.Squares.Values.Any(t => t.MapId == s.MapId && t.Id != s.Id));
                (GameProcessor g, PlayerState me) Ready(int stand, int fan)
                {
                    var g = Make(2); var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                    me.Square = stand; me.TrainerBag.Add("バトルサーチャー");
                    g.State.Owners[fan] = you.Id;
                    return (g, me);
                }
                string Try(int stand, int fan) { var (g, me) = Ready(stand, fan); return g.Validate(new UseItem { Player = me.Id, Item = "バトルサーチャー", Target = fan }); }
                string farArea = Try(otherArea.Id, fanA.Id);
                bool sub = false;
                if (subFan != null)
                {
                    var inside = board.Squares.Values.First(t => t.MapId == subFan.MapId && t.Id != subFan.Id);
                    string fromMain = Try(otherArea.Id, subFan.Id);
                    var (sg, sme) = Ready(inside.Id, subFan.Id);
                    Apply(sg, new UseItem { Player = sme.Id, Item = "バトルサーチャー", Target = subFan.Id });
                    sub = fromMain != null && fromMain.Contains("同じエリア") && sg.State.Owners[subFan.Id] == sme.Id;
                }
                // CPU：同じエリアに使える相手がなければ 使おうとしない。同じエリアにあれば使う
                var (cg, cme) = Ready(otherArea.Id, fanA.Id);
                var far = Cpu.Decide(cg, data, board, cme.Id, new Random(1));
                var near = fanA.Neighbors.Select(n => board[n]).FirstOrDefault(n => n.MapId == "" && n.Area == fanA.Area);
                Command close = null;
                if (near != null) { var (ng, nme) = Ready(near.Id, fanA.Id); close = Cpu.Decide(ng, data, board, nme.Id, new Random(1)); }
                bool cpu = !(far is UseItem fu && fu.Item == "バトルサーチャー") && close is UseItem cu && cu.Item == "バトルサーチャー" && cu.Target == fanA.Id;
                Check(log, $"8.3 バトルサーチャー：本マップの別のエリアのファンには使えない。本マップから 別マップ（{subFan?.MapId}）の中のファンにも使えず、その中にいる人は使える。CPUは 同じエリアに相手がなければ使わない",
                    farArea != null && farArea.Contains("同じエリア") && sub && subFan != null && cpu && near != null);
            }

            // ---- 11.5 ライバルマス ----
            {
                var rivalSq = board.Squares.Values.FirstOrDefault(s => s.Type == "ライバル" && s.Neighbors.Count > 0);
                var evolver = data.Species.FirstOrDefault(s => s.Evolutions.Any(e => e.Kind == EvolutionKind.Rival));
                bool ok = false;
                if (rivalSq != null && evolver != null)
                {
                    string target = evolver.Evolutions.First(e => e.Kind == EvolutionKind.Rival).Target;
                    // 持ち主：通信ケーブルなしで 交換進化
                    var g = Make(2); var p = g.State.CurrentPlayer; var other = g.State.Players.First(x => x != p);
                    p.Party[0] = Mon(evolver.Name, 30);
                    Open(g, FacilityKind.Rival, p.Id, rivalSq.Id);
                    var ev = Apply(g, new RivalEvolve { Player = p.Id, Slot = 0, Target = target });
                    bool owner = p.Party[0].Species == target && !ev.OfType<ItemUsed>().Any();
                    // 持ち主でない：通信ケーブルが要り、使うとなくなる
                    var ng = Make(2); var q = ng.State.CurrentPlayer; var qOwner = ng.State.Players.First(x => x != q);
                    q.Party[0] = Mon(evolver.Name, 30);
                    Open(ng, FacilityKind.Rival, qOwner.Id, rivalSq.Id);
                    string noCable = ng.Validate(new RivalEvolve { Player = q.Id, Slot = 0, Target = target });
                    q.PokemonBag.Add("通信ケーブル");
                    Apply(ng, new RivalEvolve { Player = q.Id, Slot = 0, Target = target });
                    bool cable = noCable != null && noCable.Contains("通信ケーブル") && q.Party[0].Species == target && !q.PokemonBag.Contains("通信ケーブル");
                    // 他人が持っているライバルマスでは 再戦できない（止まっても バトルにならない）
                    var lg = Make(2); var l = lg.State.CurrentPlayer; var lOwner = lg.State.Players.First(x => x != l);
                    lg.State.Owners[rivalSq.Id] = lOwner.Id;
                    var lev = Land(lg, rivalSq);
                    bool noRematch = !lev.OfType<BattleStarted>().Any() && lg.State.Owners[rivalSq.Id] == lOwner.Id;
                    ok = owner && cable && noRematch;
                }
                Check(log, "11.5 ライバルマス：持ち主は 交換進化が無料、持ち主でない人は 通信ケーブルが要って 使うとなくなる。他人のライバルマスでは 再戦できない（持ち主も変わらない）", ok);
            }
        }
    }
}
