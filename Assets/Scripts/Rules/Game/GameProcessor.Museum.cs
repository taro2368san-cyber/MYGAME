using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sugoroku.Rules
{
    // ニビ科学博物館（仕様書3.2.2）。イベントマス.csv の効果の型「博物館」のマス
    // 化石を預けると fossil_hatch_weeks 週あとに孵る。受け取るのは預けた本人が止まったとき。1人1個まで
    // 化石から孵るポケモンは、アイテム一覧の効果の文「…○○に孵る」から読む
    public sealed partial class GameProcessor
    {
        const string MuseumId = "博物館";

        // 化石 → 孵るポケモン（読めなければ null）
        public string FossilSpecies(string item)
        {
            var m = Regex.Match(data.FindItem(item)?.Effect ?? "", @"([^\s、。に]+)に孵る");
            return m.Success && data.FindSpecies(m.Groups[1].Value) != null ? m.Groups[1].Value : null;
        }

        int FossilWeeksLeft(PlayerState p) => Math.Max(0, TuneInt("fossil_hatch_weeks") - (State.Week - p.FossilWeek));

        bool LandMuseum(List<GameEvent> events)
        {
            var p = Current;
            if (p.Fossil != "")
            {
                string species = FossilSpecies(p.Fossil);
                int left = FossilWeeksLeft(p);
                if (left > 0) { Say("ev_museum_waiting", events, p, ("ポケモン", species), ("週", left.ToString())); return false; }
                // 孵った：Lv は手持ち・控えの最高Lv＋fossil_level_offset（最低5）
                int top = p.Party.Concat(p.Box).Select(m => m.Level).DefaultIfEmpty(5).Max();
                var mon = Pokemons.Create(data, species, Math.Max(5, Math.Min(top + TuneInt("fossil_level_offset"), TuneInt("level_max"))), "ニビ科学博物館で目覚めた", board[p.Square].Place, State.Week);
                p.Fossil = "";
                p.Dex.Add(species);
                Say("ev_museum_hatch", events, p, ("ポケモン", species));
                events.Add(new PokemonReceived { Player = p.Id, Pokemon = species, From = "ニビ科学博物館", ToBox = p.Party.Count >= TuneInt("party_size") });
                if (AddPokemon(p, mon, events)) return true;
            }
            else Say("ev_museum", events, p);
            // 預ける：袋の化石から選ぶ
            var fossils = p.PokemonBag.Concat(p.TrainerBag).Where(x => FossilSpecies(x) != null).Distinct().ToList();
            if (fossils.Count == 0) { if (p.Fossil == "") Say("ev_museum_none", events, p); return false; }
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = MuseumId, Prompt = "どの化石を あずける？（1人1個まで）" };
            foreach (var x in fossils) f.Options.Add($"あずける：{x}（{FossilSpecies(x)}）");
            f.Options.Add("やめておく");
            f.Goods.AddRange(fossils);
            return OpenFacility(f, events);
        }

        // 預ける化石を選んだ
        void MuseumChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            if (index >= f.Goods.Count) return; // やめておく
            string item = f.Goods[index];
            RemoveItem(p, item);
            p.Fossil = item;
            p.FossilWeek = State.Week;
            Say("ev_museum_deposit", events, p, ("アイテム", item), ("週", TuneInt("fossil_hatch_weeks").ToString()));
        }
    }
}
