using System.Linq;

namespace Sugoroku.Rules
{
    // CSV の「条件の種類」「条件の値」（設計メモ/CSVの書き方.md の4・7）。新しい条件はここに1つ足す
    public static class Conditions
    {
        static readonly string[] Known = { "", "いつも", "どれにも当てはまらない", "バッジの数", "図鑑の登録数", "経過した週の数", "御三家を選んだ人がいる", "道具を渡す", "このマスの所有者" };

        public static bool IsKnown(string kind) => Known.Contains(kind);

        public static bool Met(string kind, string value, PlayerState p, GameState s)
        {
            int.TryParse(value, out int n);
            switch (kind)
            {
                case "": case "いつも": case "どれにも当てはまらない": return true;
                case "バッジの数": return p.Badges >= n;
                case "図鑑の登録数": return p.Dex.Count >= n;
                case "経過した週の数": return s.Week >= n;
                case "このマスの所有者": return s.Owners.TryGetValue(p.Square, out int owner) && owner == p.Id; // 今いるマスの持ち主が自分
                case "道具を渡す": return p.PokemonBag.Contains(value) || p.TrainerBag.Contains(value); // 持っていれば満たす。渡すのは StartEvent
                case "御三家を選んだ人がいる": return s.Players.Any(q => q.Party.Concat(q.Box).Any(m => m.MetHow == "最初のポケモン" && m.MetSpecies == value));
                default: return false;
            }
        }
    }
}
