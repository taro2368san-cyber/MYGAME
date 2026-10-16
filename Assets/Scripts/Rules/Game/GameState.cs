using System.Collections.Generic;

namespace Sugoroku.Rules
{
    // 処理役が今どの命令を待っているか（仕様書15.7）
    public enum Phase { Prepare, Moving, Facility, Battle, GameOver }
    public enum MoveMode { Highlight, Step } // ハイライト一括選択 ／ 1マスずつ選択（仕様書4.1）

    public sealed class PlayerState
    {
        public int Id;
        public string Name;
        public string Character = "";   // キャラクター.csv の id（見た目だけ、能力差なし。1.1）
        public bool IsCpu;                // CPU が操作する（1.4）
        public MoveMode MoveMode;
        public int Square, Money, Points;
        public readonly List<PlayerAilment> Ailments = new List<PlayerAilment>(); // プレイヤーの状態（1回休みなど）。残り週数つきのリスト（13.5、10章）
        public string Job = "";                                              // 今の職業（就けるのは1つだけ、9.2）
        public readonly HashSet<string> CaughtSpecies = new HashSet<string>(); // 捕まえた種類（イベントルール用）
        public readonly List<string> Gyms = new List<string>();            // 勝ったジム（バッジ）
        public readonly List<PokemonState> Party = new List<PokemonState>(); // 手持ち。0番が先頭
        public readonly List<PokemonState> Box = new List<PokemonState>();   // 控え
        public readonly List<string> PokemonBag = new List<string>();        // ポケモンの道具
        public readonly List<string> TrainerBag = new List<string>();        // トレーナーの道具
        public readonly Dictionary<string, int> Balls = new Dictionary<string, int>();
        public readonly HashSet<string> Dex = new HashSet<string>();         // 図鑑（倒した・捕まえた種族）
        public readonly HashSet<string> BeatenTrainers = new HashSet<string>(); // 倒したトレーナーの id（ジムトレーナーの判定に使う）
        public readonly List<string> UnlockedJobs = new List<string>();      // 解放した職業（イベントで）
        public int LastPlaceWeeks;                                           // 何週連続で最下位か（8.8.1 の救済）
        public readonly HashSet<int> VisitedCenters = new HashSet<int>();   // 行ったことのあるポケモンセンター（アーマーガーチケット）
        public readonly HashSet<string> RocketUsed = new HashSet<string>();  // ロケット団拠点で「1人1回」の品を使った（行の id）
        public string Fossil = ""; public int FossilWeek;                    // 博物館に預けている化石と、預けた週（3.2.2）
        public BattleState PendingBattle;
        public int IslandTurns; public string IslandLegend = ""; public int IslandLegendLevel; // 島にいる手番の数と、この島に出る伝説（3.4）
        public int TicketChangeWeeks, TicketLastWeeks;                     // 謎のチケットが変わるまでの週数と、数えた最下位の週（12章）                                  // 1往復して止めているバトル（次の自分の番に続き）
        public int ErrandStage;                                            // お使い：0＝まだ、1＝研究室に届ける途中、2＝すんだ
        public int BoxMax;                                                 // マサキの家で増えた控えの上限（0 なら調整値 box_max のまま）
        public PokemonState DayCare; public int DayCareWeek;              // 育て屋に あずけている子と、あずけた週
        public readonly List<JournalEntry> Journal = new List<JournalEntry>(); // 旅の年表（エンドロール、1.5）
        public int StepsWalked, Battles;
        public int RocketDefeats;                                            // ロケット団を追いはらった回数（最多討伐賞、2.1）                                     // 旅の数字：歩いたマス、バトルの数
        public int BikeUses;                                                 // 自転車を使った回数（サイクリングの解放）
        public int LastMainWarp = -1;                                        // 最後に止まったメインマップのワープマス（あなぬけひもで戻る先）

        // この手番の準備フェーズで決めたこと
        public bool UsedItem;
        public int DiceCount = 1, FixedDice = -1;

        public bool Has(string ailment) => Ailments.Exists(a => a.Name == ailment);
        public int Badges => Gyms.Count;
        public int BallCount { get { int n = 0; foreach (var b in Balls.Values) n += b; return n; } }
    }

    // プレイヤー単位の状態（1回休み、さいみん、のろい、へんそう など。10章）。自分のターンが来るたびに Weeks が1減る
    public sealed class PlayerAilment { public string Name; public int Weeks; }
    public sealed class TournamentState { public readonly List<int> Entrants = new List<int>(), Advanced = new List<int>(); public int Next; } // この回戦の出場者、勝ち上がった人、次の試合
    public sealed class JournalEntry { public int Week; public string Place, Kind, Text; } // Kind: catch/badge/evolve/job/rocket/gym/rival/bond/catchup/draw

