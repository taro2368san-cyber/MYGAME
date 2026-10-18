using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// メニュー「Tools > すごろく > ポケモンタワーとサファリゾーンを作る」：別マップ（3.4）の板・マス・道を置く。
// 板は Assets/maps の絵（16pxのマス目）。マスは下の表の「マス目の位置」に置き、歩ける所（タワーは床、サファリは道と砂。海上マスは水）の近くにずらす。
// もう一度実行すると、前に作ったもの（Board/PokemonTower と Board/SafariZone、mapId tower / safari の板）を消して作り直す。手で直したマスも消えるので注意
public static class SpecialMapBuilder
{
    const string SquarePrefab = "Assets/Prefabs/Square.prefab", RoadPrefab = "Assets/Prefabs/Road.prefab";
    const string TemplateMaterial = "Assets/maps/Mt. Moon/Materials/1.mat"; // おつきみ山と同じ材質

    // 1枚の板。origin は絵の左上の位置（x, z）。scale は 絵の1pxの長さ
    sealed class MapDef
    {
        public string Key, Name, Image, Place, MapId, Parent, Prefix; public float Scale; public Vector2 Origin; public bool Tower;
        public string Nodes, Edges;
    }

    // マスの書き方「番号:列,段:種類」。種類 空=空マス 草=草むら 海=海上 ト=トレーナー ボ=モンスターボール ラ=ライバル ロ=ロケット団 イ=イベントマス ワN=ワープN番
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
    };

    // 板と板をまたぐ道（階段・エリアの出入口）
    static readonly string[] Links = { "T1.08-T2.01", "T2.07-T3.01", "T3.06-T4.01", "C.07-W.01", "C.13-N.01", "C.18-E.17", "N.06-E.01", "N.20-W.07" };

    [MenuItem("Tools/すごろく/ポケモンタワーとサファリゾーンを作る")]
    static void BuildMenu()
    {
        if (Existing().Any() && !EditorUtility.DisplayDialog("作り直す", "前に作ったポケモンタワーとサファリゾーン（手で直したマスも）を消して、作り直します。よろしいですか？", "作り直す", "やめる")) return;
        Debug.Log(Build());
    }

    static IEnumerable<GameObject> Existing()
    {
        var board = GameObject.Find("Board");
        if (board == null) yield break;
        foreach (var name in Maps.Select(m => m.Parent).Distinct()) { var t = board.transform.Find(name); if (t != null) yield return t.gameObject; }
        foreach (var b in UnityEngine.Object.FindObjectsByType<SugorokuBoard>().Where(b => Maps.Any(m => m.MapId == b.mapId))) yield return b.gameObject;
    }

    // 作って、何をしたかの文を返す
    public static string Build()
    {
        foreach (var go in Existing().ToList()) Undo.DestroyObjectImmediate(go);
        var root = GameObject.Find("Board").transform;
        var squarePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SquarePrefab);
        var roadPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoadPrefab);
        var made = new Dictionary<string, SugorokuSquare>(); // "T1.08" → マス
        var log = new List<string>();
        int roads = 0;

        foreach (var m in Maps)
        {
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(m.Image)); // 読める写し（分類用）
            int tilesX = tex.width / 16, tilesY = tex.height / 16;
            float w = tex.width * m.Scale, h = tex.height * m.Scale;

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
                var (sx, sy) = Snap(tex, m.Tower, tx, ty, sea, used, tilesX, tilesY, out bool moved);
                used.Add((sx, sy));
                if (moved && (Math.Abs(sx - tx) > 2 || Math.Abs(sy - ty) > 2)) log.Add($"{m.Prefix}{parts[0]} を ({tx},{ty}) から ({sx},{sy}) に ずらした");

                var sq = ((GameObject)PrefabUtility.InstantiatePrefab(squarePrefab, parent)).GetComponent<SugorokuSquare>();
                sq.name = m.Prefix + parts[0];
                sq.transform.position = new Vector3(m.Origin.x + (sx * 16 + 8) * m.Scale, y, m.Origin.y - (sy * 16 + 8) * m.Scale);
                sq.id = 0; // 書き出しのときに番号がつく
                sq.type = TypeOf(kind, out int warp);
                sq.warpId = warp;
                sq.grassKind = sea ? GrassKind.海上 : GrassKind.草むら;
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
        foreach (var link in Links)
        {
            var ab = link.Split('-');
            var a = made[ab[0]];
            Road(roadPrefab, a.transform.parent.Find("Roads"), a, made[ab[1]]);
            roads++;
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        return $"別マップを作った：ポケモンタワー {made.Keys.Count(k => k.StartsWith("T"))}マス、サファリゾーン {made.Keys.Count(k => !k.StartsWith("T"))}マス、道 {roads}本" + (log.Count > 0 ? "\n" + string.Join("\n", log) : "");
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
        switch (kind)
        {
            case "草": case "海": return SquareType.草むら;
            case "ト": return SquareType.トレーナー;
            case "ボ": return SquareType.モンスターボール;
            case "ラ": return SquareType.ライバル;
            case "ロ": return SquareType.ロケット団;
            case "イ": return SquareType.イベントマス;
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
    static (int, int) Snap(Texture2D tex, bool tower, int tx, int ty, bool sea, HashSet<(int, int)> used, int tilesX, int tilesY, out bool moved)
    {
        moved = false;
        for (int r = 0; r <= 4; r++)
            foreach (var (x, y) in Ring(tx, ty, r).OrderBy(p => Math.Abs(p.Item1 - tx) + Math.Abs(p.Item2 - ty)))
                if (x >= 0 && y >= 0 && x < tilesX && y < tilesY && !used.Contains((x, y)) && Fits(tex, tower, x, y, sea)) { moved = r > 0; return (x, y); }
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
}
