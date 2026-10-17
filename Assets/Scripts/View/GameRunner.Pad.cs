using System.Collections.Generic;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

// コントローラー（仕様書16.1 #4・16.5・16.9・17.4）。Input System のパッケージで動く
// メニュー：十字キーで選ぶ、○で決定、×でもどる。バトル：攻撃 ○物理 △特殊 □必殺 L1/R1どうぐ、防御 ○まもる △特殊守り □カウンター L1/R1にげる
// 移動：十字キー・左スティックで 道がつながっている向きへ（ななめの道も近い向きで進める）、○で選ぶ・決定、×で1歩もどす。ふだんはコントローラー1、プレイヤー戦は 仕掛けた側が1・相手が2（16.5 #28）
// ボタンを並べた画面（タイトル・設定・ゲーム設定・つづきから・結果発表など）：十字キーで選ぶ、○で押す、×でもどる。エンドロールは ○で早送り、STARTで とばす
public partial class GameRunner
{
    int menuCursor;                            // メニューで選んでいる行
    string menuTitle;                          // メニューが変わったらカーソルを先頭に戻すため
    int padSquare = -1;                        // 移動で選んでいるマス
    readonly int[] padDevice = { -1, -1 };     // 登録したコントローラー1・2（デバイスの番号）。-1 ならつないだ順
    int padRegister = -1;                      // コントローラー登録中なら 0（1番を待つ）・1（2番を待つ）
    GameObject padLostPanel;                   // コントローラーが抜けたときの表示（17.4）
    Button screenSel;                          // ボタンを並べた画面で選んでいるボタン
    Color screenSelColor;                      // 選ぶ前の色（はずしたら戻す）
    int screenIndex;                           // 画面を描き直しても同じ位置を選ぶため
    string screenName;                         // 画面が変わったら先頭に戻すため
    static readonly string[] BackLabels = { "もどる", "タイトルへ", "閉じる", "やめる" }; // ×で押すボタン

    // 演出の早送り（スペース、コントローラーの ○・×）
    static bool SkipHeld()
    {
        if (Input.GetKey(KeyCode.Space)) return true;
#if ENABLE_INPUT_SYSTEM
        foreach (var g in Gamepad.all) if (g.buttonEast.isPressed || g.buttonSouth.isPressed) return true;
#endif
        return false;
    }

    // 右スティック（画面を動かす）
    static Vector2 PadStick()
    {
#if ENABLE_INPUT_SYSTEM
        foreach (var g in Gamepad.all) { var v = g.rightStick.ReadValue(); if (v.sqrMagnitude > 0.04f) return v; }
#endif
        return Vector2.zero;
    }

    // メニューを描くときに、カーソルの行を目立たせる
    void PadHighlight(string title, List<Button> made)
    {
        if (title != menuTitle) { menuTitle = title; menuCursor = 0; }
        if (menu.Count == 0) return;
        menuCursor = Mathf.Clamp(menuCursor, 0, menu.Count - 1);
        if (!menu[menuCursor].enabled) menuCursor = Mathf.Max(0, menu.FindIndex(m => m.enabled));
        if (menuCursor < made.Count) made[menuCursor].image.color = new Color(1f, 0.85f, 0.4f);
    }

#if ENABLE_INPUT_SYSTEM
    // n 番目（0 か 1）のコントローラー。登録していなければ、つないだ順（ほかの番号に登録したものは除く）
    Gamepad Pad(int n)
    {
        if (padDevice[n] >= 0) return InputSystem.GetDeviceById(padDevice[n]) as Gamepad;
        var others = new HashSet<int>(padDevice.Where(id => id >= 0));
        var free = Gamepad.all.Where(g => !others.Contains(g.deviceId)).ToList();
        int index = n == 0 || padDevice[0] >= 0 ? 0 : 1;
        return index < free.Count ? free[index] : null;
    }

    static bool Down(Gamepad g, System.Func<Gamepad, ButtonControl> button) => g != null && button(g).wasPressedThisFrame;

