using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sugoroku.Rules
{
    // ニビ科学博物館（仕様書3.2.2）。イベントマス.csv の効果の型「博物館」のマス
    // 化石を預けると fossil_hatch_weeks 週あとに孵る。受け取るのは預けた本人が止まったとき。1人1個まで
    // 化石から孵るポケモンは、アイテム一覧の効果の文「…○○に孵る」から読む
    // グレンの研究所：効果の型「研究所」。博物館と同じ化石（預けた化石は 博物館・研究所の どちらで受け取ってもよい）＋ 値のポケモンと交換（交換マスと同じ。1人1回ずつ）
    // セリフは 博物館が ev_museum_*、研究所が ev_lab_*（続きの名前は同じ）
    public sealed partial class GameProcessor
    {
        const string MuseumId = "博物館";
        const string TradeGood = "交換:"; // 研究所の選択肢で、交換する子の印

        // 化石 → 孵るポケモン（読めなければ null）
        public string FossilSpecies(string item)
        {
            var m = Regex.Match(data.FindItem(item)?.Effect ?? "", @"([^\s、。に]+)に孵る");
            return m.Success && data.FindSpecies(m.Groups[1].Value) != null ? m.Groups[1].Value : null;
        }

        int FossilWeeksLeft(PlayerState p) => Math.Max(0, TuneInt("fossil_hatch_weeks") - (State.Week - p.FossilWeek));

        bool IsLab(BoardSquare sq) => EffectOf(sq)?.Kind == EffectKind.Lab;

        // イベントマス.csv の「内容」（研究所・博物館の名前）。空なら fallback
        string EventTitle(BoardSquare sq, string fallback) => data.EventSquares.FirstOrDefault(x => x.Square == sq.Name)?.Content is string c && c != "" ? c : fallback;

        bool LandMuseum(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            bool lab = IsLab(sq);
            string key = lab ? "ev_lab" : "ev_museum";
            string from = EventTitle(sq, "博物館");
            if (p.Fossil != "")
            {
                string species = FossilSpecies(p.Fossil);
                int left = FossilWeeksLeft(p);
                if (left > 0)
                {
                    Say(key + "_waiting", events, p, ("ポケモン", species), ("週", left.ToString()));
                    if (!lab) return false;
                }
                else
                {
                    // 孵った：Lv は手持ち・控えの最高Lv＋fossil_level_offset（最低5）
                    int top = p.Party.Concat(p.Box).Select(m => m.Level).DefaultIfEmpty(5).Max();
                    var mon = Pokemons.Create(data, species, Math.Max(5, Math.Min(top + TuneInt("fossil_level_offset"), TuneInt("level_max"))), from + "で目覚めた", sq.Place, State.Week);
                    p.Fossil = "";
                    p.Dex.Add(species);
                    Say(key + "_hatch", events, p, ("ポケモン", species));
                    events.Add(new PokemonReceived { Player = p.Id, Pokemon = species, From = from, ToBox = p.Party.Count >= TuneInt("party_size") });
                    if (AddPokemon(p, mon, events)) return true;
                }
            }
            else Say(key, events, p);
            // 預ける：袋の化石から選ぶ（まだ預けていれば出さない）。研究所は 交換も ならべる
            var fossils = p.Fossil != "" ? new List<string>() : p.PokemonBag.Concat(p.TrainerBag).Where(x => FossilSpecies(x) != null).Distinct().ToList();
            var trades = lab ? NpcTrades(p, sq) : new List<(string species, int level, double need)>();
            if (fossils.Count == 0 && p.Fossil == "") Say(key + "_none", events, p);
            if (fossils.Count == 0 && trades.Count == 0) return false;
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = sq.Id, EventId = MuseumId, Prompt = lab ? "化石を あずける？ ポケモンを 交換する？" : "どの化石を あずける？（1人1個まで）" };
            foreach (var x in fossils) { f.Options.Add($"あずける：{x}（{FossilSpecies(x)}）"); f.Goods.Add(x); }
            foreach (var t in trades) { f.Options.Add($"交換：{t.species} Lv{t.level}（コスト{t.need:0.#}以上の子と。1人1回）"); f.Goods.Add(TradeGood + t.species); }
            f.Options.Add("やめておく");
            return OpenFacility(f, events);
        }

        // 預ける化石（研究所なら 交換でもらう子）を選んだ。まだ操作待ちなら true
        bool MuseumChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            if (index >= f.Goods.Count) return false; // やめておく
            string item = f.Goods[index];
            if (item.StartsWith(TradeGood)) return StartNpcTrade(p, board[f.Square], item.Substring(TradeGood.Length), events);
            RemoveItem(p, item);
            p.Fossil = item;
            p.FossilWeek = State.Week;
            Say((IsLab(board[f.Square]) ? "ev_lab" : "ev_museum") + "_deposit", events, p, ("アイテム", item), ("週", TuneInt("fossil_hatch_weeks").ToString()));
            return false;
        }
    }
}
