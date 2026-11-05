using System;
using UnityEngine;
using UnityEngine.UI;

// 確認ダイアログ（仕様書 16.10 の骨組み）：画面のまん中に 問いの文と「○ はい　× いいえ」。ボタンを押した瞬間に決まる（カーソルは出さない。7.3 と同じ考え方）
// 中身は メニューの2行（はい＝Enter、いいえ＝Esc）なので、キーボードは今までどおり。コントローラーは ○・×（GameRunner.Pad.cs）
// 出ている間は 後ろを暗い板でふさぎ、メニュー欄・イベント画面の選択肢は出さない（後ろのボタンは押せない）
public partial class GameRunner
{
    RectTransform confirmRoot;
    Text confirmText;
    Button confirmYes;
    string confirmQuestion; // 出している問い（出していなければ null）
    bool ConfirmOpen => confirmQuestion != null;

    void BuildConfirm()
    {
        confirmRoot = Ui.Panel(canvas.transform, "Confirm", Vector2.zero, Vector2.one, Ui.DimColor).rectTransform; // 後ろを暗くして、押せなくする
        var box = Ui.Frame(confirmRoot, "Box", new Vector2(0.28f, 0.36f), new Vector2(0.72f, 0.64f));
        confirmText = Ui.Label(Ui.Rect(box.transform, "Question", new Vector2(0, 0.42f), Vector2.one), "", 34, TextAnchor.MiddleCenter);
        confirmText.rectTransform.offsetMin = new Vector2(24, 0); confirmText.rectTransform.offsetMax = new Vector2(-24, -12);
        var row = Ui.Stack(box.transform, "Buttons", new Vector2(0, 0.08f), new Vector2(1, 0.42f), false, 48, 0);
        row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        row.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;
        confirmYes = Ui.Button(row, "○ はい", () => PressConfirm(true), 260, 72);
        confirmYes.image.color = Ui.YesColor;
        Ui.Button(row, "× いいえ", () => PressConfirm(false), 260, 72).image.color = Ui.NoColor;
        confirmRoot.gameObject.SetActive(false);
    }

    // 確認を出す（メニューを作るところから呼ぶ。描くのは いつもどおり DrawMenu）。問いには 名前や金額を差し込んだ文を渡す。canYes が false なら「はい」は押せない
    void Confirm(string question, Action yes, Action no, bool canYes = true)
    {
        Add("はい", yes, KeyCode.Return, canYes);
        Add("いいえ", no, KeyCode.Escape);
        confirmQuestion = question;
    }

    // DrawMenu から：出すか隠すか
    void DrawConfirm()
    {
        if (confirmRoot == null) return;
        confirmRoot.gameObject.SetActive(ConfirmOpen);
        if (!ConfirmOpen) return;
        confirmText.text = confirmQuestion;
        confirmYes.interactable = menu.Count > 0 && menu[0].enabled;
        confirmYes.GetComponentInChildren<Text>().color = new Color(1, 1, 1, confirmYes.interactable ? 1 : 0.35f);
        confirmRoot.SetAsLastSibling(); // イベント画面の上（大きな知らせは 出すときに さらに上へ）
    }

    void PressConfirm(bool yes)
    {
        if (playing || !ConfirmOpen) return;
        PressKey(yes ? KeyCode.Return : KeyCode.Escape);
    }

    // メニューを空にする（確認も閉じる）
    void ClearMenu()
    {
        menu.Clear(); menuArt.Clear();
        confirmQuestion = null;
    }
}
