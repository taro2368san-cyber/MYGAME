using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 特性（仕様書7.12）。特性の名前ごとに、バトルのどこで何が起きるかをここにまとめる
    // 新しい特性を足すとき：GameData の AbilityInfo.Built に名前を足して、下の当てはまる場所に case を1つ足す
    // 数字（○%・○倍・1/○）は特性の文から読む。文に数字がないときは、ここに書いた数字を使う
    public sealed partial class GameProcessor
    {
        // 場にいるポケモンの特性。相手が「かがくへんかガス」なら無効。へんしん中のメタモンは相手の特性を写す
        AbilityInfo AbilityOf(BattleSide s)
        {
            var mine = BattleSpecies(s).AbilityData;
            var other = BattleSpecies(Other(s)).AbilityData;
            return other?.Name == "かがくへんかガス" && mine?.Name != "かがくへんかガス" ? null : mine;
        }

        string AbilityName(BattleSide s) => AbilityOf(s)?.Name ?? "";
        BattleSide Other(BattleSide s) => B.Sides[B.Sides[0] == s ? 1 : 0];
        static double Or(double value, double fallback) => value > 0 ? value : fallback;

        void Announce(int side, string ability, List<GameEvent> events) =>
            events.Add(new AbilityTriggered { Side = side, Pokemon = B.Sides[side].Current.Species, Ability = ability });

        // 週イベントの天候 → バトルの天候（7.13）。三竜のふえは StartBattle で優先
        static string WeatherOfWeek(string week) => week == "雨季" ? "雨" : week == "乾季" ? "晴れ" : week == "すなあらしの日" ? "砂あらし" : "";

        // 場に出たとき：いかく、天候を変える特性。速い側から発動するので、天候は遅い側（あとから発動した側）が勝つ
        void OnEnter(List<GameEvent> events)
        {
            foreach (int i in new[] { 0, 1 }.Where(i => B.Sides[i].Entered).OrderByDescending(i => BattleSpeed(B.Sides[i])).ToList())
            {
                var s = B.Sides[i];
                s.Entered = false;
                string name = AbilityName(s);
                string weather = name == "ひでり" ? "晴れ" : name == "アメフラシ" ? "雨" : name == "すなおこし" ? "砂あらし" : null;
                if (weather != null && weather != B.Weather)
                {
                    B.Weather = weather; // このバトルの間だけ
                    events.Add(new BattleWeatherChanged { Weather = weather, Pokemon = s.Current.Species, Ability = name });
                }
                if (name == "いかく") { Announce(i, name, events); ChangeRank(1 - i, 0, -1, events); }
            }
        }

        // すばやさの倍率（天候で2倍になる特性）
        double AbilitySpeedMult(BattleSide s)
        {
            var a = AbilityOf(s);
            bool on = a != null && ((a.Name == "かるわざ" && s.LostItem) || (a.Name == "すいすい" && B.Weather == "雨") || (a.Name == "ようりょくそ" && B.Weather == "晴れ") || (a.Name == "すなかき" && B.Weather == "砂あらし"));
            return on ? Or(a.Mult, 2) : 1;
        }

        // 命中率から引く分（すながくれ）
        double AbilityEvasion(BattleSide d)
        {
            var a = AbilityOf(d);
            return a?.Name == "すながくれ" && B.Weather == "砂あらし" ? Or(a.Rate, 0.2) : 0;
        }

        // 雷の攻撃を受けない特性（あとでランクが上がる）
        bool Absorbs(BattleSide A, BattleSide D)
        {
            string name = AbilityName(D);
            return (name == "ひらいしん" || name == "でんきエンジン") && BattleSpecies(A).Type == "雷";
        }

        // ダメージの倍率（攻撃側・防御側の特性）。発動したら ev.Abilities に名前を残す
        double AbilityDamageMult(BattleSide A, BattleSide D, bool physical, bool guarded, RoundResolved ev)
        {
            double mult = 1;
            var a = AbilityOf(A);
            switch (a?.Name)
            {
                case "しんりょく": case "もうか": case "げきりゅう": case "むしのしらせ": // ピンチで強くなる
                    if (A.Current.Hp <= Pokemons.MaxHp(data, A.Current) * Or(a.Frac, 1.0 / 3)) { mult *= Or(a.Mult, 1.5); ev.Abilities.Add(a.Name); }
                    break;
                case "すなのちから":
                    if (B.Weather == "砂あらし") { mult *= Or(a.Mult, 1.3); ev.Abilities.Add(a.Name); }
                    break;
                case "アナライズ":
                    if (B.First != B.Attacker) { mult *= Or(a.Mult, 1.3); ev.Abilities.Add(a.Name); }
                    break;
                case "はりきり":
                    if (physical && !guarded && Chance(Or(a.Rate, 0.2))) { mult *= Or(a.Mult, 1.5); ev.Abilities.Add(a.Name); }
                    break;
            }
            var d = AbilityOf(D);
            if (d?.Name == "ふゆう" && BattleSpecies(A).Type == "闘") { mult *= 0.5; ev.Abilities.Add(d.Name); } // 闘の攻撃は半分
            return mult;
        }

        // 受けるダメージを特性でおさえる（ばけのかわ・がんじょう）
        int AbilityCapDamage(BattleSide D, int damage, RoundResolved ev)
        {
            var m = D.Current;
            var d = AbilityOf(D);
            int max = Pokemons.MaxHp(data, m);
            if (d?.Name == "ばけのかわ" && !D.DisguiseUsed)
            {
                D.DisguiseUsed = true; // 1バトルに1回
                ev.Abilities.Add(d.Name);
                return Math.Min(damage, Math.Max(1, (int)(max * Or(d.Frac, 1.0 / 8))));
            }
            if (d?.Name == "がんじょう" && m.Hp == max && damage >= m.Hp) { ev.Abilities.Add(d.Name); return m.Hp - 1; }
            return damage;
        }

        // 攻撃が当たったあと：雷を受けない特性のランク上げ、技の追加効果、接触に反応する特性（7.16）
        void AfterHit(Attack atk, RoundResolved ev, MoveEffect fx, List<GameEvent> events)
        {
            int ai = B.Attacker, di = 1 - ai;
            var A = B.Sides[ai];
            var D = B.Sides[di];
            if (ev.Missed || ev.Reflected) return;
            var d = AbilityOf(D);
            if (ev.Absorbed)
            {
                if (ev.Abilities.Contains("ふうせん")) Consume(di, events, false);
                else ChangeRank(di, d.Name == "ひらいしん" ? 1 : 2, 1, events);
                return;
            }
            if (fx != null) ApplyMoveEffect(fx, ai, events);
            // 接触：物理攻撃と、こうげきを使う必殺技（7.16）
            bool contact = atk == Attack.Physical || (atk == Attack.Super && BattleStat(A, x => x.Attack) >= BattleStat(A, x => x.SpAttack));
            bool failed = !ev.Guarded;
            if (contact && d != null)
            switch (d.Name)
            {
                case "せいでんき": if (failed && Chance(Or(d.Rate, 0.3))) Inflict(ai, Status.Paralysis, events, di, d.Name); break;
                case "ほのおのからだ": if (failed && Chance(Or(d.Rate, 0.2))) Inflict(ai, Status.Burn, events, di, d.Name); break;
                case "のろわれボディ": if (failed && Chance(Or(d.Rate, 0.2))) Inflict(ai, Status.Paralysis, events, di, d.Name); break;
                case "くだけるよろい": if (failed && !D.Current.Fainted) { Announce(di, d.Name, events); ChangeRank(di, 2, 1, events); } break;
            }
            HeldAfterHit(contact, ev, events);
        }

        // ラウンドの終わり（攻撃した側）：雨で回復する・状態異常が治る
        void AbilityRoundEnd(int side, List<GameEvent> events)
        {
            var s = B.Sides[side];
            var m = s.Current;
            var a = AbilityOf(s);
            if (m.Fainted || a == null || B.Weather != "雨") return;
            int max = Pokemons.MaxHp(data, m);
            if (a.Name == "アメのうけざら" && m.Hp < max)
            {
                int heal = Math.Max(1, (int)(max * Or(a.Frac, 1.0 / 16)));
                m.Hp = Math.Min(max, m.Hp + heal);
                events.Add(new AbilityHealed { Side = side, Pokemon = m.Species, Ability = a.Name, Amount = heal, Hp = m.Hp });
            }
            if (a.Name == "うるおいボディ" && m.Status != Status.None)
            {
                m.Status = Status.None; m.SleepRounds = 0;
                events.Add(new StatusCured { Side = side, Pokemon = m.Species, Ability = a.Name });
            }
        }

        // 相手を倒したとき（じしんかじょう）
        void OnKnockOut(int side, List<GameEvent> events)
        {
            var s = B.Sides[side];
            if (s.Current.Fainted || AbilityName(s) != "じしんかじょう") return;
            Announce(side, "じしんかじょう", events);
            ChangeRank(side, 0, 1, events);
        }

        // バトルが終わったとき（いやしのこころ：場にいるポケモンの持ち主の手持ち全員）
        void AbilityBattleEnd(List<GameEvent> events)
        {
            for (int i = 0; i < 2; i++)
            {
                var s = B.Sides[i];
                var a = s.Player >= 0 && s.Active >= 0 && !s.Current.Fainted ? AbilityOf(s) : null;
                if (a?.Name != "いやしのこころ" || s.Team.All(m => m.Status == Status.None) || !Chance(Or(a.Rate, 0.1))) continue;
                foreach (var m in s.Team) { m.Status = Status.None; m.SleepRounds = 0; }
                events.Add(new StatusCured { Side = i, Pokemon = s.Current.Species, Ability = a.Name, All = true });
            }
        }
    }
}
