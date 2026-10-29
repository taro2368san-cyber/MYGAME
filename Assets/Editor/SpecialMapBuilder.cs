using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// メニュー「Tools > すごろく > 別マップを作る > ○○」：別マップ（3.4）の板・マス・道を置く。
// 板は Assets/maps の絵（16pxのマス目）。マスは下の表の「マス目の位置」に置き、歩ける所（Floor の見本のマス目に近い色。海上マスは水）の近くにずらす。
// もう一度実行すると、そのマップだけ（Board/<Parent> と その mapId の板）を消して作り直す。手で直したマスも消えるので注意
public static class SpecialMapBuilder
{
    const string SquarePrefab = "Assets/Prefabs/Square.prefab", RoadPrefab = "Assets/Prefabs/Road.prefab";
    const string TemplateMaterial = "Assets/maps/Mt. Moon/Materials/1.mat"; // おつきみ山と同じ材質

    // 1枚の板。origin は絵の左上の位置（x, z）。scale は 絵の1pxの長さ
    // Floor：床の見本のマス目「列,段;列,段」。この色に近いマス目を歩ける所とみなす（空ならタワー・サファリの色の決まりを使う）
    sealed class MapDef
    {
        public string Key, Name, Image, Place, MapId, Parent, Prefix; public float Scale; public Vector2 Origin; public bool Tower;
        public string Nodes, Edges, Floor;
        public bool Main; // 本マップ：板は作らず、Image の絵を貼った板（置いてあるもの）の上にマスを置く。Origin・Scale はその板から決める
    }

