using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 表示役（仕様書15.2）。命令を処理役（GameProcessor）に渡し、返ってきた「起きたこと」を順に見せる
// 状態を書き換えるのは処理役だけ。ここは見せることと、ボタン・クリックを命令にすることだけをする
// 操作：ボタンをクリック、または数字キー1〜9。バトルは Z/X/C/V（攻撃）、A/S/D/F（防御）。マウスホイールで寄る・引く
public partial class GameRunner : MonoBehaviour
{
    [Header("見た目（あとで差し替えられる）")]
    [Tooltip("プレイヤーのコマ。空なら色つきのカプセル")]
    public GameObject pieceVisual;
    public Color[] playerColors = { new Color(0.9f, 0.25f, 0.25f), new Color(0.25f, 0.45f, 0.95f), new Color(0.25f, 0.8f, 0.3f), new Color(0.95f, 0.8f, 0.2f) };
    [Tooltip("ポケモンの絵は Resources/Pokemon/<ポケモン名> の画像を使う。なければタイプの色の丸")]
    public string pokemonSpriteFolder = "Pokemon";

    [Header("演出")]
    public float stepSeconds = 0.2f;     // コマが1マス進む時間（17.6）
    public Vector3 cameraOffset = new Vector3(0, 12, -9);

    [Header("確認用")]
    [Tooltip("0ならランダム。同じ数にすると同じ流れになる")]
    public int seed;

    [Header("タイトル")]
    public string gameTitle = "ポケモンすごろく";
    [Tooltip("タイトルの背景の絵。空なら紺色の単色")]
    public Sprite titleBackground;

    bool shortBattle; // 設定：バトル演出を短くする（仕様書1.3）


    GameData data;
    BoardData board;
    GameProcessor game;
    Narrator narrator;
    GameState S => game.State;
    readonly Dictionary<int, SugorokuSquare> squares = new Dictionary<int, SugorokuSquare>();
    readonly List<Transform> pieces = new List<Transform>();
    readonly Queue<GameEvent> queue = new Queue<GameEvent>();
    bool playing;
    float zoom = 1;
    Transform cameraTarget;

    // 画面
    Canvas canvas;
    Text log, prompt;
    RectTransform buttons, battlePanel;
    readonly List<string> logLines = new List<string>();
    readonly List<(string label, Action action, KeyCode key, bool enabled)> menu = new List<(string, Action, KeyCode, bool)>();
    Action subMenu; // 「どうぐ」などで開いた小さなメニュー（null なら場面ごとのメニュー）
    readonly BattleCard[] cards = new BattleCard[2];

    // ---- 起動 ----

    void Start()
    {
        canvas = Ui.Canvas("GameUI");
        sound = gameObject.AddComponent<Sound>();
        var problems = new List<string>();
        try
        {
            string dir = Path.Combine(Application.streamingAssetsPath, "GameData");
            data = GameData.Load(dir, problems);
            board = BoardData.Load(dir, data, problems);
        }
        catch (Exception e) { problems.Add("[エラー] " + e.Message); }
        var errors = problems.Where(p => p.StartsWith("[エラー]")).ToList();
        if (errors.Count > 0)
        {
            // データに問題があれば、一覧を出して止める（17.4）
            Ui.Panel(canvas.transform, "Error", Vector2.zero, Vector2.one, new Color(0.1f, 0, 0, 0.95f));
            Ui.Label(canvas.transform, "データを読み込めませんでした（Tools > すごろく > データをチェック で詳しく見られます）\n\n" + string.Join("\n", errors.Take(30)), 26);
            return;
        }
        foreach (var sq in FindObjectsByType<SugorokuSquare>()) if (sq.id > 0) squares[sq.id] = sq;
        shortBattle = PlayerPrefs.GetInt("short_battle", 0) == 1;
        ShowTitle();
    }

    // ---- タイトルと設定（16.1 の1・2）----

    Image FullScreen(string name)
    {
        Ui.Clear(canvas.transform);
        var bg = Ui.Panel(canvas.transform, name, Vector2.zero, Vector2.one, titleBackground != null ? Color.white : new Color(0.05f, 0.08f, 0.15f, 1));
        bg.sprite = titleBackground;
        return bg;
    }

    void ShowTitle()
    {
        var bg = FullScreen("Title");
        sound.PlayBgm("title");
        Ui.Label(Ui.Rect(bg.transform, "Name", new Vector2(0, 0.6f), new Vector2(1, 0.85f)), gameTitle, 110, TextAnchor.MiddleCenter);
        var col = Ui.Stack(bg.transform, "Menu", new Vector2(0.38f, 0.12f), new Vector2(0.62f, 0.52f), true, 18, 0);
        Ui.Button(col, "はじめから", () => RegisterThen(ShowSetup, ShowTitle), 420, 76); // コントローラー登録 → ゲーム設定（1.1）
        // セーブが1つもなければ押せない
        Ui.Button(col, "つづきから", ShowLoad, 420, 76).interactable = SaveSlots().Any(x => x.summary != null);
        Ui.Button(col, "設定", ShowSettings, 420, 76);
    }

