using System.Linq;

namespace Sugoroku.Rules
{
    // 文の自動送り（相談38、仕様書 1.3・1.4・17.6）。表示側（GameRunner の ReadMessage）だけが使う。処理役は使わない（15.3 の約束7：待つのは表示側）
    public static class MessagePace
    {
        // その文を読む人（すごろくの番の人と、バトル中なら両者）に 人間が1人もいなければ、○ を待たずに自動で送る
        // 例：CPU の番・CPU どうしのプレイヤー戦 → 自動。CPU が人間にプレイヤー戦をしかけた → 待つ
        public static bool AutoAdvance(GameState s, int turnPlayer, int[] battlePlayers)
        {
            bool Human(int p) => p >= 0 && p < s.Players.Count && !s.Players[p].IsCpu;
            return !Human(turnPlayer) && (battlePlayers == null || !battlePlayers.Any(Human));
        }

        // 自動で送るときの間（全文が出てから）。バトル中で「バトル演出を短くする」がオンなら 半分（17.6）
        public static float AutoSeconds(bool inBattle, bool shortBattle) => inBattle && shortBattle ? 0.25f : 0.5f;
    }

    // 文を読む人を 出来事を見せながら追う（再生中のゲーム状態は もう命令のあとなので、状態からは読めない）
    // バトルの両者は バトルの後の文（経験値・進化）まで読む。番が変わる・週が進むと もう読まない（バトルの終わりと同じ命令で 次の番まで進むため）
    public sealed class MessageReaders
    {
        public int Turn = -1;
        public int[] Battle;
        public bool InBattle => Battle != null;

        public void See(GameEvent e)
        {
            switch (e)
            {
                case TurnStarted t: Turn = t.Player; Battle = null; break;
                case WeekAdvanced _: Battle = null; break;
                case BattleStarted b: Battle = b.Players; break;
                case BattleResumed r: Battle = r.Players; break;
                case BattleSuspended _: Battle = null; break;
            }
        }

        public bool Auto(GameState s) => MessagePace.AutoAdvance(s, Turn, Battle);
    }
}
