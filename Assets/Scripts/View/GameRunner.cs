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
    public float messageSeconds = 0.5f;  // 1つの出来事を見せる時間（スペースキーで早送り）
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
        Ui.Button(col, "はじめから", ShowSetup, 420, 76);
        // ponytail: セーブ・ロードを作るまで押せない
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

    // 名前はひらがなと「ー」だけ（16.1）
    static bool IsNameChar(char c) => (c >= '\u3041' && c <= '\u3096') || c == 'ー';

    // キャラクターの色（キャラクター.csv の「色」）。なければプレイヤーの番号の色（17.7）
    Color ColorOf(Character c, int fallback) => c != null && ColorUtility.TryParseHtmlString(c.Color, out var color) ? color : playerColors[fallback % playerColors.Length];
    Color PlayerColor(PlayerState p) => ColorOf(data.Characters.FirstOrDefault(x => x.Id == p.Character), p.Id);
    string StarterOf(int i) { var c = CharacterOf(i); return c.Starters[starterIndex[i] % c.Starters.Count]; }
    string NameOf(int i) => string.IsNullOrWhiteSpace(names[i]) ? CharacterOf(i).Name : names[i].Trim();

    // 同じキャラクター・同じポケモンは選べない（1.1）。ほかの人とかぶっていたら次のものにずらす
    void FixPicks()
    {
        for (int i = 0; i < playerCount; i++)
            if (CharacterTaken(i)) NextCharacter(i);
            else if (StarterTaken(i)) NextStarter(i);
    }

    bool CharacterTaken(int i) => Enumerable.Range(0, playerCount).Any(j => j != i && CharacterOf(j) == CharacterOf(i));
    bool StarterTaken(int i) => Enumerable.Range(0, playerCount).Any(j => j != i && StarterOf(j) == StarterOf(i));

    // 次のキャラクターへ（ほかの人のキャラクターは飛ばす）。ポケモンはその世代の最初の、だれも選んでいないものにする
    void NextCharacter(int i)
    {
        for (int n = 0; n < data.Characters.Count; n++) { characterIndex[i] = (characterIndex[i] + 1) % data.Characters.Count; if (!CharacterTaken(i)) break; }
        starterIndex[i] = 0;
        for (int n = 0; n < CharacterOf(i).Starters.Count && StarterTaken(i); n++) starterIndex[i]++;
    }

    // 次のポケモンへ（同じ世代のほかの人が選んでいるものは飛ばす）。変えるのは自分だけ
    void NextStarter(int i)
    {
        int count = CharacterOf(i).Starters.Count;
        for (int n = 0; n < count; n++) { starterIndex[i] = (starterIndex[i] + 1) % count; if (!StarterTaken(i)) break; }
    }

    void SetupPlayerRow(Transform col, int i)
    {
        var row = Row(col);
        Ui.Line(row, $"P{i + 1}", 30, ColorOf(CharacterOf(i), i));
        if (CharacterSprite(CharacterOf(i)) is Sprite face) IconIn(row, face, 60); // キャラクターの絵
        Ui.Button(row, "キャラ：" + CharacterOf(i).Name, () => { NextCharacter(i); ShowSetup(); }, 230);
        Ui.Button(row, StarterOf(i), () => { NextStarter(i); ShowSetup(); }, 170);
        var field = Ui.Input(row, names[i] ?? "", (int)data.Required("name_max"), 170, v => names[i] = v, IsNameChar);
        field.placeholder.GetComponent<Text>().text = CharacterOf(i).Name;
        Ui.Button(row, "かな", () => OpenKana(i), 90);
        Ui.Button(row, cpus[i] ? "CPU" : "人", () => { cpus[i] = !cpus[i]; ShowSetup(); }, 90);
        if (!cpus[i]) Ui.Button(row, modes[i] == MoveMode.Highlight ? "移動：まとめて" : "移動：1マスずつ", () => { modes[i] = modes[i] == MoveMode.Highlight ? MoveMode.Step : MoveMode.Highlight; ShowSetup(); }, 230);
    }

    void ShowSetup()
    {
        Ui.Clear(canvas.transform);
        if (weeks == 0) weeks = (int)data.Required("weeks_min");
        var bg = Ui.Panel(canvas.transform, "Setup", Vector2.zero, Vector2.one, new Color(0.05f, 0.08f, 0.15f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.25f, 0.05f), new Vector2(0.75f, 0.95f), true, 14, 20);
        Ui.Line(col, "ポケモンすごろく（エリア1 試作）", 44);
        var row = Row(col);
        Ui.Line(row, $"人数 {playerCount}人", 30);
        for (int n = 2; n <= 4; n++) { int k = n; Ui.Button(row, $"{n}人", () => { playerCount = k; ShowSetup(); }, 110); }
        row = Row(col);
        int wMin = (int)data.Required("weeks_min"), wMax = (int)data.Required("weeks_max");
        Ui.Line(row, $"週数 {weeks}週", 30);
        Ui.Button(row, "−10", () => { weeks = Math.Max(wMin, weeks - 10); ShowSetup(); }, 110);
        Ui.Button(row, "＋10", () => { weeks = Math.Min(wMax, weeks + 10); ShowSetup(); }, 110);
        row = Row(col);
        Ui.Line(row, "イベントルール", 30);
        Ui.Button(row, EventRuleLabel, () => { eventRuleIndex = (eventRuleIndex + 1) % (data.EventRules.Count + 2); ShowSetup(); }, 300);
        if (PickedRule != null) Ui.Line(row, PickedRule.Description, 22);
        if (data.Characters.Count == 0) { Ui.Line(col, "キャラクター.csv がありません（設計メモに置いてください）", 30, Color.red); return; }
        FixPicks();
        for (int i = 0; i < playerCount; i++) SetupPlayerRow(col, i);
        row = Row(col);
        Ui.Button(row, "つぎへ", ShowConfirm, 300, 70);
        Ui.Button(row, "タイトルへ", ShowTitle, 220, 70);
#if ENABLE_INPUT_SYSTEM
        Ui.Button(row, "コントローラー登録", StartPadRegister, 300, 70);
#endif
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
            Ui.Line(col, $"P{i + 1}　{NameOf(i)}{(NameOf(i) != CharacterOf(i).Name ? $"（{CharacterOf(i).Name}）" : "")}　{StarterOf(i)}　{(cpus[i] ? "CPU" : "移動：" + (modes[i] == MoveMode.Highlight ? "まとめて" : "1マスずつ"))}", 28, ColorOf(CharacterOf(i), i));
        Ui.Line(col, "手番の順番は、はじめたあとにランダムで決まります", 24, new Color(0.8f, 0.8f, 0.8f));
        var row = Row(col);
        Ui.Button(row, "これで始める", StartGame, 300, 70);
        Ui.Button(row, "もどる", ShowSetup, 220, 70);
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
        game = GameProcessor.NewGame(board, data, list, weeks, s, out var events, characters, EventRuleId, cpus.Take(playerCount).ToList());
        BeginPlay(events);
    }

    // ゲーム画面を作ってコマを置く（はじめから・つづきから 共通）
    void BeginPlay(List<GameEvent> events)
    {
        game.DebugEnabled = Debug.isDebugBuild; // デバッグ機能（F1）は開発用ビルドとエディタだけ（17.1）
        narrator = new Narrator(data, board, S);
        lastAutoSaveWeek = S.Week;
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
        // G セリフ欄（下の中央）：濃い緑の箱に白い文字、細い金色のふち。出すときは下からスライド（GameRunner.Hud.cs の SlideMessage）
        var gold = Ui.Panel(canvas.transform, "G_Message", new Vector2(0.32f, 0.01f), new Vector2(0.75f, 0.25f), new Color(0.85f, 0.7f, 0.3f));
        messagePanel = gold.gameObject;
        var bottom = Ui.Panel(gold.transform, "Inner", Vector2.zero, Vector2.one, new Color(0.04f, 0.25f, 0.14f, 0.95f));
        bottom.rectTransform.offsetMin = new Vector2(3, 3); bottom.rectTransform.offsetMax = new Vector2(-3, -3);
        messageShown = 0;
        log = Ui.Label(bottom.transform, "", 24);
        log.rectTransform.offsetMin = new Vector2(16, 8); log.rectTransform.offsetMax = new Vector2(-16, -8);
        log.verticalOverflow = VerticalWrapMode.Truncate;
        // E メニュー（左下）。準備フェーズは いどう／どうぐ／マップ／ポケモン／ずかん／せってい。ほかの場面の選択肢もここに出す
        var right = Ui.Panel(canvas.transform, "E_Menu", new Vector2(0.005f, 0.01f), new Vector2(0.31f, 0.52f), HudColor);
        var promptRect = Ui.Rect(right.transform, "Prompt", new Vector2(0, 0.86f), Vector2.one);
        prompt = Ui.Label(promptRect, "", 24, TextAnchor.MiddleLeft, new Color(1, 0.9f, 0.5f));
        prompt.rectTransform.offsetMin = new Vector2(14, 0); prompt.rectTransform.offsetMax = new Vector2(-10, 0);
        buttons = Ui.Rect(right.transform, "Buttons", Vector2.zero, new Vector2(1, 0.86f));
        var grid = buttons.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(560, 50); grid.spacing = new Vector2(8, 6); grid.padding = new RectOffset(12, 12, 6, 6);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 1;

        battlePanel = Ui.Panel(canvas.transform, "Battle", new Vector2(0.27f, 0.36f), new Vector2(0.97f, 0.9f), new Color(0.05f, 0.05f, 0.1f, 0.85f)).rectTransform;
        cards[0] = new BattleCard(Ui.Rect(battlePanel, "Side0", new Vector2(0.03f, 0.08f), new Vector2(0.47f, 0.92f)), this);
        cards[1] = new BattleCard(Ui.Rect(battlePanel, "Side1", new Vector2(0.53f, 0.08f), new Vector2(0.97f, 0.92f)), this);
        battlePanel.gameObject.SetActive(false);
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
        typingText = text.Split('\n').Length > logLines.Count ? string.Join("\n", logLines) : text; // 9行より長い文は 最後の9行だけ
        typed = 0; lastLogTime = Time.time;
        RenderLog();
    }

    // ---- 命令を出して、起きたことを見せる ----

    void Send(Command c)
    {
        subMenu = null;
        var events = game.Apply(c, out string error);
        if (error != null) { AddLog("× " + error); RefreshAll(); return; }
        Play(events);
    }

    void Play(List<GameEvent> events)
    {
        foreach (var e in events) queue.Enqueue(e);
        if (!playing) StartCoroutine(PlayQueue());
    }

    IEnumerator PlayQueue()
    {
        playing = true;
        menu.Clear(); menuArt.Clear(); DrawMenu(""); PaintBoard(false);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            string text = narrator.Describe(e);
            if (text != null) AddLog(text);
            EventSound(e);
            CollectNotice(e);
            switch (e)
            {
                case TurnStarted t:
                    cameraTarget = pieces[t.Player];
                    camPan = Vector3.zero;
                    yield return ShowNotices(t.Player); // 17.5
                    break;
                case BallThrown bt: // ボールが揺れる（17.6）：野生のカードで ボールの絵を動かす
                    yield return cards[cards[1].Player < 0 ? 1 : 0].Catch(Ui.Ball(BallColor(bt.Ball)), bt.Caught);
                    break;
                case ExpGained xg when battlePanel.gameObject.activeSelf && cards.FirstOrDefault(c => c.Owns(xg.Player, xg.Pokemon)) is BattleCard xc:
                    yield return xc.ExpTo(xg.Total - xg.Exp, xg.Total, (int)data.Required("level_max")); // 経験値バーが ゆっくり伸びる
                    break;
                case WeekEventHappened we: yield return Banner(text, new Color(0.7f, 0.9f, 1f), 2); continue;            // 週イベントの告知（17.6）
                case DiceRolled dr: yield return DiceAnim(dr); continue;          // 17.6
                case InitiativeDrawn idr: yield return CardAnim(idr); continue;
                case Moved m: camPan = Vector3.zero; yield return MovePiece(m.Player, m.Path); continue;
                case Warped w: pieces[w.Player].position = PiecePosition(w.Player, w.To); break;
                case SailedToIsland si: pieces[si.Player].position = PiecePosition(si.Player, si.To); cameraTarget = pieces[si.Player]; camPan = Vector3.zero; yield return Banner(text, PlayerColor(S.Players[si.Player]), 2); continue;
                case ReturnedFromIsland ri: pieces[ri.Player].position = PiecePosition(ri.Player, ri.To); cameraTarget = pieces[ri.Player]; camPan = Vector3.zero; break;
                case TicketChanged tc: yield return Banner(text, PlayerColor(S.Players[tc.Player]), 2); continue;
                case SentToCenter c: pieces[c.Player].position = PiecePosition(c.Player, c.Square); break;
                case BattleStarted b:
                    hudRoot.SetActive(false); // バトル中は A〜D を隠す
                    if ((b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament) && !S.Players[b.Players[0]].IsCpu && !S.Players[b.Players[1]].IsCpu) yield return Banner($"{S.Players[b.Players[0]].Name}さんは コントローラー1、{S.Players[b.Players[1]].Name}さんは 2を 持ってください", Color.white, 2);
                    battlePanel.gameObject.SetActive(true);
                    cards[0].Clear(); cards[1].Clear();
                    cards[0].SetOwner(b.Players[0] >= 0 ? S.Players[b.Players[0]].Name : "", b.Players[0]);
                    cards[1].SetOwner(b.Players[1] >= 0 ? S.Players[b.Players[1]].Name : b.Opponent, b.Players[1]);
                    break;
                case BattleResumed br:
                    hudRoot.SetActive(false);
                    battlePanel.gameObject.SetActive(true);
                    cards[0].Clear(); cards[1].Clear();
                    cards[0].SetOwner(S.Players[br.Players[0]].Name, br.Players[0]);
                    cards[1].SetOwner(br.Players[1] >= 0 ? S.Players[br.Players[1]].Name : br.Opponent, br.Players[1]);
                    break;
                case BattleSuspended bs: battlePanel.gameObject.SetActive(false); yield return Banner(text, PlayerColor(S.Players[bs.Player]), 1.5f); continue;
                case SentOut s:
                    cards[s.Side].Show(s.Pokemon, s.Level, s.Hp, s.MaxHp, s.Type, s.Weakness, s.Exp); cards[s.Side].SetStatus(Pokemons.StatusName(s.Status));
                    yield return cards[s.Side].Appear(); // ポンと出てくる
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
                    if (r.Item == null) yield return AttackAnim(r); // 技の演出（17.6）
                    cards[r.Attacker].Flash($"攻撃ダイス {r.Dice}");
                    yield return Together(cards[r.Attacker].HpTo(r.AttackerHp), cards[1 - r.Attacker].HpTo(r.DefenderHp)); // HPが ゆっくり減る（回復なら増える）
                    break;
                case SandstormDamage sd: yield return cards[sd.Side].HpTo(sd.Hp); break;
                case PokemonFainted f: yield return cards[f.Side].HpTo(0); yield return cards[f.Side].Faint(); break; // 沈んで消える
                // 他の人の大事な出来事は、その人の色と名前つきで大きく（17.5）。長さは17.6
                case Caught c: yield return Banner(text, PlayerColor(S.Players[c.Player]), 2, c.Pokemon); continue;
                case Evolved ev: yield return Banner(text, PlayerColor(S.Players[ev.Player]), 5, ev.To); continue;
                case BadgeEarned be: yield return Banner(text, PlayerColor(S.Players[be.Player]), 3); continue;
                case SymbolGone sg: SyncSymbols(); yield return Banner(text, PlayerColor(S.Players[sg.Player]), 2, sg.Pokemon); continue; // 伝説が盤面から消えた（全員に大きく）
                case LevelCaughtUp lc: yield return Banner(text, PlayerColor(S.Players[lc.Player]), 2); continue;
            }
            // バトル演出の短縮。絆による回避・進化・バッジは思い出の場面なので短くしない（17.6）
            bool keep = e is Evolved || e is BadgeEarned || (e is RoundResolved rr && rr.Bond);
            if (text != null) yield return WaitMessage(shortBattle && battlePanel.gameObject.activeSelf && !keep ? messageSeconds / 2 : messageSeconds);
            if (HintFor(e) is string hint) yield return ShowHint(hint);
        }
        playing = false;
        if (S.Phase == Phase.Prepare && S.Week != lastAutoSaveWeek) { lastAutoSaveWeek = S.Week; SaveTo(0); } // 週の終わりに自動セーブ（1.2）
        RefreshAll();
    }

    IEnumerator Wait(float seconds)
    {
        for (float t = 0; t < seconds && !SkipHeld(); t += Time.deltaTime) yield return null;
    }

    // 文が出きるまで待ち、出てから seconds たつまで見せる（長い文でも 出し始めから seconds は待つ）
    IEnumerator WaitMessage(float seconds)
    {
        float start = Time.time;
        while (Typing) yield return null;
        yield return Wait(seconds - (Time.time - start));
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
        foreach (int sq in S.Blocked.Keys.Where(k => !snorlax.ContainsKey(k)))
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
        if (S.Battle == null) battlePanel.gameObject.SetActive(false);
        RefreshHud(); // メイン画面の部品（UI仕様書_メイン画面）
        PaintBoard(true);
        BuildMenu();
        if (debugPanel != null) BuildDebugPanel();
        SyncSnorlax();
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
        menu.Clear(); menuArt.Clear();
        if (S.Phase == Phase.GameOver)
        {
            Add("結果発表", ShowResults, KeyCode.Return);
            Add("エンドロール", () => ShowEndroll(0));
            Add("タイトルへもどる", () => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex), KeyCode.Escape);
            DrawMenu("ゲーム終了");
            return;
        }
        var p = S.CurrentPlayer;
        int me = p.Id;
        string title = "";
        if (subMenu != null) { subMenu(); return; }
        if (!HumanWaiting()) { DrawMenu($"{string.Join("・", game.WaitingFor().Select(id => S.Players[id].Name))}（CPU）が 考え中…"); return; }
        switch (S.Phase)
        {
            case Phase.Prepare:
                title = $"{p.Name}の ばん：サイコロを振る前";
                // E メニュー（UI仕様書 E）：いどう／どうぐ／マップ／ポケモン／ずかん／せってい。左に その行動の絵
                AddArt(Ui.Icon("くつ"), "いどう", () => { subMenu = () => DiceMenu(me); RefreshAll(); }, KeyCode.Return);
                AddArt(Ui.Icon("つりざお"), "どうぐ", () => { subMenu = () => ItemMenu(me); RefreshAll(); }, KeyCode.None, !p.UsedItem && p.PokemonBag.Concat(p.TrainerBag).Any());
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

    // ポケモン：手持ち一覧。1体を選ぶと くわしく／入れ替え（UI仕様書_メイン画面 E）
    void PokemonMenu(int me)
    {
        var p = S.Players[me];
        for (int i = 0; i < p.Party.Count; i++)
        {
            int k = i;
            var m = p.Party[k];
            AddMon(m.Species, $"{(k == 0 ? "先頭 " : "")}{m.Species} Lv{m.Level}　HP {m.Hp}/{Pokemons.MaxHp(data, m)}" + (m.Fainted ? "（たおれている）" : ""), () => { subMenu = () => PokemonActions(me, k); RefreshAll(); });
        }
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("ポケモン：どの子？");
    }

    void PokemonActions(int me, int slot)
    {
        var p = S.Players[me];
        Add("くわしく見る", () => ShowPokemon(p, slot));
        Add("ならびを入れ替える", () => { subMenu = () => SwapMenu(me, slot); RefreshAll(); }, KeyCode.None, p.Party.Count > 1);
        Add("もどる", () => { CloseOverlay(); subMenu = () => PokemonMenu(me); RefreshAll(); }, KeyCode.Escape);
        DrawMenu($"{p.Party[slot].Species}を どうする？");
    }

    // 入れ替え：1体目を選んでから2体目を選ぶ（16.9）
    void SwapMenu(int me, int first)
    {
        var p = S.Players[me];
        for (int i = 0; i < p.Party.Count; i++)
        {
            int k = i;
            string label = $"{(k == 0 ? "先頭 " : "")}{p.Party[k].Species} Lv{p.Party[k].Level}";
            if (first < 0) AddMon(p.Party[k].Species, label, () => { subMenu = () => SwapMenu(me, k); RefreshAll(); });
            else AddMon(p.Party[k].Species, label, () => Send(new SwapParty { Player = me, A = first, B = k }), KeyCode.None, k != first);
        }
        Add("もどる", () => { subMenu = first < 0 ? null : (Action)(() => PokemonMenu(me)); RefreshAll(); }, KeyCode.Escape);
        DrawMenu(first < 0 ? "入れ替える1体目を えらぶ" : $"{p.Party[first].Species}と 入れ替える子を えらぶ");
    }

    void ItemMenu(int me)
    {
        var p = S.Players[me];
        foreach (var item in p.PokemonBag.Concat(p.TrainerBag).Distinct())
        {
            string name = item;
            if (game.TargetOf(name) != TargetKind.None)
                Add(name, () => { subMenu = () => TargetMenu(me, name); RefreshAll(); }, KeyCode.None, Targets(me, name).Any(t => Ok(new UseItem { Player = me, Item = name, Target = t.target })));
            else Add(name, () => Send(new UseItem { Player = me, Item = name }), KeyCode.None, Ok(new UseItem { Player = me, Item = name }));
        }
        Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu("どの道具を使う？　ボール：" + (p.Balls.Count(b => b.Value > 0) == 0 ? "なし" : string.Join("　", p.Balls.Where(b => b.Value > 0).Select(b => $"{b.Key}×{b.Value}"))));
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

    void TargetMenu(int me, string item)
    {
        foreach (var (target, label) in Targets(me, item))
        {
            var c = new UseItem { Player = me, Item = item, Target = target };
            if (game.TargetOf(item) == TargetKind.Square && !Ok(c)) continue; // マスは使えるところだけ並べる
            Add(label, () => Send(c), KeyCode.None, Ok(c));
        }
        Add("もどる", () => { subMenu = () => ItemMenu(me); RefreshAll(); }, KeyCode.Escape);
        DrawMenu(game.TargetOf(item) == TargetKind.Square ? $"{item}：どこに？" : game.TargetOf(item) == TargetKind.Number ? $"{item}：何マス進む？" : $"{item}を だれに使う？");
    }

    int exchangePoints = 1; // 換金所で選んでいるポイント

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
            case FacilityKind.JobOffer:
                for (int i = 0; i < f.Options.Count; i++)
                {
                    var c = new ChooseOption { Player = me, Index = i };
                    var key = i == f.Options.Count - 1 ? KeyCode.Escape : KeyCode.None;
                    // 控えがいっぱい：控えの子と、来た子（最後の行）に絵
                    if (f.Kind == FacilityKind.BoxFull) AddMon(i < p.Box.Count ? p.Box[i].Species : f.PendingPokemon.Species, f.Options[i], () => Send(c), key);
                    else Add(f.Options[i], () => Send(c), key);
                }
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
                return $"{f.Prompt}　→ {exchangePoints}ポイント（{exchangePoints * rate:#,0}円）、のこり {p.Money - exchangePoints * rate:#,0}円";
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
                return owner ? (f.Owner == me ? "自分のライバルマス" : "へんそう中") + "：交換進化が 無料でできる" : $"{S.Players[f.Owner].Name}の ライバルマス：交換進化には 通信ケーブルが要る（{cables}個 持っている）";
            }
            case FacilityKind.Shop:
                foreach (var item in f.Goods) { var c = new Buy { Player = me, Item = item }; Add($"買う {item} {game.BuyPrice(item)}円", () => Send(c), KeyCode.None, Ok(c)); }
                foreach (var item in p.PokemonBag.Concat(p.TrainerBag).Distinct()) { var c = new Sell { Player = me, Item = item }; if (Ok(c)) Add($"売る {item} {game.SellPrice(item)}円", () => Send(c)); }
                Add("店を出る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                return $"{(f.Prompt != "" ? f.Prompt : "ショップ")}（所持金 {p.Money}円）" + (S.WeekEvent == "セール" ? "　セール中！" : "");
            case FacilityKind.Center:
                for (int i = 0; i < p.Party.Count; i++) { var c = new CenterSwap { Player = me, PartySlot = i }; AddMon(p.Party[i].Species, $"あずける {p.Party[i].Species}", () => Send(c), KeyCode.None, Ok(c)); }
                for (int i = 0; i < p.Box.Count; i++) { var c = new CenterSwap { Player = me, BoxIndex = i }; AddMon(p.Box[i].Species, $"手持ちに {p.Box[i].Species} Lv{p.Box[i].Level}", () => Send(c), KeyCode.None, Ok(c)); }
                foreach (var job in p.UnlockedJobs) { var c = new ChangeJob { Player = me, Job = job }; Add($"職業：{job}に 就く", () => Send(c), KeyCode.None, Ok(c)); }
                // もちものの着けはずし（8.2）
                var heldItems = p.PokemonBag.Where(x => data.FindItem(x)?.IsHeldItem == true).Distinct().ToList();
                for (int i = 0; i < p.Party.Count; i++)
                {
                    if (p.Party[i].HeldItem != "") { var c = new HoldItem { Player = me, Slot = i }; Add($"はずす：{p.Party[i].Species}の {p.Party[i].HeldItem}", () => Send(c), KeyCode.None, Ok(c)); }
                    foreach (var item in heldItems) { var c = new HoldItem { Player = me, Slot = i, Item = item }; Add($"もたせる：{item}→{p.Party[i].Species}", () => Send(c), KeyCode.None, Ok(c)); }
                }
                if (f.IsLab)
                    for (int i = 0; i < p.Party.Count; i++)
                        foreach (var target in Pokemons.LabTargets(data, p.Party[i]))
                        {
                            var c = new LabEvolve { Player = me, Slot = i, Target = target };
                            Add($"進化サービス：{p.Party[i].Species}→{target}（{game.LabPrice(p):#,0}円）", () => Send(c), KeyCode.None, Ok(c));
                        }
                Add(f.IsLab ? "研究室を出る" : "センターを出る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
                return (f.IsLab ? "オーキド博士の研究室" : "ポケモンセンター") + $"：みんな元気になった。手持ちと控えを入れかえられる" + (p.Job != "" ? $"（今の職業：{p.Job}）" : "");
            default:
                Add($"{f.Price}円 払って回復", () => Send(new AcceptHeal { Player = me }), KeyCode.Return, Ok(new AcceptHeal { Player = me }));
                Add("立ち去る", () => Send(new LeaveFacility { Player = me }), KeyCode.Escape);
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
                for (int i = 0; i < side.Team.Count; i++)
                {
                    var m = side.Team[i];
                    var c = new SelectPokemon { Player = side.Player, Slot = i };
                    Add($"{m.Species} Lv{m.Level} {m.Hp}/{Pokemons.MaxHp(data, m)}", () => Send(c), KeyCode.None, Ok(c));
                }
                return $"{side.Name}さん、出すポケモンを えらんでください（相手には見えません）";
            }
            case BattleStep.Commands:
            {
                var a = b.Sides[b.Attacker];
                var d = b.Sides[1 - b.Attacker];
                if (b.AttackCmd == null && a.Player >= 0 && !IsCpu(a.Player))
                {
                    int pl = a.Player;
                    Add("物理攻撃 [Z]", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Physical }), KeyCode.Z, Ok(new ChooseAttack { Player = pl, Attack = Attack.Physical }));
                    Add("特殊攻撃 [X]", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Special }), KeyCode.X, Ok(new ChooseAttack { Player = pl, Attack = Attack.Special }));
                    Add("必殺技 [C]", () => Send(new ChooseAttack { Player = pl, Attack = Attack.Super }), KeyCode.C, Ok(new ChooseAttack { Player = pl, Attack = Attack.Super }));
                    var usable = S.Players[pl].PokemonBag.Distinct().Where(i => Ok(new ChooseAttack { Player = pl, Attack = Attack.Item, Item = i })).ToList();
                    Add("どうぐ [V]", () => { subMenu = () => BattleItemMenu(pl, usable); RefreshAll(); }, KeyCode.V, usable.Count > 0);
                    return $"{a.Name}さん（攻撃）：{a.Current.Species}の コマンド";
                }
                int dp = d.Player;
                Add("まもる [A]", () => Send(new ChooseDefense { Player = dp, Defense = Defense.Guard }), KeyCode.A);
                Add("特殊守り [S]", () => Send(new ChooseDefense { Player = dp, Defense = Defense.SpecialGuard }), KeyCode.S);
                Add("カウンター [D]", () => Send(new ChooseDefense { Player = dp, Defense = Defense.Counter }), KeyCode.D);
                string flee = b.Kind == BattleKind.Player ? "降参" : "にげる";
                Add($"{flee} [F]", () => { subMenu = () => FleeConfirm(dp, flee); RefreshAll(); }, KeyCode.F, Ok(new ChooseDefense { Player = dp, Defense = Defense.Flee }));
                return $"{d.Name}さん（防御）：{d.Current.Species}の コマンド" + (b.AttackCmd != null && a.Player >= 0 ? "　※相手は選択済み" : "");
            }
            case BattleStep.Ball:
            {
                var p = S.Players[b.Sides[0].Player];
                foreach (var kv in p.Balls.Where(x => x.Value > 0)) { var c = new ThrowBall { Player = p.Id, Ball = kv.Key }; Add($"{kv.Key}（{kv.Value}個）", () => Send(c), KeyCode.None, Ok(c)); }
                Add("投げない", () => Send(new ThrowBall { Player = p.Id }), KeyCode.Escape);
                return "ボールを 投げる？";
            }
            default:
            {
                var winner = S.Players[b.Sides[b.Winner].Player];
                var loser = S.Players[b.Sides[1 - b.Winner].Player];
                foreach (var item in loser.PokemonBag.Concat(loser.TrainerBag).Distinct()) { string it = item; Add($"うばう：{it}", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Item, Item = it })); }
                Add("ファンを1人うばう", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Fan }), KeyCode.None, Ok(new ChooseReward { Player = winner.Id, Reward = Reward.Fan }));
                Add("ポケモンを交換", () => { subMenu = () => TradeMenu(winner, loser, -1); RefreshAll(); }, KeyCode.None, !b.Surrendered && loser.Party.Count > 1); // 相手が1体だけなら奪えない
                Add("許す", () => Send(new ChooseReward { Player = winner.Id, Reward = Reward.Forgive }), KeyCode.Escape);
                return $"{winner.Name}さん、ほうびを えらんでください";
            }
        }
    }

    void FleeConfirm(int player, string flee)
    {
        Add($"はい（{flee}）", () => Send(new ChooseDefense { Player = player, Defense = Defense.Flee }), KeyCode.Return);
        Add("いいえ", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
        DrawMenu(flee == "降参" ? "本当に 降参する？（負けになります）" : "本当に にげる？");
    }

    // ポケモン交換（7.7）：相手の手持ちから1体 → 差し出す自分の子（手持ちか控え。コストが足りない子は押せない）
    void TradeMenu(PlayerState winner, PlayerState loser, int take)
    {
        if (take < 0)
        {
            for (int i = 0; i < loser.Party.Count; i++) { int k = i; var m = loser.Party[i]; AddMon(m.Species, $"{m.Species} Lv{m.Level}（コスト{Pokemons.Cost10(data, m) / 10.0:0.0}）", () => { subMenu = () => TradeMenu(winner, loser, k); RefreshAll(); }); }
            Add("もどる", () => { subMenu = null; RefreshAll(); }, KeyCode.Escape);
            DrawMenu($"{winner.Name}さん：{loser.Name}さんの どの子が ほしい？");
            return;
        }
        void Offer(PokemonState m, int index, bool box)
        {
            var c = new ChooseReward { Player = winner.Id, Reward = Reward.Trade, Take = take, Give = index, GiveFromBox = box };
            AddMon(m.Species, $"{(box ? "控え " : "")}{m.Species} Lv{m.Level}（コスト{Pokemons.Cost10(data, m) / 10.0:0.0}）", () => Send(c), KeyCode.None, Ok(c));
        }
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
        // 項目が多いときは2列に
        var grid = buttons.GetComponent<GridLayoutGroup>();
        grid.constraintCount = menu.Count > 7 ? 2 : 1;
        grid.cellSize = new Vector2(menu.Count > 7 ? 276 : 560, menu.Count > 14 ? 40 : 50);
        var made = new List<Button>();
        for (int i = 0; i < menu.Count; i++)
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
        var cam = Camera.main;
        if (cam == null || cameraTarget == null) return;
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
        var full = WalkSprite(species, 1) ?? PokemonSprite(species);
        if (full == null || !full.texture.isReadable) return icons[species] = full;
        var tex = full.texture;
        var px = tex.GetPixels32();
        int x0 = tex.width, y0 = tex.height, x1 = -1, y1 = -1;
        for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
                if (px[y * tex.width + x].a > 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return icons[species] = x1 < 0 ? full : Sprite.Create(tex, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), new Vector2(0.5f, 0.5f));
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

    sealed class BattleCard
    {
        readonly Text owner, title, hpText, flash;
        readonly Image circle, hpBar, expBack, expBar, ball;
        readonly GameRunner runner;
        int maxHp = 1, hp;
        float shownHp;     // バーに出しているHP（ゆっくり近づける）
        string status = "";
        public int Player = -1;      // このカードのプレイヤー（野生・NPC なら -1。経験値バーと警告音は プレイヤーのポケモンだけ）
        string species = "", type = "", weakness = "";
        int level;
        float shownExp;    // 経験値バーに出している経験値の合計

        public BattleCard(RectTransform root, GameRunner runner)
        {
            this.runner = runner;
            owner = Ui.Label(Ui.Rect(root, "Owner", new Vector2(0, 0.88f), Vector2.one), "", 26, TextAnchor.MiddleCenter);
            circle = Ui.Panel(root, "Circle", new Vector2(0.3f, 0.38f), new Vector2(0.7f, 0.86f), Color.gray);
            circle.preserveAspect = true;
            circle.sprite = Ui.Circle;
            ball = Ui.Panel(root, "Ball", new Vector2(0.44f, 0.4f), new Vector2(0.56f, 0.56f), Color.white); // 投げたボール
            ball.preserveAspect = true; ball.enabled = false;
            title = Ui.Label(Ui.Rect(root, "Title", new Vector2(0, 0.2f), new Vector2(1, 0.36f)), "", 26, TextAnchor.MiddleCenter);
            Ui.Panel(root, "HpBack", new Vector2(0.1f, 0.12f), new Vector2(0.9f, 0.18f), new Color(0.2f, 0.2f, 0.2f));
            hpBar = Ui.Panel(root, "Hp", new Vector2(0.1f, 0.12f), new Vector2(0.9f, 0.18f), new Color(0.3f, 0.9f, 0.4f));
            expBack = Ui.Panel(root, "ExpBack", new Vector2(0.1f, 0.095f), new Vector2(0.9f, 0.112f), new Color(0.15f, 0.15f, 0.2f)); // 経験値バー（HPバーの下の細い青）
            expBar = Ui.Panel(expBack.transform, "Exp", Vector2.zero, new Vector2(0, 1), new Color(0.3f, 0.65f, 1f));
            hpText = Ui.Label(Ui.Rect(root, "HpText", new Vector2(0, 0), new Vector2(1, 0.095f)), "", 22, TextAnchor.MiddleCenter);
            flash = Ui.Label(Ui.Rect(root, "Flash", new Vector2(0, 0.38f), new Vector2(0.3f, 0.6f)), "", 22, TextAnchor.MiddleCenter, new Color(1, 0.9f, 0.4f));
        }

        public void SetOwner(string name, int player) { owner.text = name; Player = player; }
        public void SetStatus(string s) { status = s; Draw(); }
        public void Clear() { status = ""; title.text = hpText.text = flash.text = ""; circle.color = Color.clear; ball.enabled = false; species = ""; hp = 0; shownHp = 0; expBack.gameObject.SetActive(false); hpBar.rectTransform.anchorMax = new Vector2(0.9f, 0.18f); }

        public void Show(string species, int level, int hp, int max, string type, string weakness, int exp)
        {
            maxHp = Math.Max(1, max);
            this.species = species; this.level = level; this.type = type; this.weakness = weakness;
            var sprite = runner.PokemonSprite(species);
            circle.sprite = sprite != null ? sprite : Ui.Circle;
            circle.color = sprite != null ? Color.white : TypeColor(type);
            circle.enabled = true;
            ball.enabled = false;
            circle.rectTransform.anchoredPosition = Vector2.zero; circle.rectTransform.localScale = Vector3.one; // たおれた・出てくる演出のあと
            DrawTitle();
            flash.text = "";
            SetHp(hp);
            expBack.gameObject.SetActive(Player >= 0);
            shownExp = exp; DrawExp();
        }

        void DrawTitle() => title.text = $"{species}　Lv{level}\nタイプ {type}　弱点 {weakness}";

        // 経験値バー：今のレベルの はじめ〜次のレベルまでの どこにいるか
        void DrawExp()
        {
            float from = Pokemons.ExpForLevel(level), to = Pokemons.ExpForLevel(level + 1);
            expBar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01((shownExp - from) / Mathf.Max(1, to - from)), 1);
        }

        public bool Owns(int player, string mon) => Player >= 0 && player == Player && mon == species;

        // 経験値バーが ゆっくり伸びる。いっぱいになったら レベルアップして、0から また伸びる
        public IEnumerator ExpTo(int from, int to, int maxLevel)
        {
            shownExp = from; DrawExp();
            float seconds = runner.shortBattle ? 0.5f : 1f; // バー1本ぶんの時間
            while (shownExp < to && !SkipHeld())
            {
                float next = Mathf.Min(to, level < maxLevel ? Pokemons.ExpForLevel(level + 1) : to);
                float speed = (Pokemons.ExpForLevel(level + 1) - Pokemons.ExpForLevel(level)) / seconds;
                shownExp = Mathf.MoveTowards(shownExp, next, speed * Time.deltaTime);
                if (level < maxLevel && shownExp >= Pokemons.ExpForLevel(level + 1)) { level++; DrawTitle(); Flash("レベルアップ！"); }
                DrawExp();
                yield return null;
            }
            shownExp = to;
            while (level < maxLevel && to >= Pokemons.ExpForLevel(level + 1)) { level++; DrawTitle(); } // 早送りしたとき
            DrawExp();
        }

        // HPが赤い（2割以下）。プレイヤーのポケモンだけ（警告音）
        public bool Danger => Player >= 0 && species != "" && shownHp > 0 && shownHp <= maxHp * 0.2f;

        // ボールを投げる：ボールが飛んできて ポケモンが吸いこまれ、ゆれる。捕まえたら そのまま、逃げられたら ポケモンが出てくる
        public IEnumerator Catch(Sprite ballSprite, bool caught)
        {
            float s = runner.shortBattle ? 0.5f : 1f;
            Color baseColor = circle.color;
            ball.sprite = ballSprite; ball.color = Color.white; ball.enabled = true;
            ball.rectTransform.localRotation = Quaternion.identity;
            for (float t = 0; t < 0.35f * s && !SkipHeld(); t += Time.deltaTime) // 左下から 弧をえがいて飛んでくる
            {
                float k = t / (0.35f * s);
                ball.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(-260, 0, k), Mathf.Sin(k * Mathf.PI) * 120 + Mathf.Lerp(-80, 0, k));
                ball.rectTransform.localRotation = Quaternion.Euler(0, 0, -720 * k);
                yield return null;
            }
            ball.rectTransform.anchoredPosition = Vector2.zero; ball.rectTransform.localRotation = Quaternion.identity;
            for (float t = 0; t < 0.3f * s && !SkipHeld(); t += Time.deltaTime) // 白く光って 吸いこまれる
            {
                float k = t / (0.3f * s);
                circle.rectTransform.localScale = Vector3.one * (1 - k);
                circle.color = Color.Lerp(baseColor, new Color(1, 1, 1, 0.6f), k);
                yield return null;
            }
            circle.enabled = false;
            int wobbles = caught ? 3 : UnityEngine.Random.Range(1, 3); // 逃げられるときは 1〜2回で
            for (int w = 0; w < wobbles; w++)
            {
                yield return runner.Wait(0.35f * s);
                for (float t = 0; t < 0.4f * s && !SkipHeld(); t += Time.deltaTime) // 左右に ゆらっ
                {
                    ball.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t / (0.4f * s) * Mathf.PI * 2) * 25);
                    yield return null;
                }
                ball.rectTransform.localRotation = Quaternion.identity;
            }
            yield return runner.Wait(0.3f * s);
            circle.rectTransform.localScale = Vector3.one; circle.color = baseColor;
            if (caught)
            {
                ball.color = new Color(0.75f, 0.75f, 0.75f); // カチッ：少し暗くなって 止まる
                Flash("つかまえた！");
                yield return runner.Wait(0.5f * s);
            }
            else
            {
                ball.enabled = false;
                circle.enabled = true;
                Flash("あっ！ でてきた");
                yield return Appear();
            }
        }

        // すぐに変える（出したとき）
        public void SetHp(int hp) { this.hp = hp; shownHp = hp; Draw(); }

        // HPを ゆっくり変える（本家のように、満タンから0まで およそ1.5秒。早送りなら すぐ）
        public IEnumerator HpTo(int hp)
        {
            this.hp = hp;
            float speed = maxHp / (runner.shortBattle ? 0.8f : 1.5f);
            while (!Mathf.Approximately(shownHp, hp))
            {
                shownHp = SkipHeld() ? hp : Mathf.MoveTowards(shownHp, hp, speed * Time.deltaTime);
                Draw();
                yield return null;
            }
        }

        void Draw()
        {
            float r = Mathf.Clamp01(shownHp / maxHp);
            hpBar.rectTransform.anchorMax = new Vector2(0.1f + 0.8f * r, 0.18f);
            hpBar.color = r > 0.5f ? new Color(0.3f, 0.9f, 0.4f) : r > 0.2f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.95f, 0.3f, 0.25f);
            hpText.text = $"HP {Mathf.CeilToInt(shownHp)} / {maxHp}" + (status != "" ? $"　【{status}】" : "");
        }

        // 出てくる：小さい所から ポンと大きくなる
        public IEnumerator Appear()
        {
            float total = runner.shortBattle ? 0.2f : 0.35f;
            for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / total;
                circle.rectTransform.localScale = Vector3.one * (k < 0.8f ? Mathf.Lerp(0.1f, 1.1f, k / 0.8f) : Mathf.Lerp(1.1f, 1f, (k - 0.8f) / 0.2f));
                yield return null;
            }
            circle.rectTransform.localScale = Vector3.one;
        }

        // たおれる：下に沈みながら 消える
        public IEnumerator Faint()
        {
            float total = runner.shortBattle ? 0.35f : 0.7f;
            Color c = circle.color;
            for (float t = 0; t < total && !SkipHeld(); t += Time.deltaTime)
            {
                float k = t / total;
                circle.rectTransform.anchoredPosition = new Vector2(0, -140 * k * k);
                circle.color = new Color(c.r, c.g, c.b, 1 - k);
                yield return null;
            }
            circle.rectTransform.anchoredPosition = Vector2.zero;
            circle.color = new Color(c.r, c.g, c.b, 0);
        }

        public void Flash(string text) => flash.text = text;
        public Image Body => circle; // 技の演出で動かす
    }
}
