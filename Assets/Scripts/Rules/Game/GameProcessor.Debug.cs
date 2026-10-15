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
                case DebugAction.Badge:
                    return NextGym(p) != null ? null : "もうすべてのバッジを持っています";
                case DebugAction.AddPokemon:
                    if (data.FindSpecies(d.Text) == null) return $"ポケモン「{d.Text}」がいません";
                    return p.Party.Count < TuneInt("party_size") || p.Box.Count < BoxLimit(p) ? null : "手持ちも控えもいっぱいです";
                case DebugAction.WinBattle:
                case DebugAction.LoseBattle:
                    return State.Phase == Phase.Battle && State.Battle.Step != BattleStep.Reward && State.Battle.Sides.All(s => s.Active >= 0) ? null : "バトル中（コマンドを選ぶとき）だけ";
                case DebugAction.Weather:
                    return d.Text == "" || d.Text == "雨季" || d.Text == "乾季" || d.Text == "すなあらしの日" ? null : "天候は 雨季／乾季／すなあらしの日 のどれか";
                case DebugAction.WeekEvent:
                    return d.Text == "" || d.Text == "セール" || d.Text == "ストライキ" ? null : "週イベントは セール／ストライキ のどちらか";
                case DebugAction.NextWeek:
                    return State.Phase == Phase.Prepare ? null : "週を進めるのはサイコロを振る前だけ";
                case DebugAction.Status:
                    return new[] { "やけど", "どく", "まひ", "ねむり", "" }.Contains(d.Text) ? null : "状態異常は やけど／どく／まひ／ねむり（空なら治す）";
                case DebugAction.Battle:
                    if (State.Phase != Phase.Prepare) return "バトルを始めるのはサイコロを振る前だけ";
                    if (data.FindSpecies(d.Text) == null) return $"ポケモン「{d.Text}」がいません";
                    return p.Party.Any(m => !m.Fainted) ? null : "戦えるポケモンがいません";
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
                    string gym = NextGym(p);
                    p.Gyms.Add(gym);
                    events.Add(new BadgeEarned { Player = p.Id, Gym = gym, Badges = p.Badges });
                    break;
                case DebugAction.HealAll: HealParty(p, events); break;
                case DebugAction.LevelUp:
                    foreach (var m in p.Party) Pokemons.SetLevel(data, m, m.Level + d.Value);
                    text = $"手持ちのレベルを {d.Value} 上げた";
                    break;
                case DebugAction.AddPokemon:
                    AddPokemon(p, Pokemons.Create(data, d.Text, System.Math.Max(1, d.Value), "デバッグ", board[p.Square].Place, State.Week), events);
                    text = $"{d.Text} Lv{System.Math.Max(1, d.Value)} を もらった";
                    break;
                case DebugAction.WinBattle:
                case DebugAction.LoseBattle:
                {
                    var b = State.Battle;
                    int me = b.Sides[0].Player == p.Id ? 0 : 1;
                    int loser = d.Action == DebugAction.WinBattle ? 1 - me : me;
                    var lose = b.Sides[loser];
                    if (loser != 0) b.DefeatedLevels.AddRange(lose.Team.Where(m => !m.Fainted).Select(m => m.Level));
                    foreach (var m in lose.Team) m.Hp = 0;
                    events.Add(new DebugDone { Text = d.Action == DebugAction.WinBattle ? "即勝利" : "即敗北" });
                    EndBattle(1 - loser, false, events);
                    return;
                }
                case DebugAction.Weather:
                    State.Weather = d.Text; State.WeatherWeeks = d.Text == "" ? 0 : TuneInt("event_weeks");
                    if (d.Text != "") events.Add(new WeekEventHappened { Name = d.Text, Weeks = State.WeatherWeeks });
                    else text = "天候を なしにした";
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
                    var lead = p.Party[0];
                    lead.Status = d.Text == "やけど" ? Status.Burn : d.Text == "どく" ? Status.Poison : d.Text == "まひ" ? Status.Paralysis : d.Text == "ねむり" ? Status.Sleep : Status.None;
                    lead.SleepRounds = lead.Status == Status.Sleep ? 2 : 0;
                    text = $"{lead.Species}を {(d.Text == "" ? "元気に" : d.Text + "に")} した";
                    break;
                }
                case DebugAction.Battle:
                    events.Add(new DebugDone { Text = $"野生の {d.Text} Lv{System.Math.Max(1, d.Value)} と バトル" });
                    StartBattle(BattleKind.Wild, false, Npc($"野生の{d.Text}", new[] { (d.Text, System.Math.Max(1, d.Value)) }), events);
                    return;
                case DebugAction.WeekEvent:
                    State.WeekEvent = d.Text; State.WeekEventWeeks = d.Text == "" ? 0 : TuneInt("event_weeks");
                    if (d.Text != "") events.Add(new WeekEventHappened { Name = d.Text, Weeks = State.WeekEventWeeks });
                    else text = "週イベントを なしにした";
                    break;
            }
            if (text != null) events.Add(new DebugDone { Text = text });
        }

        // まだ持っていないジムのうち、何個目のバッジかがいちばん早いもの
        string NextGym(PlayerState p) =>
            data.GymMembers.Select(m => m.Gym).Distinct().Where(g => !p.Gyms.Contains(g))
                .OrderBy(g => data.GymMembers.First(m => m.Gym == g).BadgeOrder).FirstOrDefault();
    }
}
