using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // ルールの自己テスト。小さな盤面で移動・手番・週・乱数を確かめ、失敗したら例外を投げる
    //
    //  1(スタート)─2─3─4(行き止まり)        10─11（ほかとつながっていない短い道）
    //                │
    //                5─6─7(バッジ1個で解放)
    //                │
    //                8(ワープ1) ……ワープ……> 9(ワープ1、道なし)
    public static class RulesSelfTest
    {
        public static List<string> Run()
        {
            var log = new List<string>();
            var board = TestBoard();

            Check(log, "ちょうど3歩", Same(Movement.Reachable(board, 1, 3, 0, false), 4, 5));
            Check(log, "直前のマスに戻らない（1から2歩で1に戻らない）", Same(Movement.Reachable(board, 1, 2, 0, false), 3));
            Check(log, "手番の最初の1歩はどちらへも進める", Same(Movement.Reachable(board, 2, 1, 0, false), 1, 3));
            Check(log, "行き止まりの先には進めない", Same(Movement.Reachable(board, 3, 2, 0, false), 1, 6, 8));
            Check(log, "止まれるマスがないとき", Movement.Reachable(board, 10, 3, 0, false).Count == 0);
            Check(log, "折り返しを数えると止まれる", Same(Movement.Reachable(board, 10, 3, 0, true), 11));
            Check(log, "未解放のマスは通れない", Same(Movement.Reachable(board, 5, 2, 0, false), 2, 4));
            Check(log, "バッジがあれば通れる", Same(Movement.Reachable(board, 5, 2, 1, false), 2, 4, 7));
            Check(log, "距離：1から4は3マス、未解放の7とワープの先の9は道では行けない", Movement.Distance(board, 1, 4, 0) == 3 && Movement.Distance(board, 1, 7, 0) == -1 && Movement.Distance(board, 1, 7, 1) == 5 && Movement.Distance(board, 1, 9, 0) == -1);

            // 1マスずつ方式（出目3）
            var game = NewGame(board, 3, MoveMode.Step);
            Apply(game, new RollDice());
            Check(log, "1マスずつ：出目3で止まれるのは4と5", Same(new HashSet<int>(game.State.Move.Reachable), 4, 5));
            Apply(game, new StepTo { Square = 2 });
            Apply(game, new StepTo { Square = 1 }); // 考え直し
            Check(log, "考え直しで歩数が戻る", game.State.Move.Path.SequenceEqual(new[] { 1 }));
            Check(log, "となりでないマスには進めない", Error(game, new StepTo { Square = 3 }) != null);
            Apply(game, new StepTo { Square = 2 });
            Apply(game, new StepTo { Square = 3 });
            Check(log, "歩数が残っているうちは確定できない", Error(game, new ConfirmMove()) != null);
            Apply(game, new StepTo { Square = 4 });
            var events = Apply(game, new ConfirmMove());
            Check(log, "確定すると4に止まる", events.OfType<Landed>().Single().Square == 4 && game.State.Players[0].Square == 4);

            // 行き詰まる道は選べない（出目4で 1→2→3→4 は止まれない）
            game = NewGame(board, 4, MoveMode.Step);
            Apply(game, new RollDice());
            Apply(game, new StepTo { Square = 2 });
            Apply(game, new StepTo { Square = 3 });
            Check(log, "ちょうど止まれない道には進めない", Error(game, new StepTo { Square = 4 }) != null);

            // ハイライト方式とワープ（出目4で8に止まると9へ）
            game = NewGame(board, 4, MoveMode.Highlight);
            Apply(game, new RollDice());
            Check(log, "ハイライト：止まれないマスは選べない", Error(game, new ChooseDestination { Square = 4 }) != null);
            Apply(game, new ChooseDestination { Square = 8 });
            Check(log, "ハイライト：道順が自動で決まる", game.State.Move.Path.SequenceEqual(new[] { 1, 2, 3, 5, 8 }));
            events = Apply(game, new ConfirmMove());
            Check(log, "ワープに止まると相手のマスへ", events.OfType<Warped>().Any(w => w.From == 8 && w.To == 9) && events.OfType<Landed>().Single().Square == 9);

            // 手番と週（2人・1週で終わる）
            game = NewGame(board, 3, MoveMode.Highlight, players: 2, weeks: 1);
            int first = game.State.CurrentPlayer.Id;
            Check(log, "手番でない人の命令は受け付けない", Error(game, new RollDice { Player = 1 - first }) != null);
            Apply(game, new RollDice { Player = first });
            Apply(game, new ChooseDestination { Player = first, Square = 4 });
            Apply(game, new ConfirmMove { Player = first });
            Check(log, "次の人の手番になる", game.State.CurrentPlayer.Id == 1 - first);
            Apply(game, new RollDice { Player = 1 - first });
            Apply(game, new ChooseDestination { Player = 1 - first, Square = 5 });
            events = Apply(game, new ConfirmMove { Player = 1 - first });
            Check(log, "全員動いて週が終わり、最終週ならゲーム終了", events.OfType<GameEnded>().Any() && game.State.Phase == Phase.GameOver);

            // 同じ乱数の種なら同じ流れ
            var fx1 = MoveEffect.Parse("20%で相手をどく"); var fx2 = MoveEffect.Parse("10%で自分のこうげき・とくこう・すばやさを1段階上げる");
            var fx3 = MoveEffect.Parse("20%相手の素早さを1段階下げる"); var fx4 = MoveEffect.Parse("急所に当たりやすい（15%）");
            Check(log, "技の効果の文を読む（どく・自分のランク3つ・素早さ表記・急所）", fx1.Status == Status.Poison && fx1.Chance == 0.2 && fx2.Self && fx2.Stats.Count == 3 && fx2.Stages == 1
                && !fx3.Self && fx3.Stats.Single() == 2 && fx3.Stages == -1 && fx4.CritRate == 0.15 && MoveEffect.Parse("10%でマヒ").Status == Status.Paralysis
                && MoveEffect.Parse("後攻であれば攻撃力1.3倍").LateMult == 1.3 && MoveEffect.Parse("相手のとくこうの効果") != null);
            var ab = AbilityInfo.Parse("しんりょく：HPが最大HPの1/3以下のとき、攻撃のダメージが1.5倍"); var ab2 = AbilityInfo.Parse("はりきり：攻撃がガードされなかったとき、20%で物理攻撃のダメージが1.5倍");
            Check(log, "特性の文から名前と数字を読む", ab.Name == "しんりょく" && Math.Abs(ab.Frac - 1.0 / 3) < 1e-9 && ab.Mult == 1.5 && ab2.Rate == 0.2 && ab2.Mult == 1.5);
            Check(log, "同じ種なら手番の順番とサイコロが同じ", DiceSequence(board, 12345) == DiceSequence(board, 12345) && DiceSequence(board, 12345) != DiceSequence(board, 999));
            return log;
        }

        // 実際のデータを使うテスト：実数値・ダメージの例（仕様書7.2、7.5）と、ランダムに遊ばせたときに壊れないか
        public static List<string> RunWithData(GameData data, BoardData board)
        {
            var log = new List<string>();
            (int atk, int hp) Stats(string species, int level)
            {
                var p = Pokemons.Create(data, species, level, "", "", 1);
                return (Pokemons.Attack(data, p), Pokemons.MaxHp(data, p));
            }
            Check(log, "実数値：ヒトカゲLv5はこうげき10・HP18", Stats("ヒトカゲ", 5) == (10, 18));
            Check(log, "実数値：リザードLv30はこうげき43・HP74", Stats("リザード", 30) == (43, 74));
            Check(log, "実数値：リザードンLv50はこうげき89・HP138", Stats("リザードン", 50) == (89, 138));
            Check(log, "ダメージ：種族値80どうしLv10で基本ダメージ17", (int)GameProcessor.BaseDamage(data, 10, Pokemons.Stat(80, 10), Pokemons.Stat(80, 10)) == 17);
            Check(log, "ダメージ：種族値80どうしLv50で基本ダメージ57", (int)GameProcessor.BaseDamage(data, 50, Pokemons.Stat(80, 50), Pokemons.Stat(80, 50)) == 57);

            var hito = Pokemons.Create(data, "ヒトカゲ", 15, "", "", 1);
            Check(log, "進化：ヒトカゲLv16でリザードに", Pokemons.TryEvolve(data, hito) == null && Pokemons.GainExp(data, hito, Pokemons.ExpForLevel(16) - hito.Exp) == 1 && Pokemons.TryEvolve(data, hito) == "リザード");
            Check(log, "進化：1回に1段階だけ", Pokemons.GainExp(data, hito, Pokemons.ExpForLevel(40) - hito.Exp) > 0 && Pokemons.TryEvolve(data, hito) == "リザードン" && Pokemons.TryEvolve(data, hito) == null);

            Check(log, "ボールマスの段階：バッジ0個はbt0、3個はbt3、8個はbt8", data.TierFor(0)?.Id == "bt0" && data.TierFor(3)?.Id == "bt3" && data.TierFor(8)?.Id == "bt8" && data.TierFor(0, true)?.Id == "bt3" && data.TierFor(8, true)?.Id == "bt8");
            var nibiGoods = data.ShopGoods("shop_nibi", _ => true);
            Check(log, "ショップ：ニビの品ぞろえに共通の品（モンスターボール）と町の品（4のくつ）がある", nibiGoods.Contains("モンスターボール") && nibiGoods.Contains("4のくつ") && !nibiGoods.Contains("1のくつ"));
            Check(log, "ランダムイベント：発生率の合計が100%以下", data.RandomEvents.Sum(e => e.Rate) <= 100);

            // ニビジムとライバルマスに、となりから1歩で止まらせる
            BattleStarted LandOn(BoardSquare target, Action<GameState> setup = null) => LandEvents(target, "ヒトカゲ", setup).events.OfType<BattleStarted>().SingleOrDefault();
            (GameProcessor g, List<GameEvent> events) LandEvents(BoardSquare target, string starter, Action<GameState> setup = null)
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, starter) }, 50, 7, out _);
                var p = g.State.CurrentPlayer;
                setup?.Invoke(g.State);
                p.Square = target.Neighbors[0];
                p.FixedDice = 1;
                Apply(g, new RollDice());
                Apply(g, new ChooseDestination { Square = target.Id });
                return (g, Apply(g, new ConfirmMove()));
            }
            var nibi = board.Squares.Values.FirstOrDefault(x => x.Type == "ジム" && x.Place == "ニビシティ");
            var nibiTrainerSquares = data.Trainers.Where(t => t.IsGymTrainer && t.Place == "ニビシティ").Select(t => board.Squares.Values.First(x => x.Name == t.Square).Id).ToList();
            if (nibi != null && nibi.Neighbors.Count > 0 && nibiTrainerSquares.Count > 0) Check(log, "ジムトレーナーがだれにも倒されていないとニビジムに挑めない", LandOn(nibi) == null);
            if (nibi != null && nibi.Neighbors.Count > 0) Check(log, "ジムトレーナーが全員（他人でも）倒されているとタケシとフルバトル", LandOn(nibi, s => { foreach (int id in nibiTrainerSquares) s.Owners[id] = 99; }) is BattleStarted gb && gb.Kind == BattleKind.Gym && gb.Full && gb.Opponent == "タケシ");
            var gymTrainerSq = board.Squares.Values.FirstOrDefault(x => data.Trainers.Any(t => t.IsGymTrainer && t.Place == "ニビシティ" && t.Square == x.Name));
            if (gymTrainerSq != null && gymTrainerSq.Neighbors.Count > 0) Check(log, "ジムトレーナーのマスに止まるとトレーナー戦", LandOn(gymTrainerSq) is BattleStarted tb && tb.Kind == BattleKind.Trainer);
            if (gymTrainerSq != null && gymTrainerSq.Neighbors.Count > 0 && data.FindSpecies("メタモン") != null)
            {
                var (mg, mev) = LandEvents(gymTrainerSq, "メタモン");
                Check(log, "へんしん：メタモンは相手に変身し、必ず先攻、必殺技は選べない", mev.OfType<Transformed>().Any(x => x.Side == 0) && mev.OfType<InitiativeDrawn>().Single().First == 0
                    && mg.Validate(new ChooseAttack { Attack = Attack.Super }) != null && mg.Validate(new ChooseAttack { Attack = Attack.Physical }) == null);
            }
            if (gymTrainerSq != null && gymTrainerSq.Neighbors.Count > 0 && data.FindSpecies("アーボック")?.AbilityData?.Name == "いかく")
            {
                var (_, iev) = LandEvents(gymTrainerSq, "アーボック");
                Check(log, "特性いかく：出たとき相手のこうげきが1段階下がる", iev.OfType<AbilityTriggered>().Any(x => x.Ability == "いかく" && x.Side == 0) && iev.OfType<RankChanged>().Any(x => x.Side == 1 && x.Stat == "こうげき" && x.Delta == -1));
            }
            // もちもの（8.2）：センターで持たせると袋から出て、はずすと袋に戻る
            var center = board.Squares.Values.FirstOrDefault(x => x.Type == "ポケモンセンター" && x.Neighbors.Count > 0);
            if (center != null && data.FindItem("いのちのたま")?.IsHeldItem == true)
            {
                var (cg, _) = LandEvents(center, "ヒトカゲ", s => s.Players[0].PokemonBag.Add("いのちのたま"));
                var cp = cg.State.Players[0];
                bool onIt = cg.State.Facility?.Kind == FacilityKind.Center;
                if (onIt) Apply(cg, new HoldItem { Slot = 0, Item = "いのちのたま" });
                bool held = cp.Party[0].HeldItem == "いのちのたま" && !cp.PokemonBag.Contains("いのちのたま");
                if (onIt) Apply(cg, new HoldItem { Slot = 0 });
                Check(log, "もちもの：センターで持たせる・はずす", onIt && held && cp.Party[0].HeldItem == "" && cp.PokemonBag.Contains("いのちのたま"));
            }
            // トレーナーの道具（8.3、8.6）：2人で、同じエリアの相手に使う
            (GameProcessor g, PlayerState me, PlayerState you) Two(string myItem, Action<PlayerState> setupYou = null)
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ヒトカゲ") }, 50, 5, out _);
                var me = g.State.CurrentPlayer; var you = g.State.Players.First(x => x != me);
                me.TrainerBag.Add(myItem);
                setupYou?.Invoke(you);
                return (g, me, you);
            }
            {
                var (g, me, you) = Two("スリーパーのこいん");
                g.Apply(new UseItem { Player = me.Id, Item = "スリーパーのこいん", Target = you.Id }, out string err);
                var (g2, me2, you2) = Two("スリーパーのこいん", y => y.TrainerBag.Add("かごのみ"));
                g2.Apply(new UseItem { Player = me2.Id, Item = "スリーパーのこいん", Target = you2.Id }, out string err2);
                var far = board.Squares.Values.FirstOrDefault(x => x.Area != board[board.StartSquare].Area);
                var (g3, me3, you3) = Two("スリーパーのこいん", y => { if (far != null) y.Square = far.Id; });
                Check(log, "スリーパーのこいん：相手がさいみん、かごのみで防ぐ、ほかのエリアには届かない", err == null && you.Has("さいみん") && err2 == null && !you2.Has("さいみん") && !you2.TrainerBag.Contains("かごのみ")
                    && (far == null || g3.Validate(new UseItem { Player = me3.Id, Item = "スリーパーのこいん", Target = you3.Id }) != null));
            }
            {
                var (g, me, you) = Two("キャッチャ", y => y.PokemonBag.Add("キズくすり"));
                g.Apply(new UseItem { Player = me.Id, Item = "キャッチャ", Target = you.Id }, out string err);
                Check(log, "キャッチャ：相手の道具を1つ奪い、準備フェーズのまま", err == null && me.PokemonBag.Contains("キズくすり") && !you.PokemonBag.Contains("キズくすり") && g.State.Phase == Phase.Prepare);
            }
            {
                var (g, me, _) = Two("ダート自転車");
                g.Apply(new UseItem { Player = me.Id, Item = "ダート自転車", Target = 0 }, out string err);
                int n = g.Apply(new RollDice { Player = me.Id }, out _).OfType<DiceRolled>().First().Dice.Count;
                Check(log, "ダート自転車：ルーレットが2〜4個", err == null && n >= 2 && n <= 4 && me.Has("ダート自転車"));
            }
            {
                // あなぬけひも：メインマップ以外（マップIDあり）にいると、最後に止まったメインマップのワープマスへ
                var cave = board.Squares.Values.First(x => x.Id != board.StartSquare);
                string keep = cave.MapId;
                cave.MapId = "test_cave";
                var (g, me, _) = Two("あなぬけひも", null);
                me.Square = cave.Id; me.LastMainWarp = board.StartSquare;
                g.Apply(new UseItem { Player = me.Id, Item = "あなぬけひも", Target = 0 }, out string err);
                var (g2, me2, _) = Two("あなぬけひも", null);
                bool mainRejected = g2.Validate(new UseItem { Player = me2.Id, Item = "あなぬけひも", Target = 0 }) != null;
                cave.MapId = keep;
                Check(log, "あなぬけひも：洞窟の中からメインマップのワープマスへ、メインマップでは使えない", err == null && me.Square == board.StartSquare && mainRejected);
            }
            {
                // アマイ三ツ：となりのマスにカビゴンがいると、1歩では そこに止まれない。ポケモンのふえで どかせる
                var (g, me, _) = Two("ポケモンのふえ");
                int next = board[me.Square].Neighbors[0];
                g.State.Blocked[next] = 2;
                g.Apply(new UseItem { Player = me.Id, Item = "ポケモンのふえ", Target = next }, out string ferr);
                var (g2, me2, _) = Two("アマイ三ツ");
                g2.State.Blocked[next] = 2;
                me2.FixedDice = 1;
                var reach = g2.Apply(new RollDice { Player = me2.Id }, out _).OfType<DiceRolled>().First().Reachable;
                var farSq = board.Squares.Values.FirstOrDefault(x => x.Area != board[me.Square].Area);
                var (g3, me3, _) = Two("ポケモンのふえ");
                if (farSq != null) g3.State.Blocked[farSq.Id] = 2;
                bool farRejected = farSq == null || g3.Validate(new UseItem { Player = me3.Id, Item = "ポケモンのふえ", Target = farSq.Id }) != null;
                Check(log, "アマイ三ツ：カビゴンのマスには入れない、ポケモンのふえで どく（同じエリアだけ）", !reach.Contains(next) && ferr == null && !g.State.Blocked.ContainsKey(next) && farRejected);
            }
            if (data.RandomEvents.Any(e => e.Bell))
            {
                // ポケベル：4人から選ぶ。やめたら準備フェーズに戻って道具も残る。呼ぶと道具がなくなってイベント
                var (g, me, _) = Two("ポケベル");
                g.Apply(new UseItem { Player = me.Id, Item = "ポケベル" }, out string e1);
                int options = g.State.Facility?.Options.Count ?? 0;
                g.Apply(new ChooseOption { Player = me.Id, Index = options - 1 }, out string e2);
                bool back = g.State.Phase == Phase.Prepare && me.TrainerBag.Contains("ポケベル");
                g.Apply(new UseItem { Player = me.Id, Item = "ポケベル" }, out string e3);
                var called = g.Apply(new ChooseOption { Player = me.Id, Index = 0 }, out string e4);
                Check(log, "ポケベル：4人から選ぶ、やめたら戻る、呼ぶとイベント", e1 == null && options == 5 && e2 == null && back && e3 == null && e4 == null && !me.TrainerBag.Contains("ポケベル") && called.OfType<EventStarted>().Any());
            }
            {
                // 鍵付きモンスターボール：カギがあれば開いて1つ上の表、なければ開かない（盤面にまだないので、マスの種類を一時的に変えて試す）
                var sq = board.Squares.Values.First(x => x.Type == "空マス" && x.Neighbors.Count > 0);
                string keep = sq.Type;
                sq.Type = "鍵付きモンスターボール";
                var (_, kev) = LandEvents(sq, "ヒトカゲ", s => s.Players[0].TrainerBag.Add("カギ"));
                var (ng, nev) = LandEvents(sq, "ヒトカゲ");
                sq.Type = keep;
                int prizes = 0;
                for (ulong seed = 1; seed <= 40 && data.KeyPrizeItems.Count > 0; seed++)
                {
                    sq.Type = "鍵付きモンスターボール";
                    var (pg, pev) = LandEvents(sq, "ヒトカゲ", s => { s.Players[0].TrainerBag.Add("カギ"); s.Rng = seed * 7919; });
                    sq.Type = keep;
                    var got = pev.OfType<ItemGained>().FirstOrDefault()?.Item;
                    if (pev.OfType<BallTierUsed>().Any(x => x.Prize)) { prizes++; if (!data.KeyPrizeItems.Any(x => x.Item == got && x.Weights["bt0"] > 0)) prizes = -999; }
                }
                Check(log, "カギ：鍵付きボールマスを開けると当たり枠か1つ上の表、カギがなければ開かない", (data.KeyPrizeItems.Count == 0 || prizes > 0) && kev.OfType<BallTierUsed>().Any(x => x.Keyed) && kev.OfType<ItemUsed>().Any(x => x.Item == "カギ") && !nev.OfType<BallTierUsed>().Any());
            }
            if (data.Jobs.Any(j => j.Name == "だいすきくらぶ"))
            {
                // 条件「道具を渡す」：金の入れ歯を持っていれば渡して、だいすきくらぶを解放できる
                var sq = board.Squares.Values.First(x => x.Type == "空マス" && x.Neighbors.Count > 0);
                string keep = sq.Type;
                sq.Type = "イベントマス";
                var row = new EventSquare { Square = sq.Name, Content = "園長", Effect = new Effect { Id = "test_safari", Name = "園長", Kind = EffectKind.UnlockJob, Value = "だいすきくらぶ", CondKind = "道具を渡す", CondValue = "金の入れ歯", Text = "", TextKey = "", Reward = "" } };
                data.EventSquares.Insert(0, row);
                var (dg, dev) = LandEvents(sq, "ヒトカゲ", s => s.Players[0].TrainerBag.Add("金の入れ歯"));
                var (_, nev) = LandEvents(sq, "ヒトカゲ");
                data.EventSquares.Remove(row);
                sq.Type = keep;
                Check(log, "金の入れ歯：園長に渡すと だいすきくらぶを選べる、持っていなければ何もない", dev.OfType<ItemGiven>().Any() && !dg.State.Players[0].TrainerBag.Contains("金の入れ歯") && dg.State.Facility?.Options.Contains("だいすきくらぶ") == true && !nev.OfType<ItemGiven>().Any());
            }
            {
                // サイクリング：出目1でも、1〜4歩先のどこにでも止まれる
                int Reach(string job)
                {
                    var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, 7, out _);
                    var pl = g.State.Players[0]; pl.Job = job; pl.FixedDice = 1;
                    return Apply(g, new RollDice()).OfType<DiceRolled>().First().Reachable.Count;
                }
                Check(log, "サイクリング：サイコロの目に3歩まで足せる（止まれるマスが増える）", Reach("サイクリング") > Reach(""));
            }
            if (center != null)
            {
                // ロケット団：センターでは回復しない（入れ替えなどはできる）
                var (rg, _) = LandEvents(center, "ヒトカゲ", s => { s.Players[0].Job = "ロケット団"; s.Players[0].Party[0].Hp = 1; });
                Check(log, "ロケット団：センターで回復しないが、センターの画面は開く", rg.State.Players[0].Party[0].Hp == 1 && rg.State.Facility?.Kind == FacilityKind.Center);
            }
            if (data.RocketBases.Count > 0)
            {
                // ロケット団マス（11.3）：盤面にまだないので、空マスを一時的に「おつきみ山のロケット団マス」にして試す
                var sq = board.Squares.Values.First(x => x.Type == "空マス" && x.Neighbors.Count > 0);
                var keep = (sq.Type, sq.Place);
                sq.Type = "ロケット団"; sq.Place = "おつきみ山";
                void Strong(GameState s) { var pl = s.Players[0]; pl.Money = 20000; pl.Party[0] = Pokemons.Create(data, "カメックス", 60, "", "", 1); pl.Party.Add(Pokemons.Create(data, "ヒトカゲ", 5, "", "", 1)); }
                List<GameEvent> Fight(GameProcessor g)
                {
                    var all = new List<GameEvent>();
                    for (int i = 0; i < 60 && g.State.Battle != null; i++)
                    {
                        var b = g.State.Battle;
                        if (b.Step == BattleStep.Select) { all.AddRange(Apply(g, new SelectPokemon { Slot = b.Sides[0].Team.FindIndex(m => !m.Fainted) })); continue; }
                        all.AddRange(b.Sides[b.Attacker].Player == 0 ? Apply(g, new ChooseAttack { Attack = Attack.Physical }) : Apply(g, new ChooseDefense { Defense = Defense.Counter }));
                    }
                    return all;
                }
                var (g1, ev1) = LandEvents(sq, "ヒトカゲ", Strong);
                bool battle = ev1.OfType<BattleStarted>().Any(x => x.Kind == BattleKind.Rocket);
                var fought = Fight(g1);
                var won = fought.OfType<RocketDefeated>().FirstOrDefault();
                int areaTrainers = board.Squares.Values.Count(x => x.Type == "トレーナー" && x.Area == sq.Area);
                Check(log, "ロケット団マス：団員とクイックバトル、勝つと拠点とエリアのトレーナーマス2つが自分のものに", battle && won != null && won.First && g1.State.Owners[sq.Id] == 0 && won.Trainers.Count == Math.Min(2, areaTrainers) && won.Trainers.All(x => g1.State.Owners[x] == 0));

                var (g2, _) = LandEvents(sq, "ヒトカゲ", s => { Strong(s); s.Owners[sq.Id] = 0; });
                var f = g2.State.Facility;
                int money = g2.State.Players[0].Money;
                bool opened = f?.Kind == FacilityKind.RocketBase && f.Options[0].Contains("かいのかせき") && f.Options[0].Contains("2,100");
                if (opened) Apply(g2, new ChooseOption { Index = 0 });
                bool bought = g2.State.Players[0].Money == money - 2100 && g2.State.Players[0].PokemonBag.Contains("かいのかせき") && g2.State.Facility?.Kind == FacilityKind.RocketBase;
                if (bought) Apply(g2, new ChooseOption { Index = g2.State.Facility.Options.Count - 1 });
                Check(log, "ロケット団の拠点：持ち主は割引で買える、買ったあとも拠点は開いたまま、立ち去ると閉じる", opened && bought && g2.State.Facility == null);

                sq.Place = "24番道路";
                var (g3, _) = LandEvents(sq, "ヒトカゲ", s => { Strong(s); s.Owners[sq.Id] = 0; });
                var f3 = g3.State.Facility;
                int trade = f3 == null ? -1 : f3.Options.FindIndex(o => o.StartsWith("交換：トゲピー"));
                if (trade >= 0) Apply(g3, new ChooseOption { Index = trade });
                bool choosing = g3.State.Facility?.Kind == FacilityKind.Trade && g3.State.Facility.Options[0].StartsWith("カメックス");
                if (choosing) Apply(g3, new ChooseOption { Index = 0 });
                var party = g3.State.Players[0].Party;
                Check(log, "ロケット団の拠点：交換でコスト以上の子を渡してトゲピー（表のLv）をもらう", trade >= 0 && choosing && party.Any(m => m.Species == "トゲピー" && m.Level == 10) && !party.Any(m => m.Species == "カメックス") && g3.State.Players[0].RocketUsed.Contains("rb_bridge_2"));
                sq.Place = "ポケモンタワー";
                var (g4, _) = LandEvents(sq, "ヒトカゲ", s => { Strong(s); s.Owners[sq.Id] = 0; });
                var all4 = g4.State.Players[0].Party.Concat(g4.State.Players[0].Box).ToList();
                var (g5, _) = LandEvents(sq, "ヒトカゲ", s => { Strong(s); s.Owners[sq.Id] = 0; s.Players[0].RocketUsed.Add("rb_tower_2"); });
                Check(log, "ロケット団の拠点：タワーでヒトモシを1人1回もらえる", all4.Count(m => m.Species == "ヒトモシ" && m.Level == 15) == 1 && !g5.State.Players[0].Party.Concat(g5.State.Players[0].Box).Any(m => m.Species == "ヒトモシ"));
                sq.Type = keep.Type; sq.Place = keep.Place;
            }
            var museumRow = data.EventSquares.FirstOrDefault(x => x.Effect.Kind == EffectKind.Museum);
            var museum = museumRow == null ? null : board.Squares.Values.FirstOrDefault(x => x.Name == museumRow.Square && x.Neighbors.Count > 0);
            if (museum != null && data.FindItem("こうらのかせき") != null)
            {
                // 博物館（3.2.2）：化石を預ける → 週がたつまで眠ったまま → 孵る（Lvは手持ちの最高Lv−5、最低5）
                var (mg, _) = LandEvents(museum, "ヒトカゲ", s => s.Players[0].PokemonBag.Add("こうらのかせき"));
                var mp = mg.State.Players[0];
                bool asked = mg.State.Facility?.Options[0].Contains("こうらのかせき") == true;
                if (asked) Apply(mg, new ChooseOption { Index = 0 });
                bool deposited = mp.Fossil == "こうらのかせき" && !mp.PokemonBag.Contains("こうらのかせき");
                var (wg, _) = LandEvents(museum, "ヒトカゲ", s => { s.Players[0].Fossil = "こうらのかせき"; s.Players[0].FossilWeek = s.Week; });
                bool waiting = wg.State.Players[0].Fossil != "" && !wg.State.Players[0].Party.Any(m => m.Species == "カブト");
                var (hg, _) = LandEvents(museum, "ヒトカゲ", s => { var pl = s.Players[0]; pl.Fossil = "こうらのかせき"; pl.FossilWeek = s.Week - 3; Pokemons.SetLevel(data, pl.Party[0], 30); });
                var hp = hg.State.Players[0];
                Check(log, "博物館：化石を預けると、3週あとに孵る（Lvは最高Lv−5）", asked && deposited && waiting && hp.Fossil == "" && hp.Party.Any(m => m.Species == "カブト" && m.Level == 25 && m.MetHow == "ニビ科学博物館で目覚めた"));
            }
            {
                // 海上マスとつり（4.1.2）：スタートのとなりを一時的に「海上の草むら」にして試す
                int seaId = board[board.StartSquare].Neighbors[0];
                var sea = board[seaId];
                var keep = (sea.Type, sea.GrassKind);
                sea.Type = "草むら"; sea.GrassKind = "海上";
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, 7, out _);
                g.State.Players[0].FixedDice = 1;
                var reach = Apply(g, new RollDice()).OfType<DiceRolled>().First().Reachable;
                var g3 = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, 7, out _);
                var fev = Apply(g3, new Fish());
                bool fished = fev.OfType<Fished>().Any() && (fev.OfType<BattleStarted>().Any(b => b.Kind == BattleKind.Wild) || !data.Encounters.Any(e => e.Method == "つり" && (e.Place == board[board.StartSquare].Place || e.Place == sea.Place)));
                var (_, sev) = LandEvents(sea, "ヒトカゲ");
                var wild = sev.OfType<BattleStarted>().FirstOrDefault(b => b.Kind == BattleKind.Wild);
                bool waterTable = wild == null || data.Encounters.Any(e => e.Place == sea.Place && e.Method == "水上" && wild.Opponent == "野生の" + e.Pokemon);
                (sea.Type, sea.GrassKind) = keep;
                var g4 = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, 7, out _);
                bool noFishOnLand = g4.Validate(new Fish { Player = 0 }) != null;
                Check(log, "海上マス：だれでも行き先に選べる、止まると水上の野生、となりでは つりができる", reach.Contains(seaId) && fished && waterTable && noFishOnLand);
            }
            var rivalSq = board.Squares.Values.FirstOrDefault(x => x.Type == "ライバル" && x.Neighbors.Count > 0);
            if (rivalSq != null && data.Rivals.Any(r => r.Place == rivalSq.Place && r.Trades.Count > 0))
            {
                // ライバルマスの交換（11.5）：持ち主はコスト低めでケーブル不要。1人1回
                var rv = data.Rivals.First(r => r.Place == rivalSq.Place);
                var (tg, _) = LandEvents(rivalSq, "ヒトカゲ", s => { s.Owners[rivalSq.Id] = 0; s.Players[0].Party.Add(Pokemons.Create(data, "カメックス", 60, "", "", 1)); });
                var trades = tg.RivalTrades(tg.State.Players[0], rivalSq.Id);
                bool opened = tg.State.Facility?.Kind == FacilityKind.Rival && trades.Count == rv.Trades.Count;
                if (opened) Apply(tg, new RivalTrade { Species = rv.Trades[0] });
                bool choosing = tg.State.Facility?.Kind == FacilityKind.Trade;
                int give = choosing ? tg.State.Facility.Goods.IndexOf("party:1") : -1;
                if (give >= 0) Apply(tg, new ChooseOption { Index = give });
                var tp = tg.State.Players[0];
                bool got = tp.Party.Concat(tp.Box).Any(m => m.Species == rv.Trades[0] && m.Level == rv.TradeLevel) && !tp.Party.Any(m => m.Species == "カメックス");
                bool again = tp.RocketUsed.Contains("rival:" + rv.Trades[0]); // もう一度は交換できない（することがなければ施設は閉じる）
                var (ng, _) = LandEvents(rivalSq, "ヒトカゲ", s => { s.Owners[rivalSq.Id] = 99; s.Players[0].Party.Add(Pokemons.Create(data, "カメックス", 60, "", "", 1)); });
                var noCable = ng.State.Facility?.Kind == FacilityKind.Rival ? Apply(ng, new RivalTrade { Species = rv.Trades[0] }) : new List<GameEvent>();
                bool needCable = noCable.OfType<Dialog>().Any(d => d.Key == "rival_no_cable") && ng.State.Facility?.Kind == FacilityKind.Rival;
                Check(log, "ライバルマスの交換：持ち主は他地方のポケモンをもらえる（1人1回）、ほかの人は通信ケーブルが要る", opened && choosing && give >= 0 && got && again && needCable);
            }
            if (rivalSq != null && data.Rivals.Any(r => r.Place == rivalSq.Place) && data.HasDialog(data.Rivals.First(r => r.Place == rivalSq.Place).Id))
            {
                // ライバル文言：はじめは「ライバルid」のセリフでバトル。持ち主が止まると「_owner」、ほかの人には「_other」
                string id = data.Rivals.First(r => r.Place == rivalSq.Place).Id;
                var (_, first) = LandEvents(rivalSq, "ヒトカゲ");
                var (_, oev) = LandEvents(rivalSq, "ヒトカゲ", s => s.Owners[rivalSq.Id] = 0);
                var (_, xev) = LandEvents(rivalSq, "ヒトカゲ", s => s.Owners[rivalSq.Id] = 99);
                Check(log, "ライバル文言：はじめのセリフ、持ち主のセリフ、ほかの人へのセリフ", first.OfType<Dialog>().Any(d => d.Key == id) && oev.OfType<Dialog>().Any(d => d.Key == id + "_owner") && xev.OfType<Dialog>().Any(d => d.Key == id + "_other"));
            }
            if (rivalSq != null && data.Rivals.Count > 0)
            {
                // 脱出ボタン：フルバトルでダメージを受けたら交代（いまの子は選べない）
                var (bg, _) = LandEvents(rivalSq, "ヒトカゲ", s =>
                {
                    var pl = s.Players[0];
                    pl.Party[0] = Pokemons.Create(data, "カメックス", 60, "", "", 1); pl.Party[0].HeldItem = "脱出ボタン";
                    pl.Party.Add(Pokemons.Create(data, "リザードン", 60, "", "", 1));
                });
                bool switched = false;
                for (int i = 0; i < 40 && bg.State.Battle != null && !switched; i++)
                {
                    var b = bg.State.Battle;
                    if (b.Step == BattleStep.Select && i == 0) { bg.Apply(new SelectPokemon { Player = 0, Slot = 0 }, out _); continue; } // フルバトルの最初の選出
                    if (b.Step != BattleStep.Commands) break; // 先に倒れた（選び直し）など
                    var ev = b.Sides[b.Attacker].Player == 0 ? bg.Apply(new ChooseAttack { Player = 0, Attack = Attack.Special }, out _) : bg.Apply(new ChooseDefense { Player = 0, Defense = Defense.Counter }, out _);
                    switched = ev.OfType<SwitchedOut>().Any(x => x.Side == 0);
                }
                Check(log, "脱出ボタン：ダメージを受けたら交代、いまの子は選べない、なくならない", switched && bg.State.Players[0].Party[0].HeldItem == "脱出ボタン" && bg.State.Battle?.Step == BattleStep.Select && bg.Validate(new SelectPokemon { Player = 0, Slot = 0 }) != null && bg.Validate(new SelectPokemon { Player = 0, Slot = 1 }) == null);
            }
            if (gymTrainerSq != null && gymTrainerSq.Neighbors.Count > 0)
            {
                var (_, wev) = LandEvents(gymTrainerSq, "ヒトカゲ", s => { s.Weather = "雨季"; s.WeatherWeeks = 3; s.FluteWeather = "晴れ"; s.FluteWeeks = 3; });
                Check(log, "三竜のふえ：週イベントの雨より、ふえの晴れが優先", wev.OfType<BattleWeatherChanged>().FirstOrDefault()?.Weather == "晴れ");
            }
            if (center != null)
            {
                var (cg2, _) = LandEvents(center, "ヒトカゲ", s => s.Players[0].Ailments.Add(new PlayerAilment { Name = "のろい", Weeks = 3 }));
                Check(log, "のろい：ポケモンセンターに入れない", cg2.State.Facility == null);
            }
            var plain = board.Squares.Values.FirstOrDefault(x => x.Type == "空マス" && x.Neighbors.Count > 0);
            if (plain != null)
            {
                var (tg, tev) = LandEvents(plain, "ヒトカゲ", s => { s.Traps[plain.Id] = 99; s.Players[0].Party[0].Hp = 10; });
                var mon = tg.State.Players[0].Party[0];
                Check(log, "ワナ：止まると手持ちがダメージ（1は残る）、ワナは消える", tev.OfType<TrapSprung>().Any() && mon.Hp >= 1 && mon.Hp < 10 && !tg.State.Traps.ContainsKey(plain.Id));
            }
            var tokiwaTrainerSq = board.Squares.Values.FirstOrDefault(x => data.Trainers.Any(t => t.IsGymTrainer && t.Place == "トキワシティ" && t.Square == x.Name));
            if (tokiwaTrainerSq != null && tokiwaTrainerSq.Neighbors.Count > 0)
                Check(log, "トキワのジムトレーナーは、バッジ7個まで戦わない", LandOn(tokiwaTrainerSq) == null
                    && LandOn(tokiwaTrainerSq, s => { for (int i = 0; i < 7; i++) s.Players[0].Gyms.Add("ジム" + i); }) is BattleStarted kb && kb.Kind == BattleKind.Trainer);
            var rival = board.Squares.Values.FirstOrDefault(x => x.Type == "ライバル");
            if (rival != null && rival.Neighbors.Count > 0 && data.Rivals.Count > 0) Check(log, "ライバルマスに止まるとフルバトル", LandOn(rival) is BattleStarted rb && rb.Kind == BattleKind.Rival && rb.Full);
            if (rival != null && rival.Neighbors.Count > 0 && data.RivalParties.Any(x => x.CondKind == "御三家を選んだ人がいる" && x.CondValue == "チコリータ"))
            {
                // ライバルの手持ちは条件で変わる：だれかがチコリータを選んでいれば、シルバーはヒノアラシ（silver_a）
                var cond = data.RivalParties.First(x => x.CondKind == "御三家を選んだ人がいる" && x.CondValue == "チコリータ");
                var (cg, cev) = LandEvents(rival, "チコリータ");
                var team = cg.State.Battle?.Sides[1].Team.Select(m => m.Species).ToList() ?? new List<string>();
                Check(log, "ライバルの手持ち：御三家の条件に合う行が使われる", cev.OfType<BattleStarted>().Any() && team.SequenceEqual(cond.Party.Select(m => m.species)));
            }

            // アイテム進化（6.5）：いしを使うとその場で進化する
            var stoneMon = data.Species.FirstOrDefault(s => s.Evolutions.Any(e => e.Kind == EvolutionKind.Item));
            if (stoneMon != null)
            {
                var evo = stoneMon.Evolutions.First(e => e.Kind == EvolutionKind.Item);
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, stoneMon.Name) }, 50, 3, out _);
                var p = g.State.CurrentPlayer;
                p.PokemonBag.Add(evo.Item);
                g.Apply(new UseItem { Player = p.Id, Item = evo.Item, Target = 0 }, out string err);
                Check(log, $"アイテム進化：{stoneMon.Name}に{evo.Item}で{evo.Target}", err == null && p.Party[0].Species == evo.Target && !p.PokemonBag.Contains(evo.Item));
            }

            // 1回休み（10章）：自分の番が来たら休み、状態は消える
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ"), ("B", MoveMode.Highlight, "ゼニガメ") }, 50, 3, out _);
                var first = g.State.CurrentPlayer;
                var second = g.State.Players.First(x => x != first);
                second.Ailments.Add(new PlayerAilment { Name = "1回休み", Weeks = 1 });
                second.Square = board.Squares.Keys.First(k => k != first.Square); // 同じマスだとプレイヤー戦になるので離す
                first.FixedDice = 0; // 0のくつと同じ（その場に止まる）
                var ev = new List<GameEvent>();
                ev.AddRange(g.Apply(new RollDice { Player = first.Id }, out _));
                ev.AddRange(g.Apply(new ChooseDestination { Player = first.Id, Square = first.Square }, out _));
                ev.AddRange(g.Apply(new ConfirmMove { Player = first.Id }, out _));
                while (g.State.Phase == Phase.Facility) ev.AddRange(g.Apply(new LeaveFacility { Player = g.State.Facility.Player }, out _));
                Check(log, "1回休み：次の人の番を飛ばして、状態も消える", ev.OfType<TurnSkipped>().Any(t => t.Player == second.Id) && !second.Has("1回休み") && g.State.CurrentPlayer == first);
            }

            // 職業（9.1）：エリートトレーナーに就くと最大HPが1.2倍
            if (data.JobBonuses.Any(j => j.Job == "エリートトレーナー"))
            {
                var g = GameProcessor.NewGame(board, data, new[] { ("A", MoveMode.Highlight, "ヒトカゲ") }, 50, 3, out _);
                var p = g.State.CurrentPlayer;
                var start = board[board.StartSquare];
                int before = Pokemons.MaxHp(data, p.Party[0]);
                p.UnlockedJobs.Add("エリートトレーナー");
                p.Square = start.Neighbors[0]; p.FixedDice = 1;
                Apply(g, new RollDice()); Apply(g, new ChooseDestination { Square = start.Id }); Apply(g, new ConfirmMove());
                Apply(g, new ChangeJob { Job = "エリートトレーナー" });
                Check(log, "職業：エリートトレーナーで最大HPが1.2倍", p.Job == "エリートトレーナー" && Pokemons.MaxHp(data, p.Party[0]) == (int)(before * 1.2));
            }

            Check(log, "CPUだけで最後まで遊べる（4人×20週×2回）", BalanceReport(data, board, 2, 20).Count > 0);

            Check(log, "セーブ→ロードで同じ状態に戻る（3回）", SaveRoundTrip(data, board, 11) && SaveRoundTrip(data, board, 22) && SaveRoundTrip(data, board, 33));

            var summary = new Dictionary<string, int>();
            for (ulong seed = 1; seed <= 5; seed++)
            {
                string a = Simulate(data, board, seed, 30, summary), b = Simulate(data, board, seed, 30, new Dictionary<string, int>());
                if (a != b) throw new Exception($"テスト失敗: 種{seed}で、同じ流れにならない");
            }
            log.Add($"OK ランダムに遊ばせる（4人×30週×5回）：壊れず、同じ種なら同じ結果。合計 " + string.Join("、", summary.Select(kv => $"{kv.Key}{kv.Value}")));
            return log;
        }

        // バランスを見る（仕様書6.6・13.4）：CPU 4人で weeks 週を games 回遊ばせて、平均を出す。CPU が止まったら例外
        public static List<string> BalanceReport(GameData data, BoardData board, int games, int weeks)
        {
            var starters = new[] { ("ヒトカゲ", "leaf"), ("チコリータ", "gold"), ("アチャモ", "ruby"), ("ポッチャマ", "diamond") };
            var stats = new Dictionary<string, double>();
            void Add(string key, double v) => stats[key] = (stats.TryGetValue(key, out double n) ? n : 0) + v;
            var lines = new List<string>();
            for (int gi = 0; gi < games; gi++)
            {
                var r = new Random(gi * 7919 + 1);
                var game = GameProcessor.NewGame(board, data, starters.Select((s, i) => ($"CPU{i + 1}", MoveMode.Highlight, s.Item1)).ToList(), weeks, (ulong)(gi + 1) * 1000003UL,
                    out _, starters.Select(s => s.Item2).ToList(), "", new[] { true, true, true, true });
                int steps = 0;
                while (game.State.Phase != Phase.GameOver)
                {
                    if (++steps > 300000) throw new Exception($"テスト失敗: CPUのゲームが終わらない（{game.State.Phase}）");
                    int who = game.WaitingFor().First();
                    var c = Cpu.Decide(game, data, board, who, r);
                    if (c == null) throw new Exception($"テスト失敗: CPU{who + 1}が出せる命令がない（{game.State.Phase} {game.State.Facility?.Kind} {game.State.Battle?.Step}）");
                    var result = game.Apply(c, out string error);
                    foreach (var e in result)
                    {
                        if (e is BattleStarted bs) Add("バトル:" + bs.Kind, 1);
                        if (e is BattleEnded be && game.State.Players.Count > 0) Add(be.Draw ? "引き分け" : be.Winner == 0 ? "仕掛けた側の勝ち" : be.Winner == 1 ? "仕掛けた側の負け" : "にげた", 1);
                        if (e is Caught) Add("捕獲", 1);
                        if (e is Evolved) Add("進化", 1);
                        if (e is SentToCenter) Add("全滅", 1);
                        if (e is BadgeEarned) Add("バッジ", 1);
                        if (e is StatusInflicted si) Add("状態異常:" + Pokemons.StatusName(si.Status), 1);
                        if (e is RankChanged) Add("能力ランクの変化", 1);
                        if (e is CantMove) Add("動けない", 1);
                        if (e is AbilityTriggered || e is AbilityHealed || e is StatusCured || (e is BattleWeatherChanged bw && bw.Ability != null)) Add("特性", 1);
                        if (e is RoundResolved rs && rs.Abilities.Count > 0) Add("特性", 1);
                    }
                    if (error != null) throw new Exception($"テスト失敗: CPUの命令 {c.GetType().Name} が受け付けられない：{error}");
                }
                foreach (var p in game.State.Players)
                {
                    var all = p.Party.Concat(p.Box).ToList();
                    Add("主力Lv", all.Max(m => m.Level)); Add("手持ち平均Lv", p.Party.Average(m => m.Level)); Add("所持金", p.Money);
                    Add("図鑑", p.Dex.Count); Add("ファン", game.State.Owners.Count(o => o.Value == p.Id)); Add("最終得点", game.FinalScore(p));
                    Add("2段階進化した子", all.Count(m => m.MetSpecies != m.Species && data.FindSpecies(m.MetSpecies)?.Evolutions.Any(e => data.FindSpecies(e.Target)?.Evolutions.Any(e2 => e2.Target == m.Species) == true) == true));
                }
            }
            int players = games * 4;
            string Per(string k, double div) => stats.TryGetValue(k, out double v) ? (v / div).ToString("0.#") : "0";
            lines.Add($"CPU 4人 × {weeks}週 × {games}回（1人あたりの平均）");
            lines.Add($"主力のLv {Per("主力Lv", players)}　手持ちの平均Lv {Per("手持ち平均Lv", players)}　2段階進化した子 {Per("2段階進化した子", players)}体");
            lines.Add($"バッジ {Per("バッジ", players)}個　図鑑 {Per("図鑑", players)}種類　捕獲 {Per("捕獲", players)}体　進化 {Per("進化", players)}回　全滅 {Per("全滅", players)}回");
            lines.Add($"所持金 {Per("所持金", players)}円　ファン {Per("ファン", players)}人　最終得点 {Per("最終得点", players)}p");
            lines.Add("バトル（1人あたり）：" + string.Join("　", stats.Where(kv => kv.Key.StartsWith("バトル:")).Select(kv => $"{kv.Key.Substring(4)} {kv.Value / players:0.#}回"))
                + $"　／ 勝ち {Per("仕掛けた側の勝ち", players)}・負け {Per("仕掛けた側の負け", players)}・引き分け {Per("引き分け", players)}・にげた {Per("にげた", players)}");
            lines.Add("技の追加効果（1人あたり）：" + string.Join("　", stats.Where(kv => kv.Key.StartsWith("状態異常:")).Select(kv => $"{kv.Key.Substring(5)} {kv.Value / players:0.#}回"))
                + $"　能力ランクの変化 {Per("能力ランクの変化", players)}回　動けない {Per("動けない", players)}回　特性の発動 {Per("特性", players)}回");
            return lines;
        }

        // 4人がランダムに命令を出して weeks 週遊ぶ。毎回きまりが守られているか確かめ、最後の状態を文字にして返す
        static string Simulate(GameData data, BoardData board, ulong seed, int weeks, Dictionary<string, int> summary)
        {
            var policy = new Random((int)seed);
            var starters = new[] { "ヒトカゲ", "ゼニガメ", "フシギダネ", "チコリータ" };
            var game = GameProcessor.NewGame(board, data, starters.Select((s, i) => ($"P{i}", i % 2 == 0 ? MoveMode.Highlight : MoveMode.Step, s)).ToList(), weeks, seed, out var events);
            var narrator = new Narrator(data, board, game.State);
            var s = game.State;
            int steps = 0;
            void Count(string key) => summary[key] = (summary.TryGetValue(key, out int n) ? n : 0) + 1;
            while (s.Phase != Phase.GameOver)
            {
                if (++steps > 200000) throw new Exception($"テスト失敗: 種{seed}で、ゲームが終わらない（{s.Phase}）");
                Command c = RandomCommand(s, board, policy);
                var result = game.Apply(c, out string error);
                if (error != null) continue;
                foreach (var e in result)
                {
                    narrator.Describe(e);
                    if (e is BattleStarted bs) Count(bs.Kind == BattleKind.Wild ? "野生戦" : bs.Kind == BattleKind.Trainer ? "トレーナー戦" : bs.Kind == BattleKind.Gym ? "ジム戦" : bs.Kind == BattleKind.Rival ? "ライバル戦" : "プレイヤー戦");
                    if (e is Caught) Count("捕獲");
                    if (e is BadgeEarned) Count("バッジ");
                    if (e is Evolved) Count("進化");
                    if (e is SentToCenter) Count("全滅");
                    if (e is BattleEnded be && be.Draw) Count("引き分け");
                    if (e is EventStarted) Count("イベント");
                    if (e is ItemDiscarded) Count("袋がいっぱい");
                }
                Invariants(data, s, seed);
            }
            return StateText(s);
        }

        static string StateText(GameState s) =>
            string.Join("|", s.Players.Select(p => $"{p.Square},{p.Money},{p.Badges},{string.Join("/", p.Party.Concat(p.Box).Select(m => $"{m.Species}{m.Level}:{m.Hp}"))},{string.Join("/", p.PokemonBag.Concat(p.TrainerBag))}"))
            + $"|{s.Rng}|{s.Week}|{s.TurnIndex}|{s.Phase}|{string.Join(",", s.Owners.OrderBy(o => o.Key).Select(o => o.Key + ":" + o.Value))}";

        // セーブ→ロード：ランダムに遊んだ途中で保存し、読み込むと同じ状態になる（1.2）
        static bool SaveRoundTrip(GameData data, BoardData board, ulong seed)
        {
            var policy = new Random((int)seed);
            var game = GameProcessor.NewGame(board, data, new[] { ("たろう", MoveMode.Highlight, "ヒトカゲ"), ("はなこ", MoveMode.Step, "ゼニガメ") }, 30, seed, out _, new[] { "leaf", "gold" }, "random");
            for (int n = 0, accepted = 0; accepted < 400 && n < 20000 && game.State.Phase != Phase.GameOver; n++)
                if (game.Apply(RandomCommand(game.State, board, policy), out string e).Count >= 0 && e == null) accepted++;
            var loaded = GameProcessor.Load(board, data, SaveFile.Write(game.State), out _, out string error);
            return error == null && StateText(loaded.State) == StateText(game.State);
        }

        static Command RandomCommand(GameState s, BoardData board, Random r)
        {
            var p = s.CurrentPlayer;
            T Any<T>(IList<T> list) => list[r.Next(list.Count)];
            switch (s.Phase)
            {
                case Phase.Prepare:
                    int k = r.Next(10);
                    var items = p.PokemonBag.Concat(p.TrainerBag).ToList();
                    if (k == 0 && items.Count > 0) return new UseItem { Player = p.Id, Item = Any(items), Target = r.Next(3) };
                    if (k == 1) return new SwapParty { Player = p.Id, A = r.Next(3), B = r.Next(3) };
                    return new RollDice { Player = p.Id };
                case Phase.Moving:
                    if (r.Next(3) == 0) return new ConfirmMove { Player = p.Id };
                    if (p.MoveMode == MoveMode.Highlight) return new ChooseDestination { Player = p.Id, Square = Any(s.Move.Reachable.ToList()) };
                    int cur = s.Move.Path[s.Move.Path.Count - 1];
                    return new StepTo { Player = p.Id, Square = Any(board[cur].Neighbors) };
                case Phase.Facility:
                    if (s.Facility.Options.Count > 0) return new ChooseOption { Player = s.Facility.Player, Index = r.Next(s.Facility.Options.Count) };
                    switch (r.Next(6))
                    {
                        case 0: return s.Facility.Goods.Count > 0 ? new Buy { Player = p.Id, Item = Any(s.Facility.Goods) } : (Command)new LeaveFacility { Player = p.Id };
                        case 1: var bag = p.PokemonBag.Concat(p.TrainerBag).ToList(); return bag.Count > 0 ? new Sell { Player = p.Id, Item = Any(bag) } : (Command)new LeaveFacility { Player = p.Id };
                        case 2: return new CenterSwap { Player = p.Id, PartySlot = r.Next(4) - 1, BoxIndex = r.Next(4) - 1 };
                        case 3: return new AcceptHeal { Player = p.Id };
                        default: return new LeaveFacility { Player = p.Id };
                    }
                default: // Battle
                    var b = s.Battle;
                    switch (b.Step)
                    {
                        case BattleStep.Select:
                            var side = b.Sides.First(x => x.NeedsSelect);
                            return new SelectPokemon { Player = side.Player, Slot = r.Next(side.Team.Count) };
                        case BattleStep.Commands:
                            var a = b.Sides[b.Attacker]; var d = b.Sides[1 - b.Attacker];
                            if (b.AttackCmd == null && a.Player >= 0 && (b.DefenseCmd != null || r.Next(2) == 0))
                            {
                                var bagA = s.Players[a.Player].PokemonBag;
                                var atk = (Attack)r.Next(4);
                                return new ChooseAttack { Player = a.Player, Attack = atk, Item = atk == Attack.Item && bagA.Count > 0 ? Any(bagA) : null };
                            }
                            return new ChooseDefense { Player = d.Player, Defense = r.Next(20) == 0 ? Defense.Flee : (Defense)r.Next(3) };
                        case BattleStep.Ball:
                            var balls = s.Players[b.Sides[0].Player].Balls.Where(x => x.Value > 0).Select(x => x.Key).ToList();
                            return new ThrowBall { Player = b.Sides[0].Player, Ball = balls.Count > 0 && r.Next(2) == 0 ? Any(balls) : null };
                        default:
                            var loser = s.Players[b.Sides[1 - b.Winner].Player];
                            var loot = loser.PokemonBag.Concat(loser.TrainerBag).ToList();
                            int choice = r.Next(3);
                            return new ChooseReward { Player = b.Sides[b.Winner].Player, Reward = choice == 0 && loot.Count > 0 ? Reward.Item : choice == 1 ? Reward.Fan : Reward.Forgive, Item = loot.Count > 0 ? Any(loot) : null };
                    }
            }
        }

        static void Invariants(GameData data, GameState s, ulong seed)
        {
            void Must(bool ok, string what) { if (!ok) throw new Exception($"テスト失敗: 種{seed} {s.Week}週目: {what}"); }
            Must(s.Phase != Phase.Moving || s.Move != null, "移動中なのに移動の状態がない");
            Must(s.Phase != Phase.Facility || s.Facility != null, "施設の中なのに施設の状態がない");
            Must(s.Phase != Phase.Battle || s.Battle != null, "バトル中なのにバトルの状態がない");
            foreach (var p in s.Players)
            {
                Must(p.Money >= 0, $"{p.Name}の所持金がマイナス");
                Must(p.Party.Count >= 1 && p.Party.Count <= (int)data.Required("party_size"), $"{p.Name}の手持ちが{p.Party.Count}体");
                Must(p.Box.Count <= (int)data.Required("box_max"), $"{p.Name}の控えが多すぎる");
                int extra = data.JobBonuses.FirstOrDefault(b => b.Job == p.Job && b.Type == "")?.Bag ?? 0; // ロケット団は袋が大きい
                Must(p.PokemonBag.Count <= (int)data.Required("pokemon_bag") + extra && p.TrainerBag.Count <= (int)data.Required("trainer_bag") + extra, $"{p.Name}の袋があふれている");
                Must(p.Balls.Values.All(n => n >= 0), $"{p.Name}のボールがマイナス");
                Must(s.Phase == Phase.Battle || p.Party.Any(m => !m.Fainted), $"{p.Name}の手持ちが全滅したまま");
                foreach (var m in p.Party.Concat(p.Box))
                    Must(m.Hp >= 0 && m.Hp <= Pokemons.MaxHp(data, m), $"{m.Species}のHPが{m.Hp}");
            }
        }

        // 盤面の全マス×出目で、1マスずつ方式でたどり着ける所とハイライトの光るマスが一致するか（実際の盤面のチェック用）
        public static List<string> CompareModes(BoardData board, int maxDice, int badges)
        {
            var mismatches = new List<string>();
            foreach (int start in board.Squares.Keys)
                for (int dice = 1; dice <= maxDice; dice++)
                {
                    bool reverse = false;
                    var highlight = Movement.Reachable(board, start, dice, badges, false);
                    if (highlight.Count == 0) { reverse = true; highlight = Movement.Reachable(board, start, dice, badges, true); }
                    var stepped = new HashSet<int>();
                    StepAll(board, -1, start, dice, badges, reverse, highlight, stepped, new HashSet<(int, int, int)>());
                    if (!stepped.SetEquals(highlight)) mismatches.Add($"{board[start].Name} 出目{dice}");
                }
            return mismatches;
        }

        static void StepAll(BoardData board, int prev, int cur, int left, int badges, bool reverse, HashSet<int> targets, HashSet<int> ends, HashSet<(int, int, int)> seen)
        {
            if (!seen.Add((prev, cur, left))) return;
            if (left == 0) { ends.Add(cur); return; }
            foreach (int n in board[cur].Neighbors)
                if ((reverse || n != prev) && Movement.CanEnter(board, n, badges) && Movement.CanFinish(board, cur, n, left - 1, badges, reverse, targets))
                    StepAll(board, cur, n, left - 1, badges, reverse, targets, ends, seen);
        }

        static BoardData TestBoard()
        {
            var squares = Enumerable.Range(1, 11).Select(i => new BoardSquare { Id = i, Name = i.ToString(), Type = i == 1 ? "スタート" : "空マス" }).ToList();
            squares[6].RequiredBadges = 1;            // 7
            squares[7].WarpId = squares[8].WarpId = 1; // 8 と 9
            squares[7].Type = squares[8].Type = "ワープ";
            var problems = new List<string>();
            var board = BoardData.Create(squares, new[] { (1, 2), (2, 3), (3, 4), (3, 5), (5, 6), (6, 7), (5, 8), (10, 11) }, problems);
            if (problems.Any(p => p.StartsWith("[エラー]"))) throw new Exception("テスト用の盤面: " + string.Join(" / ", problems));
            return board;
        }

        // 移動のテスト用のデータ（ポケモンなし）
        static GameData TestData(int diceMin, int diceMax)
        {
            var data = new GameData();
            foreach (var (k, v) in new (string, double)[] { ("dice_min", diceMin), ("dice_max", diceMax), ("start_money", 500), ("start_balls", 3), ("start_level", 5),
                ("weekly_event_min", 3), ("weekly_event_max", 8), ("dividend_interval", 7), ("badge_point", 5), ("yen_per_point", 1000) })
                data.Tuning[k] = v;
            return data;
        }

        static GameProcessor NewGame(BoardData board, int dice, MoveMode mode, int players = 1, int weeks = 50) =>
            GameProcessor.NewGame(board, TestData(dice, dice), Enumerable.Range(0, players).Select(i => ($"P{i}", mode, "")).ToList(), weeks, 1, out _);

        static string DiceSequence(BoardData board, ulong seed)
        {
            var game = GameProcessor.NewGame(board, TestData(1, 10), new[] { ("A", MoveMode.Highlight, ""), ("B", MoveMode.Highlight, ""), ("C", MoveMode.Highlight, "") }, 50, seed, out _);
            var values = new List<int>(game.State.TurnOrder);
            for (int i = 0; i < 6; i++)
            {
                int p = game.State.CurrentPlayer.Id;
                var rolled = Apply(game, new RollDice { Player = p }).OfType<DiceRolled>().Single();
                values.Add(rolled.Value);
                if (game.State.Phase != Phase.Moving) continue;
                Apply(game, new ChooseDestination { Player = p, Square = game.State.Move.Reachable.First() });
                Apply(game, new ConfirmMove { Player = p });
            }
            return string.Join(",", values);
        }

        static List<GameEvent> Apply(GameProcessor game, Command c)
        {
            if (c.Player == 0 && game.State.Players.Count == 1) c.Player = game.State.CurrentPlayer.Id;
            var events = game.Apply(c, out string error);
            if (error != null) throw new Exception($"命令 {c.GetType().Name} が受け付けられませんでした: {error}");
            return events;
        }

        static string Error(GameProcessor game, Command c)
        {
            if (c.Player == 0 && game.State.Players.Count == 1) c.Player = game.State.CurrentPlayer.Id;
            game.Apply(c, out string error);
            if (error == null) throw new Exception($"命令 {c.GetType().Name} は受け付けられないはずなのに、受け付けられました");
            return error;
        }

        static bool Same(HashSet<int> set, params int[] expected) => set.SetEquals(expected);

        static void Check(List<string> log, string name, bool ok)
        {
            if (!ok) throw new Exception("テスト失敗: " + name);
            log.Add("OK " + name);
        }
    }
}
