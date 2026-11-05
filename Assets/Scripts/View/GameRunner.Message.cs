using System.Collections;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// メッセージの文の送り方（UI仕様書 2章 G・仕様書 1.3、相談37）：下のセリフ欄（G）と イベント画面のセリフが 同じ部品（TypeWriter・ReadMessage）を通る
// 1文字ずつ 決まった速さで出す。出している途中の ○ で全文。全文が出たら ▼ を出して、○ を押すまで待つ。○ で次の文へ
// 待つのは 出来事の再生（PlayQueue）の中だけ。再生が終わると メニューが出るので、○ はメニューに効く（ぶつからない）
// 帯の知らせ（Banner）や 時間で決まっている演出（17.6）は 今までどおり時間で進む（○・スペースを押し続けると早送り）
public partial class GameRunner
{
    sealed class TypeWriter
    {
        public string Full = "";
        public float Typed;
        public bool Waiting;                 // 全文が出て ○ を待っている（▼ を出す）
        public bool Typing => Typed < Full.Length;
        public string Shown => Full.Substring(0, Mathf.Min(Full.Length, (int)Typed));
        int startFrame = -1;
        public void Start(string text) { Full = text ?? ""; Typed = 0; Waiting = false; startFrame = Time.frameCount; }
        // 毎フレーム（LateUpdate）：決まった速さで進める。途中で ○ を押したら 全文。
        // 出し始めたフレームの ○ は数えない（前の文を送った ○・メニューで決めた ○ で、次の文まで 全文にならないように）
        public void Tick(bool pressed) { if (Typing) Typed = pressed && Time.frameCount != startFrame ? Full.Length : Typed + TypeSpeed * Time.deltaTime; }
    }

    readonly TypeWriter logWriter = new TypeWriter();   // G セリフ欄
    readonly TypeWriter eventWriter = new TypeWriter(); // イベント画面のセリフ
    Text logArrow, eventArrow;

    // 文が出きるのを待ち、そのあと ○ を押すまで待つ
    IEnumerator ReadMessage(TypeWriter w)
    {
        while (w.Typing) yield return null; // 途中の ○ は Tick が 全文にする（同じ ○ で 次へは進まない）
        w.Waiting = true;
        yield return null;                  // 全文にした ○ と 同じフレームでは進めない
        while (!AdvancePressed()) yield return null;
        w.Waiting = false;
    }

    // セリフ欄を マウスで押しても ○ と同じ（15.10.1：キーボードとマウスでも同じ操作）。押したことは 次に読んだフレームの間だけ有効
    bool clickPending;
    int clickFrame = -1;
    void ClickAdvance() { if (logWriter.Typing || logWriter.Waiting || eventWriter.Typing || eventWriter.Waiting) clickPending = true; }

    static void Clickable(Image box, UnityEngine.Events.UnityAction onClick)
    {
        var b = box.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
    }

    // 文を送る ○：コントローラーは いま操作する人（ふだんは1番、プレイヤー戦はバトル画面と同じ決まり）。キーボードは Enter（スペースも）
    bool AdvancePressed()
    {
        if (clickPending) { clickPending = false; clickFrame = Time.frameCount; }
        if (clickFrame == Time.frameCount) return true;
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) return true;
#if ENABLE_INPUT_SYSTEM
        var g = game != null ? ActingPad() : null;
        if (g == null) g = Gamepad.current;
        if (g != null && g.buttonEast.wasPressedThisFrame) return true;
#endif
        return false;
    }

    // ▼（待っている印）：右下に 点滅させて出す
    static Text MakeArrow(Transform box)
    {
        var t = Ui.Label(Ui.Rect(box, "Next", new Vector2(0.94f, 0.02f), new Vector2(0.995f, 0.3f)), "▼", 26, TextAnchor.LowerRight, new Color(1, 0.9f, 0.5f));
        t.enabled = false;
        return t;
    }

    static void ShowArrow(Text arrow, TypeWriter w)
    {
        if (arrow != null) arrow.enabled = w.Waiting && Time.time % 0.8f < 0.55f;
    }
}
