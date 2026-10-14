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
// 移動：←→で止まるマスを選ぶ、○で選ぶ・決定、×で1歩もどす。ふだんはコントローラー1、プレイヤー戦は 仕掛けた側が1・相手が2（16.5 #28）
// ponytail: タイトル・設定・ゲーム設定の画面は まだマウスとキーボードだけ（名前の五十音表はコントローラーでも打てる）
public partial class GameRunner
{
    int menuCursor;                            // メニューで選んでいる行
    string menuTitle;                          // メニューが変わったらカーソルを先頭に戻すため
    int padSquare = -1;                        // 移動で選んでいるマス
    readonly int[] padDevice = { -1, -1 };     // 登録したコントローラー1・2（デバイスの番号）。-1 ならつないだ順
    int padRegister = -1;                      // コントローラー登録中なら 0（1番を待つ）・1（2番を待つ）
    GameObject padLostPanel;                   // コントローラーが抜けたときの表示（17.4）

    // 演出の早送り（スペース、コントローラーの ○・×）
    static bool SkipHeld()
    {
        if (Input.GetKey(KeyCode.Space)) return true;
#if ENABLE_INPUT_SYSTEM
        foreach (var g in Gamepad.all) if (g.buttonEast.isPressed || g.buttonSouth.isPressed) return true;
#endif
        return false;
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
        if (game == null || playing) return false;
        var g = ActingPad();
        if (g == null) return false;
        if (overlay != null) { if (Down(g, x => x.buttonSouth) || Down(g, x => x.buttonEast)) { CloseOverlay(); return true; } return false; }
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
        // 移動：←→で止まるマスを選ぶ、○で選ぶ（もう選んであれば決定）、×で1歩もどす
        if (S.Phase == Phase.Moving && subMenu == null)
        {
            var cands = PadSquares();
            if (Down(g, x => x.dpad.right) || Down(g, x => x.dpad.left))
            {
                int i = cands.IndexOf(padSquare);
                i = Down(g, x => x.dpad.right) ? i + 1 : i - 1;
                padSquare = cands.Count == 0 ? -1 : cands[(i % cands.Count + cands.Count) % cands.Count];
                RefreshAll();
                return true;
            }
            if (Down(g, x => x.buttonEast))
            {
                int me = S.CurrentPlayer.Id;
                var path = S.Move.Path;
                if (padSquare >= 0 && path[path.Count - 1] != padSquare && cands.Contains(padSquare))
                {
                    if (S.CurrentPlayer.MoveMode == MoveMode.Highlight) Send(new ChooseDestination { Player = me, Square = padSquare });
                    else { Send(new StepTo { Player = me, Square = padSquare }); padSquare = -1; }
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

    // 移動でコントローラーが選べるマス：まとめて方式は止まれるマス、1マスずつ方式は 進めるとなりのマス
    List<int> PadSquares()
    {
        var p = S.CurrentPlayer;
        if (p.MoveMode == MoveMode.Highlight) return S.Move.Reachable.OrderBy(id => board[id].X).ThenBy(id => board[id].Z).ToList();
        var cur = S.Move.Path[S.Move.Path.Count - 1];
        return board[cur].Neighbors.Where(n => game.Validate(new StepTo { Player = p.Id, Square = n }) == null).ToList();
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
