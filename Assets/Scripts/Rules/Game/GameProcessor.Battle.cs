using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // バトル（仕様書7章）。流れ：選出 → 先攻・後攻のカード → (攻撃側・防御側のコマンド → 結果 → 攻守交代) をくり返す
    // 特性は GameProcessor.Ability.cs。ponytail: もちもの・ロケット団は未実装。足すときはダメージ計算（Hit）に倍率を足す
    public sealed partial class GameProcessor
    {
        BattleState B => State.Battle;

        // ---- 始める ----

        bool StartWild(BoardSquare sq, List<GameEvent> events, string method = "草むら")
        {
            if (sq == null) return false;
            if (NewsLegend(sq, method) is var legend && legend != null) // ポケニュース特番の準伝説（5.3）
            {
                events.Add(new NewsLegendAppeared { Pokemon = legend.Value.species });
                return StartBattle(BattleKind.Wild, false, Npc($"野生の{legend.Value.species}", new[] { legend.Value }), events);
            }
            var rows = data.Encounters.Where(e => e.Place == sq.Place && e.Method == method).ToList();
            var row = Pick(rows, e => NewsWeight(e, rows)); // ポケニュースなら レア枠が出やすい
            if (row == null) return false;
            int level = Roll(row.MinLevel, row.MaxLevel);
            if (Current.Has("アマいおこう")) level = Math.Min(level + TuneInt("sweet_scent_level"), TuneInt("level_max")); // 少し高いレベルで出る
            return StartBattle(BattleKind.Wild, false, Npc($"野生の{row.Pokemon}", new[] { (row.Pokemon, level) }), events);
        }

        // 同じマスに止まったプレイヤーどうし（7.6）。クイックバトル、両方が3体から選ぶ
        bool StartPlayerBattle(PlayerState other, List<GameEvent> events) =>
            StartBattle(BattleKind.Player, false, new BattleSide { Player = other.Id, Name = other.Name, Team = other.Party }, events);

        // first：仕掛けた側のプレイヤー（省略なら手番の人。トーナメントでは試合の1人目）
        bool StartBattle(BattleKind kind, bool full, BattleSide opponent, List<GameEvent> events, string gym = null, int first = -1)
        {
            var p = first >= 0 ? State.Players[first] : Current;
            var b = State.Battle = new BattleState { Kind = kind, Full = full, Square = p.Square, Gym = gym };
            b.Sides[0] = new BattleSide { Player = p.Id, Name = p.Name, Team = p.Party };
            b.Sides[1] = opponent;
            State.Phase = Phase.Battle;
            events.Add(new BattleStarted { Kind = kind, Full = full, Opponent = opponent.Name, Players = b.Sides.Select(s => s.Player).ToArray() });
            b.Weather = State.FluteWeeks > 0 ? State.FluteWeather : WeatherOfWeek(State.Weather);
            if (b.Weather != "") events.Add(new BattleWeatherChanged { Weather = b.Weather });
            // 選出があるのはフルバトルとプレイヤー戦（7.1.1）。先頭固定のバトルでも、先頭が戦闘不能なら選ぶ
            bool choose = full || kind == BattleKind.Player || kind == BattleKind.Tournament;
            for (int i = 0; i < 2; i++) PrepareSide(i, choose, events);
            AfterSelect(events);
            return true;
        }

        // exclude：交代させられた子（脱出ボタン）。その子は選べない
        void PrepareSide(int i, bool choose, List<GameEvent> events, int exclude = -1)
        {
            var s = B.Sides[i];
            ResetSide(s);
            s.SwitchFrom = exclude;
            if (s.Player < 0) { s.Active = Enumerable.Range(0, s.Team.Count).First(k => k != exclude && !s.Team[k].Fainted); return; }
            if (!choose && !s.Team[0].Fainted) { s.Active = 0; return; }
            s.Active = -1;
            s.NeedsSelect = true;
            events.Add(new SelectRequested { Side = i, Player = s.Player });
        }

        // 新しい子が場に出るときに戻すもの：能力ランク（7.15）、出たときの特性、ばけのかわ、かるわざ、オボンのみ
        static void ResetSide(BattleSide s)
        {
            s.Ranks = new int[3];
            s.Entered = true; s.DisguiseUsed = false; s.LostItem = false; s.BerryUsed = false;
        }

        // 脱出ボタン（自分で選んで交代）・レッドカード（相手をランダムに交代）。交代したらカードを引き直す（7.1.2）
        void SwitchOut(int side, bool random, List<GameEvent> events)
        {
            var s = B.Sides[side];
            int from = s.Active;
            events.Add(new SwitchedOut { Side = side, Pokemon = s.Current.Species });
            if (random)
            {
                var options = Enumerable.Range(0, s.Team.Count).Where(k => k != from && !s.Team[k].Fainted).ToList();
                ResetSide(s);
                s.Active = options[Roll(0, options.Count - 1)];
            }
            else PrepareSide(side, true, events, from);
            AfterSelect(events);
        }

        // 全員が選び終えたら、まとめて場に出して（相手の選択を先に見せないため）カードを引く
        void AfterSelect(List<GameEvent> events)
        {
            if (B.Sides.Any(s => s.NeedsSelect)) { B.Step = BattleStep.Select; return; }
            for (int i = 0; i < 2; i++)
            {
                var s = B.Sides[i];
                var m = s.Current;
                if (s.Player >= 0) s.Participated.Add(s.Active);
                var sp = Pokemons.Of(data, m);
                events.Add(new SentOut { Side = i, Pokemon = m.Species, Level = m.Level, Hp = m.Hp, MaxHp = Pokemons.MaxHp(data, m), Type = sp.Type, Weakness = sp.Weakness, Status = m.Status, Exp = m.Exp });
            }
            for (int i = 0; i < 2; i++)
                if (Transforms(B.Sides[i])) events.Add(new Transformed { Side = i, Pokemon = B.Sides[i].Current.Species, Into = B.Sides[1 - i].Current.Species });
            OnEnter(events);
            if (DrawInitiative(events)) BeginCommands(events);
        }

        // 先攻・後攻のカード（7.1.2）。すばやさの比で、速い側の先攻カードが1〜3枚になる。表で見せて混ぜたあと、仕掛けた側が1枚選ぶ（PickCard）
        // へんしん・せんせいのツメで すぐ決まったら true（選ぶのを待つなら false）
        bool DrawInitiative(List<GameEvent> events)
        {
            if (Transforms(B.Sides[0]) != Transforms(B.Sides[1]))
            {
                B.First = B.Attacker = Transforms(B.Sides[0]) ? 0 : 1;
                events.Add(new InitiativeDrawn { Cards = new List<int> { B.Attacker }, Picked = 0, First = B.Attacker, FirstPokemon = B.Sides[B.Attacker].Current.Species });
                return true;
            }
            int claw = HeldFirst(events); // せんせいのツメ
            if (claw >= 0)
            {
                B.First = B.Attacker = claw;
                events.Add(new InitiativeDrawn { Cards = new List<int> { claw }, Picked = 0, First = claw, FirstPokemon = B.Sides[claw].Current.Species });
                return true;
            }
            int s0 = BattleSpeed(B.Sides[0]), s1 = BattleSpeed(B.Sides[1]);
            int fast = s0 >= s1 ? 0 : 1;
            double ratio = (double)Math.Max(s0, s1) / Math.Max(1, Math.Min(s0, s1));
            int fastCards = ratio >= Tune("initiative_ratio_large") ? 3 : ratio >= Tune("initiative_ratio_small") ? 2 : 1;
            // こうこうのしっぽ：持っている側（片方だけのとき）を、大きく遅い側として扱う
            bool tail0 = HeldOf(B.Sides[0]) == "こうこうのしっぽ", tail1 = HeldOf(B.Sides[1]) == "こうこうのしっぽ";
            if (tail0 != tail1) { fast = tail0 ? 1 : 0; fastCards = 3; }
            var cards = Enumerable.Repeat(fast, fastCards).Append(1 - fast).ToList();
            for (int i = cards.Count - 1; i > 0; i--) { int j = Roll(0, i); (cards[i], cards[j]) = (cards[j], cards[i]); }
            B.Cards = cards;
            B.Step = BattleStep.Initiative;
            events.Add(new CardsDealt { Cards = cards, Player = B.Sides[0].Player });
            return false;
        }

        // 選んだカードをめくる。そのカードの側が先攻
        void PickInitiative(int picked, List<GameEvent> events)
        {
            var cards = B.Cards;
            B.Cards = null;
            B.First = B.Attacker = cards[picked];
            events.Add(new InitiativeDrawn { Cards = cards, Picked = picked, First = B.Attacker, FirstPokemon = B.Sides[B.Attacker].Current.Species });
            BeginCommands(events);
        }

        // 前の版のセーブ（カードを選ぶ命令がない）を読むとき：前と同じく乱数で1枚引く。命令の記録には残さない
        internal void AutoPickCard()
        {
            var events = new List<GameEvent>();
            PickInitiative(Roll(0, B.Cards.Count - 1), events);
            Record(events);
        }

        // 攻撃側・防御側のコマンドを待つ。NPC はここで決めてしまう（見せない）
        void BeginCommands(List<GameEvent> events)
        {
            var b = B;
            // 攻守が1往復したら（野生はボールのあと）、すごろくの番を終える。続きは次の自分の番。プレイヤー戦・トーナメントは最後まで
            if (b.TurnRounds >= 2 && b.IsNpc) { SuspendBattle(events); return; }
            b.Step = BattleStep.Commands;
            b.AttackCmd = null; b.AttackItem = null; b.DefenseCmd = null;
            var a = b.Sides[b.Attacker];
            var d = b.Sides[1 - b.Attacker];
            // ponytail: NPC はランダムに選ぶ。強くするなら CPU（1.4）と同じ考え方でここを差し替える
            if (a.Player < 0) b.AttackCmd = b.Kind == BattleKind.Wild ? WildAttack(a) : (Attack)Roll(0, a.LastWasSuper || Transforms(a) ? 1 : 2);
            if (d.Player < 0) b.DefenseCmd = (Defense)Roll(0, 2);
            events.Add(new CommandsRequested { Attacker = b.Attacker, AttackPlayer = a.Player, DefensePlayer = d.Player, SuperLocked = a.LastWasSuper || Transforms(a) });
        }

        // 野生の攻撃：必殺技は 調整値 wild_super_rate の確率（必殺が使えないときは出さない）。のこりは 物理・特殊を半分ずつ
        Attack WildAttack(BattleSide a)
        {
            double x = Rng.Next(ref State.Rng);
            double super = a.LastWasSuper || Transforms(a) ? 0 : Tune("wild_super_rate");
            return x < super ? Attack.Super : x < super + (1 - super) / 2 ? Attack.Physical : Attack.Special;
        }

        // ---- 命令の確認 ----

        string CheckBattle(Command c)
        {
            var b = B;
            switch (c)
            {
                case SelectPokemon s:
                {
                    if (b.Step != BattleStep.Select) return "今はポケモンを選ぶときではありません";
                    var side = b.Sides.FirstOrDefault(x => x.Player == c.Player && x.NeedsSelect);
                    if (side == null) return "ポケモンを選ぶのはあなたではありません";
                    if (s.Slot == side.SwitchFrom) return "交代するので、ほかの子を選んでください";
                    return s.Slot >= 0 && s.Slot < side.Team.Count && !side.Team[s.Slot].Fainted ? null : "そのポケモンは出せません";
                }
                case ChooseAttack a:
                {
                    if (b.Step != BattleStep.Commands) return "今はコマンドを選ぶときではありません";
                    var side = b.Sides[b.Attacker];
                    if (side.Player != c.Player) return "攻撃のコマンドを選ぶのはあなたではありません";
                    if (b.AttackCmd != null) return "もう選んでいます";
                    if (a.Attack == Attack.Super && side.LastWasSuper) return "必殺技は続けて打てません";
                    if (a.Attack == Attack.Super && Transforms(side)) return "へんしん中は 必殺技を 選べません";
                    if (HeldForbids(side, a.Attack) is string forbid) return forbid;
                    if (a.Attack != Attack.Item) return null;
                    var p = State.Players[c.Player];
                    if (!p.PokemonBag.Contains(a.Item)) return "その道具を持っていません";
                    var (use, value, _) = ItemEffects.Of(a.Item);
                    return use == ItemUse.Heal || use == ItemUse.Cure ? CanUseOn(use, value, side.Current) : "バトル中に使えない道具です";
                }
                case ChooseDefense d:
                {
                    if (b.Step != BattleStep.Commands) return "今はコマンドを選ぶときではありません";
                    if (b.Sides[1 - b.Attacker].Player != c.Player) return "防御のコマンドを選ぶのはあなたではありません";
                    if (b.DefenseCmd != null) return "もう選んでいます";
                    return d.Defense == Defense.Flee && b.Kind == BattleKind.Gym ? "ジム戦からは にげられません" : null;
                }
                case PickCard pc:
                    if (b.Step != BattleStep.Initiative) return "今はカードを選ぶときではありません";
                    if (b.Sides[0].Player != c.Player) return "カードを選ぶのはあなたではありません";
                    return pc.Card >= 0 && pc.Card < b.Cards.Count ? null : "そのカードはありません";
                case ThrowBall t:
                {
                    if (b.Step != BattleStep.Ball) return "今はボールを投げられません";
                    if (b.Sides[0].Player != c.Player) return "ボールを投げるのはあなたではありません";
                    if (t.Ball == null) return null;
                    var p = State.Players[c.Player];
                    if (!p.Balls.TryGetValue(t.Ball, out int n) || n <= 0) return "そのボールを持っていません";
                    return null;
                }
                case ChooseReward r:
                {
                    if (b.Step != BattleStep.Reward) return "今は報酬を選ぶときではありません";
                    if (b.Sides[b.Winner].Player != c.Player) return "報酬を選ぶのはあなたではありません";
                    var loser = State.Players[b.Sides[1 - b.Winner].Player];
                    if (r.Reward == Reward.Item) return loser.PokemonBag.Contains(r.Item) || loser.TrainerBag.Contains(r.Item) ? null : "相手はその道具を持っていません";
                    if (r.Reward == Reward.Fan) return OwnedSquares(loser.Id, "トレーナー").Any() ? null : "相手はファンを持っていません";
                    if (r.Reward == Reward.Trade)
                    {
                        if (b.Surrendered) return "降参した相手とは ポケモンを交換できません";
                        if (loser.Party.Count <= 1) return "相手の ポケモンが 1体だけなので 交換できません";
                        var winner = State.Players[c.Player];
                        var mine = r.GiveFromBox ? winner.Box : winner.Party;
                        if (r.Take < 0 || r.Take >= loser.Party.Count || r.Give < 0 || r.Give >= mine.Count) return "交換するポケモンを選んでください";
                        // 同等以上のコストの子を差し出す（7.7）。勝者は控えの子も出せる、敗者は手持ちだけ
                        if (JobFreeTrade(winner)) return null; // ロケット団：コストに関係なく奪える（9.4）
                        return Pokemons.Cost10(data, mine[r.Give]) >= Pokemons.Cost10(data, loser.Party[r.Take]) ? null : "コストが足りません（同じか高いコストの子を出してください）";
                    }
                    return null;
                }
            }
            return "知らない命令です";
        }


        // ---- 命令の処理 ----

        void DoBattle(Command c, List<GameEvent> events)
        {
            var b = B;
            switch (c)
            {
                case SelectPokemon s:
                    var side = b.Sides.First(x => x.Player == c.Player && x.NeedsSelect);
                    side.Active = s.Slot;
                    side.NeedsSelect = false;
                    AfterSelect(events);
                    return;
                case ChooseAttack a:
                    b.AttackCmd = a.Attack; b.AttackItem = a.Item;
                    break;
                case ChooseDefense d:
                    b.DefenseCmd = d.Defense;
                    break;
                case PickCard pc:
                    PickInitiative(pc.Card, events);
                    return;
                case ThrowBall t:
                    if (t.Ball == null) BeginCommands(events);
                    else Throw(t.Ball, events);
                    return;
                case ChooseReward r:
                    bool wait = GiveReward(r, events);
                    State.Battle = null;
                    if (!wait) EndTurn(events);
                    return;
            }
            if (b.AttackCmd != null && b.DefenseCmd != null) ResolveRound(events);
        }

        // 1回の攻撃（7.3〜7.5、7.16、7.18）
        void ResolveRound(List<GameEvent> events)
        {
            var b = B;
            var A = b.Sides[b.Attacker];
            var D = b.Sides[1 - b.Attacker];
            b.Rounds++;
            b.TurnRounds++;
            Attack atk = b.AttackCmd.Value;
            Defense def = b.DefenseCmd.Value;
            if (def == Defense.Flee) { RunAway(1 - b.Attacker, events); return; }

            // ねむり・まひで動けないときは攻撃しない（どうぐはトレーナーが使うので使える、7.17）
            bool stuck = atk != Attack.Item && CantAct(b.Attacker, events);
            if (!stuck)
            {
                var ev = new RoundResolved { Attacker = b.Attacker, Attack = atk, Defense = def, AttackerPokemon = A.Current.Species, DefenderPokemon = D.Current.Species, DefenderTrainer = D.Name };
                MoveEffect fx = null;
                if (atk == Attack.Item)
                {
                    var (use, value, _) = ItemEffects.Of(b.AttackItem);
                    UseOn(use, value, A.Current);
                    RemoveItem(State.Players[A.Player], b.AttackItem);
                    ev.Item = b.AttackItem;
                }
                else fx = Hit(A, D, atk, def, ev);
                ev.AttackerHp = A.Current.Hp; ev.DefenderHp = D.Current.Hp;
                events.Add(ev);
                if (atk != Attack.Item) AfterHit(atk, ev, fx, events);
            }
            A.LastWasSuper = !stuck && atk == Attack.Super;

            // 砂あらし：攻守が1往復するたびに、闘タイプ以外は最大HPの1/10（7.13）
            if (b.Weather == "砂あらし" && b.Rounds % 2 == 0)
                for (int i = 0; i < 2; i++)
                {
                    var m = b.Sides[i].Current;
                    if (m.Fainted || Pokemons.Of(data, m).Type == "闘") continue;
                    int dmg = Math.Max(1, (int)(Pokemons.MaxHp(data, m) * Tune("sand_damage")));
                    m.Hp = Math.Max(0, m.Hp - dmg);
                    events.Add(new SandstormDamage { Side = i, Damage = dmg, Hp = m.Hp, Pokemon = m.Species });
                }
            // やけど・どく：攻撃したラウンドの終わりに、攻撃した側が受ける（＝1往復で1回ずつ、7.17）
            var am = A.Current;
            if (!am.Fainted && (am.Status == Status.Burn || am.Status == Status.Poison))
            {
                int dmg = Math.Max(1, (int)(Pokemons.MaxHp(data, am) * Tune(am.Status == Status.Burn ? "burn_damage" : "poison_damage")));
                am.Hp = Math.Max(0, am.Hp - dmg);
                events.Add(new StatusDamage { Side = b.Attacker, Damage = dmg, Hp = am.Hp, Pokemon = am.Species, Status = am.Status });
            }
            AbilityRoundEnd(b.Attacker, events);
            HeldRoundEnd(b.Attacker, events);

            int forced = b.ForcedSwitch;
            b.ForcedSwitch = -1;
            if (HandleFaints(events)) return; // 倒れた子がいたら、交代はそちらの選び直しにまかせる
            if (forced >= 0) { SwitchOut(forced, b.ForcedRandom, events); return; }
            b.Attacker = 1 - b.Attacker;
            if (b.Rounds >= TuneInt(b.Full ? "round_limit_full" : "round_limit_quick")) { Judge(events); return; }
            // 野生戦は攻守が1往復するたびに「ボールを投げる？」（7.1.3）
            var me = State.Players[b.Sides[0].Player];
            if (b.Kind == BattleKind.Wild && b.Rounds % 2 == 0 && me.BallCount > 0)
            {
                b.Step = BattleStep.Ball;
                events.Add(new BallPrompt { Player = me.Id });
                return;
            }
            BeginCommands(events);
        }

        void SuspendBattle(List<GameEvent> events)
        {
            var b = B;
            var p = State.Players[b.Sides[0].Player];
            b.TurnRounds = 0;
            p.PendingBattle = b;
            events.Add(new BattleSuspended { Player = p.Id, Opponent = b.Sides[1].Name });
            EndTurn(events);
        }

        // 自分の番が来たら、止めていたバトルの続き（サイコロは振らない）
        void ResumeBattle(PlayerState p, List<GameEvent> events)
        {
            var b = State.Battle = p.PendingBattle;
            p.PendingBattle = null;
            State.Phase = Phase.Battle;
            events.Add(new BattleResumed { Player = p.Id, Kind = b.Kind, Full = b.Full, Opponent = b.Sides[1].Name, Players = b.Sides.Select(s => s.Player).ToArray() });
            for (int i = 0; i < 2; i++)
            {
                var m = b.Sides[i].Current;
                var sp = Pokemons.Of(data, m);
                events.Add(new SentOut { Side = i, Pokemon = m.Species, Level = m.Level, Hp = m.Hp, MaxHp = Pokemons.MaxHp(data, m), Type = sp.Type, Weakness = sp.Weakness, Status = m.Status, Exp = m.Exp });
            }
            BeginCommands(events);
        }

        // ねむっている・まひで しびれた。動けなければ true
        bool CantAct(int side, List<GameEvent> events)
        {
            var m = B.Sides[side].Current;
            if (m.Status == Status.Sleep)
            {
                if (--m.SleepRounds <= 0) { m.Status = Status.None; m.SleepRounds = 0; }
                events.Add(new CantMove { Side = side, Pokemon = m.Species, Status = Status.Sleep, Woke = m.Status == Status.None });
                return true;
            }
            if (m.Status != Status.Paralysis || !Chance(Tune("paralysis_skip_rate"))) return false;
            events.Add(new CantMove { Side = side, Pokemon = m.Species, Status = Status.Paralysis });
            return true;
        }

        // 能力ランクの倍率（7.15）
        double RankMult(int rank) => rank >= 2 ? Tune("rank_up2") : rank == 1 ? Tune("rank_up1") : rank == -1 ? Tune("rank_down1") : rank <= -2 ? Tune("rank_down2") : 1;

        // バトル中のすばやさ：ランクとまひ（半分）を入れたもの。先攻カードと命中に使う
        int BattleSpeed(BattleSide s) => (int)(BattleStat(s, x => x.Speed) * RankMult(s.Ranks[2]) * AbilitySpeedMult(s) * (s.Current.Status == Status.Paralysis ? 0.5 : 1));

        // 技の追加効果（7.16）。ガードに失敗したときだけ呼ばれる。急所だけの効果は Hit で使い済み
        void ApplyMoveEffect(MoveEffect fx, int attacker, List<GameEvent> events)
        {
            if (HeldOf(B.Sides[attacker]) == "とつげきチョッキ") return; // 持たせた子の攻撃は追加効果なし
            if ((fx.Status == Status.None && fx.Stats.Count == 0) || !Chance(fx.Chance)) return;
            int side = fx.Status != Status.None || !fx.Self ? 1 - attacker : attacker;
            if (side != attacker && AbilityName(B.Sides[side]) == "りんぷん") { Announce(side, "りんぷん", events); return; } // 追加効果を受けない
            if (fx.Status != Status.None) { Inflict(side, fx.Status, events); return; }
            if (B.Sides[side].Current.Fainted) return;
            foreach (int st in fx.Stats) ChangeRank(side, st, fx.Stages, events);
        }

        void ChangeRank(int side, int stat, int delta, List<GameEvent> events)
        {
            var s = B.Sides[side];
            if (delta < 0 && HeldOf(s) == "しろいハーブ") { Consume(side, events); return; } // 下がらない
            int before = s.Ranks[stat];
            s.Ranks[stat] = Math.Max(-2, Math.Min(2, before + delta));
            if (s.Ranks[stat] != before) events.Add(new RankChanged { Side = side, Pokemon = s.Current.Species, Stat = MoveEffect.StatNames[stat], Delta = s.Ranks[stat] - before, Now = s.Ranks[stat] });
        }

        // 状態異常にする（1体に1つだけ）。ability があれば、bySide のその特性で起きたと知らせる
        void Inflict(int side, Status status, List<GameEvent> events, int bySide = -1, string ability = null, bool sync = true)
        {
            var s = B.Sides[side];
            var m = s.Current;
            if (m.Fainted || m.Status != Status.None) return;
            string mine = AbilityName(s);
            if (mine == "リーフガード" && B.Weather == "晴れ") { Announce(side, mine, events); return; }
            // かごのみ（トレーナーの道具）：ねむりにされても起こしてもらえる（なくなる）
            if (status == Status.Sleep && s.Player >= 0 && State.Players[s.Player].TrainerBag.Contains("かごのみ"))
            {
                RemoveItem(State.Players[s.Player], "かごのみ");
                events.Add(new HeldItemUsed { Side = side, Pokemon = m.Species, Item = "かごのみ", Consumed = true });
                return;
            }
            if (ability != null) Announce(bySide, ability, events);
            m.Status = status;
            if (status == Status.Sleep)
            {
                m.SleepRounds = Roll(TuneInt("sleep_min"), TuneInt("sleep_max"));
                if (mine == "はやおき") m.SleepRounds = (m.SleepRounds + 1) / 2; // 半分、端数は切り上げ
            }
            events.Add(new StatusInflicted { Side = side, Pokemon = m.Species, Status = status });
            // シンクロ：やけど・まひ・どくにされたら、相手も同じにする
            if (sync && mine == "シンクロ" && status != Status.Sleep) Inflict(1 - side, status, events, side, mine, false);
        }

        // バトルが終わったら、まひ・ねむりは治る。やけど・どくは残る（7.17）
        void CureAfterBattle(List<GameEvent> events)
        {
            ReturnStolen();
            AbilityBattleEnd(events);
            foreach (var s in B.Sides.Where(s => s.Player >= 0))
                foreach (var m in s.Team.Where(m => m.Status == Status.Paralysis || m.Status == Status.Sleep)) { m.Status = Status.None; m.SleepRounds = 0; }
        }

        // へんしん（メタモンの特性）：場にいる間は相手のポケモンの種族になる（ステータス・タイプ・弱点・技の効果）。HP・レベルは自分のまま
        bool Transforms(BattleSide s) => Pokemons.Of(data, s.Current).Ability.StartsWith("へんしん");
        Species BattleSpecies(BattleSide s) => Pokemons.Of(data, Transforms(s) ? B.Sides[B.Sides[0] == s ? 1 : 0].Current : s.Current);
        int BattleStat(BattleSide s, Func<Species, int> stat) => Pokemons.Stat(stat(BattleSpecies(s)), s.Current.Level);

        MoveEffect Hit(BattleSide A, BattleSide D, Attack atk, Defense def, RoundResolved ev)
        {
            var ap = A.Current; var dp = D.Current;
            // 命中：基礎命中率（必殺は80%）＋すばやさの差（±10%まで）。NPC戦でHPが減ったプレイヤーのポケモンは絆で避けやすい
            double hit = atk == Attack.Super ? Tune("special_hit_rate") : Tune("hit_rate_base");
            double clip = Tune("speed_hit_clip");
            hit += Math.Max(-clip, Math.Min(clip, (BattleSpeed(A) - BattleSpeed(D)) * Tune("speed_hit_coef")));
            double evasion = AbilityEvasion(D) + (HeldOf(D) == "ひかりのこな" ? Tune("bright_powder_evasion") : 0); // すながくれ・ひかりのこな（すばやさの補正とは別枠）
            hit -= evasion;
            double bond = B.IsNpc && D.Player >= 0 && dp.Hp < Pokemons.MaxHp(data, dp) * Tune("bond_hp_threshold") ? Tune("bond_evasion_bonus") : 0;
            double r = Rng.Next(ref State.Rng);
            if (r >= hit - bond)
            {
                ev.Missed = true; ev.Bond = r < hit;
                if (!ev.Bond && r < hit + evasion) ev.Abilities.Add(AbilityName(D));
                return null;
            }
            if (Absorbs(A, D)) { ev.Absorbed = true; ev.Abilities.Add(AbilityName(D)); return null; } // 雷の攻撃を受けない
            if (HeldOf(D) == "ふうせん" && BattleSpecies(A).Type == "闘") { ev.Absorbed = true; ev.Abilities.Add("ふうせん"); return null; } // 1回だけ（AfterHit で消費）

            // ガードの成否と倍率（7.3の表）
            bool guarded; double mult;
            if (atk == Attack.Super)
            {
                guarded = def == Defense.Counter;
                mult = guarded ? Tune("reflect_mult") : Tune("special_through_mult");
            }
            else if (def == Defense.Counter) { guarded = false; mult = Tune("counter_miss_mult"); }
            else
            {
                guarded = atk == Attack.Physical ? def == Defense.Guard : def == Defense.SpecialGuard;
                mult = guarded ? Tune("guard_success_mult") : Tune("guard_wrong_mult");
            }

            // ダメージ（7.5）：(Lv＋5)×攻撃÷防御＋2 に、コマンド・弱点・急所・乱数をかける
            int a, d;
            var sp = BattleSpecies(A); var dsp = BattleSpecies(D);
            bool physical = atk == Attack.Physical || (atk == Attack.Super && BattleStat(A, x => x.Attack) >= BattleStat(A, x => x.SpAttack));
            // 職業の補正はステータスにかける（7.5 の①）。必殺技の補正は最後にかける
            var ja = JobBonusFor(A.Player, ap); var jd = JobBonusFor(D.Player, dp);
            if (physical) { a = (int)(BattleStat(A, x => x.Attack) * ja.atk * RankMult(A.Ranks[0]) * HeldAttackMult(A, true)); d = (int)(BattleStat(D, x => x.Defense) * jd.def * HeldDefenseMult(D)); }
            else { a = (int)(BattleStat(A, x => x.SpAttack) * ja.spa * RankMult(A.Ranks[1]) * HeldAttackMult(A, false)); d = (int)(BattleStat(D, x => x.SpDefense) * jd.spd * HeldDefenseMult(D)); }
            // 追加効果は こうげき技・とくこう技だけ（必殺技には効果の列がない）
            var fx = atk == Attack.Physical ? sp.PhysicalEffect : atk == Attack.Special ? sp.SpecialEffect : null;
            double baseDamage = BaseDamage(data, ap.Level, a, d);
            ev.Weakness = sp.Type == dsp.Weakness;
            ev.Critical = !guarded && AbilityName(D) != "カブトアーマー" && Chance(Math.Max(fx != null && fx.CritRate > 0 ? fx.CritRate : Tune("crit_rate"), HeldOf(A) == "ピントレンズ" ? Tune("crit_rate_scope") : 0));
            int dice = ev.Dice = Roll(1, 10);
            double random = Tune("damage_random_min") + (Tune("damage_random_max") - Tune("damage_random_min")) * (dice - 1) / 9;
            double damage = baseDamage * mult * (ev.Weakness ? Tune("weakness_mult") : 1) * (ev.Critical ? Tune("crit_mult") : 1) * random * (atk == Attack.Super ? ja.super : 1) * (physical && ap.Status == Status.Burn ? 0.5 : 1)
                * (fx != null && fx.LateMult > 0 && B.First != B.Attacker ? fx.LateMult : 1) * AbilityDamageMult(A, D, physical, guarded, ev)
                * (HeldOf(A) == "いのちのたま" ? Tune("life_orb_mult") : 1)
                * (B.Kind == BattleKind.Wild && A.Player < 0 ? Tune("wild_damage_mult") : 1); // 野生が与えるダメージ
            ev.Damage = Math.Max(TuneInt("damage_min"), (int)damage);
            ev.Guarded = guarded;
            if (atk == Attack.Super && guarded)
            {
                ev.Reflected = true; // カウンター成功：防御側は無傷、2倍を攻撃側へ
                ap.Hp = Math.Max(0, ap.Hp - ev.Damage);
            }
            else
            {
                ev.Damage = HeldCapDamage(D, AbilityCapDamage(D, ev.Damage, ev), ev);
                dp.Hp = Math.Max(0, dp.Hp - ev.Damage);
            }
            return guarded ? null : fx;
        }

        // 基本ダメージ（7.5 の②）＝（攻撃側のレベル＋5）× 攻撃 ÷ 防御 ＋ 2
        public static double BaseDamage(GameData data, int level, int attack, int defense) =>
            (level + data.Required("damage_level_offset")) * attack / Math.Max(1, defense) + data.Required("damage_flat_bonus");

        // 倒れたポケモンの処理。バトルが終わるか、次のポケモンを選ぶ待ちになったら true
        bool HandleFaints(List<GameEvent> events)
        {
            var b = B;
            bool any = false;
            for (int i = 0; i < 2; i++)
            {
                var s = b.Sides[i];
                var m = s.Current;
                if (!m.Fainted || s.NeedsSelect) continue;
                any = true;
                events.Add(new PokemonFainted { Side = i, Pokemon = m.Species });
                var winner = b.Sides[1 - i];
                OnKnockOut(1 - i, events);
                if (winner.Player < 0 || winner.Current.Fainted) continue;
                var me = State.Players[winner.Player];
                Register(me, m.Species, "たおした");
                if (i == 1) b.DefeatedLevels.Add(m.Level);
                // レベル追いつき（7.8）：低いレベルで倒したら差分だけ上がる。そのバトルの経験値はなし
                if (m.Level > winner.Current.Level)
                {
                    Pokemons.SetLevel(data, winner.Current, m.Level);
                    b.CatchUp = true;
                    events.Add(new LevelCaughtUp { Player = me.Id, Pokemon = winner.Current.Species, Level = winner.Current.Level });
                }
            }
            if (!any) return false;

            bool out0 = b.Full ? !b.Sides[0].HasAlive : b.Sides[0].Current.Fainted;
            bool out1 = b.Full ? !b.Sides[1].HasAlive : b.Sides[1].Current.Fainted;
            if (out0 || out1) { EndBattle(out0 && out1 ? -1 : out0 ? 1 : 0, false, events); return true; }
            // フルバトル：倒れた側が次を選び、カードを引き直す（7.1.2）
            for (int i = 0; i < 2; i++)
                if (b.Sides[i].Current.Fainted) PrepareSide(i, true, events);
            AfterSelect(events);
            return true;
        }

        // ラウンド上限での判定（7.1.1）
        void Judge(List<GameEvent> events)
        {
            double Ratio(PokemonState m) => (double)m.Hp / Pokemons.MaxHp(data, m);
            var s0 = B.Sides[0]; var s1 = B.Sides[1];
            int w;
            if (!B.Full) w = Compare(Ratio(s0.Current), Ratio(s1.Current));
            else
            {
                w = Compare(s0.Team.Count(m => !m.Fainted), s1.Team.Count(m => !m.Fainted));
                if (w < 0) w = Compare(s0.Team.Sum(Ratio), s1.Team.Sum(Ratio));
            }
            EndBattle(w, true, events);
        }

        static int Compare(double a, double b) => a > b ? 0 : b > a ? 1 : -1;

        // にげる（7.3）。プレイヤー戦では降参＝負け
        void RunAway(int side, List<GameEvent> events)
        {
            var b = B;
            bool surrender = b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament;
            events.Add(new Fled { Side = side, Surrender = surrender });
            if (surrender)
            {
                // 降参でもレベル追いつきは起きる（7.3）：勝った側のほうがレベルが低ければ、差分だけ上がる
                var w = b.Sides[1 - side];
                if (w.Player >= 0 && b.Sides[side].Current.Level > w.Current.Level)
                {
                    Pokemons.SetLevel(data, w.Current, b.Sides[side].Current.Level);
                    b.CatchUp = true;
                    events.Add(new LevelCaughtUp { Player = w.Player, Pokemon = w.Current.Species, Level = w.Current.Level });
                }
                b.Surrendered = true;
                EndBattle(1 - side, false, events);
                return;
            }
            CureAfterBattle(events);
            events.Add(new BattleEnded { Winner = -1 });
            if (b.Full) Rest(State.Players[b.Sides[side].Player], events); // フルバトルから逃げたら1回休み
            FinishBattle(events);
        }

        // 野生にボールを投げる（6.3）：種族コスト×(HP÷最大HP)＋0〜15 ≦ ボールの閾値（バッジ1個ごとに上乗せ）
        void Throw(string ball, List<GameEvent> events)
        {
            var p = State.Players[B.Sides[0].Player];
            var wild = B.Sides[1].Current;
            p.Balls[ball]--;
            double difficulty = Pokemons.Of(data, wild).Total / 10.0 * wild.Hp / Pokemons.MaxHp(data, wild) + Roll(0, TuneInt("capture_random_max"));
            bool caught = ball == "マスターボール" || difficulty <= BallThreshold(ball) + p.Badges * Tune("badge_capture_bonus") + JobCaptureBonus(p);
            events.Add(new BallThrown { Player = p.Id, Ball = ball, Caught = caught, Pokemon = wild.Species });
            if (!caught) { BeginCommands(events); return; }
            if (B.Legend) p.IslandLegend = ""; // チケットの伝説を捕まえた
            if (B.Symbol) SymbolBeaten(p.Id, wild.Species, true, events); // 盤面の伝説を捕まえた

            wild.MetHow = "捕獲"; wild.MetPlace = board[B.Square].Place; wild.MetWeek = State.Week; wild.MetSpecies = wild.Species;
            wild.Status = Status.None;
            CureAfterBattle(events);
            p.CaughtSpecies.Add(wild.Species);
            events.Add(new Caught { Player = p.Id, Pokemon = wild.Species, ToBox = p.Party.Count >= TuneInt("party_size") });
            State.Battle = null;
            if (!AddPokemon(p, wild, events)) EndTurn(events); // 控えもいっぱいなら、逃がす子を選んでから手番が終わる
        }

        double BallThreshold(string ball) =>
            ball == "モンスターボール" ? Tune("ball_threshold_monster") : ball == "スーパーボール" ? Tune("ball_threshold_super") : ball == "ハイパーボール" ? Tune("ball_threshold_hyper") : 0;

        // ---- 終わり ----

        // winner は勝った側（0/1）、-1 は引き分け
        void EndBattle(int winner, bool judged, List<GameEvent> events)
        {
            var b = B;
            CureAfterBattle(events);
            if (b.Kind == BattleKind.Tournament)
            {
                // トーナメントの試合：報酬も罰もなし。引き分けなら どちらかが勝ち上がる
                if (winner < 0) winner = Roll(0, 1);
                events.Add(new BattleEnded { Winner = winner, Judged = judged, WinnerName = b.Sides[winner].Name });
                TournamentMatchEnded(b, winner, events);
                return;
            }
            events.Add(new BattleEnded { Winner = winner, Draw = winner < 0, Judged = judged, WinnerName = winner >= 0 ? b.Sides[winner].Name : null });
            if (winner < 0)
            {
                // 引き分け：プレイヤーは全員1回休み、報酬も奪取もなし
                foreach (var s in b.Sides.Where(s => s.Player >= 0)) Rest(State.Players[s.Player], events);
                AfterBattle(events);
                FinishBattle(events);
                return;
            }
            var win = b.Sides[winner];
            var lose = b.Sides[1 - winner];
            if (b.Legend && winner == 0) State.Players[win.Player].IslandLegend = ""; // チケットの伝説を倒した
            if (b.Symbol && winner == 0) SymbolBeaten(win.Player, lose.Current.Species, false, events); // 盤面の伝説を倒した
            if (win.Player >= 0) WinnerRewards(win, lose, events);
            if (lose.Player >= 0) LoserPenalty(lose, win, events);
            AfterBattle(events);
            if (State.Phase == Phase.Facility) { State.Battle = null; return; } // もらったポケモンの入れ先を選ぶ（控えがいっぱい）
            if (b.Kind == BattleKind.Player)
            {
                b.Winner = winner;
                b.Step = BattleStep.Reward;
                events.Add(new RewardRequested { Winner = win.Player, Loser = lose.Player });
                return;
            }
            FinishBattle(events);
        }

        void WinnerRewards(BattleSide win, BattleSide lose, List<GameEvent> events)
        {
            var b = B;
            var p = State.Players[win.Player];
            // お金（7.7）：相手の一番高いレベル×種類ごとの値。プレイヤー戦は負けた人が落としたお金
            int topLevel = lose.Team.Max(m => m.Level);
            string key = b.Kind == BattleKind.Wild ? "wild_win_money" : b.Kind == BattleKind.Trainer ? "trainer_win_money" : b.Kind == BattleKind.Gym ? "gym_win_money" : b.Kind == BattleKind.Rival ? "rival_win_money" : b.Kind == BattleKind.Event ? "event_win_money" : b.Kind == BattleKind.Rocket ? "rocket_win_money" : b.Kind == BattleKind.Job ? "trainer_win_money" : null;
            if (key != null) AddMoney(p, TuneInt(key) * topLevel * (p.Party.Any(m => m.HeldItem == "おまもりこばん") ? TuneInt("amulet_coin_mult") : 1), "賞金", events);

            // 経験値（6.6）：倒した相手ごとに 係数×Lv²。出たポケモンは1.0倍、出ていないポケモンは0.6倍
            if (!b.CatchUp)
            {
                var levels = win == b.Sides[0] ? b.DefeatedLevels : lose.Team.Where(m => m.Fainted).Select(m => m.Level).ToList();
                double total = levels.Sum(l => Tune("exp_coef") * l * l);
                for (int i = 0; i < win.Team.Count; i++)
                {
                    var m = win.Team[i];
                    if (total <= 0) continue; // 手持ち全員に入る（戦闘不能でも、6.6）
                    int exp = (int)(total * Tune(win.Participated.Contains(i) ? "exp_selected" : "exp_bench"));
                    int up = Pokemons.GainExp(data, m, exp);
                    events.Add(new ExpGained { Player = p.Id, Pokemon = m.Species, Exp = exp, Levels = up, Level = m.Level, Total = m.Exp });
                }
            }

            if (b.Kind == BattleKind.Trainer)
            {
                Say(b.DialogKey + "_win", events, p, ("名前", lose.Name), ("ポケモン", lose.Team[0].Species));
                if (b.TrainerId != null) p.BeatenTrainers.Add(b.TrainerId);
                // 持ち主がいなければファンになる（ジムトレーナーがもう他人のファンなら、倒した記録だけ残る）
                if (!State.Owners.ContainsKey(b.Square)) { SetOwner(b.Square, p.Id, events); Say("trainer_become_fan", events, p, ("名前", lose.Name)); }
            }
            if (b.Kind == BattleKind.Rival)
            {
                Say(b.DialogKey + "_win", events, p);
                SetOwner(b.Square, p.Id, events);
                Say("rival_become_owner", events, p);
            }
            if (b.Kind == BattleKind.Rocket) RocketCleared(p, events);
            if (b.Kind == BattleKind.Job) { UnlockJob(p, b.Job, events); OfferJob(p, b.Job, events); } // 勝つと解放、その場で就ける（9.2）
            if (b.Kind == BattleKind.Event) EventBattleWon(p, events);
            // 共通報酬のアイテム（7.7）：NPC戦に勝つと確率で、ボールマスと同じ表（バッジの段階）から1つ。もらったポケモンの入れ先を選んでいる最中なら出さない
            if (b.IsNpc && State.Phase != Phase.Facility && Chance(Tune("win_item_rate")))
            {
                var tier = data.TierFor(p.Badges);
                var item = tier == null ? null : Pick(data.BallSquareItems, x => x.Weights[tier.Id]);
                if (item != null) GiveItem(p, item.Item, events);
            }
            if (b.Kind == BattleKind.Gym)
            {
                p.Gyms.Add(b.Gym);
                events.Add(new BadgeEarned { Player = p.Id, Gym = b.Gym, Badges = p.Badges });
            }
        }

        // 負けたとき（7.9）：出ていたポケモン（フルなら全員）が戦闘不能、所持金の1/3、60%で1回休み、全滅ならセンターへ
        void LoserPenalty(BattleSide lose, BattleSide win, List<GameEvent> events)
        {
            var b = B;
            var p = State.Players[lose.Player];
            int loss = (int)Math.Ceiling(p.Money * Tune("defeat_money_ratio") - 1e-9);
            if (b.Kind == BattleKind.Event)
            {
                // イベントのバトルに負けたときは所持金だけ（戦闘不能・1回休みなし）
                AddMoney(p, -loss, "負けて落とした", events);
                Say(data.RandomEvents.FirstOrDefault(e => e.Id == b.EventId)?.TextKey + "_lose", events, p);
                return;
            }
            foreach (var m in b.Full ? lose.Team : new List<PokemonState> { lose.Current }) m.Hp = 0;
            AddMoney(p, -loss, "負けて落とした", events);
            if (Chance(Tune("defeat_rest_rate"))) Rest(p, events);
            if (b.Kind == BattleKind.Rival) Say(b.DialogKey + "_lose", events, p);
            if (b.Kind == BattleKind.Trainer || b.Kind == BattleKind.Rocket) Say(b.DialogKey + "_lose", events, p, ("名前", win.Name), ("ポケモン", win.Current.Species));
        }

        void Rest(PlayerState p, List<GameEvent> events)
        {
            if (!p.Has("1回休み")) p.Ailments.Add(new PlayerAilment { Name = "1回休み", Weeks = 1 });
            events.Add(new RestNextTurn { Player = p.Id });
        }

        // バトルのあと：全滅したらセンターへ、進化の判定（6.5、1段階だけ）
        void AfterBattle(List<GameEvent> events)
        {
            foreach (var s in B.Sides.Where(s => s.Player >= 0))
            {
                var p = State.Players[s.Player];
                if (p.Party.All(m => m.Fainted)) SendToCenter(p, events);
                foreach (var m in p.Party)
                {
                    string from = m.Species, to = Pokemons.TryEvolve(data, m);
                    if (to != null) { Register(p, to, "しんかさせた"); events.Add(new Evolved { Player = p.Id, From = from, To = to }); }
                }
            }
        }

        // 全滅：道のりがいちばん近いポケモンセンター（研究室を含む、入れるエリアだけ）へ送って全回復（7.9）
        void SendToCenter(PlayerState p, List<GameEvent> events)
        {
            var seen = new HashSet<int> { p.Square };
            var queue = new Queue<int>();
            queue.Enqueue(p.Square);
            int found = board.StartSquare;
            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                if (board[s].Type == "ポケモンセンター" || board[s].Type == "スタート") { found = s; break; }
                var next = board[s].Neighbors.ToList();
                if (board[s].WarpPartner >= 0) next.Add(board[s].WarpPartner);
                foreach (int n in next)
                    if (Movement.CanEnter(board, n, p.Badges, TopBadges, p.Surf) && seen.Add(n)) queue.Enqueue(n);
            }
            p.Square = found;
            foreach (var m in p.Party.Concat(p.Box)) Pokemons.HealFull(data, m);
            events.Add(new SentToCenter { Player = p.Id, Square = found });
        }

        // イベントのバトルに勝った：相手の「報酬」のポケモンをもらう（Lvは調整値 event_reward_level）
        void EventBattleWon(PlayerState p, List<GameEvent> events)
        {
            var e = data.RandomEvents.FirstOrDefault(x => x.Id == B.EventId);
            if (e == null) return;
            Say(e.TextKey + "_win", events, p);
            if (e.Reward == "" || data.FindSpecies(e.Reward) == null) return;
            var mon = Pokemons.Create(data, e.Reward, TuneInt("event_reward_level"), $"{e.Name}からもらった", board[p.Square].Place, State.Week);
            events.Add(new PokemonReceived { Player = p.Id, Pokemon = e.Reward, From = e.Name, ToBox = p.Party.Count >= TuneInt("party_size") });
            AddPokemon(p, mon, events);
        }

        // プレイヤー戦の勝利報酬。袋がいっぱいで選択を待つなら true
        bool GiveReward(ChooseReward r, List<GameEvent> events)
        {
            var b = B;
            var winner = State.Players[b.Sides[b.Winner].Player];
            var loser = State.Players[b.Sides[1 - b.Winner].Player];
            if (r.Reward == Reward.Item)
            {
                RemoveItem(loser, r.Item);
                events.Add(new ItemLost { Player = loser.Id, Item = r.Item, To = winner.Id });
                return GiveItem(winner, r.Item, events);
            }
            else if (r.Reward == Reward.Fan)
            {
                var fans = OwnedSquares(loser.Id, "トレーナー").ToList();
                SetOwner(fans[Roll(0, fans.Count - 1)].Id, winner.Id, events);
            }
            else if (r.Reward == Reward.Trade)
            {
                var mine = r.GiveFromBox ? winner.Box : winner.Party;
                PokemonState taken = loser.Party[r.Take], given = mine[r.Give];
                // もちものは外れて、元の持ち主の袋に残る（8.2）。ponytail: 袋がいっぱいでも入れる（そのあと拾えないだけ）
                if (taken.HeldItem != "") { loser.PokemonBag.Add(taken.HeldItem); taken.HeldItem = ""; }
                if (given.HeldItem != "") { winner.PokemonBag.Add(given.HeldItem); given.HeldItem = ""; }
                loser.Party[r.Take] = given;
                mine[r.Give] = taken;
                // 交換で来た子は「〇〇さんから受け取った」と記録する（1.5）
                taken.MetHow = $"{loser.Name}から受け取った"; taken.MetPlace = board[winner.Square].Place; taken.MetWeek = State.Week; taken.MetSpecies = taken.Species; taken.LeadTurns = 0;
                given.MetHow = $"{winner.Name}から受け取った"; given.MetPlace = board[loser.Square].Place; given.MetWeek = State.Week; given.MetSpecies = given.Species; given.LeadTurns = 0;
                Register(winner, taken.Species, "うけとった"); Register(loser, given.Species, "うけとった");
                ApplyJobHp(winner); ApplyJobHp(loser);
                events.Add(new PokemonTraded { Winner = winner.Id, Loser = loser.Id, Taken = taken.Species, Given = given.Species });
            }
            return false;
        }

        void SetOwner(int square, int player, List<GameEvent> events)
        {
            int prev = State.Owners.TryGetValue(square, out int o) ? o : -1;
            State.Owners[square] = player;
            events.Add(new OwnerChanged { Square = square, Owner = player, Previous = prev });
            if (player >= 0 && player < State.Players.Count) CheckCyclingUnlock(State.Players[player], events);
        }

        void FinishBattle(List<GameEvent> events)
        {
            State.Battle = null;
            EndTurn(events);
        }
    }
}
