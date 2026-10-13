using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 旅の記録（仕様書1.5 のエンドロール用）。命令を処理したあとに、出来事から「年表に残すもの」と旅の数字を拾う
    // 記録するもの：はじめての捕獲、バッジ、進化、職業への就任、ロケット団・ライバル・ジムの撃破、絆による回避、レベル追いつき、引き分け
    public sealed partial class GameProcessor
    {
        // いま行われているバトル（BattleStarted で覚え、勝ち負けの記録に使う）
        BattleStarted journalBattle;

        void Record(List<GameEvent> events)
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case Moved m: State.Players[m.Player].StepsWalked += m.Path.Count - 1; break;
                    case BattleStarted b:
                        journalBattle = b;
                        foreach (int id in b.Players.Where(id => id >= 0)) State.Players[id].Battles++;
                        break;
                    case Caught c when State.Players[c.Player].Journal.All(j => j.Kind != "catch"):
                        Log(c.Player, "catch", $"はじめて {c.Pokemon}を つかまえた");
                        break;
                    case BadgeEarned b: Log(b.Player, "badge", $"{b.Gym}の バッジを てにいれた（{b.Badges}個目）"); break;
                    case Evolved v: Log(v.Player, "evolve", $"{v.From}が {v.To}に しんかした"); break;
                    case JobChanged j when j.Job != "": Log(j.Player, "job", $"{j.Job}に なった"); break;
                    case RocketDefeated r: Log(r.Player, "rocket", "ロケット団を 追いはらった"); break;
                    case LevelCaughtUp l: Log(l.Player, "catchup", $"{l.Pokemon}が 格上を たおして Lv{l.Level}に"); break;
                    case RoundResolved r when r.Bond && journalBattle != null:
                    {
                        int id = journalBattle.Players[1 - r.Attacker];
                        if (id >= 0) Log(id, "bond", $"{r.DefenderPokemon}が {r.DefenderTrainer}の ために 技を よけた");
                        break;
                    }
                    case BattleEnded be when journalBattle != null:
                    {
                        var b = journalBattle;
                        if (be.Draw) foreach (int id in b.Players.Where(id => id >= 0)) Log(id, "draw", $"{(b.Kind == BattleKind.Player ? Opponent(b, id) : b.Opponent)}と ひきわけた");
                        else if (be.Winner >= 0 && b.Players[be.Winner] >= 0 && (b.Kind == BattleKind.Gym || b.Kind == BattleKind.Rival))
                            Log(b.Players[be.Winner], b.Kind == BattleKind.Gym ? "gym" : "rival", $"{b.Opponent}に 勝った");
                        journalBattle = null;
                        break;
                    }
                }
            }
        }

        string Opponent(BattleStarted b, int me) => State.Players[b.Players[0] == me ? b.Players[1] : b.Players[0]].Name;

        void Log(int player, string kind, string text) =>
            State.Players[player].Journal.Add(new JournalEntry { Week = State.Week, Place = board[State.Players[player].Square].Place, Kind = kind, Text = text });
    }
}
