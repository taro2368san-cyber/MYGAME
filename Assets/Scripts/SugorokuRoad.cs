using UnityEngine;

// マスとマスをつなぐ道。Square A と Square B のあいだに、自動で伸びて表示される。
// 道でつながったマスどうしは、両方向に行き来できる（道を消すと、つながりも切れる）。
[ExecuteAlways]
public class SugorokuRoad : MonoBehaviour
{
    public SugorokuSquare squareA;
    public SugorokuSquare squareB;
    public float width = 0.35f;

    void Update()
    {
        Fit(); // マスを動かしたら、道もついてくる
    }

    // 2つのマスのあいだにぴったり伸ばす
    public void Fit()
    {
        if (squareA == null || squareB == null) return;
        Vector3 from = squareA.transform.position;
        Vector3 to = squareB.transform.position;
        if (from == to) return;

        Vector3 position = (from + to) / 2f;
        Quaternion rotation = Quaternion.LookRotation(to - from);
        Vector3 scale = new Vector3(width, 1f, Vector3.Distance(from, to)); // 厚みは見た目（Visual）側で決める
        if (transform.position != position || transform.rotation != rotation || transform.localScale != scale)
        {
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
        }
    }
}
