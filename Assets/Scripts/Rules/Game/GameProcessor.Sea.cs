using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 海上マスとつり（仕様書4.1.2）
    // 海上マス：草むらマスの中の種類「海上」。なみのりを覚えた人だけが入れる（Movement.CanEnter の surf）。止まると出現表の「水上」から野生（BoardSquare.Sea）
    // なみのり：道具「ひでんマシン なみのり」をもらうと覚える。袋には入らない（すてる・とられる・わたすことがない）
    // つり：海上マスから道で2マス以内にいて「つりざお」を持っているとき、準備フェーズで「移動」の代わりに選べる（なみのりは要らない）。つりざおを1本使い、動かずに、出現表の「つり」から野生とバトル。その手番は終わる
    public sealed partial class GameProcessor
    {
        public const string SurfItem = "ひでんマシン なみのり";
        public const string FishingRod = "つりざお"; // 使うとなくなる（トレーナーの道具）

        string CheckFish(PlayerState p) => !p.TrainerBag.Contains(FishingRod) ? "つりざおを 持っていません" : !board.CanFish(p.Square) ? "つりは 海上マスから 2マス以内で できます" : null;

        void DoFish(List<GameEvent> events)
        {
            var p = Current;
            p.TrainerBag.Remove(FishingRod);
            events.Add(new Fished { Player = p.Id });
            // 出現表は 今いる場所の「つり」。なければ 近くの海上マスの場所の「つり」
            if (StartWild(board[p.Square], events, "つり") || StartWild(board.FishingSea(p.Square), events, "つり")) return;
            Nothing("なにも つれなかった", events);
            EndTurn(events);
        }
    }
}
