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
            // 何ポイント換えるかは ExchangePoints 命令で選ぶ（1,000円単位）。換えないなら LeaveFacility
            return OpenFacility(new FacilityState { Kind = FacilityKind.Exchange, Player = p.Id, Square = sq.Id, Prompt = $"換金所：{rate:#,0}円で 1ポイント（所持金 {p.Money:#,0}円）" }, events);
        }

        // いま換えられる最大のポイント
        public int ExchangeMax(PlayerState p) => p.Money / TuneInt("yen_per_point");

        string CheckExchange(PlayerState p, FacilityState f, int points)
        {
            if (f.Kind != FacilityKind.Exchange) return "ここは換金所ではありません";
            if (points < 1) return "1ポイント以上 選んでください";
            return points <= ExchangeMax(p) ? null : "お金が足りません";
        }

        void DoExchange(PlayerState p, int points, List<GameEvent> events)
        {
            AddMoney(p, -points * TuneInt("yen_per_point"), "換金", events);
            p.Points += points;
            events.Add(new PointsChanged { Player = p.Id, Amount = points });
        }

        // ロケット団最多討伐賞（2.1）：ゲーム終了時、ロケット団を追いはらった回数がいちばん多い人へ（同数なら全員）
        public double RocketAward(PlayerState p)
        {
            int most = State.Players.Max(x => x.RocketDefeats);
            return most > 0 && p.RocketDefeats == most ? Tune("rocket_award") : 0;
        }
    }
}
