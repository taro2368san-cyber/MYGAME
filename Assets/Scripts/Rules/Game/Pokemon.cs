using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    public enum Status { None, Burn, Poison, Paralysis, Sleep } // 仕様書7.17。戦闘不能は Hp == 0 で表す

    // 1体のポケモン（仕様書6章）
    public sealed class PokemonState
    {
        public string Species;
        public int Level, Exp, Hp;
        public Status Status;
        public int SleepRounds;                        // ねむりの残りラウンド（7.17）
        public string HeldItem = "";                   // もちもの（8.2）。ないときは ""
        public int LeadTurns;                          // 先頭にしたターン数（進化条件「先頭にして○ターン」用）
        public double HpMult = 1;                      // 持ち主の職業によるHPの倍率（エリートトレーナー 1.2倍、9.1）
        public string MetHow, MetPlace, MetSpecies;    // 出会いの記録（1.5）
        public int MetWeek;
        public bool Fainted => Hp <= 0;
    }

    // ポケモンの計算（実数値・経験値・進化・コスト）。状態は書き換えるが、乱数や手番には触らない
    public static class Pokemons
    {
        public static PokemonState Create(GameData d, string species, int level, string how, string place, int week)
        {
            var p = new PokemonState { Species = species, Level = level, Exp = ExpForLevel(level), MetHow = how, MetPlace = place, MetSpecies = species, MetWeek = week };
            p.Hp = MaxHp(d, p);
            return p;
        }

        public static Species Of(GameData d, PokemonState p) => d.FindSpecies(p.Species) ?? throw new KeyNotFoundException($"ポケモン「{p.Species}」がポケモンシートにありません");

        // 実数値（仕様書7.2）。個体値・努力値・性格なし、小数は切り捨て
        public static int MaxHp(GameData d, PokemonState p) => (int)((Of(d, p).Hp * 2 * p.Level / 100 + p.Level + 10) * p.HpMult);
        public static int Stat(int baseStat, int level) => baseStat * 2 * level / 100 + 5;
        public static int Attack(GameData d, PokemonState p) => Stat(Of(d, p).Attack, p.Level);
        public static int Defense(GameData d, PokemonState p) => Stat(Of(d, p).Defense, p.Level);
        public static int SpAttack(GameData d, PokemonState p) => Stat(Of(d, p).SpAttack, p.Level);
        public static int SpDefense(GameData d, PokemonState p) => Stat(Of(d, p).SpDefense, p.Level);
        public static int Speed(GameData d, PokemonState p) => Stat(Of(d, p).Speed, p.Level);

        // コスト（仕様書6.2）の10倍の整数＝種族値合計＋レベル
        public static int Cost10(GameData d, PokemonState p) => Of(d, p).Total + p.Level;

        public static int ExpForLevel(int level) => level * level * level; // 必要経験値＝レベル³（6.6 案）

        public static void HealFull(GameData d, PokemonState p) { p.Hp = MaxHp(d, p); p.Status = Status.None; }

        public static string StatusName(Status s) => s == Status.Burn ? "やけど" : s == Status.Poison ? "どく" : s == Status.Paralysis ? "まひ" : s == Status.Sleep ? "ねむり" : "";

        // 経験値を足してレベルを上げる。上がったレベルを返す
        public static int GainExp(GameData d, PokemonState p, int amount)
        {
            int max = (int)d.Required("level_max"), before = p.Level;
            p.Exp += amount;
            int level = p.Level;
            while (level < max && p.Exp >= ExpForLevel(level + 1)) level++;
            SetLevel(d, p, level);
            return p.Level - before;
        }

        // レベルを変える（最大HPが増えた分だけ今のHPも増やす。戦闘不能ならそのまま）
        public static void SetLevel(GameData d, PokemonState p, int level)
        {
            int oldMax = MaxHp(d, p);
            p.Level = Math.Min(level, (int)d.Required("level_max"));
            p.Exp = Math.Max(p.Exp, ExpForLevel(p.Level));
            if (!p.Fainted) p.Hp = Math.Max(1, p.Hp + MaxHp(d, p) - oldMax);
        }

        // バトル後の進化（6.5）。1段階だけ。進化先を返す（しなければ null）。アイテム・ライバルマスの進化はここでは見ない
        public static string TryEvolve(GameData d, PokemonState p)
        {
            if (p.HeldItem == "変わらずの石") return null; // 自分で進化しない（アイテム・研究室・ライバルマスの進化は、外してから）
            var e = Of(d, p).Evolutions.FirstOrDefault(x =>
                (x.Kind == EvolutionKind.Level && p.Level >= x.Value) || (x.Kind == EvolutionKind.LeadTurns && p.LeadTurns >= x.Value));
            if (e == null) return null;
            EvolveTo(d, p, e.Target);
            return e.Target;
        }

        // 種族を進化先に変える（最大HPが増えた分だけ今のHPも増やす）
        public static void EvolveTo(GameData d, PokemonState p, string target)
        {
            int oldMax = MaxHp(d, p);
            p.Species = target;
            if (!p.Fainted) p.Hp = Math.Max(1, p.Hp + MaxHp(d, p) - oldMax);
        }

        // 研究室の進化サービスで進化できる先（アイテム進化・交換進化。レベル進化は対象外、3.2.1）
        public static List<string> LabTargets(GameData d, PokemonState p) =>
            Of(d, p).Evolutions.Where(e => e.Kind == EvolutionKind.Item || e.Kind == EvolutionKind.Rival).Select(e => e.Target).Distinct().ToList();
    }
}
