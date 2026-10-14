using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // CPUプレイヤー（仕様書1.4）。人と同じ入口（命令）を出すだけなので、ルールには手を入れない
    // 判断は単純な決まりの積み重ね。強くしたいときは、ここの決まりを足していく
    public static class Cpu
    {
        // player が今出す命令。どれも受け付けられないときは null（そのときはバグなので画面に出す）
        public static Command Decide(GameProcessor g, GameData d, BoardData board, int player, Random r)
        {
            var s = g.State;
            var p = s.Players[player];
            Command Pick(IEnumerable<Command> candidates) => candidates.FirstOrDefault(c => c != null && g.Validate(c) == null);
            switch (s.Phase)
            {
                case Phase.Prepare: return Pick(Prepare(d, p).Concat(PrepareItems(g, d, board, p)).Append(new RollDice { Player = player }));
                case Phase.Moving: return Pick(Move(g, d, board, p, r));
                case Phase.Facility: return Pick(Facility(g, d, p).Append(new LeaveFacility { Player = player }).Append(new ChooseOption { Player = player, Index = 0 }));
                case Phase.Battle: return Pick(Battle(g, d, p, r));
                default: return null;
            }
        }

        static bool Hurt(GameData d, PokemonState m, double ratio = 0.5) => m.Fainted || m.Hp < Pokemons.MaxHp(d, m) * ratio;

        static IEnumerable<Command> Prepare(GameData d, PlayerState p)
        {
            // HPが半分を切った子に回復アイテム（1.4）
            for (int i = 0; i < p.Party.Count; i++)
                if (!p.Party[i].Fainted && Hurt(d, p.Party[i]))
                    foreach (var item in p.PokemonBag.Distinct().Where(x => ItemEffects.Of(x).use == ItemUse.Heal))
                        yield return new UseItem { Player = p.Id, Item = item, Target = i };
            // 先頭が倒れていたら、元気な子を先頭に
            int alive = p.Party.FindIndex(m => !m.Fainted);
            if (p.Party.Count > 1 && p.Party[0].Fainted && alive > 0) yield return new SwapParty { Player = p.Id, A = 0, B = alive };
        }

        // 準備フェーズの道具（回復のあと）。どれも1手番に1個まで。受け付けられないものは Pick が飛ばす
        static IEnumerable<Command> PrepareItems(GameProcessor g, GameData d, BoardData board, PlayerState p)
        {
            var s = g.State;
            bool Has(string item) => p.TrainerBag.Contains(item) || p.PokemonBag.Contains(item);
            UseItem Use(string item, int target = 0) => new UseItem { Player = p.Id, Item = item, Target = target };
            // のろわれていたら解く
            if (p.Has("のろい") && Has("おふだ")) yield return Use("おふだ");
            // 弱っていて、行ったことのあるセンターへ飛べるなら飛ぶ（いちばん近いところ）
            if (p.Party.Any(m => Hurt(d, m)) && Has("アーマーガーチケット"))
                foreach (int c in p.VisitedCenters.OrderBy(c => Movement.Distance(board, p.Square, c, p.Badges) is int n && n > 0 ? n : 999)) // 道で行けない所は後回し
                    yield return Use("アーマーガーチケット", c);
            // 妨害：同じエリアにいる人のうち、点数がいちばん高い人へ（8.3）
            foreach (var o in s.Players.Where(o => o != p).OrderByDescending(g.Score))
            {
                if (Has("スリーパーのこいん")) yield return Use("スリーパーのこいん", o.Id);
                if (Has("のろいのふだ")) yield return Use("のろいのふだ", o.Id);
                if (Has("キャッチャ")) yield return Use("キャッチャ", o.Id);
            }
            // バトルサーチャー：ほかの人のファンを1人もらう（点数が高い人のファンから）
            if (Has("バトルサーチャー"))
                foreach (var fan in s.Owners.Where(o => o.Value != p.Id).OrderByDescending(o => g.Score(s.Players[o.Value])).Select(o => o.Key))
                    yield return Use("バトルサーチャー", fan);
            // 自転車：サイコロを増やす（なくならない）
            foreach (var bike in new[] { "ロトム自転車", "マッハ自転車", "自転車" }) if (Has(bike)) yield return Use(bike);
        }

        static IEnumerable<Command> Move(GameProcessor g, GameData d, BoardData board, PlayerState p, Random r)
        {
            var move = g.State.Move;
            yield return new ConfirmMove { Player = p.Id };
            // 行き先の好み：ジム＞ライバル＞アイテム＞草むら＞空マス（1.4）。けがをしていたら回復できる所
            // さらに、次に取るジムへ近づくほど よい（ワープも道として数える）
            var toGym = DistanceToNextGym(g, d, board, p);
            int Closer(int sq) => toGym == null || !toGym.ContainsKey(sq) || !toGym.ContainsKey(move.Path[0]) ? 0 : 3 * (toGym[move.Path[0]] - toGym[sq]);
            int best = move.Reachable.OrderByDescending(sq => Want(g, d, board, p, sq) + Closer(sq)).ThenBy(_ => r.Next()).First();
            if (p.MoveMode == MoveMode.Highlight) { yield return new ChooseDestination { Player = p.Id, Square = best }; yield break; }
            // 1マスずつ方式は、行けるとなりへ1歩ずつ
            int cur = move.Path[move.Path.Count - 1];
            foreach (int n in board[cur].Neighbors.OrderBy(_ => r.Next())) yield return new StepTo { Player = p.Id, Square = n };
        }

        // 挑めるジム：まだバッジを持っていない、閉まっていない（トキワはバッジ7個から）
        static bool GymOpenFor(GameData d, PlayerState p, BoardSquare s)
        {
            string gym = d.Places.FirstOrDefault(x => x.Name == s.Place)?.Gym ?? "";
            return gym != "" && !p.Gyms.Contains(gym) && !GameProcessor.GymClosed(gym, p);
        }

        // そのジムの町で、まだだれも倒していないジムトレーナーのマス（先に倒す必要がある）
        static List<int> GymTrainersLeft(GameProcessor g, GameData d, BoardData board, BoardSquare gymSquare) =>
            d.Trainers.Where(t => t.IsGymTrainer && t.Place == gymSquare.Place)
                .Select(t => board.Squares.Values.FirstOrDefault(s => s.Name == t.Square)).Where(s => s != null && !g.State.Owners.ContainsKey(s.Id)).Select(s => s.Id).ToList();

        // 次の目標（挑めるジム。ジムトレーナーが残っていればそのマス）までの、各マスからの歩数。ワープは1歩として数える
        static Dictionary<int, int> DistanceToNextGym(GameProcessor g, GameData d, BoardData board, PlayerState p)
        {
            var gyms = board.Squares.Values.Where(s => s.Type == "ジム" && Movement.CanEnter(board, s.Id, p.Badges) && GymOpenFor(d, p, s))
                .SelectMany(s => GymTrainersLeft(g, d, board, s) is var left && left.Count > 0 ? left : new List<int> { s.Id }).Distinct().ToList();
            if (gyms.Count == 0) return null;
            var dist = gyms.ToDictionary(x => x, x => 0);
            var queue = new Queue<int>(gyms);
            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                var next = board[s].Neighbors.ToList();
                if (board[s].WarpPartner >= 0) next.Add(board[s].WarpPartner);
                foreach (int n in next)
                    if (!dist.ContainsKey(n) && Movement.CanEnter(board, n, p.Badges)) { dist[n] = dist[s] + 1; queue.Enqueue(n); }
            }
            return dist;
        }

        static int Want(GameProcessor g, GameData d, BoardData board, PlayerState p, int id)
        {
            var sq = board[id];
            bool hurt = p.Party.Any(m => Hurt(d, m));
            var rival = g.State.Players.FirstOrDefault(o => o != p && o.Square == id && o.Party.Any(m => !m.Fainted));
            if (rival != null) // 同じマスのプレイヤーとはバトルになる。勝てそうなら狙う
                return Lead(p).Level > Lead(rival).Level + 2 ? 45 : -10;
            g.State.Owners.TryGetValue(id, out int owner);
            bool owned = g.State.Owners.ContainsKey(id);
            switch (sq.Type)
            {
                case "ジム": return GymOpenFor(d, p, sq) && GymTrainersLeft(g, d, board, sq).Count == 0 ? 50 : 0;
                case "ライバル": return owned ? 3 : 40;
                case "ロケット団": return !owned ? 25 : owner == p.Id ? 5 : 1; // 拠点の買い物はまだしない
                case "ポケモンセンター": case "スタート": return hurt ? 35 : 2;
                case "トレーナー":
                {
                    if (owned) return owner == p.Id ? (hurt ? 35 : 12) : 1;
                    // レベルが10以上高いトレーナー（トキワのジムトレーナーなど）は避ける
                    var t = d.Trainers.FirstOrDefault(x => x.Square == sq.Name);
                    if (t != null && t.IsGymTrainer && GameProcessor.GymClosed(d.Places.FirstOrDefault(x => x.Name == sq.Place)?.Gym ?? "", p)) return 5; // まだ戦えない
                    if (t?.Level != null && t.Level > Lead(p).Level + 10) return -20;
                    return t != null && t.IsGymTrainer ? 40 : 20;
                }
                case "モンスターボール": return 30;
                case "イベントマス": return 25;
                case "ショップ": return p.Money >= 1000 ? 15 : 3;
                case "ポイント交換NPC": return !g.ExchangeClosed && p.Money >= 3000 ? 22 : 1;
                case "草むら": return 10;
                case "ワープ": return 8;
                default: return 5;
            }
        }

        static PokemonState Lead(PlayerState p) => p.Party.FirstOrDefault(m => !m.Fainted) ?? p.Party[0];

        static IEnumerable<Command> Facility(GameProcessor g, GameData d, PlayerState p)
        {
            var f = g.State.Facility;
            switch (f.Kind)
            {
                case FacilityKind.Shop:
                    if (p.BallCount < 3) yield return new Buy { Player = p.Id, Item = "モンスターボール" };
                    if (!p.PokemonBag.Any(x => ItemEffects.Of(x).use == ItemUse.Heal) && p.Money >= 800) yield return new Buy { Player = p.Id, Item = "キズくすり" };
                    break;
                case FacilityKind.Center:
                    // 何も持っていない子に、袋のもちものを持たせる
                    for (int i = 0; i < p.Party.Count; i++)
                        if (p.Party[i].HeldItem == "")
                            foreach (var item in p.PokemonBag.Where(x => d.FindItem(x)?.IsHeldItem == true).Distinct()) yield return new HoldItem { Player = p.Id, Slot = i, Item = item };
                    if (p.Job == "") foreach (var job in p.UnlockedJobs) yield return new ChangeJob { Player = p.Id, Job = job };
                    break;
                case FacilityKind.FanHeal:
                    if (p.Party.Any(m => Hurt(d, m, 0.6)) && p.Money >= f.Price * 2) yield return new AcceptHeal { Player = p.Id };
                    break;
                case FacilityKind.Rival:
                    for (int i = 0; i < p.Party.Count; i++)
                        foreach (var t in g.RivalTargets(p.Party[i])) yield return new RivalEvolve { Player = p.Id, Slot = i, Target = t };
                    break;
                case FacilityKind.Exchange:
                {
                    // 換金所：2,000円は手元に残して、残りをポイントに（1,000円単位）
                    int n = (p.Money - 2000) / (int)d.Tuning["yen_per_point"];
                    if (n >= 1) yield return new ExchangePoints { Player = p.Id, Points = n };
                    break;
                }
                case FacilityKind.RocketBase:
                    for (int i = 0; i < f.Goods.Count; i++)
                        if (d.RocketBases.FirstOrDefault(r => r.Id == f.Goods[i]) is RocketBaseRow r && r.Mode == "買う" && p.Money >= (r.Price ?? 0) + 3000)
                            yield return new ChooseOption { Player = p.Id, Index = i };
                    yield return new ChooseOption { Player = p.Id, Index = f.Options.Count - 1 }; // 立ち去る
                    break;
                case FacilityKind.Trade:
                case FacilityKind.BagFull:
                case FacilityKind.BoxFull:
                    yield return new ChooseOption { Player = p.Id, Index = f.Options.Count - 1 }; // 拾わない／来た子を逃がす
                    break;
                default: // イベントの「はい」、職業に就く、解放する職業は最初のもの
                    yield return new ChooseOption { Player = p.Id, Index = 0 };
                    break;
            }
        }

        static IEnumerable<Command> Battle(GameProcessor g, GameData d, PlayerState p, Random r)
        {
            var b = g.State.Battle;
            var mine = b.Sides.First(x => x.Player == p.Id);
            var other = b.Sides.First(x => x != mine);
            switch (b.Step)
            {
                case BattleStep.Select:
                {
                    // 相手の弱点を突ける子、いなければレベルが一番高い子（1.4）
                    string weak = other.Active >= 0 ? Pokemons.Of(d, other.Current).Weakness : "";
                    var order = Enumerable.Range(0, mine.Team.Count).Where(i => !mine.Team[i].Fainted)
                        .OrderByDescending(i => Pokemons.Of(d, mine.Team[i]).Type == weak).ThenByDescending(i => mine.Team[i].Level);
                    foreach (int i in order) yield return new SelectPokemon { Player = p.Id, Slot = i };
                    break;
                }
                case BattleStep.Commands:
                    if (b.Sides[b.Attacker] == mine && b.AttackCmd == null)
                    {
                        var me = mine.Current;
                        if (Hurt(d, me, 0.3))
                            foreach (var item in p.PokemonBag.Distinct().Where(x => ItemEffects.Of(x).use == ItemUse.Heal))
                                yield return new ChooseAttack { Player = p.Id, Attack = Attack.Item, Item = item };
                        if (!mine.LastWasSuper && r.Next(4) == 0) yield return new ChooseAttack { Player = p.Id, Attack = Attack.Super };
                        var better = Pokemons.Attack(d, me) >= Pokemons.SpAttack(d, me) ? Attack.Physical : Attack.Special;
                        yield return new ChooseAttack { Player = p.Id, Attack = better };
                        yield return new ChooseAttack { Player = p.Id, Attack = better == Attack.Physical ? Attack.Special : Attack.Physical }; // こだわり系で選べないとき
                    }
                    else
                    {
                        // 相手の得意な攻撃を読んで守る。ときどき必殺を読んでカウンター
                        var foe = other.Current;
                        int roll = r.Next(100);
                        bool physical = Pokemons.Attack(d, foe) >= Pokemons.SpAttack(d, foe);
                        var guess = roll < 15 ? Defense.Counter : (roll < 75) == physical ? Defense.Guard : Defense.SpecialGuard;
                        yield return new ChooseDefense { Player = p.Id, Defense = guess };
                    }
                    break;
                case BattleStep.Ball:
                {
                    var wild = other.Current;
                    if (Hurt(d, wild, 0.6))
                        foreach (var ball in new[] { "マスターボール", "ハイパーボール", "スーパーボール", "モンスターボール" })
                            yield return new ThrowBall { Player = p.Id, Ball = ball };
                    yield return new ThrowBall { Player = p.Id };
                    break;
                }
                default: // 勝利報酬：一番高い道具、なければファン、なければ許す
                {
                    var loser = g.State.Players[other.Player];
                    foreach (var item in loser.PokemonBag.Concat(loser.TrainerBag).OrderByDescending(x => d.FindItem(x)?.Price ?? 0))
                        yield return new ChooseReward { Player = p.Id, Reward = Reward.Item, Item = item };
                    yield return new ChooseReward { Player = p.Id, Reward = Reward.Fan };
                    yield return new ChooseReward { Player = p.Id, Reward = Reward.Forgive };
                    break;
                }
            }
        }
    }
}
