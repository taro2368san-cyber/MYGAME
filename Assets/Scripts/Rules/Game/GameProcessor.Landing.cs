using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 止まったマスの効果（仕様書3.2）、ランダムイベント、施設（16.4）
    public sealed partial class GameProcessor
    {
        // 止まったマスの効果を始める。プレイヤーの操作を待つ（施設・バトル・選択肢・追加の移動）なら true
        // 新しいマスの種類を作るときは、この switch に1つ足す
        bool Land(List<GameEvent> events)
        {
            var player = Current;
            var sq = board[player.Square];
            var other = State.Players.FirstOrDefault(p => p != player && p.Square == player.Square && p.Party.Any(m => !m.Fainted));
            if (other != null && player.Party.Any(m => !m.Fainted)) return StartPlayerBattle(other, events);
            SpringTrap(player, events);

            switch (sq.Type)
            {
                case "空マス":
                    return RandomEvent(events, out bool w1) ? w1 : Nothing("何も起きなかった", events);
                case "草むら":
                    if (RandomEvent(events, out bool w2)) return w2;
                    if (player.Has("ゴールドスプレー")) return Nothing("ゴールドスプレーで 野生のポケモンが よってこない", events);
                    return StartWild(sq, events) || Nothing("野生のポケモンは出てこなかった", events);
                case "トレーナー": return LandTrainer(sq, events);
                case "ポケモンセンター":
                case "スタート": // スタートは研究室（3.2.1）：センターの機能＋進化サービス＋けんきゅういんの解放
                    if (State.WeekEvent == "ストライキ") return Nothing("ストライキ中で休みだった", events);
                    player.VisitedCenters.Add(sq.Id);
                    // のろい：センターに入れない。シオンタウンのセンターでは解いてもらえる（アイテム一覧の のろいのふだ）
                    if (player.Has("のろい") && sq.Place != "シオンタウン") return Nothing("のろわれていて ポケモンセンターに 入れない…", events);
                    if (player.Has("のろい")) { player.Ailments.RemoveAll(a => a.Name == "のろい"); events.Add(new AilmentCured { Player = player.Id, Name = "のろい" }); }
                    if (JobNoCenterHeal(player)) events.Add(new NothingHappened { Player = player.Id, Reason = $"{player.Job}なので 回復は してもらえない（入れ替えなどは できる）" });
                    else HealParty(player, events);
                    bool lab = sq.Type == "スタート";
                    if (lab && player.Dex.Count >= TuneInt("lab_researcher_dex") && data.Jobs.Any(j => j.Name == "けんきゅういん") && !player.UnlockedJobs.Contains("けんきゅういん"))
                        UnlockJob(player, "けんきゅういん", events); // 解放した直後は、このままセンターの画面で就ける（9.2）
                    return OpenFacility(new FacilityState { Kind = FacilityKind.Center, Player = player.Id, Square = sq.Id, IsLab = lab }, events);
                case "ショップ": return LandShop(sq, events);
                case "モンスターボール": return LandBallSquare(sq, events);
                case "鍵付きモンスターボール":
                    // カギがあれば使って開ける。中身は「当たり枠」（key_prize_rate の確率）か、1つ上の段階の表（8.8.1）
                    if (!player.TrainerBag.Contains("カギ")) return Nothing("カギが かかっていて 開かない", events);
                    RemoveItem(player, "カギ");
                    events.Add(new ItemUsed { Player = player.Id, Item = "カギ" });
                    return LandBallSquare(sq, events, true);
                case "ジム": return LandGym(sq, events);
                case "ライバル": return LandRival(sq, events);
                case "ロケット団": return LandRocket(sq, events);
                case "イベントマス":
                    var e = data.EventSquares.FirstOrDefault(x => x.Square == sq.Name);
                    if (e == null || e.Effect.Kind == EffectKind.None) return Nothing(e?.Content is string c && c != "" ? c : "何も起きなかった", events);
                    if (e.Effect.Kind == EffectKind.Museum) return LandMuseum(events);
                    return StartEvent(e.Effect, events);
                default: // ワープ（着いた先）、ゴールなど
                    return Nothing("何も起きなかった", events);
            }
        }

        bool Nothing(string reason, List<GameEvent> events)
        {
            events.Add(new NothingHappened { Player = Current.Id, Reason = reason });
            return false;
        }

        // ---- ランダムイベント（3.2、ランダムイベント.csv）----

        // 草むら・空マスで、各イベントの発生率(%)を順に足していき、1回の抽選でどれか1つが起きる（残りは何も起きない）
        bool RandomEvent(List<GameEvent> events, out bool wait)
        {
            wait = false;
            double r = Rng.Next(ref State.Rng) * 100, sum = 0;
            var e = data.RandomEvents.FirstOrDefault(x => x.Rate > 0 && r < (sum += x.Rate));
            if (e == null) return false;
            wait = StartEvent(e, events);
            return true;
        }

        // イベントを始める：セリフ → 条件 → 選択肢（はい/いいえ、職業を選ぶ）→ 効果。操作待ちなら true
        bool StartEvent(Effect e, List<GameEvent> events)
        {
            var p = Current;
            events.Add(new EventStarted { Player = p.Id, Id = e.Id, Name = e.Name });
            if (e.TextKey != "" && data.HasDialog(e.TextKey)) Say(e.TextKey, events);
            else events.Add(new RandomEventHappened { Player = p.Id, Name = e.Name, Text = e.Text });

            if (e.CondKind != "" && !Conditions.Met(e.CondKind, e.CondValue, p, State))
            {
                Say(e.TextKey + "_notyet", events);
                return false;
            }
            if (e.CondKind == "道具を渡す") // 例：サファリゾーンの園長に 金の入れ歯を渡すと、だいすきくらぶ
            {
                RemoveItem(p, e.CondValue);
                events.Add(new ItemGiven { Player = p.Id, Item = e.CondValue, To = e.Name });
            }
            if (e.Kind == EffectKind.UnlockJob)
            {
                Say(e.TextKey + "_ok", events);
                var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = e.Id, Prompt = "どの職業に なれるように する？" };
                f.Options.AddRange(e.Value.Split('|'));
                f.Options.Add("やめておく");
                return OpenFacility(f, events);
            }
            if (e.HasChoice)
            {
                var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = p.Square, EventId = e.Id, Prompt = e.Kind == EffectKind.Battle ? "勝負を うける？" : "どうする？" };
                f.Options.AddRange(new[] { "はい", "いいえ" });
                return OpenFacility(f, events);
            }
            return ApplyEffect(e, events);
        }

        // 効果の型（13.2）を実行する。操作待ちになるなら true
        bool ApplyEffect(Effect e, List<GameEvent> events)
        {
            var p = Current;
            switch (e.Kind)
            {
                case EffectKind.Money: AddMoney(p, int.Parse(e.Value), e.Name, events); return false;
                case EffectKind.Points: p.Points += int.Parse(e.Value); events.Add(new PointsChanged { Player = p.Id, Amount = int.Parse(e.Value) }); return false;
                case EffectKind.Item: return GiveItem(p, e.Value, events);
                case EffectKind.HealAll: HealParty(p, events); return false;
                case EffectKind.CureStatus:
                    foreach (var m in p.Party) m.Status = Status.None;
                    events.Add(new PartyHealed { Player = p.Id });
                    return false;
                case EffectKind.ExtraMove:
                    BeginMove(int.Parse(e.Value), events);
                    if (State.Move.Reachable.Count == 0) { State.Move = null; return false; }
                    events.Insert(events.Count - 1, new ExtraMoveStarted { Player = p.Id, Steps = State.Move.Dice, Reachable = State.Move.Reachable.ToList() });
                    return true;
                case EffectKind.Battle:
                    var party = data.FindParty(e.Value);
                    if (party == null || party.Members.Count == 0) return false;
                    if (!StartBattle(BattleKind.Event, party.Full, Npc(party.Name, party.Members), events)) return false;
                    B.EventId = e.Id;
                    return true;
                case EffectKind.UnlockJob:
                    foreach (var job in e.Value.Split('|')) UnlockJob(p, job, events);
                    return false;
                default: return false;
            }
        }

        void UnlockJob(PlayerState p, string job, List<GameEvent> events)
        {
            if (!p.UnlockedJobs.Contains(job)) p.UnlockedJobs.Add(job);
            events.Add(new JobUnlocked { Player = p.Id, Job = job });
        }

        // 「今すぐこの職業に就きますか」（9.2、16.4 #27）
        bool OfferJob(PlayerState p, string job, List<GameEvent> events)
        {
            if (p.Job == job) return false;
            var f = new FacilityState { Kind = FacilityKind.JobOffer, Player = p.Id, Square = p.Square, PendingJob = job, Prompt = $"今すぐ「{job}」に 就きますか？" + (p.Job != "" ? $"（今は {p.Job}）" : "") };
            f.Options.AddRange(new[] { "就く", "あとで（ポケモンセンターで変えられる）" });
            return OpenFacility(f, events);
        }

        void SetJob(PlayerState p, string job, List<GameEvent> events)
        {
            p.Job = job; // 有効な職業は1つだけ。前の職業の効果は消える（9.2）
            ApplyJobHp(p);
            events.Add(new JobChanged { Player = p.Id, Job = job });
        }

        // その人の職業の、このポケモンへの補正（タイプ職は対象タイプだけ、ステータス職は全員）
        (double atk, double def, double spa, double spd, double hp, double super) JobBonusFor(int player, PokemonState m)
        {
            (double atk, double def, double spa, double spd, double hp, double super) r = (1, 1, 1, 1, 1, 1);
            if (player < 0) return r;
            string type = Pokemons.Of(data, m).Type;
            foreach (var b in data.JobBonuses.Where(b => b.Job == State.Players[player].Job && (b.Type == "" || b.Type == type)))
                r = (r.atk * b.Attack, r.def * b.Defense, r.spa * b.SpAttack, r.spd * b.SpDefense, r.hp * b.Hp, r.super * b.Super);
            return r;
        }

        // HPの倍率を職業に合わせる（最大HPが変わった分だけ今のHPも動かす）。職業を変えたとき・ポケモンが来たときに呼ぶ
        void ApplyJobHp(PlayerState p)
        {
            foreach (var m in p.Party.Concat(p.Box))
            {
                int oldMax = Pokemons.MaxHp(data, m);
                m.HpMult = JobBonusFor(p.Id, m).hp;
                if (!m.Fainted) m.Hp = Math.Max(1, Math.Min(Pokemons.MaxHp(data, m), m.Hp + Pokemons.MaxHp(data, m) - oldMax));
            }
        }

        // 手持ち → 控えの順に入れる。どちらもいっぱいなら、控えから1体逃がすか、来たポケモンを逃がすかを選ぶ（6.1）。選択を待つなら true
        bool AddPokemon(PlayerState p, PokemonState mon, List<GameEvent> events)
        {
            p.Dex.Add(mon.Species);
            if (p.Party.Count < TuneInt("party_size")) { p.Party.Add(mon); ApplyJobHp(p); return false; }
            if (p.Box.Count < TuneInt("box_max")) { p.Box.Add(mon); ApplyJobHp(p); return false; }
            var f = new FacilityState { Kind = FacilityKind.BoxFull, Player = p.Id, Square = p.Square, PendingPokemon = mon, Prompt = $"控えが いっぱい。{mon.Species}を 入れるなら、どの子を 逃がす？" };
            f.Options.AddRange(p.Box.Select(m => $"{m.Species} Lv{m.Level}"));
            f.Options.Add($"{mon.Species}を 逃がす");
            return OpenFacility(f, events);
        }

        // ---- ショップ・モンスターボールマス ----

        bool LandShop(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            if (State.WeekEvent == "ストライキ") return Nothing("ストライキ中で休みだった", events);
            var shop = sq.ShopId != "" ? data.FindShop(sq.ShopId) : data.Shops.FirstOrDefault(s => s.Place == sq.Place);
            if (shop == null) return Nothing("店は閉まっていた", events);
            var goods = data.ShopGoods(shop.Id, g => Conditions.Met(g.CondKind, g.CondValue, p, State));
            return OpenFacility(new FacilityState { Kind = FacilityKind.Shop, Player = p.Id, Square = sq.Id, Goods = goods, Prompt = shop.Name }, events);
        }

        // バッジの数で段階が決まる（8.8.1）。10週連続で最下位なら1段階上の表
        bool LandBallSquare(BoardSquare sq, List<GameEvent> events, bool keyed = false)
        {
            var p = Current;
            bool rescue = p.LastPlaceWeeks >= TuneInt("ballmass_boost_weeks");
            var tier = data.TierFor(p.Badges, rescue || keyed);
            var own = data.TierFor(p.Badges);
            bool prize = keyed && own != null && data.KeyPrizeItems.Any(x => x.Weights[own.Id] > 0) && Chance(Tune("key_prize_rate"));
            var item = prize ? Pick(data.KeyPrizeItems, x => x.Weights[own.Id]) : tier == null ? null : Pick(data.BallSquareItems, x => x.Weights[tier.Id]);
            if (item == null) return Nothing("中は空っぽだった", events);
            events.Add(new BallTierUsed { Player = p.Id, Tier = prize ? own.Name : tier.Name, Rescue = rescue && !keyed && tier != own, Keyed = keyed, Prize = prize });
            for (int i = 0; i < Math.Max(1, item.Count); i++)
                if (GiveItem(p, item.Item, events)) return true;
            return false;
        }

        // ---- トレーナー（11.2）----

        bool LandTrainer(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            var entry = data.Trainers.FirstOrDefault(t => t.Square == sq.Name);
            bool owned = State.Owners.TryGetValue(sq.Id, out int owner);
            string name = entry != null && entry.Name != "" ? entry.Name : "トレーナー";
            string leader = data.GymMembers.FirstOrDefault(m => m.Gym == data.Places.FirstOrDefault(x => x.Name == sq.Place)?.Gym)?.Leader ?? "ジムリーダー";

            // ジムが閉まっている町のジムトレーナー（トキワ）は、ジムが開くまで戦わない
            if (!owned && entry != null && entry.IsGymTrainer && GymClosed(data.Places.FirstOrDefault(x => x.Name == sq.Place)?.Gym ?? "", p))
                return Nothing($"{name}は ジムが 開くのを 待っている（バッジ7個で開く）", events);
            // 持ち主がいなければバトル（ジムトレーナーも同じ。だれかに倒されたら、その人のファンになる）
            if (!owned)
            {
                if (!TrainerMon(sq, entry, out var mon)) return Nothing("トレーナーは留守だった", events);
                string key = TrainerDialogKey(entry);
                Say(key, events, p, ("名前", name), ("ポケモン", mon.species), ("リーダー", leader));
                StartBattle(BattleKind.Trainer, false, Npc(name, new[] { mon }), events);
                B.TrainerId = entry?.Id;
                B.DialogKey = key;
                return true;
            }
            if (owner == p.Id)
            {
                // 自分のファン：回復と、ちょっとした道具（ボールマスの最初の段階の表から）
                string key = entry != null && entry.IsGymTrainer ? "gymtrainer_cleared" : entry != null && data.HasDialog(entry.Id + "_fan") ? entry.Id + "_fan" : "trainer_fan";
                Say(key, events, p, ("名前", name), ("リーダー", leader));
                HealParty(p, events);
                var first = data.TierFor(0);
                var gift = first == null ? null : Pick(data.BallSquareItems, x => x.Weights[first.Id]);
                return gift != null && GiveItem(p, gift.Item, events);
            }
            var facility = new FacilityState { Kind = FacilityKind.FanHeal, Player = p.Id, Square = sq.Id, Owner = owner, Price = TuneInt("fan_heal_price") };
            Say(entry != null && entry.IsGymTrainer ? "gymtrainer_cleared" : "trainer_other", events, p, ("名前", name), ("所有者", owner >= 0 && owner < State.Players.Count ? State.Players[owner].Name : "だれか"), ("金額", facility.Price.ToString()), ("リーダー", leader));
            if (TuneInt("fan_heal_forced") == 1)
            {
                PayHeal(facility, events);
                return false;
            }
            return OpenFacility(facility, events);
        }

        // トレーナーの相手のポケモン。表にポケモンかLvがなければ、その場所（なければ同じ解放条件の場所）の出現表からランダム。Lvは最高Lv＋1（仮）
        bool TrainerMon(BoardSquare sq, TrainerEntry entry, out (string species, int level) mon)
        {
            if (entry != null && entry.Pokemon != "" && entry.Level != null) { mon = (entry.Pokemon, entry.Level.Value); return true; }
            var rows = data.Encounters.Where(x => x.Place == sq.Place && x.Rate != null).ToList();
            if (rows.Count == 0)
            {
                var sameUnlock = new HashSet<string>(data.Places.Where(x => x.Area == sq.Area && BoardData.ParseUnlock(x.Unlock) == sq.RequiredBadges).Select(x => x.Name));
                rows = data.Encounters.Where(x => sameUnlock.Contains(x.Place) && x.Rate != null).ToList();
            }
            var row = Pick(rows, x => x.Rate ?? 0);
            mon = row == null ? ("", 0) : (entry != null && entry.Pokemon != "" ? entry.Pokemon : row.Pokemon, entry?.Level ?? row.MaxLevel + 1);
            return row != null;
        }

        // セリフのキー：そのトレーナー専用 → ジムトレーナー共通 → 職ごと → 共通 の順
        string TrainerDialogKey(TrainerEntry entry)
        {
            if (entry == null) return "trainer";
            if (data.HasDialog(entry.Id)) return entry.Id;
            if (entry.IsGymTrainer) return "gymtrainer";
            var cls = data.TrainerClasses.FirstOrDefault(c => c.Class == entry.Class);
            return cls != null && data.HasDialog(cls.TextId) ? cls.TextId : "trainer";
        }

        void PayHeal(FacilityState f, List<GameEvent> events)
        {
            var p = Current;
            AddMoney(p, -Math.Min(p.Money, f.Price), "回復代", events, State.Players[f.Owner]);
            HealParty(p, events);
        }

        // トキワジムはバッジ7個を取ったプレイヤーにだけ開く（3.3）。町のジムトレーナーも、開くまでは戦わない
        public static bool GymClosed(string gym, PlayerState p) => gym == "トキワジム" && p.Badges < 7;

        // ジム（3.3）。町のジムトレーナーが全員（だれかに）倒されてから。手持ちの数はジムシート、レベルはこのプレイヤーの何個目か
        bool LandGym(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            string gym = data.Places.FirstOrDefault(x => x.Name == sq.Place)?.Gym ?? "";
            var members = data.GymMembers.Where(m => m.Gym == gym).ToList();
            if (members.Count == 0) return Nothing("ジムはお休みだった", events);
            if (p.Gyms.Contains(gym)) return Nothing("もうバッジを持っている", events);
            if (State.WeekEvent == "ストライキ") return Nothing("ストライキ中で休みだった", events);
            if (GymClosed(gym, p)) return Nothing("トキワジムは閉まっている（バッジ7個で開く）", events);
            var remaining = data.Trainers.Where(t => t.IsGymTrainer && t.Place == sq.Place && !(board.Squares.Values.FirstOrDefault(x => x.Name == t.Square) is BoardSquare ts && State.Owners.ContainsKey(ts.Id))).ToList();
            if (remaining.Count > 0) return Nothing($"まず 町の ジムトレーナーを 倒そう（あと {remaining.Count}人：{string.Join("、", remaining.Select(t => t.Name))}）", events);
            int order = Math.Min(p.Badges + 1, 8);
            int ace = TuneInt($"gym_ace_level_{order}"), step = TuneInt("gym_level_step");
            int aceIndex = members.FindLastIndex(m => m.Role == "エース");
            if (aceIndex < 0) aceIndex = members.Count - 1;
            var team = members.Select((m, i) => (m.Pokemon, Math.Max(1, ace - step * Math.Abs(aceIndex - i))));
            return StartBattle(BattleKind.Gym, true, Npc(members[0].Leader, team), events, gym);
        }

        // ライバルマス（11.5）。持ち主のいないマスだけ戦える（所有権は固定）
        bool LandRival(BoardSquare sq, List<GameEvent> events)
        {
            if (State.Owners.TryGetValue(sq.Id, out int owner)) return OpenRival(Current, sq, owner, true, events);
            return RivalBattle(sq, false, events);
        }

        // ライバルとフルバトル。セリフは ライバル文言.csv の「ライバルid」（再戦は「_rematch」）、勝ち負けは「_win」「_lose」
        bool RivalBattle(BoardSquare sq, bool rematch, List<GameEvent> events)
        {
            var p = Current;
            var rival = RivalOf(sq);
            // 手持ちは ライバル_手持ち.csv：条件（御三家を選んだ人がいる など）に合う行のうち、優先の小さいもの
            var party = rival == null ? null : data.RivalParties.Where(x => x.RivalId == rival.Id && x.Party.Count > 0 && Conditions.Met(x.CondKind, x.CondValue, p, State)).OrderBy(x => x.Priority).FirstOrDefault();
            if (party == null) return Nothing("ライバルはいなかった", events);
            Say(rival.Id + (rematch ? "_rematch" : ""), events, p);
            StartBattle(BattleKind.Rival, true, Npc(rival.Name, party.Party), events);
            B.DialogKey = rival.Id;
            B.Rematch = rematch;
            return true;
        }

        RivalEntry RivalOf(BoardSquare sq) => data.Rivals.FirstOrDefault(r => r.Place == sq.Place);

        // 持ち主がいるライバルマスは施設（11.5）：交換進化と、他地方のポケモンとの交換。持ち主は無料・コスト低め、ほかの人は通信ケーブルが要る
        // greet：止まったとき（持ち主には「_owner」、ほかの人には「_other」のセリフ）。交換のあとの開き直しでは false
        bool OpenRival(PlayerState p, BoardSquare sq, int owner, bool greet, List<GameEvent> events)
        {
            var rival = RivalOf(sq);
            if (greet && rival != null) Say(rival.Id + (owner == p.Id ? "_owner" : "_other"), events, p, ("所有者", owner >= 0 && owner < State.Players.Count ? State.Players[owner].Name : "だれか"));
            bool canEvolve = p.Party.Any(m => RivalTargets(m).Any());
            // 持ち主は再戦できるので、いつでも開く。ほかの人は、することがなければ開かない
            if (owner != p.Id && !canEvolve && RivalTrades(p, sq.Id).Count == 0) return false;
            return OpenFacility(new FacilityState { Kind = FacilityKind.Rival, Player = p.Id, Square = sq.Id, Owner = owner }, events);
        }

        // ライバルマスで交換できる他地方のポケモン：(種族, Lv, 渡す子に要るコスト)。1人1回ずつ
        public List<(string species, int level, double need)> RivalTrades(PlayerState p, int square)
        {
            var sq = board[square];
            var rival = RivalOf(sq);
            var list = new List<(string, int, double)>();
            if (rival == null) return list;
            bool asOwner = State.Owners.TryGetValue(square, out int o) && (o == p.Id || p.Has("へんそう"));
            foreach (string s in rival.Trades.Where(s => !p.RocketUsed.Contains("rival:" + s) && data.FindSpecies(s) != null))
            {
                double need = Pokemons.Cost10(data, Pokemons.Create(data, s, rival.TradeLevel, "", "", 0)) * Tune(asOwner ? "rival_trade_cost_owner" : "rival_trade_cost") / 10.0;
                list.Add((s, rival.TradeLevel, need));
            }
            return list;
        }

        public List<string> RivalTargets(PokemonState m) => Pokemons.Of(data, m).Evolutions.Where(e => e.Kind == EvolutionKind.Rival).Select(e => e.Target).ToList();

        // 研究室の進化サービスの値段。けんきゅういんは無料（進化に必要なアイテムを踏み倒せる、3.2.1・9.1）
        public int LabPrice(PlayerState p) => p.Job == "けんきゅういん" ? 0 : TuneInt("lab_evolve_price");

        BattleSide Npc(string name, IEnumerable<(string species, int level)> team) =>
            new BattleSide { Name = name, Team = team.Select(t => Pokemons.Create(data, t.species, t.level, "", "", State.Week)).ToList() };

        // ---- 施設・選択肢 ----

        bool OpenFacility(FacilityState f, List<GameEvent> events)
        {
            State.Facility = f;
            State.Phase = Phase.Facility;
            events.Add(new FacilityOpened { Player = f.Player, Kind = f.Kind, Goods = f.Goods.ToList(), Price = f.Price, Owner = f.Owner });
            if (f.Options.Count > 0) events.Add(new OptionsRequested { Player = f.Player, Prompt = f.Prompt, Options = f.Options.ToList() });
            return true;
        }

        public int BuyPrice(string item)
        {
            int price = data.FindItem(item)?.Price ?? 0;
            return State.WeekEvent == "セール" ? (int)(price * (1 - Tune("sale_discount"))) : price;
        }

        public int SellPrice(string item) => (int)((data.FindItem(item)?.Price ?? 0) * Tune("sell_ratio"));

        string CheckFacility(Command c)
        {
            var f = State.Facility;
            var p = State.Players[f.Player];
            switch (c)
            {
                case Buy b:
                    if (f.Kind != FacilityKind.Shop) return "ここはショップではありません";
                    if (!f.Goods.Contains(b.Item)) return "その品物は売っていません";
                    if (p.Money < BuyPrice(b.Item)) return "お金が足りません";
                    return HasRoomFor(p, b.Item) ? null : "袋がいっぱいです";
                case Sell s:
                    if (f.Kind != FacilityKind.Shop) return "ここはショップではありません";
                    if (!p.PokemonBag.Contains(s.Item) && !p.TrainerBag.Contains(s.Item)) return "その道具を持っていません";
                    var info = data.FindItem(s.Item);
                    return info != null && info.Sellable && info.Price != null ? null : "その道具は売れません";
                case CenterSwap w:
                    if (f.Kind != FacilityKind.Center) return "ここではポケモンを入れ替えられません";
                    bool inParty = w.PartySlot >= 0 && w.PartySlot < p.Party.Count, inBox = w.BoxIndex >= 0 && w.BoxIndex < p.Box.Count;
                    if (w.PartySlot >= 0 && !inParty || w.BoxIndex >= 0 && !inBox || !inParty && !inBox) return "入れ替えるポケモンを選んでください";
                    if (inParty && !inBox && (p.Party.Count <= 1 || p.Box.Count >= TuneInt("box_max"))) return p.Party.Count <= 1 ? "手持ちを0体にはできません" : "控えがいっぱいです";
                    if (!inParty && inBox && p.Party.Count >= TuneInt("party_size")) return "手持ちがいっぱいです";
                    return null;
                case HoldItem h:
                {
                    if (f.Kind != FacilityKind.Center) return "もちものは ポケモンセンターで 着けはずしします";
                    if (h.Slot < 0 || h.Slot >= p.Party.Count) return "ポケモンを選んでください";
                    if (string.IsNullOrEmpty(h.Item))
                        return p.Party[h.Slot].HeldItem == "" ? "何も持っていません" : p.PokemonBag.Count < BagLimit(p, Bag.Pokemon) ? null : "ポケモンの道具が いっぱいです";
                    if (!p.PokemonBag.Contains(h.Item)) return "その道具を持っていません";
                    return data.FindItem(h.Item)?.IsHeldItem == true ? null : "それは もちものでは ありません";
                }
                case AcceptHeal _:
                    return f.Kind == FacilityKind.FanHeal ? null : "ここでは回復代を払えません";
                case ChangeJob j:
                    if (f.Kind != FacilityKind.Center) return "職業はポケモンセンターで変えます";
                    if (!p.UnlockedJobs.Contains(j.Job)) return "その職業はまだ解放していません";
                    return p.Job == j.Job ? "もう その職業です" : null;
                case LabEvolve l:
                    if (f.Kind != FacilityKind.Center || !f.IsLab) return "進化サービスは研究室だけです";
                    if (l.Slot < 0 || l.Slot >= p.Party.Count) return "進化させるポケモンを選んでください";
                    if (!Pokemons.LabTargets(data, p.Party[l.Slot]).Contains(l.Target)) return "その進化は 研究室では できません";
                    return p.Money >= LabPrice(p) ? null : "お金が足りません";
                case RivalTrade rt:
                {
                    if (f.Kind != FacilityKind.Rival) return "他地方のポケモンとの交換はライバルマスだけです";
                    var trade = RivalTrades(p, f.Square).FirstOrDefault(x => x.species == rt.Species);
                    return trade.species == null ? "その子とは 交換できません（もう交換した か、表にない）" : null; // ケーブルや渡す子がないときは、選んだあとにセリフで知らせる
                }
                case RivalRematch _:
                    if (f.Kind != FacilityKind.Rival || f.Owner != p.Id) return "再戦できるのは そのライバルマスの持ち主だけです";
                    return p.Party.Any(m => !m.Fainted) ? null : "戦える ポケモンが いません";
                case RivalEvolve rv:
                    if (f.Kind != FacilityKind.Rival) return "交換進化はライバルマスだけです";
                    if (rv.Slot < 0 || rv.Slot >= p.Party.Count || !RivalTargets(p.Party[rv.Slot]).Contains(rv.Target)) return "その子は ここでは進化しません";
                    return ActsAsOwner(f, p) || p.PokemonBag.Contains("通信ケーブル") ? null : "通信ケーブルが要ります（ライバルマスの持ち主は無料）";
                case ChooseOption o:
                    if (f.Options.Count == 0) return "今は選ぶものがありません";
                    if (o.Index < 0 || o.Index >= f.Options.Count) return "その番号の選択肢はありません";
                    return CheckRocketOption(f, p, o.Index);
                default:
                    return f.Options.Count == 0 ? null : "選択肢から選んでください"; // LeaveFacility
            }
        }

        void DoFacility(Command c, List<GameEvent> events)
        {
            var f = State.Facility;
            var p = State.Players[f.Player];
            switch (c)
            {
                case Buy b:
                    AddMoney(p, -BuyPrice(b.Item), "買い物", events);
                    GiveItem(p, b.Item, events);
                    return;
                case Sell s:
                    RemoveItem(p, s.Item);
                    events.Add(new ItemLost { Player = p.Id, Item = s.Item });
                    AddMoney(p, SellPrice(s.Item), "売った", events);
                    return;
                case CenterSwap w:
                    PokemonState fromParty = w.PartySlot >= 0 ? p.Party[w.PartySlot] : null, fromBox = w.BoxIndex >= 0 ? p.Box[w.BoxIndex] : null;
                    if (fromParty != null && fromBox != null) { p.Party[w.PartySlot] = fromBox; p.Box[w.BoxIndex] = fromParty; }
                    else if (fromParty != null) { p.Party.RemoveAt(w.PartySlot); p.Box.Add(fromParty); }
                    else { p.Box.RemoveAt(w.BoxIndex); p.Party.Add(fromBox); }
                    events.Add(new Deposited { Player = p.Id, In = fromBox?.Species, Out = fromParty?.Species });
                    return;
                case HoldItem h:
                {
                    var mon = p.Party[h.Slot];
                    string old = mon.HeldItem;
                    if (!string.IsNullOrEmpty(h.Item)) p.PokemonBag.Remove(h.Item);
                    if (old != "") p.PokemonBag.Add(old); // 着けかえなら、前のものは袋へ
                    mon.HeldItem = h.Item ?? "";
                    events.Add(new HeldItemChanged { Player = p.Id, Pokemon = mon.Species, Item = mon.HeldItem, Old = old });
                    return;
                }
                case AcceptHeal _:
                    PayHeal(f, events);
                    break;
                case ChangeJob j:
                    SetJob(p, j.Job, events);
                    return;
                case RivalRematch _:
                    State.Facility = null;
                    if (!RivalBattle(board[f.Square], true, events)) EndTurn(events);
                    return;
                case RivalTrade rt:
                {
                    var trade = RivalTrades(p, f.Square).First(x => x.species == rt.Species);
                    var rv = RivalOf(board[f.Square]);
                    if (!ActsAsOwner(f, p) && !p.PokemonBag.Contains("通信ケーブル")) { Say("rival_no_cable", events, p); return; }
                    if (TradeCandidates(p, trade.need).Count == 0) { Say("rival_trade_short", events, p, ("コスト", trade.need.ToString("0.#"))); return; }
                    Say(rv.Id + "_trade", events, p);
                    var t = new FacilityState { Kind = FacilityKind.Trade, Player = p.Id, Square = f.Square, Owner = f.Owner, EventId = "rival:" + RivalOf(board[f.Square]).Id, PendingItem = rt.Species, Price = trade.level, Prompt = $"{rt.Species}と 交換する子を えらぶ（コスト{trade.need:0.#}以上）" };
                    FillTradeOptions(t, p, trade.need);
                    OpenFacility(t, events);
                    return;
                }
                case RivalEvolve rv:
                {
                    var mon = p.Party[rv.Slot];
                    string before = mon.Species;
                    if (!ActsAsOwner(f, p)) { RemoveItem(p, "通信ケーブル"); events.Add(new ItemUsed { Player = p.Id, Item = "通信ケーブル", Pokemon = before }); }
                    Pokemons.EvolveTo(data, mon, rv.Target);
                    p.Dex.Add(rv.Target);
                    events.Add(new Evolved { Player = p.Id, From = before, To = rv.Target });
                    return;
                }
                case LabEvolve l:
                    var m = p.Party[l.Slot];
                    string from = m.Species;
                    AddMoney(p, -LabPrice(p), "進化サービス", events);
                    Pokemons.EvolveTo(data, m, l.Target);
                    p.Dex.Add(l.Target);
                    events.Add(new Evolved { Player = p.Id, From = from, To = l.Target });
                    return;
                case ChooseOption o:
                    State.Facility = null;
                    if (f.Kind == FacilityKind.RocketBase || f.Kind == FacilityKind.Trade)
                    {
                        if (RocketOption(f, o.Index, events)) return;
                        EndTurn(events);
                        return;
                    }
                    if (f.EventId == MuseumId) { MuseumChosen(f, o.Index, events); EndTurn(events); return; }
                    if (f.EventId == PagerId)
                    {
                        bool wait = PagerChosen(f, o.Index, events, out bool back);
                        if (back) { events.Add(new FacilityClosed { Player = p.Id }); return; }
                        if (wait) return;
                        EndTurn(events);
                        return;
                    }
                    if (ChooseOption(f, o.Index, events)) return; // まだ操作待ち（バトルなど）
                    EndTurn(events);
                    return;
            }
            events.Add(new FacilityClosed { Player = p.Id });
            EndTurn(events);
        }

        // 選択肢を選んだ。続けて操作待ちになるなら true
        bool ChooseOption(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            string choice = f.Options[index];
            if (f.Kind == FacilityKind.JobOffer)
            {
                if (index == 0) SetJob(p, f.PendingJob, events);
                return false;
            }
            if (f.Kind == FacilityKind.BoxFull)
            {
                // 最後の選択肢は「来たポケモンを逃がす」。それ以外は控えのその子を逃がして入れ替える（6.1）
                if (index == f.Options.Count - 1) { events.Add(new PokemonReleased { Player = p.Id, Pokemon = f.PendingPokemon.Species }); return false; }
                events.Add(new PokemonReleased { Player = p.Id, Pokemon = p.Box[index].Species });
                p.Box[index] = f.PendingPokemon;
                ApplyJobHp(p);
                return false;
            }
            if (f.Kind == FacilityKind.BagFull)
            {
                bool take = index < f.Options.Count - 1;
                if (take)
                {
                    RemoveItem(p, choice);
                    events.Add(new ItemDiscarded { Player = p.Id, Item = choice });
                    PutItem(p, f.PendingItem);
                    events.Add(new ItemGained { Player = p.Id, Item = f.PendingItem });
                }
                else events.Add(new ItemDiscarded { Player = p.Id, Item = f.PendingItem, NotTaken = true });
                return false;
            }
            var e = data.RandomEvents.Concat(data.EventSquares.Select(x => x.Effect)).FirstOrDefault(x => x.Id == f.EventId);
            if (e == null) return false;
            if (e.Kind == EffectKind.UnlockJob)
            {
                if (index >= f.Options.Count - 1) { Say(e.TextKey + "_no", events, p); return false; }
                UnlockJob(p, choice, events);
                return OfferJob(p, choice, events);
            }
            if (index != 0) { Say(e.TextKey + "_no", events, p); return false; }
            Say(e.TextKey + "_yes", events, p);
            return ApplyEffect(e, events);
        }
    }
}
