using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // デバッグ機能（仕様書17.1）。ふつうの命令と同じ入口を通るので、命令の記録（15.9）にも残る
    // 製品として遊ぶときは DebugEnabled を false にする（画面側で開発用ビルドのときだけ true にしている）
    public sealed partial class GameProcessor
    {
        public bool DebugEnabled;

        string CheckDebug(DebugCommand d)
        {
            if (!DebugEnabled) return "デバッグ機能はオフです";
            var p = Current;
            switch (d.Action)
            {
                case DebugAction.Warp:
                    if (State.Phase != Phase.Prepare) return "ワープはサイコロを振る前だけ";
                    return board.Squares.ContainsKey(d.Value) ? null : "そのマスはありません";
                case DebugAction.SetDice:
                    return State.Phase == Phase.Prepare ? null : "出目の指定はサイコロを振る前だけ";
                case DebugAction.Item:
                    if (data.FindItem(d.Text) == null) return $"アイテム「{d.Text}」がありません";
                    return HasRoomFor(p, d.Text) ? null : "袋がいっぱいです";
                case DebugAction.Badge: // Value が 0 なら 1個、1 以上なら その数まで（8 なら 全エリア解放。7個目で トキワジムも出る）
                    return NextGym(p) != null && (d.Value <= 0 || p.Badges < d.Value) ? null : "もうすべてのバッジを持っています";
                case DebugAction.AddPokemon:
                    if (data.FindSpecies(d.Text) == null) return $"ポケモン「{d.Text}」がいません";
                    return p.Party.Count < TuneInt("party_size") || p.Box.Count < BoxLimit(p) ? null : "手持ちも控えもいっぱいです";
                case DebugAction.WinBattle:
                case DebugAction.LoseBattle:
                    return State.Phase == Phase.Battle && State.Battle.Step != BattleStep.Reward && State.Battle.Sides.All(s => s.Active >= 0) ? null : "バトル中（コマンドを選ぶとき）だけ";
                case DebugAction.Weather:
                    return d.Text == "" || d.Text == "雨季" || d.Text == "乾季" || d.Text == "すなあらしの日" ? null : "天候は 雨季／乾季／すなあらしの日 のどれか";
                case DebugAction.WeekEvent: // 週イベントの発生（17.1）。突発バトルイベントは 週のはじめの流れ（手番の前のトーナメント）なので ここでは起こさない
                    if (d.Text == "") return null;
                    if (d.Text == "突発バトルイベント") return "突発バトルイベントは デバッグでは 起こせません（週のはじめの流れのため）";
                    return data.WeekEvents.Any(x => x.Name == d.Text) ? null : $"週イベント「{d.Text}」は ありません";
                case DebugAction.NextWeek:
                    return State.Phase == Phase.Prepare ? null : "週を進めるのはサイコロを振る前だけ";
                case DebugAction.Status: // Value：手持ちの何番目（0 が先頭）
                    if (d.Value < 0 || d.Value >= p.Party.Count) return "その番号の手持ちは いません";
                    return new[] { "やけど", "どく", "まひ", "ねむり", "" }.Contains(d.Text) ? null : "状態異常は やけど／どく／まひ／ねむり（空なら治す）";
                case DebugAction.Battle:
                    if (State.Phase != Phase.Prepare) return "バトルを始めるのはサイコロを振る前だけ";
                    if (!p.Party.Any(m => !m.Fainted)) return "戦えるポケモンがいません";
                    return CheckDebugBattle(DebugFoe.Parse(d.Text), d.Value, p);
                default:
                    return null;
            }
        }

        void DoDebug(DebugCommand d, List<GameEvent> events)
        {
            var p = Current;
            string text = null;
            switch (d.Action)
            {
                case DebugAction.Warp:
                    events.Add(new Warped { Player = p.Id, From = p.Square, To = d.Value });
                    p.Square = d.Value;
                    break;
                case DebugAction.SetDice: p.FixedDice = d.Value; text = $"次の出目を {d.Value} にした"; break;
                case DebugAction.Money: AddMoney(p, d.Value, "デバッグ", events); break;
                case DebugAction.Balls:
                    foreach (var ball in new[] { "モンスターボール", "スーパーボール", "ハイパーボール" })
                        p.Balls[ball] = (p.Balls.TryGetValue(ball, out int n) ? n : 0) + d.Value;
                    text = $"ボールを {d.Value}個ずつ もらった";
                    break;
                case DebugAction.Item: PutItem(p, d.Text); events.Add(new ItemGained { Player = p.Id, Item = d.Text }); break;
                case DebugAction.Badge:
                    do EarnBadge(p, NextGym(p), events); while (d.Value > 0 && p.Badges < d.Value && NextGym(p) != null);
                    break;
                case DebugAction.HealAll: HealParty(p, events); break;
                case DebugAction.LevelUp:
                    foreach (var m in p.Party) Pokemons.SetLevel(data, m, m.Level + d.Value);
                    text = $"手持ちのレベルを {d.Value} 上げた";
                    break;
                case DebugAction.AddPokemon:
                    AddPokemon(p, Pokemons.Create(data, d.Text, Math.Max(1, d.Value), "デバッグ", board[p.Square].Place, State.Week), events);
                    text = $"{d.Text} Lv{Math.Max(1, d.Value)} を もらった";
                    break;
                case DebugAction.WinBattle:
                case DebugAction.LoseBattle:
                {
                    var b = State.Battle;
                    int me = b.Sides[0].Player == p.Id ? 0 : 1;
                    int loser = d.Action == DebugAction.WinBattle ? 1 - me : me;
                    var lose = b.Sides[loser];
                    b.DefeatedLevels[1 - loser].AddRange(lose.Team.Where(m => !m.Fainted).Select(m => m.Level));
                    foreach (var m in lose.Team) m.Hp = 0;
                    events.Add(new DebugDone { Text = d.Action == DebugAction.WinBattle ? "即勝利" : "即敗北" });
                    EndBattle(1 - loser, false, events);
                    return;
                }
                case DebugAction.Weather: // 週イベントと同じ処理（同じ枠なら上書き、週数も同じ。相談11）
                    if (d.Text != "") ApplyWeekEvent(d.Text, events);
                    else { State.Weather = ""; State.WeatherWeeks = 0; text = "天候を なしにした"; }
                    break;
                case DebugAction.NextWeek:
                {
                    // 全員の手番を飛ばして、次の週の最初の人の番まで進める
                    int week = State.Week;
                    for (int i = 0; i < State.Players.Count * 2 && State.Week == week && State.Phase != Phase.GameOver; i++) EndTurn(events);
                    text = $"{State.Week}週目に 進めた";
                    break;
                }
                case DebugAction.Status:
                {
                    var mon = p.Party[d.Value];
                    mon.Status = d.Text == "やけど" ? Status.Burn : d.Text == "どく" ? Status.Poison : d.Text == "まひ" ? Status.Paralysis : d.Text == "ねむり" ? Status.Sleep : Status.None;
                    mon.SleepRounds = mon.Status == Status.Sleep ? 2 : 0;
                    text = $"{mon.Species}を {(d.Text == "" ? "元気に" : d.Text + "に")} した";
                    break;
                }
                case DebugAction.Battle:
                    StartDebugBattle(DebugFoe.Parse(d.Text), d.Value, p, events);
                    return;
                case DebugAction.WeekEvent: // 週イベントと同じ処理（セールとストライキは同じ枠、週数も同じ。相談11）
                    if (d.Text != "") ApplyWeekEvent(d.Text, events);
                    else { State.WeekEvent = ""; State.WeekEventWeeks = 0; text = "週イベントを なしにした"; }
                    break;
            }
            if (text != null) events.Add(new DebugDone { Text = text });
        }

        // ---- 相手を指定してバトル（17.1）----
        // 命令の Text に「種類:相手」（プレイヤー戦は「プレイヤー:番号:フル」も）。種類がなければ 野生（前のセーブの書き方）。野生の Lv は Value
        // バトルの種類・終わったあとの扱いは ふつうにその相手と戦うときと同じ（同じ関数で始める）。マスのある相手（トレーナー・ライバル・ロケット団）は そのマスへ移ってから
        public sealed class DebugFoe
        {
            public string Kind = "野生", Target = "";
            public bool Full;
            public static DebugFoe Parse(string text)
            {
                var parts = (text ?? "").Split(':');
                if (parts.Length == 1) return new DebugFoe { Target = parts[0].Trim() };
                return new DebugFoe { Kind = parts[0], Target = parts[1], Full = parts.Length > 2 && parts[2] == "フル" };
            }
        }

        public static readonly string[] DebugFoeKinds = { "野生", "トレーナー", "ジム", "ライバル", "ロケット団", "イベント", "リーグ", "プレイヤー" };

        // 画面に並べる相手：(名前, 命令の Text)。野生は ポケモンの名前を入れるので 空
        public List<(string label, string text)> DebugFoes(string kind)
        {
            var list = new List<(string, string)>();
            switch (kind)
            {
                case "トレーナー":
                    foreach (var t in data.Trainers.Where(t => t.Kind != "ロケット団" && SquareNamed(t.Square) != null)) list.Add(($"{(t.Name != "" ? t.Name : t.Class)}（{t.Place}）", "トレーナー:" + t.Id));
                    break;
                case "ジム":
                    foreach (var gym in data.GymMembers.OrderBy(m => m.BadgeOrder).Select(m => m.Gym).Distinct()) list.Add((gym, "ジム:" + gym));
                    break;
                case "ライバル":
                    foreach (var r in data.Rivals.Where(r => RivalSquare(r) != null)) list.Add(($"{r.Name}（{r.Place}）", "ライバル:" + r.Id));
                    break;
                case "ロケット団":
                    foreach (var s in board.Squares.Values.Where(s => s.Type == "ロケット団").OrderBy(s => s.Id)) list.Add(($"{s.Place}（{s.Id}）", "ロケット団:" + s.Id));
                    break;
                case "イベント":
                    foreach (var e in DebugEventBattles()) list.Add(($"{e.Name}（{data.FindParty(e.Value).Name}）", "イベント:" + e.Id));
                    break;
                case "リーグ":
                    for (int i = 0; i < data.League.Count; i++) list.Add(($"{i + 1}人目 {data.League[i].Name}", "リーグ:" + i));
                    break;
                case "プレイヤー":
                    foreach (var o in State.Players.Where(o => o != Current)) { list.Add(($"{o.Name}（クイック）", $"プレイヤー:{o.Id}")); list.Add(($"{o.Name}（フル）", $"プレイヤー:{o.Id}:フル")); }
                    break;
            }
            return list;
        }

        BoardSquare SquareNamed(string name) => string.IsNullOrEmpty(name) ? null : board.Squares.Values.FirstOrDefault(s => s.Name == name);
        BoardSquare RivalSquare(RivalEntry r) => board.Squares.Values.FirstOrDefault(s => s.Type == "ライバル" && s.Place == r.Place);
        IEnumerable<Effect> DebugEventBattles() =>
            data.EventSquares.Select(x => x.Effect).Concat(data.RandomEvents)
                .Where(e => e != null && (e.Kind == EffectKind.Battle || e.Kind == EffectKind.Dojo) && data.FindParty(e.Value)?.Members.Count > 0)
                .GroupBy(e => e.Id).Select(g => g.First());

        string CheckDebugBattle(DebugFoe f, int level, PlayerState p)
        {
            switch (f.Kind)
            {
                case "野生": return data.FindSpecies(f.Target) != null ? null : $"ポケモン「{f.Target}」がいません";
                case "トレーナー": { var t = data.Trainers.FirstOrDefault(x => x.Id == f.Target); return t != null && SquareNamed(t.Square) != null ? null : $"トレーナー「{f.Target}」の マスがありません"; }
                case "ジム": return data.GymMembers.Any(m => m.Gym == f.Target) ? null : $"ジム「{f.Target}」がありません";
                case "ライバル": { var r = data.Rivals.FirstOrDefault(x => x.Id == f.Target); return r != null && RivalSquare(r) != null ? null : $"ライバル「{f.Target}」の マスがありません"; }
                case "ロケット団": return int.TryParse(f.Target, out int sq) && board.Squares.TryGetValue(sq, out var s) && s.Type == "ロケット団" ? null : "その ロケット団マスは ありません";
                case "イベント": return DebugEventBattles().Any(e => e.Id == f.Target) ? null : $"イベントのバトル「{f.Target}」が ありません";
                case "リーグ": return int.TryParse(f.Target, out int n) && n >= 0 && n < data.League.Count ? null : "その リーグの相手は いません";
                case "プレイヤー": return int.TryParse(f.Target, out int id) && id >= 0 && id < State.Players.Count && id != p.Id && State.Players[id].Party.Any(m => !m.Fainted) ? null : "その プレイヤーとは 戦えません";
                default: return $"相手の種類「{f.Kind}」は ありません";
            }
        }

        void StartDebugBattle(DebugFoe f, int level, PlayerState p, List<GameEvent> events)
        {
            void MoveTo(BoardSquare sq) { if (p.Square == sq.Id) return; events.Add(new Warped { Player = p.Id, From = p.Square, To = sq.Id }); p.Square = sq.Id; }
            events.Add(new DebugDone { Text = f.Kind == "野生" ? $"野生の {f.Target} Lv{Math.Max(1, level)} と バトル" : $"{f.Kind}（{f.Target}{(f.Full ? "・フル" : "")}）と バトル" });
            switch (f.Kind)
            {
                case "野生":
                    StartBattle(BattleKind.Wild, false, Npc($"野生の{f.Target}", new[] { (f.Target, Math.Max(1, level)) }), events);
                    return;
                case "トレーナー":
                {
                    var entry = data.Trainers.First(x => x.Id == f.Target);
                    var sq = SquareNamed(entry.Square);
                    MoveTo(sq);
                    StartTrainerBattle(sq, entry, events);
                    return;
                }
                case "ジム":
                    StartBattle(BattleKind.Gym, true, GymTeam(f.Target, p), events, f.Target);
                    return;
                case "ライバル":
                {
                    var sq = RivalSquare(data.Rivals.First(x => x.Id == f.Target));
                    MoveTo(sq);
                    RivalBattle(sq, events);
                    return;
                }
                case "ロケット団":
                {
                    var sq = board[int.Parse(f.Target)];
                    MoveTo(sq);
                    StartRocketBattle(sq, events);
                    return;
                }
                case "イベント":
                {
                    var e = DebugEventBattles().First(x => x.Id == f.Target);
                    var party = data.FindParty(e.Value);
                    StartBattle(BattleKind.Event, party.Full, Npc(party.Name, party.Members), events);
                    B.EventId = e.Id;
                    return;
                }
                case "リーグ":
                    p.LeagueBeaten = int.Parse(f.Target);
                    StartLeagueBattle(p, events);
                    return;
                case "プレイヤー":
                {
                    var o = State.Players[int.Parse(f.Target)];
                    StartBattle(BattleKind.Player, f.Full, new BattleSide { Player = o.Id, Name = o.Name, Team = o.Party }, events); // ふだんの プレイヤー戦は クイック（7.6）。フルは 確かめる用
                    return;
                }
            }
        }

        // まだ持っていないジムのうち、何個目のバッジかがいちばん早いもの
        string NextGym(PlayerState p) =>
            data.GymMembers.Select(m => m.Gym).Distinct().Where(g => !p.Gyms.Contains(g))
                .OrderBy(g => data.GymMembers.First(m => m.Gym == g).BadgeOrder).FirstOrDefault();
    }
}