    // マスの書き方「番号:列,段:種類」。種類 空=空マス 草=草むら 海=海上 ト=トレーナー ボ=モンスターボール ラ=ライバル ロ=ロケット団 イ=イベントマス ワN=ワープN番 シ=伝説のシンボル（番号のところに ポケモンの名前）
    // セ=ポケモンセンター ジ=ジム 店<ショップid>=ショップ（例：店shop_guren）
    static readonly MapDef[] Maps =
    {
        // ポケモンタワー：シオンタウンのワープ8から入る行き止まり。4階（絵は 1・3・5・7 を使う）
        new MapDef { Key = "T1", Name = "Pokemon Tower 1F", Image = "Assets/maps/Pokemon Tower/1.png", Place = "ポケモンタワー", MapId = "tower", Parent = "PokemonTower", Prefix = "PT_1F_", Scale = 0.1f, Origin = new Vector2(0, 440), Tower = true,
            Nodes = "01:11,17:ワ8 02:11,14:空 03:6,12:空 04:5,8:ト 05:8,4:空 06:13,3:ラ 07:17,6:空 08:18,9:空 09:11,9:ボ",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 02-09 09-08" },
        new MapDef { Key = "T2", Name = "Pokemon Tower 2F", Image = "Assets/maps/Pokemon Tower/3.png", Place = "ポケモンタワー", MapId = "tower", Parent = "PokemonTower", Prefix = "PT_2F_", Scale = 0.1f, Origin = new Vector2(42, 440), Tower = true,
            Nodes = "01:4,8:空 02:5,12:草 03:7,14:ト 04:12,14:草 05:17,14:空 06:17,10:草 07:18,7:空 08:13,5:草 09:7,5:ボ",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 01-09 09-08 08-07" },
        new MapDef { Key = "T3", Name = "Pokemon Tower 3F", Image = "Assets/maps/Pokemon Tower/5.png", Place = "ポケモンタワー", MapId = "tower", Parent = "PokemonTower", Prefix = "PT_3F_", Scale = 0.1f, Origin = new Vector2(84, 440), Tower = true,
            Nodes = "01:3,12:空 02:4,15:草 03:9,16:ト 04:14,15:草 05:18,12:草 06:18,9:空 07:12,6:草 08:5,6:ボ 09:9,9:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 01-08 08-07 07-06 03-09 09-07" },
        new MapDef { Key = "T4", Name = "Pokemon Tower 7F", Image = "Assets/maps/Pokemon Tower/7.png", Place = "ポケモンタワー", MapId = "tower", Parent = "PokemonTower", Prefix = "PT_7F_", Scale = 0.1f, Origin = new Vector2(126, 456), Tower = true,
            Nodes = "01:12,16:空 02:10,13:空 03:10,10:ト 04:10,7:ロ 05:10,4:空",
            Edges = "01-02 02-03 03-04 04-05" },

        // サファリゾーン：中央（入口はワープ9。セキチクシティに同じ番号のワープを置くとつながる）・西・北・東。小さい建物の中の絵（5・6）は使わない
        new MapDef { Key = "C", Name = "Safari Zone Center", Image = "Assets/maps/Safari Zone/1.png", Place = "サファリゾーン", MapId = "safari", Parent = "SafariZone", Prefix = "SZ_C_", Scale = 0.05f, Origin = new Vector2(300, 520),
            Nodes = "01:25,29:ワ9 02:25,26:空 03:19,26:草 04:14,25:草 05:11,21:空 06:10,18:空 07:3,17:空 08:11,14:草 09:14,9:草 10:19,8:草 11:24,9:空 12:25,6:空 13:25,2:空 14:30,9:草 15:36,9:草 16:40,12:空 17:42,15:空 18:48,15:空 19:38,20:草 20:37,25:草 21:31,27:ボ 22:30,21:イ 23:20,17:海 24:15,18:空 25:32,17:海",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 05-24 24-08 24-23 08-09 09-10 10-11 11-12 12-13 11-14 14-15 15-16 16-17 17-18 17-19 19-20 20-21 21-02 02-22 22-19 19-25" },
        new MapDef { Key = "W", Name = "Safari Zone West", Image = "Assets/maps/Safari Zone/3.png", Place = "サファリゾーン", MapId = "safari", Parent = "SafariZone", Prefix = "SZ_W_", Scale = 0.05f, Origin = new Vector2(259.6f, 520),
            Nodes = "01:46,26:空 02:41,26:空 03:36,24:空 04:34,20:空 05:37,16:空 06:38,10:空 07:38,4:空 08:31,8:空 09:24,9:空 10:19,9:草 11:13,9:イ 12:8,11:空 13:8,14:空 14:12,17:海 15:12,21:空 16:9,25:草 17:13,27:草 18:18,28:草 19:23,27:草 20:28,28:草 21:32,28:空 22:20,21:ボ 23:28,20:空 24:24,13:草 25:17,13:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 06-08 08-09 09-10 10-11 11-12 12-13 13-14 13-15 15-16 16-17 17-18 18-19 19-20 20-21 21-03 15-22 22-23 23-04 09-24 24-25 25-22" },
        new MapDef { Key = "N", Name = "Safari Zone North", Image = "Assets/maps/Safari Zone/4.png", Place = "サファリゾーン", MapId = "safari", Parent = "SafariZone", Prefix = "SZ_N_", Scale = 0.05f, Origin = new Vector2(296, 554),
            Nodes = "01:30,37:空 02:30,32:空 03:33,28:草 04:38,24:空 05:43,30:空 06:53,31:空 07:46,22:空 08:45,15:空 09:44,10:ボ 10:38,9:草 11:36,5:草 12:30,7:空 13:25,6:空 14:20,9:草 15:14,8:草 16:10,11:草 17:10,16:空 18:10,22:空 19:10,28:空 20:10,36:空 21:14,20:海 22:22,22:草 23:23,29:草 24:28,18:草 25:26,13:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 04-07 07-08 08-09 08-10 10-11 11-12 12-13 13-14 14-15 15-16 16-17 17-18 18-19 19-20 18-21 18-22 22-23 23-02 22-24 24-25 25-12 24-03" },
        new MapDef { Key = "E", Name = "Safari Zone East", Image = "Assets/maps/Safari Zone/2.png", Place = "サファリゾーン", MapId = "safari", Parent = "SafariZone", Prefix = "SZ_E_", Scale = 0.05f, Origin = new Vector2(342.8f, 528.8f),
            Nodes = "01:2,9:空 02:9,9:空 03:12,6:空 04:18,7:草 05:25,6:草 06:32,6:草 07:39,7:草 08:44,10:空 09:42,16:ボ 10:44,22:空 11:41,27:草 12:35,27:草 13:29,28:草 14:22,29:空 15:14,28:空 16:9,27:空 17:2,26:空 18:14,20:草 19:15,15:草 20:20,14:草 21:24,19:空 22:30,17:海 23:19,24:空 24:38,20:空 25:11,13:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-24 24-10 10-11 11-12 12-13 13-14 14-15 15-16 16-17 02-25 25-19 19-20 20-21 21-22 22-24 21-23 23-14 19-18 18-23" },

        // ディグダの穴：2番道路（ワープ6）と11番道路（ワープ7）をつなぐ1本道
        new MapDef { Key = "DG", Name = "Diglett's Cave", Image = "Assets/maps/Diglett's Cave/1.png", Place = "ディグダの穴", MapId = "diglett", Parent = "DiglettsCave", Prefix = "DG_", Scale = 0.04f, Origin = new Vector2(0, 640), Floor = "3,7;43,38;30,22",
            Nodes = "01:3,5:ワ6 02:8,9:空 03:11,14:草 04:15,19:空 05:24,21:草 06:32,26:ト 07:37,33:草 08:42,39:空 09:50,43:草 10:57,50:ボ 11:65,52:草 12:71,59:空 13:72,66:草 14:78,71:ワ7",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10 10-11 11-12 12-13 13-14" },

        // イワヤマトンネル：10番道路の北（ワープ101）と南（ワープ102）をつなぐ。地下1階をまわる道もある
        new MapDef { Key = "RT1", Name = "Rock Tunnel 1F", Image = "Assets/maps/Rock Tunnel/1.png", Place = "イワヤマトンネル", MapId = "rocktunnel", Parent = "RockTunnel", Prefix = "RT_1F_", Scale = 0.05f, Origin = new Vector2(60, 620), Floor = "18,21;13,18;5,30",
            Nodes = "01:17,3:ワ101 02:20,6:空 03:28,5:草 04:36,7:空 05:44,4:空 06:26,11:草 07:18,14:ト 08:13,19:草 09:5,22:空 10:4,29:草 11:16,29:ボ 12:26,29:草 13:18,34:空 14:19,37:ワ102",
            Edges = "01-02 02-03 03-04 04-05 02-06 06-07 07-08 08-09 09-10 10-11 11-12 11-13 12-13 13-14" },
        new MapDef { Key = "RT2", Name = "Rock Tunnel B1F", Image = "Assets/maps/Rock Tunnel/2.png", Place = "イワヤマトンネル", MapId = "rocktunnel", Parent = "RockTunnel", Prefix = "RT_B1F_", Scale = 0.05f, Origin = new Vector2(102, 620), Floor = "10,8;17,10;3,15",
            Nodes = "01:38,3:空 02:42,10:草 03:40,15:空 04:33,13:ト 05:24,12:草 06:16,13:空 07:8,12:草 08:3,19:ボ 09:9,28:草 10:22,29:空 11:35,29:草 12:40,36:空 13:22,37:草 14:38,28:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10 10-11 11-14 11-12 12-13 13-10" },

        // ハナダの洞窟：ハナダシティから入る（ワープ103）。地下1階の奥に ミュウツーのシンボル
        new MapDef { Key = "CC1", Name = "Cerulean Cave 1F", Image = "Assets/maps/Cerulean Cave/1.png", Place = "ハナダの洞窟", MapId = "ceruleancave", Parent = "CeruleanCave", Prefix = "CC_1F_", Scale = 0.06f, Origin = new Vector2(146, 612), Floor = "6,9;20,18",
            Nodes = "01:33,21:ワ103 02:33,16:空 03:28,13:草 04:24,17:空 05:18,19:草 06:8,19:空 07:5,14:草 08:3,8:ト 09:10,6:草 10:11,2:空 11:22,7:草",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10 03-11" },
        new MapDef { Key = "CC2", Name = "Cerulean Cave B1F", Image = "Assets/maps/Cerulean Cave/3.png", Place = "ハナダの洞窟", MapId = "ceruleancave", Parent = "CeruleanCave", Prefix = "CC_B1F_", Scale = 0.06f, Origin = new Vector2(188, 612), Floor = "6,4;20,12",
            Nodes = "01:6,6:空 02:12,6:草 03:17,8:空 04:22,10:草 05:28,8:ト 06:34,6:草 07:34,14:空 08:29,13:草 09:23,17:草 ミュウツー:4,2:シ",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 01-ミュウツー" },

        // むじんはつでんしょ：10番道路から入る（ワープ104）。奥に サンダーのシンボル
        new MapDef { Key = "PP", Name = "Power Plant", Image = "Assets/maps/Power Plant/1.png", Place = "むじんはつでんしょ", MapId = "powerplant", Parent = "PowerPlant", Prefix = "PP_", Scale = 0.05f, Origin = new Vector2(230, 620), Floor = "10,12;25,20;40,33",
            Nodes = "01:5,37:ワ104 02:5,33:空 03:4,27:草 04:4,19:空 05:8,14:草 06:5,6:ボ 07:15,5:草 08:16,13:空 09:26,13:草 10:24,6:ト 11:37,13:空 12:44,15:草 13:44,26:空 14:35,28:草 15:22,28:ト 16:14,22:草 17:12,34:ボ サンダー:29,35:シ 19:40,35:草",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-05 08-09 09-10 09-11 11-12 12-13 13-14 14-15 15-16 16-04 15-17 17-02 14-サンダー サンダー-19 19-13" },

        // ふたご島：20番水道の西（ワープ105）と東（ワープ106）から入る。1階と、いちばん下の階（フリーザーのシンボル。海上マスは つり用）
        new MapDef { Key = "SF1", Name = "Seafoam Islands 1F", Image = "Assets/maps/Seafoam Islands/1.png", Place = "ふたご島", MapId = "seafoam", Parent = "SeafoamIslands", Prefix = "SF_1F_", Scale = 0.06f, Origin = new Vector2(0, 690), Floor = "8,16;8,8;20,10",
            Nodes = "01:6,22:ワ105 02:6,18:空 03:8,11:草 04:5,5:ボ 05:13,4:空 06:22,4:空 07:20,9:空 08:17,15:草 09:13,18:草 10:30,4:空 11:33,10:草 12:28,16:ト 13:32,19:空 14:32,22:ワ106",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-10 10-11 11-12 12-13 13-14 02-09 09-08 08-07 07-06" },
        new MapDef { Key = "SF2", Name = "Seafoam Islands B4F", Image = "Assets/maps/Seafoam Islands/5.png", Place = "ふたご島", MapId = "seafoam", Parent = "SeafoamIslands", Prefix = "SF_B4F_", Scale = 0.06f, Origin = new Vector2(42, 690), Floor = "20,8;25,3",
            Nodes = "01:17,9:空 02:19,4:空 03:25,3:草 04:31,2:空 05:12,11:草 06:19,14:空 07:15,18:草 08:22,19:草 フリーザー:10,2:シ 10:5,10:海 11:28,10:海 12:31,19:空",
            Edges = "01-02 02-03 03-04 01-05 05-06 06-07 07-08 08-12 01-フリーザー 05-10 03-11 08-11" },

        // サントアンヌ号：クチバシティから入る（ワープ107）。1階・2階（奥にライバル）・甲板
        new MapDef { Key = "SS1", Name = "S.S. Anne 1F", Image = "Assets/maps/S.S. Anne/2.png", Place = "サントアンヌ号", MapId = "ssanne", Parent = "SSAnne", Prefix = "SS_1F_", Scale = 0.05f, Origin = new Vector2(0, 780), Floor = "15,9;2,13;19,3",
            Nodes = "01:3,18:ワ107 02:3,14:空 03:3,10:ト 04:8,9:空 05:13,10:ボ 06:18,9:ト 07:20,5:空 08:24,9:空 09:29,10:ト 10:29,14:空 11:29,17:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 06-08 08-09 09-10 10-11" },
        new MapDef { Key = "SS2", Name = "S.S. Anne 2F", Image = "Assets/maps/S.S. Anne/3.png", Place = "サントアンヌ号", MapId = "ssanne", Parent = "SSAnne", Prefix = "SS_2F_", Scale = 0.05f, Origin = new Vector2(28, 780), Floor = "15,13;2,5",
            Nodes = "01:30,5:空 02:30,9:空 03:28,12:ト 04:23,13:空 05:18,12:ト 06:13,13:ボ 07:8,12:ト 08:4,13:空 09:3,9:空 10:2,5:ラ",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10" },
        new MapDef { Key = "SS3", Name = "S.S. Anne Deck", Image = "Assets/maps/S.S. Anne/4.png", Place = "サントアンヌ号", MapId = "ssanne", Parent = "SSAnne", Prefix = "SS_Deck_", Scale = 0.05f, Origin = new Vector2(4, 800), Floor = "8,9",
            Nodes = "01:14,7:空 02:10,6:ト 03:5,8:空 04:3,9:ボ 05:11,11:ト 06:12,13:イ",
            Edges = "01-02 02-03 03-04 01-05 05-06" },

        // ポケモン屋敷：グレンタウンから入る（ワープ108）。1階と地下
        new MapDef { Key = "PM1", Name = "Pokemon Mansion 1F", Image = "Assets/maps/Pokemon Mansion/1.png", Place = "ポケモン屋敷", MapId = "mansion", Parent = "PokemonMansion", Prefix = "PM_1F_", Scale = 0.05f, Origin = new Vector2(82, 780), Floor = "3,12;20,20;3,9",
            Nodes = "01:8,32:ワ108 02:8,26:草 03:8,19:空 04:8,13:草 05:3,9:ト 06:3,5:ボ 07:10,7:草 08:15,10:空 09:22,7:草 10:27,12:ト 11:26,16:草 12:20,19:空 13:16,23:草 14:22,26:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10 10-11 11-12 12-13 13-14 03-12" },
        new MapDef { Key = "PM2", Name = "Pokemon Mansion B1F", Image = "Assets/maps/Pokemon Mansion/4.png", Place = "ポケモン屋敷", MapId = "mansion", Parent = "PokemonMansion", Prefix = "PM_B1F_", Scale = 0.05f, Origin = new Vector2(116, 780), Floor = "3,8;20,18;30,12",
            Nodes = "01:35,28:空 02:32,24:草 03:35,17:草 04:34,12:空 05:33,8:ト 06:30,4:草 07:20,11:草 08:18,5:ボ 09:8,12:空 10:4,5:草 11:7,19:草 12:3,22:ボ 13:15,27:草 14:8,29:イ 15:22,25:ト",
            Edges = "01-02 02-03 03-04 04-05 05-06 04-07 07-08 07-09 09-10 09-11 11-12 11-13 13-14 13-15 15-02" },

        // チャンピオンロード：23番道路（ワープ109）から入り、セキエイ高原（ワープ110）へ抜ける。3階に ファイヤーのシンボル
        new MapDef { Key = "VR1", Name = "Victory Road 1F", Image = "Assets/maps/Victory Road/1.png", Place = "チャンピオンロード", MapId = "victoryroad", Parent = "VictoryRoad", Prefix = "VR_1F_", Scale = 0.05f, Origin = new Vector2(150, 780), Floor = "8,4;3,19;12,18",
            Nodes = "01:11,20:ワ109 02:6,19:草 03:3,15:空 04:3,10:草 05:4,5:ト 06:9,3:草 07:15,2:空 08:21,5:草 09:21,11:空 10:20,16:ボ 11:16,18:ト 12:3,2:空",
            Edges = "01-02 02-03 03-04 04-05 05-12 05-06 06-07 07-08 08-09 09-10 10-11 11-01" },
        new MapDef { Key = "VR2", Name = "Victory Road 2F", Image = "Assets/maps/Victory Road/2.png", Place = "チャンピオンロード", MapId = "victoryroad", Parent = "VictoryRoad", Prefix = "VR_2F_", Scale = 0.05f, Origin = new Vector2(192, 780), Floor = "8,4;3,19;34,2",
            Nodes = "01:3,3:空 02:3,8:草 03:3,14:ト 04:2,19:ボ 05:8,17:草 06:14,19:草 07:18,8:空 08:12,5:草 09:26,6:ト 10:33,3:草 11:40,8:空 12:45,11:草 13:48,13:ワ110 14:30,16:ラ 15:36,15:空",
            Edges = "01-02 02-08 08-07 07-09 09-10 10-11 11-12 12-13 02-03 03-04 04-05 05-06 06-14 14-15 15-12" },
        new MapDef { Key = "VR3", Name = "Victory Road 3F", Image = "Assets/maps/Victory Road/3.png", Place = "チャンピオンロード", MapId = "victoryroad", Parent = "VictoryRoad", Prefix = "VR_3F_", Scale = 0.05f, Origin = new Vector2(236, 780), Floor = "8,3;20,15;5,10",
            Nodes = "01:37,12:空 02:32,14:草 03:24,16:ト 04:13,16:草 05:4,13:空 06:7,7:ボ 07:12,4:草 08:30,4:草 ファイヤー:39,4:シ",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-ファイヤー ファイヤー-01" },

        // シルフカンパニー：ヤマブキシティの入口（ワープ111）から。1〜5階と11階（社長室にロケット団の拠点、ライバルのハウ）。階段で上の階へ（6〜10階は とばす）
        new MapDef { Key = "SC1", Name = "Silph Co 1F", Image = "Assets/maps/Silph Co/1.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_1F_", Scale = 0.05f, Origin = new Vector2(0, 840), Floor = "5,15;30,15",
            Nodes = "01:9,21:ワ111 02:9,16:空 03:4,12:空 04:13,6:空 05:23,6:ボ 06:30,10:空 07:33,5:空 08:30,20:空 09:20,20:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 02-09 09-08 08-06" },
        new MapDef { Key = "SC2", Name = "Silph Co 2F", Image = "Assets/maps/Silph Co/2.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_2F_", Scale = 0.05f, Origin = new Vector2(34, 840), Floor = "31,11;10,10",
            Nodes = "01:28,5:空 02:30,10:空 03:22,10:ト 04:12,10:空 05:5,6:ボ 06:4,19:ト 07:20,19:空 08:31,20:空 09:33,5:空",
            Edges = "01-02 02-03 03-04 04-05 04-06 03-07 02-08 02-09" },
        new MapDef { Key = "SC3", Name = "Silph Co 3F", Image = "Assets/maps/Silph Co/3.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_3F_", Scale = 0.05f, Origin = new Vector2(68, 840), Floor = "24,12;10,5",
            Nodes = "01:28,5:空 02:30,6:空 03:18,5:ト 04:8,6:空 05:6,12:ボ 06:16,12:ト 07:24,12:空 08:24,19:空 09:10,20:ト 10:33,12:空 11:33,5:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 03-07 07-08 08-09 07-10 02-11" },
        new MapDef { Key = "SC4", Name = "Silph Co 4F", Image = "Assets/maps/Silph Co/4.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_4F_", Scale = 0.05f, Origin = new Vector2(102, 840), Floor = "23,10;10,19",
            Nodes = "01:28,5:空 02:24,8:空 03:23,15:空 04:15,19:ト 05:4,19:空 06:5,8:ボ 07:15,7:ト 08:31,13:空 09:33,5:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 02-07 02-08 01-09" },
        new MapDef { Key = "SC5", Name = "Silph Co 5F", Image = "Assets/maps/Silph Co/5.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_5F_", Scale = 0.05f, Origin = new Vector2(136, 840), Floor = "23,7;12,20",
            Nodes = "01:28,5:空 02:23,7:空 03:14,8:ト 04:6,10:ボ 05:12,20:空 06:30,20:ト 07:16,13:空 08:34,6:空",
            Edges = "01-02 02-03 03-04 03-05 05-06 02-07 01-08" },
        new MapDef { Key = "SC11", Name = "Silph Co 11F", Image = "Assets/maps/Silph Co/11.png", Place = "シルフカンパニー", MapId = "silph", Parent = "SilphCo", Prefix = "SC_11F_", Scale = 0.05f, Origin = new Vector2(170, 840), Floor = "3,10;15,12",
            Nodes = "01:7,5:空 02:3,8:空 03:3,17:空 04:9,19:ト 05:10,10:ロ 06:15,12:ラ 07:15,5:空",
            Edges = "01-02 02-03 03-04 04-05 04-06 06-07 07-01" },

        // ---- 本マップ（置いてある板の上にマスを置く。板は消さない）----
        // 9番道路：ハナダシティの東の出口から 10番道路の北へ。上の砂の道と 下の段の道
        new MapDef { Key = "R9", Name = "Route 9", Image = "Assets/maps/Route 09/1.png", Place = "9番道路", MapId = "", Parent = "Route9", Prefix = "R9_", Main = true, Floor = "20,9;30,17;25,6;12,3;3,9;1,9",
            Nodes = "01:1,9:空 02:7,9:空 03:11,4:草 04:16,2:ボ 06:22,9:ト 07:29,9:空 08:38,2:草 09:39,8:空 10:47,7:ト 11:55,8:空 12:63,7:ボ 13:70,9:空 14:10,16:空 15:24,17:ト 16:38,17:空 17:52,17:草 18:66,17:空",
            Edges = "01-02 02-03 03-04 02-06 06-07 07-09 09-08 09-10 10-11 11-12 12-13 02-14 14-15 15-16 16-17 17-18 18-13" },
        // 10番道路の北：9番道路から。イワヤマトンネル北の入口（ワープ101）、ポケモンセンター、川を なみのりで下って むじんはつでんしょ（ワープ104）
        new MapDef { Key = "R10N", Name = "Route 10 North", Image = "Assets/maps/Route 10/1.png", Place = "10番道路", MapId = "", Parent = "Route10North", Prefix = "R10N_", Main = true, Floor = "5,23;2,9;10,12;10,6;14,7;1,9",
            Nodes = "01:1,9:空 02:7,7:草 03:13,6:草 04:3,14:空 05:8,20:ワ101 06:13,21:セ 07:5,25:ト 08:12,27:空 09:17,24:空 10:20,26:海 11:20,36:海 12:16,44:空 13:7,42:ワ104",
            Edges = "01-02 02-03 01-04 04-05 05-06 06-09 04-07 07-08 08-09 09-10 10-11 11-12 12-13" },
        // 23番道路：22番道路のリーグの門（ワープ112）から北へ。バッジの門と 3つの池（なみのり）、草むら、大きな岩の左に チャンピオンロードの入口（ワープ109）
        // 岩の右の出口（ワープ110）から 上の広場へ。入口側と出口側は道でつながない（チャンピオンロードを通る）
        new MapDef { Key = "R23", Name = "Route 23", Image = "Assets/maps/Route 23/1.png", Place = "23番道路", MapId = "", Parent = "Route23", Prefix = "R23_", Main = true, Floor = "9,154;3,132;12,78;8,128;9,72;5,40;14,17",
            Nodes = "01:8,155:ワ112 02:6,150:空 03:10,143:ト 04:14,137:空 05:8,128:空 06:12,121:ト 07:13,114:空 08:8,112:海 09:13,105:空 10:8,99:空 11:10,92:海 12:8,88:空 13:10,83:海 14:12,78:空 15:9,72:草 16:15,66:草 17:11,62:草 18:13,54:空 19:10,48:空 20:5,40:空 21:5,30:ワ109 22:16,46:ボ " +
                    "23:19,30:ワ110 24:21,24:空 25:14,17:空 26:8,10:ト 27:12,2:空",
            Edges = "01-02 02-03 03-04 04-05 05-06 06-07 07-08 08-09 09-10 10-11 11-12 12-13 13-14 14-15 15-16 16-17 17-18 18-19 19-20 20-21 19-22 23-24 24-25 25-26 26-27" },
        // セキエイ高原：23番道路の上から。ポケモンセンター・ショップ・ライバル
        new MapDef { Key = "IP", Name = "Indigo Plateau", Image = "Assets/maps/Indigo Plateau/1.png", Place = "セキエイ高原", MapId = "", Parent = "IndigoPlateau", Prefix = "IP_", Main = true, Floor = "12,12;12,15;9,9",
            Nodes = "01:12,18:空 02:12,12:空 03:9,8:セ 04:15,8:店shop_sekiei 05:12,7:ラ",
            Edges = "01-02 02-03 02-04 03-05 04-05" },
        // 19番水道の南：セキチクの浜から 海を南へ（なみのり）。下で 20番水道へ
        new MapDef { Key = "R19S", Name = "Route 19 South", Image = "Assets/maps/Route 19/1.png", Place = "19番水道", MapId = "", Parent = "Route19South", Prefix = "R19S_", Main = true, Floor = "12,3",
            Nodes = "01:12,16:海 02:8,26:海 03:14,38:海 04:8,48:海 05:4,55:海",
            Edges = "01-02 02-03 03-04 04-05" },
        // 20番水道：19番水道から西へ グレンタウンまで（なみのり）。ふたご島の 東の入口（ワープ106）と 西の入口（ワープ105）、砂の小島
        new MapDef { Key = "R20", Name = "Route 20", Image = "Assets/maps/Route 20/1.png", Place = "20番水道", MapId = "", Parent = "Route20", Prefix = "R20_", Main = true, Floor = "24,6;43,9;66,12",
            Nodes = "01:118,9:海 02:105,9:海 03:92,8:海 04:80,12:海 05:72,15:ワ106 06:60,9:ワ105 07:52,9:海 08:43,9:ト 09:33,9:海 10:24,6:ボ 11:14,9:海 12:3,9:海 13:62,17:海",
            Edges = "01-02 02-03 03-04 04-05 04-13 13-07 07-06 07-08 08-09 09-10 10-11 11-12" },
        // グレンタウン：ポケモン屋敷（ワープ108）、ジム、ポケモンセンター、ショップ。東は 20番水道、北の桟橋は 21番水道
        new MapDef { Key = "CI", Name = "Cinnabar Island", Image = "Assets/maps/Cinnabar Island/1.png", Place = "グレンタウン", MapId = "", Parent = "Cinnabar", Prefix = "CI_", Main = true, Floor = "12,7;22,13;14,13;5,9",
            Nodes = "01:22,13:空 02:19,12:店shop_guren 03:14,12:セ 04:12,7:空 05:20,5:ジ 06:8,5:ワ108 07:8,11:空 08:4,8:空 10:14,2:空",
            Edges = "01-02 02-03 03-04 04-05 04-06 04-07 07-08 04-10" },
        // 21番水道：マサラタウンの南から グレンタウンの北の桟橋まで（なみのり）。上に草むら、砂の小島
        new MapDef { Key = "R21N", Name = "Route 21 North", Image = "Assets/maps/Route 21/1.png", Place = "21番水道", MapId = "", Parent = "Route21", Prefix = "R21N_", Main = true, Floor = "16,24;14,8;7,29;17,42",
            Nodes = "01:8,2:海 02:14,8:草 03:8,16:海 04:16,24:空 05:7,29:ト 06:9,37:海 07:17,42:ボ 08:10,48:海",
            Edges = "01-02 01-03 03-04 03-05 05-06 04-06 06-07 06-08" },
        new MapDef { Key = "R21S", Name = "Route 21 South", Image = "Assets/maps/Route 21/2.png", Place = "21番水道", MapId = "", Parent = "Route21", Prefix = "R21S_", Main = true, Floor = "9,8;16,15;8,23;8,37;12,46",
            Nodes = "01:9,2:海 02:9,8:空 03:16,15:ト 04:8,23:空 05:12,31:海 06:8,37:ボ 07:12,46:空",
            Edges = "01-02 02-03 02-04 03-04 04-05 05-06 05-07" },
    };

    // 板と板をまたぐ道（階段・エリアの出入口）
    static readonly string[] Links = { "T1.08-T2.01", "T2.07-T3.01", "T3.06-T4.01", "C.07-W.01", "C.13-N.01", "C.18-E.17", "N.06-E.01", "N.20-W.07",
        "RT1.05-RT2.01", "RT2.14-RT1.12", "CC1.10-CC2.01", "SF1.07-SF2.01",
        "SS1.11-SS2.01", "SS1.07-SS3.01", "PM1.14-PM2.01", "VR1.12-VR2.01", "VR2.15-VR3.01",
        "SC1.07-SC2.01", "SC2.09-SC3.01", "SC3.11-SC4.01", "SC4.09-SC5.01", "SC5.08-SC11.01",
        "R9.01-#355", "R9.13-R10N.01", // #355：ハナダシティの東の出口
        "R23.27-IP.01",
        "R19S.01-#1084", "R19S.05-R20.01", "R20.12-CI.01", "R21N.01-#4", "R21N.08-R21S.01", "R21S.07-CI.10" }; // #1084：19番水道の浜、#4：マサラタウンの南

    // 別マップごとのメニュー（そのマップだけ作り直す）
    [MenuItem("Tools/すごろく/別マップを作る/ポケモンタワー")] static void MenuTower() => BuildMenu("PokemonTower");
    [MenuItem("Tools/すごろく/別マップを作る/サファリゾーン")] static void MenuSafari() => BuildMenu("SafariZone");
    [MenuItem("Tools/すごろく/別マップを作る/ディグダの穴")] static void MenuDiglett() => BuildMenu("DiglettsCave");
    [MenuItem("Tools/すごろく/別マップを作る/イワヤマトンネル")] static void MenuRockTunnel() => BuildMenu("RockTunnel");
    [MenuItem("Tools/すごろく/別マップを作る/ハナダの洞窟")] static void MenuCeruleanCave() => BuildMenu("CeruleanCave");
    [MenuItem("Tools/すごろく/別マップを作る/むじんはつでんしょ")] static void MenuPowerPlant() => BuildMenu("PowerPlant");
    [MenuItem("Tools/すごろく/別マップを作る/ふたご島")] static void MenuSeafoam() => BuildMenu("SeafoamIslands");
    [MenuItem("Tools/すごろく/別マップを作る/サントアンヌ号")] static void MenuSSAnne() => BuildMenu("SSAnne");
    [MenuItem("Tools/すごろく/別マップを作る/ポケモン屋敷")] static void MenuMansion() => BuildMenu("PokemonMansion");
    [MenuItem("Tools/すごろく/別マップを作る/チャンピオンロード")] static void MenuVictoryRoad() => BuildMenu("VictoryRoad");
    [MenuItem("Tools/すごろく/別マップを作る/シルフカンパニー")] static void MenuSilph() => BuildMenu("SilphCo");
    [MenuItem("Tools/すごろく/本マップのマスを置く/9番道路")] static void MenuRoute9() => BuildMenu("Route9");
    [MenuItem("Tools/すごろく/本マップのマスを置く/10番道路の北")] static void MenuRoute10North() => BuildMenu("Route10North");
    [MenuItem("Tools/すごろく/本マップのマスを置く/23番道路")] static void MenuRoute23() => BuildMenu("Route23");
    [MenuItem("Tools/すごろく/本マップのマスを置く/セキエイ高原")] static void MenuIndigo() => BuildMenu("IndigoPlateau");
    [MenuItem("Tools/すごろく/本マップのマスを置く/19番水道の南")] static void MenuRoute19South() => BuildMenu("Route19South");
    [MenuItem("Tools/すごろく/本マップのマスを置く/20番水道")] static void MenuRoute20() => BuildMenu("Route20");
    [MenuItem("Tools/すごろく/本マップのマスを置く/グレンタウン")] static void MenuCinnabar() => BuildMenu("Cinnabar");
    [MenuItem("Tools/すごろく/本マップのマスを置く/21番水道")] static void MenuRoute21() => BuildMenu("Route21");

    static void BuildMenu(string group)
    {
        if (Existing(new[] { group }).Any() && !EditorUtility.DisplayDialog("作り直す", "前に作ったこのマップ（手で直したマスも）を消して、作り直します。よろしいですか？", "作り直す", "やめる")) return;
        Debug.Log(Build(group));
    }

    static IEnumerable<GameObject> Existing(string[] groups)
    {
        var board = GameObject.Find("Board");
        if (board == null) yield break;
        foreach (var name in groups) { var t = board.transform.Find(name); if (t != null) yield return t.gameObject; }
        var ids = new HashSet<string>(Maps.Where(m => groups.Contains(m.Parent) && !m.Main).Select(m => m.MapId)); // 本マップの板は消さない
        foreach (var b in UnityEngine.Object.FindObjectsByType<SugorokuBoard>().Where(b => ids.Contains(b.mapId))) yield return b.gameObject;
    }

    // groups（Parent の名前）のマップだけを作る。何もわたさなければ全部。何をしたかの文を返す
    public static string Build(params string[] groups)
    {
        if (groups == null || groups.Length == 0) groups = Maps.Select(m => m.Parent).Distinct().ToArray();
        var build = Maps.Where(m => groups.Contains(m.Parent)).ToList();
        foreach (var go in Existing(groups).ToList()) Undo.DestroyObjectImmediate(go);
        // ほかのグループに置いた「消したマスへの道」も消す（あとで つなぎ直す）
        foreach (var r in UnityEngine.Object.FindObjectsByType<SugorokuRoad>(FindObjectsInactive.Include).Where(r => r.squareA == null || r.squareB == null).ToList())
            Undo.DestroyObjectImmediate(r.gameObject);
        var root = GameObject.Find("Board").transform;
        var squarePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SquarePrefab);
        var roadPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoadPrefab);
        var made = new Dictionary<string, SugorokuSquare>(); // "T1.08" → マス
        var log = new List<string>();
        int roads = 0;

        foreach (var m in build)
        {
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(m.Image)); // 読める写し（分類用）
            int tilesX = tex.width / 16, tilesY = tex.height / 16;
            var seeds = string.IsNullOrEmpty(m.Floor) ? null
                : m.Floor.Split(';').Select(p => p.Split(',')).Select(p => TileMean(tex, int.Parse(p[0]), int.Parse(p[1]))).ToList();
            if (m.Main)
            {
                // 本マップ：その絵を貼った板の 左上と 1px の長さを使う
                var onBoard = UnityEngine.Object.FindObjectsByType<SugorokuBoard>(FindObjectsInactive.Include)
                    .FirstOrDefault(b => b.GetComponent<MeshRenderer>()?.sharedMaterial?.mainTexture is Texture t && AssetDatabase.GetAssetPath(t) == m.Image);
                if (onBoard == null) { log.Add($"{m.Name}：{m.Image} の板が見つからないので とばした"); UnityEngine.Object.DestroyImmediate(tex); continue; }
                var bb = onBoard.GetComponent<MeshRenderer>().bounds;
                m.Origin = new Vector2(bb.min.x, bb.max.z);
                m.Scale = bb.size.x / tex.width;
            }
            float w = tex.width * m.Scale, h = tex.height * m.Scale;

            if (!m.Main)
            {
            // 板：おつきみ山と同じ（キューブ、Y 180度、厚さ0.2）
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = $"BoardBase ({m.Name})";
            plate.transform.SetParent(root, false);
            plate.transform.SetPositionAndRotation(new Vector3(m.Origin.x + w / 2, -0.2f, m.Origin.y - h / 2), Quaternion.Euler(0, 180, 0));
            plate.transform.localScale = new Vector3(w, 0.2f, h);
            plate.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(m.Image);
            var sb = plate.AddComponent<SugorokuBoard>();
            sb.place = m.Place; sb.mapId = m.MapId;
            Undo.RegisterCreatedObjectUndo(plate, "別マップを作る");
            }
            float y = -0.1f + 0.1f; // 板の上面 ＋ 0.1（おつきみ山と同じ高さの差）

            var parent = root.Find(m.Parent) ?? NewChild(root, m.Parent);
            var roadParent = parent.Find("Roads") ?? NewChild(parent, "Roads");
            var used = new HashSet<(int, int)>();
            foreach (var spec in m.Nodes.Split(' '))
            {
                var parts = spec.Split(':');
                var xy = parts[1].Split(',');
                int tx = int.Parse(xy[0]), ty = int.Parse(xy[1]);
                string kind = parts[2];
                bool sea = kind == "海";
                var (sx, sy) = Snap(tex, m.Tower, seeds, tx, ty, sea, used, tilesX, tilesY, out bool moved);
                used.Add((sx, sy));
                if (moved && (Math.Abs(sx - tx) > 2 || Math.Abs(sy - ty) > 2)) log.Add($"{m.Prefix}{parts[0]} を ({tx},{ty}) から ({sx},{sy}) に ずらした");

                var sq = ((GameObject)PrefabUtility.InstantiatePrefab(squarePrefab, parent)).GetComponent<SugorokuSquare>();
                sq.name = m.Prefix + parts[0];
                sq.transform.position = new Vector3(m.Origin.x + (sx * 16 + 8) * m.Scale, y, m.Origin.y - (sy * 16 + 8) * m.Scale);
                sq.id = 0; // 書き出しのときに番号がつく
                sq.type = TypeOf(kind, out int warp);
                sq.warpId = warp;
                if (kind.StartsWith("店")) sq.shopId = kind.Substring(1);
                sq.grassKind = sea ? GrassKind.海上 : GrassKind.草むら;
                if (kind == "シ") sq.symbolPokemon = parts[0]; // 伝説のシンボル：番号のところが ポケモンの名前
                typeof(SugorokuSquare).GetMethod("OnValidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(sq, null); // マスの上の名前
                PrefabUtility.RecordPrefabInstancePropertyModifications(sq);
                PrefabUtility.RecordPrefabInstancePropertyModifications(sq.transform);
                Undo.RegisterCreatedObjectUndo(sq.gameObject, "別マップを作る");
                made[$"{m.Key}.{parts[0]}"] = sq;
            }
            foreach (var e in m.Edges.Split(' '))
            {
                var ab = e.Split('-');
                Road(roadPrefab, roadParent, made[$"{m.Key}.{ab[0]}"], made[$"{m.Key}.{ab[1]}"]);
                roads++;
            }
            UnityEngine.Object.DestroyImmediate(tex);
        }
        // 「#番号」は 今ある盤面のマス（SugorokuSquare の id）
        var existing = UnityEngine.Object.FindObjectsByType<SugorokuSquare>(FindObjectsInactive.Include);
        // 「キー.番号」は 今回作ったマス。今回作らなかったグループなら、前に作ったマス（名前は Prefix＋番号）を探す
        SugorokuSquare End(string key)
        {
            if (key.StartsWith("#")) return existing.FirstOrDefault(s => s.id == int.Parse(key.Substring(1)));
            if (made.TryGetValue(key, out var s2)) return s2;
            var kv = key.Split('.');
            var def = Maps.FirstOrDefault(m => m.Key == kv[0]);
            return def == null ? null : existing.FirstOrDefault(s => s != null && s.name == def.Prefix + kv[1]);
        }
        foreach (var link in Links)
        {
            var ab = link.Split('-');
            if (!ab.Any(k => made.ContainsKey(k))) continue; // 今回 作らないマップ
            var a = End(ab[0]); var b = End(ab[1]);
            if (a == null || b == null) { log.Add($"つなげなかった：{link}（マスが見つからない）"); continue; }
            var mine = made.ContainsKey(ab[0]) ? a : b;
            Road(roadPrefab, mine.transform.parent.Find("Roads"), a, b);
            roads++;
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        var counts = build.GroupBy(m => m.Place).Select(g => $"{g.Key} {g.Sum(m => m.Nodes.Split(' ').Length)}マス");
        return $"別マップを作った：{string.Join("、", counts)}、道 {roads}本" + (log.Count > 0 ? "\n" + string.Join("\n", log) : "");
    }

    // マス目の平均の色
    static Color TileMean(Texture2D tex, int tx, int ty)
    {
        float r = 0, g = 0, b = 0; int n = 0;
        for (int py = ty * 16 + 1; py < ty * 16 + 16; py += 3)
            for (int px = tx * 16 + 1; px < tx * 16 + 16; px += 3) { var c = tex.GetPixel(px, tex.height - 1 - py); r += c.r; g += c.g; b += c.b; n++; }
        return new Color(r / n, g / n, b / n);
    }

    static Transform NewChild(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "別マップを作る");
        return go.transform;
    }

    static void Road(GameObject prefab, Transform parent, SugorokuSquare a, SugorokuSquare b)
    {
        var road = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        road.name = $"Road_{a.name}_{b.name}";
        var r = road.GetComponent<SugorokuRoad>();
        r.squareA = a; r.squareB = b;
        r.Fit();
        PrefabUtility.RecordPrefabInstancePropertyModifications(road);
        PrefabUtility.RecordPrefabInstancePropertyModifications(r);
        Undo.RegisterCreatedObjectUndo(road, "別マップを作る");
    }

    static SquareType TypeOf(string kind, out int warp)
    {
        warp = 0;
        if (kind.StartsWith("ワ")) { warp = int.Parse(kind.Substring(1)); return SquareType.ワープ; }
        if (kind.StartsWith("店")) return SquareType.ショップ;
        switch (kind)
        {
            case "草": case "海": return SquareType.草むら;
            case "ト": return SquareType.トレーナー;
            case "ボ": return SquareType.モンスターボール;
            case "ラ": return SquareType.ライバル;
            case "ロ": return SquareType.ロケット団;
            case "イ": return SquareType.イベントマス;
            case "シ": return SquareType.シンボル;
            case "セ": return SquareType.ポケモンセンター;
            case "ジ": return SquareType.ジム;
            default: return SquareType.空マス;
        }
    }

    // 絵ごとの材質（おつきみ山の材質の写しに 絵を貼る）。前に作ったものがあれば使う
    static Material MaterialFor(string image)
    {
        string dir = Path.Combine(Path.GetDirectoryName(image), "Materials").Replace('\\', '/');
        string path = $"{dir}/{Path.GetFileNameWithoutExtension(image)}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(Path.GetDirectoryName(image).Replace('\\', '/'), "Materials");
        mat = new Material(AssetDatabase.LoadAssetAtPath<Material>(TemplateMaterial)) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(image) };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // (tx, ty) のいちばん近くの 置けるマス目（まだ使っていない所）。ty は絵の上から数える
    static (int, int) Snap(Texture2D tex, bool tower, List<Color> seeds, int tx, int ty, bool sea, HashSet<(int, int)> used, int tilesX, int tilesY, out bool moved)
    {
        moved = false;
        for (int r = 0; r <= 4; r++)
            foreach (var (x, y) in Ring(tx, ty, r).OrderBy(p => Math.Abs(p.Item1 - tx) + Math.Abs(p.Item2 - ty)))
                if (x >= 0 && y >= 0 && x < tilesX && y < tilesY && !used.Contains((x, y)) && (seeds != null && !sea ? NearSeed(tex, seeds, x, y) : Fits(tex, tower, x, y, sea))) { moved = r > 0; return (x, y); }
        Debug.LogWarning($"別マップ: マス目 ({tx},{ty}) の近くに 置ける所がない（そのまま置く）");
        return (tx, ty);
    }

    static IEnumerable<(int, int)> Ring(int cx, int cy, int r)
    {
        if (r == 0) { yield return (cx, cy); yield break; }
        for (int x = cx - r; x <= cx + r; x++) for (int y = cy - r; y <= cy + r; y++)
            if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) == r) yield return (x, y);
    }

    // そのマス目の半分以上が 歩ける色か（タワー：緑の床。サファリ：道・砂。sea なら水）
    static bool Fits(Texture2D tex, bool tower, int tx, int ty, bool sea)
    {
        int ok = 0, all = 0;
        for (int py = ty * 16 + 1; py < ty * 16 + 16; py += 3)
            for (int px = tx * 16 + 1; px < tx * 16 + 16; px += 3)
            {
                var c = (Color32)tex.GetPixel(px, tex.height - 1 - py); // テクスチャは下から数える
                all++;
                bool water = c.b > c.r + 50 && c.b > c.g;
                bool floor = tower ? c.g > c.r + 15 && c.g > c.b && c.r < 140
                    : (c.g > 180 && c.b > 130 && c.r < 180) || (c.r > 200 && c.g > 180 && c.b < 170);
                if (sea ? water : floor) ok++;
            }
        return ok * 2 > all;
    }

    // マス目の平均の色が、床の見本のどれかに近いか（RGB の距離が 30/255 より小さい）
    static bool NearSeed(Texture2D tex, List<Color> seeds, int tx, int ty)
    {
        var m = TileMean(tex, tx, ty);
        return seeds.Any(s => Mathf.Sqrt((m.r - s.r) * (m.r - s.r) + (m.g - s.g) * (m.g - s.g) + (m.b - s.b) * (m.b - s.b)) < 30f / 255f);
    }
}
