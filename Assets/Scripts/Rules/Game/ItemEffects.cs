using System.Text.RegularExpressions;

namespace Sugoroku.Rules
{
    public enum ItemUse { None, Heal, Cure, Revive, FixedDice, DiceCount, PickDice, Weather, NoWild, Uncurse, Sleep, Curse, Steal, Trap, Searcher, Ticket, FanTicket, DartBike, EscapeRope, SweetScent, Disguise, Snorlax, PokeFlute, Pager }
    public enum TargetKind { None, Pokemon, Player, Square, Number } // UseItem.Target の意味（手持ちの番号・プレイヤー・マス・数）
    public enum Bag { Ball, Pokemon, Trainer }

    // アイテムを使ったときの効果。xlsx の「効果」は文章なので、使える効果はここに1つずつ書く
    // 新しいアイテムを使えるようにするときは、この switch に1行足す
    public static class ItemEffects
    {
        // (効果, 値, 使うとなくなるか)。Heal の値はHPの割合、FixedDice は出目、DiceCount はルーレットの数
        public static (ItemUse use, double value, bool consumed) Of(string name)
        {
            switch (name)
            {
                case "モーモーミルク": return (ItemUse.Heal, 1.0 / 3, true);
                case "キズくすり": return (ItemUse.Heal, 0.5, true);
                case "マンタンのくすり": return (ItemUse.Heal, 1, true);
                case "なんでもなおし": return (ItemUse.Cure, 0, true);
                case "どくけし": return (ItemUse.Cure, (int)Status.Poison, true);
                case "やけどなおし": return (ItemUse.Cure, (int)Status.Burn, true);
                case "げんきのかたまり": return (ItemUse.Revive, 0.5, true);
                case "自転車": return (ItemUse.DiceCount, 2, false);       // 自転車は使ってもなくならない（仮）
                case "マッハ自転車": return (ItemUse.DiceCount, 3, false);
                case "ロトム自転車": return (ItemUse.DiceCount, 4, false);
                case "ポケベル": return (ItemUse.Pager, 0, false);              // 呼ぶ人を選んだときに なくなる（やめたら残る）
                case "アマイ三ツ": return (ItemUse.Snorlax, 0, true);
                case "ポケモンのふえ": return (ItemUse.PokeFlute, 0, true);
                case "ダート自転車": return (ItemUse.DartBike, 0, true);          // 期間（調整値）のあいだ効いて、なくなる
                case "あなぬけひも": return (ItemUse.EscapeRope, 0, true);
                case "アマいおこう": return (ItemUse.SweetScent, 0, true);
                case "へんそうセット": return (ItemUse.Disguise, 0, true);
                case "マルチなくつ": return (ItemUse.PickDice, 6, true);          // 値は選べる最大
                case "グラードンのふえ": return (ItemUse.Weather, 1, true);       // 値は天候（1晴れ 2雨 3砂あらし）
                case "カイオーガのふえ": return (ItemUse.Weather, 2, true);
                case "レックウザのふえ": return (ItemUse.Weather, 3, true);
                case "ゴールドスプレー": return (ItemUse.NoWild, 0, true);
                case "おふだ": return (ItemUse.Uncurse, 0, true);
                case "スリーパーのこいん": return (ItemUse.Sleep, 0, true);       // ここから4つは妨害（同じエリアの相手だけ、8.3）
                case "のろいのふだ": return (ItemUse.Curse, 0, true);
                case "キャッチャ": return (ItemUse.Steal, 0, true);
                case "ワナ": return (ItemUse.Trap, 0, true);
                case "バトルサーチャー": return (ItemUse.Searcher, 0, true);
                case "アーマーガーチケット": return (ItemUse.Ticket, 0, true);
                case "スペシャルアーマーガーチケット": return (ItemUse.FanTicket, 0, true);
            }
            var shoes = Regex.Match(name, @"^(\d)のくつ$");
            if (shoes.Success) return (ItemUse.FixedDice, int.Parse(shoes.Groups[1].Value), true);
            return (ItemUse.None, 0, false);
        }

        // ポケモンに使う道具か（使う相手のポケモンを選ぶ）
        public static bool TargetsPokemon(ItemUse use) => use == ItemUse.Heal || use == ItemUse.Cure || use == ItemUse.Revive;

        public static TargetKind TargetOf(ItemUse use) =>
            TargetsPokemon(use) ? TargetKind.Pokemon
            : use == ItemUse.Sleep || use == ItemUse.Curse || use == ItemUse.Steal ? TargetKind.Player
            : use == ItemUse.Searcher || use == ItemUse.Ticket || use == ItemUse.FanTicket || use == ItemUse.PokeFlute ? TargetKind.Square
            : use == ItemUse.PickDice ? TargetKind.Number : TargetKind.None;

        public static string WeatherName(double value) => value == 1 ? "晴れ" : value == 2 ? "雨" : "砂あらし";

        // どの袋に入るか（仕様書8.1）
        public static Bag BagOf(Item item)
        {
            if (item.Category == "捕獲" && item.Name.EndsWith("ボール")) return Bag.Ball;
            if (item.IsHeldItem || item.Category == "回復" || item.Category == "進化アイテム") return Bag.Pokemon;
            return Bag.Trainer;
        }
    }
}
