using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // ポイント交換NPC（換金所、仕様書2.1・2.3）：所持金を勝利ポイントに換える（yen_per_point 円 → 1p）
    // 最後の exchange_close_weeks 週は閉まる（終盤に貯めたお金で一気に逃げ切るのを防ぐ）
    public sealed partial class GameProcessor
    {
        public bool ExchangeClosed => State.Week > State.MaxWeeks - TuneInt("exchange_close_weeks");

        bool LandExchange(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            if (ExchangeClosed) return Nothing($"換金所は 閉まっている（最後の{TuneInt("exchange_close_weeks")}週）", events);
            int rate = TuneInt("yen_per_point"), most = p.Money / rate;
            if (most <= 0) return Nothing($"お金が 足りない（{rate:#,0}円で 1ポイント）", events);
            var f = new FacilityState { Kind = FacilityKind.Exchange, Player = p.Id, Square = sq.Id, Prompt = $"換金所：{rate:#,0}円で 1ポイント（所持金 {p.Money:#,0}円）" };
            foreach (int n in new[] { 1, 5, 10 }.Where(n => n < most).Append(most))
            {
                f.Goods.Add(n.ToString());
                f.Options.Add(n == most ? $"全部：{n}ポイント（{n * rate:#,0}円）" : $"{n}ポイント（{n * rate:#,0}円）");
            }
            f.Goods.Add(""); f.Options.Add("換えない");
            return OpenFacility(f, events);
        }

        void ExchangeChosen(FacilityState f, int index, List<GameEvent> events)
        {
            if (index >= f.Goods.Count || f.Goods[index] == "") return;
            var p = State.Players[f.Player];
            int n = int.Parse(f.Goods[index]);
            AddMoney(p, -n * TuneInt("yen_per_point"), "換金", events);
            p.Points += n;
            events.Add(new PointsChanged { Player = p.Id, Amount = n });
        }

        // ロケット団最多討伐賞（2.1）：ゲーム終了時、ロケット団を追いはらった回数がいちばん多い人へ（同数なら全員）
        public double RocketAward(PlayerState p)
        {
            int most = State.Players.Max(x => x.RocketDefeats);
            return most > 0 && p.RocketDefeats == most ? Tune("rocket_award") : 0;
        }
    }
}
