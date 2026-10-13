using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// ネットワーク状のすごろく。
// ・マスどうしは道（SugorokuRoad）で両方向につながる
// ・分かれ道では、光っているマスをクリックして進む道を選ぶ
// ・来た道をすぐにもどることはできない（行き止まりのときだけもどる）
// ・ゴールにぴったり止まったらクリア。ゴールを超えた分は、来た道をもどる
// 画面の流れ: タイトル画面 →（スタート）→ ゲーム →（ゴール）→ ゴール画面 →「もういちど」か「タイトルへ」
public class SugorokuGame : MonoBehaviour
{
    [Header("盤面")]
    [SerializeField] SugorokuSquare startSquare;
    [SerializeField] SugorokuSquare goalSquare;
    [SerializeField] Transform piece;
    [SerializeField] Vector3 pieceOffset = new Vector3(0f, 0.1f, 0.25f); // マスの中心からコマを置く位置までのずれ

    [Header("ゲーム中のUI")]
    [SerializeField] GameObject hud;
    [SerializeField] Button rollButton;
    [SerializeField] Text diceText;
    [SerializeField] Text messageText;

    [Header("タイトル画面")]
    [SerializeField] GameObject titleScreen;
    [SerializeField] Button startButton;

    [Header("ゴール画面")]
    [SerializeField] GameObject clearScreen;
    [SerializeField] Text clearText;
    [SerializeField] Button retryButton;
    [SerializeField] Button titleButton;

    [Header("演出")]
    [SerializeField] float stepTime = 0.25f; // 1マス進むのにかかる秒数
    [SerializeField] float hopHeight = 0.5f; // コマが跳ねる高さ
    [SerializeField] Color glowColor = new Color(1f, 0.7f, 0.1f); // 分かれ道で選べるマスを光らせる色

    // マスごとの「となりのマス」（道を両方向にまとめたもの）
    readonly Dictionary<SugorokuSquare, List<SugorokuSquare>> neighbors = new Dictionary<SugorokuSquare, List<SugorokuSquare>>();
    // スタートから通ってきたマス（最後が今いるマス）。ゴールを超えたら、これをさかのぼってもどる
    readonly List<SugorokuSquare> history = new List<SugorokuSquare>();
    int rollCount; // サイコロをふった回数

    List<SugorokuSquare> choices; // 分かれ道で選べるマス（選んでいないときは null）
    SugorokuSquare chosen;        // クリックで選ばれたマス

    SugorokuSquare Current => history[history.Count - 1];
    SugorokuSquare Previous => history.Count >= 2 ? history[history.Count - 2] : null;

    void Start()
    {
        BuildNetwork();
        rollButton.onClick.AddListener(RollDice);
        startButton.onClick.AddListener(StartGame);
        retryButton.onClick.AddListener(StartGame);
        titleButton.onClick.AddListener(ShowTitle);
        ShowTitle();
    }

