using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 町の人のイベントマス（イベントマス.csv の効果の型）。セリフは イベント文言.csv の ev_errand_* / ev_bill_* / ev_trade_* / ev_daycare_*
    // お使い：止まると頼まれ、研究室に止まると「値」個のモンスターボールをもらえる（1人1回）
    // 控えの上限：マサキの家。止まると その人の控えの上限が「値」体になる（ふだんは調整値 box_max）
    // 交換：ライバルマスと同じしくみ（コストが足りる子を出す）。値に「バリヤード Lv15、カモネギ Lv15」。1人1回ずつ、通信ケーブルは要らない
    // 育て屋：控えから1体あずけると daycare_weeks_per_level 週ごとに 1Lv 育つ。もう一度止まると引き取れる（1人1体）
    public sealed partial class GameProcessor
    {
        const string NpcTradeId = "交換", DayCareId = "育て屋";

        public int BoxLimit(PlayerState p) => Math.Max(TuneInt("box_max"), p.BoxMax);

        Effect EffectOf(BoardSquare sq) => data.EventSquares.FirstOrDefault(x => x.Square == sq.Name)?.Effect;

        // ---- お使い ----

        bool LandErrand(Effect e, List<GameEvent> events)
        {
            var p = Current;
            if (p.ErrandStage == 2) return Nothing("お使いは もう すんでいる", events);
            if (p.ErrandStage == 1) { Say("ev_errand_waiting", events, p); return false; }
            p.ErrandStage = 1;
            Say("ev_errand", events, p, ("数", e.Value));
            return false;
        }

        // 研究室に止まったとき：届ける途中なら、お礼のモンスターボール
        void LabErrand(PlayerState p, List<GameEvent> events)
        {
            if (p.ErrandStage != 1) return;
            var e = data.EventSquares.FirstOrDefault(x => x.Effect.Kind == EffectKind.Errand)?.Effect;
            int n = e != null && int.TryParse(e.Value, out int v) ? v : 0;
            p.ErrandStage = 2;
            p.Balls["モンスターボール"] = (p.Balls.TryGetValue("モンスターボール", out int had) ? had : 0) + n;
            Say("ev_errand_done", events, p, ("数", n.ToString()));
            events.Add(new ItemGained { Player = p.Id, Item = $"モンスターボール{n}個" });
        }

        // ---- マサキの家（控えの上限）----

        bool LandBoxUp(Effect e, List<GameEvent> events)
        {
            var p = Current;
            int n = int.Parse(e.Value);
            if (BoxLimit(p) >= n) { Say("ev_bill_again", events, p); return Nothing("何も起きなかった", events); }
            p.BoxMax = n;
            Say("ev_bill", events, p, ("数", n.ToString()));
            events.Add(new BoxExpanded { Player = p.Id, Max = n });
            return false;
        }

        // ---- 交換 ----

        // まだ交換していない、もらえるポケモン：(種族, Lv, 渡す子に要るコスト)
        public List<(string species, int level, double need)> NpcTrades(PlayerState p, BoardSquare sq)
        {
            var e = EffectOf(sq);
            if (e == null || (e.Kind != EffectKind.Trade && e.Kind != EffectKind.Lab)) return new List<(string, int, double)>();
            return GameData.TradeList(e.Value).Where(t => data.FindSpecies(t.species) != null && !p.RocketUsed.Contains(NpcTradeKey(sq, t.species)))
                .Select(t => (t.species, t.level, Pokemons.Cost10(data, Pokemons.Create(data, t.species, t.level, "", "", 0)) * Tune("rival_trade_cost") / 10.0)).ToList();
        }

        static string NpcTradeKey(BoardSquare sq, string species) => $"npc:{sq.Id}:{species}";

        // greet：止まったとき（交換のあとの開き直しでは false）
        bool LandNpcTrade(BoardSquare sq, bool greet, List<GameEvent> events)
        {
            var p = Current;
            var list = NpcTrades(p, sq);
            if (list.Count == 0) { if (greet) Say("ev_trade_done", events, p); return false; }
            if (greet) Say("ev_trade", events, p);
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = sq.Id, EventId = NpcTradeId, Prompt = "どの子と 交換する？（1人1回ずつ）" };
            foreach (var t in list) { f.Options.Add($"{t.species} Lv{t.level}（コスト{t.need:0.#}以上の子と）"); f.Goods.Add(t.species); }
            f.Options.Add("やめる");
            return OpenFacility(f, events);
        }

        // もらう子を選んだ → 渡す子を選ぶ画面へ（TradeOption が受ける）。まだ操作待ちなら true
        bool NpcTradeChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            var sq = board[f.Square];
            if (index >= f.Goods.Count) return false; // やめる
            return StartNpcTrade(p, sq, f.Goods[index], events);
        }

        // もらう子が決まった → 渡す子を選ぶ画面へ（研究所からも呼ぶ）
        bool StartNpcTrade(PlayerState p, BoardSquare sq, string species, List<GameEvent> events)
        {
            var t = NpcTrades(p, sq).First(x => x.species == species);
            if (TradeCandidates(p, t.need).Count == 0) { Say("ev_trade_short", events, p, ("コスト", t.need.ToString("0.#"))); return false; }
            var tf = new FacilityState { Kind = FacilityKind.Trade, Player = p.Id, Square = sq.Id, EventId = "npc:" + sq.Id, PendingItem = t.species, Price = t.level, Prompt = $"{t.species}と 交換する子を えらぶ（コスト{t.need:0.#}以上）" };
            FillTradeOptions(tf, p, t.need);
            return OpenFacility(tf, events);
        }

        // ---- 格闘道場（効果の型「道場」）----
        // 値のバトル相手（からておう）に勝つと、報酬から1つ選ぶ：職（からておう）に就く／ポケモン（エビワラー・サワムラー）と交換
        // 交換はライバルマスと同じく コストが足りる子を出す。どの子もゲーム中に1体。職は 職業マス.csv の条件（バッジ6個など）を満たすときだけ
        // 「1人1回」は 受け取ったときに数える（勝っても選べなかったら また来られる）
        const string DojoId = "格闘道場";

        int DojoLevel => TuneInt("event_reward_level");
        double DojoNeed(string species) => Pokemons.Cost10(data, Pokemons.Create(data, species, DojoLevel, "", "", 0)) * Tune("rival_trade_cost") / 10.0;

        // いま選べるもの（職の名前か ポケモンの名前）
        List<string> DojoChoices(PlayerState p, Effect e) => e.Reward.Split('|').Where(r =>
        {
            if (data.FindSpecies(r) != null) return !State.EventsGone.Contains("dojo:" + r);
            var rule = data.JobSquares.FirstOrDefault(x => x.Job == r);
            return p.Job != r && (rule == null || JobConditionsMet(p, rule));
        }).ToList();

        bool DojoWon(PlayerState p, Effect e, List<GameEvent> events)
        {
            var list = DojoChoices(p, e);
            if (list.Count == 0) return false;
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = DojoId, PendingItem = e.Id, Prompt = "どちらかを えらぶ（1人1回）" };
            foreach (var r in list)
            {
                f.Goods.Add(r);
                f.Options.Add(data.FindSpecies(r) != null ? $"{r} Lv{DojoLevel}と 交換（コスト{DojoNeed(r):0.#}以上の子と。ゲーム中に1体）" : $"{r}の 職に 就く");
            }
            f.Goods.Add(""); f.Options.Add("やめておく");
            return OpenFacility(f, events);
        }

        // 選んだ。交換なら 渡す子を選ぶ画面へ（TradeOption が受ける）。まだ操作待ちなら true
        bool DojoChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            var e = FindEffect(f.PendingItem);
            string pick = f.Goods[index];
            if (pick == "") return false;
            if (data.FindSpecies(pick) == null)
            {
                EventOnceDone(e, p);
                UnlockJob(p, pick, events);
                SetJob(p, pick, events);
                return false;
            }
            double need = DojoNeed(pick);
            if (TradeCandidates(p, need).Count == 0) { Say("ev_trade_short", events, p, ("コスト", need.ToString("0.#"))); return DojoWon(p, e, events); }
            var t = new FacilityState { Kind = FacilityKind.Trade, Player = p.Id, Square = f.Square, EventId = "dojo:" + e.Id, PendingItem = pick, Price = DojoLevel, Prompt = $"{pick}と 交換する子を えらぶ（コスト{need:0.#}以上）" };
            FillTradeOptions(t, p, need);
            return OpenFacility(t, events);
        }

        // ---- サファリゾーン（別マップ safari）----
        // 止まったマスで出入りを見る。入るときに入場料 safari_fee を払い、サファリボールを safari_balls 個もらう。出ると 余ったボールは なくなる
        // 中の草むらでは 入るたびに1回だけ 野生に会える。戦わずに サファリボールを投げ、はずれると safari_flee_rate で にげられる
        const string SafariMap = "safari", SafariBall = "サファリボール";

        // 入場料が払えず 外へ出したなら true
        bool SafariGate(PlayerState p, BoardSquare sq, List<GameEvent> events)
        {
            bool inside = sq.MapId == SafariMap;
            if (inside == p.InSafari) return false;
            p.InSafari = inside;
            if (!inside) { p.Balls.Remove(SafariBall); return false; } // 余ったボールは持ち出せない
            int fee = TuneInt("safari_fee");
            var gate = board.Squares.Values.FirstOrDefault(s => s.MapId == SafariMap && s.WarpPartner >= 0 && board[s.WarpPartner].MapId != SafariMap);
            if (p.Money < fee && gate != null)
            {
                p.InSafari = false;
                events.Add(new NothingHappened { Player = p.Id, Reason = $"サファリゾーンの 入場料（{fee:#,0}円）が 足りないので 入れなかった" });
                int from = p.Square;
                p.Square = gate.WarpPartner;
                events.Add(new Warped { Player = p.Id, From = from, To = p.Square });
                return true;
            }
            AddMoney(p, -fee, "サファリゾーンの入場料", events);
            int n = TuneInt("safari_balls");
            p.Balls[SafariBall] = n;
            events.Add(new ItemGained { Player = p.Id, Item = $"{SafariBall}{n}個" });
            return false;
        }

        void SafariPrompt(List<GameEvent> events)
        {
            B.Step = BattleStep.Ball;
            events.Add(new BallPrompt { Player = B.Sides[0].Player });
        }

        // はずれた：ボールがなければ おしまい、あれば にげられるか もう1球
        void SafariMissed(List<GameEvent> events)
        {
            var p = State.Players[B.Sides[0].Player];
            string wild = B.Sides[1].Current.Species;
            if (!(p.Balls.TryGetValue(SafariBall, out int n) && n > 0)) { SafariOver($"サファリボールが なくなった。{wild}は どこかへ いってしまった", events); return; }
            if (Chance(Tune("safari_flee_rate"))) { SafariOver($"野生の{wild}は にげだした！", events); return; }
            SafariPrompt(events);
        }

        void SafariOver(string reason, List<GameEvent> events)
        {
            var p = State.Players[B.Sides[0].Player];
            p.Balls.Remove(SafariBall); // 遭遇は 入るたびに1回
            events.Add(new NothingHappened { Player = p.Id, Reason = reason });
            CureAfterBattle(events);
            events.Add(new BattleEnded { Winner = -1 });
            FinishBattle(events);
        }

        // ---- 育て屋 ----

        // 引き取るときのLv（あずけてからの週数で育つ）
        public int DayCareLevel(PlayerState p) =>
            p.DayCare == null ? 0 : Math.Min(TuneInt("level_max"), p.DayCare.Level + (State.Week - p.DayCareWeek) / Math.Max(1, TuneInt("daycare_weeks_per_level")));

        bool LandDayCare(List<GameEvent> events)
        {
            var p = Current;
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = DayCareId };
            if (p.DayCare != null)
            {
                Say("ev_daycare_back", events, p, ("ポケモン", p.DayCare.Species), ("Lv", DayCareLevel(p).ToString()));
                f.Prompt = $"{p.DayCare.Species}を 引き取る？";
                f.Options.Add($"引き取る：{p.DayCare.Species} Lv{DayCareLevel(p)}");
                f.Options.Add("そのまま あずけておく");
                return OpenFacility(f, events);
            }
            if (p.Box.Count == 0) { Say("ev_daycare_none", events, p); return Nothing("控えに ポケモンが いない", events); }
            Say("ev_daycare", events, p, ("週", TuneInt("daycare_weeks_per_level").ToString()));
            f.Prompt = "控えから どの子を あずける？";
            foreach (var m in p.Box) f.Options.Add($"あずける：{m.Species} Lv{m.Level}");
            f.Options.Add("やめておく");
            return OpenFacility(f, events);
        }

        // あずける子・引き取るかを選んだ。まだ操作待ちなら true
        bool DayCareChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            if (p.DayCare != null)
            {
                if (index != 0) return false; // そのまま あずけておく
                var mon = p.DayCare;
                int level = DayCareLevel(p);
                if (level > mon.Level) Pokemons.GainExp(data, mon, Pokemons.ExpForLevel(level) - mon.Exp);
                p.DayCare = null;
                Say("ev_daycare_return", events, p, ("ポケモン", mon.Species), ("Lv", mon.Level.ToString()));
                events.Add(new PokemonReceived { Player = p.Id, Pokemon = mon.Species, From = DayCareId, ToBox = p.Box.Count < BoxLimit(p) });
                if (p.Box.Count < BoxLimit(p)) { p.Box.Add(mon); return false; } // もとの控えに戻す
                return AddPokemon(p, mon, events);
            }
            if (index >= p.Box.Count) return false; // やめておく
            var m = p.Box[index];
            p.Box.RemoveAt(index);
            p.DayCare = m;
            p.DayCareWeek = State.Week;
            events.Add(new PokemonGiven { Player = p.Id, Pokemon = m.Species, To = DayCareId });
            Say("ev_daycare_deposit", events, p, ("ポケモン", m.Species));
            return false;
        }
    }
}
