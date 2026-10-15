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
            if (e == null || e.Kind != EffectKind.Trade) return new List<(string, int, double)>();
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
            var t = NpcTrades(p, sq).First(x => x.species == f.Goods[index]);
            if (TradeCandidates(p, t.need).Count == 0) { Say("ev_trade_short", events, p, ("コスト", t.need.ToString("0.#"))); return false; }
            var tf = new FacilityState { Kind = FacilityKind.Trade, Player = p.Id, Square = sq.Id, EventId = "npc:" + sq.Id, PendingItem = t.species, Price = t.level, Prompt = $"{t.species}と 交換する子を えらぶ（コスト{t.need:0.#}以上）" };
            FillTradeOptions(tf, p, t.need);
            return OpenFacility(tf, events);
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