    // いま操作するコントローラー：プレイヤー戦（とトーナメント）で人どうしなら、選ぶ側の番号。それ以外はコントローラー1
    Gamepad ActingPad()
    {
        var b = S?.Battle;
        if (b != null && (b.Kind == BattleKind.Player || b.Kind == BattleKind.Tournament) && b.Sides.All(s => s.Player >= 0 && !S.Players[s.Player].IsCpu))
        {
            int side = b.Step == BattleStep.Select ? System.Array.FindIndex(b.Sides, s => s.NeedsSelect)
                     : b.Step == BattleStep.Commands ? (b.AttackCmd == null ? b.Attacker : 1 - b.Attacker) : 0;
            var pad = Pad(Mathf.Max(0, side));
            return pad ?? Pad(0); // 2台目がなければ1台で回す
        }
        return Pad(0);
    }
#endif

    // Update の最初に呼ぶ。コントローラーで何か操作した（または待っている）なら true
    bool PadUpdate()
    {
#if ENABLE_INPUT_SYSTEM
        if (padRegister >= 0) { PadRegisterUpdate(); return true; }
        if (PadLostCheck()) return true;
        if (kanaFor >= 0) return PadKana();
        if (endrollRun != null) { if (Down(Pad(0) ?? Gamepad.current, x => x.startButton)) SkipEndroll(); return true; } // ○は SkipHeld で早送り
        if (game == null) return PadScreen(Pad(0) ?? Gamepad.current);
        if (playing) return false;
        var g = ActingPad();
        if (g == null) return false;
        if (overlay != null) return PadScreen(g);
        if (exploring)
        {
            var stick = g.leftStick.ReadValue() + g.dpad.ReadValue();
            if (stick.sqrMagnitude > 0.04f) exploreFocus.position += new Vector3(stick.x, 0, stick.y).normalized * 15f * zoom * Time.deltaTime;
        }
        // バトルのコマンド：○△□ と L1/R1 を、そのコマンドのキーとして押す
        if (S.Phase == Phase.Battle && S.Battle.Step == BattleStep.Commands && subMenu == null)
        {
            bool attack = S.Battle.AttackCmd == null;
            KeyCode key = Down(g, x => x.buttonEast) ? (attack ? KeyCode.Z : KeyCode.A)
                        : Down(g, x => x.buttonNorth) ? (attack ? KeyCode.X : KeyCode.S)
                        : Down(g, x => x.buttonWest) ? (attack ? KeyCode.C : KeyCode.D)
                        : Down(g, x => x.leftShoulder) || Down(g, x => x.rightShoulder) ? (attack ? KeyCode.V : KeyCode.F) : KeyCode.None;
            if (key != KeyCode.None) { PressKey(key); return true; }
        }
        // 移動：十字キー・左スティックで 道がつながっている向きへ（1マスずつ方式は そのまま1歩、まとめて方式は カーソルが道をたどる）
        // ○で決定（まとめて方式は 光っているマスを選ぶ → もう一度○で決定）、×で1歩もどす
        if (S.Phase == Phase.Moving && subMenu == null)
        {
            int me = S.CurrentPlayer.Id;
            int tip = S.Move.Path[S.Move.Path.Count - 1];
            var dir = PadDirection(g);
            if (dir != Vector2.zero)
            {
                if (S.CurrentPlayer.MoveMode == MoveMode.Step)
                {
                    int next = PadRoad(tip, dir);
                    if (next >= 0 && Ok(new StepTo { Player = me, Square = next })) Send(new StepTo { Player = me, Square = next });
                    return true;
                }
                int pick = PadRoad(padSquare >= 0 ? padSquare : tip, dir);
                if (pick >= 0)
                {
                    padSquare = pick;
                    if (cameraTarget != null) { var d = SquarePosition(pick) - cameraTarget.position; camPan = new Vector3(d.x, 0, d.z); } // 選んだマスが画面の真ん中に来る
                    RefreshAll();
                }
                return true;
            }
            if (Down(g, x => x.buttonEast))
            {
                if (padSquare >= 0 && padSquare != tip)
                {
                    if (S.Move.Reachable.Contains(padSquare)) Send(new ChooseDestination { Player = me, Square = padSquare }); // 光っていないマスでは何もしない
                }
                else if (Ok(new ConfirmMove { Player = me })) { padSquare = -1; Send(new ConfirmMove { Player = me }); }
                return true;
            }
            if (Down(g, x => x.buttonSouth) && Ok(new StepBack { Player = S.CurrentPlayer.Id })) { Send(new StepBack { Player = S.CurrentPlayer.Id }); return true; }
        }
        // メニュー：↑↓で選ぶ、○で決定、×でもどる（Esc のボタン）
        if (Down(g, x => x.dpad.down) || Down(g, x => x.dpad.up))
        {
            int step = Down(g, x => x.dpad.down) ? 1 : -1;
            for (int n = 0; n < menu.Count; n++)
            {
                menuCursor = (menuCursor + step + menu.Count) % menu.Count;
                if (menu[menuCursor].enabled) break;
            }
            DrawMenu(prompt.text);
            return true;
        }
        if (Down(g, x => x.buttonEast) && menuCursor < menu.Count && menu[menuCursor].enabled) { menu[menuCursor].action(); return true; }
        if (Down(g, x => x.buttonSouth)) { PressKey(KeyCode.Escape); return true; }
#endif
        return false;
    }

