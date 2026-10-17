using System.Collections.Generic;

namespace Sugoroku.Rules
{
    // 移動のルール（仕様書4章）。歩き方の状態は (直前のマス, 今いるマス, 残り歩数)
    public static class Movement
    {
        // 入れるか：場所の解放（3.3）と、マスの通行条件（バッジの数）と、海上マス（なみのり、4.1.2）。
        // top：いちばんバッジの多い人の数。全員に開くマスは、だれか1人が条件を満たせば通れる（バッジは減らないので、いちど開いたら開いたまま）。省略すると自分だけで見る
        // surf：なみのりを覚えているか。覚えていなければ 海上マスは 未解放のマスと同じく通れない。省略すると覚えている扱い
        public static bool CanEnter(BoardData board, int square, int badges, int top = -1, bool surf = true)
        {
            var s = board[square];
            return s.RequiredBadges <= badges && s.GateBadges <= (s.GateShared ? System.Math.Max(badges, top) : badges) && (surf || !s.Sea);
        }

        // start から、直前のマスに戻らずに（allowReverse なら戻ってもよい）ちょうど steps 歩で止まれるマス
        // blocked：ふさがれていて入れないマス（アマイ三ツのカビゴン）。省略できる
        public static HashSet<int> Reachable(BoardData board, int start, int steps, int badges, bool allowReverse, ICollection<int> blocked = null, int top = -1, bool surf = true)
        {
            var result = new HashSet<int>();
            Walk(board, -1, start, steps, badges, top, surf, allowReverse, blocked, (prev, cur, left) => { if (left == 0) result.Add(cur); });
            return result;
        }

        // prev から cur に来て残り left 歩のとき、targets のどれかにちょうど止まれるか（1マスずつ方式で、行き詰まる道を選ばせないため）
        // slack：歩数を最後まで使わず、残りが slack 以下で止まってもよい（サイクリング）
        public static bool CanFinish(BoardData board, int prev, int cur, int left, int badges, bool allowReverse, HashSet<int> targets, ICollection<int> blocked = null, int slack = 0, int top = -1, bool surf = true)
        {
            bool found = false;
            Walk(board, prev, cur, left, badges, top, surf, allowReverse, blocked, (p, c, l) => { if (l <= slack && targets.Contains(c)) found = true; });
            return found;
        }

        // start から goal まで、ちょうど steps 歩の道順を1本（start と goal を含む）。なければ null
        public static List<int> FindPath(BoardData board, int start, int steps, int badges, bool allowReverse, int goal, ICollection<int> blocked = null, int top = -1, bool surf = true)
        {
            var parent = new Dictionary<(int prev, int cur, int left), (int prev, int cur, int left)>();
            var first = (-1, start, steps);
            var queue = new Queue<(int prev, int cur, int left)>();
            queue.Enqueue(first);
            parent[first] = first;
            while (queue.Count > 0)
            {
                var s = queue.Dequeue();
                if (s.left == 0)
                {
                    if (s.cur != goal) continue;
                    var path = new List<int>();
                    for (var x = s; ; x = parent[x]) { path.Add(x.cur); if (x.Equals(first)) break; }
                    path.Reverse();
                    return path;
                }
                foreach (int n in board[s.cur].Neighbors)
                {
                    if ((!allowReverse && n == s.prev) || !CanEnter(board, n, badges, top, surf) || (blocked != null && blocked.Contains(n))) continue;
                    var next = (s.cur, n, s.left - 1);
                    if (parent.ContainsKey(next)) continue;
                    parent[next] = s;
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        // from から to まで、道をたどって何マスか（入れないマスは通らない。ワープは数えない）。行けなければ -1（見渡しモード、16.7）
        public static int Distance(BoardData board, int from, int to, int badges, int top = -1, bool surf = true)
        {
            var dist = new Dictionary<int, int> { [from] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                if (s == to) return dist[s];
                foreach (int n in board[s].Neighbors)
                    if (!dist.ContainsKey(n) && CanEnter(board, n, badges, top, surf)) { dist[n] = dist[s] + 1; queue.Enqueue(n); }
            }
            return -1;
        }

        // ありうる歩き方をすべてたどる（同じ状態は1回だけ）
        static void Walk(BoardData board, int prev, int start, int steps, int badges, int top, bool surf, bool allowReverse, ICollection<int> blocked, System.Action<int, int, int> visit)
        {
            var seen = new HashSet<(int, int, int)>();
            var stack = new Stack<(int prev, int cur, int left)>();
            stack.Push((prev, start, steps));
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                if (!seen.Add(s)) continue;
                visit(s.prev, s.cur, s.left);
                if (s.left == 0) continue;
                foreach (int n in board[s.cur].Neighbors)
                    if ((allowReverse || n != s.prev) && CanEnter(board, n, badges, top, surf) && (blocked == null || !blocked.Contains(n)))
                        stack.Push((s.cur, n, s.left - 1));
            }
        }
    }
}
