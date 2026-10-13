using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 海上マスとつり（仕様書4.1.2）
    // 海上マス：草むらマスの中の種類「海上」。だれでも行き先に選べる。止まると出現表の「水上」から野生（BoardSquare.Sea）
    // つり：海上マスのとなりにいるとき、準備フェーズで「移動」の代わりに選べる。動かずに、出現表の「つり」から野生とバトル。その手番は終わる
    public sealed partial class GameProcessor
    {
        // 今いるマスのとなりの海上マス（なければ null）
        BoardSquare SeaNextTo(PlayerState p) => board[p.Square].Neighbors.Select(n => board[n]).FirstOrDefault(s => s.Sea);

        string CheckFish(PlayerState p) => SeaNextTo(p) == null ? "つりは 海上マスの となりで できます" : null;

        void DoFish(List<GameEvent> events)
        {
            var p = Current;
            events.Add(new Fished { Player = p.Id });
            // 出現表は 今いる場所の「つり」。なければ となりの海上マスの場所の「つり」
            if (StartWild(board[p.Square], events, "つり") || StartWild(SeaNextTo(p), events, "つり")) return;
            Nothing("なにも つれなかった", events);
            EndTurn(events);
        }
    }
}