    // そのキーが割り当てられたメニューの行を押す
    void PressKey(KeyCode key)
    {
        var item = menu.FirstOrDefault(m => m.key == key && m.enabled);
        item.action?.Invoke();
    }

#if ENABLE_INPUT_SYSTEM
    bool stickHeld; // 左スティックを倒したまま（倒すたびに1回だけ動かす）

    // 十字キー（上下左右）か 左スティック（ななめも）の 押した向き。押していなければ zero
    Vector2 PadDirection(Gamepad g)
    {
        if (Down(g, x => x.dpad.up)) return Vector2.up;
        if (Down(g, x => x.dpad.down)) return Vector2.down;
        if (Down(g, x => x.dpad.left)) return Vector2.left;
        if (Down(g, x => x.dpad.right)) return Vector2.right;
        var v = g.leftStick.ReadValue();
        bool held = v.sqrMagnitude > 0.25f;
        bool fresh = held && !stickHeld;
        stickHeld = held;
        return fresh ? v.normalized : Vector2.zero;
    }
#endif

    // from から 道がつながっている となりのマスのうち、押した向き dir（画面で見た向き）に いちばん近い道の先。
    // 道がななめでも、向きのずれが 80度までなら その道とみなす（十字キーの上で 右上の道に進める）。なければ -1
    int PadRoad(int from, Vector2 dir)
    {
        var cam = Camera.main;
        if (cam == null) return -1;
        Vector2 o = cam.WorldToScreenPoint(SquarePosition(from));
        int best = -1; float bestAngle = 80f;
        foreach (int n in board[from].Neighbors)
        {
            float a = Vector2.Angle((Vector2)cam.WorldToScreenPoint(SquarePosition(n)) - o, dir);
            if (a < bestAngle) { bestAngle = a; best = n; }
        }
        return best;
    }

#if ENABLE_INPUT_SYSTEM
    // 五十音表：十字キーで選ぶ、○で入力、×で1文字けす、△かSTARTで おわり
    bool PadKana()
    {
        var g = Pad(0) ?? Gamepad.current;
        if (g == null) return false;
        int dx = Down(g, x => x.dpad.right) ? 1 : Down(g, x => x.dpad.left) ? -1 : 0;
        int dy = Down(g, x => x.dpad.down) ? 1 : Down(g, x => x.dpad.up) ? -1 : 0;
        if (dx != 0 || dy != 0) { KanaMove(dx, dy); return true; }
        if (Down(g, x => x.buttonEast)) { KanaType(); return true; }
        if (Down(g, x => x.buttonSouth)) { KanaBack(); return true; }
        if (Down(g, x => x.buttonNorth) || Down(g, x => x.startButton)) { CloseKana(); return true; }
        return false;
    }