    // 移動中だけ使う状態
    public sealed class MoveState
    {
        public int Dice;
        public int Extra;                                  // サイコロの目より多く進んでよい歩数（サイクリング）
        public bool AllowReverse;                          // 止まれるマスがなく、折り返しも歩数に数える特別な移動
        public HashSet<int> Reachable = new HashSet<int>();
        public List<int> Path = new List<int>();           // 出発マスから今いるマスまで
    }

    // Event: イベントの選択肢 ／ BagFull: 袋がいっぱいで捨てる物を選ぶ ／ BoxFull: 控えがいっぱいで逃がす子を選ぶ ／ JobOffer: 解放した職業に今すぐ就くか
    public enum FacilityKind { Center, Shop, FanHeal, Event, BagFull, BoxFull, JobOffer, Rival, RocketBase, Trade, Exchange } // Rival: ライバルマスの進化・交換（11.5）、RocketBase: ロケット団の拠点（11.3）、Trade: 渡すポケモンを選ぶ（拠点・ライバル）
    public sealed class FacilityState
    {
        public FacilityKind Kind;
        public int Player;                              // 操作するプレイヤー（ふつうは手番の人。袋がいっぱいのときは物をもらった人）
        public int Square;
        public List<string> Goods = new List<string>(); // ショップの品
        public int Owner = -1, Price;                   // 他人のファンマスの回復代と払う相手
        public List<string> Options = new List<string>(); // Event / BagFull の選択肢
        public string Prompt = "", EventId, PendingItem, PendingJob; // 選択肢の見出し、進行中のイベント、袋に入れたい物、就くか聞いている職業
        public PokemonState PendingPokemon;            // 控えに入れたいポケモン（BoxFull）
        public bool IsLab;                             // 研究室（センターの機能＋進化サービス、3.2.1）
    }

    // ゲームのすべての情報（仕様書15.2）。書き換えてよいのは GameProcessor だけ
    public sealed class GameState
    {
        public readonly List<PlayerState> Players = new List<PlayerState>();
        public readonly List<int> TurnOrder = new List<int>();   // Players の番号を手番の順に
        public int TurnIndex;
        public int Week = 1, MaxWeeks;
        public Phase Phase = Phase.Prepare;
        public ulong Rng;                                        // 乱数の状態（保存すれば同じ流れを再現できる）
        public MoveState Move;
        public FacilityState Facility;
        public BattleState Battle;
        public readonly Dictionary<int, int> Owners = new Dictionary<int, int>(); // マス → 持ち主のプレイヤー
        public string EventRule = "";                          // 今回のイベントルールの id（2.2。空ならなし）
        public string Weather = "", WeekEvent = "";              // 週イベントの天候（雨季など）と、それ以外の週イベント（セールなど）
        public int WeatherWeeks, WeekEventWeeks, NextEventWeek;
        public string FluteWeather = ""; public int FluteWeeks;
        public string NewsKind = "", NewsPlace = ""; public int NewsWeeks;     // ポケニュース・特番：種類、指定の道路、残り週数（5.3）
        public TournamentState Tournament;                                     // 突発バトルイベントのミニトーナメント（進行中なら）   // 三竜のふえの天候（週イベントより優先、7.13）
        public readonly Dictionary<int, int> Traps = new Dictionary<int, int>(); // ワナ：マス → 仕掛けた人
        public readonly Dictionary<int, int> Blocked = new Dictionary<int, int>();
        public readonly Dictionary<int, string> JobSquares = new Dictionary<int, string>(); // 職業マス：トレーナーマス → 職業（ゲーム開始時に決まる、9.2）
        public readonly HashSet<int> RocketCleared = new HashSet<int>();      // 一度でも倒されたロケット団マス（トレーナーマスの報酬は初回だけ、11.3）
        public List<string> RocketShelf = new List<string>();                 // どろぼうの家に今並んでいる品（行の id） // カビゴン（アマイ三ツ）：マス → 残り週数。だれも通れない
        public readonly List<Command> History = new List<Command>(); // 受け付けた命令の記録（仕様書15.9）
        public GameSettings Settings;                            // 開始時の設定（セーブに使う）

        public PlayerState CurrentPlayer => Players[TurnOrder[TurnIndex]];
    }

