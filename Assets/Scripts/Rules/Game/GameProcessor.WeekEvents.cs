using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 週イベントのうち、ポケニュース・ポケニュース特番・突発バトルイベント（仕様書5.3）
    // ポケニュース：event_weeks 週、指定の道路の草むらで「レア枠」（その場所でいちばん出現率が低い行）が news_rare_mult 倍 出やすい
    // ポケニュース特番：event_weeks 週、指定の道路の草むらで news_legend_rate の確率で 準伝説（エンテイ・スイクン・ライコウ・ラティアス・ラティオス）
    // 突発バトルイベント：週のはじめに 全員でミニトーナメント（クイックバトル）。優勝するとポケベル
    public sealed partial class GameProcessor
    {
        static readonly string[] NewsLegends = { "エンテイ", "スイクン", "ライコウ", "ラティアス", "ラティオス" };

        // ニュースで指定する道路：盤面に草むらがあって、出現表に草むらがある場所（道路を優先）
        string PickNewsPlace()
        {
            var places = board.Squares.Values.Where(s => s.Type == "草むら" && !s.Sea).Select(s => s.Place).Distinct()
                .Where(pl => data.Encounters.Any(e => e.Place == pl && e.Method == "草むら")).OrderBy(pl => pl).ToList();
            var roads = places.Where(pl => data.Places.FirstOrDefault(x => x.Name == pl)?.Kind == "道路").ToList();
            var pool = roads.Count > 0 ? roads : places;
            return pool.Count == 0 ? null : pool[Roll(0, pool.Count - 1)];
        }

        // 週イベントを起こす。トーナメントが始まって 手番を始めるのを待つときは false
        internal bool ApplyWeekEvent(string name, List<GameEvent> events)
        {
            int weeks = TuneInt("event_weeks");
            switch (name)
            {
                case "雨季": case "乾季": case "すなあらしの日":
                    State.Weather = name; State.WeatherWeeks = weeks;
                    events.Add(new WeekEventHappened { Name = name, Weeks = weeks });
                    return true;
                case "セール": case "ストライキ":
                    State.WeekEvent = name; State.WeekEventWeeks = weeks;
                    events.Add(new WeekEventHappened { Name = name, Weeks = weeks });
                    return true;
                case "ポケニュース": case "ポケニュース特番":
                {
                    string place = PickNewsPlace();
                    if (place == null) return true;
                    State.NewsKind = name; State.NewsPlace = place; State.NewsWeeks = weeks;
                    events.Add(new WeekEventHappened { Name = $"{name}（{place}）", Weeks = weeks });
                    return true;
                }
                case "突発バトルイベント":
                    return !StartTournament(events);
                default:
                    return true;
            }
        }

        // ---- ポケニュース（StartWild から）----

        // 出現表の重み：ポケニュースの場所なら、レア枠を news_rare_mult 倍
        int NewsWeight(Encounter e, List<Encounter> rows)
        {
            int w = e.Rate ?? 0;
            if (State.NewsKind != "ポケニュース" || e.Place != State.NewsPlace || e.Method != "草むら") return w;
            int rarest = rows.Min(x => x.Rate ?? 0);
            return w == rarest ? (int)Math.Round(w * Tune("news_rare_mult")) : w;
        }

        // 特番の場所の草むらなら、確率で準伝説（Lv は その場所の最高Lv＋news_legend_level_bonus）
        (string species, int level)? NewsLegend(BoardSquare sq, string method)
        {
            if (State.NewsKind != "ポケニュース特番" || sq.Place != State.NewsPlace || method != "草むら") return null;
            var legends = NewsLegends.Where(n => data.FindSpecies(n) != null).ToList();
            if (legends.Count == 0 || !Chance(Tune("news_legend_rate"))) return null;
            int top = data.Encounters.Where(e => e.Place == sq.Place).Select(e => e.MaxLevel).DefaultIfEmpty(5).Max();
            return (legends[Roll(0, legends.Count - 1)], Math.Min(top + TuneInt("news_legend_level_bonus"), TuneInt("level_max")));
        }

        // ---- 突発バトルイベント（ミニトーナメント）----

        // はじめる。戦える人が2人いなければ何もしない（false）
        internal bool StartTournament(List<GameEvent> events)
        {
            var entrants = State.Players.Where(p => p.Party.Any(m => !m.Fainted)).Select(p => p.Id).ToList();
            if (entrants.Count < 2) return false;
            for (int i = entrants.Count - 1; i > 0; i--) { int j = Roll(0, i); (entrants[i], entrants[j]) = (entrants[j], entrants[i]); }
            State.Tournament = new TournamentState();
            State.Tournament.Entrants.AddRange(entrants);
            events.Add(new TournamentStarted { Players = entrants.ToList() });
            return NextTournamentMatch(events);
        }

        // 次の試合を始める（あれば true）。なければ優勝を決めて、週の最初の手番を始める
        bool NextTournamentMatch(List<GameEvent> events)
        {
            var t = State.Tournament;
            while (true)
            {
                if (t.Next + 1 < t.Entrants.Count)
                {
                    int a = t.Entrants[t.Next], b = t.Entrants[t.Next + 1];
                    t.Next += 2;
                    var you = State.Players[b];
                    StartBattle(BattleKind.Tournament, false, new BattleSide { Player = you.Id, Name = you.Name, Team = you.Party }, events, null, a);
                    return true;
                }
                if (t.Next < t.Entrants.Count) { t.Advanced.Add(t.Entrants[t.Next]); t.Next++; } // ひとりあまったら不戦勝
                if (t.Advanced.Count <= 1) break;
                t.Entrants.Clear(); t.Entrants.AddRange(t.Advanced); t.Advanced.Clear(); t.Next = 0;
            }
            // 優勝：ポケベル。ponytail: 袋がいっぱいでも入れる（そのあと拾えないだけ。袋の画面を出すと週の始まりが乱れるため）
            var winner = State.Players[t.Advanced[0]];
            PutItem(winner, "ポケベル");
            events.Add(new TournamentWon { Player = winner.Id, Item = "ポケベル" });
            State.Tournament = null;
            return false;
        }

        // 試合が終わった（EndBattle から）。勝った人が次に進む。両方とも回復する
        void TournamentMatchEnded(BattleState b, int winner, List<GameEvent> events)
        {
            State.Tournament.Advanced.Add(b.Sides[winner].Player);
            foreach (var s in b.Sides) foreach (var m in s.Team) Pokemons.HealFull(data, m);
            State.Battle = null;
            State.Phase = Phase.Prepare;
            if (NextTournamentMatch(events)) return;
            StartTurn(events); // トーナメントのあとで、週の最初の人の手番
        }
    }
}
