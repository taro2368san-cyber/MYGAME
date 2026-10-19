using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    public enum BattleKind { Wild, Trainer, Gym, Rival, Player, Event, Rocket, Job, Tournament } // Tournament: 突発バトルイベントのミニトーナメント（プレイヤーどうし、5.3） // Job: 職業マスの職業トレーナー（9.2） // Event: ランダムイベント（チャンピオンなど）
    public enum Attack { Physical, Special, Super, Item }           // ○物理 △特殊 □必殺 L1/R1どうぐ（仕様書7.3）
    public enum Defense { Guard, SpecialGuard, Counter, Flee }      // ○まもる △特殊守り □カウンター L1/R1にげる
    public enum BattleStep { Select, Commands, Ball, Reward }
    public enum Reward { Item, Fan, Forgive, Trade }                 // プレイヤー戦の勝利報酬（7.7）

    public sealed class BattleSide
    {
        public int Player = -1;                    // NPC なら -1
        public string Name;
        public List<PokemonState> Team;            // プレイヤーなら手持ちそのもの（ダメージが残る）
        public int Active = -1;
        public bool LastWasSuper;                  // 前の攻撃が必殺技（次の攻撃では打てない、7.4）
        public int SwitchFrom = -1;                // 交代させられた子（選び直しのとき、この子は選べない）
        public bool BerryUsed;                     // このバトルでオボンのみを使った（1バトルに1回）
        public bool LostItem;                      // このバトルでもちものを使い切った・奪われた（かるわざ）
        public bool Entered, DisguiseUsed;         // 場に出たばかり（出たときの特性がまだ）、ばけのかわを使った
        public int[] Ranks = new int[3];           // 能力ランク（こうげき・とくこう・すばやさ、−2〜＋2、7.15）。交代で0に戻る
        public readonly HashSet<int> Participated = new HashSet<int>();
        public bool NeedsSelect;
        public PokemonState Current => Team[Active];
        public bool HasAlive => Team.Any(p => !p.Fainted);
    }

    public sealed class BattleState
    {
        public BattleKind Kind;
        public bool Full;                          // フルバトル（手持ちを全部倒すまで）
        public int Square;
        public string Gym;                         // ジム戦なら「ニビジム」など
        public string TrainerId, DialogKey, EventId, Job; // トレーナー戦のトレーナー id とセリフのキー、イベント戦のイベント id
        public readonly BattleSide[] Sides = new BattleSide[2];   // 0 が仕掛けた側（手番のプレイヤー）
        public int Attacker, Rounds;
        public int TurnRounds;
        public bool Legend;     // チケットの島の伝説（勝つか捕まえると いなくなる）  // この番に戦ったラウンド数（2で1往復。1往復したら番を終える）
        public int First;                          // 先攻・後攻のカードで先攻になった側
        public int ForcedSwitch = -1;              // ラウンドの終わりに交代する側（脱出ボタン・レッドカード）
        public bool ForcedRandom;                  // レッドカード：交代先はランダム
        public readonly HashSet<PokemonState> UsedOnce = new HashSet<PokemonState>(); // このバトルで脱出ボタン・レッドカードを使った子
        public string Weather = "";                // バトルの天候（晴れ・雨・砂あらし）。特性で変わってもこのバトルの間だけ（7.13）
        public BattleStep Step;
        public Attack? AttackCmd; public string AttackItem;
        public Defense? DefenseCmd;
        public int Winner = -1;                    // 報酬を選ぶ勝者（プレイヤー戦）
        public bool Surrendered;                   // 降参で終わった（ポケモン交換は選べない、7.3）
        public bool CatchUp;                       // レベル追いつきが起きた（そのバトルの経験値はなし、7.8）
        public readonly List<(PokemonState thief, PokemonState victim, string item)> Stolen = new List<(PokemonState, PokemonState, string)>(); // わるいてぐせ
        public readonly List<int> DefeatedLevels = new List<int>(); // 仕掛けた側が倒した相手のレベル（経験値用）

        public bool IsNpc => Kind != BattleKind.Player && Kind != BattleKind.Tournament;
        public BattleSide Side(int i) => Sides[i];
    }

    public sealed class BattleStarted : GameEvent { public BattleKind Kind; public bool Full; public string Opponent; public int[] Players; }
    public sealed class SelectRequested : GameEvent { public int Side, Player; }
    public sealed class BattleSuspended : GameEvent { public int Player; public string Opponent; }   // 1往復したので、続きは次の自分の番
    public sealed class BattleResumed : GameEvent { public int Player; public BattleKind Kind; public bool Full; public string Opponent; public int[] Players; }
    public sealed class SentOut : GameEvent { public int Side; public string Pokemon; public int Level, Hp, MaxHp; public string Type, Weakness; public Status Status; public int Exp; } // Exp：経験値の合計（経験値バー）
    public sealed class InitiativeDrawn : GameEvent { public List<int> Cards; public int Picked, First; public string FirstPokemon; } // Cards: 各カードで先攻になる側
    public sealed class CommandsRequested : GameEvent { public int Attacker; public int AttackPlayer, DefensePlayer; public bool SuperLocked; }
    public sealed class RoundResolved : GameEvent
    {
        public int Attacker; public Attack Attack; public Defense Defense;
        public bool Missed, Guarded, Critical, Weakness, Bond, Reflected, Absorbed; // Absorbed: 雷の攻撃を特性で受けなかった
        public List<string> Abilities = new List<string>();  // この攻撃で発動した特性
        public int Damage, AttackerHp, DefenderHp, Dice; // Dice: 攻撃ダイス（7.14）
        public string AttackerPokemon, DefenderPokemon, DefenderTrainer;
        public string Item;
    }
    public sealed class SandstormDamage : GameEvent { public int Side, Damage, Hp; public string Pokemon; }
    public sealed class StatusDamage : GameEvent { public int Side, Damage, Hp; public string Pokemon; public Status Status; } // やけど・どく（7.17）
    public sealed class StatusInflicted : GameEvent { public int Side; public string Pokemon; public Status Status; }
    public sealed class CantMove : GameEvent { public int Side; public string Pokemon; public Status Status; public bool Woke; } // ねむり・まひで動けない
    public sealed class SwitchedOut : GameEvent { public int Side; public string Pokemon; }
    public sealed class HeldItemUsed : GameEvent { public int Side; public string Pokemon, Item; public bool Consumed; }
    public sealed class HeldItemStolen : GameEvent { public int Side; public string Pokemon, From, Item; }
    public sealed class HeldItemDamage : GameEvent { public int Side, Damage, Hp; public string Pokemon, Item, Owner; } // Owner のもちもので Pokemon が受けた
    public sealed class AbilityTriggered : GameEvent { public int Side; public string Pokemon, Ability; }
    public sealed class BattleWeatherChanged : GameEvent { public string Weather, Pokemon, Ability; } // Ability が null なら週イベントの天候
    public sealed class AbilityHealed : GameEvent { public int Side, Amount, Hp; public string Pokemon, Ability; }
    public sealed class StatusCured : GameEvent { public int Side; public string Pokemon, Ability; public bool All; }
    public sealed class Transformed : GameEvent { public int Side; public string Pokemon, Into; } // へんしん（メタモン）
    public sealed class RankChanged : GameEvent { public int Side, Delta, Now; public string Pokemon, Stat; }    // 能力ランク（7.15）
    public sealed class PokemonFainted : GameEvent { public int Side; public string Pokemon; }
    public sealed class BallPrompt : GameEvent { public int Player; }
    public sealed class BallThrown : GameEvent { public int Player; public string Ball; public bool Caught; public string Pokemon; }
    public sealed class Fled : GameEvent { public int Side; public bool Surrender; }
    public sealed class BattleEnded : GameEvent { public int Winner; public bool Draw, Judged; public string WinnerName; } // Winner は勝った側（0/1）、引き分けなら -1
    public sealed class ExpGained : GameEvent { public int Player; public string Pokemon; public int Exp, Levels, Level, Total; } // Total：もらったあとの経験値の合計
    public sealed class LevelCaughtUp : GameEvent { public int Player; public string Pokemon; public int Level; }
    public sealed class Evolved : GameEvent { public int Player; public string From, To; }
    public sealed class BadgeEarned : GameEvent { public int Player; public string Gym; public int Badges; }
    public sealed class RestNextTurn : GameEvent { public int Player; }
    public sealed class RewardRequested : GameEvent { public int Winner, Loser; }
    public sealed class Caught : GameEvent { public int Player; public string Pokemon; public bool ToBox; }
}