    void ShowSettings()
    {
        var bg = FullScreen("Settings");
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.3f, 0.15f), new Vector2(0.7f, 0.85f), true, 18, 0);
        Ui.Line(col, "設定", 56);
        Ui.Button(col, "バトル演出を短くする：" + (shortBattle ? "オン" : "オフ"), () =>
        {
            shortBattle = !shortBattle;
            PlayerPrefs.SetInt("short_battle", shortBattle ? 1 : 0);
            ShowSettings();
        }, 600, 70);
        VolumeRow(col, "マスター", () => Sound.Master, v => Sound.Master = v);
        VolumeRow(col, "BGM", () => Sound.Bgm, v => Sound.Bgm = v);
        VolumeRow(col, "効果音", () => Sound.Se, v => { Sound.Se = v; sound.PlaySe("item"); });
        Ui.Button(col, "はじめての場面のヒント：" + (HintsOn ? "オン" : "オフ"), () => { HintsOn = !HintsOn; ShowSettings(); }, 600, 70);
        Ui.Button(col, "ヒントを もう一度 出すようにする", () => { foreach (var k in new[] { "grass", "gym", "battle", "ball", "center", "shop", "fan", "jama" }) PlayerPrefs.DeleteKey("seen_hint_" + k); ShowSettings(); }, 600, 60);
        Ui.Button(col, "もどる", ShowTitle, 600, 70);
    }

    // 音量の行（0〜10の11段階）
    void VolumeRow(RectTransform col, string name, Func<float> get, Action<float> set, Action redraw = null)
    {
        var row = Row(col);
        Ui.Line(row, $"{name} {Mathf.RoundToInt(get() * 10)}", 34);
        void Change(float d) { set(Mathf.Clamp01(Mathf.Round((get() + d) * 10) / 10)); sound.ApplyVolume(); (redraw ?? ShowSettings)(); }
        Ui.Button(row, "−", () => Change(-0.1f), 110);
        Ui.Button(row, "＋", () => Change(0.1f), 110);
    }

    // ---- ゲーム設定（16.1 の5〜8をまとめた仮の画面）----
    // プレイヤーごとに キャラクター → 最初のポケモン（キャラクターの世代の御三家）→ 名前 → 移動方式（仕様書1.1）

    int playerCount = 2, weeks;
    string setupError;                              // 処理役が はじめの設定を断った理由（ゲーム設定に1回だけ出す）
    readonly int[] characterIndex = { 0, 1, 3, 5 };
    readonly int[] starterIndex = { 1, 0, 0, 0 };   // キャラクターの御三家の何番目か
    readonly string[] names = new string[4];        // 空ならキャラクターの名前
    readonly MoveMode[] modes = new MoveMode[4];
    readonly bool[] cpus = new bool[4];             // CPU が操作する（1.4）

    int eventRuleIndex;                             // 0＝ランダム、1〜＝イベントルール.csv の行、最後＝なし
    Character CharacterOf(int i) => data.Characters[characterIndex[i] % data.Characters.Count];
    EventRule PickedRule => eventRuleIndex >= 1 && eventRuleIndex <= data.EventRules.Count ? data.EventRules[eventRuleIndex - 1] : null;
    string EventRuleId => eventRuleIndex == 0 ? "random" : PickedRule?.Id ?? "";
    string EventRuleLabel => eventRuleIndex == 0 ? "ランダム" : PickedRule?.Name ?? "なし";

    // キャラクターの色（キャラクター.csv の「色」）。なければプレイヤーの番号の色（17.7）
    Color ColorOf(Character c, int fallback) => c != null && ColorUtility.TryParseHtmlString(c.Color, out var color) ? color : playerColors[fallback % playerColors.Length];
    Color PlayerColor(PlayerState p) => ColorOf(data.Characters.FirstOrDefault(x => x.Id == p.Character), p.Id);
    string StarterOf(int i) { var c = CharacterOf(i); return c.Starters[starterIndex[i] % c.Starters.Count]; }
    string NameOf(int i) => string.IsNullOrWhiteSpace(names[i]) ? CharacterOf(i).Name : names[i].Trim();

    // ---- 始まりの流れ（ロードマップ 3-1、仕様書1.1・16.1 #5〜#10）----
    // コントローラー登録 → ゲーム設定 → プレイヤーごとに キャラクター（人は移動方式も）→ 名前（CPU はとばしてキャラクターの名前）→ 最初のポケモン → 確認 → 手番の発表（相談.md 食い違い3）
    // 同じキャラクター・同じポケモンは選べない（先に選んだ人のものは押せない）。× で1つ前の画面へ（名前は ×で1文字けす（16.1 #7）。手番の発表は もう順番が決まったので もどらない）

    // 先に選んだ人（i より前）と かぶっているか
    bool CharacterTakenBefore(int i, int k) => Enumerable.Range(0, i).Any(j => characterIndex[j] % data.Characters.Count == k);
    bool StarterTakenBefore(int i, string species) => Enumerable.Range(0, i).Any(j => StarterOf(j) == species);

    // ゲーム設定（#5）：人数、週数、イベントルール、だれが CPU か
    void ShowSetup()
    {
        Ui.Clear(canvas.transform);
        int wMin = (int)data.Required("weeks_min"), wMax = (int)data.Required("weeks_max");
        if (weeks == 0) weeks = Mathf.Clamp(50, wMin, wMax); // ふつうは50週（1.1）
        var bg = Ui.Panel(canvas.transform, "Setup", Vector2.zero, Vector2.one, new Color(0.05f, 0.08f, 0.15f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.25f, 0.08f), new Vector2(0.75f, 0.92f), true, 16, 20);
        Ui.Line(col, "ゲーム設定", 52);
        if (setupError != null) { Ui.Line(col, "はじめられません：" + setupError, 26, new Color(1, 0.5f, 0.5f)); setupError = null; } // 処理役が断ったとき（StartGame）
        var row = Row(col);
        Ui.Line(row, $"人数 {playerCount}人", 30);
        for (int n = 2; n <= 4; n++) { int k = n; Ui.Button(row, $"{n}人", () => { playerCount = k; ShowSetup(); }, 110); }
        row = Row(col);
        Ui.Line(row, $"週数 {weeks}週", 30);
        Ui.Button(row, "−10", () => { weeks = Math.Max(wMin, weeks - 10); ShowSetup(); }, 110);
        Ui.Button(row, "＋10", () => { weeks = Math.Min(wMax, weeks + 10); ShowSetup(); }, 110);
        row = Row(col);
        Ui.Line(row, "イベントルール", 30);
        Ui.Button(row, EventRuleLabel, () => { eventRuleIndex = (eventRuleIndex + 1) % (data.EventRules.Count + 2); ShowSetup(); }, 300);
        if (PickedRule != null) Ui.Line(col, PickedRule.Description, 22, new Color(0.8f, 0.8f, 0.8f));
        row = Row(col);
        Ui.Line(row, "操作", 30);
        for (int i = 0; i < playerCount; i++) { int k = i; Ui.Button(row, $"P{i + 1}：{(cpus[i] ? "CPU" : "人")}", () => { cpus[k] = !cpus[k]; ShowSetup(); }, 170); }
        if (data.Characters.Count == 0) { Ui.Line(col, "キャラクター.csv がありません（設計メモに置いてください）", 30, Color.red); return; }
        row = Row(col);
        Ui.Button(row, "つぎへ", () => ShowCharacterPick(0), 300, 70);
        Ui.Button(row, "もどる（コントローラー登録）", () => RegisterThen(ShowSetup, ShowTitle), 460, 70); // ×で1つ前（登録）へ。登録の × でタイトルへ
    }

    string PlayerTitle(int i) => $"P{i + 1}" + (cpus[i] ? "（CPU）" : "");

    // キャラクター選択（#6）：絵と名前。人なら 移動方式も
    void ShowCharacterPick(int i)
    {
        int count = data.Characters.Count;
        if (CharacterTakenBefore(i, characterIndex[i] % count))
            for (int n = 0; n < count && CharacterTakenBefore(i, characterIndex[i] % count); n++) characterIndex[i] = (characterIndex[i] + 1) % count;
        Ui.Clear(canvas.transform);
        var bg = Ui.Panel(canvas.transform, "CharacterPick", Vector2.zero, Vector2.one, new Color(0.05f, 0.08f, 0.15f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.12f, 0.06f), new Vector2(0.88f, 0.94f), true, 14, 20);
        Ui.Line(col, $"{PlayerTitle(i)}の キャラクター", 48, ColorOf(CharacterOf(i), i));
        RectTransform row = null;
        for (int k = 0; k < count; k++)
        {
            if (k % 4 == 0) { row = Row(col); row.GetComponent<LayoutElement>().preferredHeight = 120; }
            var c = data.Characters[k];
            int kk = k;
            if (CharacterSprite(c) is Sprite face) IconIn(row, face, 100);
            var b = Ui.Button(row, (characterIndex[i] % count == k ? "▶ " : "") + c.Name, () =>
            {
                characterIndex[i] = kk;
                starterIndex[i] = 0;
                for (int n = 0; n < CharacterOf(i).Starters.Count && StarterTakenBefore(i, StarterOf(i)); n++) starterIndex[i]++;
                if (cpus[i]) ShowStarterPick(i); else OpenKana(i);
            }, 200, 90);
            b.interactable = !CharacterTakenBefore(i, k);
            b.image.color = ColorOf(c, k);
        }
        if (!cpus[i])
        {
            row = Row(col);
            Ui.Button(row, modes[i] == MoveMode.Highlight ? "移動：まとめて選ぶ" : "移動：1マスずつ", () => { modes[i] = modes[i] == MoveMode.Highlight ? MoveMode.Step : MoveMode.Highlight; ShowCharacterPick(i); }, 360);
            Ui.Line(row, "（まとめて＝止まれるマスが光る／1マスずつ＝分かれ道で 方向を選ぶ）", 22, new Color(0.8f, 0.8f, 0.8f));
        }
        row = Row(col);
        Ui.Button(row, "もどる", () => { if (i == 0) ShowSetup(); else ShowStarterPick(i - 1); }, 220, 70);
    }

    // 最初のポケモン（#8）：キャラクターの世代の御三家
    void ShowStarterPick(int i)
    {
        var c = CharacterOf(i);
        Ui.Clear(canvas.transform);
        var bg = Ui.Panel(canvas.transform, "StarterPick", Vector2.zero, Vector2.one, new Color(0.05f, 0.08f, 0.15f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.15f, 0.1f), new Vector2(0.85f, 0.9f), true, 18, 20);
        Ui.Line(col, $"{NameOf(i)}（{PlayerTitle(i)}）の 最初の ポケモン", 46, ColorOf(c, i));
        var row = Row(col);
        row.GetComponent<LayoutElement>().preferredHeight = 160;
        for (int k = 0; k < c.Starters.Count; k++)
        {
            string sp = c.Starters[k];
            int kk = k;
            if (PokemonIcon(sp) is Sprite art) IconIn(row, art, 140);
            var b = Ui.Button(row, $"{sp}\n{Pokemons.Of(data, Pokemons.Create(data, sp, 5, "", "", 0)).Type}タイプ", () =>
            {
                starterIndex[i] = kk;
                if (i + 1 < playerCount) ShowCharacterPick(i + 1); else ShowConfirm();
            }, 200, 100);
            b.interactable = !StarterTakenBefore(i, sp);
        }
        row = Row(col);
        Ui.Button(row, "もどる", () => { if (cpus[i]) ShowCharacterPick(i); else OpenKana(i); }, 220, 70);
    }

    // 確認画面：設定の一覧と「これで始める」（1.1、16.1 #9）
    void ShowConfirm()
    {
        Ui.Clear(canvas.transform);
        var bg = Ui.Panel(canvas.transform, "Confirm", Vector2.zero, Vector2.one, new Color(0.05f, 0.08f, 0.15f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.18f, 0.05f), new Vector2(0.82f, 0.95f), true, 14, 20);
        Ui.Line(col, "この設定で はじめますか？", 44);
        Ui.Line(col, $"{playerCount}人　{weeks}週　イベントルール：{EventRuleLabel}", 30);
        for (int i = 0; i < playerCount; i++)
        {
            var row = Row(col);
            if (CharacterSprite(CharacterOf(i)) is Sprite face) IconIn(row, face, 60);
            if (PokemonIcon(StarterOf(i)) is Sprite art) IconIn(row, art, 56);
            Ui.Line(row, $"P{i + 1}　{NameOf(i)}{(NameOf(i) != CharacterOf(i).Name ? $"（{CharacterOf(i).Name}）" : "")}　{StarterOf(i)}　{(cpus[i] ? "CPU" : "移動：" + (modes[i] == MoveMode.Highlight ? "まとめて" : "1マスずつ"))}", 28, ColorOf(CharacterOf(i), i));
        }
        Ui.Line(col, "手番の順番は、はじめたあとにランダムで決まります", 24, new Color(0.8f, 0.8f, 0.8f));
        var buttons = Row(col);
        Ui.Button(buttons, "これで始める", StartGame, 300, 70);
        Ui.Button(buttons, "もどる", () => ShowStarterPick(playerCount - 1), 220, 70);
        Ui.Button(buttons, "ゲーム設定から やりなおす", ShowSetup, 380, 70);
    }

    // 手番の発表（#10）：ランダムで決まった順番を見せてから ゲームを始める
    void ShowTurnOrder(List<GameEvent> events)
    {
        var bg = FullScreen("TurnOrder");
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.25f, 0.1f), new Vector2(0.75f, 0.9f), true, 18, 20);
        Ui.Line(col, "手番の 順番", 56, new Color(1, 0.85f, 0.4f));
        int n = 0;
        foreach (int id in S.TurnOrder)
        {
            var p = S.Players[id];
            var row = Row(col);
            Ui.Line(row, $"{++n}ばん", 36);
            if (CharacterSprite(data.Characters.FirstOrDefault(c => c.Id == p.Character)) is Sprite face) IconIn(row, face, 64);
            Ui.Line(row, p.Name + (p.IsCpu ? "（CPU）" : ""), 40, PlayerColor(p));
        }
        var rule = data.EventRules.FirstOrDefault(r => r.Id == S.EventRule);
        if (rule != null) Ui.Line(col, $"今回のイベントルール：{rule.Name}（{rule.Description}）", 26);
        Ui.Button(col, "はじめる", () => BeginPlay(events), 360, 76);
    }
    static RectTransform Row(Transform parent)
    {
        var row = Ui.Stack(parent, "Row", Vector2.zero, Vector2.one, false, 12, 0);
        var le = row.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 70;
        le.flexibleHeight = 0; // 縦に伸びないように
        return row;
    }

    void StartGame()
    {
        var list = Enumerable.Range(0, playerCount).Select(i => (NameOf(i), cpus[i] ? MoveMode.Highlight : modes[i], StarterOf(i))).ToList();
        var characters = Enumerable.Range(0, playerCount).Select(i => CharacterOf(i).Id).ToList();
        ulong s = seed != 0 ? (ulong)seed : (ulong)DateTime.Now.Ticks;
        // 画面でも止めているが、処理役でも確かめる（15.3 約束4）。だめなら ゲーム設定へ戻る
        if (GameProcessor.ValidateSetup(data, list, weeks, characters) is string bad) { setupError = bad; ShowSetup(); return; }
        game = GameProcessor.NewGame(board, data, list, weeks, s, out var events, characters, EventRuleId, cpus.Take(playerCount).ToList());
        ShowTurnOrder(events);
    }

    // ゲーム画面を作ってコマを置く（はじめから・つづきから 共通）
    void BeginPlay(List<GameEvent> events)
    {
        game.DebugEnabled = Debug.isDebugBuild; // デバッグ機能（F1）は開発用ビルドとエディタだけ（17.1）
        narrator = new Narrator(data, board, S);
        lastAutoSaveWeek = S.Week;
        readers = new MessageReaders { Turn = S.TurnOrder.Count > 0 ? S.CurrentPlayer.Id : -1, Battle = S.Battle?.Sides.Select(sd => sd.Player).ToArray() }; // 文を読む人（相談38）
        logLines.Clear();
        BuildGameScreen();
        if (Camera.main != null) { Camera.main.clearFlags = CameraClearFlags.SolidColor; Camera.main.backgroundColor = SkyColor; } // 背景は明るい水色
        foreach (var p in S.Players)
        {
            Transform piece;
            if (pieceVisual != null) piece = Instantiate(pieceVisual).transform;
            else if (CharacterSprite(data.Characters.FirstOrDefault(c => c.Id == p.Character)) is Sprite art)
            {
                // キャラクターの絵（Resources/Characters）を立てる。足もとに その人の色の輪
                piece = new GameObject("Piece").transform;
                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(ring.GetComponent<Collider>());
                ring.name = "Ring";
                ring.transform.SetParent(piece, false);
                ring.transform.localPosition = new Vector3(0, -0.58f, 0);
                ring.transform.localScale = new Vector3(0.6f, 0.015f, 0.6f);
                ring.GetComponent<Renderer>().material.color = PlayerColor(p);
                var sr = new GameObject("Sprite", typeof(SpriteRenderer)).GetComponent<SpriteRenderer>();
                sr.sprite = art;
                sr.transform.SetParent(piece, false);
                sr.transform.localPosition = new Vector3(0, -0.6f, 0); // 絵の足もと（下の中央）を盤面に
                sr.transform.localScale = Vector3.one * 1.3f;
            }
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Destroy(go.GetComponent<Collider>()); // マスのクリックのじゃまをしない
                go.transform.localScale = Vector3.one * 0.6f;
                go.GetComponent<Renderer>().material.color = PlayerColor(p); // キャラクターごとの色の駒（17.7）
                piece = go.transform;
            }
            piece.name = p.Name;
            pieces.Add(piece);
            piece.position = PiecePosition(p.Id, p.Square);
        }
        Play(events);
    }

    // ---- ゲーム画面 ----

    void BuildGameScreen()
    {
        Ui.Clear(canvas.transform);
        BuildHud(); // A 週と天気・B 場所名・C 手番の人・D ほかの人（GameRunner.Hud.cs）
        BuildBattleScene(); // バトル画面（GameRunner.Battle.cs）。セリフ欄とメニューは この上に出す
        // G セリフ欄（下の中央）：濃い緑の箱に白い文字、細い金色のふち。出すときは下からスライド（GameRunner.Hud.cs の SlideMessage）
        var gold = messageGold = Ui.Round(Ui.Panel(canvas.transform, "G_Message", new Vector2(0.32f, 0.01f), new Vector2(0.75f, 0.25f), new Color(0.85f, 0.7f, 0.3f)));
        messagePanel = gold.gameObject;
        var bottom = Ui.Round(Ui.Panel(gold.transform, "Inner", Vector2.zero, Vector2.one, new Color(0.04f, 0.25f, 0.14f, 0.95f)));
        bottom.rectTransform.offsetMin = new Vector2(3, 3); bottom.rectTransform.offsetMax = new Vector2(-3, -3);
        messageShown = 0;
        log = Ui.Label(bottom.transform, "", 24);
        log.rectTransform.offsetMin = new Vector2(16, 8); log.rectTransform.offsetMax = new Vector2(-16, -8);
        log.verticalOverflow = VerticalWrapMode.Truncate;
        logArrow = MakeArrow(bottom.transform); // 全文が出て ○ を待っている印（GameRunner.Message.cs）
        Clickable(bottom, ClickAdvance);        // 押すと ○ と同じ
        // E メニュー（左下）。準備フェーズは いどう／どうぐ／マップ／ポケモン／ずかん／せってい。ほかの場面の選択肢もここに出す
        var right = menuPanel = Ui.Round(Ui.Panel(canvas.transform, "E_Menu", new Vector2(0.005f, 0.01f), new Vector2(0.31f, 0.52f), HudColor));
        var promptRect = Ui.Rect(right.transform, "Prompt", new Vector2(0, 0.86f), Vector2.one);
        prompt = Ui.Label(promptRect, "", 24, TextAnchor.MiddleLeft, new Color(1, 0.9f, 0.5f));
        prompt.rectTransform.offsetMin = new Vector2(14, 0); prompt.rectTransform.offsetMax = new Vector2(-10, 0);
        buttons = Ui.Rect(right.transform, "Buttons", Vector2.zero, new Vector2(1, 0.86f));
        var grid = buttons.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(560, 50); grid.spacing = new Vector2(8, 6); grid.padding = new RectOffset(12, 12, 6, 6);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 1;

        BuildEventScreen(); // イベント画面（GameRunner.Event.cs）。セリフ欄・メニューの上、大きな知らせの下
        BuildFrame();       // 施設メニューの枠・ポケモン一覧（GameRunner.Frame.cs）
        BuildConfirm();     // 確認ダイアログ（GameRunner.Confirm.cs）。イベント画面の上
        var bannerPanel = Ui.Panel(canvas.transform, "Banner", new Vector2(0.1f, 0.42f), new Vector2(0.9f, 0.62f), new Color(0, 0, 0, 0.85f));
        banner = Ui.Label(bannerPanel.transform, "", 46, TextAnchor.MiddleCenter);
        bannerIcon = Ui.Panel(bannerPanel.transform, "Mon", new Vector2(0.02f, 0.08f), new Vector2(0.14f, 0.92f), Color.white); // 捕まえた・進化した子の絵
        bannerIcon.preserveAspect = true;
        bannerIcon.enabled = false;
        bannerPanel.gameObject.SetActive(false);
    }

    Text banner;
    Image bannerIcon;

    // species：知らせに そのポケモンの絵を出す（捕まえた・進化した）
    IEnumerator Banner(string text, Color color, float seconds, string species = null)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        bannerIcon.sprite = species != null ? PokemonIcon(species) : null;
        bannerIcon.enabled = bannerIcon.sprite != null;
        banner.text = text; banner.color = color;
        banner.transform.parent.gameObject.SetActive(true);
        banner.transform.parent.SetAsLastSibling();
        yield return Wait(seconds);
        banner.transform.parent.gameObject.SetActive(false);
    }

    // セリフ欄に足す。新しい文は 1文字ずつ出す（SlideMessage）
    void AddLog(string text)
    {
        foreach (var line in text.Split('\n')) logLines.Add(line);
        while (logLines.Count > 9) logLines.RemoveAt(0);
        logWriter.Start(text.Split('\n').Length > logLines.Count ? string.Join("\n", logLines) : text); // 9行より長い文は 最後の9行だけ
        lastLogTime = Time.time;
        RenderLog();
    }

    // ---- 命令を出して、起きたことを見せる ----

    void Send(Command c)
    {
        subMenu = null;
        if (!playing && S.TurnOrder.Count > 0) readers.Turn = S.CurrentPlayer.Id; // 命令を出す前の番の人（相談38）
        var events = game.Apply(c, out string error);
        if (error != null) { AddLog("× " + error); RefreshAll(); return; }
        Play(events);
    }

    // 同じ命令を n 回つづけて出す（ショップで 個数を選んで買う・売る、相談31）。できなくなったら そこで止める。文は1つにまとめる
    readonly HashSet<GameEvent> silent = new HashSet<GameEvent>();
    void SendMany(Func<Command> make, int n, Func<int, string> summary)
    {
        subMenu = null;
        if (!playing && S.TurnOrder.Count > 0) readers.Turn = S.CurrentPlayer.Id;
        var all = new List<GameEvent>();
        int done = 0;
        for (; done < n; done++)
        {
            var c = make();
            if (game.Validate(c) != null) break;
            var events = game.Apply(c, out string error);
            if (error != null) break;
            all.AddRange(events);
        }
        if (done > 1) { foreach (var e in all) silent.Add(e); AddLog(summary(done)); }
        Play(all);
    }

    void Play(List<GameEvent> events)
    {
        foreach (var e in events) queue.Enqueue(e);
        if (!playing) StartCoroutine(PlayQueue());
    }

    IEnumerator PlayQueue()
    {
        playing = true;
        ClearMenu(); DrawMenu(""); PaintBoard(false);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            readers.See(e); // 文を読む人を追う（相談38）：その出来事の文から 新しい番の人・バトルの両者が読む
            // セリフは 話す人ごとの行で受けとる。ライバル・拠点の人・ランダムイベントの人なら イベント画面で見せる（UI仕様書 5）
            var lines = e is Dialog dg ? narrator.DialogLines(dg) : null;
            string text = lines != null ? Narrator.Join(lines) : narrator.Describe(e);
            if (silent.Remove(e)) text = null; // まとめ買いの1個ずつの文は出さない（SendMany がまとめた文を出す）
            // 勝敗と報酬（相談30）：バトルの終わりと、すぐあとに続く 経験値・お金・アイテム・1回休みの文は、結果の枠に まとめて出す
            if (resultLines != null && !IsResultEvent(e)) yield return ShowResult();
            if (e is BattleEnded && text != null && battlePanel.gameObject.activeSelf) { resultLines = new List<string> { text }; text = null; }
            else if (resultLines != null && text != null) { resultLines.Add(text); text = null; }
            string person = e is Dialog dp ? EventPersonOf(dp, lines) : null;
            if (person != null) yield return OpenEvent(person, ((Dialog)e).Player);
            if (e is BattleStarted) CloseEvent(); // バトルになるなら バトル画面へ（ジムリーダー・職業トレーナーは BattleStarted で見せる）
            bool inEvent = EventOpen && text != null;
            if (inEvent) yield return EventLines(lines ?? new List<(string, string)> { ("", text) }); // イベント画面が開いているあいだは 文もそこに
            else if (text != null) AddLog(text);
            EventSound(e);
            CollectNotice(e);
            switch (e)
            {
                case TurnStarted t:
                    cameraTarget = pieces[t.Player];
                    camPan = Vector3.zero;
                    yield return TurnSwapAnim(t.Player); // 次の人の欄が C へ上がる
                    yield return ShowNotices(t.Player); // 17.5
                    break;
                case BallThrown bt: // ボールが揺れる（17.6）：野生のカードで ボールの絵を動かす
                    yield return cards[cards[1].Player < 0 ? 1 : 0].Catch(Ui.Ball(BallColor(bt.Ball)), bt.Caught);
                    break;
                case ExpGained xg when battlePanel.gameObject.activeSelf && cards.FirstOrDefault(c => c.Owns(xg.Player, xg.Pokemon)) is BattleCard xc:
                    yield return xc.ExpTo(xg.Total - xg.Exp, xg.Total, (int)data.Required("level_max")); // 経験値バーが ゆっくり伸びる
                    break;
                case WeekEventHappened we: yield return Banner(text, new Color(0.7f, 0.9f, 1f), 2); continue;            // 週イベントの告知（17.6）
                // 週の知らせは 帯で 自動で流す（○で早送り。相談32）
                case WeekAdvanced _: yield return Banner(text, Color.white, 1.2f); continue;
                case Dividend dv: yield return Banner(text, PlayerColor(S.Players[dv.Player]), 1.5f); continue;
                case WeekEventEnded _: yield return Banner(text, new Color(0.7f, 0.9f, 1f), 1.2f); continue;
                case FacilityOpened fo:
                    facilityTab = "";
                    if (fo.Kind == FacilityKind.Center) { sound.PlaySe("item"); yield return Banner("みんな 元気に なった！", new Color(0.6f, 1f, 0.7f), 1); continue; } // 回復の演出（相談34）
                    break;
                case DiceRolled dr: yield return DiceAnim(dr); continue;          // 17.6
                case BallTierUsed bu when bu.Prize: yield return PrizeAnim(text, bu.Player); continue; // 鍵付きボールの当たり（8.8.3）
                case CardsDealt cd: yield return DealCards(cd); continue;           // 先攻・後攻のカード（UI仕様書 7.3 ②）
                case InitiativeDrawn idr: yield return RevealCard(idr); yield return SwapSides(idr.First, true); continue;
                case CommandsRequested cr: yield return SwapSides(cr.Attacker); break; // 攻守の交代（⑥）
                case Moved m: camPan = Vector3.zero; yield return MovePiece(m.Player, m.Path); continue;
                case Warped w: pieces[w.Player].position = PiecePosition(w.Player, w.To); break;
                case SailedToIsland si: pieces[si.Player].position = PiecePosition(si.Player, si.To); cameraTarget = pieces[si.Player]; camPan = Vector3.zero; yield return Banner(text, PlayerColor(S.Players[si.Player]), 2); continue;
                case ReturnedFromIsland ri: pieces[ri.Player].position = PiecePosition(ri.Player, ri.To); cameraTarget = pieces[ri.Player]; camPan = Vector3.zero; break;
                case TicketChanged tc: yield return Banner(text, PlayerColor(S.Players[tc.Player]), 2); continue;
                case SentToCenter c: pieces[c.Player].position = PiecePosition(c.Player, c.Square); break;
                case BattleStarted b:
                    // ジムリーダー・職業マスのトレーナーは、バトルの前に イベント画面で しょうぶを しかけてくる
                    if (b.Kind == BattleKind.Gym || b.Kind == BattleKind.Job || b.Kind == BattleKind.League)
                    {
                        yield return OpenEvent(b.Opponent, b.Players[0]);
                        yield return EventLines(new List<(string, string)> { ("", text ?? $"{b.Opponent}との バトル！") }); // セリフは文言CSVにないので、ふつうの知らせの文
                        CloseEvent();
                    }
                    if ((b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament) && !S.Players[b.Players[0]].IsCpu && !S.Players[b.Players[1]].IsCpu) yield return Banner($"{S.Players[b.Players[0]].Name}さんは コントローラー1、{S.Players[b.Players[1]].Name}さんは 2を 持ってください", Color.white, 2);
                    StartBattleScene(b.Kind); // バトル画面（A〜D は隠す）
                    cards[0].SetOwner(b.Players[0] >= 0 ? S.Players[b.Players[0]].Name : "", b.Players[0]);
                    cards[1].SetOwner(b.Players[1] >= 0 ? S.Players[b.Players[1]].Name : b.Opponent, b.Players[1]);
                    break;
                case BattleResumed br: // NPC戦の続き：登場から見せる
                    StartBattleScene(br.Kind);
                    cards[0].SetOwner(S.Players[br.Players[0]].Name, br.Players[0]);
                    cards[1].SetOwner(br.Players[1] >= 0 ? S.Players[br.Players[1]].Name : br.Opponent, br.Players[1]);
                    break;
                case BattleSuspended bs: ShowBattle(false); yield return Banner(text, PlayerColor(S.Players[bs.Player]), 1.5f); continue;
                case SentOut s:
                    cards[s.Side].Show(s.Pokemon, s.Level, s.Hp, s.MaxHp, s.Type, s.Weakness, s.Exp); cards[s.Side].SetStatus(Pokemons.StatusName(s.Status));
                    yield return Enter(s.Side); // 野生は横から、ほかはボールから
                    break;
                case StatusInflicted si: cards[si.Side].SetStatus(Pokemons.StatusName(si.Status)); break;
                case CantMove cm when cm.Woke: cards[cm.Side].SetStatus(""); break;
                case StatusDamage sdm: yield return cards[sdm.Side].HpTo(sdm.Hp); break;
                case AbilityHealed ah: yield return cards[ah.Side].HpTo(ah.Hp); break;
                case HeldItemDamage hd: yield return cards[hd.Side].HpTo(hd.Hp); break;
                case StatusCured sc: cards[sc.Side].SetStatus(""); break;
                case RankChanged rc: cards[rc.Side].Flash($"{rc.Stat}{(rc.Delta > 0 ? "↑" : "↓")}"); break;
                case RoundResolved rb when rb.Bond: yield return Banner(text.Split('\n').Last(), Color.white, 2); continue; // 絆による回避（7.18）
                case RoundResolved r:
                    yield return RevealCommands(r); // 結果発表：選んだコマンドだけ残して、オーラ（UI仕様書 7.3 ④）
                    if (r.Item == null) yield return AttackAnim(r); // 技の演出（17.6）
                    cards[r.Attacker].Flash($"攻撃ダイス {r.Dice}");
                    yield return Together(cards[r.Attacker].HpTo(r.AttackerHp), cards[1 - r.Attacker].HpTo(r.DefenderHp)); // HPが ゆっくり減る（回復なら増える）
                    cards[0].ClearAura(); cards[1].ClearAura();
                    UpdateTurns();
                    break;
                case SandstormDamage sd: yield return cards[sd.Side].HpTo(sd.Hp); break;
                case PokemonFainted f: yield return cards[f.Side].HpTo(0); yield return cards[f.Side].Faint(); break; // 沈んで消える
                // 他の人の大事な出来事は、その人の色と名前つきで大きく（17.5）。長さは17.6
                case Caught c: yield return Banner(text, PlayerColor(S.Players[c.Player]), 2, c.Pokemon); continue;
                case Evolved ev: yield return Banner(text, PlayerColor(S.Players[ev.Player]), 5, ev.To); continue;
                case BadgeEarned be: yield return Banner(text, PlayerColor(S.Players[be.Player]), 3); continue;
                case GymAppeared ga: yield return Banner(text, PlayerColor(S.Players[ga.Player]), 2); continue; // トキワジムの出現（3.3）。バッジの知らせのすぐあと
                case SymbolGone sg: SyncSymbols(); yield return Banner(text, PlayerColor(S.Players[sg.Player]), 2, sg.Pokemon); continue; // 伝説が盤面から消えた（全員に大きく）
                case LevelCaughtUp lc: yield return Banner(text, PlayerColor(S.Players[lc.Player]), 2); continue;
            }
            // 文は 読み終えて ○ を押すまで 次の演出・結果へ進まない（UI仕様書 2章 G、相談37）。帯の知らせと 時間の演出は 上の continue で 時間どおり
            if (text != null && !inEvent) yield return ReadMessage(logWriter);
            if (HintFor(e) is string hint) yield return ShowHint(hint);
        }
        yield return ShowResult();
        playing = false;
        if (S.Battle == null) readers.Battle = null; // バトルの後の文（経験値・進化）までは 両者が読む
        AfterEventQueue(); // 選択肢を待っていなければ イベント画面を閉じる
        if (S.Phase == Phase.Prepare && S.Week != lastAutoSaveWeek) { lastAutoSaveWeek = S.Week; SaveTo(0); } // 週の終わりに自動セーブ（1.2）
        RefreshAll();
        if (S.Phase == Phase.GameOver && !resultsShown) { resultsShown = true; ShowBattle(false); ShowResults(); } // 最後の週が終わったら 結果発表（ロードマップ 3-2）
    }
    bool resultsShown;

    IEnumerator Wait(float seconds)
    {
        for (float t = 0; t < seconds && !SkipHeld(); t += Time.deltaTime) yield return null;
    }

    // いくつかの演出を同時に動かして、全部終わるまで待つ（両方のHPバーを いっしょに減らす など）
    IEnumerator Together(params IEnumerator[] parts)
    {
        int left = parts.Length;
        IEnumerator Run(IEnumerator part) { yield return part; left--; }
        foreach (var part in parts) StartCoroutine(Run(part));
        while (left > 0) yield return null;
    }

    IEnumerator MovePiece(int player, List<int> path)
    {
        cameraTarget = pieces[player];
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 from = pieces[player].position, to = PiecePosition(player, path[i]);
            sound.PlaySe("step");
            float time = SkipHeld() ? stepSeconds / 4 : stepSeconds;
            for (float t = 0; t < time; t += Time.deltaTime)
            {
                pieces[player].position = Vector3.Lerp(from, to, t / time) + Vector3.up * Mathf.Sin(t / time * Mathf.PI) * 0.3f;
                yield return null;
            }
            pieces[player].position = to;
        }
    }

    // カビゴンのいるマスに目印（黒っぽい大きな玉）。状態に合わせて毎回そろえるので、ロードしても正しく出る
    readonly Dictionary<int, GameObject> snorlax = new Dictionary<int, GameObject>();
    void SyncSnorlax()
    {
        foreach (int sq in snorlax.Keys.Where(k => !S.Blocked.ContainsKey(k)).ToList()) { Destroy(snorlax[sq]); snorlax.Remove(sq); }
        foreach (int sq in S.Blocked.Keys.Where(k => !snorlax.ContainsKey(k) && game.SymbolHere(k) == null)) // 12番・16番道路のカビゴンは シンボルの絵が出るので 玉は出さない
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = "カビゴン";
            go.transform.position = SquarePosition(sq) + Vector3.up * 0.5f;
            go.transform.localScale = new Vector3(0.9f, 0.7f, 0.9f);
            go.GetComponent<Renderer>().material.color = new Color(0.15f, 0.3f, 0.35f);
            snorlax[sq] = go;
        }
    }

    // 手番の人のバッジでは入れないマスに ×。入れるマスのとなりのマスと、行き先が入れないワープだけ（エリアの中まで全部に付けると 盤面が × だらけになる）
    readonly Dictionary<int, GameObject> lockMarks = new Dictionary<int, GameObject>();
    void SyncLocks()
    {
        var p = S.CurrentPlayer;
        bool Open(int id) => Movement.CanEnter(board, id, p.Badges, game.TopBadges);
        var want = new HashSet<int>(board.Squares.Values.Where(s =>
            Open(s.Id) ? s.WarpPartner >= 0 && !Open(s.WarpPartner) : s.Neighbors.Any(Open)).Select(s => s.Id));
        foreach (int sq in lockMarks.Keys.Where(k => !want.Contains(k)).ToList()) { Destroy(lockMarks[sq]); lockMarks.Remove(sq); }
        foreach (int sq in want.Where(k => !lockMarks.ContainsKey(k)))
        {
            var go = new GameObject("×");
            go.transform.position = SquarePosition(sq) + Vector3.up * 0.4f;
            foreach (float angle in new[] { 45f, -45f })
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(bar.GetComponent<Collider>());
                bar.transform.SetParent(go.transform, false);
                bar.transform.localRotation = Quaternion.Euler(0, angle, 0);
                bar.transform.localScale = new Vector3(1.1f, 0.08f, 0.18f);
                bar.GetComponent<Renderer>().material.color = new Color(0.9f, 0.1f, 0.1f);
            }
            lockMarks[sq] = go;
        }
    }

    // つりができるマス（海上マスから2マス以内、4.1.2）に つりのマーク。場所の印なので つりざおの有無では変えない。別マップの板にも同じく置く（見えるのは表示中の板だけ）
    // 絵は Ui.Icon の「つりざお」（仮のドット絵）。差し替えるときは FishMarkIcon か Ui の絵を変える
    const string FishMarkIcon = "つりざお";
    readonly List<GameObject> fishMarks = new List<GameObject>();
    void SyncFishMarks()
    {
        if (fishMarks.Count > 0) return; // 盤面は変わらないので 1回だけ
        foreach (var s in board.Squares.Values.Where(s => board.CanFish(s.Id)))
        {
            var go = new GameObject("つり");
            go.transform.position = SquarePosition(s.Id) + new Vector3(0.55f, 0.35f, 0.35f); // マスの右上（真ん中は マスの名前と ×）
            go.transform.rotation = Quaternion.Euler(90, 0, 0); // 寝かせて 上から見える向き（× と同じ）
            go.transform.localScale = Vector3.one * 5f;
            go.AddComponent<SpriteRenderer>().sprite = Ui.Icon(FishMarkIcon);
            fishMarks.Add(go);
        }
    }

    Vector3 SquarePosition(int id) =>
        squares.TryGetValue(id, out var sq) ? sq.transform.position : new Vector3(board[id].X, 0, board[id].Z);

    // 同じマスに何人かいても重ならないよう、少しずらす
    Vector3 PiecePosition(int player, int square)
    {
        var offsets = new[] { new Vector3(-0.3f, 0, 0.3f), new Vector3(0.3f, 0, 0.3f), new Vector3(-0.3f, 0, -0.3f), new Vector3(0.3f, 0, -0.3f) };
        return SquarePosition(square) + offsets[player % 4] + Vector3.up * 0.6f;
    }

    // ---- 画面の更新 ----

    void RefreshAll()
    {
        if (game == null) return;
        if (S.Battle == null && battlePanel.gameObject.activeSelf) ShowBattle(false);
        RefreshHud(); // メイン画面の部品（UI仕様書）
        PaintBoard(true);
        BuildMenu();
        RefreshBattleScene(); // コマンドの押せる・押せない は メニューを作ったあとで
        if (debugPanel != null) BuildDebugPanel();
        SyncSnorlax();
        SyncLocks();
        SyncFishMarks();
        SyncShownMap(true); // 映すのは カメラが追っている人がいるマップだけ（3.4・16.7。あとから作られたマークも）
        TryCpu();
    }

    bool cpuThinking;
    readonly System.Random cpuRandom = new System.Random();
    public float cpuSeconds = 0.4f;

    bool IsCpu(int player) => player >= 0 && S.Players[player].IsCpu;
    bool HumanWaiting() => game.WaitingFor().Any(id => !IsCpu(id));

    // 入力を待っている人に CPU がいれば、少し間をおいて命令を出す
    void TryCpu()
    {
        if (cpuThinking || playing || S.Phase == Phase.GameOver) return;
        int id = game.WaitingFor().Where(IsCpu).DefaultIfEmpty(-1).First();
        if (id >= 0) StartCoroutine(CpuAct(id));
    }

    IEnumerator CpuAct(int id)
    {
        cpuThinking = true;
        yield return Wait(cpuSeconds);
        cpuThinking = false;
        if (playing || game.WaitingFor().All(x => x != id)) yield break;
        var c = Cpu.Decide(game, data, board, id, cpuRandom);
        if (c == null) { AddLog($"× {S.Players[id].Name}（CPU）が 動けません"); yield break; }
        Send(c);
    }

    string CharacterLabel(PlayerState p)
    {
        var c = data.Characters.FirstOrDefault(x => x.Id == p.Character);
        return c != null && c.Name != p.Name ? $"（{c.Name}）" : "";
    }

    // 移動中は、止まれるマスを光らせ、ほかをグレーにする（4.1）。道順はオレンジ
    void PaintBoard(bool show)
    {
        var move = show && S.Phase == Phase.Moving ? S.Move : null;
        var next = new HashSet<int>();
        if (move != null && S.CurrentPlayer.MoveMode == MoveMode.Step)
            foreach (int n in board[move.Path[move.Path.Count - 1]].Neighbors)
                if (game.Validate(new StepTo { Player = S.CurrentPlayer.Id, Square = n }) == null) next.Add(n);
        foreach (var kv in squares)
        {
            if (move == null) kv.Value.SetGlow(Color.white, 0);
            else if (move.Path.Contains(kv.Key) && kv.Key != move.Path[0]) kv.Value.SetGlow(new Color(1, 0.55f, 0.1f), 0.9f);
            else if (move.Reachable.Contains(kv.Key)) kv.Value.SetGlow(new Color(0.3f, 1, 1), 0.85f);
            else if (next.Contains(kv.Key)) kv.Value.SetGlow(new Color(0.6f, 1, 0.6f), 0.6f);
            else kv.Value.SetGlow(new Color(0.35f, 0.35f, 0.35f), 0.75f);
        }
        if (move == null) padSquare = -1;
        else if (padSquare >= 0 && squares.TryGetValue(padSquare, out var cursor)) cursor.SetGlow(Color.white, 1); // コントローラーで選んでいるマス
        HideSeaRoads();
    }

    // なみのりを覚えていない人の手番は、海上マスにつながる道を見せない（4.1.2）
    SugorokuRoad[] roads;
    void HideSeaRoads()
    {
        if (roads == null) roads = FindObjectsByType<SugorokuRoad>(FindObjectsInactive.Include); // 隠したあとも見つけられるように Include
        bool hide = !S.CurrentPlayer.Surf;
        bool Sea(SugorokuSquare sq) => sq != null && board.Squares.TryGetValue(sq.id, out var b) && b.Sea;
        foreach (var r in roads)
            if (r != null) r.gameObject.SetActive(!(hide && (Sea(r.squareA) || Sea(r.squareB))));
    }

    // ---- メニュー（場面ごとのボタン）----

    void Add(string label, Action action, KeyCode key = KeyCode.None, bool enabled = true) => menu.Add((label, action, key, enabled));
    // 絵つきの行：ボタンの左に絵を出す（ポケモンの行は そのポケモンの絵）
    readonly Dictionary<int, Sprite> menuArt = new Dictionary<int, Sprite>(); // 行の番号 → 絵
    void AddArt(Sprite art, string label, Action action, KeyCode key = KeyCode.None, bool enabled = true) { if (art != null) menuArt[menu.Count] = art; Add(label, action, key, enabled); }
    void AddMon(string species, string label, Action action, KeyCode key = KeyCode.None, bool enabled = true) => AddArt(PokemonIcon(species), label, action, key, enabled);
    bool Ok(Command c) => game.Validate(c) == null;

    void BuildMenu()
    {
        ClearMenu();
        promptSide = -1;
        if (S.Phase == Phase.GameOver)
        {
            Add("結果発表", ShowResults, KeyCode.Return);
            Add("エンドロール", () => ShowEndroll(0));
            Add("タイトルへもどる", ReturnToTitle, KeyCode.Escape);
            DrawMenu("ゲーム終了");
            return;
        }
        var p = S.CurrentPlayer;
        int me = p.Id;
        string title = "";
        if (S.Phase != Phase.Facility) facilityTab = "";
        if (subMenu != null) { subMenu(); return; }
        if (!HumanWaiting()) { DrawMenu($"{string.Join("・", game.WaitingFor().Select(id => S.Players[id].Name))}（CPU）が 考え中…"); return; }
        switch (S.Phase)
        {
            case Phase.Prepare:
                title = $"{p.Name}の ばん：サイコロを振る前";
                // E メニュー（UI仕様書 E）：いどう／どうぐ／マップ／ポケモン／ずかん／せってい。左に その行動の絵
                AddArt(Ui.Icon("くつ"), "いどう", () => { subMenu = () => DiceMenu(me); RefreshAll(); }, KeyCode.Return);
                AddArt(Ui.Icon("つりざお"), "どうぐ", () => { itemPokemonBag = false; subMenu = () => ItemMenu(me); RefreshAll(); }, KeyCode.None, !p.UsedItem && p.PokemonBag.Concat(p.TrainerBag).Any());
                AddArt(Ui.Icon("地図"), "マップ", StartExplore);
                AddArt(Ui.Ball(new Color(0.9f, 0.2f, 0.2f)), "ポケモン", () => { subMenu = () => PokemonMenu(me); RefreshAll(); });
                AddArt(Ui.Icon("本"), "ずかん", () => ShowDex(p));
                AddArt(Ui.Icon("スパナ"), "せってい", () => { subMenu = SystemMenu; RefreshAll(); });
                break;
            case Phase.Moving:
                int left = S.Move.Dice + S.Move.Extra - (S.Move.Path.Count - 1);
                title = Ok(new ConfirmMove { Player = me }) ? "ここに止まるなら「決定」（ほかのマスを選び直してもよい）"
                    : p.MoveMode == MoveMode.Highlight ? $"光っているマスをクリック（{S.Move.Dice}歩" + (S.Move.Extra > 0 ? $"〜{S.Move.Dice + S.Move.Extra}歩" : "") + "）" : $"となりの緑のマスをクリック（残り {left}歩）";
                Add("決定", () => Send(new ConfirmMove { Player = me }), KeyCode.Return, Ok(new ConfirmMove { Player = me }));
                if (p.MoveMode == MoveMode.Step) Add("1歩もどす", () => Send(new StepBack { Player = me }), KeyCode.Backspace, Ok(new StepBack { Player = me }));
                break;
            case Phase.Facility: title = FacilityMenu(p); break;
            case Phase.Battle: title = BattleMenu(); break;
        }
        DrawMenu(title);
    }

    // 「○でサイコロを振る」。×（Esc）で準備フェーズへ戻れる（16.9）
    void DiceMenu(int me)
    {
        var p = S.Players[me];
        Add("サイコロを振る", () => Send(new RollDice { Player = me }), KeyCode.Return);
        if (Ok(new Fish { Player = me })) Add("つり（動かずに、つったポケモンとバトル）", () => Send(new Fish { Player = me })); // 移動の代わり（4.1.2）
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu(p.FixedDice >= 0 ? $"Enterで すすむ（出目は {p.FixedDice}）" : p.DiceCount > 1 ? $"Enterで サイコロを振る（{p.DiceCount}個）" : "Enterで サイコロを振る");
    }

    // ポケモン：手持ち一覧。1体を選ぶと くわしく／入れ替え（UI仕様書 E）
    void PokemonMenu(int me)
    {
        var p = S.Players[me];
        OpenFrame("ポケモン", p.Name);
        for (int i = 0; i < p.Party.Count; i++)
        {
            int k = i;
            AddCard(p.Party[k], k == 0 ? "先頭" : "", () => { subMenu = () => PokemonActions(me, k); RefreshAll(); });
        }
        foreach (var m in p.Box) { var mon = m; AddCard(mon, "", () => { subMenu = () => BoxInfo(me, mon); RefreshAll(); }, KeyCode.None, true, true); } // 控えは 見るだけ
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu(p.Box.Count > 0 ? "どの子？（下は 控え）" : "どの子？");
    }

    // 控えの子の説明（見るだけ）
    void BoxInfo(int me, PokemonState m)
    {
        OpenFrame("ポケモン（控え）", S.Players[me].Name);
        Add("もどる", () => { subMenu = () => PokemonMenu(me); RefreshAll(); }, KeyCode.Escape); Detail(MonDetail(m));
        DrawMenu("控えの子は ポケモンセンターで 手持ちに入れられる");
    }

    void PokemonActions(int me, int slot)
    {
        var p = S.Players[me];
        OpenFrame("ポケモン", p.Name);
        string detail = MonDetail(p.Party[slot]);
        Add("くわしく見る", () => ShowPokemon(p, slot)); Detail(detail);
        Add("ならびを入れ替える", () => { subMenu = () => SwapMenu(me, slot); RefreshAll(); }, KeyCode.None, p.Party.Count > 1); Detail(detail);
        Add("もどる", () => { CloseOverlay(); subMenu = () => PokemonMenu(me); RefreshAll(); }, KeyCode.Escape); Detail(detail);
        DrawMenu($"{p.Party[slot].Species}を どうする？");
    }

    // 入れ替え：1体目を選んでから2体目を選ぶ（16.9）
    void SwapMenu(int me, int first)
    {
        var p = S.Players[me];
        OpenFrame("ポケモン", p.Name);
        for (int i = 0; i < p.Party.Count; i++)
        {
            int k = i;
            string label = k == first ? "この子と" : k == 0 ? "先頭" : "";
            if (first < 0) AddCard(p.Party[k], label, () => { subMenu = () => SwapMenu(me, k); RefreshAll(); });
            else AddCard(p.Party[k], label, () => Send(new SwapParty { Player = me, A = first, B = k }), KeyCode.None, k != first);
        }
        Add("もどる", () => { subMenu = first < 0 ? null : (Action)(() => PokemonMenu(me)); RefreshAll(); }, KeyCode.Escape);
        DrawMenu(first < 0 ? "入れ替える1体目を えらぶ" : $"{p.Party[first].Species}と 入れ替える子を えらぶ");
    }

    // どうぐ（16.9・16.6 #43）：2つの袋の一覧 → 使う相手 → 確認 → 効果。×で1つ前へ
    // 袋は「トレーナーの道具」「ポケモンの道具」を ←→（コントローラーは L1・R1）で切りかえる。使えるかどうかは 処理役（Validate）で決める
    bool itemPokemonBag; // いま見ている袋（false＝トレーナーの道具）

    void ItemMenu(int me)
    {
        var p = S.Players[me];
        var bag = itemPokemonBag ? p.PokemonBag : p.TrainerBag;
        string ballLine = p.Balls.Count(b => b.Value > 0) == 0 ? "なし" : string.Join("　", p.Balls.Where(b => b.Value > 0).Select(b => $"{b.Key}×{b.Value}"));
        OpenFrame("どうぐ", "ボール：" + ballLine);
        foreach (var item in bag.Distinct())
        {
            string name = item;
            int count = bag.Count(x => x == name);
            string label = count > 1 ? $"{name}×{count}" : name;
            if (game.TargetOf(name) != TargetKind.None)
                Add(label, () => { subMenu = () => TargetMenu(me, name); RefreshAll(); }, KeyCode.None, Targets(me, name).Any(t => Ok(new UseItem { Player = me, Item = name, Target = t.target })));
            else Add(label, () => { subMenu = () => ConfirmItem(me, name, -1, ""); RefreshAll(); }, KeyCode.None, Ok(new UseItem { Player = me, Item = name }));
            Detail(ItemDetail(p, name));
        }
        int trainerMax = (int)data.Required("trainer_bag"), pokemonMax = (int)data.Required("pokemon_bag");
        Add(itemPokemonBag ? $"← トレーナーの道具（{p.TrainerBag.Count}/{trainerMax}）" : $"ポケモンの道具（{p.PokemonBag.Count}/{pokemonMax}） →",
            () => { itemPokemonBag = !itemPokemonBag; RefreshAll(); }, itemPokemonBag ? KeyCode.LeftArrow : KeyCode.RightArrow);
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu(itemPokemonBag ? $"ポケモンの道具（{p.PokemonBag.Count}/{pokemonMax}）" : $"トレーナーの道具（{p.TrainerBag.Count}/{trainerMax}）");
    }

    // 確認（16.9）：○で使う、×で1つ前（相手を選んだなら相手の画面、ないなら一覧）へ
    void ConfirmItem(int me, string item, int target, string label)
    {
        var c = new UseItem { Player = me, Item = item, Target = target };
        var kind = game.TargetOf(item);
        string question = kind == TargetKind.None ? $"{item}を つかいますか？" : kind == TargetKind.Number ? $"{item}で {label} すすみますか？" : $"{item}を {label}に つかいますか？";
        Confirm(question, () => Send(c), () => { subMenu = target >= 0 || kind != TargetKind.None ? (Action)(() => TargetMenu(me, item)) : () => ItemMenu(me); RefreshAll(); }, Ok(c));
        DrawMenu(question);
    }

    // 道具を使う相手の候補（ポケモン・プレイヤー・マス・数）と、ボタンの文字
    IEnumerable<(int target, string label)> Targets(int me, string item)
    {
        var p = S.Players[me];
        switch (game.TargetOf(item))
        {
            case TargetKind.Pokemon: return p.Party.Select((m, i) => (i, $"{m.Species} {m.Hp}/{Pokemons.MaxHp(data, m)}"));
            case TargetKind.Player: return S.Players.Where(o => o.Id != me).Select(o => (o.Id, $"{o.Name}（{board[o.Square].Place}）"));
            case TargetKind.Number: return Enumerable.Range(0, 7).Select(n => (n, $"{n}マス"));
            default: return board.Squares.Values.OrderBy(s => s.Id).Select(s => (s.Id, $"{s.Place}　{s.Name}"));
        }
    }

    // 使う相手：処理役が「使える」と返す相手だけ並べる（妨害は 同じエリアの人だけ、8.3）
    void TargetMenu(int me, string item)
    {
        OpenFrame("どうぐ", item);
        foreach (var (target, label) in Targets(me, item))
        {
            var c = new UseItem { Player = me, Item = item, Target = target };
            if (!Ok(c)) continue;
            var kind = game.TargetOf(item);
            string name = kind == TargetKind.Pokemon ? S.Players[me].Party[target].Species : kind == TargetKind.Player ? S.Players[target].Name : label; // 確認の文に入れる名前
            Action pick = () => { subMenu = () => ConfirmItem(me, item, c.Target, name); RefreshAll(); };
            if (kind == TargetKind.Pokemon) AddCard(S.Players[me].Party[target], "", pick); else Add(label, pick);
        }
        Add("もどる", () => { subMenu = () => ItemMenu(me); RefreshAll(); }, KeyCode.Escape);
        DrawMenu(game.TargetOf(item) == TargetKind.Square ? $"{item}：どこに？" : game.TargetOf(item) == TargetKind.Number ? $"{item}：何マス進む？" : $"{item}を だれに使う？");
    }

    int exchangePoints = 1; // 換金所で選んでいるポイント
    string facilityTab = ""; // 施設の中で開いているところ（ショップの buy・sell、センターの swap・held・job・lab）。"" は入口

    // ショップ：個数を選んで 買う・売る（相談31）。買えるのは お金と袋が足りるところまで
    void QuantityMenu(int me, string item, bool sell, int n)
    {
        var p = S.Players[me];
        int price = sell ? game.SellPrice(item) : game.BuyPrice(item);
        int have = p.Balls.TryGetValue(item, out int balls) ? balls : p.PokemonBag.Concat(p.TrainerBag).Count(x => x == item);
        int max = Mathf.Clamp(sell ? have : p.Money / Mathf.Max(1, price), 1, 99);
        n = Mathf.Clamp(n, 1, max);
        string verb = sell ? "売る" : "買う";
        void Set(int v) { subMenu = () => QuantityMenu(me, item, sell, v); RefreshAll(); }
        OpenFrame(verb, $"所持金 {p.Money:#,0}円");
        Add("－1", () => Set(n - 1), KeyCode.LeftArrow, n > 1);
        Add("＋1", () => Set(n + 1), KeyCode.RightArrow, n < max);
        Add("＋10", () => Set(n + 10), KeyCode.None, n < max);
        Add($"{n}個 {verb}（{price * n:#,0}円）", () => SendMany(() => sell ? (Command)new Sell { Player = me, Item = item } : new Buy { Player = me, Item = item }, n,
            done => $"{item}を {done}個 {(sell ? "売った" : "買った")}（{price * done:#,0}円）"), KeyCode.Return);
        Add("やめる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        for (int i = 0; i < menu.Count; i++) menuDetail[i] = ItemDetail(p, item) + $"\n\n1個 {price:#,0}円\n{n}個で {price * n:#,0}円";
        DrawMenu($"{item}を いくつ {verb}？");
    }

    string FacilityMenu(PlayerState _)
    {
        var f = S.Facility;
        int me = f.Player; // 袋がいっぱいのときは、物をもらった人が選ぶ（手番の人とは限らない）
        var p = S.Players[me];
        switch (f.Kind)
        {
            case FacilityKind.Event:
            case FacilityKind.RocketBase:
            case FacilityKind.Trade:
            case FacilityKind.BagFull:
            case FacilityKind.BoxFull:
                for (int i = 0; i < f.Options.Count; i++)
                {
                    var c = new ChooseOption { Player = me, Index = i };
                    var key = i == f.Options.Count - 1 ? KeyCode.Escape : KeyCode.None;
                    // 控えがいっぱい：控えの子と、来た子（最後の行）に絵
                    if (f.Kind == FacilityKind.BoxFull) AddMon(i < p.Box.Count ? p.Box[i].Species : f.PendingPokemon.Species, f.Options[i], () => Send(c), key);
                    else Add(f.Options[i], () => Send(c), key);
                }
                return $"{p.Name}さん：{f.Prompt}";
            case FacilityKind.JobOffer: // 職業の解放（16.4 #27）：選択肢の 0 が「就く」、1 が「あとで」
                Confirm($"「{f.PendingJob}」に いま つきますか？" + (p.Job != "" ? $"\n（今は {p.Job}）" : ""), () => Send(new ChooseOption { Player = me, Index = 0 }), () => Send(new ChooseOption { Player = me, Index = 1 }));
                return $"{p.Name}さん：{f.Prompt}";
            case FacilityKind.Exchange:
            {
                // 1,000円単位で、何ポイント換えるかを選ぶ（2.1）
                int max = game.ExchangeMax(p), rate = (int)data.Required("yen_per_point");
                exchangePoints = Mathf.Clamp(exchangePoints, 1, Mathf.Max(1, max));
                void Set(int n) { exchangePoints = Mathf.Clamp(n, 1, Mathf.Max(1, max)); RefreshAll(); }
                Add("－10", () => Set(exchangePoints - 10), KeyCode.None, exchangePoints > 1);
                Add("－1", () => Set(exchangePoints - 1), KeyCode.LeftArrow, exchangePoints > 1);
                Add("＋1", () => Set(exchangePoints + 1), KeyCode.RightArrow, exchangePoints < max);
                Add("＋10", () => Set(exchangePoints + 10), KeyCode.None, exchangePoints < max);
                Add("全部", () => Set(max), KeyCode.None, exchangePoints < max);
                var c = new ExchangePoints { Player = me, Points = exchangePoints };
                Add($"{exchangePoints}ポイントに 換える（{exchangePoints * rate:#,0}円）", () => Send(c), KeyCode.Return, Ok(c));
                Add("換えない", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                OpenFrame("ポイント交換所", $"所持金 {p.Money:#,0}円");
                for (int i = 0; i < menu.Count; i++) menuDetail[i] = $"{rate:#,0}円で 1ポイント\n\n換える数：{exchangePoints}ポイント\nはらう お金：{exchangePoints * rate:#,0}円\nのこる お金：{p.Money - exchangePoints * rate:#,0}円\n\nいちどに 換えられるのは {max}ポイントまで";
                return f.Prompt;
            }
            case FacilityKind.Rival:
            {
                bool owner = game.ActsAsOwner(f, p);
                int cables = p.PokemonBag.Count(x => x == "通信ケーブル");
                for (int i = 0; i < p.Party.Count; i++)
                    foreach (var target in game.RivalTargets(p.Party[i]))
                    {
                        var c = new RivalEvolve { Player = me, Slot = i, Target = target };
                        Add($"交換進化：{p.Party[i].Species}→{target}", () => Send(c), KeyCode.None, Ok(c));
                    }
                foreach (var (species, level, need) in game.RivalTrades(p, f.Square))
                {
                    var c = new RivalTrade { Player = me, Species = species };
                    Add($"交換：{species} Lv{level}（コスト{need:0.#}以上の子を 1体わたす）", () => Send(c), KeyCode.None, Ok(c));
                }
                Add("立ち去る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                OpenFrame("ライバルマス", $"通信ケーブル {cables}個");
                return owner ? (f.Owner == me ? "自分のライバルマス" : "へんそう中") + "：交換進化が 無料でできる" : $"{S.Players[f.Owner].Name}の ライバルマス：交換進化には 通信ケーブルが要る（{cables}個 持っている）";
            }
            case FacilityKind.Shop:
            {
                // 買う・売るを最初に選び、品目を選んだあとに 個数を選ぶ（相談31）
                OpenFrame((f.Prompt != "" ? f.Prompt : "ショップ") + (S.WeekEvent == "セール" ? "　セール中！" : ""), $"所持金 {p.Money:#,0}円");
                var sellable = p.PokemonBag.Concat(p.TrainerBag).Concat(p.Balls.Where(x => x.Value > 0).Select(x => x.Key)).Distinct().Where(x => Ok(new Sell { Player = me, Item = x })).ToList(); // ボールも（マスターボールは売れないので出ない、8.9）
                if (facilityTab == "buy" || facilityTab == "sell")
                {
                    bool sell = facilityTab == "sell";
                    foreach (var item in sell ? sellable : f.Goods)
                    {
                        string it = item;
                        Add($"{it}　{(sell ? game.SellPrice(it) : game.BuyPrice(it)):#,0}円", () => { subMenu = () => QuantityMenu(me, it, sell, 1); RefreshAll(); }, KeyCode.None, sell || Ok(new Buy { Player = me, Item = it }));
                        Detail(ItemDetail(p, it));
                    }
                    Add("もどる", () => { facilityTab = ""; RefreshAll(); }, KeyCode.Escape);
                    return sell ? "なにを 売る？" : "なにを 買う？（買えないものは うすい字）";
                }
                Add("買う", () => { facilityTab = "buy"; RefreshAll(); });
                Add("売る", () => { facilityTab = "sell"; RefreshAll(); }, KeyCode.None, sellable.Count > 0);
                Add("店を出る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                return "いらっしゃいませ！";
            }
            case FacilityKind.Center:
            {
                // 最初に「入れ替え／もちもの／職業（／進化サービス）／出る」を選び、それぞれを開く（相談34）
                OpenFrame(f.IsLab ? "オーキド博士の研究室" : "ポケモンセンター", (p.Job != "" ? $"職業：{p.Job}　" : "") + $"所持金 {p.Money:#,0}円");
                void Tab(string tab) { facilityTab = tab; RefreshAll(); }
                var heldItems = p.PokemonBag.Where(x => data.FindItem(x)?.IsHeldItem == true).Distinct().ToList();
                switch (facilityTab)
                {
                    case "swap": // 上の大きいカードは手持ち（押すと あずける）、下の小さいカードは控え（押すと 手持ちに）
                        for (int i = 0; i < p.Party.Count; i++) { var c = new CenterSwap { Player = me, PartySlot = i }; AddCard(p.Party[i], "あずける", () => Send(c), KeyCode.None, Ok(c)); }
                        for (int i = 0; i < p.Box.Count; i++) { var c = new CenterSwap { Player = me, BoxIndex = i }; AddCard(p.Box[i], "", () => Send(c), KeyCode.None, Ok(c), true); }
                        Add("もどる", () => Tab(""), KeyCode.Escape);
                        string why = p.Party.Count > 0 ? game.Validate(new CenterSwap { Player = me, PartySlot = 0 }) : null; // あずけられない理由（控えがいっぱい など）
                        return "手持ち（上）を押すと あずける。控え（下）を押すと 手持ちに入れる" + (why != null ? $"　※{why}" : "");
                    case "held": // もちものの着けはずし（8.2）
                        for (int i = 0; i < p.Party.Count; i++)
                        {
                            if (p.Party[i].HeldItem != "") { var c = new HoldItem { Player = me, Slot = i }; Add($"はずす：{p.Party[i].Species}の {p.Party[i].HeldItem}", () => Send(c), KeyCode.None, Ok(c)); Detail(ItemDetail(p, p.Party[i].HeldItem)); }
                            foreach (var item in heldItems) { var c = new HoldItem { Player = me, Slot = i, Item = item }; Add($"もたせる：{item} → {p.Party[i].Species}", () => Send(c), KeyCode.None, Ok(c)); Detail(ItemDetail(p, item)); }
                        }
                        Add("もどる", () => Tab(""), KeyCode.Escape);
                        return "もちものを もたせる・はずす";
                    case "job":
                        foreach (var job in p.UnlockedJobs) { var c = new ChangeJob { Player = me, Job = job }; Add(job + (job == p.Job ? "（今の職業）" : ""), () => Send(c), KeyCode.None, Ok(c)); Detail($"{job}\n\n{data.Jobs.FirstOrDefault(j => j.Name == job)?.Effect}"); }
                        Add("もどる", () => Tab(""), KeyCode.Escape);
                        return "どの職業に つく？";
                    case "lab":
                        for (int i = 0; i < p.Party.Count; i++)
                            foreach (var target in Pokemons.LabTargets(data, p.Party[i]))
                            {
                                var c = new LabEvolve { Player = me, Slot = i, Target = target };
                                Add($"{p.Party[i].Species} → {target}", () => Send(c), KeyCode.None, Ok(c)); Detail($"進化サービス\n\n{p.Party[i].Species}を {target}に 進化させる\n\nねだん：{game.LabPrice(p):#,0}円");
                            }
                        Add("もどる", () => Tab(""), KeyCode.Escape);
                        return $"進化サービス（1回 {game.LabPrice(p):#,0}円）";
                }
                Add("入れ替え（手持ちと 控え）", () => Tab("swap"), KeyCode.None, p.Party.Count > 1 || p.Box.Count > 0); Detail($"手持ち {p.Party.Count}体\n控え {p.Box.Count}体");
                Add("もちもの", () => Tab("held"), KeyCode.None, heldItems.Count > 0 || p.Party.Any(m => m.HeldItem != "")); Detail("ポケモンに 道具を もたせる・はずす");
                Add("職業", () => Tab("job"), KeyCode.None, p.UnlockedJobs.Count > 0); Detail(p.Job != "" ? $"今の職業：{p.Job}" : "まだ 職業に ついていない");
                if (f.IsLab) { Add("進化サービス", () => Tab("lab"), KeyCode.None, p.Party.Any(m => Pokemons.LabTargets(data, m).Any())); Detail($"お金を はらって ポケモンを 進化させる\n1回 {game.LabPrice(p):#,0}円"); }
                Add(f.IsLab ? "研究室を出る" : "センターを出る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                return "みんな 元気に なった！　なにを する？";
            }
            default: // 他人のファンマスの有料回復（16.4 #26）。いいえは 立ち去る
                bool canPay = Ok(new AcceptHeal { Player = me });
                Confirm($"{S.Players[f.Owner].Name}の ファンの家\n{f.Price}円 はらって かいふくしますか？" + (canPay ? "" : "（お金が たりない）"),
                    () => Send(new AcceptHeal { Player = me }), () => Send(new LeaveFacility { Player = me }), canPay);
                return $"{S.Players[f.Owner].Name}の ファンの家";
        }
    }

    string BattleMenu()
    {
        var b = S.Battle;
        switch (b.Step)
        {
            case BattleStep.Select:
            {
                var side = b.Sides.First(s => s.NeedsSelect && !IsCpu(s.Player));
                // プレイヤー戦は □○△（キーボードは A S D）で押した瞬間に決まる。ボタンは バトル画面に出す（BlindSelect、RefreshBattleScene）
                bool blind = BlindSelect;
                if (blind) promptSide = System.Array.IndexOf(b.Sides, side);
                var keys = new[] { KeyCode.A, KeyCode.S, KeyCode.D };
                for (int i = 0; i < side.Team.Count; i++)
                {
                    var m = side.Team[i];
                    var c = new SelectPokemon { Player = side.Player, Slot = i };
                    Add($"{m.Species} Lv{m.Level} {m.Hp}/{Pokemons.MaxHp(data, m)}", () => Send(c), blind && i < keys.Length ? keys[i] : KeyCode.None, Ok(c));
                }
                return $"{side.Name}さん、出すポケモンを えらんでください（相手には見えません）" + FoeLeadLine(side);
            }
            // コマンドは バトル画面の ポケモンの下で選ぶ（UI仕様書 7.2）。ここでは 押したときの動きとキーだけを決める（並びは □ ○ △ R1）
            // コントローラー：□ ○ △ R1。キーボード：A S D F（攻撃・防御とも同じ記号は同じキー）
            case BattleStep.Commands:
            {
                var a = b.Sides[b.Attacker];
                var d = b.Sides[1 - b.Attacker];
                if (b.AttackCmd == null && a.Player >= 0 && !IsCpu(a.Player))
                {
                    int pl = a.Player;
                    promptSide = b.Attacker;
                    Add("こうげき", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Physical }), KeyCode.A, Ok(new ChooseAttack { Player = pl, Attack = Attack.Physical }));
                    Add("とくこう", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Special }), KeyCode.S, Ok(new ChooseAttack { Player = pl, Attack = Attack.Special }));
                    Add("必殺", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Super }), KeyCode.D, Ok(new ChooseAttack { Player = pl, Attack = Attack.Super }));
                    var usable = S.Players[pl].PokemonBag.Distinct().Where(i => Ok(new ChooseAttack { Player = pl, Attack = Attack.Item, Item = i })).ToList();
                    Add("どうぐ", () => { subMenu = () => BattleItemMenu(pl, usable); RefreshAll(); }, KeyCode.F, usable.Count > 0);
                    return $"{a.Name}さん（攻撃）：{a.Current.Species}の コマンド";
                }
                if (b.DefenseCmd == Defense.Asleep) return $"{d.Current.Species}は ねむっている（守れない）"; // 守る番も止まる（相談10-2）
                int dp = d.Player;
                promptSide = 1 - b.Attacker;
                Add("ぼうぎょ", () => Send(new ChooseDefense { Player = dp, Defense = Defense.Guard }), KeyCode.A);
                Add("とくぼう", () => Send(new ChooseDefense { Player = dp, Defense = Defense.SpecialGuard }), KeyCode.S);
                Add("カウンター", () => Send(new ChooseDefense { Player = dp, Defense = Defense.Counter }), KeyCode.D);
                string flee = b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament ? "降参" : "にげる";
                Add(flee, () => { subMenu = () => FleeConfirm(dp, flee); RefreshAll(); }, KeyCode.F, Ok(new ChooseDefense { Player = dp, Defense = Defense.Flee }));
                // L1 こうたい（相談24）：フルバトルで 戦える控えがいるときだけ押せる（キーボードは G）
                bool canSwitch = Enumerable.Range(0, d.Team.Count).Any(k => Ok(new SwitchPokemon { Player = dp, Slot = k }));
                Add("こうたい", () => { switchMenu = () => SwitchMenu(dp); subMenu = switchMenu; RefreshAll(); }, KeyCode.G, canSwitch);
                return $"{d.Name}さん（防御）：{d.Current.Species}の コマンド";
            }
            // 先攻・後攻のカード：←→ で選んで ○（Enter）。カードを押しても選べる
            case BattleStep.Initiative:
            {
                int pl = b.Sides[0].Player;
                Add("ひだりの カード", () => MoveCardCursor(-1), KeyCode.LeftArrow);
                Add("みぎの カード", () => MoveCardCursor(1), KeyCode.RightArrow);
                Add("この カードに する", () => Send(new PickCard { Player = pl, Card = cardCursor }), KeyCode.Return);
                return $"{b.Sides[0].Name}さん：カードを 1枚 えらんでください";
            }
            case BattleStep.Ball:
            {
                var p = S.Players[b.Sides[0].Player];
                foreach (var kv in p.Balls.Where(x => x.Value > 0)) { var c = new ThrowBall { Player = p.Id, Ball = kv.Key }; Add($"{kv.Key}（{kv.Value}個）", () => Send(c), KeyCode.None, Ok(c)); }
                Add(b.Safari ? "にげる" : "投げない", () => Send(new ThrowBall { Player = p.Id }), KeyCode.Escape);
                return b.Safari ? "サファリボールを 投げる？（はずれると にげられることがある）" : "ボールを 投げる？";
            }
            default:
            {
                var winner = S.Players[b.Sides[b.Winner].Player];
                var loser = S.Players[b.Sides[1 - b.Winner].Player];
                OpenFrame("ほうび", $"{winner.Name}の 勝ち！");
                foreach (var item in loser.PokemonBag.Concat(loser.TrainerBag).Distinct()) { string it = item; Add($"うばう：{it}", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Item, Item = it })); Detail(ItemDetail(loser, it)); }
                Add("ファンを1人うばう", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Fan }), KeyCode.None, Ok(new ChooseReward { Player = winner.Id, Reward = Reward.Fan }));
                Add("ポケモンを交換", () => { subMenu = () => TradeMenu(winner, loser, -1); RefreshAll(); }, KeyCode.None, !b.Surrendered && loser.Party.Count > 1); // 相手が1体だけなら奪えない
                Add("許す", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Forgive }), KeyCode.Escape);
                return $"{winner.Name}さん、ほうびを えらんでください";
            }
        }
    }

    // 相手は 先頭だけ見える（16.5・7.11）。プレイヤーの相手は 選び直すので 出てくるとは限らない（NPC は いつも先頭を出す）
    // バトルの はじめ（まだ1体も出していない）だけ。倒れたあとの選び直しでは 相手の場の子が見えている
    string FoeLeadLine(BattleSide side)
    {
        if (side.Participated.Count > 0) return "";
        var b = S.Battle;
        var foe = b.Sides[1 - System.Array.IndexOf(b.Sides, side)];
        var lead = game.Lead(foe);
        var sp = Pokemons.Of(data, lead);
        return $"\nあいての せんとう：{lead.Species} Lv{lead.Level}（じゃくてん：{(sp.Weakness != "" ? sp.Weakness : "なし")}）" + (foe.Player >= 0 ? "　※あいても えらぶので、出てくるとは かぎらない" : "");
    }

    // 守る番の交代：出す子を選ぶ（相談24）。□○△（キーボードは A S D）が 手持ちの1〜3番目。押した瞬間に決まる。×（Esc）で 守りのコマンドへ
    // 行は バトル画面の □○△ のボタンに出す（RefreshBattleScene）。プレイヤー戦でも 相手には「選択済み」としか見えない
    void SwitchMenu(int player)
    {
        var side = S.Battle.Sides.First(s => s.Player == player);
        promptSide = Array.IndexOf(S.Battle.Sides, side); // バトル画面で この側の □○△ に 子の名前を出す
        var keys = new[] { KeyCode.A, KeyCode.S, KeyCode.D };
        for (int i = 0; i < side.Team.Count && i < keys.Length; i++)
        {
            var m = side.Team[i];
            var c = new SwitchPokemon { Player = player, Slot = i };
            Add($"{m.Species} Lv{m.Level} {m.Hp}/{Pokemons.MaxHp(data, m)}", () => Send(c), keys[i], Ok(c));
        }
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu($"{side.Name}さん：出す子を えらぶ（相手の攻撃は 出てきた子が受けます）");
    }

    // にげる・降参の確認（7.3「押したら確認を1回挟む」）。いいえは コマンド選びへ
    void FleeConfirm(int player, string flee)
    {
        string question = flee == "降参" ? "こうさんしますか？\n（負けに なります）" : "にげますか？";
        Confirm(question, () => Send(new ChooseDefense { Player = player, Defense = Defense.Flee }), () => { subMenu = null; RefreshAll(); });
        DrawMenu(question);
    }

    // ポケモン交換（7.7）：相手の手持ちから1体 → 差し出す自分の子（手持ちか控え。コストが足りない子は押せない）
    void TradeMenu(PlayerState winner, PlayerState loser, int take)
    {
        if (take < 0)
        {
            OpenFrame("ポケモン交換", $"{loser.Name}の 手持ち");
            for (int i = 0; i < loser.Party.Count; i++) { int k = i; var m = loser.Party[i]; AddCard(m, $"コスト{Pokemons.Cost10(data, m) / 10.0:0.0}", () => { subMenu = () => TradeMenu(winner, loser, k); RefreshAll(); }); }
            Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
            DrawMenu($"{winner.Name}さん：{loser.Name}さんの どの子が ほしい？");
            return;
        }
        void Offer(PokemonState m, int index, bool box)
        {
            var c = new ChooseReward { Player = winner.Id, Reward = Reward.Trade, Take = take, Give = index, GiveFromBox = box };
            AddCard(m, $"コスト{Pokemons.Cost10(data, m) / 10.0:0.0}", () => Send(c), KeyCode.None, Ok(c), box);
        }
        OpenFrame("ポケモン交換", $"{winner.Name}の 手持ちと 控え");
        for (int i = 0; i < winner.Party.Count; i++) Offer(winner.Party[i], i, false);
        for (int i = 0; i < winner.Box.Count; i++) Offer(winner.Box[i], i, true);
        Add("もどる", () => { subMenu = () => TradeMenu(winner, loser, -1); RefreshAll(); }, KeyCode.Escape);
        DrawMenu($"{loser.Party[take].Species}（コスト{Pokemons.Cost10(data, loser.Party[take]) / 10.0:0.0}）の かわりに 差し出す子（同じか高いコスト）");
    }

    void BattleItemMenu(int player, List<string> usable)
    {
        foreach (var item in usable) { string it = item; Add(it, () => Send(new ChooseAttack { Player = player, Attack = Attack.Item, Item = it })); }
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("どの道具を使う？（このラウンドは攻撃しない）");
    }

    void DrawMenu(string title)
    {
        prompt.text = title;
        Ui.Clear(buttons);
        bool frame = FrameOpen; // まん中の枠に出す（施設・ポケモン・どうぐ）。そのあいだ E メニューは隠す
        frameRoot.gameObject.SetActive(frame);
        ballPickOpen = !frame && BallPick; // ボール選びは バトル画面の中に（相談33）
        ballBox.gameObject.SetActive(ballPickOpen);
        // 項目が多いときは2列に
        var grid = buttons.GetComponent<GridLayoutGroup>();
        grid.constraintCount = menu.Count > 7 ? 2 : 1;
        grid.cellSize = new Vector2(menu.Count > 7 ? 276 : 560, menu.Count > 14 ? 40 : 50);
        var made = frame ? DrawFrame(title) : ballPickOpen ? DrawBalls(title) : new List<Button>();
        for (int i = 0; i < menu.Count && !frame && !ballPickOpen; i++)
        {
            var (label, action, _, enabled) = menu[i];
            var b = Ui.Button(buttons, i < 9 ? $"{i + 1}. {label}" : label, action);
            b.interactable = enabled;
            var faded = new Color(1, 1, 1, enabled ? 1 : 0.35f); // 使えないときは 絵も文字も薄く
            b.GetComponentInChildren<Text>().color = faded;
            if (menuArt.TryGetValue(i, out var art))
            {
                float h = grid.cellSize.y - 4;
                var icon = Ui.Panel(b.transform, "Mon", new Vector2(0, 0.5f), new Vector2(0, 0.5f), faded);
                icon.sprite = art; icon.preserveAspect = true; icon.raycastTarget = false;
                icon.rectTransform.sizeDelta = new Vector2(h, h); icon.rectTransform.anchoredPosition = new Vector2(6 + h / 2, 0);
                b.GetComponentInChildren<Text>().rectTransform.offsetMin = new Vector2(h + 10, 0); // 文字は絵の右から
            }
            made.Add(b);
        }
        PadHighlight(title, made); // コントローラーのカーソル
        if (frame) ShowDetail(menuCursor);
        if (ballPickOpen && menuCursor < made.Count) made[menuCursor].GetComponentInChildren<Text>().color = Color.black; // 黄色の上は 黒い字
        // カードは 色を変えると字が読めないので、金色のふちで 選んでいることを見せる
        if (frame && menuCursor < made.Count && menuCards.TryGetValue(menuCursor, out var picked))
        {
            made[menuCursor].image.color = picked.mon.Fainted ? FaintedColor : CardColor;
            var edge = made[menuCursor].gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(1f, 0.85f, 0.4f); edge.effectDistance = new Vector2(5, 5);
        }
        // バトル中、コマンドとカードは バトル画面で選ぶので E メニューは隠す（選ぶものがないときも）
        if (menuPanel != null) menuPanel.gameObject.SetActive(!frame && !ballPickOpen && !EventOpen && !ConfirmOpen && !(battlePanel.gameObject.activeSelf && (menu.Count == 0 || SceneInput)));
        if (EventOpen && ConfirmOpen) { Ui.Clear(eventChoices); eventPrompt.text = ""; } // 確認は まん中の枠で（後ろの選択肢は出さない）
        else if (EventOpen) DrawEventChoices(title); // イベント画面では 選択肢をセリフ欄の右に
        DrawConfirm();
    }

    // ---- 入力 ----

    void Update()
    {
        if (PadUpdate()) return; // コントローラー（登録・抜けたとき・五十音表・メニュー・バトル・移動）
        if (kanaFor >= 0) { KanaUpdate(); return; } // 名前の五十音表（ゲームが始まる前）
        if (game == null) return;
        zoom = Mathf.Clamp(zoom - Input.mouseScrollDelta.y * 0.1f, 0.3f, 3f);
        if (Input.GetKeyDown(KeyCode.F1) && game.DebugEnabled) ToggleDebug();
        PanCamera();
        if (playing) return;
        if (DexOpen) { DexKeys(); return; } // 図鑑（GameRunner.Info.cs）
        // 入力欄に文字を打っている間は、数字キーなどのショートカットを使わない
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected.GetComponent<InputField>() is InputField input && input.isFocused) return;
        if (exploring) { ExploreUpdate(); }
        if (debugWarp && Input.GetMouseButtonDown(0) && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject())) { DebugWarpClick(); return; }
        for (int i = 0; i < menu.Count; i++)
        {
            var (_, action, key, enabled) = menu[i];
            if (!enabled) continue;
            if ((i < 9 && Input.GetKeyDown(KeyCode.Alpha1 + i)) || (key != KeyCode.None && Input.GetKeyDown(key))) { action(); return; }
        }
        if (Input.GetMouseButtonDown(0) && S.Phase == Phase.Moving && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            ClickSquare();
    }

    void ClickSquare()
    {
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(Input.mousePosition), out var hit)) return;
        var sq = hit.collider.GetComponentInParent<SugorokuSquare>();
        if (sq == null) return;
        int me = S.CurrentPlayer.Id;
        if (S.CurrentPlayer.MoveMode == MoveMode.Highlight) Send(new ChooseDestination { Player = me, Square = sq.id });
        else Send(new StepTo { Player = me, Square = sq.id });
    }

    // 画面を動かす：WASD・矢印キー（コントローラーは右スティック）。UIの下に隠れたマスを押せるように。手番が変わる・コマが動くと もどる
    // バトル中は A/S/D が防御のキーなので動かさない。探索中は探索の動きにまかせる
    Vector3 camPan;
    void PanCamera()
    {
        if (exploring || S.Phase == Phase.Battle || overlay != null) return;
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected.GetComponent<InputField>() is InputField input && input.isFocused) return;
        var move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")) + PadStick();
        if (move.sqrMagnitude > 0.04f) camPan += new Vector3(move.x, 0, move.y).normalized * 15f * zoom * Time.deltaTime;
    }

    void LateUpdate()
    {
        // HPが赤いあいだ ピコピコ（バトル画面が出ていて、プレイヤーのポケモンのHPが2割以下）
        if (sound != null) sound.SetAlarm(battlePanel != null && battlePanel.gameObject.activeInHierarchy && cards[0] != null && cards.Any(c => c.Danger));
        SlideMessage(); // G セリフ欄
        AnimateWeather(); // バトルの天気（GameRunner.Battle.cs）
        AnimateEvent();   // イベント画面のセリフ（GameRunner.Event.cs）
        var cam = Camera.main;
        if (cam == null || cameraTarget == null) return;
        if (exploring) { ClampExploreCamera(cam); AimSquare(); } // 見渡しは 今いるマップの中だけ。まん中の目印のマスを選ぶ（16.7）
        SyncShownMap(false); // 手番やワープで 映すマップが変わったら 切り替える（3.4）
        Vector3 want = cameraTarget.position + camPan + cameraOffset * zoom;
        cam.transform.position = Vector3.Lerp(cam.transform.position, want, 1 - Mathf.Exp(-6 * Time.deltaTime));
        cam.transform.LookAt(cam.transform.position - cameraOffset);
        MoveBuddies(); // 先頭のポケモンがコマについていく
    }

    // ---- バトルのポケモン表示（タイプの色の丸と名前。Resources に絵があればそれを使う、17.7）----

    public Sprite PokemonSprite(string species) => Resources.Load<Sprite>($"{pokemonSpriteFolder}/{species}");

    // 歩く絵（HGSS の連れ歩きの正面2コマ。Resources/PokemonIcons/<名前>_1・_2）。図鑑の493番までの245体。なければ null
    readonly Dictionary<string, Sprite> walkSprites = new Dictionary<string, Sprite>();
    public Sprite WalkSprite(string species, int frame)
    {
        string key = species + "_" + frame;
        if (!walkSprites.TryGetValue(key, out var s)) walkSprites[key] = s = Resources.Load<Sprite>("PokemonIcons/" + key);
        return s;
    }

    // 一覧に並べる小さい絵：歩く絵の1コマ目（なければ バトル用の絵）の、まわりの透明な余白を切ったもの。絵がなければ null
    readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
    public Sprite PokemonIcon(string species)
    {
        if (icons.TryGetValue(species, out var icon)) return icon;
        return icons[species] = Trim(WalkSprite(species, 1) ?? PokemonSprite(species), new Vector2(0.5f, 0.5f));
    }

    // 絵のまわりの透明な余白を切る（読めない絵・全部透明なら そのまま）。pivot は切ったあとの絵の基準点
    static Sprite Trim(Sprite full, Vector2 pivot)
    {
        if (full == null || !full.texture.isReadable) return full;
        var tex = full.texture;
        var r = full.rect;
        var px = tex.GetPixels32();
        int x0 = (int)r.xMax, y0 = (int)r.yMax, x1 = -1, y1 = -1;
        for (int y = (int)r.y; y < (int)r.yMax; y++)
            for (int x = (int)r.x; x < (int)r.xMax; x++)
                if (px[y * tex.width + x].a > 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return x1 < 0 ? full : Sprite.Create(tex, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), pivot);
    }

    // バトルの絵：余白を切って、足もと（下の中央）を基準に
    readonly Dictionary<string, Sprite> battleSprites = new Dictionary<string, Sprite>();
    public Sprite BattleSprite(string species)
    {
        if (!battleSprites.TryGetValue(species, out var s)) battleSprites[species] = s = Trim(PokemonSprite(species), new Vector2(0.5f, 0));
        return s;
    }

    // 並べる箱の中に置く ポケモンの絵（縦横の比はそのまま）
    Image MonIcon(Transform parent, string species, float size)
    {
        var img = new GameObject("Mon", typeof(RectTransform), typeof(Image), typeof(LayoutElement)).GetComponent<Image>();
        img.transform.SetParent(parent, false);
        img.sprite = PokemonIcon(species); img.preserveAspect = true; img.raycastTarget = false;
        img.enabled = img.sprite != null;
        var le = img.GetComponent<LayoutElement>(); le.preferredWidth = le.preferredHeight = le.minWidth = le.minHeight = size;
        return img;
    }

    // ボールの上半分の色（モンスターボール 赤・スーパー 青・ハイパー 黄・マスター 紫）
    static Color BallColor(string ball) =>
        ball.Contains("スーパー") ? new Color(0.2f, 0.45f, 0.95f) : ball.Contains("ハイパー") ? new Color(0.95f, 0.8f, 0.15f)
        : ball.Contains("マスター") ? new Color(0.55f, 0.25f, 0.75f) : new Color(0.9f, 0.15f, 0.15f);

    public static Color TypeColor(string type)
    {
        switch (type)
        {
            case "草": return new Color(0.35f, 0.75f, 0.3f);
            case "炎": return new Color(0.95f, 0.4f, 0.2f);
            case "水": return new Color(0.3f, 0.55f, 0.95f);
            case "雷": return new Color(0.95f, 0.85f, 0.2f);
            case "超": return new Color(0.65f, 0.35f, 0.85f);
            case "フェアリー": return new Color(0.95f, 0.6f, 0.8f);
            case "闘": return new Color(0.7f, 0.45f, 0.25f);
            case "悪": return new Color(0.3f, 0.3f, 0.35f);
            case "鋼": return new Color(0.7f, 0.72f, 0.78f);
            case "ドラゴン": return new Color(0.8f, 0.65f, 0.25f);
            default: return new Color(0.8f, 0.8f, 0.75f);
        }
    }
}
