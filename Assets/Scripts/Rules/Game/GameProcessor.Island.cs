using System.Collections.Generic;
using System.Linq;

namespace Sugoroku.Rules
{
    // 謎のチケット・不思議なチケットと島マップ（仕様書3.4、12章、設計メモ/チケットの設定.md）
    // クチバの船着場でチケットを使う → チケットの行き先.csv の重みで行き先を抽選 → その島マップの港へ
    // 伝説の島では、引いた伝説がシンボルのマスに1体だけ現れる（チケットのたびに別の個体）。勝つか捕まえると いなくなる
    // 島の港に止まるか、ticket_island_turns 手番を過ぎると クチバの船着場へ戻る（バトルの途中なら、終わってから）
    // 島はその人だけのエリア：妨害は届かず、同じマスでもプレイヤー戦にならない
    public sealed partial class GameProcessor
    {
        const string PortId = "船着場";
        public const string MysteryTicket = "謎のチケット", WonderTicket = "不思議なチケット";

        public bool OnIsland(PlayerState p) => data.IsIslandMap(board[p.Square].MapId);

        static bool HasItem(PlayerState p, string item) => p.TrainerBag.Contains(item) || p.PokemonBag.Contains(item);

        // 本マップの船着場（クチバ）。なければ -1
        int MainPort() => board.Squares.Values.FirstOrDefault(s => s.Type == "船着場" && !data.IsIslandMap(s.MapId))?.Id ?? -1;

        // その島の港。なければ -1
        int IslandPort(string island) => board.Squares.Values.FirstOrDefault(s => s.Type == "船着場" && s.Place == island)?.Id ?? -1;

        // 船着場に止まった：本マップならチケットを使うか選ぶ、島の港なら クチバに戻る
        bool LandPort(BoardSquare sq, List<GameEvent> events)
        {
            var p = Current;
            if (data.IsIslandMap(sq.MapId)) { ReturnFromIsland(p, "港から 船で もどった", events); return false; }
            var tickets = new[] { MysteryTicket, WonderTicket }.Where(t => HasItem(p, t)).ToList();
            if (tickets.Count == 0) return Nothing("船に乗るには チケットが いる", events);
            var f = new FacilityState { Kind = FacilityKind.Event, Player = p.Id, Square = sq.Id, EventId = PortId, Prompt = "どのチケットで 船に乗る？" };
            foreach (var t in tickets) { f.Options.Add($"{t}を 使う"); f.Goods.Add(t); }
            f.Options.Add("使わない");
            return OpenFacility(f, events);
        }

        // 使うチケットを選んだ → 行き先を抽選して島へ。まだ操作待ちなら true
        bool PortChosen(FacilityState f, int index, List<GameEvent> events)
        {
            var p = State.Players[f.Player];
            if (index >= f.Goods.Count) return false; // 使わない
            string ticket = f.Goods[index];
            var rows = data.TicketRows.Where(r => r.Ticket == ticket && r.Weight > 0 && IslandPort(r.Island) >= 0).ToList();
            if (rows.Count == 0) return Nothing("この チケットの 船は まだ 出ていない", events); // 島のマスがまだない
            var row = Pick(rows, r => r.Weight);
            RemoveItem(p, ticket);
            events.Add(new ItemUsed { Player = p.Id, Item = ticket });
            int from = p.Square;
            p.Square = IslandPort(row.Island);
            p.IslandTurns = 0;
            p.IslandLegend = row.Kind == "伝説" ? row.Target : "";
            p.IslandLegendLevel = row.Level;
            events.Add(new SailedToIsland { Player = p.Id, From = from, To = p.Square, Island = row.Island, Legend = p.IslandLegend });
            return false;
        }

        void ReturnFromIsland(PlayerState p, string reason, List<GameEvent> events)
        {
            int from = p.Square, to = MainPort();
            p.Square = to >= 0 ? to : board.StartSquare;
            p.IslandLegend = "";
            p.IslandTurns = 0;
            events.Add(new ReturnedFromIsland { Player = p.Id, From = from, To = p.Square, Reason = reason });
        }

        // 手番の始め：島にいる手番を数え、過ぎていたら戻す。島にいないのに伝説が残っていたら消す（全滅・あなぬけひもなどで出たとき）
        void IslandTurnStart(PlayerState p, List<GameEvent> events)
        {
            if (!OnIsland(p)) { p.IslandLegend = ""; p.IslandTurns = 0; return; }
            p.IslandTurns++;
            if (p.IslandTurns > TuneInt("ticket_island_turns") && p.PendingBattle == null) ReturnFromIsland(p, "島に いられる 時間が すぎた", events);
        }

