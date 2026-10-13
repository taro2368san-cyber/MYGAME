using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // もちもの（仕様書8.2）。ポケモン1体に1つ、ポケモンセンターで着けはずしする。バトルでの効果を名前ごとにここにまとめる
    // 効果の数字は調整値（区分「もちもの」）。新しいもちものは、下の当てはまる場所に case を1つ足す
    public sealed partial class GameProcessor
    {
        string HeldOf(BattleSide s) => s.Current.HeldItem ?? "";

        // 1回で消費するもちものを使う
        void Consume(int side, List<GameEvent> events, bool announce = true)
        {
            var s = B.Sides[side];
            if (announce) events.Add(new HeldItemUsed { Side = side, Pokemon = s.Current.Species, Item = s.Current.HeldItem, Consumed = true });
            s.Current.HeldItem = "";
            s.LostItem = true; // かるわざ
        }

        void AnnounceItem(int side, List<GameEvent> events) =>
            events.Add(new HeldItemUsed { Side = side, Pokemon = B.Sides[side].Current.Species, Item = HeldOf(B.Sides[side]) });

        // こうげき・とくこうの倍率（攻撃側）
        double HeldAttackMult(BattleSide s, bool physical)
        {
            string item = HeldOf(s), species = s.Current.Species;
            if (item == (physical ? "こだわりハチマキ" : "こだわりメガネ")) return Tune("choice_item_mult");
            if (item == "でんきだま" && species == "ピカチュウ") return Tune("light_ball_mult");
            if (item == "ふといホネ" && physical && (species == "カラカラ" || species == "ガラガラ")) return Tune("thick_club_mult");
            return 1;
        }

        // ぼうぎょ・とくぼうの倍率（防御側）
        double HeldDefenseMult(BattleSide s)
        {
            string item = HeldOf(s);
            if (item == "とつげきチョッキ") return Tune("assault_vest_mult");
            if (item == "しんかのきせき" && Pokemons.Of(data, s.Current).Evolutions.Count > 0) return Tune("eviolite_mult");
            return 1;
        }

        // こだわり系：選べない攻撃
        string HeldForbids(BattleSide s, Attack atk) =>
            atk == Attack.Special && HeldOf(s) == "こだわりハチマキ" ? "こだわりハチマキを持っているので とくこう技は 選べません"
            : atk == Attack.Physical && HeldOf(s) == "こだわりメガネ" ? "こだわりメガネを持っているので こうげき技は 選べません" : null;

        // 先攻・後攻のカードの前に（せんせいのツメ）。先攻が決まったら、その側を返す
        int HeldFirst(List<GameEvent> events)
        {
            for (int i = 0; i < 2; i++)
                if (HeldOf(B.Sides[i]) == "せんせいのツメ" && Chance(Tune("quick_claw_rate"))) { AnnounceItem(i, events); return i; }
            return -1;
        }

        // 受けるダメージをおさえる（きあいのタスキ。なくならない）
        int HeldCapDamage(BattleSide D, int damage, RoundResolved ev)
        {
            var m = D.Current;
            if (HeldOf(D) == "きあいのタスキ" && m.Hp == Pokemons.MaxHp(data, m) && damage >= m.Hp) { ev.Abilities.Add("きあいのタスキ"); return m.Hp - 1; }
            return damage;
        }

        // 攻撃が当たったあと（AfterHit から）：いのちのたまの反動、ゴツゴツメット、弱点保険、わるいてぐせ
        void HeldAfterHit(bool contact, RoundResolved ev, List<GameEvent> events)
        {
            int ai = B.Attacker, di = 1 - ai;
            var A = B.Sides[ai];
            var D = B.Sides[di];
            if (HeldOf(A) == "いのちのたま" && ev.Damage > 0) ItemDamage(ai, ai, Tune("life_orb_recoil"), events);
            if (contact && !ev.Guarded && HeldOf(D) == "ゴツゴツメット") ItemDamage(di, ai, Tune("rocky_helmet_damage"), events);
            if (ev.Weakness && HeldOf(D) == "弱点保険" && !D.Current.Fainted)
            {
                Consume(di, events);
                ChangeRank(di, 0, TuneInt("weakness_policy_stages"), events);
                ChangeRank(di, 1, TuneInt("weakness_policy_stages"), events);
            }
            // 脱出ボタン・レッドカード（フルバトルだけ）：ダメージを受けたら、ラウンドの終わりに交代。なくならないが、1体につき1バトル1回
            if (B.Full && ev.Damage > 0 && !D.Current.Fainted && !B.UsedOnce.Contains(D.Current))
            {
                if (HeldOf(D) == "脱出ボタン" && D.Team.Count(m => !m.Fainted) > 1) { AnnounceItem(di, events); B.UsedOnce.Add(D.Current); B.ForcedSwitch = di; B.ForcedRandom = false; }
                else if (HeldOf(D) == "レッドカード" && !A.Current.Fainted && A.Team.Count(m => !m.Fainted) > 1) { AnnounceItem(di, events); B.UsedOnce.Add(D.Current); B.ForcedSwitch = ai; B.ForcedRandom = true; }
            }
            // わるいてぐせ（特性）：接触でガード失敗、自分が何も持っていなければ相手のもちものを奪う。バトルが終わったら返す
            if (contact && !ev.Guarded && AbilityName(D) == "わるいてぐせ" && HeldOf(D) == "" && HeldOf(A) != "" && !D.Current.Fainted)
            {
                string item = HeldOf(A);
                Announce(di, "わるいてぐせ", events);
                D.Current.HeldItem = item;
                A.Current.HeldItem = "";
                A.LostItem = true;
                B.Stolen.Add((D.Current, A.Current, item));
                events.Add(new HeldItemStolen { Side = di, Pokemon = D.Current.Species, From = A.Current.Species, Item = item });
            }
        }

        // もちもので受けるダメージ（itemSide のもちもの、target が受ける）
        void ItemDamage(int itemSide, int target, double ratio, List<GameEvent> events)
        {
            var m = B.Sides[target].Current;
            if (m.Fainted) return;
            int dmg = Math.Max(1, (int)(Pokemons.MaxHp(data, m) * ratio));
            m.Hp = Math.Max(0, m.Hp - dmg);
            events.Add(new HeldItemDamage { Side = target, Pokemon = m.Species, Item = HeldOf(B.Sides[itemSide]), Owner = B.Sides[itemSide].Current.Species, Damage = dmg, Hp = m.Hp });
        }

        // ラウンドの終わり：たべのこし（攻撃した側）、オボンのみ（両方）
        void HeldRoundEnd(int attacker, List<GameEvent> events)
        {
            for (int i = 0; i < 2; i++)
            {
                var s = B.Sides[i];
                var m = s.Current;
                if (m.Fainted) continue;
                int max = Pokemons.MaxHp(data, m);
                if (i == attacker && HeldOf(s) == "たべのこし" && m.Hp < max) Heal(i, Tune("leftovers_heal"), events);
                // オボンのみ：なくならないが、1バトルに1回だけ
                if (HeldOf(s) == "オボンのみ" && !s.BerryUsed && m.Hp < max * Tune("sitrus_threshold")) { s.BerryUsed = true; Heal(i, Tune("sitrus_heal"), events); }
            }
        }

        void Heal(int side, double ratio, List<GameEvent> events)
        {
            var m = B.Sides[side].Current;
            int max = Pokemons.MaxHp(data, m), amount = Math.Max(1, (int)(max * ratio));
            m.Hp = Math.Min(max, m.Hp + amount);
            events.Add(new AbilityHealed { Side = side, Pokemon = m.Species, Ability = HeldOf(B.Sides[side]), Amount = amount, Hp = m.Hp });
        }

        // バトルが終わったら、わるいてぐせで奪ったもちものを返す
        void ReturnStolen()
        {
            foreach (var (thief, victim, item) in B.Stolen)
            {
                if (thief.HeldItem != item) continue; // バトル中に食べられた・使われたものは戻らない
                thief.HeldItem = "";
                if (victim.HeldItem == "") victim.HeldItem = item;
            }
        }
    }
}
