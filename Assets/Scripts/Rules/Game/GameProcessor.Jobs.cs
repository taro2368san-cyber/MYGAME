using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 特殊な職業（仕様書9.1、9.5）：だいすきくらぶ・ロケット団・サイクリング。効きめの数字は 職の補正.csv の
    // 「捕獲」「道具枠」「追加の歩数」「センターで回復」「交換のコスト」の列。ステータスの倍率は JobBonusFor
    public sealed partial class GameProcessor
    {
        // その職業の、タイプを問わない行（特殊な効きめはここに書く）
        JobBonus JobRule(PlayerState p) => p.Job == "" ? null : data.JobBonuses.FirstOrDefault(b => b.Job == p.Job && b.Type == "");

        // 袋に入る数（ロケット団は増える）
        public int BagLimit(PlayerState p, Bag bag) => TuneInt(bag == Bag.Pokemon ? "pokemon_bag" : "trainer_bag") + (JobRule(p)?.Bag ?? 0);

        // ボールの閾値に足す分（だいすきくらぶ）
        double JobCaptureBonus(PlayerState p) => JobRule(p)?.Capture ?? 0;

        // サイコロの目に足して進める歩数の上限（サイクリング）
        int JobExtraSteps(PlayerState p) => JobRule(p)?.ExtraSteps ?? 0;

        // ポケモンセンター・研究室で回復できない（ロケット団。入れ替え・もちもの・転職はできる、9.4）
        bool JobNoCenterHeal(PlayerState p) => JobRule(p)?.NoCenterHeal == true;

        // プレイヤー戦の勝利報酬で、コストに関係なく相手のポケモンをもらえる（ロケット団、9.4）
        bool JobFreeTrade(PlayerState p) => JobRule(p)?.FreeTrade == true;

        // サイクリングの解放：17番道路（サイクリングロード）のトレーナーを3人以上ファンにして、自転車を30回以上使った
        const string CyclingRoad = "17番道路";
        void CheckCyclingUnlock(PlayerState p, List<GameEvent> events)
        {
            if (p.UnlockedJobs.Contains("サイクリング") || !data.Jobs.Any(j => j.Name == "サイクリング")) return;
            int fans = State.Owners.Count(o => o.Value == p.Id && board.Squares.TryGetValue(o.Key, out var s) && s.Type == "トレーナー" && s.Place == CyclingRoad);
            if (p.BikeUses >= TuneInt("cycling_bike_uses") && fans >= TuneInt("cycling_road_fans")) UnlockJob(p, "サイクリング", events);
        }

        // ロケット団のスカウト（最下位の救済、13.5）：バッジ6個以上で、50週つづけて最下位
        void CheckRocketScout(PlayerState p, List<GameEvent> events)
        {
            if (p.UnlockedJobs.Contains("ロケット団") || !data.Jobs.Any(j => j.Name == "ロケット団")) return;
            if (p.Badges >= TuneInt("rocket_scout_badges") && p.LastPlaceWeeks >= TuneInt("rocket_scout_weeks")) UnlockJob(p, "ロケット団", events);
        }
    }
}