        // シンボルのマス：島なら自分の伝説（チケット）、それ以外は盤面の伝説のシンボル。いればバトル（クイック、ボールを投げられる）
        bool LandSymbol(List<GameEvent> events)
        {
            var p = Current;
            var sq = board[p.Square];
            if (!data.IsIslandMap(sq.MapId))
            {
                // 盤面の伝説（仕様書の シンボルエンカウント）：早い者勝ち。倒すか捕まえると消え、逃げたり負けたりしたら残る
                string legend = SymbolHere(sq.Id);
                if (legend == null) return Nothing("ここには もう 何も いない", events);
                StartBattle(BattleKind.Wild, false, Npc($"野生の{legend}", new[] { (legend, SymbolLevel(sq, legend)) }), events);
                if (State.Battle != null) State.Battle.Symbol = true;
                return true;
            }
            if (!OnIsland(p) || p.IslandLegend == "") return Nothing("ここには 何も いない", events);
            StartBattle(BattleKind.Wild, false, Npc($"野生の{p.IslandLegend}", new[] { (p.IslandLegend, p.IslandLegendLevel) }), events);
            if (State.Battle != null) State.Battle.Legend = true;
            return true;
        }

        // そのマスに いまいる盤面の伝説（マスに書いたポケモン、なければ その場所の出現表「シンボル」）。いなければ null
        public string SymbolHere(int square)
        {
            var sq = board[square];
            if (sq.Type != "シンボル" || data.IsIslandMap(sq.MapId) || State.SymbolsGone.Contains(square)) return null;
            string species = sq.Symbol != "" ? sq.Symbol : data.Encounters.FirstOrDefault(e => e.Place == sq.Place && e.Method == "シンボル")?.Pokemon;
            return string.IsNullOrEmpty(species) ? null : species;
        }

        // 伝説のLv：出現表に その場所の シンボルの行があれば そのLv、なければ その場所の野生の最高Lv＋5
        int SymbolLevel(BoardSquare sq, string species)
        {
            var row = data.Encounters.FirstOrDefault(e => e.Place == sq.Place && e.Method == "シンボル" && e.Pokemon == species);
            if (row != null) return Roll(row.MinLevel, row.MaxLevel);
            int wild = data.Encounters.Where(e => e.Place == sq.Place && e.Method != "シンボル" && e.Method != "固定").Select(e => e.MaxLevel).DefaultIfEmpty(30).Max();
            return System.Math.Min(wild + 5, TuneInt("level_max"));
        }

        // 伝説のシンボルを倒した・捕まえた：盤面から消す
        void SymbolBeaten(int player, string species, bool caught, List<GameEvent> events)
        {
            State.SymbolsGone.Add(B.Square);
            events.Add(new SymbolGone { Square = B.Square, Player = player, Pokemon = species, Caught = caught });
        }

        // 謎のチケットが袋に入った：不思議なチケットに変わるまでの週数を決める
        void TicketReceived(PlayerState p, string item)
        {
            if (item != MysteryTicket || p.TicketChangeWeeks > 0) return;
            p.TicketChangeWeeks = Roll(TuneInt("ticket_change_weeks_min"), TuneInt("ticket_change_weeks_max"));
            p.TicketLastWeeks = 0;
        }

        // 週の終わり（最下位の判定のあと）：謎のチケットの変化と、下位救済の謎のチケット（12章）
        void TicketWeekEnd(double worst, List<GameEvent> events)
        {
            foreach (var p in State.Players)
            {
                bool last = Score(p) <= worst + 1e-9;
                if (!HasItem(p, MysteryTicket)) p.TicketChangeWeeks = 0;
                else if (p.TicketChangeWeeks > 0 && last && ++p.TicketLastWeeks >= p.TicketChangeWeeks)
                {
                    RemoveItem(p, MysteryTicket);
                    PutItem(p, WonderTicket);
                    p.TicketChangeWeeks = 0;
                    events.Add(new TicketChanged { Player = p.Id });
                }
                // 下位救済：バッジがそろっていて、ちょうど決まった週数だけ最下位が続いたら（1回の連続で1枚）
                if (p.Badges >= TuneInt("ticket_rescue_badges") && p.LastPlaceWeeks == TuneInt("ticket_rescue_weeks") && HasRoomFor(p, MysteryTicket))
                {
                    PutItem(p, MysteryTicket);
                    events.Add(new RescueGiven { Player = p.Id, Item = MysteryTicket });
                }
            }
        }
    }
}