    // 命令（仕様書15.4）。Player はその命令を出したプレイヤーの番号
    public abstract class Command { public int Player; }
    // 準備フェーズ
    public sealed class UseItem : Command { public string Item; public int Target; }   // Target: 手持ちの番号・プレイヤー・マス・数（ItemEffects.TargetOf）
    public sealed class SwapParty : Command { public int A, B; }
    public sealed class Fish : Command { }                                            // つり（海上マスのとなりで、移動の代わりに、4.1.2）
    public sealed class RollDice : Command { }
    // 移動
    public sealed class ChooseDestination : Command { public int Square; }  // ハイライト方式：行き先を選ぶ（確定前なら選び直せる）
    public sealed class StepTo : Command { public int Square; }             // 1マスずつ方式：となりのマスへ1歩（直前のマスへなら考え直し）
    public sealed class StepBack : Command { }                              // 1マスずつ方式：1歩取り消す
    public sealed class ConfirmMove : Command { }
    // 施設
    public sealed class Buy : Command { public string Item; }
    public sealed class Sell : Command { public string Item; }
    public sealed class HoldItem : Command { public int Slot; public string Item; }    // センターでもちものを着ける（Item が空なら外す、8.2）
    public sealed class CenterSwap : Command { public int PartySlot = -1, BoxIndex = -1; } // 片方だけなら預ける／引き出す
    public sealed class AcceptHeal : Command { }                                         // 他人のファンマスで回復代を払う
    public sealed class LeaveFacility : Command { }
    public sealed class ExchangePoints : Command { public int Points; }              // 換金所で、1,000円単位で何ポイント換えるか（2.1）
    public sealed class ChooseOption : Command { public int Index; }                     // イベントの選択肢・捨てる物を選ぶ
    public sealed class ChangeJob : Command { public string Job; }                       // センターで職業を変える（9.2）
    public sealed class LabEvolve : Command { public int Slot; public string Target; }   // 研究室の進化サービス（3.2.1）
    public sealed class RivalTrade : Command { public string Species; }               // ライバルマスで他地方のポケモンと交換する（11.5）。渡す子はそのあと選ぶ
    public sealed class RivalEvolve : Command { public int Slot; public string Target; } // ライバルマスの交換進化（11.5）
    // デバッグ（17.1、F1）。開発用。GameProcessor.DebugEnabled のときだけ受け付ける
    public enum DebugAction { Warp, SetDice, Money, Balls, Item, Badge, HealAll, LevelUp, AddPokemon, WinBattle, LoseBattle, Weather, WeekEvent, NextWeek, Status, Battle }
    public sealed class DebugCommand : Command { public DebugAction Action; public int Value; public string Text; }
    // バトル
    public sealed class SelectPokemon : Command { public int Slot; }
    public sealed class ChooseAttack : Command { public Attack Attack; public string Item; }
    public sealed class ChooseDefense : Command { public Defense Defense; }
    public sealed class ThrowBall : Command { public string Ball; }      // Ball が null なら投げない
    public sealed class ChooseReward : Command { public Reward Reward; public string Item; public int Take = -1, Give = -1; public bool GiveFromBox; } // Trade：相手の手持ちの Take 番を、自分の Give 番（GiveFromBox なら控え）と交換

