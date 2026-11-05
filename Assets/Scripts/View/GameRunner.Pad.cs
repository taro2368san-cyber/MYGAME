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
// メニュー：十字キーで選ぶ、○で決定、×でもどる。バトル（UI仕様書 7.2）：攻撃 □こうげき ○とくこう △必殺 R1どうぐ、防御 □ぼうぎょ ○とくぼう △カウンター R1にげる。カードは 十字キー左右と ○
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
    static readonly string[] BackLabels = { "もどる", "タイトルへ", "閉じる", "やめる", "いいえ" }; // ×で押すボタン

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
        if (DexOpen) return PadDex(g);
        if (overlay != null) return PadScreen(g);
        // 確認ダイアログ：○で はい、×で いいえ（押した瞬間に決まる。十字キーは使わない）
        if (ConfirmOpen)
        {
            if (Down(g, x => x.buttonEast)) PressKey(KeyCode.Return);
            else if (Down(g, x => x.buttonSouth)) PressKey(KeyCode.Escape);
            else return false;
            return true;
        }
        // 見渡し（16.7）：左スティック・十字キーで動く、R1 で寄る・L1 で引く（ここだけ。袋の切りかえとは ぶつからない）、×で もどる。マスは まん中の目印で選ぶ（AimSquare）
        // ほかのコントローラーの操作（メニューの↑↓・○）には渡さない。false を返すので キーボード・マウスは そのまま使える
        if (exploring)
        {
            var stick = g.leftStick.ReadValue() + g.dpad.ReadValue();
            if (stick.sqrMagnitude > 0.04f) exploreFocus.position += new Vector3(stick.x, 0, stick.y).normalized * 15f * zoom * Time.deltaTime;
            float z = (g.leftShoulder.isPressed ? 1 : 0) - (g.rightShoulder.isPressed ? 1 : 0);
            if (z != 0) zoom = Mathf.Clamp(zoom + z * 1.5f * Time.deltaTime, 0.3f, 3f); // 引きすぎは ClampExploreCamera が止める
            if (Down(g, x => x.buttonSouth)) { EndExplore(); return true; }
            return false;
        }
        // バトルのコマンド（UI仕様書 7.2）：□ ○ △ R1 を、そのコマンドのキー（A S D F）として押す。攻撃も防御も同じ記号は同じボタン
        // プレイヤー戦の選出も同じ：□ ○ △ が 手持ちの1・2・3番目（A S D）。押した人のコントローラーは ActingPad（1＝仕掛けた側、2＝仕掛けられた側）
        if (S.Phase == Phase.Battle && (S.Battle.Step == BattleStep.Commands || BlindSelect) && subMenu == null)
        {
            KeyCode key = Down(g, x => x.buttonWest) ? KeyCode.A : Down(g, x => x.buttonEast) ? KeyCode.S
                        : Down(g, x => x.buttonNorth) ? KeyCode.D : Down(g, x => x.rightShoulder) ? KeyCode.F : KeyCode.None;
            if (key != KeyCode.None) { PressKey(key); return true; }
        }
        // 先攻・後攻のカード：十字キー左右で選んで ○
        if (S.Phase == Phase.Battle && S.Battle.Step == BattleStep.Initiative && subMenu == null)
        {
            KeyCode key = Down(g, x => x.dpad.left) ? KeyCode.LeftArrow : Down(g, x => x.dpad.right) ? KeyCode.RightArrow : Down(g, x => x.buttonEast) ? KeyCode.Return : KeyCode.None;
            if (key != KeyCode.None) { PressKey(key); return true; }
        }
        // 移動：十字キー・左スティックで 道がつながっている向きへ（1マスずつ方式は そのまま1歩、まとめて方式は カーソルが道をたどる）
        // ○で決定（まとめて方式は 光っているマスを選ぶ → もう一度○で決定）、×で1歩もどす
        if (S.Phase == Phase.Moving && subMenu == null)
        {
            int me = S.CurrentPlayer.Id;
            int tip = S.Move.Path[S.Move.Path.Count - 1];
            // まとめて方式：右スティックで画面を動かすと、画面の真ん中に いちばん近いマスを選ぶ（画面を動かすのは PanCamera にまかせるので true は返さない）
            if (S.CurrentPlayer.MoveMode == MoveMode.Highlight && g.rightStick.ReadValue().sqrMagnitude > 0.04f)
            {
                int c = CenterSquare();
                if (c >= 0 && c != padSquare) { padSquare = c; PaintBoard(true); }
            }
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
        // メニューの L1・R1 は ←→ のキーとして押す（どうぐの袋の切りかえ、16.9）
        if (subMenu != null && (Down(g, x => x.leftShoulder) || Down(g, x => x.rightShoulder))) { PressKey(Down(g, x => x.leftShoulder) ? KeyCode.LeftArrow : KeyCode.RightArrow); return true; }
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

    // 画面の真ん中に いちばん近いマス（真ん中から 画面の高さの1割より遠いマスは選ばない）。なければ -1
    int CenterSquare()
    {
        var cam = Camera.main;
        if (cam == null) return -1;
        var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
        int best = -1; float bestDist = Screen.height * 0.1f;
        foreach (var kv in squares)
        {
            Vector3 sp = cam.WorldToScreenPoint(kv.Value.transform.position);
            if (sp.z <= 0) continue; // カメラの後ろ
            float d = Vector2.Distance(sp, center);
            if (d < bestDist) { bestDist = d; best = kv.Key; }
        }
        return best;
    }

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
            if (board[n].Sea && !S.CurrentPlayer.Surf) continue; // 見えていない海への道
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

    // 図鑑：十字キー上下 1体・左右 10体、○ くわしく、× もどる、L1/R1 ほかの人の図鑑（UI仕様書 6.4）
    bool PadDex(Gamepad g)
    {
        int dy = Down(g, x => x.dpad.down) ? 1 : Down(g, x => x.dpad.up) ? -1 : 0;
        int dx = Down(g, x => x.dpad.right) ? 1 : Down(g, x => x.dpad.left) ? -1 : 0;
        int sw = Down(g, x => x.rightShoulder) ? 1 : Down(g, x => x.leftShoulder) ? -1 : 0;
        bool ok = Down(g, x => x.buttonEast), back = Down(g, x => x.buttonSouth);
        if (Down(g, x => x.buttonNorth)) { DexCycleType(); return true; } // △ タイプでしぼりこむ
        if (dy == 0 && dx == 0 && sw == 0 && !ok && !back) return false;
        DexInput(dy, dx, ok, back, sw);
        return true;
    }

    void ScreenSelect(Button b, List<Button> list)
    {
        if (screenSel != null) screenSel.image.color = screenSelColor;
        screenSel = b;
        screenSelColor = b.image.color;
        screenIndex = list.IndexOf(b);
        b.image.color = new Color(1f, 0.85f, 0.4f);
    }

    // ---- コントローラー登録（1.1・15.10.1・16.1 #4）：はじめから・つづきから（枠を選んだあと）の前に必ず通る ----
    // ○を押したコントローラーが 1番、次に押した別のものが 2番。キーボードは Enter で1つの機器として登録できる（開発中の確認用）
    // 1台で遊ぶなら、1番のあと START（か画面のボタン）で すすむ。×（Esc）で1つ前の画面へ。つづきからも通すのは 相談.md 15

    System.Action padNext, padBack;

    void PadRegisterShow()
    {
        var bg = FullScreen("PadRegister");
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.2f, 0.12f), new Vector2(0.8f, 0.88f), true, 18, 0);
        Ui.Line(col, "コントローラー登録", 56);
        Ui.Line(col, $"{padRegister + 1}ばんの コントローラーで ○を おしてください", 38);
        Ui.Line(col, "（キーボードで あそぶなら Enter）", 26, new Color(0.8f, 0.8f, 0.8f));
        if (padRegister == 1) Ui.Line(col, $"1ばん：{DeviceName(padDevice[0])}", 30, new Color(1, 0.9f, 0.5f));
        Ui.Line(col, $"つながっている コントローラー：{Gamepad.all.Count}台", 26);
        if (padRegister == 1) Ui.Button(col, "1台で すすむ（START）", FinishPadRegister, 520, 66);
        Ui.Button(col, "もどる（×）", CancelPadRegister, 520, 66);
    }

    static string DeviceName(int id) => InputSystem.GetDeviceById(id) is InputDevice d ? (d is Keyboard ? "キーボード" : d.displayName) : "なし";

    void PadRegisterUpdate()
    {
        if (Input.GetKeyDown(KeyCode.Escape) || Gamepad.all.Any(g => g.buttonSouth.wasPressedThisFrame)) { CancelPadRegister(); return; }
        if (padRegister == 1 && Gamepad.all.Any(g => g.startButton.wasPressedThisFrame)) { FinishPadRegister(); return; }
        int id = -1;
        if (Input.GetKeyDown(KeyCode.Return) && Keyboard.current != null) id = Keyboard.current.deviceId;
        foreach (var g in Gamepad.all) if (g.buttonEast.wasPressedThisFrame) id = g.deviceId;
        if (id < 0 || padDevice.Contains(id)) return; // 同じ機器で2回押しても 2番にはしない
        padDevice[padRegister] = id;
        if (++padRegister >= 2) FinishPadRegister(); else PadRegisterShow();
    }

    void FinishPadRegister() { padRegister = -1; padNext(); }
    void CancelPadRegister() { padRegister = -1; padDevice[0] = padDevice[1] = -1; padBack(); }

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
            // 確定前のものは捨てて選び直し（17.4）：開いていた下のメニュー（どうぐ・にげるの確認など）と、移動で選んでいたマス。命令として送ったものは そのまま
            subMenu = null; padSquare = -1;
            if (game != null && !playing) RefreshAll();
            var bg = Ui.Panel(canvas.transform, "PadLost", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0.85f));
            Ui.Label(bg.transform, $"コントローラー{string.Join("・", lost.Select(n => n + 1))}が はずれました\nつなぎなおしてください\n（別の番号で つながったら ○。登録をやめるなら Esc）", 44, TextAnchor.MiddleCenter);
            padLostPanel = bg.gameObject;
        }
        // つなぎ直したコントローラーが 新しい機器として見えたときは、○を押せば はずれた番号に入る
        var back = Gamepad.all.FirstOrDefault(g => !padDevice.Contains(g.deviceId) && g.buttonEast.wasPressedThisFrame);
        if (back != null) padDevice[lost[0]] = back.deviceId;
        if (Input.GetKeyDown(KeyCode.Escape)) { foreach (int n in lost) padDevice[n] = -1; } // キーボードで遊び続ける
        return true;
    }
#endif

    // コントローラー登録を通してから next へ（× なら back へ）。Input System がなければ そのまま next
    void RegisterThen(System.Action next, System.Action back)
    {
#if ENABLE_INPUT_SYSTEM
        padNext = next; padBack = back;
        padDevice[0] = padDevice[1] = -1;
        padRegister = 0;
        PadRegisterShow();
#else
        next();
#endif
    }
}
