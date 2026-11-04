using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Sugoroku.Rules
{
    // 6・7章（ポケモンとバトル）の 確定の決まりのテスト。数は調整値から読む（調整値を変えても落ちないように）。仕様書に書いてある例だけ そのままの数で確かめる
    // ヒトカゲ・ゼニガメ・フシギダネは 特性も技の効果もないので、ダメージの式を そのまま比べられる
    public static partial class RulesSelfTest
    {
        static object Call(GameProcessor g, string name, params object[] args) =>
            typeof(GameProcessor).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(g, args);

        static void BattleRules(GameData data, BoardData board, List<string> log)
        {
            double T(string key) => data.Required(key);
            int MaxHp(PokemonState m) => Pokemons.MaxHp(data, m);

            // 野生戦を始める（デバッグのバトル）。kind を変えると 同じ形のトレーナー戦などとして扱う
            (GameProcessor g, BattleState b, PlayerState p) Fight(string mine, int myLv, string foe, int foeLv, BattleKind kind = BattleKind.Wild, ulong seed = 7, int square = -1)
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, seed, out _);
                var p = g.State.Players[0];
                p.Party[0] = Pokemons.Create(data, mine, myLv, "", "", 1);
                if (square >= 0) p.Square = square;
                g.DebugEnabled = true;
                Apply(g, new DebugCommand { Player = 0, Action = DebugAction.Battle, Text = foe, Value = foeLv });
                var b = g.State.Battle;
                b.Kind = kind;
                return (g, b, p);
            }
            // 同じマスに止まって プレイヤー戦（7.6）。はじめの攻撃側まで進める
            (GameProcessor g, BattleState b) PvP(string mine, int myLv, string yours, int yourLv)
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ヒトカゲ") }, 50, 3, out _);
                var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                me.Party[0] = Pokemons.Create(data, mine, myLv, "", "", 1);
                you.Party[0] = Pokemons.Create(data, yours, yourLv, "", "", 1);
                g.DebugEnabled = true;
                Apply(g, new DebugCommand { Player = me.Id, Action = DebugAction.SetDice, Value = 1 });
                int to = Apply(g, new RollDice { Player = me.Id }).OfType<DiceRolled>().First().Reachable.First();
                you.Square = to;
                Apply(g, new ChooseDestination { Player = me.Id, Square = to });
                Apply(g, new ConfirmMove { Player = me.Id });
                foreach (var s in g.State.Battle?.Sides ?? new BattleSide[0]) if (s.NeedsSelect) Apply(g, new SelectPokemon { Player = s.Player, Slot = 0 });
                return (g, g.State.Battle?.Kind == BattleKind.Player ? g.State.Battle : null);
            }
            RoundResolved Hit(GameProcessor g, BattleState b, int attacker, Attack atk, Defense def)
            {
                b.Attacker = attacker; b.First = attacker;
                var ev = new RoundResolved();
                Call(g, "Hit", b.Sides[attacker], b.Sides[1 - attacker], atk, def, ev);
                return ev;
            }
            double Rank(int r) => r >= 2 ? T("rank_up2") : r == 1 ? T("rank_up1") : r == -1 ? T("rank_down1") : r <= -2 ? T("rank_down2") : 1;
            // 7.5 のダメージ式で 出るはずのダメージ（特性・もちもの・職業・野生の倍率がないとき）。ダイス・急所・弱点は 出来事のものを使う
            int Expected(BattleState b, int attacker, Attack atk, double mult, RoundResolved ev)
            {
                var A = b.Sides[attacker].Current; var D = b.Sides[1 - attacker].Current;
                var sa = Pokemons.Of(data, A); var sd = Pokemons.Of(data, D);
                bool physical = atk == Attack.Physical || (atk == Attack.Super && Pokemons.Stat(sa.Attack, A.Level) >= Pokemons.Stat(sa.SpAttack, A.Level));
                int a = (int)(Pokemons.Stat(physical ? sa.Attack : sa.SpAttack, A.Level) * Rank(b.Sides[attacker].Ranks[physical ? 0 : 1]));
                int d = Pokemons.Stat(physical ? sd.Defense : sd.SpDefense, D.Level);
                double random = T("damage_random_min") + (T("damage_random_max") - T("damage_random_min")) * (ev.Dice - 1) / 9;
                double damage = GameProcessor.BaseDamage(data, A.Level, a, d) * mult * (ev.Weakness ? T("weakness_mult") : 1) * (ev.Critical ? T("crit_mult") : 1) * random;
                return Math.Max((int)T("damage_min"), (int)damage);
            }
            void Heal(BattleState b) { foreach (var s in b.Sides) s.Current.Hp = MaxHp(s.Current); }

            // ---- 6.2 コスト ----
            {
                var cat = Pokemons.Create(data, "キャタピー", 99, "", "", 1);
                var zard = Pokemons.Create(data, "リザードン", 1, "", "", 1);
                Check(log, "6.2 コスト：内部は10倍の整数（種族値合計＋レベル）。Lv99のキャタピー（195＋99）は Lv1のリザードン（534＋1）に届かない",
                    Pokemons.Cost10(data, cat) == 195 + 99 && Pokemons.Cost10(data, zard) == 534 + 1 && Pokemons.Cost10(data, cat) < Pokemons.Cost10(data, zard));
            }

            // ---- 6.3 捕獲 ----
            {
                // 種族コスト×(HP÷最大HP)＋0〜capture_random_max ≦ 閾値（＋バッジ×badge_capture_bonus）。種を変えて 必ず捕まる・捕まらない・どちらもある を確かめる
                (int caught, int missed, bool dex) Throws(string species, int level, Func<int, int> hp, string ball, int badges)
                {
                    int c = 0, m = 0; bool dex = true;
                    for (ulong seed = 1; seed <= 40; seed++)
                    {
                        var (g, b, p) = Fight("ヒトカゲ", 50, species, level);
                        for (int i = 0; i < badges; i++) p.Gyms.Add("テスト" + i);
                        p.Balls[ball] = 10;
                        var wild = b.Sides[1].Current;
                        wild.Hp = hp(MaxHp(wild));
                        g.State.Rng = seed * 7919;
                        var ev = new List<GameEvent>();
                        Call(g, "Throw", ball, ev);
                        if (ev.OfType<BallThrown>().Single().Caught) { c++; dex &= p.Dex.Contains(species); } else m++;
                    }
                    return (c, m, dex);
                }
                double thr = T("ball_threshold_monster"), rmax = T("capture_random_max"), bonus = T("badge_capture_bonus");
                double catCost = data.FindSpecies("キャタピー").Total / 10.0, zardCost = data.FindSpecies("リザードン").Total / 10.0;
                var low = Throws("キャタピー", 10, max => (int)Math.Floor((thr - rmax) / catCost * max), "モンスターボール", 0); // 乱数が最大でも閾値以下
                var full = Throws("キャタピー", 10, max => max, "モンスターボール", 0);                                          // 乱数しだい
                var never = Throws("リザードン", 50, max => max, "モンスターボール", 0);                                         // 乱数が0でも閾値より上
                var master = Throws("リザードン", 50, max => max, "マスターボール", 0);
                Check(log, $"6.3 捕獲：種族コスト×HPの割合＋乱数 ≦ 閾値で成功（必ず {low.caught}/40、乱数しだい {full.caught}/40、届かない {never.caught}/40）、マスターボールは必ず（{master.caught}/40）。捕まえると図鑑に入る（6.7）",
                    low.caught == 40 && low.dex && full.caught > 0 && full.missed > 0 && never.caught == 0 && master.caught == 40);
                // バッジ（6.4）：乱数0でも バッジなしの閾値より上で、バッジ8個なら 乱数が最大でも届く HP（そんな HP があるときだけ）
                var zardMax = MaxHp(Pokemons.Create(data, "リザードン", 50, "", "", 1));
                int edge = Enumerable.Range(1, zardMax).FirstOrDefault(h => zardCost * h / zardMax > thr && zardCost * h / zardMax + rmax <= thr + 8 * bonus);
                if (edge > 0)
                {
                    var none = Throws("リザードン", 50, _ => edge, "モンスターボール", 0);
                    var eight = Throws("リザードン", 50, _ => edge, "モンスターボール", 8);
                    Check(log, $"6.4 捕獲：バッジ1個ごとに閾値が badge_capture_bonus 上がる（同じHPで バッジ0個 {none.caught}/40、8個 {eight.caught}/40）", none.caught == 0 && eight.caught == 40);
                }
            }

            // ---- 7.1.1 ラウンド上限と判定 ----
            {
                List<GameEvent> JudgeWith(double mine, double foe, bool full = false)
                {
                    var (g, b, p) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer); // 野生は賞金が0なので、賞金の出るトレーナー戦で
                    if (full)
                    {
                        // フル：手持ち3体どうし。mine・foe は 1体目のHPの割合、2・3体目は満タン
                        b.Full = true;
                        p.Party.Add(Pokemons.Create(data, "ゼニガメ", 50, "", "", 1)); p.Party.Add(Pokemons.Create(data, "フシギダネ", 50, "", "", 1));
                        b.Sides[1].Team.Add(Pokemons.Create(data, "ゼニガメ", 50, "", "", 1)); b.Sides[1].Team.Add(Pokemons.Create(data, "フシギダネ", 50, "", "", 1));
                    }
                    b.Sides[0].Team[0].Hp = (int)(MaxHp(b.Sides[0].Team[0]) * mine);
                    b.Sides[1].Team[0].Hp = (int)(MaxHp(b.Sides[1].Team[0]) * foe);
                    p.Money = 1000;
                    var ev = new List<GameEvent>();
                    Call(g, "Judge", ev);
                    return ev;
                }
                bool Won(List<GameEvent> ev, int side) => ev.OfType<BattleEnded>().Any(x => x.Judged && x.Winner == side && !x.Draw);
                var win = JudgeWith(0.5, 0.4); var lose = JudgeWith(0.4, 0.5); var draw = JudgeWith(1, 1);
                bool quick = Won(win, 0) && win.OfType<MoneyChanged>().Any(m => m.Player == 0 && m.Amount > 0)   // 判定の勝ちも ふつうの勝ちと同じ（賞金）
                    && Won(lose, 1) && lose.OfType<MoneyChanged>().Any(m => m.Player == 0 && m.Amount < 0)          // 判定の負けも ふつうの負けと同じ（所持金を失う）
                    && draw.OfType<BattleEnded>().Any(x => x.Draw && x.Judged) && draw.OfType<RestNextTurn>().Count() == 1 && !draw.OfType<MoneyChanged>().Any() && !draw.OfType<ExpGained>().Any();
                string Dump(List<GameEvent> ev) => string.Join(",", ev.Select(e => e.GetType().Name + (e is MoneyChanged mc ? mc.Amount.ToString() : e is BattleEnded be ? $"(w{be.Winner} d{be.Draw} j{be.Judged})" : "")));
                Check(log, "7.1.1 判定（クイック）：出ている子のHPの割合が高いほうの勝ちで、ふつうの勝ち負けと同じ（賞金・所持金）。同じなら引き分けで、NPC戦はプレイヤーだけ1回休み、報酬なし" + (quick ? "" : $" [win:{Dump(win)}] [lose:{Dump(lose)}] [draw:{Dump(draw)}]"), quick);
                // フル：倒れていない数 → 同じなら手持ち全員のHPの割合の合計
                var moreAlive = JudgeWith(0.1, 0, true);   // 3体 対 2体（相手の1体目は0）。HPの合計は相手のほうが上でも、数で勝つ
                var sumWin = JudgeWith(0.6, 0.5, true); var sumLose = JudgeWith(0.5, 0.6, true);
                Check(log, "7.1.1 判定（フル）：倒れていない数が多いほうの勝ち。同じ数なら 手持ち全員の「HP÷最大HP」の合計が高いほうの勝ち", Won(moreAlive, 0) && Won(sumWin, 0) && Won(sumLose, 1));
                // 上限のラウンドで判定に進む（クイック round_limit_quick、フル round_limit_full）。1つ前のラウンドでは まだ続く
                bool EndsAt(bool full, int rounds)
                {
                    var (g, b, _) = Fight("ヒトカゲ", 100, "ヒトカゲ", 100);
                    b.Full = full;
                    b.Rounds = rounds - 1; b.Attacker = 0; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.Guard;
                    var ev = new List<GameEvent>();
                    Call(g, "ResolveRound", ev);
                    return ev.OfType<BattleEnded>().Any(x => x.Judged);
                }
                int lq = (int)T("round_limit_quick"), lf = (int)T("round_limit_full");
                Check(log, $"7.1.1 ラウンド上限：クイックは{lq}ラウンド目、フルは{lf}ラウンド目で判定に進み、その前では続く",
                    EndsAt(false, lq) && !EndsAt(false, lq - 1) && EndsAt(true, lf) && !EndsAt(true, lf - 1));
                // プレイヤー戦の引き分け：2人とも1回休み。報酬（奪取）もお金のやりとりもない
                var (pg, pb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                var pev = new List<GameEvent>();
                if (pb != null) Call(pg, "Judge", pev);
                Check(log, "7.1.1 引き分け（プレイヤー戦）：2人とも1回休み、報酬の選択（奪取）も お金のやりとりもない",
                    pb != null && pev.OfType<BattleEnded>().Any(x => x.Draw) && pev.OfType<RestNextTurn>().Select(x => x.Player).Distinct().Count() == 2 && !pev.OfType<RewardRequested>().Any() && !pev.OfType<MoneyChanged>().Any());
            }

            // ---- 7.1.2 先攻・後攻のカード ----
            {
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50);
                int Speed(int level) => Pokemons.Stat(data.FindSpecies("ヒトカゲ").Speed, level);
                double small = T("initiative_ratio_small"), large = T("initiative_ratio_large");
                int mine = Speed(50);
                // 相手のレベルを変えて、すばやさの比が「ほぼ同じ」「少し上」「大きく上」になるところを探す
                int Level(Func<double, bool> want) => Enumerable.Range(1, 50).Reverse().FirstOrDefault(l => want((double)mine / Speed(l)));
                List<int> Cards(int level)
                {
                    Pokemons.SetLevel(data, b.Sides[1].Current, level);
                    Call(g, "DrawInitiative", new List<GameEvent>());
                    return b.Cards;
                }
                int same = Level(r => r < small), little = Level(r => r >= small && r < large), much = Level(r => r >= large);
                var c2 = Cards(same); var c3 = Cards(little); var c4 = Cards(much);
                Check(log, $"7.1.2 カード：すばやさの比が{small}倍未満は2枚（先攻1・後攻1）、{small}倍以上で3枚、{large}倍以上で4枚（速い側の先攻が1枚ずつ増え、後攻は1枚）",
                    same > 0 && little > 0 && much > 0 && c2.Count == 2 && c3.Count == 3 && c3.Count(x => x == 0) == 2 && c4.Count == 4 && c4.Count(x => x == 0) == 3);
            }

            // ---- 7.3 相性表（9マス）と 反射 ----
            {
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                var table = new (Attack atk, Defense def, string key, bool guarded)[]
                {
                    (Attack.Physical, Defense.Guard, "guard_success_mult", true), (Attack.Physical, Defense.SpecialGuard, "guard_wrong_mult", false), (Attack.Physical, Defense.Counter, "counter_miss_mult", false),
                    (Attack.Special, Defense.Guard, "guard_wrong_mult", false), (Attack.Special, Defense.SpecialGuard, "guard_success_mult", true), (Attack.Special, Defense.Counter, "counter_miss_mult", false),
                    (Attack.Super, Defense.Guard, "special_through_mult", false), (Attack.Super, Defense.SpecialGuard, "special_through_mult", false), (Attack.Super, Defense.Counter, "reflect_mult", true),
                };
                var bad = new List<string>();
                foreach (var (atk, def, key, guarded) in table)
                {
                    g.State.Rng = 12345;
                    RoundResolved ev;
                    int tries = 0;
                    do { Heal(b); ev = Hit(g, b, 0, atk, def); } while (ev.Missed && ++tries < 50); // 必殺は外れることがあるので 当たるまで
                    bool reflected = atk == Attack.Super && def == Defense.Counter;
                    var A = b.Sides[0].Current; var D = b.Sides[1].Current;
                    bool ok = !ev.Missed && ev.Guarded == guarded && ev.Reflected == reflected && ev.Damage == Expected(b, 0, atk, T(key), ev)
                        && (reflected ? D.Hp == MaxHp(D) && A.Hp == Math.Max(0, MaxHp(A) - ev.Damage) : D.Hp == Math.Max(0, MaxHp(D) - ev.Damage));
                    if (!ok) bad.Add($"{atk}×{def}");
                }
                Check(log, "7.3 相性表：9マスとも 調整値の倍率でダメージが出る（ガード成功・失敗の印も合う）。必殺をカウンターで受けると 防御側は無傷で、2倍が攻撃側に返る" + (bad.Count > 0 ? "（合わない：" + string.Join("、", bad) + "）" : ""), bad.Count == 0);
            }

            // ---- 7.4 必殺技の命中とクールダウン ----
            {
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                g.State.Rng = 2024;
                int n = 3000, hits = 0;
                for (int i = 0; i < n; i++) { Heal(b); if (!Hit(g, b, 0, Attack.Super, Defense.Guard).Missed) hits++; }
                double rate = (double)hits / n, want = T("special_hit_rate");
                Check(log, $"7.4 必殺の命中：すばやさが同じなら 約{want:P0}（{n}回で {rate:P1}）", Math.Abs(rate - want) < 0.03);
                // クールダウン：必殺を打ったら、次に自分が攻撃側になる機会では打てない。その次は打てる
                var (pg, pb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                bool cooled = false;
                if (pb != null)
                {
                    int me = pb.Sides[pb.Attacker].Player, you = pb.Sides[1 - pb.Attacker].Player;
                    void Round(int atkPlayer, int defPlayer, Attack atk)
                    {
                        Apply(pg, new ChooseAttack { Player = atkPlayer, Attack = atk });
                        Apply(pg, new ChooseDefense { Player = defPlayer, Defense = Defense.Guard });
                        Heal(pb); // 倒れないように
                    }
                    Round(me, you, Attack.Super);
                    Round(you, me, Attack.Physical);
                    string err = Error(pg, new ChooseAttack { Player = me, Attack = Attack.Super }); // 次の自分の攻撃では打てない
                    Round(me, you, Attack.Physical);
                    Round(you, me, Attack.Physical);
                    Round(me, you, Attack.Super); // その次は打てる（受け付けられなければ例外）
                    cooled = err.Contains("続けて");
                }
                Check(log, "7.4 必殺のクールダウン：打った次の 自分の攻撃の機会では選べず、その次の機会では選べる", cooled);
            }

            // ---- 7.8 レベル追いつき ----
            {
                // レベルの低い子が 高い子を倒すと、差の分だけ上がり、そのバトルの経験値はない。倒した相手は図鑑に入る（6.7）
                bool caught = false;
                for (ulong seed = 1; seed <= 20 && !caught; seed++)
                {
                    var (g, b, p) = Fight("ヒトカゲ", 5, "ゼニガメ", 20);
                    b.Sides[1].Current.Hp = 1;
                    b.Attacker = 0; b.First = 0; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.SpecialGuard;
                    g.State.Rng = seed * 7919;
                    var ev = new List<GameEvent>();
                    Call(g, "ResolveRound", ev);
                    if (!ev.OfType<PokemonFainted>().Any(x => x.Side == 1)) continue; // 外れたら 種を変えて
                    caught = ev.OfType<LevelCaughtUp>().Any(x => x.Player == 0 && x.Level == 20) && p.Party[0].Level == 20 && !ev.OfType<ExpGained>().Any() && p.Dex.Contains("ゼニガメ");
                    if (!caught) break;
                }
                Check(log, "7.8 レベル追いつき：Lv5で Lv20を倒すと Lv20になり、そのバトルの経験値はない。倒した相手は図鑑に入る", caught);
                // 降参でも起きる（7.3）：仕掛けた側（Lv5）の攻撃に 守る側（Lv20）が にげる＝降参
                var (pg, pb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                bool surrender = false;
                if (pb != null)
                {
                    var A = pb.Sides[pb.Attacker]; var D = pb.Sides[1 - pb.Attacker];
                    Pokemons.SetLevel(data, A.Current, 5); Pokemons.SetLevel(data, D.Current, 20);
                    Apply(pg, new ChooseAttack { Player = A.Player, Attack = Attack.Physical });
                    var ev = Apply(pg, new ChooseDefense { Player = D.Player, Defense = Defense.Flee });
                    surrender = ev.OfType<Fled>().Any(x => x.Surrender) && ev.OfType<LevelCaughtUp>().Any(x => x.Player == A.Player && x.Level == 20) && A.Current.Level == 20;
                }
                Check(log, "7.8 レベル追いつき：プレイヤー戦の降参でも、勝った側が低ければ 差の分だけ上がる", surrender);
            }

            // ---- 7.9 負けたとき ----
            {
                // 所持金の1/3（切り上げ）、defeat_rest_rate で1回休み
                int rests = 0, n = 200; bool money = true;
                for (ulong seed = 1; seed <= (ulong)n; seed++)
                {
                    var (g, b, p) = Fight("ヒトカゲ", 5, "ゼニガメ", 5, seed: seed);
                    p.Money = 1001;
                    var ev = Apply(g, new DebugCommand { Player = 0, Action = DebugAction.LoseBattle });
                    if (ev.OfType<RestNextTurn>().Any(x => x.Player == 0)) rests++;
                    money &= ev.OfType<MoneyChanged>().Any(x => x.Player == 0 && x.Amount == -(int)Math.Ceiling(1001 * T("defeat_money_ratio") - 1e-9)) && p.Money == 1001 - (int)Math.Ceiling(1001 * T("defeat_money_ratio") - 1e-9);
                }
                double rate = (double)rests / n, want = T("defeat_rest_rate");
                Check(log, $"7.9 負け：所持金1,001円から 1/3（切り上げ）を失い、約{want:P0}で1回休み（{n}回で {rate:P0}）", money && Math.Abs(rate - want) < 0.1);
                // 全滅：最寄りのセンターへ送られて 全回復（となりにセンターがあるマスで負ける）
                var center = board.Squares.Values.Where(s => s.Type == "ポケモンセンター" && s.MapId == "" && Movement.CanEnter(board, s.Id, 0, 0))
                    .SelectMany(c => c.Neighbors.Select(nb => (c, nb: board[nb]))).FirstOrDefault(x => x.nb.Type != "ポケモンセンター" && x.nb.Type != "スタート" && Movement.CanEnter(board, x.nb.Id, 0, 0));
                bool sent = false;
                if (center.c != null)
                {
                    var (g, b, p) = Fight("ヒトカゲ", 5, "ゼニガメ", 5, square: center.nb.Id);
                    p.Party[0].Status = Status.Burn;
                    var ev = Apply(g, new DebugCommand { Player = 0, Action = DebugAction.LoseBattle });
                    var to = ev.OfType<SentToCenter>().FirstOrDefault();
                    sent = to != null && p.Square == to.Square && (board[to.Square].Type == "ポケモンセンター" || board[to.Square].Type == "スタート")
                        && center.nb.Neighbors.Contains(to.Square) && p.Party.All(m => m.Hp == MaxHp(m) && m.Status == Status.None);
                }
                Check(log, "7.9 全滅：その場で いちばん近いセンター（となりのマス）へ送られて、全回復（状態異常も）", sent);
            }

            // ---- 7.11 弱点 ----
            {
                // ゼニガメ（水）の攻撃は ヒトカゲ（弱点 水）に weakness_mult 倍。ヒトカゲ（炎）の攻撃は ゼニガメ（弱点 雷）に ふつう
                RoundResolved Land(GameProcessor g, BattleState b)
                {
                    RoundResolved ev; int tries = 0;
                    do { Heal(b); ev = Hit(g, b, 0, Attack.Physical, Defense.SpecialGuard); } while (ev.Missed && ++tries < 50);
                    return ev;
                }
                var (wg, wb, _) = Fight("ゼニガメ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                wg.State.Rng = 99;
                var weak = Land(wg, wb);
                var (ng, nb2, _) = Fight("ヒトカゲ", 50, "ゼニガメ", 50, BattleKind.Trainer);
                ng.State.Rng = 99;
                var plain = Land(ng, nb2);
                Check(log, "7.11 弱点：攻撃のタイプが 相手の弱点なら weakness_mult 倍、ちがえば ふつう",
                    !weak.Missed && weak.Weakness && weak.Damage == Expected(wb, 0, Attack.Physical, T("guard_wrong_mult"), weak)
                    && !plain.Missed && !plain.Weakness && plain.Damage == Expected(nb2, 0, Attack.Physical, T("guard_wrong_mult"), plain));
            }

            // ---- 7.15 能力ランク ----
            {
                var (g, b, p) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                bool mults = Enumerable.Range(-2, 5).All(r => Math.Abs((double)Call(g, "RankMult", r) - Rank(r)) < 1e-9) && Math.Abs(Rank(0) - 1) < 1e-9;
                var ev0 = new List<GameEvent>();
                Call(g, "ChangeRank", 0, 0, 3, ev0); bool upClamp = b.Sides[0].Ranks[0] == 2;
                Call(g, "ChangeRank", 0, 0, -5, ev0); bool downClamp = b.Sides[0].Ranks[0] == -2;
                // ランクは こうげき（ステータス）にかかる：＋1 と −2 で 式どおりのダメージ
                bool damage = true;
                foreach (int r in new[] { 1, -2 })
                {
                    b.Sides[0].Ranks[0] = r;
                    g.State.Rng = 77;
                    Heal(b);
                    var ev = Hit(g, b, 0, Attack.Physical, Defense.SpecialGuard);
                    damage &= !ev.Missed && ev.Damage == Expected(b, 0, Attack.Physical, T("guard_wrong_mult"), ev);
                }
                // 交代（レッドカードと同じ ランダムな交代）で 0 に戻る
                p.Party.Add(Pokemons.Create(data, "ゼニガメ", 50, "", "", 1));
                b.Sides[0].Ranks = new[] { 2, -1, 1 };
                Call(g, "SwitchOut", 0, true, new List<GameEvent>());
                bool reset = b.Sides[0].Active == 1 && b.Sides[0].Ranks.All(x => x == 0);
                Check(log, "7.15 能力ランク：±2で止まり、倍率は rank_up1・up2・down1・down2（−2〜＋2）。こうげきにかかって ダメージが式どおりになり、交代で0に戻る",
                    mults && upClamp && downClamp && damage && reset);
            }

            // ---- 7.16 急所 ----
            {
                double Crits(string held, Defense def)
                {
                    var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                    b.Sides[0].Current.HeldItem = held;
                    g.State.Rng = 4242;
                    int n = 4000, c = 0;
                    for (int i = 0; i < n; i++) { Heal(b); if (Hit(g, b, 0, Attack.Physical, def).Critical) c++; }
                    return (double)c / n;
                }
                double normal = Crits("", Defense.SpecialGuard), scope = Crits("ピントレンズ", Defense.SpecialGuard), guarded = Crits("ピントレンズ", Defense.Guard);
                Check(log, $"7.16 急所：ガード失敗のときだけ。ふつう約{T("crit_rate"):P0}（{normal:P1}）、ピントレンズで約{T("crit_rate_scope"):P0}（{scope:P1}）、ガード成功では出ない（{guarded:P1}）",
                    Math.Abs(normal - T("crit_rate")) < 0.015 && Math.Abs(scope - T("crit_rate_scope")) < 0.03 && guarded == 0);
            }

            // ---- 7.17 状態異常 ----
            {
                // バトルのあと：やけど・どくは残り、まひ・ねむりは治る
                var (g, b, p) = Fight("ヒトカゲ", 10, "ヒトカゲ", 5); // 進化しないレベル
                p.Party[0].Status = Status.Burn;
                var para = Pokemons.Create(data, "ゼニガメ", 10, "", "", 1); para.Status = Status.Paralysis; p.Party.Add(para);
                var sleep = Pokemons.Create(data, "フシギダネ", 10, "", "", 1); sleep.Status = Status.Sleep; sleep.SleepRounds = 2; p.Party.Add(sleep);
                Apply(g, new DebugCommand { Player = 0, Action = DebugAction.WinBattle });
                var (pg, pb, pp) = Fight("ヒトカゲ", 10, "ヒトカゲ", 5);
                pp.Party[0].Status = Status.Poison;
                Apply(pg, new DebugCommand { Player = 0, Action = DebugAction.WinBattle });
                Check(log, "7.17 状態異常：バトルのあと、やけど・どくは残り、まひ・ねむりは治る",
                    p.Party[0].Status == Status.Burn && para.Status == Status.None && sleep.Status == Status.None && pp.Party[0].Status == Status.Poison);
                // ラウンドの終わり：攻撃した側が やけど 最大HP×burn_damage、どく 最大HP×poison_damage（最低1）
                bool RoundEnd(Status st, string key)
                {
                    var (rg, rb, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                    var me = rb.Sides[0].Current;
                    me.Status = st;
                    rb.Attacker = 0; rb.First = 0; rb.AttackCmd = Attack.Physical; rb.DefenseCmd = Defense.Guard;
                    var ev = new List<GameEvent>();
                    Call(rg, "ResolveRound", ev);
                    return ev.OfType<StatusDamage>().Any(x => x.Side == 0 && x.Damage == Math.Max(1, (int)(MaxHp(me) * T(key))));
                }
                Check(log, "7.17 状態異常：ラウンドの終わりに、やけどは最大HP×burn_damage、どくは最大HP×poison_damage のダメージ", RoundEnd(Status.Burn, "burn_damage") && RoundEnd(Status.Poison, "poison_damage"));
                // ねむりの長さは sleep_min〜sleep_max、まひは paralysis_skip_rate で動けない
                var (sg, sb, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                sg.State.Rng = 31;
                var lengths = new List<int>();
                for (int i = 0; i < 200; i++)
                {
                    var m = sb.Sides[0].Current; m.Status = Status.None;
                    Call(sg, "Inflict", 0, Status.Sleep, new List<GameEvent>(), -1, null, true);
                    lengths.Add(m.SleepRounds);
                }
                int skipped = 0, tries = 2000;
                for (int i = 0; i < tries; i++)
                {
                    sb.Sides[0].Current.Status = Status.Paralysis;
                    if ((bool)Call(sg, "CantAct", 0, new List<GameEvent>())) skipped++;
                }
                double skip = (double)skipped / tries;
                Check(log, $"7.17 状態異常：ねむりの長さは {T("sleep_min")}〜{T("sleep_max")}ラウンド（両はしも出る）、まひで動けないのは 約{T("paralysis_skip_rate"):P0}（{skip:P1}）",
                    lengths.Min() == (int)T("sleep_min") && lengths.Max() == (int)T("sleep_max") && Math.Abs(skip - T("paralysis_skip_rate")) < 0.04);
            }

            // ---- 7.18 絆による回避 ----
            {
                // NPC戦で、プレイヤーの子のHPが bond_hp_threshold を下回ると、回避が bond_evasion_bonus 上がる（すばやさが同じなら 物理は必ず当たるので、外れた分が絆）
                double Bonds(GameProcessor g, BattleState b, int attacker, double hpRatio, out int misses)
                {
                    g.State.Rng = 555;
                    int n = 3000, bonds = 0; misses = 0;
                    for (int i = 0; i < n; i++)
                    {
                        Heal(b);
                        var d = b.Sides[1 - attacker].Current;
                        d.Hp = Math.Max(1, (int)(MaxHp(d) * hpRatio));
                        var ev = Hit(g, b, attacker, Attack.Physical, Defense.SpecialGuard);
                        if (ev.Missed) misses++;
                        if (ev.Bond) bonds++;
                    }
                    return (double)bonds / n;
                }
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                double th = T("bond_hp_threshold");
                double low = Bonds(g, b, 1, th - 0.1, out _), high = Bonds(g, b, 1, th + 0.1, out int highMiss);
                var (pg, pb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                double pvp = pb == null ? -1 : Bonds(pg, pb, 0, th - 0.1, out int pvpMiss);
                Check(log, $"7.18 絆：NPC戦でHPが{th:P0}未満なら 約{T("bond_evasion_bonus"):P0}よける（{low:P1}）、{th:P0}以上では よけない、プレイヤー戦では起きない",
                    Math.Abs(low - T("bond_evasion_bonus")) < 0.03 && high == 0 && highMiss == 0 && pvp == 0);
            }
        }
    }
}
