using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 海上マスとつり（仕様書4.1.2）
    // 海上マス：「ひでんマシン なみのり」を持っている人だけ入れる。持っていない人には、通れないマスとして経路から外す
    // つり：海上マスのとなりにいるとき、準備フェーズで「移動」の代わりに選べる。動かずに、出現表の「つり」から野生とバトル。その手番は終わる
    public sealed partial class GameProcessor
    {
        public const string SurfItem = "ひでんマシン なみのり";

        public bool CanSurf(PlayerState p) => p.TrainerBag.Contains(SurfItem) || p.PokemonBag.Contains(SurfItem);

        HashSet<int> seaSquares;
        HashSet<int> SeaSquares => seaSquares ?? (seaSquares = new HashSet<int>(board.Squares.Values.Where(s => s.Type == "海上").Select(s => s.Id)));

        // その人が入れないマス：カビゴンのいるマス（アマイ三ツ）と、なみのりがなければ海上マス
        ICollection<int> BlockedFor(PlayerState p)
        {
            if (CanSurf(p) || SeaSquares.Count == 0) return State.Blocked.Keys;
            var set = new HashSet<int>(State.Blocked.Keys);
            set.UnionWith(SeaSquares);
            return set;
        }

        // 今いるマスのとなりの海上マス（なければ null）
        BoardSquare SeaNextTo(PlayerState p) => board[p.Square].Neighbors.Where(n => SeaSquares.Contains(n)).Select(n => board[n]).FirstOrDefault();

        string CheckFish(PlayerState p) => SeaNextTo(p) == null ? "つりは 海上マスの となりで できます" : null;

        void DoFish(List<GameEvent> events)
        {
            var p = Current;
            events.Add(new Fished { Player = p.Id });
            // 出現表は 今いる場所の「つり」。なければ となりの海上マスの場所の「つり」
            var here = board[p.Square];
            if (StartWild(here, events, "つり") || StartWild(SeaNextTo(p), events, "つり")) return;
            Nothing("なにも つれなかった", events);
            EndTurn(events);
        }
    }
}
