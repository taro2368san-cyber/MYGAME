using System.Collections;
using System.Linq;
using Sugoroku.Rules;
using UnityEngine;

// 音（1.3、17.7）、はじめての場面のヒント・あそびかた（17.8）
// 文はどれも 文言シートのキー（hint_○○／help_○○）で差し替えられる。ここにあるのは、シートにないときの文
public partial class GameRunner
{
    Sound sound;

    // ---- 音 ----

    void BoardBgm()
    {
        if (game == null || S.Players.Count == 0) { sound.PlayBgm("board"); return; }
        string area = new string((board[S.CurrentPlayer.Square].Area ?? "").Where(char.IsDigit).ToArray());
        sound.PlayBgm("board_area" + area, "board");
    }

    // 出来事ごとの効果音と BGM の切り替え
    void EventSound(GameEvent e)
    {
        switch (e)
        {
            case TurnStarted _: BoardBgm(); break;
            case DiceRolled _: sound.PlaySe("dice"); break;
            case BattleStarted b:
                if (b.Kind == BattleKind.Wild) sound.PlayBgm("wild");
                else if (b.Kind == BattleKind.Gym || b.Kind == BattleKind.Rival) sound.PlayBgm("gym", "trainer");
                else if (b.Kind == BattleKind.Event) sound.PlayBgm("legend", "gym", "trainer");
                else sound.PlayBgm("trainer");
                break;
            case BattleEnded _: case Caught _: BoardBgm(); break;
            case RoundResolved r when r.Item == null: sound.PlaySe(r.Missed ? "miss" : r.Guarded ? "guard" : "hit"); break;
            case PokemonFainted _: sound.PlaySe("faint"); break;
            case BallThrown bt: sound.PlaySe(bt.Caught ? "catch" : "ball"); break;
            case ExpGained x when x.Levels > 0: sound.PlaySe("levelup"); break;
            case Evolved _: sound.PlaySe("evolve"); break;
            case BadgeEarned _: sound.PlaySe("badge"); break;
            case MoneyChanged m when m.Amount > 0: sound.PlaySe("money"); break;
            case ItemGained ig when !ig.BagFull: sound.PlaySe("item"); break;
            case PartyHealed _: sound.PlaySe("heal"); break;
        }
    }

    // ---- 演出（17.6）：サイコロ 1.5秒、先攻・後攻のカード 2秒（短縮で1秒）。スペースで早送り ----
    // ponytail: 文字だけの演出。サイコロやカードの絵・3Dは素材ができたら差し替える

    IEnumerator ShowBig(string text, Color color)
    {
        banner.text = text; banner.color = color;
        banner.transform.parent.gameObject.SetActive(true);
        banner.transform.parent.SetAsLastSibling();
        yield return null;
    }

    IEnumerator DiceAnim(DiceRolled d)
    {
        var rnd = new System.Random();
        for (float t = 0; t < 1.1f && !Input.GetKey(KeyCode.Space); t += 0.08f)
        {
            yield return ShowBig("サイコロ　" + string.Join("　", d.Dice.Select(_ => rnd.Next(1, 11))), new Color(0.8f, 0.8f, 0.8f));
            yield return Wait(0.08f);
        }
        yield return ShowBig(d.Dice.Count > 1 ? $"サイコロ　{string.Join(" ＋ ", d.Dice)} ＝ {d.Value}" : $"サイコロ　{d.Value}", Color.white);
        yield return Wait(0.4f);
        banner.transform.parent.gameObject.SetActive(false);
    }

    IEnumerator CardAnim(InitiativeDrawn x)
    {
        float total = shortBattle ? 1f : 2f;
        string Row(System.Func<int, string> card) => string.Join(" ", Enumerable.Range(0, x.Cards.Count).Select(card));
        yield return ShowBig($"カード {x.Cards.Count}枚\n{Row(_ => "［？］")}", Color.white);
        yield return Wait(total * 0.35f);
        yield return ShowBig($"1枚 ひく…\n{Row(i => i == x.Picked ? "【？】" : "［？］")}", new Color(1f, 0.95f, 0.6f));
        yield return Wait(total * 0.3f);
        // めくると、引いたカードの先攻が決まる。ほかのカードも表にする（同じ側の先攻なら「先」）
        yield return ShowBig($"{x.FirstPokemon}の 先攻！\n{Row(i => i == x.Picked ? "【先】" : x.Cards[i] == x.First ? "［先］" : "［後］")}", new Color(1f, 0.85f, 0.4f));
        yield return Wait(total * 0.35f);
        banner.transform.parent.gameObject.SetActive(false);
    }

    // ---- 自分に起きたことは、自分の番の最初にまとめて伝える（17.5）----
    readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>> notices = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>();

    // ほかの人の番に、このプレイヤーに起きたこと（妨害・奪われた道具・とられたファン）を覚えておく
    void CollectNotice(GameEvent e)
    {
        int now = S.CurrentPlayer.Id;
        void Note(int player, string text)
        {
            if (player < 0 || player == now) return; // 自分の番に起きたことは、その場で見ている
            if (!notices.TryGetValue(player, out var list)) notices[player] = list = new System.Collections.Generic.List<string>();
            list.Add(text);
        }
        switch (e)
        {
            case AilmentAdded a when a.By >= 0 && a.By != a.Player: Note(a.Player, $"{S.Players[a.By].Name}に {a.Name}に された（{a.Weeks}週）"); break;
            case ItemLost l when l.To >= 0: Note(l.Player, $"{S.Players[l.To].Name}に {l.Item}を とられた"); break;
            case OwnerChanged o when o.Previous >= 0 && o.Owner != o.Previous: Note(o.Previous, $"{(o.Owner >= 0 ? S.Players[o.Owner].Name : "だれか")}に {board[o.Square].Place}の ファンを とられた"); break;
        }
    }

