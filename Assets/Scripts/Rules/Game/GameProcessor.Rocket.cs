using System;
using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // ロケット団マス（仕様書11.3）。持ち主がいなければ団員とクイックバトル、勝つとそのマスと エリア内のトレーナーマス2つ（初回だけ）が自分のものに
    // 持ち主がいれば拠点のイベント（ロケット団拠点.csv）：買う・もらう・交換。持ち主（とへんそう中の人）は割引。ほかの人は挑んで奪える
    // 拠点ごとの決まり（橋のきんのたま、タワーののろい、どろぼうの家の品ぞろえ）は備考にしかないので、ここに直接書いてある
    public sealed partial class GameProcessor
    {
        const string RocketBridge = "rb_bridge", RocketTower = "rb_tower", RocketThief = "rb_cerulean";
        const string Challenge = "#challenge";

        // 拠点の文言キー：rb_mtmoon → ev_rocket_mtmoon
        static string RocketKey(string baseId) => "ev_rocket_" + baseId.Substring(3);

        List<RocketBaseRow> RocketRows(BoardSquare sq) => data.RocketBases.Where(r => r.Place == sq.Place).ToList();

        bool LandRocket(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            if (!State.Owners.TryGetValue(sq.Id, out int owner)) return StartRocketBattle(sq, events);
            return OpenRocketBase(p, sq, owner, true, events);
        }

        // 団員とのバトル。相手はトレーナー.csv の 種類「ロケット団」（マス、なければ場所で探す）。なければ場所の野生の最高Lv＋1
        bool StartRocketBattle(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            var grunts = data.Trainers.Where(t => t.Kind == "ロケット団" && (t.Square == sq.Name || (t.Square == "" && t.Place == sq.Place))).ToList();
            var entry = grunts.Count == 0 ? null : grunts[Roll(0, grunts.Count - 1)];
            if (!TrainerMon(sq, entry, out var mon)) return Nothing("ロケット団は 留守だった", events);
            string name = entry != null && entry.Name != "" ? entry.Name : "ロケット団員";
            string key = TrainerDialogKey(entry);
            Say(key, events, p, ("名前", name), ("ポケモン", mon.species));
            StartBattle(BattleKind.Rocket, false, Npc(name, new[] { mon }), events);
            B.TrainerId = entry?.Id;
            B.DialogKey = key;
            return true;
        }

        // ロケット団に勝った：マスが自分のものに。はじめて倒したマスなら、エリア内のトレーナーマスを2つ（ほかの人のものでも）
        void RocketCleared(PlayerState p, List<GameEvent> events)
        {
            var b = B;
            Say(b.DialogKey + "_win", events, p);
            bool first = State.RocketCleared.Add(b.Square);
            SetOwner(b.Square, p.Id, events);
            var gained = new List<int>();
            if (first)
            {
                var pool = board.Squares.Values.Where(s => s.Type == "トレーナー" && s.Area == board[b.Square].Area && !(State.Owners.TryGetValue(s.Id, out int o) && o == p.Id)).Select(s => s.Id).ToList();
                for (int i = 0; i < TuneInt("rocket_reward_trainers") && pool.Count > 0; i++)
                {
                    int k = Roll(0, pool.Count - 1);
                    SetOwner(pool[k], p.Id, events);
                    gained.Add(pool[k]);
                    pool.RemoveAt(k);
                }
            }
            events.Add(new RocketDefeated { Player = p.Id, Square = b.Square, First = first, Trainers = gained });
        }

        // 拠点を開く。greet は止まったとき（あいさつ・もらう・のろい解き）。買ったあとの開き直しでは false
        bool OpenRocketBase(PlayerState p, BoardSquare sq, int owner, bool greet, List<GameEvent> events)
        {
            var rows = RocketRows(sq);
            if (rows.Count == 0) return Nothing("ロケット団の 拠点は からっぽだった（ロケット団拠点.csv に この場所の行がない）", events);
            string baseId = rows[0].BaseId, key = RocketKey(baseId);
            bool asOwner = owner == p.Id || p.Has("へんそう");
            var f = new FacilityState { Kind = FacilityKind.RocketBase, Player = p.Id, Square = sq.Id, Owner = owner, EventId = baseId };
            if (greet)
            {
                // もらう：アイテム（橋のきんのたま）は ほかの人は1人1回、持ち主は止まるたびに確率で。ポケモン（タワーのヒトモシ）は だれでも1人1回
                var gifts = new List<RocketBaseRow>();
                bool usedUp = false;
                foreach (var r in rows.Where(r => r.Mode == "もらう"))
                {
                    bool ok = asOwner && r.Kind == "アイテム" ? Chance(Tune("rocket_bridge_owner_rate")) : !p.RocketUsed.Contains(r.Id);
                    if (!ok) { usedUp = true; continue; }
                    if (!(asOwner && r.Kind == "アイテム")) p.RocketUsed.Add(r.Id);
                    gifts.Add(r);
                }
                // 1人1回の品を使い切っていれば「もう やっただろ」のほう
                if (gifts.Count == 0 && (usedUp || rows.Any(r => r.Once && r.Mode != "もらう" && p.RocketUsed.Contains(r.Id))) && data.HasDialog(key + "_used")) Say(key + "_used", events, p);
                else Say(key, events, p);
                foreach (var r in gifts)
                {
                    if (r.Kind == "アイテム") { if (GiveItem(p, r.Item, events)) return true; continue; } // 袋がいっぱい：捨てる物を選んでから（拠点は閉じる）
                    var mon = Pokemons.Create(data, r.Item, r.Level ?? 1, $"{r.BaseName}で もらった", sq.Place, State.Week);
                    p.Dex.Add(r.Item);
                    events.Add(new PokemonReceived { Player = p.Id, Pokemon = r.Item, From = r.BaseName, ToBox = p.Party.Count >= TuneInt("party_size") });
                    if (AddPokemon(p, mon, events)) return true;
                }
                // タワー：のろいは無料で解いてもらえる
                if (baseId == RocketTower && p.Has("のろい")) { p.Ailments.RemoveAll(a => a.Name == "のろい"); Say(key + "_curse", events, p); events.Add(new AilmentCured { Player = p.Id, Name = "のろい" }); }
            }
            // 並ぶ品：どろぼうの家は止まるたびに2つだけ（手番の間は同じ2つ）
            var goods = rows.Where(r => r.Mode != "もらう" && !(r.Once && p.RocketUsed.Contains(r.Id))).ToList();
            if (baseId == RocketThief && greet) { State.RocketShelf = goods.OrderBy(_ => Roll(0, 1 << 20)).Take(TuneInt("rocket_thief_shelf")).Select(r => r.Id).ToList(); }
            if (baseId == RocketThief) goods = goods.Where(r => State.RocketShelf.Contains(r.Id)).ToList();
            foreach (var r in goods)
            {
                f.Goods.Add(r.Id);
                f.Options.Add(r.Mode == "交換" ? $"交換：{r.Item}（コスト{RocketCost(r, asOwner):0.#}以上の子を 1体わたす）" : $"買う：{r.Item} {RocketPrice(r, asOwner):#,0}円" + (r.Once ? "（1人1回）" : ""));
            }
            if (!asOwner) { f.Goods.Add(Challenge); f.Options.Add("ロケット団に 挑む（勝てば このマスが 自分のものに）"); }
            f.Goods.Add(""); f.Options.Add("立ち去る");
            f.Prompt = $"{rows[0].BaseName}" + (asOwner ? "（自分の拠点：割引）" : owner >= 0 ? $"（{State.Players[owner].Name}の拠点）" : "") + $"　所持金 {p.Money:#,0}円";
            return OpenFacility(f, events);
        }

        int RocketPrice(RocketBaseRow r, bool asOwner) => asOwner ? r.OwnerPrice ?? (int)((r.Price ?? 0) * Tune("owner_discount")) : r.Price ?? 0;
        double RocketCost(RocketBaseRow r, bool asOwner) => asOwner ? r.OwnerCost ?? r.Cost ?? 0 : r.Cost ?? 0;
        RocketBaseRow RocketRow(string id) => data.RocketBases.FirstOrDefault(r => r.Id == id);

        // 渡せる子：コストが need 以上。手持ちからは最後の1体は出せない。"party:i"／"box:i"
        List<(string key, PokemonState mon)> TradeCandidates(PlayerState p, double need)
        {
            var list = new List<(string, PokemonState)>();
            for (int i = 0; i < p.Party.Count; i++) if (p.Party.Count > 1 && Pokemons.Cost10(data, p.Party[i]) >= need * 10) list.Add(("party:" + i, p.Party[i]));
            for (int i = 0; i < p.Box.Count; i++) if (Pokemons.Cost10(data, p.Box[i]) >= need * 10) list.Add(("box:" + i, p.Box[i]));
            return list;
        }

        void FillTradeOptions(FacilityState t, PlayerState p, double need)
        {
            foreach (var (k, m) in TradeCandidates(p, need)) { t.Goods.Add(k); t.Options.Add((k.StartsWith("box:") ? "控え " : "") + $"{m.Species} Lv{m.Level}（コスト{Pokemons.Cost10(data, m) / 10.0:0.#}）"); }
            t.Goods.Add(""); t.Options.Add("やめる");
        }

        // 渡す子を選んだ（拠点の交換・ライバルの交換）。EventId が "rival:" ならライバルマス
        bool TradeOption(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            var sq = board[f.Square];
            bool rival = f.EventId.StartsWith("rival:");
            string pick = index < f.Goods.Count ? f.Goods[index] : "";
            bool Back() => rival ? OpenRival(p, sq, f.Owner, events) : OpenRocketBase(p, sq, f.Owner, false, events);
            if (pick == "") return Back(); // やめる
            var list = pick.StartsWith("box:") ? p.Box : p.Party;
            var give = list[int.Parse(pick.Substring(pick.IndexOf(':') + 1))];
            list.Remove(give);
            if (give.HeldItem != "") { p.PokemonBag.Add(give.HeldItem); give.HeldItem = ""; } // もちものは外れて残る（8.2）
            string from = rival ? RivalOf(sq).Name : RocketRows(sq)[0].BaseName;
            events.Add(new PokemonGiven { Player = p.Id, Pokemon = give.Species, To = from });
            var got = Pokemons.Create(data, f.PendingItem, f.Price, $"{from}と 交換", sq.Place, State.Week);
            if (rival)
            {
                p.RocketUsed.Add("rival:" + f.PendingItem); // ライバルの交換も1人1回ずつ
                if (!ActsAsOwner(f, p)) { RemoveItem(p, "通信ケーブル"); events.Add(new ItemUsed { Player = p.Id, Item = "通信ケーブル", Pokemon = f.PendingItem }); }
            }
            else
            {
                var r = data.RocketBases.First(x => x.Place == sq.Place && x.Item == f.PendingItem && x.Mode == "交換");
                p.RocketUsed.Add(r.Id);
                Say(RocketKey(r.BaseId) + "_trade_ok", events, p, ("ポケモン", r.Item));
            }
            p.Dex.Add(f.PendingItem);
            events.Add(new PokemonReceived { Player = p.Id, Pokemon = f.PendingItem, From = from, ToBox = p.Party.Count >= TuneInt("party_size") });
            if (AddPokemon(p, got, events)) return true;
            return Back();
        }

        // 選択肢が選べるか（CheckFacility の ChooseOption から）
        string CheckRocketOption(FacilityState f, PlayerState p, int index)
        {
            if (f.Kind != FacilityKind.RocketBase || index >= f.Goods.Count) return null;
            var r = RocketRow(f.Goods[index]);
            if (r == null || r.Mode != "買う") return null;
            if (p.Money < RocketPrice(r, f.Owner == p.Id || p.Has("へんそう"))) return "お金が足りません";
            return r.Kind == "アイテム" && !HasRoomFor(p, r.Item) ? "袋がいっぱいです" : null;
        }

        // 拠点・交換の選択肢を選んだ。まだ操作待ち（拠点を開き直した、バトル、袋いっぱい）なら true
        bool RocketOption(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            var sq = board[f.Square];
            bool asOwner = f.Owner == p.Id || p.Has("へんそう");
            string key = RocketKey(f.EventId);
            string pick = index < f.Goods.Count ? f.Goods[index] : "";
            if (f.Kind == FacilityKind.Trade) return TradeOption(f, index, events);
            if (pick == "") return false;                                   // 立ち去る
            if (pick == Challenge) return StartRocketBattle(sq, events);    // ほかの人の拠点を奪いに
            var row = RocketRow(pick);
            if (row.Mode == "交換")
            {
                double need = RocketCost(row, asOwner);
                var t = new FacilityState { Kind = FacilityKind.Trade, Player = p.Id, Square = sq.Id, Owner = f.Owner, EventId = f.EventId, PendingItem = row.Item, Price = row.Level ?? 1, Prompt = $"{row.Item}と 交換する子を えらぶ（コスト{need:0.#}以上）" };
                if (TradeCandidates(p, need).Count == 0) { Say("ev_rocket_trade_short", events, p, ("コスト", need.ToString("0.#"))); return OpenRocketBase(p, sq, f.Owner, false, events); }
                FillTradeOptions(t, p, need);
                Say(key + "_trade", events, p);
                return OpenFacility(t, events);
            }
            // 買う
            AddMoney(p, -RocketPrice(row, asOwner), "買い物", events);
            if (row.Once) p.RocketUsed.Add(row.Id);
            if (row.Kind == "ポケモン")
            {
                var mon = Pokemons.Create(data, row.Item, row.Level ?? 1, $"{row.BaseName}で 買った", sq.Place, State.Week);
                p.Dex.Add(row.Item);
                events.Add(new PokemonReceived { Player = p.Id, Pokemon = row.Item, From = row.BaseName, ToBox = p.Party.Count >= TuneInt("party_size") });
                if (AddPokemon(p, mon, events)) return true;
            }
            else if (GiveItem(p, row.Item, events)) return true;
            return OpenRocketBase(p, sq, f.Owner, false, events);
        }
    }
}