    void Update()
    {
        // 分かれ道で、光っているマスがクリックされたらそれを選ぶ
        if (choices != null && Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                var square = hit.collider.GetComponentInParent<SugorokuSquare>();
                if (choices.Contains(square)) chosen = square;
            }
        }
    }

    void ShowTitle() => ShowScreen(title: true);
    void StartGame() => ShowScreen(title: false);

    void ShowScreen(bool title)
    {
        ResetGame();
        titleScreen.SetActive(title);
        hud.SetActive(!title);
        clearScreen.SetActive(false);
    }

    void ResetGame()
    {
        history.Clear();
        history.Add(startSquare);
        rollCount = 0;
        piece.position = startSquare.transform.position + pieceOffset;
        diceText.text = "?";

        int steps = StepsToGoal();
        rollButton.interactable = steps >= 0;
        messageText.text = steps >= 0
            ? $"サイコロをふってね！ ゴールまで 最短 {steps} マス"
            : "スタートからゴールまで道がつながっていません。マスをつないでね！";
    }

    void RollDice()
    {
        // Random.Range(1, 7) は 1〜6 のどれか（7は含まれない）
        StartCoroutine(PlayTurn(Random.Range(1, 7)));
    }

    IEnumerator PlayTurn(int dice)
    {
        rollButton.interactable = false;
        rollCount++;

        // サイコロが回っているように数字を切りかえる
        for (int i = 0; i < 10; i++)
        {
            diceText.text = Random.Range(1, 7).ToString();
            yield return new WaitForSeconds(0.05f);
        }
        diceText.text = dice.ToString();
        messageText.text = $"{dice} が出た！";

        // 1マスずつ進む。ゴールに着いてもまだ歩数が残っていたら、その分だけ来た道をもどる
        int overshoot = 0;
        for (int left = dice; left > 0; left--)
        {
            if (Current == goalSquare)
            {
                overshoot = left;
                break;
            }

            var options = NextOptions(Current, Previous);
            if (options.Count == 0) break; // どこにもつながっていない

            var next = options[0];
            if (options.Count > 1)
            {
                messageText.text = $"分かれ道！ 光っているマスをクリックしてね（のこり {left} マス）";
                yield return WaitForChoice(options);
                next = chosen;
                messageText.text = $"{dice} が出た！";
            }
            history.Add(next);
            yield return Hop(next.transform.position + pieceOffset);
        }

        if (overshoot > 0)
        {
            messageText.text = $"ゴールを {overshoot} マス通りすぎた！ もどります";
            for (int i = 0; i < overshoot && history.Count > 1; i++)
            {
                history.RemoveAt(history.Count - 1);
                yield return Hop(Current.transform.position + pieceOffset);
            }
        }

        if (Current == goalSquare)
        {
            messageText.text = "ゴール！";
            // その場で2回ジャンプしてからゴール画面を出す
            for (int i = 0; i < 2; i++)
                yield return Hop(piece.position);
            clearText.text = $"{rollCount} 回でゴールしました！";
            clearScreen.SetActive(true);
            yield break;
        }

        int steps = StepsToGoal();
        string rest = steps >= 0 ? $"ゴールまで 最短 あと {steps} マス" : "ゴールへの道がありません…";
        messageText.text = overshoot > 0 ? $"{overshoot} マスもどった… {rest}" : rest;
        rollButton.interactable = true;
    }

    // 分かれ道：選べるマスを光らせて、クリックされるまで待つ（選ばれたマスは chosen に入る）
    IEnumerator WaitForChoice(List<SugorokuSquare> options)
    {
        choices = options;
        chosen = null;
        while (chosen == null)
        {
            float glow = Mathf.PingPong(Time.time * 2f, 1f); // 0〜1 をいったりきたり
            foreach (var option in options) option.SetGlow(glowColor, glow);
            yield return null;
        }
        foreach (var option in options) option.SetGlow(glowColor, 0f);
        choices = null;
    }

    // 次に進めるマス。来たマスにはもどれない（行き止まりで、ほかに道がないときだけもどれる）
    List<SugorokuSquare> NextOptions(SugorokuSquare from, SugorokuSquare cameFrom)
    {
        var options = new List<SugorokuSquare>(Neighbors(from));
        if (options.Count > 1) options.Remove(cameFrom);
        return options;
    }

    // ゴールまで最短で何マスか（来た道をもどれないルールも考える）。たどり着けないときは -1
    int StepsToGoal()
    {
        var visited = new HashSet<(SugorokuSquare, SugorokuSquare)>();
        var queue = new Queue<(SugorokuSquare prev, SugorokuSquare cur, int steps)>();
        queue.Enqueue((Previous, Current, 0));
        while (queue.Count > 0)
        {
            var (prev, cur, steps) = queue.Dequeue();
            if (cur == goalSquare) return steps;
            foreach (var next in NextOptions(cur, prev))
            {
                if (visited.Add((cur, next)))
                    queue.Enqueue((cur, next, steps + 1));
            }
        }
        return -1;
    }

    // 道（SugorokuRoad）をもとに、マスごとの「となりのマス」をまとめる
    void BuildNetwork()
    {
        foreach (var road in FindObjectsByType<SugorokuRoad>())
        {
            var a = road.squareA;
            var b = road.squareB;
            if (a == null || b == null || a == b || Neighbors(a).Contains(b)) continue;
            Neighbors(a).Add(b);
            Neighbors(b).Add(a);
        }
    }

    List<SugorokuSquare> Neighbors(SugorokuSquare square)
    {
        if (!neighbors.TryGetValue(square, out var list))
            neighbors[square] = list = new List<SugorokuSquare>();
        return list;
    }

    // コマを target までぴょんと跳ねさせる（target が今の場所なら、その場でジャンプ）
    IEnumerator Hop(Vector3 target)
    {
        Vector3 start = piece.position;
        for (float t = 0f; t < 1f; t += Time.deltaTime / stepTime)
        {
            Vector3 p = Vector3.Lerp(start, target, t);
            p.y += Mathf.Sin(t * Mathf.PI) * hopHeight;
            piece.position = p;
            yield return null;
        }
        piece.position = target;
    }
}
