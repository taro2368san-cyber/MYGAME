using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sugoroku.Rules
{
    public enum EvolutionKind { Level, Item, LeadTurns, Rival }

    // 進化の条件1つ分（分かれ道のある種族は複数持つ）
    public sealed class Evolution
    {
        public EvolutionKind Kind;
        public int Value;      // Level: レベル / LeadTurns: 先頭にしたターン数
        public string Item;    // Item: 必要なアイテム
        public string Target;  // 進化先
    }

    public sealed class Species
    {
        public int Number;
        public string Name, Type, Weakness, Ability;
        public string PhysicalMove, PhysicalMoveEffect, SpecialMove, SpecialMoveEffect, SuperMove;
        public MoveEffect PhysicalEffect, SpecialEffect; // 効果の文を読んだもの。読めない・ないときは null
        public AbilityInfo AbilityData;                  // 特性を読んだもの。ないときは null
        public int Hp, Attack, Defense, SpAttack, SpDefense, Speed, Total;
        public List<Evolution> Evolutions = new List<Evolution>();
    }

    // 特性（仕様書7.12）。何が起きるかは名前（「：」の前）で決まる（GameProcessor.Ability.cs）
    // 数字は文から読むので、xlsx の文の「20%」「1.5倍」「1/3」を書き換えれば変わる
    public sealed class AbilityInfo
    {
        public string Name;
        public double Rate, Mult, Frac; // 文の中の「○%」「○倍」「1/○」。なければ 0

        public static readonly HashSet<string> Built = new HashSet<string> {
            "しんりょく", "もうか", "げきりゅう", "むしのしらせ", "りんぷん", "はりきり", "いかく", "せいでんき", "すなかき", "ひでり", "ようりょくそ",
            "いやしのこころ", "すなのちから", "すいすい", "アメフラシ", "アメのうけざら", "すながくれ", "ほのおのからだ", "がんじょう", "はやおき",
            "うるおいボディ", "のろわれボディ", "カブトアーマー", "かがくへんかガス", "ひらいしん", "リーフガード", "でんきエンジン", "じしんかじょう",
            "へんしん", "アナライズ", "くだけるよろい", "すなおこし", "シンクロ", "ふゆう", "ばけのかわ", "わるいてぐせ", "かるわざ" };
        // ponytail: 自分からの交代ができるまで何も起きない
        public static readonly HashSet<string> Waiting = new HashSet<string> { "さいせいりょく" };

        public static AbilityInfo Parse(string text)
        {
            double Num(string pattern) { var m = Regex.Match(text, pattern); return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0; }
            double frac = Num(@"1/(\d+)");
            return new AbilityInfo { Name = text.Split('：', ':')[0].Trim(), Rate = Num(@"(\d+)[%％]") / 100, Mult = Num(@"([\d.]+)倍"), Frac = frac > 0 ? 1 / frac : 0 };
        }
    }

    // 技の追加効果（仕様書7.16）。ガードに失敗したときに Chance の確率で起きる
    // 読める文：「20%で相手をどく」「10%でマヒ」「20%で相手のとくこうを1段階下げる」「10%で自分のこうげき・とくこうを1段階上げる」「急所に当たりやすい（15%）」
    public sealed class MoveEffect
    {
        public double Chance;
        public Status Status;              // 相手を状態異常にする
        public bool Self;                  // ランクを変えるのが自分か
        public List<int> Stats = new List<int>(); // ランクを変える能力（0こうげき 1とくこう 2すばやさ）
        public int Stages;                 // ＋上げる －下げる
        public double CritRate;            // 0 でなければ、この攻撃の急所の確率
        public double LateMult;            // 0 でなければ、後攻のときダメージがこの倍率（ベルベルト）

        public static readonly string[] StatNames = { "こうげき", "とくこう", "すばやさ" };

        public static MoveEffect Parse(string text)
        {
            text = text.Replace("素早さ", "すばやさ").Replace("マヒ", "まひ").Replace("％", "%").Trim();
            var m = Regex.Match(text, @"^急所に当たりやすい[（(](\d+)%[）)]$");
            if (m.Success) return new MoveEffect { Chance = 1, CritRate = int.Parse(m.Groups[1].Value) / 100.0 };
            m = Regex.Match(text, @"^後攻であれば攻撃力([\d.]+)倍$");
            if (m.Success) return new MoveEffect { Chance = 1, LateMult = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) };
            // メタモン：へんしん中は相手の種族の効果を使う（GameProcessor.BattleSpecies）。ここでは何もしない
            if (Regex.IsMatch(text, @"^相手の(こうげき|とくこう)の効果$")) return new MoveEffect();
            m = Regex.Match(text, @"^(\d+)%で?(?:相手を)?(どく|まひ|やけど|ねむり)(?:にする)?$");
            if (m.Success)
                return new MoveEffect { Chance = int.Parse(m.Groups[1].Value) / 100.0, Status = m.Groups[2].Value == "どく" ? Status.Poison : m.Groups[2].Value == "まひ" ? Status.Paralysis : m.Groups[2].Value == "やけど" ? Status.Burn : Status.Sleep };
            m = Regex.Match(text, @"^(\d+)%で?(相手|自分)の(.+)を([12])段階(上げる|下げる)$");
            if (!m.Success) return null;
            var stats = m.Groups[3].Value.Split('・').Select(s => Array.IndexOf(StatNames, s)).ToList();
            if (stats.Any(i => i < 0)) return null;
            return new MoveEffect { Chance = int.Parse(m.Groups[1].Value) / 100.0, Self = m.Groups[2].Value == "自分", Stats = stats, Stages = int.Parse(m.Groups[4].Value) * (m.Groups[5].Value == "上げる" ? 1 : -1) };
        }
    }

    public sealed class Item
    {
        public string Category, Name, Effect, Note;
        public int? Price;
        public bool Sellable, ShopBuyable, IsHeldItem; // IsHeldItem: ポケモンに持たせる道具（ポケモンの道具シート）
    }

    public sealed class Place { public string Area, Unlock, Name, Kind, Gym, Note; }
    public sealed class GymMember { public string Gym, Leader, Type, BadgeOrder, Pokemon, Role, Condition; }
    public sealed class Encounter { public string Place, Area, Method, Pokemon, Note; public int? Rate; public int MinLevel, MaxLevel; }
    public sealed class Job { public string Category, Name, Effect, Note, Candidates; }
    public sealed class WeekEvent { public string Name, Effect; public double Probability; }

    // ---- ここから下は 設計メモ のCSV（xlsx に同じ名前のシートを作ればそちらが使われる。書き方は 設計メモ/CSVの書き方.md）----

    // マスやイベントの効果の型（仕様書13.2）。「効果の型」列の言葉で見分ける
    public enum EffectKind { None, Money, Item, ExtraMove, CureStatus, HealAll, Points, Battle, UnlockJob, Museum, Errand, BoxUp, Trade, DayCare } // Museum: 化石を預ける博物館（3.2.2）。Errand 以降は町の人のイベントマス（GameProcessor.Town.cs）

    // ランダムイベント・イベントマスの1件
    public sealed class Effect
    {
        public string Id, Name, Value, Reward, CondKind, CondValue;
        public string TextKey;      // イベント文言.csv のキー（_yes/_no/_win/_lose/_ok/_notyet が後ろに付く）
        public string Text;         // 文言キーがないときの、その場で書いた文（イベントマス.csv）
        public EffectKind Kind;
        public double Rate;         // 発生率(%)。0 なら草むら・空マスでは起きない（ポケベルで呼ぶだけ）
        public bool Bell, HasChoice;
    }

    public sealed class Shop { public string Id, Name, Place, Base; }                   // Base: 品ぞろえを受け継ぐショップの id
    public sealed class ShopItem { public string ShopId, Item, CondKind, CondValue; }
    public sealed class BallTier { public string Id, Name; public int MinBadges, MaxBadges; } // ボールマスの段階（バッジの数で決まる）
    public sealed class BallSquareItem { public string Item, Bag; public int Count; public readonly Dictionary<string, int> Weights = new Dictionary<string, int>(); } // Weights: 段階id → 重み
    public sealed class EventSquare { public string Square, Content; public Effect Effect; }
    // 島マップ.csv の1行（3.4、チケットの設定.md）。Kind：地方の島／伝説の島
    public sealed class IslandMap { public string MapId, Name, Kind; }
    // チケットの行き先.csv の1行。Kind：島（Target は島の名前）／伝説（Target はポケモン、Level はそのLv）。Island は渡る島マップの名前
    public sealed class TicketRow { public string Id, Ticket, Kind, Target, Island; public int Weight, Level; }
    // ロケット団拠点.csv の1行（11.3）。Mode: 買う／もらう／交換、Kind: アイテム／ポケモン。Once: 1人1回
    public sealed class RocketBaseRow { public string Id, BaseId, BaseName, Place, Mode, Kind, Item, Note; public int? Price, OwnerPrice, Level; public double? Cost, OwnerCost; public bool Once; } // Level: もらう・買うポケモンのLv
    public sealed class TrainerEntry
    {
        public string Id, Square, Place, Kind, Class, Name, Pokemon;
        public int? Level;
        public bool IsGymTrainer => Kind == "ジムトレーナー";
    }
    public sealed class TrainerClass { public string Class, TextId; }
    public sealed class EventParty { public string Id, Name, Title; public bool Full; public readonly List<(string species, int level)> Members = new List<(string, int)>(); }
    public sealed class DialogLine { public string Key, Speaker, Text, CondKind, CondValue; public int Pattern, Order; }
    // ライバル.csv の1行（11.5）：場所のライバルマスで戦う手持ちと、持ち主になったあとに交換できる他地方のポケモン
    public sealed class RivalEntry { public string Id, Name, Game, Place; public int Order, TradeLevel; public List<string> Trades = new List<string>(); }
    // ライバル_手持ち.csv の1行：条件（御三家を選んだ人がいる など）に合う手持ちを、優先の小さい順に使う
    public sealed class RivalParty { public string Id, RivalId, CondKind, CondValue; public int Priority; public List<(string species, int level)> Party = new List<(string, int)>(); }
    public sealed class Character { public string Id, Name, Color; public int Generation; public List<string> Starters = new List<string>(); } // 仕様書1.1。Color はコマの色（#RRGGBB）
    // イベントルール（2.2、13.3）。方式：「数に応じて」＝数×係数、「1位だけ」＝いちばん多い人に係数（同数は全員）
    public sealed class EventRule { public string Id, Name, Description, Target, Mode; public double Coef; }
    // 職業の補正（9.1、9.5）。Type が空なら手持ち全員、あればそのタイプのポケモンだけ。値は倍率（空欄は1）
    // 職業マス.csv の1行（9.2）：どこに出るか、挑める条件、相手のタイプ
    public sealed class JobSquareRule { public string Job, Type, FoeType; public List<string> Places = new List<string>(); public int Badges, TypeLevels, Dex; }
    public sealed class JobBonus
    {
        public string Job, Type;
        public double Attack = 1, Defense = 1, SpAttack = 1, SpDefense = 1, Hp = 1, Super = 1;
        public double Capture;                 // ボールの閾値に足す（だいすきくらぶ）
        public int Bag, ExtraSteps;            // 袋に足す数（ロケット団）、サイコロに足せる歩数（サイクリング）
        public bool NoCenterHeal, FreeTrade;   // センターで回復できない、交換でコストを見ない（ロケット団）
    }

    // xlsx から書き出した CSV（StreamingAssets/GameData）を読み込んだもの。値は読み込み後に変えない
    public sealed class GameData
    {
        public static readonly string[] Types = { "草", "炎", "水", "雷", "超", "フェアリー", "闘", "悪", "鋼", "ドラゴン", "ノーマル" };

        public readonly List<Species> Species = new List<Species>();
        public readonly List<Item> Items = new List<Item>();
        public readonly List<Place> Places = new List<Place>();
        public readonly List<GymMember> GymMembers = new List<GymMember>();
        public readonly List<Encounter> Encounters = new List<Encounter>();
        public readonly List<Job> Jobs = new List<Job>();
        public readonly List<WeekEvent> WeekEvents = new List<WeekEvent>();
        public readonly Dictionary<string, double> Tuning = new Dictionary<string, double>();
        public readonly Dictionary<string, List<string>> Texts = new Dictionary<string, List<string>>();
        // 設計メモ のCSV
        public readonly List<Shop> Shops = new List<Shop>();
        public readonly List<ShopItem> ShopItems = new List<ShopItem>();
        public readonly List<BallTier> BallTiers = new List<BallTier>();
        public readonly List<BallSquareItem> BallSquareItems = new List<BallSquareItem>();
        public readonly List<BallSquareItem> KeyPrizeItems = new List<BallSquareItem>();
        public readonly List<RocketBaseRow> RocketBases = new List<RocketBaseRow>();   // 鍵付きボールマスの当たり枠（段階は開けた人のバッジ）
        public readonly List<Effect> RandomEvents = new List<Effect>();
        public readonly List<EventSquare> EventSquares = new List<EventSquare>();
        public readonly List<IslandMap> IslandMaps = new List<IslandMap>();
        public readonly List<TicketRow> TicketRows = new List<TicketRow>();
        public bool IsIslandMap(string mapId) => !string.IsNullOrEmpty(mapId) && IslandMaps.Any(i => i.MapId == mapId);
        public readonly List<TrainerEntry> Trainers = new List<TrainerEntry>();
        public readonly List<TrainerClass> TrainerClasses = new List<TrainerClass>();
        public readonly List<EventParty> EventParties = new List<EventParty>();
        public readonly List<DialogLine> Dialogs = new List<DialogLine>();
        public readonly List<RivalEntry> Rivals = new List<RivalEntry>();
        public readonly List<JobSquareRule> JobSquares = new List<JobSquareRule>();
        public readonly List<RivalParty> RivalParties = new List<RivalParty>();
        public readonly List<Character> Characters = new List<Character>();
        public readonly List<EventRule> EventRules = new List<EventRule>();
        public readonly List<JobBonus> JobBonuses = new List<JobBonus>();

        public Species FindSpecies(string name) => Species.FirstOrDefault(s => s.Name == name);
        public Item FindItem(string name) => Items.FirstOrDefault(i => i.Name == name);
        public Shop FindShop(string id) => Shops.FirstOrDefault(s => s.Id == id);
        public EventParty FindParty(string id) => EventParties.FirstOrDefault(p => p.Id == id);
        public bool HasDialog(string key) => Dialogs.Any(d => d.Key == key);

        // ショップの品（受け継ぎ元の品 → 自分の品の順）。cond で条件つきの品をふるいにかける
        public List<string> ShopGoods(string shopId, Func<ShopItem, bool> cond)
        {
            var chain = new List<string>();
            for (var s = FindShop(shopId); s != null && chain.Count < 10 && !chain.Contains(s.Id); s = FindShop(s.Base)) chain.Insert(0, s.Id);
            return chain.SelectMany(id => ShopItems.Where(x => x.ShopId == id && cond(x)).Select(x => x.Item)).Distinct().ToList();
        }

        // バッジの数に合う段階。next なら1つ上の段階（最上位ならそのまま）
        public BallTier TierFor(int badges, bool next = false)
        {
            var tiers = BallTiers.OrderBy(t => t.MinBadges).ToList();
            int i = tiers.FindIndex(t => badges >= t.MinBadges && badges <= t.MaxBadges);
            if (i < 0) i = tiers.FindLastIndex(t => badges >= t.MinBadges);
            if (i < 0) return tiers.FirstOrDefault();
            return tiers[Math.Min(tiers.Count - 1, next ? i + 1 : i)];
        }

        // 調整値をキーで引く。無ければ、何を足せばよいか分かるメッセージで止める
        public double Required(string key) =>
            Tuning.TryGetValue(key, out double v) ? v : throw new KeyNotFoundException($"調整値シートにキー「{key}」がありません。xlsx の調整値シートに追加してください。");

        // dir の CSV をすべて読み込み、気になる点を problems に入れる（[エラー] は遊べない問題、[警告] は直したほうがよい点）
        public static GameData Load(string dir, List<string> problems)
        {
            var data = new GameData();
            CsvTable Sheet(string name)
            {
                string path = Path.Combine(dir, name + ".csv");
                if (!File.Exists(path)) { problems.Add($"[エラー] {name}.csv がありません"); return new CsvTable(name, "", new List<string>()); }
                return new CsvTable(name, File.ReadAllText(path), problems);
            }

            var pokemon = Sheet("ポケモン");
            foreach (var r in pokemon.Rows)
            {
                var s = new Species
                {
                    Number = Int(pokemon, r, "番号", problems) ?? 0,
                    Name = pokemon.Get(r, "名前"), Type = pokemon.Get(r, "タイプ"), Weakness = pokemon.Get(r, "弱点"), Ability = pokemon.Get(r, "特性"),
                    PhysicalMove = pokemon.Get(r, "こうげき技"), PhysicalMoveEffect = pokemon.Get(r, "こうげき技の効果"),
                    SpecialMove = pokemon.Get(r, "とくこう技"), SpecialMoveEffect = pokemon.Get(r, "とくこう技の効果"), SuperMove = pokemon.Get(r, "必殺技"),
                    Hp = Int(pokemon, r, "HP", problems) ?? 0, Attack = Int(pokemon, r, "こうげき", problems) ?? 0, Defense = Int(pokemon, r, "ぼうぎょ", problems) ?? 0,
                    SpAttack = Int(pokemon, r, "とくこう", problems) ?? 0, SpDefense = Int(pokemon, r, "とくぼう", problems) ?? 0, Speed = Int(pokemon, r, "すばやさ", problems) ?? 0,
                    Total = Int(pokemon, r, "合計", problems) ?? 0,
                };
                MoveEffect Effect(string text)
                {
                    if (text == "" || text == "-") return null;
                    var e = MoveEffect.Parse(text);
                    if (e == null) problems.Add($"[警告] ポケモン「{s.Name}」: 技の効果「{text}」はまだ読めません（何も起きません）");
                    return e;
                }
                s.PhysicalEffect = Effect(s.PhysicalMoveEffect); s.SpecialEffect = Effect(s.SpecialMoveEffect);
                if (s.Ability != "" && s.Ability != "-")
                {
                    s.AbilityData = AbilityInfo.Parse(s.Ability);
                    if (!AbilityInfo.Built.Contains(s.AbilityData.Name) && !AbilityInfo.Waiting.Contains(s.AbilityData.Name))
                        problems.Add($"[警告] ポケモン「{s.Name}」: 特性「{s.AbilityData.Name}」はまだ作っていません（何も起きません）");
                }
                data.Species.Add(s);
            }

            void AddItems(CsvTable t, bool held)
            {
                foreach (var r in t.Rows)
                    data.Items.Add(new Item
                    {
                        Category = held ? "もちもの" : t.Get(r, "分類"), Name = t.Get(r, "名前"), Effect = t.Get(r, "効果"), Note = t.Get(r, "備考"),
                        Price = Int(t, r, "価格", problems), Sellable = t.Get(r, "売れる") == "○",
                        ShopBuyable = held ? t.Get(r, "価格") != "" : t.Get(r, "店で買える") == "○", IsHeldItem = held,
                    });
            }
            AddItems(Sheet("アイテム一覧"), false);
            AddItems(Sheet("ポケモンの道具"), true);

            // 進化条件は、アイテム名が分かってから読む
            for (int i = 0; i < data.Species.Count; i++)
                ParseEvolution(data, data.Species[i], pokemon.Get(pokemon.Rows[i], "進化条件"), i + 1 < data.Species.Count ? data.Species[i + 1] : null, problems);

            var area = Sheet("エリア");
            foreach (var r in area.Rows)
                data.Places.Add(new Place { Area = area.Get(r, "エリア"), Unlock = area.Get(r, "解放条件"), Name = area.Get(r, "場所"), Kind = area.Get(r, "種別"), Gym = area.Get(r, "ジム"), Note = area.Get(r, "備考") });

            var gym = Sheet("ジム");
            foreach (var r in gym.Rows)
                data.GymMembers.Add(new GymMember { Gym = gym.Get(r, "ジム"), Leader = gym.Get(r, "リーダー"), Type = gym.Get(r, "タイプ"), BadgeOrder = gym.Get(r, "何個目のバッジになるか"), Pokemon = gym.Get(r, "ポケモン"), Role = gym.Get(r, "役割"), Condition = gym.Get(r, "出す条件") });

            var enc = Sheet("出現");
            foreach (var r in enc.Rows)
                data.Encounters.Add(new Encounter { Place = enc.Get(r, "場所"), Area = enc.Get(r, "エリア"), Method = enc.Get(r, "出かた"), Pokemon = enc.Get(r, "ポケモン"), Note = enc.Get(r, "備考"), Rate = Int(enc, r, "出現率", problems), MinLevel = Int(enc, r, "最低Lv", problems) ?? 0, MaxLevel = Int(enc, r, "最高Lv", problems) ?? 0 });

            var job = Sheet("職");
            foreach (var r in job.Rows)
                data.Jobs.Add(new Job { Category = job.Get(r, "分類"), Name = job.Get(r, "名前"), Effect = job.Get(r, "効果"), Note = job.Get(r, "備考"), Candidates = job.Get(r, "配置候補") });

            var week = Sheet("週イベント");
            foreach (var r in week.Rows)
                data.WeekEvents.Add(new WeekEvent { Name = week.Get(r, "名前"), Effect = week.Get(r, "効果"), Probability = Num(week, r, "確率", problems) ?? 0 });

            var tuning = Sheet("調整値");
            foreach (var r in tuning.Rows)
            {
                string key = tuning.Get(r, "キー");
                double? v = Num(tuning, r, "値", problems);
                if (key == "" || v == null) continue;
                if (data.Tuning.ContainsKey(key)) problems.Add($"[エラー] 調整値: キー「{key}」が2回出てきます");
                else data.Tuning[key] = v.Value;
            }

            var texts = Sheet("文言");
            foreach (var r in texts.Rows)
            {
                string key = texts.Get(r, "キー");
                if (key == "") continue;
                if (!data.Texts.TryGetValue(key, out var list)) data.Texts[key] = list = new List<string>();
                list.Add(texts.Get(r, "文言"));
            }

            LoadMemoCsv(data, dir, problems);
            Validate(data, problems);
            return data;
        }

        // 設計メモ のCSV。状態が「未定」の行は読み飛ばす
        static void LoadMemoCsv(GameData data, string dir, List<string> problems)
        {
            IEnumerable<string[]> Rows(CsvTable t) => t == null ? Enumerable.Empty<string[]>() : t.Rows.Where(r => !t.Has("状態") || t.Get(r, "状態") != "未定");

            // 調整値の追加・調整値_○○（お金、チケットなど）：調整値シートにないキーだけ足す（xlsx に同じキーがあれば xlsx が正）
            var tuningFiles = new[] { "調整値の追加" }.Concat(Directory.Exists(dir) ? Directory.GetFiles(dir, "調整値_*.csv").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.Ordinal) : Enumerable.Empty<string>());
            var fromSheet = new HashSet<string>(data.Tuning.Keys);
            var seen = new Dictionary<string, string>();
            foreach (string file in tuningFiles)
            {
                var extra = Optional(dir, file, problems);
                foreach (var r in Rows(extra))
                {
                    string key = extra.Get(r, "キー");
                    double? v = Num(extra, r, "値", problems);
                    if (key == "" || v == null) continue;
                    if (fromSheet.Contains(key)) problems.Add($"[警告] {file}: キー「{key}」は調整値シートにもあるので、調整値シートの値を使います");
                    else if (seen.TryGetValue(key, out string first)) problems.Add($"[警告] {file}: キー「{key}」は {first}.csv にもあるので、{first}.csv の値を使います");
                    else { data.Tuning[key] = v.Value; seen[key] = file; }
                }
            }

            var shops = Optional(dir, "ショップ", problems);
            foreach (var r in Rows(shops))
                data.Shops.Add(new Shop { Id = shops.Get(r, "id"), Name = shops.Get(r, "名前"), Place = shops.Get(r, "場所"), Base = shops.Get(r, "基本の品ぞろえ") });
            var goods = Optional(dir, "ショップ_品ぞろえ", problems);
            foreach (var r in Rows(goods))
                data.ShopItems.Add(new ShopItem { ShopId = goods.Get(r, "ショップid"), Item = goods.Get(r, "アイテム"), CondKind = goods.Get(r, "条件の種類"), CondValue = goods.Get(r, "条件の値") });

            var tiers = Optional(dir, "ボールマスの段階", problems);
            foreach (var r in Rows(tiers))
                data.BallTiers.Add(new BallTier { Id = tiers.Get(r, "id"), Name = tiers.Get(r, "名前"), MinBadges = Int(tiers, r, "バッジ最小", problems) ?? 0, MaxBadges = Int(tiers, r, "バッジ最大", problems) ?? 99 });
            // モンスターボールマスの表と、鍵付きボールマスの当たり枠（同じ形）
            void BallTable(string name, List<BallSquareItem> into)
            {
                var balls = Optional(dir, name, problems);
                foreach (var r in Rows(balls))
                {
                    var b = new BallSquareItem { Item = balls.Get(r, "アイテム"), Bag = balls.Get(r, "入る袋"), Count = Int(balls, r, "個数", problems) ?? 1 };
                    foreach (var t in data.BallTiers) b.Weights[t.Id] = balls.Has(t.Id) ? Int(balls, r, t.Id, problems) ?? 0 : 0;
                    into.Add(b);
                }
            }
            BallTable("モンスターボールマス", data.BallSquareItems);
            BallTable("鍵付きボールマスの当たり", data.KeyPrizeItems);

            var rocket = Optional(dir, "ロケット団拠点", problems);
            foreach (var r in Rows(rocket))
                data.RocketBases.Add(new RocketBaseRow
                {
                    Id = rocket.Get(r, "id"), BaseId = rocket.Get(r, "拠点id"), BaseName = rocket.Get(r, "拠点名"), Place = rocket.Get(r, "場所"),
                    Mode = rocket.Get(r, "型"), Kind = rocket.Get(r, "種類"), Item = rocket.Get(r, "品"), Note = rocket.Get(r, "備考"),
                    Price = Int(rocket, r, "価格", problems), OwnerPrice = Int(rocket, r, "所有者の価格", problems),
                    Cost = Num(rocket, r, "必要なコスト", problems), OwnerCost = Num(rocket, r, "所有者の必要なコスト", problems), Once = rocket.Get(r, "回数") != "", Level = Int(rocket, r, "Lv", problems),
                });

            var random = Optional(dir, "ランダムイベント", problems);
            foreach (var r in Rows(random)) data.RandomEvents.Add(ParseEffect(random, r, problems));

            var islands = Optional(dir, "島マップ", problems);
            foreach (var r in Rows(islands))
                data.IslandMaps.Add(new IslandMap { MapId = islands.Get(r, "マップID"), Name = islands.Get(r, "名前"), Kind = islands.Get(r, "種類") });
            var tickets = Optional(dir, "チケットの行き先", problems);
            foreach (var r in Rows(tickets))
                data.TicketRows.Add(new TicketRow { Id = tickets.Get(r, "id"), Ticket = tickets.Get(r, "チケット"), Kind = tickets.Get(r, "行き先の種類"), Target = tickets.Get(r, "行き先"), Island = tickets.Get(r, "島マップ"), Weight = Int(tickets, r, "重み", problems) ?? 0, Level = Int(tickets, r, "Lv", problems) ?? 0 });

            var events = Optional(dir, "イベントマス", problems);
            foreach (var r in Rows(events))
                data.EventSquares.Add(new EventSquare { Square = events.Get(r, "マス"), Content = events.Get(r, "内容"), Effect = ParseEffect(events, r, problems) });

            var parties = Optional(dir, "イベント_バトル相手", problems);
            foreach (var r in Rows(parties))
            {
                var p = new EventParty { Id = parties.Get(r, "id"), Name = parties.Get(r, "名前"), Title = parties.Get(r, "肩書き"), Full = parties.Get(r, "バトルの種類") == "フル" };
                for (int i = 1; i <= 3; i++)
                    if (parties.Get(r, $"ポケモン{i}") != "") p.Members.Add((parties.Get(r, $"ポケモン{i}"), Int(parties, r, $"Lv{i}", problems) ?? 1));
                data.EventParties.Add(p);
            }

            var trainers = Optional(dir, "トレーナー", problems);
            foreach (var r in Rows(trainers))
                data.Trainers.Add(new TrainerEntry
                {
                    Id = trainers.Get(r, "id"), Square = trainers.Get(r, "マス"), Place = trainers.Get(r, "場所"), Kind = trainers.Get(r, "種類"), Class = trainers.Get(r, "職"),
                    Name = trainers.Get(r, "トレーナー名"), Pokemon = trainers.Get(r, "ポケモン"), Level = Int(trainers, r, "Lv", problems),
                });
            var classes = Optional(dir, "トレーナーの職", problems);
            foreach (var r in Rows(classes))
                data.TrainerClasses.Add(new TrainerClass { Class = classes.Get(r, "職"), TextId = classes.Get(r, "文言id") });

            foreach (string name in new[] { "イベント文言", "トレーナー文言", "ライバル文言" })
            {
                var t = Optional(dir, name, problems);
                foreach (var r in Rows(t))
                    data.Dialogs.Add(new DialogLine
                    {
                        Key = t.Get(r, "キー"), Pattern = Int(t, r, "パターン", problems) ?? 1, Order = Int(t, r, "順番", problems) ?? 1,
                        Speaker = t.Get(r, "話す人"), Text = t.Get(r, "文言"), CondKind = t.Get(r, "条件の種類"), CondValue = t.Get(r, "条件の値"),
                    });
            }

            var chars = Optional(dir, "キャラクター", problems);
            foreach (var r in Rows(chars))
                data.Characters.Add(new Character
                {
                    Id = chars.Get(r, "id"), Name = chars.Get(r, "名前"), Color = chars.Get(r, "色"), Generation = Int(chars, r, "世代", problems) ?? 0,
                    Starters = chars.Get(r, "御三家").Split('|').Select(s => s.Trim()).Where(s => s != "").ToList(),
                });

            var rules = Optional(dir, "イベントルール", problems);
            foreach (var r in Rows(rules))
                data.EventRules.Add(new EventRule { Id = rules.Get(r, "id"), Name = rules.Get(r, "名前"), Description = rules.Get(r, "説明"), Target = rules.Get(r, "カウント対象"), Mode = rules.Get(r, "方式"), Coef = Num(rules, r, "係数", problems) ?? 0 });

            var jobs = Optional(dir, "職の補正", problems);
            foreach (var r in Rows(jobs))
                data.JobBonuses.Add(new JobBonus
                {
                    Job = jobs.Get(r, "職"), Type = jobs.Get(r, "対象タイプ"),
                    Attack = Num(jobs, r, "こうげき", problems) ?? 1, Defense = Num(jobs, r, "ぼうぎょ", problems) ?? 1, SpAttack = Num(jobs, r, "とくこう", problems) ?? 1,
                    SpDefense = Num(jobs, r, "とくぼう", problems) ?? 1, Hp = Num(jobs, r, "HP", problems) ?? 1, Super = Num(jobs, r, "必殺", problems) ?? 1,
                    Capture = Num(jobs, r, "捕獲", problems) ?? 0, Bag = Int(jobs, r, "道具枠", problems) ?? 0, ExtraSteps = Int(jobs, r, "追加の歩数", problems) ?? 0,
                    NoCenterHeal = jobs.Get(r, "センターで回復") == "×", FreeTrade = jobs.Get(r, "交換のコスト") == "無視",
                });

            var jobSquares = Optional(dir, "職業マス", problems);
            foreach (var r in Rows(jobSquares))
                data.JobSquares.Add(new JobSquareRule
                {
                    Job = jobSquares.Get(r, "職"), Places = jobSquares.Get(r, "配置候補").Split("|").Select(x => x.Trim()).Where(x => x != "").ToList(),
                    Badges = Int(jobSquares, r, "バッジの数", problems) ?? 0, Type = jobSquares.Get(r, "対象タイプ"), TypeLevels = Int(jobSquares, r, "タイプのLv合計", problems) ?? 0,
                    Dex = Int(jobSquares, r, "図鑑の登録数", problems) ?? 0, FoeType = jobSquares.Get(r, "相手のタイプ"),
                });

            var rivals = Optional(dir, "ライバル", problems);
            foreach (var r in Rows(rivals))
            {
                var e = new RivalEntry { Id = rivals.Get(r, "id"), Name = rivals.Get(r, "名前"), Game = rivals.Get(r, "作品"), Place = rivals.Get(r, "場所"), Order = Int(rivals, r, "順番", problems) ?? 0, TradeLevel = Int(rivals, r, "交換Lv", problems) ?? 10 };
                for (int i = 1; i <= 2; i++) if (rivals.Get(r, "交換ポケモン" + i) != "") e.Trades.Add(rivals.Get(r, "交換ポケモン" + i));
                data.Rivals.Add(e);
            }
            var rivalParties = Optional(dir, "ライバル_手持ち", problems);
            foreach (var r in Rows(rivalParties))
            {
                var e = new RivalParty { Id = rivalParties.Get(r, "id"), RivalId = rivalParties.Get(r, "ライバルid"), CondKind = rivalParties.Get(r, "条件の種類"), CondValue = rivalParties.Get(r, "条件の値"), Priority = Int(rivalParties, r, "優先", problems) ?? 9 };
                for (int i = 1; i <= 3; i++) if (rivalParties.Get(r, "ポケモン" + i) != "") e.Party.Add((rivalParties.Get(r, "ポケモン" + i), Int(rivalParties, r, "Lv" + i, problems) ?? 1));
                data.RivalParties.Add(e);
            }
        }

        static CsvTable Optional(string dir, string name, List<string> problems)
        {
            string path = Path.Combine(dir, name + ".csv");
            if (File.Exists(path)) return new CsvTable(name, File.ReadAllText(path), problems);
            problems.Add($"[警告] {name}.csv がありません（xlsx に「{name}」シートを作るか、設計メモに置いてください）");
            return null;
        }

        // 交換のイベントマスの値「バリヤード Lv15、カモネギ Lv15」→ (種族, Lv)。Lv がなければ 10
        public static List<(string species, int level)> TradeList(string value) =>
            value.Split(new[] { '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Regex.Match(x.Trim(), @"^(.+?)\s*(?:Lv\s*(\d+))?$"))
                .Where(m => m.Success).Select(m => (m.Groups[1].Value.Trim(), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 10)).ToList();

        // ランダムイベント.csv / イベントマス.csv の1行
        static Effect ParseEffect(CsvTable t, string[] row, List<string> problems)
        {
            var e = new Effect
            {
                Name = t.Has("名前") ? t.Get(row, "名前") : t.Get(row, "内容"),
                Value = t.Get(row, "値"), Reward = t.Get(row, "報酬"), CondKind = t.Get(row, "条件の種類"), CondValue = t.Get(row, "条件の値"),
                TextKey = t.Get(row, "文言キー"), Text = t.Get(row, "文言"),
                Rate = Num(t, row, "発生率(%)", problems) ?? 0, Bell = t.Get(row, "ポケベル") == "○", HasChoice = t.Get(row, "選択肢") == "あり",
            };
            e.Id = t.Has("id") ? t.Get(row, "id") : t.Get(row, "マス");
            string kind = t.Get(row, "効果の型");
            if (kind == "" || kind.StartsWith("何もしない")) e.Kind = EffectKind.None;
            else if (kind.StartsWith("所持金")) e.Kind = EffectKind.Money;
            else if (kind.StartsWith("アイテム")) e.Kind = EffectKind.Item;
            else if (kind.StartsWith("強制移動")) e.Kind = EffectKind.ExtraMove;
            else if (kind.StartsWith("状態異常")) e.Kind = EffectKind.CureStatus;
            else if (kind.StartsWith("回復")) e.Kind = EffectKind.HealAll;
            else if (kind.StartsWith("ポイント")) e.Kind = EffectKind.Points;
            else if (kind.StartsWith("バトル")) e.Kind = EffectKind.Battle;
            else if (kind.StartsWith("職業")) e.Kind = EffectKind.UnlockJob;
            else if (kind.StartsWith("博物館")) e.Kind = EffectKind.Museum;
            else if (kind.StartsWith("お使い")) e.Kind = EffectKind.Errand;
            else if (kind.StartsWith("控えの上限")) e.Kind = EffectKind.BoxUp;
            else if (kind.StartsWith("交換")) e.Kind = EffectKind.Trade;
            else if (kind.StartsWith("育て屋")) e.Kind = EffectKind.DayCare;
            else problems.Add($"[警告] {t.Name}: 「{e.Id}」の効果の型「{kind}」はまだ作っていません（何も起きません）");
            bool numeric = e.Kind == EffectKind.Money || e.Kind == EffectKind.ExtraMove || e.Kind == EffectKind.Points || e.Kind == EffectKind.Errand || e.Kind == EffectKind.BoxUp;
            if (numeric && !int.TryParse(e.Value, out _)) { problems.Add($"[エラー] {t.Name}: 「{e.Id}」の値「{e.Value}」は数字にしてください"); e.Kind = EffectKind.None; }
            return e;
        }

        static void ParseEvolution(GameData data, Species s, string text, Species next, List<string> problems)
        {
            if (text == "") return;
            var parts = text.Split(new[] { '\n', '/', '／' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p != "").ToList();
            foreach (var part in parts)
            {
                string condition = part, target = next?.Name;
                int eq = part.IndexOfAny(new[] { '＝', '=' });
                if (eq >= 0) { condition = part.Substring(0, eq).Trim(); target = part.Substring(eq + 1).Trim(); }
                else if (parts.Count > 1) { problems.Add($"[エラー] ポケモン「{s.Name}」の進化条件「{part}」: 分かれ道なのに進化先（＝○○）が書かれていません"); continue; }

                var e = new Evolution { Target = target };
                var turns = Regex.Match(condition, @"先頭にして(\d+)ターン");
                if (int.TryParse(condition, out int level)) { e.Kind = EvolutionKind.Level; e.Value = level; }
                else if (turns.Success) { e.Kind = EvolutionKind.LeadTurns; e.Value = int.Parse(turns.Groups[1].Value); }
                else if (condition.Contains("ライバル")) e.Kind = EvolutionKind.Rival;
                else if (data.FindItem(condition) != null) { e.Kind = EvolutionKind.Item; e.Item = condition; }
                else { problems.Add($"[エラー] ポケモン「{s.Name}」の進化条件「{condition}」が読み取れません（レベル・アイテム名・先頭にして○ターン経過・ライバルマス のどれか）"); continue; }

                if (target == null || data.FindSpecies(target) == null) { problems.Add($"[エラー] ポケモン「{s.Name}」の進化先「{target}」がポケモンシートにありません"); continue; }
                s.Evolutions.Add(e);
            }
        }

        static void Validate(GameData d, List<string> problems)
        {
            foreach (var g in d.Species.GroupBy(s => s.Name).Where(g => g.Count() > 1))
                problems.Add($"[エラー] ポケモン「{g.Key}」が{g.Count()}回出てきます");
            var noWeakness = d.Species.Where(s => s.Weakness == "").Select(s => s.Name).ToList();
            if (noWeakness.Count > 0) problems.Add($"[警告] 弱点が空欄のポケモンが{noWeakness.Count}体います（「なし」なら「なし」と書く）: {string.Join("、", noWeakness)}");
            foreach (var s in d.Species)
            {
                if (!Types.Contains(s.Type)) problems.Add($"[エラー] ポケモン「{s.Name}」のタイプ「{s.Type}」は11タイプにありません");
                if (s.Weakness != "" && s.Weakness != "なし" && !Types.Contains(s.Weakness)) problems.Add($"[エラー] ポケモン「{s.Name}」の弱点「{s.Weakness}」は11タイプにありません");
                int sum = s.Hp + s.Attack + s.Defense + s.SpAttack + s.SpDefense + s.Speed;
                if (s.Total != sum) problems.Add($"[警告] ポケモン「{s.Name}」の合計が{s.Total}ですが、6つの種族値を足すと{sum}です");
            }

            foreach (var g in d.Items.GroupBy(i => i.Name).Where(g => g.Key != "" && g.Count() > 1))
                problems.Add($"[警告] アイテム「{g.Key}」が{g.Count()}回出てきます");

            var places = new HashSet<string>(d.Places.Select(p => p.Name));
            foreach (var e in d.Encounters)
            {
                if (!places.Contains(e.Place)) problems.Add($"[エラー] 出現: 場所「{e.Place}」がエリアシートにありません");
                if (d.FindSpecies(e.Pokemon) == null) problems.Add($"[エラー] 出現: ポケモン「{e.Pokemon}」（{e.Place}）がポケモンシートにありません");
                if (e.MinLevel > e.MaxLevel) problems.Add($"[エラー] 出現: {e.Place}の{e.Pokemon}は最低Lv（{e.MinLevel}）が最高Lv（{e.MaxLevel}）より大きいです");
            }
            foreach (var g in d.Encounters.Where(e => e.Method != "固定" && e.Method != "シンボル").GroupBy(e => (e.Place, e.Method)))
            {
                int total = g.Sum(e => e.Rate ?? 0);
                if (total != 100) problems.Add($"[エラー] 出現: {g.Key.Place}の{g.Key.Method}は出現率の合計が{total}です（100にする）");
            }
            foreach (var m in d.GymMembers.Where(m => d.FindSpecies(m.Pokemon) == null))
                problems.Add($"[エラー] ジム: {m.Gym}のポケモン「{m.Pokemon}」がポケモンシートにありません");

            ValidateMemoCsv(d, problems, places);
        }

        // 設計メモ のCSVどうし・xlsx とのつながり
        static void ValidateMemoCsv(GameData d, List<string> problems, HashSet<string> places)
        {
            void Error(string s) => problems.Add("[エラー] " + s);
            void Warn(string s) => problems.Add("[警告] " + s);

            foreach (var t in d.TicketRows)
            {
                if (d.FindItem(t.Ticket) == null) Error($"チケットの行き先: {t.Id} のチケット「{t.Ticket}」がアイテム一覧にありません");
                if (!d.IslandMaps.Any(i => i.Name == t.Island)) Error($"チケットの行き先: {t.Id} の島マップ「{t.Island}」が 島マップ.csv にありません");
                if (t.Weight <= 0) Warn($"チケットの行き先: {t.Id} の重みが0なので、出ません");
                if (t.Kind == "伝説" && (d.FindSpecies(t.Target) == null || t.Level <= 0)) Error($"チケットの行き先: {t.Id} の伝説「{t.Target}」がポケモンシートにないか、Lvがありません");
                if (t.Kind != "伝説" && t.Kind != "島") Warn($"チケットの行き先: {t.Id} の行き先の種類「{t.Kind}」は 島／伝説 のどちらかにしてください");
            }
            foreach (var i in d.IslandMaps.Where(i => !places.Contains(i.Name))) Error($"島マップ: 「{i.Name}」がエリアシートの場所にありません");

            foreach (var e in d.EventSquares.Where(x => x.Effect.Kind == EffectKind.Trade))
            {
                var list = TradeList(e.Effect.Value);
                if (list.Count == 0) Error($"イベントマス: 「{e.Square}」の交換の値に、もらえるポケモンを「バリヤード Lv15、カモネギ Lv15」のように書いてください");
                foreach (var (species, _) in list.Where(x => d.FindSpecies(x.species) == null)) Error($"イベントマス: 「{e.Square}」の交換の「{species}」がポケモンシートにありません");
            }

            foreach (var s in d.Shops)
            {
                if (s.Place != "" && !places.Contains(s.Place)) Error($"ショップ: {s.Id} の場所「{s.Place}」がエリアシートにありません");
                if (s.Base != "" && d.FindShop(s.Base) == null) Error($"ショップ: {s.Id} の基本の品ぞろえ「{s.Base}」がショップ表にありません");
            }
            foreach (var g in d.ShopItems)
            {
                if (d.FindShop(g.ShopId) == null) Error($"ショップ_品ぞろえ: ショップid「{g.ShopId}」がショップ表にありません");
                var item = d.FindItem(g.Item);
                if (item == null) Error($"ショップ_品ぞろえ: アイテム「{g.Item}」がアイテム一覧・ポケモンの道具シートにありません");
                else if (!item.ShopBuyable) Warn($"ショップ_品ぞろえ: 「{g.Item}」は店で買える×（非売品）です");
                if (!Conditions.IsKnown(g.CondKind)) Warn($"ショップ_品ぞろえ: 「{g.Item}」の条件の種類「{g.CondKind}」は知らない言葉です（満たさない扱い）");
            }

            for (int b = 0; b <= 8; b++)
                if (d.BallTiers.Count > 0 && !d.BallTiers.Any(t => b >= t.MinBadges && b <= t.MaxBadges)) Warn($"ボールマスの段階: バッジ{b}個のときの段階がありません");
            foreach (var b in d.BallSquareItems.Concat(d.KeyPrizeItems))
            {
                var item = d.FindItem(b.Item);
                if (item == null) { Error($"モンスターボールマス: アイテム「{b.Item}」がアイテム一覧・ポケモンの道具シートにありません"); continue; }
                string bag = BagName(ItemEffects.BagOf(item));
                if (b.Bag != bag) Warn($"モンスターボールマス: 「{b.Item}」の入る袋が「{b.Bag}」ですが、分類からは「{bag}」です（分類のほうを使います）");
            }
            foreach (var t in d.BallTiers.Where(t => d.BallSquareItems.Count > 0 && d.BallSquareItems.Sum(b => b.Weights[t.Id]) == 0))
                Warn($"モンスターボールマス: 段階「{t.Id}」の列がないか、重みがすべて0です（何も出ません）");

            foreach (var t in d.Trainers)
            {
                if (t.Kind != "トレーナー" && t.Kind != "ジムトレーナー" && t.Kind != "ロケット団") Warn($"トレーナー: {t.Id} の種類「{t.Kind}」は トレーナー／ジムトレーナー／ロケット団 のどれかにしてください（トレーナー扱い）");
                if (t.Pokemon != "" && d.FindSpecies(t.Pokemon) == null) Error($"トレーナー: {t.Id} のポケモン「{t.Pokemon}」がポケモンシートにありません");
                if (t.Pokemon != "" && t.Level == null) Warn($"トレーナー: {t.Id}（{t.Name}）のLvが空欄です（場所の出現表の最高Lv＋1で戦います）");
                if (!d.HasDialog(t.Id) && !t.IsGymTrainer && !d.TrainerClasses.Any(c => c.Class == t.Class)) Warn($"トレーナー: {t.Id} の職「{t.Class}」がトレーナーの職.csvになく、{t.Id} のセリフもありません（共通のセリフを使います）");
            }
            // 化石：効果の文の「○○に孵る」から孵るポケモンを読む（3.2.2）
            foreach (var f in d.Items.Where(i => i.Category == "化石"))
            {
                var m = Regex.Match(f.Effect, @"([^\s、。に]+)に孵る");
                if (!m.Success || d.FindSpecies(m.Groups[1].Value) == null) Warn($"アイテム一覧: 化石「{f.Name}」の効果に「（ポケモン名）に孵る」がないか、そのポケモンがポケモンシートにありません（博物館で預けられません）");
            }
            foreach (var j in d.JobSquares)
                if (!d.Jobs.Any(x => x.Name == j.Job)) Error($"職業マス: 職業「{j.Job}」が職シートにありません");
            foreach (var r in d.RocketBases)
            {
                if (r.Mode != "買う" && r.Mode != "もらう" && r.Mode != "交換") Error($"ロケット団拠点: {r.Id} の型「{r.Mode}」は 買う／もらう／交換 のどれかにしてください");
                if (r.Kind != "アイテム" && r.Kind != "ポケモン") Error($"ロケット団拠点: {r.Id} の種類「{r.Kind}」は アイテム／ポケモン のどちらかにしてください");
                if (r.Kind == "アイテム" && d.FindItem(r.Item) == null) Error($"ロケット団拠点: {r.Id} の品「{r.Item}」がアイテム一覧・ポケモンの道具シートにありません");
                if (r.Kind == "ポケモン" && d.FindSpecies(r.Item) == null) Error($"ロケット団拠点: {r.Id} の品「{r.Item}」がポケモンシートにありません");
                if (!d.Places.Any(p => p.Name == r.Place)) Error($"ロケット団拠点: {r.Id} の場所「{r.Place}」がエリアシートにありません");
                if (r.Mode == "買う" && r.Price == null) Error($"ロケット団拠点: {r.Id} は「買う」なのに価格が空です");
                if (r.Kind == "ポケモン" && r.Level == null) Error($"ロケット団拠点: {r.Id} はポケモンなのに Lv が空です");
                if (r.Mode == "交換" && r.Cost == null) Error($"ロケット団拠点: {r.Id} は「交換」なのに必要なコストが空です");
                if (!d.HasDialog("ev_rocket_" + r.BaseId.Substring(Math.Min(3, r.BaseId.Length)))) Warn($"ロケット団拠点: 拠点「{r.BaseId}」のセリフ ev_rocket_{r.BaseId.Substring(Math.Min(3, r.BaseId.Length))} がイベント文言.csvにありません");
            }
            foreach (var c in d.TrainerClasses.Where(c => !d.HasDialog(c.TextId)))
                Warn($"トレーナーの職: 「{c.Class}」の文言id「{c.TextId}」のセリフがトレーナー文言.csvにありません");

            foreach (var p in d.EventParties)
                foreach (var (species, _) in p.Members.Where(m => d.FindSpecies(m.species) == null))
                    Error($"イベント_バトル相手: {p.Id} のポケモン「{species}」がポケモンシートにありません");

            foreach (var e in d.RandomEvents.Concat(d.EventSquares.Select(x => x.Effect)))
            {
                if (e.Kind == EffectKind.Item && d.FindItem(e.Value) == null) Error($"イベント: {e.Id} のアイテム「{e.Value}」がアイテム一覧・ポケモンの道具シートにありません");
                if (e.Kind == EffectKind.Battle && d.FindParty(e.Value) == null) Error($"イベント: {e.Id} のバトル相手「{e.Value}」がイベント_バトル相手.csvにありません");
                if (e.Kind == EffectKind.Battle && e.Reward != "" && d.FindSpecies(e.Reward) == null) Error($"イベント: {e.Id} の報酬「{e.Reward}」がポケモンシートにありません");
                if (e.Kind == EffectKind.UnlockJob)
                    foreach (var j in e.Value.Split('|').Where(j => !d.Jobs.Any(x => x.Name == j))) Error($"イベント: {e.Id} の職業「{j}」が職シートにありません");
                if (!Conditions.IsKnown(e.CondKind)) Warn($"イベント: {e.Id} の条件の種類「{e.CondKind}」は知らない言葉です（満たさない扱い）");
                if (e.TextKey != "" && !d.HasDialog(e.TextKey)) Warn($"イベント: {e.Id} の文言キー「{e.TextKey}」のセリフがイベント文言.csvにありません");
                if (e.Kind == EffectKind.Battle && e.HasChoice)
                    foreach (var suffix in new[] { "_yes", "_no", "_win", "_lose" }.Where(s => !d.HasDialog(e.TextKey + s)))
                        Warn($"イベント: {e.Id} のセリフ「{e.TextKey}{suffix}」がありません");
                if (e.Rate <= 0 && !e.Bell && d.RandomEvents.Contains(e)) Warn($"ランダムイベント: {e.Id} は発生率もポケベルも空なので、起きることがありません");
            }
            double total = d.RandomEvents.Sum(e => e.Rate);
            if (total > 100) Error($"ランダムイベント: 発生率の合計が{total}%で100を超えています");
            foreach (var l in d.Dialogs.Where(l => !Conditions.IsKnown(l.CondKind)))
                Warn($"文言: 「{l.Key}」の条件の種類「{l.CondKind}」は知らない言葉です");

            foreach (var r in d.EventRules)
            {
                if (r.Target != "図鑑の登録数" && r.Target != "捕まえた種類の数" && r.Target != "バッジの数") Error($"イベントルール: {r.Id} のカウント対象「{r.Target}」は 図鑑の登録数／捕まえた種類の数／バッジの数 のどれかにしてください");
                if (r.Mode != "数に応じて" && r.Mode != "1位だけ") Error($"イベントルール: {r.Id} の方式「{r.Mode}」は 数に応じて／1位だけ のどちらかにしてください");
            }
            foreach (var j in d.JobBonuses)
            {
                if (!d.Jobs.Any(x => x.Name == j.Job)) Error($"職の補正: 職業「{j.Job}」が職シートにありません");
                if (j.Type != "" && !Types.Contains(j.Type)) Error($"職の補正: {j.Job} の対象タイプ「{j.Type}」は11タイプにありません");
            }
            foreach (var c in d.Characters)
            {
                if (c.Starters.Count == 0) Error($"キャラクター: {c.Name} の御三家が空です");
                foreach (var s in c.Starters.Where(s => d.FindSpecies(s) == null)) Error($"キャラクター: {c.Name} の御三家「{s}」がポケモンシートにありません");
            }
            foreach (var r in d.Rivals)
            {
                if (!d.RivalParties.Any(x => x.RivalId == r.Id && x.Party.Count > 0)) Warn($"ライバル: {r.Id}（{r.Name}）の手持ちがライバル_手持ち.csvにありません（止まっても何も起きません）");
                if (!d.RivalParties.Any(x => x.RivalId == r.Id && (x.CondKind == "いつも" || x.CondKind == "どれにも当てはまらない" || x.CondKind == ""))) Warn($"ライバル: {r.Id}（{r.Name}）に「いつも」か「どれにも当てはまらない」の手持ちがありません（条件に合わないと戦えません）");
                if (!d.Places.Any(p => p.Name == r.Place)) Error($"ライバル: {r.Id} の場所「{r.Place}」がエリアシートにありません");
            }
            foreach (var x in d.RivalParties)
            {
                if (!d.Rivals.Any(r => r.Id == x.RivalId)) Error($"ライバル_手持ち: {x.Id} のライバルid「{x.RivalId}」がライバル.csvにありません");
                if (!Conditions.IsKnown(x.CondKind)) Warn($"ライバル_手持ち: {x.Id} の条件の種類「{x.CondKind}」は知らない言葉です（満たさない扱い）");
            }
            foreach (var name in d.RivalParties.SelectMany(x => x.Party.Select(m => m.species)).Concat(d.Rivals.SelectMany(r => r.Trades)).Where(n => d.FindSpecies(n) == null))
                Error($"ライバル: ポケモン「{name}」がポケモンシートにありません");
        }

        public static string BagName(Bag bag) => bag == Bag.Ball ? "ボール" : bag == Bag.Pokemon ? "ポケモンの道具" : "トレーナーの道具";

        static int? Int(CsvTable t, string[] row, string column, List<string> problems)
        {
            double? v = Num(t, row, column, problems);
            return v == null ? (int?)null : (int)Math.Round(v.Value);
        }

        static double? Num(CsvTable t, string[] row, string column, List<string> problems)
        {
            string s = t.Get(row, column);
            if (s == "") return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            problems.Add($"[エラー] {t.Name}: 「{column}」に数字でない値「{s}」があります");
            return null;
        }
    }
}
