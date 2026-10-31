using System.Collections.Generic;

namespace Sugoroku.Rules
{
    // ポケモンリーグ（仕様書3.3.2）：セキエイ高原のイベントマス（効果の型「ポケモンリーグ」）。バッジ8個で挑める
    // ポケモンリーグ.csv の順（四天王 → チャンピオン）に フルバトル。間で回復しない。1人に勝つと 次の手番で 次の相手（サイコロは振らず そのマスから動かない）
    // 負けたり 逃げたり 引き分けたら 挑戦は終わり、次は1人目から。全員に勝つと チャンピオン入り（ハナダの洞窟に入れる）
    // セリフは 施設文言.csv の league（あいさつ）・league_notyet・league_champion・相手の id（_win／_lose）・league_hof
    public sealed partial class GameProcessor
    {
        const string LeagueId = "ポケモンリーグ", ChampionOnlyPlace = "ハナダの洞窟";

        bool LandLeague(Effect e, List<GameEvent> events)
        {
            var p = Current;
            if (p.Champion) { Say(e.TextKey + "_champion", events, p); return false; }
            if (p.Badges < 8 || data.League.Count == 0) return Say(e.TextKey + "_notyet", events, p) ? false : Nothing("バッジが 8個 そろわないと 挑めない", events);
            Say(e.TextKey, events, p);
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = LeagueId, Prompt = "ポケモンリーグに 挑戦する？（負けるか チャンピオンに なるまで 続く。間で 回復は しない）" };
            f.Options.AddRange(new[] { "挑戦する", "やめておく" });
            return OpenFacility(f, events);
        }

        bool LeagueChosen(FacilityState f, int index, List<GameEvent> events)
        {
            if (index != 0) return false;
            var p = State.Players[f.Player];
            p.LeagueBeaten = 0;
            return StartLeagueBattle(p, events);
        }

        bool StartLeagueBattle(PlayerState p, List<GameEvent> events)
        {
            var foe = data.League[p.LeagueBeaten];
            Say(foe.Id, events, p);
            StartBattle(BattleKind.League, true, Npc(foe.Name, foe.Members), events);
            B.DialogKey = foe.Id;
            return true;
        }

        // 勝った（WinnerRewards から）。最後の1人なら チャンピオン入り
        void LeagueWon(PlayerState p, List<GameEvent> events)
        {
            Say(B.DialogKey + "_win", events, p);
            if (++p.LeagueBeaten < data.League.Count) return; // 次の手番で 次の相手
            p.LeagueBeaten = -1;
            p.Champion = true;
            Say("league_hof", events, p);
            events.Add(new ChampionCrowned { Player = p.Id });
        }

        // 負けた・逃げた・引き分け：挑戦は終わり
        static void LeagueOver(PlayerState p) => p.LeagueBeaten = -1;
    }
}
