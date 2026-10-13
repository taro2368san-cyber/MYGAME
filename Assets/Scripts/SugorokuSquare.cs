using System.Linq;
using UnityEngine;

// マスの種類。この名前がそのままマスに表示される。
// ※ 数字で保存されるので、新しい種類は必ずいちばん最後に追加すること（途中に入れると、置いたマスの種類がずれる）
public enum SquareType
{
    空マス, 草むら, ジム, トレーナー, イベントマス, ポケモンセンター, ショップ,
    モンスターボール, 鍵付きモンスターボール, ライバル, ロケット団, ポイント交換NPC, 職業,
    スタート, ゴール,
    ワープ,
}

// すごろくのマス。マスどうしは道（SugorokuRoad）でつながる。
public class SugorokuSquare : MonoBehaviour
{
    [Tooltip("マスの番号。盤面を書き出すときに自動でつく（手で変えない）")]
    public int id;

    [Tooltip("マスの種類。複数のマスを選んで、まとめて変えられる")]
    public SquareType type;

    [Tooltip("ワープマスのとき、同じ番号のワープマスどうしがつながる（0 = 未設定）")]
    public int warpId;

    [Tooltip("ショップマスのとき、設計メモ/ショップ.csv の id（例：shop_nibi）。空ならマスのある場所のショップを使う")]
    public string shopId = "";

    // このマスが属する場所（真下にある板の場所。板の上になければ null）
    public string Place
    {
        get
        {
            var board = SugorokuBoard.FindAt(transform.position);
            return board != null ? board.place : null;
        }
    }

    // このマスのマップID（真下の板の mapId。メインマップなら ""）
    public string MapId => SugorokuBoard.FindAt(transform.position)?.mapId ?? "";

    MaterialPropertyBlock block;

    // 種類を変えたら、マスの上の名前も変える（長い名前は2行にして、マスに収まる大きさにする）
    void OnValidate()
    {
        var label = GetComponentInChildren<TextMesh>();
        if (label == null) return;
        label.text = type switch
        {
            SquareType.ポケモンセンター => "ポケモン\nセンター",
            SquareType.モンスターボール => "モンスター\nボール",
            SquareType.鍵付きモンスターボール => "鍵付き\nモンスター\nボール",
            SquareType.ポイント交換NPC => "ポイント\n交換NPC",
            SquareType.ワープ when warpId > 0 => $"ワープ\n{warpId}",
            _ => type.ToString(),
        };
        int longest = label.text.Split('\n').Max(line => line.Length);
        label.characterSize = Mathf.Min(0.06f, 1.1f / (6.4f * longest));
#if UNITY_EDITOR
        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(label);
        UnityEditor.EditorUtility.SetDirty(label);
#endif
    }

#if UNITY_EDITOR
    // マスを選んだとき、Scene ビューに属している場所を出す
    void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.Label(transform.position + Vector3.up * 0.6f, $"場所: {Place ?? "（板の上にない）"}");
    }
#endif

    // マスを光らせる（amount: 0 = 元の色 〜 1 = glowColor）。見た目（Visual）の中身が何でも光るように、名前の文字以外を全部光らせる
    public void SetGlow(Color glowColor, float amount)
    {
        if (block == null) block = new MaterialPropertyBlock();
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (r.GetComponent<TextMesh>() != null) continue;
            if (amount <= 0f)
            {
                r.SetPropertyBlock(null);
                continue;
            }
            block.SetColor("_Color", Color.Lerp(r.sharedMaterial.color, glowColor, amount));
            r.SetPropertyBlock(block);
        }
    }
}
