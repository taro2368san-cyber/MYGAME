using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 起きたこと（GameEvent）を画面に出す文にする。文言シートにキーがあればそれを使い、なければここに書いた文
    // 言い回しを変えたいときは、xlsx の文言シートに同じキーで書く（同じキーを何行も書くとランダム）
    public sealed class Narrator
    {
        readonly GameData data;
        readonly BoardData board;
        readonly GameState state;
        readonly Random random = new Random();

        public Narrator(GameData data, BoardData board, GameState state) { this.data = data; this.board = board; this.state = state; }

        string Name(int player) => player >= 0 && player < state.Players.Count ? state.Players[player].Name : "相手";

        // key の文言（キーで始まるものを全部候補にする：bond_evade_01, _02…）。{名前} を値に置きかえる
        string T(string key, string fallback, params (string name, object value)[] values)
        {
            var list = data.Texts.Where(t => t.Key == key || t.Key.StartsWith(key + "_")).SelectMany(t => t.Value).Where(s => s != "").ToList();
            string text = list.Count > 0 ? list[random.Next(list.Count)] : fallback;
            foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value?.ToString());
            return text;
        }

        // 文言CSV（イベント文言・トレーナー文言）のセリフ。パターンはランダムに1つ、同じ順番に条件つきの行があれば満たすほうを使う
        string Lines(string key, int player, Dictionary<string, string> values)
        {
            var lines = DialogLines(key, player, values);
            return lines == null ? null : Join(lines);
        }

        // 話す人つきのセリフを1つの文に（「話す人「セリフ」」の行を改行でつなぐ）
        public static string Join(List<(string Speaker, string Text)> lines) => string.Join("\n", lines.Select(l => l.Speaker != "" ? $"{l.Speaker}「{l.Text}」" : l.Text));

        // セリフを行ごとに（話す人・文）。イベント画面で 話す人ごとに出すのに使う。キーがなければ null
        public List<(string Speaker, string Text)> DialogLines(Dialog d) => DialogLines(d.Key, d.Player, d.Values);

        List<(string Speaker, string Text)> DialogLines(string key, int player, Dictionary<string, string> values)
        {
            var all = data.Dialogs.Where(d => d.Key == key).ToList();
            if (all.Count == 0) return null;
            var p = player >= 0 && player < state.Players.Count ? state.Players[player] : null;
            var patterns = all.Select(d => d.Pattern).Distinct().ToList();
            int pattern = patterns[random.Next(patterns.Count)];
            var lines = all.Where(d => d.Pattern == pattern).GroupBy(d => d.Order).OrderBy(g => g.Key)
                .Select(g => g.FirstOrDefault(d => d.CondKind != "" && p != null && Conditions.Met(d.CondKind, d.CondValue, p, state)) ?? g.FirstOrDefault(d => d.CondKind == ""))
                .Where(d => d != null);
            string Fill(string s)
            {
                s = s.Replace("{トレーナー}", Name(player));
                if (values != null) foreach (var kv in values) s = s.Replace("{" + kv.Key + "}", kv.Value);
                return s;
            }
            return lines.Select(d => (Fill(d.Speaker), Fill(d.Text))).ToList();
        }

        // 文言シートのキーで1行（画面側から使う。エンドロールなど）
        public string Line(string key, string fallback, params (string name, object value)[] values) => T(key, fallback, values);

        // 画面に出す呼び名（UI仕様書 7.2：仕様書の 物理攻撃・特殊攻撃・必殺技・まもる・特殊守り は ひらがなの呼び名で出す）
        public static string AttackName(Attack a) => a == Attack.Physical ? "こうげき" : a == Attack.Special ? "とくこう" : a == Attack.Super ? "必殺" : "どうぐ";
        public static string DefenseName(Defense d) => d == Defense.Guard ? "ぼうぎょ" : d == Defense.SpecialGuard ? "とくぼう" : d == Defense.Counter ? "カウンター" : "にげる";

        // 表示しなくてよい出来事は null
        public string Describe(GameEvent e)
        {
            switch (e)
            {
                case TurnStarted x: return T("turn_start", "{トレーナー}の ばん！", ("トレーナー", Name(x.Player)));
                case TurnSkipped x: return x.Reason == "さいみん" ? $"{Name(x.Player)}は さいみん状態で ねむっている…" : T("turn_rest", "{トレーナー}は 1回休み…", ("トレーナー", Name(x.Player)));
                case AilmentAdded x: return x.Name == "ゴールドスプレー" ? $"{x.Weeks}週の間、野生のポケモンが よってこない"
                    : x.Name == "ダート自転車" ? $"{x.Weeks}週の間、ルーレットが 2〜4個に なる"
                    : x.Name == "アマいおこう" ? $"{x.Weeks}週の間、野生のポケモンが 少し 強くなって 出てくる"
                    : x.Name == "へんそう" ? $"{Name(x.Player)}は へんそうした！ この週は ほかの人の ライバルマスを 自分のもののように 使える" : $"{Name(x.Player)}は {x.Name}状態に なった！（{x.Weeks}週）" + (x.Name == "さいみん" ? "　次の番は休み" : x.Name == "のろい" ? "　ポケモンセンターに入れない" : "");
                case AilmentCured x: return $"{Name(x.Player)}の {x.Name}が とけた！";
                case ItemBlocked x: return $"{Name(x.Player)}の {x.Item}が {x.Attack}を ふせいだ！";
                case Fished x: return $"{Name(x.Player)}は つりざおを たらした…";
                case SnorlaxPlaced x: return $"{Name(x.Player)}は アマイ三ツで カビゴンを よびよせた！ {board[x.Square].Place}の このマスは {x.Weeks}週 通れない";
                case SymbolGone x: return $"{x.Pokemon}は {Name(x.Player)}が {(x.Caught ? "捕まえた" : "倒した")}ので、{board[x.Square].Place}から いなくなった";
                case SnorlaxRemoved x: return (x.Player >= 0 ? $"{Name(x.Player)}は ポケモンのふえを ふいた！ " : "") + $"{board[x.Square].Place}の カビゴンが 目をさまして 去っていった";
                case TrapSet x: return $"{Name(x.Player)}は {board[x.Square].Name}に ワナを しかけた";
                case TrapSprung x: return $"{Name(x.Player)}は {Name(x.Owner)}の ワナを ふんだ！ 手持ちの ポケモンが ダメージを うけた";
                case FluteStarted x: return $"{x.Weeks}週の間、バトルの天候が {x.Weather}に なる！";
                case ItemUsed x: return $"{Name(x.Player)}は {x.Item}を つかった" + (x.Pokemon != null ? $"（{x.Pokemon}）" : "");
                case DiceRolled x: return x.Dice.Count > 1 ? $"サイコロ {string.Join("＋", x.Dice)} ＝ {x.Value}" : $"サイコロの目は {x.Value}" + (x.AllowReverse ? "（止まれるマスがないので、来た道も数える）" : "");
                case Warped x: return $"ワープ！ {board[x.To].Place}へ";
                case Landed x: return $"{board[x.Square].Place}の {board[x.Square].Type}に 止まった";
                case NothingHappened x: return x.Reason;
                case RandomEventHappened x: return string.IsNullOrEmpty(x.Text) ? x.Name : x.Text.Replace("{トレーナー}", Name(x.Player));
                case EventStarted _: return null; // セリフ（Dialog）で見せる
                case TurnOrderDecided x: return "手番の順番：" + string.Join(" → ", x.Order.Select((id, i) => $"{i + 1}.{Name(id)}"));
                case EventRuleDecided x: return $"今回のイベントルール：{x.Name}（{x.Description}）";
                case JobChanged x: return $"{Name(x.Player)}は {x.Job}に なった！";
                case PokemonReleased x: return $"{x.Pokemon}を 逃がした…";
                case DebugDone x: return "［デバッグ］" + x.Text;
                case PokemonTraded x: return $"{Name(x.Winner)}は {Name(x.Loser)}の {x.Taken}と、自分の {x.Given}を 交換した！";
                case Dialog x: return Lines(x.Key, x.Player, x.Values);
                case OptionsRequested _: return null; // 画面のボタンで見せる
                case JobUnlocked x: return Lines("ev_reward_job", x.Player, new Dictionary<string, string> { ["職業"] = x.Job }) ?? $"職業「{x.Job}」に なれるように なった！";
                case PokemonReceived x: return x.Refused ? $"{x.Pokemon}を もらえるはずだったが、手持ちも 控えも いっぱいだった…" : (Lines("ev_reward_pokemon", x.Player, new Dictionary<string, string> { ["報酬"] = x.Pokemon }) ?? $"{Name(x.Player)}は {x.Pokemon}を うけとった！") + (x.ToBox ? "（控えに おくった）" : "");
                case BallTierUsed x: return x.Prize ? "カギで 開けた！ 当たりだ！" : x.Keyed ? $"カギで 開けた！ 1つ上の表（{x.Tier}）から 出る" : x.Rescue ? $"最下位が つづいているので、1つ上の表（{x.Tier}）から 出る" : null;
                case TournamentStarted x: return $"突発バトルイベント！ ミニトーナメントが はじまった（{string.Join("・", x.Players.Select(Name))}。優勝すると ポケベル）";
                case TournamentWon x: return $"{Name(x.Player)}が トーナメントで 優勝！ {x.Item}を もらった";
                case NewsLegendAppeared x: return $"ポケニュースで うわさの {x.Pokemon}が あらわれた！";
                case JobChallenge x: return $"{x.Job}の トレーナーが しょうぶを しかけてきた！（勝つと {x.Job}に なれる）";
                case RocketDefeated x: return $"{Name(x.Player)}は ロケット団を 追いはらった！ {board[x.Square].Place}の 拠点が 自分のものに" + (x.Trainers.Count > 0 ? $"　さらに {string.Join("・", x.Trainers.Select(t => board[t].Name))}の トレーナーも ファンに なった！" : "");
                case PokemonGiven x: return $"{Name(x.Player)}は {x.Pokemon}を {x.To}に わたした";
                case SailedToIsland x: return $"{Name(x.Player)}は 船で {x.Island}へ わたった！" + (x.Legend != "" ? $" どこかに {x.Legend}の けはいが する…" : "");
                case ReturnedFromIsland x: return $"{Name(x.Player)}は {x.Reason}（クチバシティへ）";
                case TicketChanged x: return $"{Name(x.Player)}の 謎のチケットが かがやきだした…！ 不思議なチケットに なった";
                case RescueGiven x: return $"{Name(x.Player)}に {x.Item}が とどいた（最下位が つづいたので）";
                case BoxExpanded x: return $"{Name(x.Player)}は 控えに {x.Max}体まで あずけられるように なった！";
                case ItemGiven x: return $"{Name(x.Player)}は {x.To}に {x.Item}を わたした";
                case SwitchedOut x: return $"{x.Pokemon}は 手持ちに もどった！";
                case ItemDiscarded x: return x.NotTaken ? $"{x.Item}は 拾わなかった" : $"{x.Item}を すてた";
                case ExtraMoveStarted x: return $"もう {x.Steps}マス 進む";
                case MoneyChanged x: return x.Amount > 0 ? $"{Name(x.Player)}は {x.Amount}円 もらった（{x.Reason}）" : $"{Name(x.Player)}は {-x.Amount}円 うしなった（{x.Reason}）" + (x.To >= 0 ? $" → {Name(x.To)}へ" : "");
                case PointsChanged x: return $"{Name(x.Player)}は {x.Amount}ポイント もらった";
                case ItemGained x: return x.BagFull ? $"{x.Item}を みつけたが、袋が いっぱいだ" : $"{Name(x.Player)}は {x.Item}を てにいれた！" + (x.Item == GameProcessor.SurfItem ? "\nなみのりを 覚えた！ 海上マスに 入れるようになった" : "");
                case ItemLost x: return x.To >= 0 ? $"{Name(x.To)}は {Name(x.Player)}から {x.Item}を うばった！" : null;
                case PartyHealed x: return $"{Name(x.Player)}の ポケモンは 元気に なった";
                case FacilityOpened x:
                    return x.Kind == FacilityKind.Shop ? "ショップに 入った" : x.Kind == FacilityKind.Center ? "ポケモンセンター" : x.Kind == FacilityKind.FanHeal ? $"{Name(x.Owner)}の ファンの家。{x.Price}円で 回復できる" : null;
                case Deposited x: return x.In != null && x.Out != null ? $"{x.Out}を あずけて {x.In}を 手持ちに" : x.In != null ? $"{x.In}を 手持ちに 入れた" : $"{x.Out}を あずけた";
                case OwnerChanged x: return T("notice_owner", "{トレーナー}に あたらしい ファンが できた！", ("トレーナー", Name(x.Owner)));
                case Dividend x: return $"{Name(x.Player)}：" + T("week_dividend", "配当の日！ ファンから {金額}円が とどいた", ("金額", x.Amount));
                case WeekAdvanced x: return T("week_start", "{週}週目が はじまった", ("週", x.Week));
                case WeekEventHappened x:
                    string key = x.Name == "ストライキ" ? "event_strike" : x.Name == "セール" ? "event_sale" : x.Name == "雨季" ? "event_rain" : x.Name == "乾季" ? "event_sun" : "event_sand";
                    return T(key, $"{x.Name}（{x.Weeks}週間）");
                case WeekEventEnded x: return $"{x.Name}が おわった";
                case SentToCenter x: return T("notice_return", "{トレーナー}の 手持ちが ぜんめつ… ポケモンセンターへ もどされた", ("トレーナー", Name(x.Player)));
                case GameEnded x: return "ゲーム終了！ " + string.Join("　", x.Ranking.Select((r, i) => $"{i + 1}位 {Name(r.player)}（{r.points:0.#}p）"));

                case BattleSuspended x: return $"{Name(x.Player)}と {x.Opponent}の バトルは、つぎの番に つづく";
                case BattleResumed x: return $"{Name(x.Player)}は {x.Opponent}との バトルの つづき！";
                case BattleStarted x: return x.Kind == BattleKind.Wild ? $"{x.Opponent}が とびだしてきた！" : x.Kind == BattleKind.Event ? null : $"{x.Opponent}との バトル！" + (x.Full ? "（フルバトル）" : "");
                case SelectRequested x: return $"{Name(x.Player)}は 出すポケモンを えらんでください";
                case SentOut x: return $"{x.Pokemon} Lv{x.Level}（{x.Type}・弱点{x.Weakness}）";
                case InitiativeDrawn x: return $"カード{x.Cards.Count}枚から 1枚… {x.FirstPokemon}の 先攻！";
                case RoundResolved x: return Round(x);
                case SandstormDamage x: return $"すなあらしが {x.Pokemon}を おそう！（{x.Damage}）";
                case HeldItemUsed x: return $"【{x.Pokemon}の {x.Item}】" + (x.Consumed ? $" {x.Item}を つかいきった" : "");
                case HeldItemStolen x: return $"{x.Pokemon}は {x.From}の {x.Item}を うばった！（バトルのあとで返す）";
                case HeldItemDamage x: return $"【{x.Owner}の {x.Item}】 {x.Pokemon}は {x.Damage}の ダメージを うけた";
                case HeldItemChanged x: return x.Item == "" ? $"{x.Pokemon}の {x.Old}を はずした" : $"{x.Pokemon}に {x.Item}を もたせた" + (x.Old != "" ? $"（{x.Old}は 袋へ）" : "");
                case AbilityTriggered x: return $"【{x.Pokemon}の {x.Ability}】";
                case BattleWeatherChanged x: return x.Ability == null ? $"バトルの天候は {x.Weather}" : $"【{x.Pokemon}の {x.Ability}】 天候が {x.Weather}に なった！";
                case AbilityHealed x: return $"【{x.Pokemon}の {x.Ability}】 HPが {x.Amount} 回復した";
                case StatusCured x: return $"【{x.Pokemon}の {x.Ability}】 " + (x.All ? "手持ちの 状態異常が なおった！" : "状態異常が なおった！");
                case Transformed x: return $"{x.Pokemon}は {x.Into}に へんしんした！";
                case StatusDamage x: return $"{x.Pokemon}は {Pokemons.StatusName(x.Status)}の ダメージを うけた！（{x.Damage}）";
                case StatusInflicted x: return x.Status == Status.Burn ? $"{x.Pokemon}は やけどを おった！" : x.Status == Status.Poison ? $"{x.Pokemon}は どくを あびた！" : x.Status == Status.Paralysis ? $"{x.Pokemon}は まひして 技が でにくくなった！" : $"{x.Pokemon}は ねむってしまった！";
                case CantMove x: return x.Status == Status.Paralysis ? $"{x.Pokemon}は からだが しびれて うごけない！" : $"{x.Pokemon}は ぐうぐう ねむっている。" + (x.Woke ? $"…{x.Pokemon}は 目を さました！" : "");
                case RankChanged x: return $"{x.Pokemon}の {x.Stat}が " + (x.Delta >= 2 ? "ぐーんと 上がった！" : x.Delta > 0 ? "上がった！" : x.Delta <= -2 ? "がくっと 下がった！" : "下がった！");
                case PokemonFainted x: return $"{x.Pokemon}は たおれた！";
                case BallThrown x: return $"{x.Ball}を なげた！ " + (x.Caught ? "" : "…だめだ、出てきてしまった");
                case Caught x: return T("notice_catch", "{トレーナー}は {ポケモン}を つかまえた！", ("トレーナー", Name(x.Player)), ("ポケモン", x.Pokemon)) + (x.ToBox ? "（控えに おくった）" : "");
                case Fled x: return x.Surrender ? "降参した…" : "うまく にげきれた";
                case BattleEnded x: return x.Draw ? "ひきわけ！" + (x.Judged ? "（判定）" : "") : x.Winner < 0 ? null : $"{x.WinnerName}の 勝ち！" + (x.Judged ? "（判定）" : "");
                case ExpGained x: return $"{x.Pokemon}は {x.Exp}の けいけんちを もらった" + (x.Levels > 0 ? $"　Lv{x.Level}に あがった！" : "");
                case LevelCaughtUp x: return $"{x.Pokemon}は 格上を たおして Lv{x.Level}に！";
                case Evolved x: return T("notice_evolve_2", "{ポケモン}は {進化後}に しんかした！", ("ポケモン", x.From), ("進化後", x.To));
                case BadgeEarned x: return T("notice_badge", "{トレーナー}は {バッジ}を てにいれた！", ("トレーナー", Name(x.Player)), ("バッジ", x.Gym + "のバッジ"));
                case RestNextTurn x: return $"{Name(x.Player)}は 次の手番 1回休み";
                case RewardRequested x: return $"{Name(x.Winner)}は ほうびを えらんでください";
            }
            return null;
        }

        string Round(RoundResolved x)
        {
            string a = x.AttackerPokemon, d = x.DefenderPokemon;
            if (x.Item != null) return $"{a}に {x.Item}を つかった（HP {x.AttackerHp}）";
            string head = $"{a}の {AttackName(x.Attack)}！ {d}は {DefenseName(x.Defense)}";
            string abilities = x.Abilities.Count > 0 ? "\n【" + string.Join("・", x.Abilities) + "】" : "";
            if (x.Absorbed) return head + abilities + $" {d}は 技を すいとった！";
            if (x.Missed)
            {
                if (!x.Bond) return head + "… はずれた" + abilities;
                var trainer = x.DefenderTrainer;
                return head + "\n" + T("bond_evade", "{ポケモン}は {トレーナー}の ために 技を よけた！", ("ポケモン", d), ("トレーナー", trainer));
            }
            var parts = new List<string> { head + abilities };
            if (x.Reflected) parts.Add($"カウンター成功！ {x.Damage}の ダメージを はねかえした（{a} HP {x.AttackerHp}）");
            else
            {
                if (x.Guarded) parts.Add("ガード成功！");
                if (x.Critical) parts.Add("急所に あたった！");
                if (x.Weakness) parts.Add("弱点を ついた！");
                parts.Add($"{x.Damage}の ダメージ（{d} HP {x.DefenderHp}）");
            }
            return string.Join("\n", parts);
        }
    }
}