    // ボタンを並べた画面（ゲームの外の画面と、ゲーム中に開いた画面）。いちばん上の画面のボタンだけを選ぶ
    bool PadScreen(Gamepad g)
    {
        if (g == null) return false;
        var root = overlay != null ? (Transform)overlay : canvas.transform.GetChild(canvas.transform.childCount - 1);
        var list = root.GetComponentsInChildren<Button>().Where(b => b.interactable).ToList();
        if (list.Count == 0)
        {
            // ボタンのない画面は ○・×で閉じる
            if (overlay != null && (Down(g, x => x.buttonSouth) || Down(g, x => x.buttonEast))) { CloseOverlay(); return true; }
            return false;
        }
        if (root.name != screenName) { screenName = root.name; screenIndex = 0; }
        if (screenSel == null || !list.Contains(screenSel)) ScreenSelect(list[Mathf.Clamp(screenIndex, 0, list.Count - 1)], list);
        var dir = Down(g, x => x.dpad.right) ? Vector2.right : Down(g, x => x.dpad.left) ? Vector2.left : Down(g, x => x.dpad.up) ? Vector2.up : Down(g, x => x.dpad.down) ? Vector2.down : Vector2.zero;
        if (dir != Vector2.zero)
        {
            // 上下：その向きのいちばん近い行の中で、横にいちばん近いボタン。左右：同じ行の となり（なければ動かない）
            Vector2 from = screenSel.transform.position;
            float tol = 20 * canvas.scaleFactor; // 同じ行（列）とみなすずれ
            var cands = list.Where(b => b != screenSel)
                .Select(b => { var d = (Vector2)b.transform.position - from; return (b, along: Vector2.Dot(d, dir), side: Mathf.Abs(Vector2.Dot(d, new Vector2(dir.y, dir.x)))); })
                .Where(x => x.along > tol / 2).ToList();
            if (cands.Count > 0)
            {
                float near = cands.Min(x => x.along);
                var next = dir.x != 0 ? cands.Where(x => x.side < tol).OrderBy(x => x.along).Select(x => x.b).FirstOrDefault()
                         : cands.Where(x => x.along <= near + tol).OrderBy(x => x.side).First().b;
                if (next != null) ScreenSelect(next, list);
            }
            return true;
        }
        if (Down(g, x => x.buttonEast)) { screenSel.onClick.Invoke(); return true; }
        if (Down(g, x => x.buttonSouth))
        {
            var back = list.FirstOrDefault(b => BackLabels.Any(l => b.GetComponentInChildren<Text>().text.StartsWith(l)));
            if (back != null) back.onClick.Invoke();
            else if (overlay != null) CloseOverlay();
            return true;
        }
        return false;
    }

    void ScreenSelect(Button b, List<Button> list)
    {
        if (screenSel != null) screenSel.image.color = screenSelColor;
        screenSel = b;
        screenSelColor = b.image.color;
        screenIndex = list.IndexOf(b);
        b.image.color = new Color(1f, 0.85f, 0.4f);
    }

    // ---- コントローラー登録（16.1 #4）：○を押したコントローラーが 1番、次に押したものが 2番 ----

    void StartPadRegister()
    {
        padDevice[0] = padDevice[1] = -1;
        padRegister = 0;
        PadRegisterShow();
    }

    void PadRegisterShow()
    {
        var col = Overlay("PadRegister");
        Ui.Line(col, "コントローラー登録", 48);
        Ui.Line(col, padRegister == 0 ? "1番にする コントローラーの ○ を 押してください" : "2番にする コントローラーの ○ を 押してください（1台だけなら Esc）", 32);
        Ui.Line(col, $"つながっている コントローラー：{Gamepad.all.Count}台", 26);
    }

    void PadRegisterUpdate()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) { padRegister = -1; CloseOverlay(); return; }
        foreach (var g in Gamepad.all)
        {
            if (!g.buttonEast.wasPressedThisFrame || padDevice.Contains(g.deviceId)) continue;
            padDevice[padRegister] = g.deviceId;
            padRegister++;
            if (padRegister >= 2) { padRegister = -1; CloseOverlay(); AddLogSafe("コントローラーを 登録しました"); return; }
            PadRegisterShow();
            return;
        }
    }

    // ---- コントローラーが抜けたとき（17.4）：登録したものが抜けたら止めて、つなぎ直したら続ける ----

    bool PadLostCheck()
    {
        var lost = Enumerable.Range(0, 2).Where(n => padDevice[n] >= 0 && InputSystem.GetDeviceById(padDevice[n]) == null).ToList();
        if (lost.Count == 0)
        {
            if (padLostPanel != null) { Destroy(padLostPanel); padLostPanel = null; Time.timeScale = 1; }
            return false;
        }
        if (padLostPanel == null)
        {
            Time.timeScale = 0; // 演出も止める
            var bg = Ui.Panel(canvas.transform, "PadLost", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0.85f));
            Ui.Label(bg.transform, $"コントローラー{string.Join("・", lost.Select(n => n + 1))}が はずれました\nつなぎなおしてください\n（登録をやめるなら Esc）", 44);
            padLostPanel = bg.gameObject;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) { foreach (int n in lost) padDevice[n] = -1; } // キーボードで遊び続ける
        return true;
    }
#endif

    void AddLogSafe(string text) { if (game != null) AddLog(text); }
}
