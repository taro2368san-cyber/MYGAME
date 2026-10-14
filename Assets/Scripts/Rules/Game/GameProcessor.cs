using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 処理役（仕様書15.2〜15.7）。命令を確かめ、状態を更新し、起きたことを返す。待ち時間や表示は持たない
    // ファイルの分け方：ここ＝手番・準備・移動・週、Landing＝止まったマスの効果と施設、Battle＝バトル
    public sealed partial class GameProcessor
    {
        public readonly GameState State;
        readonly BoardData board;
        readonly GameData data;

        GameProcessor(GameState state, BoardData board, GameData data) { State = state; this.board = board; this.data = data; }

        PlayerState Current => State.CurrentPlayer;
        double Tune(string key) => data.Required(key);
        int TuneInt(string key) => (int)Math.Round(data.Required(key));
        int Roll(int min, int max) => Rng.Range(ref State.Rng, min, max);
        bool Chance(double p) => Rng.Chance(ref State.Rng, p);

        // 新しいゲーム：所持金・ボール・最初のポケモンを配り、手番の順番をランダムに決め、全員をスタートに置く（仕様書1.1）
        // starter が空なら手持ちなしで始める（移動だけのテスト用）。characters はキャラクター.csv の id（省略できる）
        // eventRule はイベントルール.csv の id。"random" ならランダムで決める、空ならなし（2.2）
        public static GameProcessor NewGame(BoardData board, GameData data, IList<(string name, MoveMode mode, string starter)> players, int maxWeeks, ulong seed, out List<GameEvent> events, IList<string> characters = null, string eventRule = "", IList<bool> cpu = null)
        {
            var state = new GameState { MaxWeeks = maxWeeks, Rng = seed };
            state.Settings = new GameSettings { Players = players.ToList(), Characters = characters?.ToList() ?? new List<string>(), MaxWeeks = maxWeeks, Seed = seed, EventRule = eventRule ?? "", Cpu = cpu?.ToList() ?? new List<bool>() };
            var game = new GameProcessor(state, board, data);
            for (int i = 0; i < players.Count; i++)
            {
                var p = new PlayerState { Id = i, Name = players[i].name, MoveMode = players[i].mode, Square = board.StartSquare, Money = game.TuneInt("start_money"), Character = characters != null && i < characters.Count ? characters[i] : "", IsCpu = cpu != null && i < cpu.Count && cpu[i] };
                p.Balls["モンスターボール"] = game.TuneInt("start_balls");
                p.VisitedCenters.Add(board.StartSquare); // 研究室（スタート）は最初から行ったことがある
                if (!string.IsNullOrEmpty(players[i].starter))
                {
                    p.Party.Add(Pokemons.Create(data, players[i].starter, game.TuneInt("start_level"), "最初のポケモン", board[board.StartSquare].Place, 1));
                    p.Dex.Add(players[i].starter);
                }
                state.Players.Add(p);
            }
            state.TurnOrder.AddRange(Enumerable.Range(0, players.Count));
            for (int i = state.TurnOrder.Count - 1; i > 0; i--)
            {
                int j = game.Roll(0, i);
                (state.TurnOrder[i], state.TurnOrder[j]) = (state.TurnOrder[j], state.TurnOrder[i]);
            }
            state.NextEventWeek = 1 + game.Roll(game.TuneInt("weekly_event_min"), game.TuneInt("weekly_event_max"));
            events = new List<GameEvent>();
            if (eventRule == "random" && data.EventRules.Count > 0) eventRule = data.EventRules[game.Roll(0, data.EventRules.Count - 1)].Id;
            var rule = data.EventRules.FirstOrDefault(r => r.Id == eventRule);
            state.EventRule = rule?.Id ?? "";
            if (rule != null) events.Add(new EventRuleDecided { Name = rule.Name, Description = rule.Description });
            events.Add(new TurnOrderDecided { Order = state.TurnOrder.ToList() });
            game.StartTurn(events);
            return game;
        }

        // セーブを読み込む：同じ設定で始めて、記録した命令を順に流し直す。うまくいかなければ error に理由
        public static GameProcessor Load(BoardData board, GameData data, string text, out List<GameEvent> events, out string error)
        {
            events = new List<GameEvent>();
            error = null;
            try
            {
                var (settings, commands, _) = SaveFile.Read(text);
                var game = NewGame(board, data, settings.Players, settings.MaxWeeks, settings.Seed, out events, settings.Characters, settings.EventRule, settings.Cpu);
                game.DebugEnabled = true; // デバッグ命令も記録に入っているので、流し直すあいだは受け付ける
                for (int i = 0; i < commands.Count; i++)
                {
                    events = game.Apply(commands[i], out string e);
                    if (e != null) { error = $"{i + 1}番目の命令（{commands[i].GetType().Name}）が受け付けられません：{e}"; return null; }
                }
                game.DebugEnabled = false;
                return game;
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        // 今だれの入力を待っているか（ふつうは手番の人。プレイヤー戦では2人、袋がいっぱいのときは物をもらった人）
        public List<int> WaitingFor()
        {
            var s = State;
            switch (s.Phase)
            {
                case Phase.Prepare: case Phase.Moving: return new List<int> { s.CurrentPlayer.Id };
                case Phase.Facility: return new List<int> { s.Facility.Player };
                case Phase.Battle:
                    var b = s.Battle;
                    if (b.Step == BattleStep.Select) return b.Sides.Where(x => x.NeedsSelect && x.Player >= 0).Select(x => x.Player).ToList();
                    if (b.Step == BattleStep.Ball) return new List<int> { b.Sides[0].Player };
                    if (b.Step == BattleStep.Reward) return new List<int> { b.Sides[b.Winner].Player };
                    var list = new List<int>();
                    if (b.AttackCmd == null && b.Sides[b.Attacker].Player >= 0) list.Add(b.Sides[b.Attacker].Player);
                    if (b.DefenseCmd == null && b.Sides[1 - b.Attacker].Player >= 0) list.Add(b.Sides[1 - b.Attacker].Player);
                    return list;
                default: return new List<int>();
            }
        }

        // 命令を処理する。受け付けられないときは error に理由が入り、何も変わらない
        public List<GameEvent> Apply(Command c, out string error)
        {
            var events = new List<GameEvent>();
            error = Validate(c);
            if (error != null) return events;
            State.History.Add(c);
            switch (c)
            {
                case UseItem u: DoUseItem(u, events); break;
                case SwapParty s:
                    var party = Current.Party;
                    (party[s.A], party[s.B]) = (party[s.B], party[s.A]);
                    events.Add(new PartySwapped { Player = Current.Id });
                    break;
                case RollDice _: DoRoll(events); break;
                case Fish _: DoFish(events); break;
                case ChooseDestination _: case StepTo _: case StepBack _: case ConfirmMove _: DoMove(c, events); break;
                case Buy _: case Sell _: case CenterSwap _: case HoldItem _: case AcceptHeal _: case LeaveFacility _: case ChooseOption _: case ChangeJob _: case LabEvolve _: case RivalEvolve _: case RivalTrade _: case ExchangePoints _: DoFacility(c, events); break;
                case DebugCommand d: DoDebug(d, events); break;
                default: DoBattle(c, events); break;
            }
            Record(events);
            return events;
        }

        // 命令が受け付けられるか（何も変えない）。画面でボタンやマスを光らせるのに使う。だめなら理由
        public string Validate(Command c) => State.Phase == Phase.GameOver ? "ゲームは終わっています" : Check(c);

        string Check(Command c)
        {
            switch (c)
            {
                case UseItem u: return MyTurn(c, Phase.Prepare) ?? CheckUseItem(u);
                case SwapParty s:
                    return MyTurn(c, Phase.Prepare) ?? (s.A != s.B && s.A >= 0 && s.B >= 0 && s.A < Current.Party.Count && s.B < Current.Party.Count ? null : "入れ替える番号がおかしいです");
                case RollDice _: return MyTurn(c, Phase.Prepare);
                case Fish _: return MyTurn(c, Phase.Prepare) ?? CheckFish(Current);
                case ChooseDestination _: case StepTo _: case StepBack _: case ConfirmMove _: return MyTurn(c, Phase.Moving) ?? CheckMove(c);
                case Buy _: case Sell _: case CenterSwap _: case HoldItem _: case AcceptHeal _: case LeaveFacility _: case ChooseOption _: case ChangeJob _: case LabEvolve _: case RivalEvolve _: case RivalTrade _: case ExchangePoints _:
                    if (State.Phase != Phase.Facility) return $"今はできません（{PhaseName(State.Phase)}）";
                    return c.Player == State.Facility.Player ? CheckFacility(c) : "今えらぶのはあなたではありません";
                case DebugCommand d: return CheckDebug(d);
                default: return State.Phase != Phase.Battle ? "今はバトル中ではありません" : CheckBattle(c);
            }
        }

        string MyTurn(Command c, Phase phase)
        {
            if (State.Phase != phase) return $"今はできません（{PhaseName(State.Phase)}）";
            return c.Player == Current.Id ? null : "手番のプレイヤーではありません";
        }

        static string PhaseName(Phase p) =>
            p == Phase.Prepare ? "サイコロを振る前" : p == Phase.Moving ? "移動中" : p == Phase.Facility ? "施設の中" : p == Phase.Battle ? "バトル中" : "ゲーム終了";

        // ---- 準備フェーズ（5.2）----

        string CheckUseItem(UseItem u)
        {
            var p = Current;
            if (p.UsedItem) return "準備フェーズで使える道具は1個までです";
            if (!p.PokemonBag.Contains(u.Item) && !p.TrainerBag.Contains(u.Item)) return "その道具を持っていません";
            if (IsEvolutionItem(u.Item))
            {
                if (u.Target < 0 || u.Target >= p.Party.Count) return "使うポケモンを選んでください";
                return ItemEvolution(p.Party[u.Target], u.Item) != null ? null : "そのポケモンは この道具では 進化しません";
            }
            var (use, value, _) = ItemEffects.Of(u.Item);
            if (use == ItemUse.None) return "その道具はまだ使えません";
            if (!ItemEffects.TargetsPokemon(use)) return use == ItemUse.FixedDice || use == ItemUse.DiceCount ? null : CheckTrainerItem(p, use, value, u.Target);
            if (u.Target < 0 || u.Target >= p.Party.Count) return "使うポケモンを選んでください";
            return CanUseOn(use, value, p.Party[u.Target]);
        }

        string CanUseOn(ItemUse use, double value, PokemonState target)
        {
            if (use == ItemUse.Revive) return target.Fainted ? null : "戦闘不能のポケモンにだけ使えます";
            if (target.Fainted) return "戦闘不能のポケモンには使えません";
            if (use == ItemUse.Heal) return target.Hp < Pokemons.MaxHp(data, target) ? null : "HPは満タンです";
            if (use == ItemUse.Cure) return target.Status != Status.None && (value == 0 || (int)value == (int)target.Status) ? null : "治す状態異常がありません";
            return null;
        }

        bool IsEvolutionItem(string item) => data.FindItem(item)?.Category == "進化アイテム";
        Evolution ItemEvolution(PokemonState m, string item) => Pokemons.Of(data, m).Evolutions.FirstOrDefault(e => e.Kind == EvolutionKind.Item && e.Item == item);

        // ポケモンを選んで使う道具か（画面で「だれに使う？」を出すため）
        public TargetKind TargetOf(string item) => IsEvolutionItem(item) ? TargetKind.Pokemon : ItemEffects.TargetOf(ItemEffects.Of(item).use);

        void DoUseItem(UseItem u, List<GameEvent> events)
        {
            var p = Current;
            if (IsEvolutionItem(u.Item))
            {
                // アイテム進化（6.5）：その場で1段階進化する。道具はなくなる
                var m = p.Party[u.Target];
                string from = m.Species, to = ItemEvolution(m, u.Item).Target;
                Pokemons.EvolveTo(data, m, to);
                p.Dex.Add(to);
                RemoveItem(p, u.Item);
                p.UsedItem = true;
                events.Add(new ItemUsed { Player = p.Id, Item = u.Item, Pokemon = from });
                events.Add(new Evolved { Player = p.Id, From = from, To = to });
                return;
            }
            var (use, value, consumed) = ItemEffects.Of(u.Item);
            string target = null;
            if (ItemEffects.TargetsPokemon(use)) { UseOn(use, value, p.Party[u.Target]); target = p.Party[u.Target].Species; }
            else if (use == ItemUse.FixedDice) p.FixedDice = (int)value;
            else if (use == ItemUse.DiceCount) { p.DiceCount = (int)value; p.BikeUses++; CheckCyclingUnlock(p, events); }
            else DoTrainerItem(p, use, value, u.Target, events);
            if (consumed) RemoveItem(p, u.Item);
            p.UsedItem = true;
            events.Insert(0, new ItemUsed { Player = p.Id, Item = u.Item, Pokemon = target }); // 道具を使った → その結果、の順
        }

        void UseOn(ItemUse use, double value, PokemonState target)
        {
            int max = Pokemons.MaxHp(data, target);
            if (use == ItemUse.Heal) target.Hp = Math.Min(max, target.Hp + Math.Max(1, (int)(max * value)));
            else if (use == ItemUse.Revive) target.Hp = Math.Max(1, (int)(max * value));
            else if (use == ItemUse.Cure) target.Status = Status.None;
        }

        // ---- 移動（4章）----

        void DoRoll(List<GameEvent> events)
        {
            var player = Current;
            var dice = new List<int>();
            if (player.FixedDice >= 0) dice.Add(player.FixedDice);
            else
            {
                // ダート自転車の期間中は、ほかの自転車を使っていなければ ルーレットが2〜4個
                int count = player.DiceCount == 1 && player.Has("ダート自転車") ? Roll(TuneInt("dart_bike_min"), TuneInt("dart_bike_max")) : player.DiceCount;
                for (int i = 0; i < count; i++) dice.Add(Roll(TuneInt("dice_min"), TuneInt("dice_max")));
            }
            int total = dice.Sum();
            BeginMove(total, events);
            events.Insert(events.Count - 1, new DiceRolled { Player = player.Id, Value = total, Dice = dice, AllowReverse = State.Move.AllowReverse, Reachable = State.Move.Reachable.ToList() });
            if (State.Move.Reachable.Count == 0) { events.Add(new NothingHappened { Player = player.Id, Reason = "止まれるマスがない" }); EndTurn(events); }
        }

        // steps 歩の移動を始める（サイコロと「追加で進む」で共通）。最後に PathChanged を1つ足す
        void BeginMove(int steps, List<GameEvent> events)
        {
            var player = Current;
            var move = State.Move = new MoveState { Dice = steps, Extra = JobExtraSteps(player) };
            move.Path.Add(player.Square);
            // サイクリングは 目〜目＋Extra 歩のどこで止まってもよい
            HashSet<int> Reach(bool reverse) { var r = new HashSet<int>(); for (int k = 0; k <= move.Extra; k++) r.UnionWith(Movement.Reachable(board, player.Square, steps + k, player.Badges, reverse, State.Blocked.Keys)); return r; }
            move.Reachable = Reach(false);
            if (move.Reachable.Count == 0)
            {
                // 止まれるマスがないときだけ、折り返しも歩数に数える（仕様書4.1）
                move.AllowReverse = true;
                move.Reachable = Reach(true);
            }
            State.Phase = Phase.Moving;
            events.Add(new PathChanged { Player = player.Id, Path = move.Path.ToList() });
        }

        string CheckMove(Command c)
        {
            var player = Current;
            var move = State.Move;
            int cur = move.Path[move.Path.Count - 1];
            int left = move.Dice + move.Extra - (move.Path.Count - 1);
            switch (c)
            {
                case ChooseDestination choose:
                    if (player.MoveMode != MoveMode.Highlight) return "1マスずつ方式のプレイヤーは行き先を一度に選べません";
                    return move.Reachable.Contains(choose.Square) ? null : "そのマスには止まれません";
                case StepTo step:
                    if (player.MoveMode != MoveMode.Step) return "ハイライト方式のプレイヤーは1歩ずつ進めません";
                    if (IsUndoStep(step.Square)) return null;
                    if (left <= 0) return "もう歩数が残っていません";
                    if (!board[cur].Neighbors.Contains(step.Square)) return "となりのマスではありません";
                    if (!Movement.CanEnter(board, step.Square, player.Badges)) return "まだ入れないエリアです";
                    if (State.Blocked.ContainsKey(step.Square)) return "カビゴンが ねていて 通れない";
                    return Movement.CanFinish(board, cur, step.Square, left - 1, player.Badges, move.AllowReverse, move.Reachable, State.Blocked.Keys, move.Extra) ? null : "その道では、ちょうど止まれるマスがありません";
                case StepBack _:
                    if (player.MoveMode != MoveMode.Step) return "ハイライト方式では1歩戻しはありません";
                    return move.Path.Count > 1 ? null : "出発したマスより前には戻れません";
                default: // ConfirmMove
                    int taken = move.Path.Count - 1;
                    return taken >= move.Dice && taken <= move.Dice + move.Extra && move.Reachable.Contains(cur) ? null : "まだ行き先が決まっていません";
            }
        }

        void DoMove(Command c, List<GameEvent> events)
        {
            var player = Current;
            var move = State.Move;
            switch (c)
            {
                case ChooseDestination choose:
                    for (int k = 0; k <= move.Extra; k++) // 少ない歩数で行けるなら、その道
                        if (Movement.FindPath(board, move.Path[0], move.Dice + k, player.Badges, move.AllowReverse, choose.Square, State.Blocked.Keys) is List<int> path) { move.Path = path; break; }
                    break;
                case StepTo step:
                    if (IsUndoStep(step.Square)) move.Path.RemoveAt(move.Path.Count - 1);
                    else move.Path.Add(step.Square);
                    break;
                case StepBack _:
                    move.Path.RemoveAt(move.Path.Count - 1);
                    break;
                default: // ConfirmMove
                    int to = move.Path[move.Path.Count - 1];
                    player.Square = to;
                    events.Add(new Moved { Player = player.Id, Path = move.Path.ToList() });
                    State.Move = null;
                    // ワープは止まったときだけ。着いた先ではもう一度ワープしない
                    int partner = board[to].WarpPartner;
                    if (partner >= 0 && Movement.CanEnter(board, partner, player.Badges))
                    {
                        if (board[to].MapId == "") player.LastMainWarp = to;
                        player.Square = partner;
                        events.Add(new Warped { Player = player.Id, From = to, To = partner });
                    }
                    events.Add(new Landed { Player = player.Id, Square = player.Square });
                    if (!Land(events)) EndTurn(events);
                    return;
            }
            events.Add(new PathChanged { Player = player.Id, Path = move.Path.ToList() });
        }

        // 直前のマスへの1歩は「考え直し」（折り返しを歩数に数える特別な移動のときは、ふつうの1歩）
        bool IsUndoStep(int square)
        {
            var path = State.Move.Path;
            return !State.Move.AllowReverse && path.Count >= 2 && path[path.Count - 2] == square;
        }

        // ---- 手番と週（5章）----

        void StartTurn(List<GameEvent> events)
        {
            var player = Current;
            player.UsedItem = false; player.DiceCount = 1; player.FixedDice = -1;
            events.Add(new TurnStarted { Player = player.Id });
            // プレイヤーの状態は、自分のターンが来るたびに残り週数を1減らす（休んだ週も数える、10章）
            string rest = player.Has("さいみん") ? "さいみん" : player.Has("1回休み") ? "1回休み" : null; // さいみんも1回休み（7.17）
            foreach (var a in player.Ailments) a.Weeks--;
            player.Ailments.RemoveAll(a => a.Weeks <= 0);
            if (rest != null)
            {
                events.Add(new TurnSkipped { Player = player.Id, Reason = rest });
                EndTurn(events);
                return;
            }
            if (player.Party.Count > 0) player.Party[0].LeadTurns++;
        }

        // 手番を終えて次の人へ。全員が1回ずつ動いたら1週進む（仕様書5.1）
        void EndTurn(List<GameEvent> events)
        {
            State.Move = null; State.Facility = null; State.Battle = null;
            State.Phase = Phase.Prepare;
            State.TurnIndex++;
            if (State.TurnIndex == State.TurnOrder.Count)
            {
                State.TurnIndex = 0;
                if (!StartWeek(events)) return;
            }
            StartTurn(events);
        }

        // 週の始まり（5.2、5.3）。ゲームが終わったら false
        bool StartWeek(List<GameEvent> events)
        {
            State.Week++;
            foreach (var p in State.Players) foreach (var m in p.Party) m.PartyWeeks++; // 手持ちにいた週数
            if (State.Week > State.MaxWeeks)
            {
                State.Phase = Phase.GameOver;
                events.Add(new GameEnded { Ranking = State.Players.Select(p => (p.Id, FinalScore(p))).OrderByDescending(x => x.Item2).ToList() });
                return false;
            }
            events.Add(new WeekAdvanced { Week = State.Week });

            // 何週連続で最下位か（8.8.1 の救済に使う。同点で最下位なら全員数える）
            if (State.Players.Count > 1)
            {
                double worst = State.Players.Min(p => Score(p));
                foreach (var p in State.Players) p.LastPlaceWeeks = Score(p) <= worst + 1e-9 ? p.LastPlaceWeeks + 1 : 0;
                foreach (var p in State.Players) CheckRocketScout(p, events);
            }

            if (State.WeatherWeeks > 0 && --State.WeatherWeeks == 0) { events.Add(new WeekEventEnded { Name = State.Weather }); State.Weather = ""; }
            foreach (int sq in State.Blocked.Keys.ToList()) // カビゴンは週がたつと去る
                if (--State.Blocked[sq] <= 0) { State.Blocked.Remove(sq); events.Add(new SnorlaxRemoved { Square = sq, Player = -1 }); }
            if (State.FluteWeeks > 0 && --State.FluteWeeks == 0) { events.Add(new WeekEventEnded { Name = "ふえの天候（" + State.FluteWeather + "）" }); State.FluteWeather = ""; }
            if (State.WeekEventWeeks > 0 && --State.WeekEventWeeks == 0) { events.Add(new WeekEventEnded { Name = State.WeekEvent }); State.WeekEvent = ""; }

            // 配当（年貢）：7週ごとに、持っているトレーナーマスの分
            if ((State.Week - 1) % TuneInt("dividend_interval") == 0)
                foreach (var p in State.Players)
                {
                    int amount = OwnedSquares(p.Id, "トレーナー").Concat(OwnedSquares(p.Id, "ロケット団")).Sum(s => TuneInt($"tribute_trainer_area{AreaNumber(s)}")); // ロケット団マスもトレーナーマスと同じ額（仮）
                    if (amount <= 0) continue;
                    p.Money += amount;
                    events.Add(new Dividend { Player = p.Id, Amount = amount });
                }

            // 週イベント：3〜8週おきに抽選。合計確率の残りは何も起きない
            if (State.Week >= State.NextEventWeek)
            {
                State.NextEventWeek = State.Week + Roll(TuneInt("weekly_event_min"), TuneInt("weekly_event_max"));
                double r = Rng.Next(ref State.Rng), sum = 0;
                var picked = data.WeekEvents.FirstOrDefault(e => r < (sum += e.Probability));
                int weeks = TuneInt("event_weeks");
                // ponytail: 天候・セール・ストライキだけ。突発バトル・ポケニュースは引いても何も起きない（作るときにここへ足す）
                if (picked != null && (picked.Name == "雨季" || picked.Name == "乾季" || picked.Name == "すなあらしの日"))
                { State.Weather = picked.Name; State.WeatherWeeks = weeks; events.Add(new WeekEventHappened { Name = picked.Name, Weeks = weeks }); }
                else if (picked != null && (picked.Name == "セール" || picked.Name == "ストライキ"))
                { State.WeekEvent = picked.Name; State.WeekEventWeeks = weeks; events.Add(new WeekEventHappened { Name = picked.Name, Weeks = weeks }); }
            }
            return true;
        }

        // 順位用ポイント（12章）＝所持ポイント＋バッジ＋（所持金＋トレーナーマスの換算額）÷1000
        // 順位用ポイント（12章）：所持ポイント＋（所持金＋トレーナーマス・ロケット団マスの換算額）÷1,000。最下位の救済や途中の順位に使う
        public double Score(PlayerState p) =>
            p.Points + p.Badges * Tune("badge_point")
            + (p.Money + OwnedSquares(p.Id, "トレーナー").Sum(s => Tune($"trainer_value_area{AreaNumber(s)}"))
                       + OwnedSquares(p.Id, "ロケット団").Sum(s => Tune($"rocket_value_area{AreaNumber(s)}"))) / Tune("yen_per_point");

        // 結果発表の内訳（16.1 #11）
        public (double points, double badges, double rule, double rocket) ScoreBreakdown(PlayerState p) =>
            (p.Points, p.Badges * Tune("badge_point"), EventRulePoints(p), RocketAward(p));

        // 最終的な勝利ポイント（2章）＝所持ポイント（バッジ・換金所で換えたポイント）＋イベントルール＋ロケット団最多討伐賞
        // 換えていないお金とファンは入らない（お金は換金所でポイントにする。最後の10週は閉まる）
        public double FinalScore(PlayerState p) => p.Points + p.Badges * Tune("badge_point") + EventRulePoints(p) + RocketAward(p);

        public double EventRulePoints(PlayerState p)
        {
            var rule = data.EventRules.FirstOrDefault(r => r.Id == State.EventRule);
            if (rule == null) return 0;
            double Count(PlayerState q) =>
                rule.Target == "図鑑の登録数" ? q.Dex.Count : rule.Target == "捕まえた種類の数" ? q.CaughtSpecies.Count : rule.Target == "バッジの数" ? q.Badges : 0;
            if (rule.Mode == "1位だけ") return Count(p) > 0 && Count(p) >= State.Players.Max(Count) ? rule.Coef : 0;
            return Count(p) * rule.Coef;
        }

        IEnumerable<BoardSquare> OwnedSquares(int player, string type) =>
            State.Owners.Where(o => o.Value == player).Select(o => board[o.Key]).Where(s => s.Type == type);

        static int AreaNumber(BoardSquare s) => int.TryParse(new string((s.Area ?? "").Where(char.IsDigit).ToArray()), out int n) ? n : 1;

        // ---- お金・道具・回復（あちこちで使う）----

        void AddMoney(PlayerState p, int amount, string reason, List<GameEvent> events, PlayerState to = null)
        {
            if (amount < 0) amount = -Math.Min(p.Money, -amount);
            if (amount == 0) return;
            p.Money += amount;
            if (to != null) to.Money -= amount;
            events.Add(new MoneyChanged { Player = p.Id, Amount = amount, Reason = reason, To = to?.Id ?? -1 });
        }

        bool HasRoomFor(PlayerState p, string item)
        {
            var info = data.FindItem(item);
            var bag = ItemEffects.BagOf(info);
            return bag == Bag.Ball || (bag == Bag.Pokemon ? p.PokemonBag : p.TrainerBag).Count < BagLimit(p, bag);
        }

        // 道具を袋に入れる。いっぱいなら、何を捨てるか（拾わないか）を選んでもらう（8.8.1）。選択を待つなら true
        bool GiveItem(PlayerState p, string item, List<GameEvent> events)
        {
            if (!HasRoomFor(p, item))
            {
                var full = ItemEffects.BagOf(data.FindItem(item)) == Bag.Pokemon ? p.PokemonBag : p.TrainerBag;
                events.Add(new ItemGained { Player = p.Id, Item = item, BagFull = true });
                var f = new FacilityState { Kind = FacilityKind.BagFull, Player = p.Id, Square = p.Square, PendingItem = item, Prompt = $"袋がいっぱい。{item}を もつなら、どれを すてる？" };
                f.Options.AddRange(full);
                f.Options.Add("拾わない");
                return OpenFacility(f, events);
            }
            PutItem(p, item);
            events.Add(new ItemGained { Player = p.Id, Item = item });
            return false;
        }

        void PutItem(PlayerState p, string item)
        {
            var bag = ItemEffects.BagOf(data.FindItem(item));
            if (bag == Bag.Ball) p.Balls[item] = (p.Balls.TryGetValue(item, out int n) ? n : 0) + 1;
            else (bag == Bag.Pokemon ? p.PokemonBag : p.TrainerBag).Add(item);
        }

        // 文言CSVのセリフ（キーがなければ何も出さない）。表示は Narrator が行にする
        void Say(string key, List<GameEvent> events, PlayerState p = null, params (string name, string value)[] values)
        {
            if (string.IsNullOrEmpty(key) || !data.HasDialog(key)) return;
            var dict = new Dictionary<string, string>();
            foreach (var (name, value) in values) dict[name] = value;
            events.Add(new Dialog { Player = (p ?? Current).Id, Key = key, Values = dict });
        }

        static void RemoveItem(PlayerState p, string item)
        {
            if (!p.PokemonBag.Remove(item)) p.TrainerBag.Remove(item);
        }

        void HealParty(PlayerState p, List<GameEvent> events)
        {
            foreach (var m in p.Party.Concat(p.Box)) Pokemons.HealFull(data, m);
            events.Add(new PartyHealed { Player = p.Id });
        }

        // 重みつきの抽選
        T Pick<T>(IList<T> list, Func<T, int> weight)
        {
            int total = list.Sum(weight);
            if (total <= 0) return default;
            int r = Roll(1, total);
            foreach (var x in list) if ((r -= weight(x)) <= 0) return x;
            return default;
        }
    }
}
