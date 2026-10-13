using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// メニュー「Tools > すごろく」で、選んだマスのあいだに道を作ったり、道を消したりする。
// 使い方: マスを1つクリック → Ctrl を押しながら別のマスをクリック → メニューを選ぶ。
// 3つ以上選んだときは、最後にクリックしたマスと、ほかの選んだマスがそれぞれつながる。
// 道は Hierarchy の Board > Roads の中にできる。道を選んで Delete しても、つながりを切れる。
// たくさんつなぐときは「つなぐモード」が便利（マスを順にクリックするだけで道が伸びる）。
public static class SugorokuSquareTools
{
    const string ConnectMenu = "Tools/すごろく/選んだマスをつなぐ";
    const string DisconnectMenu = "Tools/すごろく/選んだマスのつながりを切る";
    const string AddMenu = "Tools/すごろく/選んだマスの先にマスを増やす";
    const string RoadPrefabPath = "Assets/Prefabs/Road.prefab";
    const string SquarePrefabPath = "Assets/Prefabs/Square.prefab";

    [MenuItem(ConnectMenu)]
    static void Connect()
    {
        var active = ActiveSquare();
        foreach (var other in SelectedSquares())
        {
            if (other != active && !FindRoads(active, other).Any())
                CreateRoad(active, other);
        }
    }

    // 選んだマスの右に新しいマスを置いて道でつなぎ、そのマスを選んだ状態にする（続けて押すと伸びていく）
    [MenuItem(AddMenu)]
    static void AddSquare()
    {
        var from = ActiveSquare();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SquarePrefabPath);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, from.transform.parent);
        int number = Object.FindObjectsByType<SugorokuSquare>().Length - 1;
        go.name = $"Square_{number:000}"; // 3桁にしておくと、200個を超えても Hierarchy で番号順に並ぶ
        go.transform.position = from.transform.position + new Vector3(2f, 0f, 0f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(go);
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        Undo.RegisterCreatedObjectUndo(go, "マスを増やす");
        CreateRoad(from, go.GetComponent<SugorokuSquare>());
        Selection.activeGameObject = go; // すぐにドラッグで動かせる
    }

    [MenuItem(AddMenu, true)]
    static bool HasSquare() => ActiveSquare() != null;

    static void CreateRoad(SugorokuSquare a, SugorokuSquare b)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoadPrefabPath);
        var road = (GameObject)PrefabUtility.InstantiatePrefab(prefab, RoadsParent(a));
        road.name = $"Road_{a.name}_{b.name}";
        var component = road.GetComponent<SugorokuRoad>();
        component.squareA = a;
        component.squareB = b;
        component.Fit();
        PrefabUtility.RecordPrefabInstancePropertyModifications(road);
        PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        Undo.RegisterCreatedObjectUndo(road, "マスをつなぐ");
    }

    // つなぐモード: Scene ビューでマスを順にクリックすると、前にクリックしたマスとの間に道ができる。
    // マス以外をクリックすると、つなぎ始めからやり直し。Esc か、もう一度メニューを押すと終わる。
    const string ConnectModeMenu = "Tools/すごろく/つなぐモード（クリックで道を伸ばす）";
    static bool connectMode;
    static SugorokuSquare lastClicked;

    [MenuItem(ConnectModeMenu)]
    static void ToggleConnectMode() => SetConnectMode(!connectMode);

    [MenuItem(ConnectModeMenu, true)]
    static bool ShowConnectModeCheck()
    {
        Menu.SetChecked(ConnectModeMenu, connectMode);
        return true;
    }

    static void SetConnectMode(bool on)
    {
        connectMode = on;
        lastClicked = null;
        SceneView.duringSceneGui -= OnSceneGUI;
        if (on) SceneView.duringSceneGui += OnSceneGUI;
        SceneView.RepaintAll();
    }

    static void OnSceneGUI(SceneView view)
    {
        var e = Event.current;
        Handles.BeginGUI();
        string message = lastClicked != null ? $"つなぐモード：{lastClicked.name} の次のマスをクリック（Escで終了）" : "つなぐモード：最初のマスをクリック（Escで終了）";
        GUI.Label(new Rect(10, 10, 460, 24), message, EditorStyles.helpBox);
        Handles.EndGUI();

        if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive)); // ふつうの選択より先にクリックを受け取る
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { SetConnectMode(false); e.Use(); return; }
        if (e.type != EventType.MouseDown || e.button != 0 || e.alt) return;

        var picked = HandleUtility.PickGameObject(e.mousePosition, false);
        var square = picked != null ? picked.GetComponentInParent<SugorokuSquare>() : null;
        if (square != null) ClickSquare(square);
        else lastClicked = null;
        e.Use();
    }

    // クリックしたマスを、前にクリックしたマスとつなぐ
    static void ClickSquare(SugorokuSquare square)
    {
        if (lastClicked != null && lastClicked != square && !FindRoads(lastClicked, square).Any())
            CreateRoad(lastClicked, square);
        lastClicked = square;
        Selection.activeGameObject = square.gameObject;
    }

    [MenuItem(DisconnectMenu)]
    static void Disconnect()
    {
        var active = ActiveSquare();
        foreach (var other in SelectedSquares())
        {
            foreach (var road in FindRoads(active, other).ToList())
                Undo.DestroyObjectImmediate(road.gameObject);
        }
    }

    // マスが2つ以上選ばれているときだけ、メニューを押せるようにする
    [MenuItem(ConnectMenu, true)]
    [MenuItem(DisconnectMenu, true)]
    static bool HasTwoSquares() => ActiveSquare() != null && SelectedSquares().Count() >= 2;

    // 2つのマスをつないでいる道
    static IEnumerable<SugorokuRoad> FindRoads(SugorokuSquare a, SugorokuSquare b) =>
        Object.FindObjectsByType<SugorokuRoad>(FindObjectsInactive.Include)
            .Where(r => (r.squareA == a && r.squareB == b) || (r.squareA == b && r.squareB == a));

    // 道をまとめておく親（マスと同じ親の下の「Roads」）。なければ作る
    static Transform RoadsParent(SugorokuSquare square)
    {
        Transform board = square.transform.parent;
        Transform roads = board != null ? board.Find("Roads") : null;
        if (roads == null)
        {
            roads = new GameObject("Roads").transform;
            roads.SetParent(board, false);
            Undo.RegisterCreatedObjectUndo(roads.gameObject, "マスをつなぐ");
        }
        return roads;
    }

    static SugorokuSquare ActiveSquare() =>
        Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<SugorokuSquare>() : null;

    static IEnumerable<SugorokuSquare> SelectedSquares() =>
        Selection.gameObjects.Select(go => go.GetComponentInParent<SugorokuSquare>()).Where(s => s != null).Distinct();
}
