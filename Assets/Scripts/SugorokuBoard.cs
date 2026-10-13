using UnityEngine;

// 盤面の板（地図）。この板の上に置いたマスは、この「場所」に属する。
public class SugorokuBoard : MonoBehaviour
{
    [Tooltip("この板の場所の名前。xlsx の「エリア」シートの場所名と同じにする（例：1番道路）")]
    public string place;

    [Tooltip("メインマップ以外の板（洞窟・建物・島など）につける id（例：iwayama）。メインマップの板は空のまま。あなぬけひもで使う")]
    public string mapId = "";

    // pos の真下にある板（どの板の上にもなければ null）。板が重なっている所では、上に見えている板を選ぶ
    public static SugorokuBoard FindAt(Vector3 pos)
    {
        SugorokuBoard found = null;
        float top = float.MinValue;
        foreach (var board in FindObjectsByType<SugorokuBoard>())
        {
            var b = board.GetComponent<Renderer>().bounds;
            if (pos.x < b.min.x || pos.x > b.max.x || pos.z < b.min.z || pos.z > b.max.z || b.max.y <= top) continue;
            found = board;
            top = b.max.y;
        }
        return found;
    }
}
