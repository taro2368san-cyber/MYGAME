using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// 名前入力の五十音表（仕様書16.1 #7）。十字キー（矢印キー）で選んで Enter で入力、Backspace で1文字消す、Esc で終わり。マウスでも押せる
// 文字はひらがな（濁点・半濁点・小さい字）と「ー」だけ。上限は調整値 name_max（6文字）
// ponytail: コントローラー（十字キー・○・×）は Input System を入れるときにつなぐ
public partial class GameRunner
{
    // 列ごとに5文字（空白は空きマス）
    static readonly string[] KanaColumns =
    {
        "あいうえお", "かきくけこ", "さしすせそ", "たちつてと", "なにぬねの", "はひふへほ", "まみむめも", "や　ゆ　よ", "らりるれろ", "わ　を　ん",
        "がぎぐげご", "ざじずぜぞ", "だぢづでど", "ばびぶべぼ", "ぱぴぷぺぽ", "ぁぃぅぇぉ", "ゃ　ゅ　ょ", "っ　ゎ　ー",
    };

    int kanaFor = -1, kanaX, kanaY; // 入力中のプレイヤー（-1 なら閉じている）とカーソル
    Text kanaName;
    Button[,] kanaButtons;

    void OpenKana(int player)
    {
        kanaFor = player;
        names[player] = names[player] ?? "";
        var bg = Ui.Panel(canvas.transform, "Kana", Vector2.zero, Vector2.one, new Color(0.03f, 0.05f, 0.1f, 0.97f));
        var col = Ui.Stack(bg.transform, "Rows", new Vector2(0.03f, 0.08f), new Vector2(0.97f, 0.92f), true, 10, 0);
        Ui.Line(col, $"P{player + 1}の なまえ（{(int)data.Required("name_max")}文字まで）", 40);
        kanaName = Ui.Line(col, "", 54, new Color(1, 0.95f, 0.6f));
        kanaButtons = new Button[KanaColumns.Length, 5];
        for (int y = 0; y < 5; y++)
        {
            var row = Row(col);
            for (int x = 0; x < KanaColumns.Length; x++)
            {
                char ch = KanaColumns[x][y];
                int cx = x, cy = y;
                var b = Ui.Button(row, ch == '　' ? "" : ch.ToString(), () => { kanaX = cx; kanaY = cy; KanaType(); }, 62, 62);
                b.interactable = ch != '　';
                kanaButtons[x, y] = b;
            }
        }
        var tools = Row(col);
        Ui.Button(tools, "1文字けす（Backspace）", KanaBack, 360, 64);
        Ui.Button(tools, "おわり（Esc）", CloseKana, 300, 64);
        KanaRefresh();
    }

    void KanaType()
    {
        char ch = KanaColumns[kanaX][kanaY];
        if (ch == '　' || names[kanaFor].Length >= (int)data.Required("name_max")) return;
        names[kanaFor] += ch;
        KanaRefresh();
    }

    void KanaBack()
    {
        if (names[kanaFor].Length > 0) names[kanaFor] = names[kanaFor].Substring(0, names[kanaFor].Length - 1);
        KanaRefresh();
    }

    void CloseKana()
    {
        kanaFor = -1;
        ShowSetup();
    }

    void KanaRefresh()
    {
        kanaName.text = names[kanaFor] == "" ? "（まだ ありません）" : names[kanaFor] + "＿";
        for (int x = 0; x < KanaColumns.Length; x++)
            for (int y = 0; y < 5; y++)
                kanaButtons[x, y].image.color = x == kanaX && y == kanaY ? new Color(1f, 0.85f, 0.4f) : Color.white;
    }

    // 矢印キーでカーソル（空きマスは飛ばす）、Enter で入力、Backspace で消す、Esc で終わり
    void KanaUpdate()
    {
        int dx = Input.GetKeyDown(KeyCode.RightArrow) ? 1 : Input.GetKeyDown(KeyCode.LeftArrow) ? -1 : 0;
        int dy = Input.GetKeyDown(KeyCode.DownArrow) ? 1 : Input.GetKeyDown(KeyCode.UpArrow) ? -1 : 0;
        if (dx != 0 || dy != 0)
        {
            int cols = KanaColumns.Length;
            for (int n = 0; n < cols * 5; n++)
            {
                kanaX = (kanaX + dx + cols) % cols;
                kanaY = (kanaY + dy + 5) % 5;
                if (KanaColumns[kanaX][kanaY] != '　') break;
            }
            KanaRefresh();
        }
        if (Input.GetKeyDown(KeyCode.Return)) KanaType();
        if (Input.GetKeyDown(KeyCode.Backspace)) KanaBack();
        if (Input.GetKeyDown(KeyCode.Escape)) CloseKana();
    }
}
