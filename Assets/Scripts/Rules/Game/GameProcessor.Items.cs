using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 準備フェーズで使うトレーナーの道具（仕様書8.3、8.6）：ふえ・くつ・お助け・妨害・バトルサーチャー・チケット
    // 道具の種類は ItemEffects.Of、だれ（どこ）に使うかは ItemEffects.TargetOf。数字は調整値（区分「道具」）
    // ponytail: チケット類はまだ（使い方を検討中）
    public sealed partial class GameProcessor
    {
        // 妨害が届くのは同じエリアの相手だけ（8.3）
        bool SameArea(PlayerState a, PlayerState b) => !OnIsland(a) && !OnIsland(b) && board[a.Square].Area == board[b.Square].Area; // 島はその人だけのエリア（3.4）

        // 使えるか（CheckUseItem から。ポケモンに使う道具より後）。だめなら理由
        string CheckTrainerItem(PlayerState p, ItemUse use, double value, int target)
        {
            switch (use)
            {
                case ItemUse.PickDice: return target >= 0 && target <= value ? null : $"0〜{value}から選んでください";
                case ItemUse.NoWild: return p.Has("ゴールドスプレー") ? "もう ゴールドスプレーが 効いています" : null;
                case ItemUse.Uncurse: return p.Has("のろい") ? null : "のろわれていません";
                case ItemUse.Trap: return State.Traps.ContainsKey(p.Square) ? "このマスには もうワナが あります" : null;
                case ItemUse.Sleep: case ItemUse.Curse: case ItemUse.Steal:
                {
                    if (!State.Players.Any(o => o != p && SameArea(p, o))) return "同じエリアに ほかのプレイヤーが いません";
                    if (target < 0 || target >= State.Players.Count || target == p.Id) return "相手を選んでください";
                    var o2 = State.Players[target];
                    if (!SameArea(p, o2)) return "同じエリアの相手にしか 使えません";
                    if (use == ItemUse.Sleep && o2.Has("さいみん")) return "その人は もう さいみん状態です";
                    if (use == ItemUse.Curse && o2.Has("のろい")) return "その人は もう のろわれています";
                    if (use == ItemUse.Steal && Stealable(p, o2).Count == 0) return "奪える道具が ありません";
                    return null;
                }
                case ItemUse.Searcher:
                    return State.Owners.TryGetValue(target, out int owner) && owner != p.Id && board.Squares.ContainsKey(target) && board[target].Type == "トレーナー" ? null : "ほかの人のファン（トレーナーマス）を 選んでください";
                case ItemUse.Ticket: case ItemUse.FanTicket:
                {
                    bool ok = use == ItemUse.Ticket ? p.VisitedCenters.Contains(target) : State.Owners.TryGetValue(target, out int o) && o == p.Id;
                    if (!ok) return use == ItemUse.Ticket ? "行ったことのある ポケモンセンターを 選んでください" : "自分のファンのマスを 選んでください";
                    if (target == p.Square) return "もう そこに います";
                    if (State.Blocked.ContainsKey(target)) return "カビゴンが いて 入れません";
                    return Movement.CanEnter(board, target, p.Badges, TopBadges, p.Surf) ? null : "まだ入れない マスです";
                }
                case ItemUse.Weather: return null;
                case ItemUse.Pager: return data.RandomEvents.Any(e => e.Bell) ? null : "ポケベルで 呼べる人が いません";
                case ItemUse.Snorlax: return State.Blocked.ContainsKey(p.Square) ? "このマスには もう カビゴンが います" : null;
                case ItemUse.PokeFlute:
                    if (!State.Blocked.ContainsKey(target)) return "カビゴンの いるマスを 選んでください";
                    return board[target].Area == board[p.Square].Area ? null : "同じエリアの カビゴンにしか 届きません";
                case ItemUse.DartBike: return p.Has("ダート自転車") ? "もう ダート自転車に 乗っています" : null;
                case ItemUse.SweetScent: return p.Has("アマいおこう") ? "もう アマいおこうが 効いています" : null;
                case ItemUse.Disguise: return p.Has("へんそう") ? "もう へんそう しています" : null;
                case ItemUse.EscapeRope:
                    if (board[p.Square].MapId == "") return "あなぬけひもは 洞窟や建物の中（メインマップ以外）で 使います";
                    return p.LastMainWarp >= 0 ? null : "もどる ワープマスが ありません";
            }
            return "その道具はまだ使えません";
        }

        // キャッチャで奪える道具：相手の袋（ボール・持たせたもちものは除く）のうち、自分の袋に入るもの
        List<string> Stealable(PlayerState me, PlayerState target) =>
            target.PokemonBag.Concat(target.TrainerBag).Where(x => HasRoomFor(me, x)).Distinct().ToList();

        const string PagerId = "ポケベル";

        // ポケベルで呼ぶ人を選んだ。やめたら準備フェーズに戻る（道具も残る）。呼んだらその人のイベント。待ちがあれば true
        bool PagerChosen(FacilityState f, int index, List<GameEvent> events, out bool backToPrepare)
        {
            var p = State.Players[f.Player];
            backToPrepare = index == f.Options.Count - 1;
            if (backToPrepare) { p.UsedItem = false; State.Phase = Phase.Prepare; return false; }
            var e = data.RandomEvents.First(x => x.Bell && x.Name == f.Options[index]);
            RemoveItem(p, "ポケベル");
            Say("ev_bell_answer", events, p, ("名前", e.Name));
            return StartEvent(e, events);
        }

        // 施設で持ち主と同じ扱いになるか（へんそう中なら、ほかの人のライバルマスでも）
        public bool ActsAsOwner(FacilityState f, PlayerState p) => f.Owner == p.Id || p.Has("へんそう");

        // 相手からの妨害を防ぐ。かごのみは さいみんだけ、みがわりにんぎょうは何でも1回（どちらも なくなる）。両方あれば かごのみ を先に使う
        bool Shielded(PlayerState t, string attack, List<GameEvent> events)
        {
            string guard = attack == "さいみん" && t.TrainerBag.Contains("かごのみ") ? "かごのみ" : t.TrainerBag.Contains("みがわりにんぎょう") ? "みがわりにんぎょう" : null;
            if (guard == null) return false;
            RemoveItem(t, guard);
            events.Add(new ItemBlocked { Player = t.Id, Item = guard, Attack = attack });
            return true;
        }

        void AddAilment(PlayerState p, string name, int weeks, int by, List<GameEvent> events)
        {
            p.Ailments.RemoveAll(a => a.Name == name);
            p.Ailments.Add(new PlayerAilment { Name = name, Weeks = weeks });
            events.Add(new AilmentAdded { Player = p.Id, Name = name, Weeks = weeks, By = by });
        }

        void DoTrainerItem(PlayerState p, ItemUse use, double value, int target, List<GameEvent> events)
        {
            var o = target >= 0 && target < State.Players.Count ? State.Players[target] : null;
            switch (use)
            {
                case ItemUse.PickDice: p.FixedDice = target; break;
                case ItemUse.Weather:
                    // 三竜のふえ：週イベントの天候より優先（7.13）。週イベントは裏で続く
                    State.FluteWeather = ItemEffects.WeatherName(value);
                    State.FluteWeeks = TuneInt("flute_weeks");
                    events.Add(new FluteStarted { Player = p.Id, Weather = State.FluteWeather, Weeks = State.FluteWeeks });
                    break;
                case ItemUse.NoWild: AddAilment(p, "ゴールドスプレー", TuneInt("gold_spray_weeks"), p.Id, events); break;
                case ItemUse.Uncurse: p.Ailments.RemoveAll(a => a.Name == "のろい"); events.Add(new AilmentCured { Player = p.Id, Name = "のろい" }); break;
                case ItemUse.Sleep: if (!Shielded(o, "さいみん", events)) AddAilment(o, "さいみん", 1, p.Id, events); break;
                case ItemUse.Curse: if (!Shielded(o, "のろい", events)) AddAilment(o, "のろい", Roll(TuneInt("curse_weeks_min"), TuneInt("curse_weeks_max")), p.Id, events); break;
                case ItemUse.Steal:
                    if (Shielded(o, "キャッチャ", events)) break;
                    // ponytail: 奪う道具はランダム。選ばせるなら UseItem に道具名を足す
                    var list = Stealable(p, o);
                    string item = list[Roll(0, list.Count - 1)];
                    RemoveItem(o, item);
                    PutItem(p, item); // 入るものだけ選んでいるので、袋いっぱいの画面は出ない
                    events.Add(new ItemLost { Player = o.Id, Item = item, To = p.Id });
                    break;
                case ItemUse.Trap:
                    State.Traps[p.Square] = p.Id;
                    events.Add(new TrapSet { Player = p.Id, Square = p.Square });
                    break;
                case ItemUse.Searcher:
                {
                    var owner = State.Players[State.Owners[target]];
                    if (!Shielded(owner, "バトルサーチャー", events)) SetOwner(target, p.Id, events);
                    break;
                }
                case ItemUse.Ticket: case ItemUse.FanTicket: case ItemUse.EscapeRope:
                {
                    // アーマーガーで飛ぶ／あなぬけひもで、最後に止まったメインマップのワープマスへ。そのあと いつもどおりサイコロを振る
                    int from = p.Square;
                    p.Square = use == ItemUse.EscapeRope ? p.LastMainWarp : target;
                    events.Add(new Warped { Player = p.Id, From = from, To = p.Square });
                    break;
                }
                case ItemUse.Pager:
                {
                    // ランダムに4人（ランダムイベント.csv の「ポケベル」が○の人）。選んだ人のイベントが起きる。この番はサイコロを振らない
                    Say("ev_bell_use", events, p);
                    var all = data.RandomEvents.Where(e => e.Bell).ToList();
                    var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = PagerId, Prompt = "ポケベルで だれを 呼ぶ？（呼ぶと この番は サイコロを ふらない）" };
                    for (int i = 0; i < TuneInt("pager_candidates") && all.Count > 0; i++) { int k = Roll(0, all.Count - 1); f.Options.Add(all[k].Name); all.RemoveAt(k); }
                    f.Options.Add("やめておく");
                    OpenFacility(f, events);
                    break;
                }
                case ItemUse.Snorlax:
                {
                    // 自分のいるマスにカビゴン。だれも通れない（自分も、ここから出るのはよい）
                    int weeks = Roll(TuneInt("snorlax_weeks_min"), TuneInt("snorlax_weeks_max"));
                    State.Blocked[p.Square] = weeks;
                    events.Add(new SnorlaxPlaced { Player = p.Id, Square = p.Square, Weeks = weeks });
                    break;
                }
                case ItemUse.PokeFlute:
                    State.Blocked.Remove(target);
                    events.Add(new SnorlaxRemoved { Square = target, Player = p.Id });
                    break;
                case ItemUse.DartBike: AddAilment(p, "ダート自転車", TuneInt("dart_bike_weeks"), p.Id, events); p.BikeUses++; CheckCyclingUnlock(p, events); break;
                case ItemUse.SweetScent: AddAilment(p, "アマいおこう", Roll(TuneInt("sweet_scent_weeks_min"), TuneInt("sweet_scent_weeks_max")), p.Id, events); break;
                // へんそう：1週だけ、ほかの人のライバルマス（ロケット団マスも作ったら）で持ち主と同じ扱い
                case ItemUse.Disguise: AddAilment(p, "へんそう", 1, p.Id, events); break;
            }
        }

        // ワナのマスに止まった（自分のワナは踏まない）。手持ち全員が最大HPの25%のダメージ。止まった先の出来事では倒れないよう1は残す
        void SpringTrap(PlayerState p, List<GameEvent> events)
        {
            if (!State.Traps.TryGetValue(p.Square, out int owner) || owner == p.Id) return;
            State.Traps.Remove(p.Square);
            if (Shielded(p, "ワナ", events)) return;
            foreach (var m in p.Party.Where(m => !m.Fainted))
                m.Hp = Math.Max(1, m.Hp - Math.Max(1, (int)(Pokemons.MaxHp(data, m) * Tune("trap_damage"))));
            events.Add(new TrapSprung { Player = p.Id, Owner = owner, Square = p.Square });
        }
    }
}
