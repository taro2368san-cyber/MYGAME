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

        // ---- 職業マス（9.2）。職業マス.csv の職業ごとに、ゲーム開始時にトレーナーマスを1つ選ぶ ----

        // 配置候補の場所のうち、盤面にトレーナーマスがある場所からランダムに1か所、その中のトレーナーマスからランダムに1つ
        // 候補の場所がまだ盤面になければ、どこかのトレーナーマス。1つのマスに職業は1つだけ
        void PlaceJobSquares()
        {
            var trainers = board.Squares.Values.Where(s => s.Type == "トレーナー").OrderBy(s => s.Id).ToList();
            foreach (var rule in data.JobSquares)
            {
                var free = trainers.Where(s => !State.JobSquares.ContainsKey(s.Id)).ToList();
                var places = rule.Places.Where(pl => free.Any(s => s.Place == pl)).ToList();
                string place = places.Count > 0 ? places[Roll(0, places.Count - 1)] : null; // くじは1回だけ（Where の中で引くと マスごとに引き直してしまう）
                var pool = place != null ? free.Where(s => s.Place == place).ToList() : free;
                if (pool.Count == 0) continue;
                State.JobSquares[pool[Roll(0, pool.Count - 1)].Id] = rule.Job;
            }
        }

        // 挑む条件：バッジの数、手持ちと控えの対象タイプのLv合計、図鑑の登録数（0 は見ない）
        public bool JobConditionsMet(PlayerState p, JobSquareRule r) =>
            p.Badges >= r.Badges && p.Dex.Count >= r.Dex
            && (r.TypeLevels <= 0 || p.Party.Concat(p.Box).Where(m => Pokemons.Of(data, m).Type == r.Type).Sum(m => m.Level) >= r.TypeLevels);

        // このマスで いま職業に挑めるなら、その職業（まだ解放していなくて、条件を満たしている）
        public JobSquareRule JobChallengeOpen(PlayerState p, int square)
        {
            if (!State.JobSquares.TryGetValue(square, out string job) || p.UnlockedJobs.Contains(job)) return null;
            var r = data.JobSquares.FirstOrDefault(x => x.Job == job);
            return r != null && JobConditionsMet(p, r) ? r : null;
        }

        // 職業トレーナーとバトル：相手のタイプのポケモン1体（野生で出るものから。なければそのタイプ全部から）
        // Lv は その場所のトレーナーのLv（表になければ出現表の最高Lv＋1）＋ job_level_bonus
        bool StartJobBattle(BoardSquare sq, JobSquareRule r, List<GameEvent> events)
        {
            var p = Current;
            var entry = data.Trainers.FirstOrDefault(t => t.Square == sq.Name);
            if (!TrainerMon(sq, entry, out var local)) local = ("", data.Trainers.Where(t => t.Place == sq.Place && t.Level != null).Select(t => t.Level.Value).DefaultIfEmpty(5).Max());
            int level = System.Math.Min(local.level + TuneInt("job_level_bonus"), TuneInt("level_max"));
            var wild = new HashSet<string>(data.Encounters.Select(e => e.Pokemon));
            var ofType = data.Species.Where(s => r.FoeType == "" || s.Type == r.FoeType).ToList();
            var pool = ofType.Where(s => wild.Contains(s.Name)).ToList();
            if (pool.Count == 0) pool = ofType;
            if (pool.Count == 0) return false;
            string species = pool[Roll(0, pool.Count - 1)].Name;
            events.Add(new JobChallenge { Player = p.Id, Job = r.Job });
            StartBattle(BattleKind.Job, false, Npc($"{r.Job}の トレーナー", new[] { (species, level) }), events);
            B.Job = r.Job;
            return true;
        }

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