    // 結果（仕様書15.5）。表示はこれを順に受け取って演出にする
    public abstract class GameEvent { }
    public sealed class TurnStarted : GameEvent { public int Player; }
    public sealed class TurnSkipped : GameEvent { public int Player; public string Reason; } // Reason: 1回休み・さいみん
    public sealed class AilmentAdded : GameEvent { public int Player, Weeks, By; public string Name; }
    public sealed class AilmentCured : GameEvent { public int Player; public string Name; }
    public sealed class ItemBlocked : GameEvent { public int Player; public string Item, Attack; } // みがわりにんぎょう・かごのみで防いだ
    public sealed class Fished : GameEvent { public int Player; }
    public sealed class SnorlaxPlaced : GameEvent { public int Player, Square, Weeks; }
    public sealed class SnorlaxRemoved : GameEvent { public int Square, Player; } // Player は ふえを吹いた人（週がたって去ったときは -1）
    public sealed class TrapSet : GameEvent { public int Player, Square; }
    public sealed class TrapSprung : GameEvent { public int Player, Owner, Square; }
    public sealed class FluteStarted : GameEvent { public int Player, Weeks; public string Weather; }
    public sealed class ItemUsed : GameEvent { public int Player; public string Item, Pokemon; }
    public sealed class PartySwapped : GameEvent { public int Player; }
    public sealed class DiceRolled : GameEvent { public int Player, Value; public List<int> Dice; public bool AllowReverse; public List<int> Reachable; }
    public sealed class PathChanged : GameEvent { public int Player; public List<int> Path; }
    public sealed class Moved : GameEvent { public int Player; public List<int> Path; }
    public sealed class Warped : GameEvent { public int Player, From, To; }
    public sealed class Landed : GameEvent { public int Player, Square; }
    public sealed class NothingHappened : GameEvent { public int Player; public string Reason; }
    public sealed class RandomEventHappened : GameEvent { public int Player; public string Name, Text; }
    public sealed class EventStarted : GameEvent { public int Player; public string Id, Name; }
    public sealed class Dialog : GameEvent { public int Player; public string Key; public Dictionary<string, string> Values; } // 文言CSVのセリフ（Narrator が行にする）
    public sealed class OptionsRequested : GameEvent { public int Player; public string Prompt; public List<string> Options; }
    public sealed class JobUnlocked : GameEvent { public int Player; public string Job; }
    public sealed class PokemonReceived : GameEvent { public int Player; public string Pokemon, From; public bool ToBox, Refused; }
    public sealed class BallTierUsed : GameEvent { public int Player; public string Tier; public bool Rescue, Keyed, Prize; } // Prize: 鍵付きの当たり枠
    public sealed class NewsLegendAppeared : GameEvent { public string Pokemon; }
    public sealed class TournamentStarted : GameEvent { public List<int> Players; }
    public sealed class TournamentWon : GameEvent { public int Player; public string Item; }
    public sealed class JobChallenge : GameEvent { public int Player; public string Job; }
    public sealed class RocketDefeated : GameEvent { public int Player, Square; public bool First; public List<int> Trainers; } // Trainers: 報酬でもらったトレーナーマス
    public sealed class PokemonGiven : GameEvent { public int Player; public string Pokemon, To; }
    public sealed class BoxExpanded : GameEvent { public int Player, Max; }
    public sealed class SailedToIsland : GameEvent { public int Player, From, To; public string Island, Legend; }     // チケットで島へ（Legend は伝説の島の伝説）
    public sealed class ReturnedFromIsland : GameEvent { public int Player, From, To; public string Reason; }
    public sealed class TicketChanged : GameEvent { public int Player; }                                           // 謎のチケットが不思議なチケットに
    public sealed class RescueGiven : GameEvent { public int Player; public string Item; }                       // 下位救済（12章） // マサキの家で控えの上限が増えた
    public sealed class ItemGiven : GameEvent { public int Player; public string Item, To; } // 条件「道具を渡す」で渡した
    public sealed class ItemDiscarded : GameEvent { public int Player; public string Item; public bool NotTaken; } // NotTaken: 拾わなかった
    public sealed class ExtraMoveStarted : GameEvent { public int Player, Steps; public List<int> Reachable; }
    public sealed class MoneyChanged : GameEvent { public int Player, Amount, To = -1; public string Reason; } // To: 払った相手
    public sealed class PointsChanged : GameEvent { public int Player, Amount; }
    public sealed class ItemGained : GameEvent { public int Player; public string Item; public bool BagFull; }
    public sealed class ItemLost : GameEvent { public int Player, To = -1; public string Item; }
    public sealed class PartyHealed : GameEvent { public int Player; }
    public sealed class FacilityOpened : GameEvent { public int Player; public FacilityKind Kind; public List<string> Goods; public int Price, Owner; }
    public sealed class FacilityClosed : GameEvent { public int Player; }
    public sealed class HeldItemChanged : GameEvent { public int Player; public string Pokemon, Item, Old; }
    public sealed class Deposited : GameEvent { public int Player; public string In, Out; } // In: 手持ちに入った、Out: 控えに入った
    public sealed class OwnerChanged : GameEvent { public int Square, Owner, Previous; }
    public sealed class Dividend : GameEvent { public int Player, Amount; }
    public sealed class WeekAdvanced : GameEvent { public int Week; }
    public sealed class WeekEventHappened : GameEvent { public string Name; public int Weeks; }
    public sealed class WeekEventEnded : GameEvent { public string Name; }
    public sealed class SentToCenter : GameEvent { public int Player, Square; }
    public sealed class GameEnded : GameEvent { public List<(int player, double points)> Ranking; }
    public sealed class TurnOrderDecided : GameEvent { public List<int> Order; }                 // 手番の順番の発表（16.1 #10）
    public sealed class EventRuleDecided : GameEvent { public string Name, Description; }       // イベントルールの公開（2.2）
    public sealed class JobChanged : GameEvent { public int Player; public string Job; }
    public sealed class PokemonReleased : GameEvent { public int Player; public string Pokemon; }
    public sealed class DebugDone : GameEvent { public string Text; }
    public sealed class PokemonTraded : GameEvent { public int Winner, Loser; public string Taken, Given; }

    // 乱数（xorshift64*）。状態は GameState.Rng に持つ
    public static class Rng
    {
        public static int Range(ref ulong state, int min, int maxInclusive)
        {
            if (state == 0) state = 0x9E3779B97F4A7C15UL;
            state ^= state >> 12; state ^= state << 25; state ^= state >> 27;
            ulong r = state * 2685821657736338717UL;
            return min + (int)(r % (ulong)(maxInclusive - min + 1));
        }

        // 0以上1未満
        public static double Next(ref ulong state) => Range(ref state, 0, 999999) / 1000000.0;
        public static bool Chance(ref ulong state, double p) => Next(ref state) < p;
    }
}