    IEnumerator ShowNotices(int player)
    {
        if (!notices.TryGetValue(player, out var list) || list.Count == 0) yield break;
        notices.Remove(player);
        var p = S.Players[player];
        yield return Banner($"{p.Name}さんへ：ほかの人の番に あったこと\n" + string.Join("\n", list.Take(4)) + (list.Count > 4 ? $"\nほか {list.Count - 4}件" : ""), PlayerColor(p), 3);
    }

    // ---- はじめての場面のヒント（17.8）。人が操作するプレイヤーに、1回だけ。設定でオフにできる ----

    static bool HintsOn { get => PlayerPrefs.GetInt("hints", 1) == 1; set => PlayerPrefs.SetInt("hints", value ? 1 : 0); }

    // その出来事で出すヒント（出さなければ null）
    string HintFor(GameEvent e)
    {
        if (!HintsOn) return null;
        (string key, string text, int player) h;
        switch (e)
        {
            case Landed l when board[l.Square].Type == "草むら": h = ("hint_grass", "草むらに止まると、野生のポケモンが出ることがあります。弱らせてから ボールを投げると 捕まえやすくなります", l.Player); break;
            case Landed l when board[l.Square].Type == "ジム": h = ("hint_gym", "ジムはフルバトルです。町のジムトレーナーが みんな倒されてからでないと 挑めません", l.Player); break;
            case BattleStarted b when b.Players[0] >= 0: h = ("hint_battle", "バトルは 攻撃側と防御側が 同時にコマンドを選びます。物理攻撃は「まもる」、特殊攻撃は「特殊守り」、必殺技は「カウンター」で防げます", b.Players[0]); break;
            case BallPrompt bp: h = ("hint_ball", "攻撃と防御が1往復するたびに ボールを投げられます。相手のHPが少ないほど 捕まえやすくなります", bp.Player); break;
            case FacilityOpened f when f.Kind == FacilityKind.Center: h = ("hint_center", "ポケモンセンターでは、回復・手持ちと控えの入れ替え・もちものの着けはずし・職業の変更が できます", f.Player); break;
            case FacilityOpened f when f.Kind == FacilityKind.Shop: h = ("hint_shop", "ショップでは道具を買ったり売ったりできます。町ごとに 品ぞろえが ちがいます", f.Player); break;
            case OwnerChanged o when o.Owner >= 0 && board[o.Square].Type == "トレーナー": h = ("hint_fan", "倒したトレーナーは ファンになります。7週ごとに年貢が入り、止まると回復してもらえます", o.Owner); break;
            case AilmentAdded a when a.By != a.Player: h = ("hint_jama", "同じエリアにいると、ほかの人から 妨害の道具を使われることがあります。みがわりにんぎょうで 1回 防げます", a.Player); break;
            default: return null;
        }
        if (h.player < 0 || h.player >= S.Players.Count || S.Players[h.player].IsCpu || PlayerPrefs.GetInt("seen_" + h.key, 0) == 1) return null;
        PlayerPrefs.SetInt("seen_" + h.key, 1);
        return narrator.Line(h.key, h.text);
    }

    IEnumerator ShowHint(string text) => Banner("ヒント：" + text, new Color(1f, 0.95f, 0.6f), 3);

    // ---- あそびかた（17.8）：その他メニューから ----

    static readonly (string key, string title, string text)[] HelpTopics =
    {
        ("help_move", "移動", "サイコロは1〜10。まとめて方式は止まれるマスが光り、1マスずつ方式は となりへ1歩ずつ進みます。直前のマスには戻れません（止まれるマスがないときだけ、折り返しも数えます）。自転車でサイコロが増え、くつで出目を決められます"),
        ("help_battle", "バトル", "攻撃側と防御側が同時にコマンドを選びます。物理攻撃は「まもる」、特殊攻撃は「特殊守り」、必殺技は「カウンター」が当たりで、ガードできるとダメージが1/3、カウンターは2倍を はね返します。弱点を突くと1.5倍。必殺技は続けて打てません。先頭1体どうしの クイックバトルと、手持ち全員の フルバトルがあります"),
        ("help_catch", "捕獲", "野生とのバトルでは、攻撃と防御が1往復するたびに ボールを投げられます。相手のHPが少ないほど、ボールが良いほど、バッジが多いほど 捕まえやすくなります。手持ちは3体まで、あふれた子は控えに入ります"),
        ("help_fan", "ファン", "トレーナーに勝つと そのトレーナーが ファンになります。7週ごとに年貢が入り、自分のファンのマスでは回復と道具、ほかの人のファンのマスでは回復代を払います。プレイヤーどうしのバトルの勝利報酬や バトルサーチャーで 奪い合えます"),
        ("help_job", "職業", "条件を満たすと職業が解放され、ポケモンセンターで就いたり変えたりできます。タイプの職業は そのタイプのポケモンが強くなるなど、職業ごとに得意なことがあります"),
        ("help_win", "勝ち方", "最後の週が終わったときの 勝利ポイントが いちばん多い人の勝ちです。バッジ・お金・ファン・図鑑などが ポイントになります。いまの順位は「その他 → 順位」で見られます"),
    };

    void ShowHelp(int topic)
    {
        var col = Overlay("Help");
        Ui.Line(col, "あそびかた：" + HelpTopics[topic].title, 44);
        Ui.Line(col, narrator.Line(HelpTopics[topic].key, HelpTopics[topic].text), 28);
        var row = Row(col);
        for (int i = 0; i < HelpTopics.Length; i++) { int k = i; Ui.Button(row, HelpTopics[i].title, () => ShowHelp(k), 150, 56); }
        Ui.Button(Row(col), "閉じる", CloseOverlay, 200, 64);
    }
}
