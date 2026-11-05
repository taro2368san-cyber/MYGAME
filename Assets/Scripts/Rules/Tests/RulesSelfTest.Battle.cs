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
            int Expected(BattleState b, int attacker, Attack atk, double mult, RoundResolved ev, double extra = 1) // extra：やけどの半減など、乱数のあとにかかる倍率
            {
                var A = b.Sides[attacker].Current; var D = b.Sides[1 - attacker].Current;
                var sa = Pokemons.Of(data, A); var sd = Pokemons.Of(data, D);
                bool physical = atk == Attack.Physical || (atk == Attack.Super && Pokemons.Stat(sa.Attack, A.Level) >= Pokemons.Stat(sa.SpAttack, A.Level));
                int a = (int)(Pokemons.Stat(physical ? sa.Attack : sa.SpAttack, A.Level) * Rank(b.Sides[attacker].Ranks[physical ? 0 : 1]));
                int d = Pokemons.Stat(physical ? sd.Defense : sd.SpDefense, D.Level);
                double random = T("damage_random_min") + (T("damage_random_max") - T("damage_random_min")) * (ev.Dice - 1) / 9;
                double damage = GameProcessor.BaseDamage(data, A.Level, a, d) * mult * (ev.Weakness ? T("weakness_mult") : 1) * (ev.Critical ? T("crit_mult") : 1) * random * extra;
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

            // ---- 7.1.1・6.6 判定勝ち・降参勝ちの経験値（相談6：場に出ていた相手1体を倒した扱い。前は0だった）----
            {
                int Exp(params int[] levels) => (int)(levels.Sum(l => T("exp_coef") * l * l) * T("exp_selected")); // 出場した子は1.0倍
                int Gained(List<GameEvent> ev, int player) => ev.OfType<ExpGained>().Where(x => x.Player == player).Sum(x => x.Exp);
                // クイックの判定勝ち：場に出ていた相手（Lv12）の分
                var (qg, qb, qp) = Fight("ヒトカゲ", 10, "ヒトカゲ", 12, BattleKind.Trainer);
                qb.Sides[0].Current.Hp = MaxHp(qb.Sides[0].Current) / 2;
                qb.Sides[1].Current.Hp = MaxHp(qb.Sides[1].Current) / 3;
                var qev = new List<GameEvent>();
                Call(qg, "Judge", qev);
                bool quick = qev.OfType<BattleEnded>().Any(x => x.Judged && x.Winner == 0) && Gained(qev, qp.Id) == Exp(12);
                // フルの判定勝ち：1体（Lv12）を倒してから、場に出ていた2体目（Lv14）。3体目（Lv16、出ていない）は数えない
                // 倒れていない数（3対2）で自分が勝つよう、自分の手持ちは3体
                bool full = false;
                var (f2, b2, p2) = Fight("ヒトカゲ", 30, "ヒトカゲ", 12, BattleKind.Trainer);
                b2.Full = true;
                p2.Party.Add(Pokemons.Create(data, "ゼニガメ", 30, "", "", 1)); p2.Party.Add(Pokemons.Create(data, "フシギダネ", 30, "", "", 1));
                b2.Sides[1].Team.Add(Pokemons.Create(data, "ゼニガメ", 14, "", "", 1));
                b2.Sides[1].Team.Add(Pokemons.Create(data, "フシギダネ", 16, "", "", 1));
                for (ulong seed = 1; seed <= 20 && !b2.Sides[1].Team[0].Fainted; seed++)
                {
                    b2.Sides[1].Current.Hp = 1;
                    b2.Attacker = 0; b2.First = 0; b2.AttackCmd = Attack.Physical; b2.DefenseCmd = Defense.SpecialGuard;
                    f2.State.Rng = seed * 7919;
                    Call(f2, "ResolveRound", new List<GameEvent>());
                }
                if (b2.Sides[1].Team[0].Fainted && b2.Sides[1].Current.Level == 14)
                {
                    var fev = new List<GameEvent>();
                    Call(f2, "Judge", fev); // 倒れていない数 3対2 で 自分の勝ち
                    // 経験値は 出場した子（1番）が1.0倍、出ていない2体が0.6倍。1番の分で比べる
                    full = fev.OfType<BattleEnded>().Any(x => x.Judged && x.Winner == 0) && fev.OfType<ExpGained>().Any(x => x.Player == p2.Id && x.Pokemon == "ヒトカゲ" && x.Exp == Exp(12, 14));
                }
                // プレイヤー戦の降参勝ち：降参した側の場に出ていた子（Lv10）の分。追いつきが起きる組み合わせ（勝った側が低い）では入らない
                List<GameEvent> Surrender(int winnerLv, int loserLv, out int winner)
                {
                    winner = -1;
                    var (pg, pb) = PvP("ヒトカゲ", 10, "ヒトカゲ", 10);
                    if (pb == null) return null;
                    var A = pb.Sides[pb.Attacker]; var D = pb.Sides[1 - pb.Attacker];
                    Pokemons.SetLevel(data, A.Current, winnerLv); Pokemons.SetLevel(data, D.Current, loserLv);
                    winner = A.Player;
                    Apply(pg, new ChooseAttack { Player = A.Player, Attack = Attack.Physical });
                    return Apply(pg, new ChooseDefense { Player = D.Player, Defense = Defense.Flee });
                }
                var sev = Surrender(14, 10, out int sw);
                var cev = Surrender(5, 10, out int cw);
                bool surrender = sev != null && sev.OfType<Fled>().Any(x => x.Surrender) && Gained(sev, sw) == Exp(10)
                    && cev != null && cev.OfType<LevelCaughtUp>().Any(x => x.Player == cw) && Gained(cev, cw) == 0;
                Check(log, "7.1.1・6.6 判定勝ち・降参勝ち：場に出ていた相手1体を倒した扱いで経験値が入る（クイックの判定、フルは倒した1体＋場に出ていた1体で 出ていない子は数えない、降参。追いつきが起きたら入らない）",
                    quick && full && surrender);
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

            // ---- 7.4 クールダウンは場に出ている子のもの（相談10-3）：交代・倒れて次の子・もう一度出てきた子は すぐ打てる ----
            {
                var (g, b, p) = Fight("ヒトカゲ", 30, "ヒトカゲ", 30, BattleKind.Trainer);
                b.Full = true;
                p.Party.Add(Pokemons.Create(data, "ゼニガメ", 30, "", "", 1));
                b.Sides[1].Team.Add(Pokemons.Create(data, "ゼニガメ", 30, "", "", 1));
                // 自分の攻撃の番にして、必殺が選べるか
                bool CanSuper()
                {
                    b.Step = BattleStep.Commands; b.Attacker = 0; b.AttackCmd = null; b.DefenseCmd = Defense.Guard;
                    return g.Validate(new ChooseAttack { Player = p.Id, Attack = Attack.Super }) == null;
                }
                void SuperNow() // 自分が必殺を打つ（倒れないように 打ったあと回復）
                {
                    b.Attacker = 0; b.First = 0; b.AttackCmd = Attack.Super; b.DefenseCmd = Defense.Guard;
                    Call(g, "ResolveRound", new List<GameEvent>());
                    Heal(b);
                }
                SuperNow();
                bool lockedBefore = !CanSuper();                                       // 交代しなければ 打てない
                Call(g, "SwitchOut", 0, true, new List<GameEvent>());                  // レッドカードと同じ交代
                bool newOne = b.Sides[0].Current.Species == "ゼニガメ" && CanSuper(); // 出てきた子は すぐ打てる
                SuperNow();
                Call(g, "SwitchOut", 0, true, new List<GameEvent>());
                bool backAgain = b.Sides[0].Current.Species == "ヒトカゲ" && CanSuper(); // 下がった子が もう一度出てきても 打てる
                // 必殺を打った子が倒れて、次の子が出たとき
                SuperNow();
                b.Sides[0].Current.Hp = 1;
                bool fainted = false;
                for (ulong seed = 1; seed <= 20 && !fainted; seed++)
                {
                    b.Sides[0].Current.Hp = 1;
                    b.Attacker = 1; b.First = 1; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.SpecialGuard;
                    g.State.Rng = seed * 7919;
                    Call(g, "ResolveRound", new List<GameEvent>());
                    fainted = b.Sides[0].Team[0].Fainted;
                }
                if (fainted && b.Step == BattleStep.Select) Apply(g, new SelectPokemon { Player = p.Id, Slot = 1 });
                bool afterFaint = fainted && b.Sides[0].Current.Species == "ゼニガメ" && CanSuper();
                Check(log, "7.4 クールダウンは場に出ている子のもの：交代しなければ打てないが、交代して出てきた子、下がってから もう一度出てきた子、必殺を打った子が倒れて出てきた子は すぐ打てる",
                    lockedBefore && newOne && backAgain && afterFaint);

                // すごろくの番をまたいでも残り、命令を流し直したセーブでも同じ。命令だけで遊ぶ（デバッグで Lv を上げ、硬くて力の弱い野生と戦う）
                // 2人（はじめの設定の確かめを通る名前）。相手の番は デバッグの「週を進める」でとばす（自分が先の番なので、とばすと すぐ自分の番＝バトルの続き）
                var tank = data.Species.Where(s => !s.Ability.StartsWith("へんしん")).OrderByDescending(s => s.Hp + s.Defense + s.SpDefense - 3 * Math.Max(s.Attack, s.SpAttack)).First(); // 必殺は こうげき・とくこうの高いほうを使う
                var cg = GameProcessor.NewGame(board, data, new[] { ("たろう", MoveMode.Highlight, "ヒトカゲ"), ("はなこ", MoveMode.Highlight, "ワニノコ") }, 30, 44, out _, new[] { "leaf", "gold" });
                cg.DebugEnabled = true;
                int me = cg.State.CurrentPlayer.Id;
                Apply(cg, new DebugCommand { Player = me, Action = DebugAction.LevelUp, Value = 45 });
                Apply(cg, new DebugCommand { Player = me, Action = DebugAction.Battle, Text = tank.Name, Value = 30 }); // Lv50 の自分が 6ラウンドは倒れない強さ
                bool supered = false, suspended = false, carried = false, flagged = false, replayed = false, freed = false;
                for (int i = 0; i < 60 && !freed; i++)
                {
                    List<GameEvent> ev;
                    if (cg.State.CurrentPlayer.Id != me) { ev = Apply(cg, new DebugCommand { Player = cg.State.CurrentPlayer.Id, Action = DebugAction.NextWeek }); continue; }
                    var cb = cg.State.Battle;
                    if (cb == null) break;
                    if (cb.Step == BattleStep.Ball) ev = Apply(cg, new ThrowBall { Player = me, Ball = null });
                    else if (cb.Step != BattleStep.Commands) break;
                    else if (cb.Sides[cb.Attacker].Player != me) ev = Apply(cg, new ChooseDefense { Player = me, Defense = Defense.Guard });
                    else if (!supered) { ev = Apply(cg, new ChooseAttack { Player = me, Attack = Attack.Super }); supered = true; }
                    else if (!carried)
                    {
                        // 必殺のあと はじめての 自分の攻撃の番：番をまたいでいて、まだ打てない
                        carried = suspended && cg.Validate(new ChooseAttack { Player = me, Attack = Attack.Super }) != null;
                        var loaded = GameProcessor.Load(board, data, SaveFile.Write(cg.State, data.Version), out _, out string err);
                        replayed = err == null && loaded?.State.Battle?.Sides[0].LastWasSuper == true && loaded.Validate(new ChooseAttack { Player = me, Attack = Attack.Super }) != null
                            && loaded.Validate(new ChooseAttack { Player = me, Attack = Attack.Physical }) == null;
                        ev = Apply(cg, new ChooseAttack { Player = me, Attack = Attack.Physical });
                    }
                    else { freed = cg.Validate(new ChooseAttack { Player = me, Attack = Attack.Super }) == null; break; } // その次は打てる
                    if (supered && ev.OfType<BattleSuspended>().Any()) suspended = true;
                    if (supered && !carried && ev.OfType<CommandsRequested>().LastOrDefault(x => x.AttackPlayer == me) is CommandsRequested cr) flagged = cr.SuperLocked;
                }
                Check(log, "7.4 クールダウン：必殺を打ったあと すごろくの番をまたいでも まだ打てず（画面の「打てない」も出る）、命令を流し直したセーブでも同じ。その次の攻撃では打てる",
                    supered && suspended && carried && flagged && replayed && freed);
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

            // ---- 7.9 イベントマスのクイックバトルの負け（相談1：HP1で残す）----
            {
                // 相手の攻撃で HP が0になって負けると、その子は HP1 で残る。所持金は1/3（切り上げ）、1回休みなし。
                // ほかの2体がバトルの前から戦闘不能でも 送還されない。やけどは残る（7.17）
                bool knocked = false;
                for (ulong seed = 1; seed <= 20 && !knocked; seed++)
                {
                    var (g, b, p) = Fight("ヒトカゲ", 10, "ヒトカゲ", 50, BattleKind.Event, seed);
                    var down1 = Pokemons.Create(data, "ゼニガメ", 10, "", "", 1); down1.Hp = 0;
                    var down2 = Pokemons.Create(data, "フシギダネ", 10, "", "", 1); down2.Hp = 0;
                    p.Party.Add(down1); p.Party.Add(down2);
                    var me = p.Party[0];
                    me.Hp = 1; me.Status = Status.Burn;
                    p.Money = 1001;
                    b.Attacker = 1; b.First = 1; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.SpecialGuard;
                    var ev = new List<GameEvent>();
                    Call(g, "ResolveRound", ev);
                    if (!ev.OfType<PokemonFainted>().Any(x => x.Side == 0)) continue; // 外れたら 種を変えて
                    int loss = (int)Math.Ceiling(1001 * T("defeat_money_ratio") - 1e-9);
                    knocked = ev.OfType<BattleEnded>().Any(x => x.Winner == 1) && me.Hp == 1 && !me.Fainted && me.Status == Status.Burn
                        && p.Money == 1001 - loss && !ev.OfType<RestNextTurn>().Any() && !ev.OfType<SentToCenter>().Any();
                    if (!knocked) break;
                }
                // 判定で負けたときは HP そのまま
                var (jg, jb, jp) = Fight("ヒトカゲ", 10, "ヒトカゲ", 10, BattleKind.Event); // バトルのあとに進化して HP が変わらないレベル
                int hp = (int)(MaxHp(jp.Party[0]) * 0.4);
                jp.Party[0].Hp = hp;
                jb.Sides[1].Current.Hp = (int)(MaxHp(jb.Sides[1].Current) * 0.5);
                var jev = new List<GameEvent>();
                Call(jg, "Judge", jev);
                bool judged = jev.OfType<BattleEnded>().Any(x => x.Judged && x.Winner == 1) && jp.Party[0].Hp == hp;
                Check(log, "7.9 イベントのクイックバトル：HPが0で負けると その子は HP1 で残り（やけどは残る）、所持金1/3を失うだけ（1回休み・送還なし。ほかの2体が戦闘不能でも）。判定の負けは HP そのまま",
                    knocked && judged);
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

            // ---- 7.17 状態異常の効果（やけど・まひ）と、1体に1つだけ ----
            {
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                var me = b.Sides[0].Current;
                // やけど：物理のダメージが半分（とくこうは そのまま）
                me.Status = Status.Burn;
                g.State.Rng = 88; Heal(b);
                var phys = Hit(g, b, 0, Attack.Physical, Defense.SpecialGuard);
                g.State.Rng = 88; Heal(b);
                var spec = Hit(g, b, 0, Attack.Special, Defense.Guard);
                bool burn = !phys.Missed && phys.Damage == Expected(b, 0, Attack.Physical, T("guard_wrong_mult"), phys, 0.5)
                    && !spec.Missed && spec.Damage == Expected(b, 0, Attack.Special, T("guard_wrong_mult"), spec);
                // まひ：すばやさが半分
                me.Status = Status.None;
                int fast = (int)Call(g, "BattleSpeed", b.Sides[0]);
                me.Status = Status.Paralysis;
                int slow = (int)Call(g, "BattleSpeed", b.Sides[0]);
                bool para = fast == Pokemons.Stat(Pokemons.Of(data, me).Speed, me.Level) && slow == (int)(fast * 0.5);
                // 1体に1つだけ：どくの子に やけどは つかない
                me.Status = Status.Poison;
                var ev = new List<GameEvent>();
                Call(g, "Inflict", 0, Status.Burn, ev, -1, null, true);
                bool one = me.Status == Status.Poison && !ev.OfType<StatusInflicted>().Any();
                Check(log, "7.17 状態異常：やけどは物理のダメージが半分（とくこうは そのまま）、まひは すばやさが半分、1体がかかるのは1つだけ", burn && para && one);
            }

            // ---- 7.13 砂あらし ----
            {
                // 攻守が1往復するたびに、闘タイプ以外は 最大HP×sand_damage（ドサイドンは闘）
                var (g, b, _) = Fight("ヒトカゲ", 50, "ドサイドン", 50, BattleKind.Trainer);
                b.Weather = "砂あらし";
                b.Rounds = 1; b.Attacker = 0; b.First = 0; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.Guard;
                var me = b.Sides[0].Current;
                var ev = new List<GameEvent>();
                Call(g, "ResolveRound", ev);
                var sand = ev.OfType<SandstormDamage>().ToList();
                Check(log, "7.13 砂あらし：1往復ごとに 闘タイプ以外は最大HP×sand_damage のダメージ、闘タイプは受けない",
                    sand.Count == 1 && sand[0].Side == 0 && sand[0].Damage == Math.Max(1, (int)(MaxHp(me) * T("sand_damage"))));
            }

            // ---- 7.3 にげる ----
            {
                List<GameEvent> Flee(BattleKind kind, bool full)
                {
                    var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, kind);
                    b.Full = full;
                    b.Attacker = 1; b.First = 1; b.AttackCmd = Attack.Physical; b.DefenseCmd = Defense.Flee;
                    var ev = new List<GameEvent>();
                    Call(g, "ResolveRound", ev);
                    return ev;
                }
                // ジム：にげられない（守る番に にげるを選べない）
                var (gg, gb, gp) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Gym);
                gb.Attacker = 1; gb.Step = BattleStep.Commands; gb.AttackCmd = Attack.Physical; gb.DefenseCmd = null;
                bool gym = Error(gg, new ChooseDefense { Player = gp.Id, Defense = Defense.Flee }).Contains("ジム");
                var quick = Flee(BattleKind.Trainer, false); var full = Flee(BattleKind.Rival, true);
                Check(log, "7.3 にげる：ジムからは にげられない。NPCのクイックバトルは お咎めなし（1回休みも お金もなし）、フルバトル（ライバルなど）から にげると1回休み",
                    gym && quick.OfType<Fled>().Any() && !quick.OfType<RestNextTurn>().Any() && !quick.OfType<MoneyChanged>().Any()
                    && full.OfType<Fled>().Any() && full.OfType<RestNextTurn>().Any(x => x.Player == 0));
            }

            // ---- 7.7 ポケモンの交換（プレイヤー戦の勝利報酬）----
            {
                // 同じか高いコストの子を出す。キャタピーを出して リザードンは取れない。相手の手持ちが1体だけなら 交換は選べない
                bool Trade(bool loserHasTwo, string give, out string err)
                {
                    err = null;
                    var (g, b) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                    if (b == null) return false;
                    var win = g.State.Players[b.Sides[0].Player]; var lose = g.State.Players[b.Sides[1].Player];
                    win.Party[0] = Pokemons.Create(data, give, give == "キャタピー" ? 99 : 36, "", "", 1);
                    lose.Party[0] = Pokemons.Create(data, "リザードン", 36, "", "", 1);
                    if (loserHasTwo) lose.Party.Add(Pokemons.Create(data, "ゼニガメ", 10, "", "", 1));
                    Apply(g, new DebugCommand { Player = win.Id, Action = DebugAction.WinBattle });
                    if (g.State.Battle?.Step != BattleStep.Reward) return false;
                    err = g.Validate(new ChooseReward { Player = win.Id, Reward = Reward.Trade, Take = 0, Give = 0 });
                    return true;
                }
                bool a = Trade(true, "キャタピー", out string cheap), c = Trade(true, "リザードン", out string same), d = Trade(false, "リザードン", out string alone);
                Check(log, "7.7 交換：Lv99のキャタピーを出して リザードンは取れない（コスト）。同じコストなら取れる。相手の手持ちが1体だけなら 交換できない",
                    a && c && d && cheap != null && cheap.Contains("コスト") && same == null && alone != null && alone.Contains("1体"));
            }

            // ---- 7.17 ねむり中は 守る番も止まり、1倍で受ける。ねむりの残りはラウンドごとに数える（相談10-2）----
            {
                // 1倍：物理・特殊・必殺の どれでも 7.3 の倍率をかけない。必殺は反射しない
                var (g, b, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                var bad = new List<string>();
                foreach (var atk in new[] { Attack.Physical, Attack.Special, Attack.Super })
                {
                    g.State.Rng = 4321;
                    RoundResolved ev; int tries = 0;
                    do { Heal(b); ev = Hit(g, b, 0, atk, Defense.Asleep); } while (ev.Missed && ++tries < 50);
                    var A = b.Sides[0].Current;
                    if (ev.Missed || ev.Guarded || ev.Reflected || A.Hp != MaxHp(A) || ev.Damage != Expected(b, 0, atk, 1, ev)) bad.Add(atk.ToString());
                }
                // 守っていないので 急所は起こる。技の追加効果も（ガード失敗と同じく）返る
                g.State.Rng = 99;
                int crits = 0;
                for (int i = 0; i < 400; i++) { Heal(b); if (Hit(g, b, 0, Attack.Physical, Defense.Asleep).Critical) crits++; }
                var withFx = data.Species.FirstOrDefault(s => s.PhysicalEffect != null && !s.Ability.StartsWith("へんしん"));
                bool fx = false;
                if (withFx != null)
                {
                    var (xg, xb, _) = Fight(withFx.Name, 50, "ヒトカゲ", 50, BattleKind.Trainer);
                    for (int i = 0; i < 20 && !fx; i++) { Heal(xb); xb.Attacker = 0; xb.First = 0; var ev = new RoundResolved(); fx = Call(xg, "Hit", xb.Sides[0], xb.Sides[1], Attack.Physical, Defense.Asleep, ev) != null; }
                }
                // プレイヤー戦：守る側がねむっていると 防御のコマンドは断られ、攻撃側のコマンドだけで そのラウンドが進む
                var (pg, pb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                bool pvp = false, countdown = false;
                if (pb != null)
                {
                    var A = pb.Sides[pb.Attacker]; var D = pb.Sides[1 - pb.Attacker];
                    D.Current.Status = Status.Sleep; D.Current.SleepRounds = 3;
                    Call(pg, "BeginCommands", new List<GameEvent>()); // 守る側がねむった状態で コマンドを待ち直す
                    string refused = pg.Validate(new ChooseDefense { Player = D.Player, Defense = Defense.Guard });
                    var ev = Apply(pg, new ChooseAttack { Player = A.Player, Attack = Attack.Physical });
                    pvp = refused != null && refused.Contains("ねむって") && ev.OfType<RoundResolved>().Any(x => x.Defense == Defense.Asleep);
                }
                // ねむりの残りは 守る番（このテストの最初の1回）・攻撃の番・守る番 の両方で1つずつ減り、決めた長さ（3）で目を覚ます
                var (cg, cb) = PvP("ヒトカゲ", 50, "ヒトカゲ", 50);
                if (cb != null)
                {
                    var sleeper = cb.Sides[0].Current;
                    sleeper.Status = Status.Sleep; sleeper.SleepRounds = 3;
                    var left = new List<int>();
                    void Round(int attacker, Defense def)
                    {
                        cb.Attacker = attacker; cb.First = attacker; cb.AttackCmd = Attack.Physical; cb.DefenseCmd = def;
                        Call(cg, "ResolveRound", new List<GameEvent>());
                        Heal(cb);
                        left.Add(sleeper.Status == Status.Sleep ? sleeper.SleepRounds : 0);
                    }
                    Round(1, Defense.Asleep);   // 守る番
                    Round(0, Defense.Guard);    // 攻撃の番（動けない）
                    Round(1, Defense.Asleep);   // 守る番 → 目を覚ます
                    countdown = left.SequenceEqual(new[] { 2, 1, 0 }) && sleeper.Status == Status.None;
                }
                // NPC の攻撃を ねむっている人が受けるときは、だれの操作も待たずに そのラウンドが進む
                var (ng, nb, _) = Fight("ヒトカゲ", 50, "ヒトカゲ", 50, BattleKind.Trainer);
                nb.Sides[0].Current.Status = Status.Sleep; nb.Sides[0].Current.SleepRounds = 3;
                nb.Attacker = 1; nb.TurnRounds = 0;
                var nev = new List<GameEvent>();
                Call(ng, "BeginCommands", nev);
                bool npc = nev.OfType<RoundResolved>().Any(x => x.Attacker == 1 && x.Defense == Defense.Asleep);
                Check(log, "7.17 ねむり：守る番も止まり（防御のコマンドは断られ、相手のコマンドだけで進む。NPCの攻撃なら そのまま進む）、物理・特殊・必殺とも1倍で受け、反射しない。急所・追加効果は起こる。ねむりの残りは 攻撃の番と守る番の両方で減る" + (bad.Count > 0 ? "（合わない：" + string.Join("、", bad) + "）" : ""),
                    bad.Count == 0 && crits > 0 && (withFx == null || fx) && pvp && countdown && npc);
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

            BattleFlowRules(data, log);
        }

        // バトルの流れ（7.1・7.1.2・7.1.3・6.1）。番をまたぐので テスト用の小さな輪（1スタート─2センター─3何もないマス）で組む
        static void BattleFlowRules(GameData data, List<string> log)
        {
            if (new[] { "カビゴン", "マルマイン", "リザードン" }.Any(n => data.FindSpecies(n) == null)) return;
            double T(string key) => data.Required(key);
            int MaxHp(PokemonState m) => Pokemons.MaxHp(data, m);
            PokemonState Mon(string species, int level) => Pokemons.Create(data, species, level, "", "", 1);
            var ring = BoardData.Create(new[] {
                new BoardSquare { Id = 1, Name = "f1", Type = "スタート", Place = "マサラタウン", Area = "エリア1" },
                new BoardSquare { Id = 2, Name = "f2", Type = "ポケモンセンター", Place = "トキワシティ", Area = "エリア1" },
                new BoardSquare { Id = 3, Name = "f3", Type = "ワープ", Place = "マサラタウン", Area = "エリア1" } }, new[] { (1, 2), (2, 3), (3, 1) }, new List<string>());
            GameProcessor Game(int players)
            {
                var g = GameProcessor.NewGame(ring, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ヒトカゲ") }.Take(players).ToList(), 50, 9, out _);
                g.State.NextEventWeek = 9999;
                g.DebugEnabled = true;
                return g;
            }
            // NPC とのバトルを処理役の入口から始める（kind・フルかどうか・相手の手持ち）
            List<GameEvent> Start(GameProcessor g, BattleKind kind, bool full, params (string, int)[] team)
            {
                var ev = new List<GameEvent>();
                Call(g, "StartBattle", kind, full, (BattleSide)Call(g, "Npc", "テスト", team), ev, null, -1);
                return ev;
            }
            // 1ラウンド：自分が攻撃側なら ぶつり、防御側なら ぼうぎょ。倒れないように 先に両方を回復しておく
            List<GameEvent> Round(GameProcessor g, PlayerState me)
            {
                var b = g.State.Battle;
                foreach (var s in b.Sides) s.Current.Hp = MaxHp(s.Current);
                return b.Sides[b.Attacker].Player == me.Id ? Apply(g, new ChooseAttack { Player = me.Id, Attack = Attack.Physical }) : Apply(g, new ChooseDefense { Player = me.Id, Defense = Defense.Guard });
            }
            // カードの枚数（7.1.2）：速い側の先攻カード 1〜3枚＋遅い側1枚。すばやさは 処理役と同じ数え方（BattleSpeed）
            int Cards(GameProcessor g)
            {
                var b = g.State.Battle;
                int s0 = (int)Call(g, "BattleSpeed", b.Sides[0]), s1 = (int)Call(g, "BattleSpeed", b.Sides[1]);
                double ratio = (double)Math.Max(s0, s1) / Math.Max(1, Math.Min(s0, s1));
                return (ratio >= T("initiative_ratio_large") ? 3 : ratio >= T("initiative_ratio_small") ? 2 : 1) + 1;
            }

            // ---- 7.1.2 交代したら カードを引き直す ----
            {
                var g = Game(1);
                var me = g.State.CurrentPlayer;
                me.Party.Clear();
                me.Party.Add(Mon("カビゴン", 50)); me.Party.Add(Mon("マルマイン", 50)); me.Party.Add(Mon("カビゴン", 50));
                Start(g, BattleKind.Trainer, true, ("カビゴン", 50), ("マルマイン", 50));
                var b = g.State.Battle;
                // フルバトルの最初の選出 → カード（同じ子どうしなので 先攻カード1枚＋1枚）
                var ev = g.Apply(new SelectPokemon { Player = me.Id, Slot = 0 }, out _);
                bool first = b.Step == BattleStep.Initiative && b.Cards.Count == Cards(g) && b.Cards.Count == 2 && ev.OfType<CardsDealt>().Single().Player == me.Id;
                Apply(g, new PickCard { Player = me.Id, Card = 0 });
                // 1体目が倒れる（どく＋HP1。自分の攻撃のラウンドの終わりに倒れる。相手の攻撃で倒れてもよい）
                me.Party[0].Status = Status.Poison;
                for (int i = 0; i < 4 && b.Step == BattleStep.Commands; i++)
                {
                    b.Sides[1].Current.Hp = MaxHp(b.Sides[1].Current);
                    me.Party[0].Hp = 1;
                    if (b.Sides[b.Attacker].Player == me.Id) Apply(g, new ChooseAttack { Player = me.Id, Attack = Attack.Physical });
                    else Apply(g, new ChooseDefense { Player = me.Id, Defense = Defense.Guard });
                }
                bool fainted = b.Step == BattleStep.Select && me.Party[0].Fainted && g.Validate(new SelectPokemon { Player = me.Id, Slot = 0 }) != null;
                ev = g.Apply(new SelectPokemon { Player = me.Id, Slot = 1 }, out _);
                int after = b.Cards?.Count ?? 0;
                bool redraw = b.Step == BattleStep.Initiative && after == Cards(g) && after != 2 && ev.OfType<CardsDealt>().Single().Player == me.Id;
                // 遅い側（相手）のカードを引くと 相手から攻撃が始まり直す
                int foeCard = b.Cards.IndexOf(1);
                Apply(g, new PickCard { Player = me.Id, Card = foeCard });
                bool restart = b.First == 1 && b.Attacker == 1 && b.Step == BattleStep.Commands;
                Check(log, $"7.1.2 カードの引き直し：フルバトルで倒れて次の子を出すと カードを引く場面になり、新しい2体のすばやさで枚数が決まり直す（2枚→{after}枚）。引くのは仕掛けた側（プレイヤー）。引いたカードの側から攻撃が始まり直す",
                    first && fainted && redraw && restart);

                // 脱出ボタン（自分で選んで交代）・レッドカード（相手をランダムに交代）でも 引き直す
                ev = new List<GameEvent>();
                Call(g, "SwitchOut", 0, false, ev);
                bool choose = b.Step == BattleStep.Select && g.Validate(new SelectPokemon { Player = me.Id, Slot = 1 }) != null; // 交代させられた子は選べない
                ev = g.Apply(new SelectPokemon { Player = me.Id, Slot = 2 }, out _);
                bool button = b.Step == BattleStep.Initiative && b.Cards.Count == Cards(g) && ev.OfType<CardsDealt>().Single().Player == me.Id;
                Apply(g, new PickCard { Player = me.Id, Card = 0 });
                ev = new List<GameEvent>();
                Call(g, "SwitchOut", 1, true, ev);
                bool red = b.Sides[1].Current.Species == "マルマイン" && b.Step == BattleStep.Initiative && b.Cards.Count == Cards(g) && b.Cards.Count != 2 && ev.OfType<CardsDealt>().Single().Player == me.Id;
                Check(log, "7.1.2 カードの引き直し：脱出ボタン（自分で選ぶ交代）でも レッドカード（相手のランダムな交代）でも 新しい子どうしで カードを引き直す。引くのは仕掛けた側",
                    choose && button && red);
            }

            // ---- 7.1.3・7.1 野生戦のボールの確認と、番の交代 ----
            {
                var g = Game(2);
                var me = g.State.CurrentPlayer; var other = g.State.Players.First(x => x != me);
                me.Party[0] = Mon("カビゴン", 60);
                me.Balls["モンスターボール"] = 5;
                Apply(g, new DebugCommand { Player = me.Id, Action = DebugAction.Battle, Text = "リザードン", Value = 50 }); // HP満タンのリザードン Lv50 は モンスターボールでは捕まらない（6.3 のテスト）
                var b = g.State.Battle;
                var ev = new List<GameEvent>();
                int rounds = 0;
                for (; rounds < 4 && b.Step == BattleStep.Commands; rounds++) ev.AddRange(Round(g, me));
                bool prompt = b.Rounds == 2 && b.Step == BattleStep.Ball && ev.OfType<BallPrompt>().Count() == 1 && !ev.OfType<BattleSuspended>().Any();
                int attacker = b.Attacker;
                foreach (var s in b.Sides) s.Current.Hp = MaxHp(s.Current);
                int myHp = me.Party[0].Hp;
                ev = Apply(g, new ThrowBall { Player = me.Id, Ball = "モンスターボール" });
                bool missed = ev.OfType<BallThrown>().Single().Caught == false && !ev.OfType<RoundResolved>().Any() && me.Party[0].Hp == myHp
                    && ev.OfType<BattleSuspended>().Any(x => x.Player == me.Id) && g.State.CurrentPlayer == other && me.PendingBattle == b;
                // 相手の番：センターに止まって出る → 自分の番は サイコロなしで バトルの続き
                other.Square = 3; other.FixedDice = 1;
                Apply(g, new RollDice { Player = other.Id });
                Apply(g, new ChooseDestination { Player = other.Id, Square = 2 });
                Apply(g, new ConfirmMove { Player = other.Id });
                ev = g.State.Phase == Phase.Facility ? Apply(g, new LeaveFacility { Player = other.Id }) : new List<GameEvent>();
                bool resumed = ev.OfType<BattleResumed>().Any(x => x.Player == me.Id) && g.State.CurrentPlayer == me && g.State.Phase == Phase.Battle && g.State.Battle == b
                    && b.Step == BattleStep.Commands && g.Validate(new RollDice { Player = me.Id }) != null;
                // 投げても 自分の攻撃の機会は減らない：ボールの前と同じ側から 攻撃が続く（交互のまま）
                bool kept = b.Attacker == attacker && ev.OfType<CommandsRequested>().LastOrDefault()?.Attacker == attacker;
                // 投げないと そのまま番が次の人へ
                ev = new List<GameEvent>();
                for (rounds = 0; rounds < 4 && b.Step == BattleStep.Commands; rounds++) ev.AddRange(Round(g, me));
                bool again = b.Rounds == 4 && b.Step == BattleStep.Ball;
                ev = Apply(g, new ThrowBall { Player = me.Id, Ball = null });
                bool passed = again && ev.OfType<BattleSuspended>().Any(x => x.Player == me.Id) && g.State.CurrentPlayer == other;
                Check(log, $"7.1.3・7.1 野生戦：攻守が1往復すると「ボールを投げる？」。失敗しても 野生の追加の攻撃はなく、番が次の人へ。次の自分の番は サイコロなしで バトルの続き（ボールの前と同じ側から攻撃）。投げなくても 番が次の人へ（上限 {T("round_limit_quick")}ラウンド）",
                    prompt && missed && resumed && kept && passed);

                // トレーナー戦では 往復のあとに ボールの確認は出ない
                var tg = Game(1);
                var tp = tg.State.CurrentPlayer;
                tp.Party[0] = Mon("カビゴン", 60);
                tp.Balls["モンスターボール"] = 5;
                Start(tg, BattleKind.Trainer, false, ("リザードン", 50));
                if (tg.State.Battle?.Step == BattleStep.Initiative) Apply(tg, new PickCard { Player = tp.Id, Card = 0 });
                var tev = new List<GameEvent>();
                for (int i = 0; i < 2 && tg.State.Battle?.Step == BattleStep.Commands; i++) tev.AddRange(Round(tg, tp));
                Check(log, "7.1.3 トレーナー戦：攻守が1往復しても ボールの確認は出ず、そのまま番が終わる",
                    tev.OfType<RoundResolved>().Count() == 2 && !tev.OfType<BallPrompt>().Any() && tev.OfType<BattleSuspended>().Any());
            }

            // ---- 6.1 先頭固定のバトルで 先頭が戦闘不能なら、出す子を選ぶ ----
            {
                bool Picks(Func<GameProcessor, PlayerState, List<GameEvent>> start)
                {
                    var g = Game(1);
                    var p = g.State.CurrentPlayer;
                    p.Party[0].Hp = 0; p.Party.Add(Mon("カビゴン", 30));
                    var ev = start(g, p);
                    var b = g.State.Battle;
                    bool asked = b?.Step == BattleStep.Select && ev.OfType<SelectRequested>().Any(x => x.Player == p.Id);
                    bool noFainted = asked && g.Validate(new SelectPokemon { Player = p.Id, Slot = 0 }) != null;
                    if (asked) Apply(g, new SelectPokemon { Player = p.Id, Slot = 1 });
                    return noFainted && b.Step != BattleStep.Select && b.Sides[0].Active == 1;
                }
                bool wild = Picks((g, p) => Apply(g, new DebugCommand { Player = p.Id, Action = DebugAction.Battle, Text = "コラッタ", Value = 5 }));
                bool trainer = Picks((g, p) => Start(g, BattleKind.Trainer, false, ("コラッタ", 5)));
                bool job = Picks((g, p) => Start(g, BattleKind.Job, false, ("コラッタ", 5)));
                // 先頭が元気なら 選ばずに先頭が出る
                var hg = Game(1);
                Start(hg, BattleKind.Trainer, false, ("コラッタ", 5));
                bool lead = hg.State.Battle.Step != BattleStep.Select && hg.State.Battle.Sides[0].Active == 0;
                Check(log, "6.1 先頭固定のバトル（野生・トレーナー・職業トレーナー）で 先頭が戦闘不能なら、出す子を選ぶ場面になる。戦闘不能の子は選べず、元気な子を選ぶと進む。先頭が元気なら選ばない",
                    wild && trainer && job && lead);
            }

            // ---- 相談24：フルバトルの 守る番に 自分で交代する（7.1.2・15.4）----
            {
                // 自分が守る番まで進める：手持ちは カビゴン・マルマイン・カビゴン、相手（NPC）は カビゴン Lv30 2体。相手のカードを引いて 相手から攻撃
                (GameProcessor g, PlayerState me, BattleState b) Defending(bool full, ulong rng = 0, int foeLevel = 30)
                {
                    var g = Game(1);
                    var me = g.State.CurrentPlayer;
                    me.Party.Clear();
                    me.Party.Add(Mon("カビゴン", 50)); me.Party.Add(Mon("マルマイン", 50)); me.Party.Add(Mon("カビゴン", 50));
                    if (rng != 0) g.State.Rng = rng;
                    Start(g, BattleKind.Trainer, full, ("カビゴン", foeLevel), ("カビゴン", foeLevel));
                    var b = g.State.Battle;
                    if (b.Step == BattleStep.Select) g.Apply(new SelectPokemon { Player = me.Id, Slot = 0 }, out _);
                    if (b.Step == BattleStep.Initiative) g.Apply(new PickCard { Player = me.Id, Card = Math.Max(0, b.Cards.IndexOf(1)) }, out _);
                    return (g, me, b);
                }
                void Heal(BattleState hb) { foreach (var s in hb.Sides) s.Current.Hp = MaxHp(s.Current); }
                List<GameEvent> Raw(GameProcessor g, Command c) { var ev = g.Apply(c, out string err); if (err != null) throw new Exception("テスト失敗: " + c.GetType().Name + " が断られた：" + err); return ev; }

                // 出てきた子が 相手の攻撃を受ける（物理・特殊・必殺）。交代が先、ガードなし、反射なし
                bool takes = true;
                foreach (var atk in new[] { Attack.Physical, Attack.Special, Attack.Super })
                {
                    var (g, me, b) = Defending(true);
                    b.AttackCmd = atk;
                    var old = me.Party[0]; var target = me.Party[1];
                    int oldHp = old.Hp, maxHp = MaxHp(target);
                    var ev = Raw(g, new SwitchPokemon { Player = me.Id, Slot = 1 });
                    var rr = ev.OfType<RoundResolved>().Single();
                    int swAt = ev.FindIndex(e => e is PokemonSwitched), rrAt = ev.IndexOf(rr);
                    takes &= swAt >= 0 && swAt < rrAt && b.Sides[0].Active == 1 && rr.Defense == Defense.Switch && rr.DefenderPokemon == target.Species
                        && !rr.Guarded && !rr.Reflected && old.Hp == oldHp && (rr.Missed || target.Hp == Math.Max(0, maxHp - rr.Damage));
                }
                // 1倍：同じ乱数なら ねむっていて守れないとき（相談10-2、1倍）と 同じダメージ
                bool oneX = true;
                foreach (var atk in new[] { Attack.Physical, Attack.Special, Attack.Super })
                {
                    var (g, me, b) = Defending(true);
                    b.Attacker = 1;
                    int compared = 0;
                    for (ulong seed = 1; seed < 60 && compared < 3; seed++)
                    {
                        Heal(b);
                        g.State.Rng = seed * 7919;
                        var e1 = new RoundResolved(); Call(g, "Hit", b.Sides[1], b.Sides[0], atk, Defense.Switch, e1);
                        Heal(b);
                        g.State.Rng = seed * 7919;
                        var e2 = new RoundResolved(); Call(g, "Hit", b.Sides[1], b.Sides[0], atk, Defense.Asleep, e2);
                        if (e1.Missed || e2.Missed) continue;
                        compared++;
                        oneX &= e1.Damage == e2.Damage && !e1.Guarded && !e1.Reflected;
                    }
                    oneX &= compared == 3;
                }
                Check(log, "相談24 交代：フルバトルの守る番に交代すると、交代が先で、出てきた子が 相手の攻撃（物理・特殊・必殺）を受ける。ガードなしの1倍（ねむりと同じ）で、必殺でも反射しない。下がった子は 傷つかない",
                    takes && oneX);

                // 交代のあと 新しい2体でカードを引き直す。引くのは仕掛けた側。下がった子のクールダウン・能力ランクは消え、やけどは残る。ラウンド上限に数える
                {
                    // 同じ Lv のカビゴンどうし（2枚）から、すばやいマルマインに交代（出てきた子が倒れない乱数で）
                    bool redraw = false, reset = false, counted = false; int before = 0, after = 0;
                    for (ulong seed = 1; seed <= 20 && !redraw; seed++)
                    {
                        var (g, me, b) = Defending(true, seed * 7919, 50);
                        b.AttackCmd = Attack.Physical;
                        var mine = b.Sides[0];
                        before = Cards(g);
                        mine.Ranks[0] = 1; mine.Ranks[2] = -1; mine.LastWasSuper = true; me.Party[0].Status = Status.Burn;
                        int rounds = b.Rounds;
                        var ev = Raw(g, new SwitchPokemon { Player = me.Id, Slot = 1 });
                        if (me.Party[1].Fainted) continue;
                        after = b.Cards?.Count ?? 0;
                        redraw = b.Step == BattleStep.Initiative && after == Cards(g) && after != before && ev.OfType<CardsDealt>().Single().Player == me.Id;
                        reset = mine.Ranks.All(r => r == 0) && !mine.LastWasSuper && me.Party[0].Status == Status.Burn;
                        counted = b.Rounds == rounds + 1;
                    }
                    // ラウンド上限のひとつ手前で交代すると、そのラウンドで判定になる
                    var (lg, lme, lb) = Defending(true);
                    lb.AttackCmd = Attack.Physical;
                    lb.Rounds = (int)T("round_limit_full") - 1;
                    var lev = Raw(lg, new SwitchPokemon { Player = lme.Id, Slot = 1 });
                    bool limit = lev.OfType<BattleEnded>().Any(x => x.Judged) && lev.OfType<PokemonSwitched>().Any();
                    Check(log, $"相談24 交代：そのあと 新しい2体でカードを引き直す（{before}枚→{after}枚）。引くのは仕掛けた側。下がった子の能力ランクと必殺のクールダウンは消え、やけどは残る。交代したラウンドも ラウンド上限に数える",
                        redraw && reset && counted && limit);
                }

                // 断られるとき：クイックバトル、攻撃の番、たおれた子、いま出ている子、ねむっている子の守る番。ChooseDefense で こうたいは選べない
                {
                    var (qg, qme, _) = Defending(false);
                    bool quick = qg.Validate(new SwitchPokemon { Player = qme.Id, Slot = 1 }) != null;
                    var ag = Game(1); var ame = ag.State.CurrentPlayer;
                    ame.Party.Clear(); ame.Party.Add(Mon("カビゴン", 50)); ame.Party.Add(Mon("マルマイン", 50));
                    Start(ag, BattleKind.Trainer, true, ("カビゴン", 30));
                    var ab = ag.State.Battle;
                    ag.Apply(new SelectPokemon { Player = ame.Id, Slot = 0 }, out _);
                    ag.Apply(new PickCard { Player = ame.Id, Card = Math.Max(0, ab.Cards.IndexOf(0)) }, out _); // 自分から攻撃
                    bool attackTurn = ab.Attacker == 0 && ag.Validate(new SwitchPokemon { Player = ame.Id, Slot = 1 }) != null;
                    var (g, me, b) = Defending(true);
                    me.Party[1].Hp = 0;
                    bool fainted = g.Validate(new SwitchPokemon { Player = me.Id, Slot = 1 }) != null && g.Validate(new SwitchPokemon { Player = me.Id, Slot = 2 }) == null;
                    bool active = g.Validate(new SwitchPokemon { Player = me.Id, Slot = 0 }) != null;
                    bool viaDefense = g.Validate(new ChooseDefense { Player = me.Id, Defense = Defense.Switch }) != null;
                    b.DefenseCmd = Defense.Asleep; // ねむっている子の守る番（処理役が入れる）
                    bool asleep = g.Validate(new SwitchPokemon { Player = me.Id, Slot = 2 }) != null;
                    Check(log, "相談24 交代：クイックバトル・攻撃の番・たおれた子へ・いま出ている子へ・ねむっている子の守る番では 断られる。守りのコマンド（ChooseDefense）では こうたいを選べない",
                        quick && attackTurn && fainted && active && viaDefense && asleep);
                }

                // 出てきた子が その攻撃で倒れると、次の子を選ぶ場面になる
                {
                    bool chose = false;
                    for (ulong seed = 1; seed <= 20 && !chose; seed++)
                    {
                        var (g, me, b) = Defending(true, seed * 104729);
                        b.AttackCmd = Attack.Physical;
                        me.Party[1].Hp = 1;
                        var ev = Raw(g, new SwitchPokemon { Player = me.Id, Slot = 1 });
                        if (ev.OfType<RoundResolved>().Single().Missed) continue;
                        chose = me.Party[1].Fainted && b.Step == BattleStep.Select && b.Sides[0].NeedsSelect
                            && g.Validate(new SelectPokemon { Player = me.Id, Slot = 1 }) != null && g.Validate(new SelectPokemon { Player = me.Id, Slot = 2 }) == null;
                    }
                    Check(log, "相談24 交代：出てきた子が その攻撃で倒れると、次の子を選ぶ場面になる（倒れた子は選べない）", chose);
                }

                // プレイヤー戦（フル。いまは 同じマスのプレイヤー戦はクイックなので 起きないが、フルのプレイヤー戦でも 相手に見えないこと）：交代を選んでも、相手の攻撃が決まるまで 結果に出ない
                {
                    var g = Game(2);
                    var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                    me.Party.Clear(); me.Party.Add(Mon("カビゴン", 50)); me.Party.Add(Mon("マルマイン", 50));
                    you.Party.Clear(); you.Party.Add(Mon("カビゴン", 50)); you.Party.Add(Mon("マルマイン", 50));
                    Call(g, "StartBattle", BattleKind.Player, true, new BattleSide { Player = you.Id, Name = you.Name, Team = you.Party }, new List<GameEvent>(), null, -1);
                    var b = g.State.Battle;
                    g.Apply(new SelectPokemon { Player = me.Id, Slot = 0 }, out _);
                    g.Apply(new SelectPokemon { Player = you.Id, Slot = 0 }, out _);
                    g.Apply(new PickCard { Player = me.Id, Card = Math.Max(0, b.Cards.IndexOf(1)) }, out _); // 相手から攻撃
                    bool defending = b.Step == BattleStep.Commands && b.Attacker == 1;
                    var e1 = Raw(g, new SwitchPokemon { Player = me.Id, Slot = 1 });
                    bool hidden = !e1.OfType<PokemonSwitched>().Any() && !e1.OfType<RoundResolved>().Any() && b.Sides[0].Active == 0;
                    var e2 = Raw(g, new ChooseAttack { Player = you.Id, Attack = Attack.Physical });
                    int sw = e2.FindIndex(e => e is PokemonSwitched), rr = e2.FindIndex(e => e is RoundResolved);
                    Check(log, "相談24 交代：プレイヤー戦で 交代を選んでも、相手の攻撃が決まるまで 結果に出ない（決まると「こうたいした」→ 攻撃の結果）",
                        defending && hidden && sw >= 0 && rr > sw);
                }
            }
        }
    }
}
