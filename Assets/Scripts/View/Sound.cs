using System.Collections.Generic;
using UnityEngine;

// BGM と効果音（仕様書1.3、17.7）。音の素材は Resources/Audio/BGM/<名前> と Resources/Audio/SE/<名前> に置く（mp3・ogg・wav）
// ファイルがなければ鳴らないだけ。BGM の名前：title, board, board_area1〜, wild, trainer, gym, legend, endroll
// 効果音の名前：dice, step, hit, guard, miss, faint, ball, catch, levelup, evolve, badge, money, item, heal, lowhp（HPが赤いあいだ くり返す）
// 音量はマスター（AudioListener.volume）× BGM／効果音。設定は PlayerPrefs に残る
public class Sound : MonoBehaviour
{
    AudioSource bgm, se;
    string current;
    readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();

    public static float Master { get => PlayerPrefs.GetFloat("volume", 1); set { PlayerPrefs.SetFloat("volume", value); AudioListener.volume = value; } }
    public static float Bgm { get => PlayerPrefs.GetFloat("volume_bgm", 0.7f); set => PlayerPrefs.SetFloat("volume_bgm", value); }
    public static float Se { get => PlayerPrefs.GetFloat("volume_se", 1); set => PlayerPrefs.SetFloat("volume_se", value); }

    void Awake()
    {
        bgm = gameObject.AddComponent<AudioSource>();
        bgm.loop = true;
        se = gameObject.AddComponent<AudioSource>();
        AudioListener.volume = Master;
        ApplyVolume();
    }

    public void ApplyVolume() { bgm.volume = Bgm; se.volume = Se; }

    AudioClip Load(string path)
    {
        if (!cache.TryGetValue(path, out var clip)) cache[path] = clip = Resources.Load<AudioClip>(path);
        return clip;
    }

    // BGM を変える。names は先に見つかったものを使う（例：board_area2 がなければ board）。同じ曲なら流し続ける
    public void PlayBgm(params string[] names)
    {
        foreach (var name in names)
        {
            var clip = Load("Audio/BGM/" + name);
            if (clip == null) continue;
            if (current == name && bgm.isPlaying) return;
            current = name;
            bgm.clip = clip;
            bgm.Play();
            return;
        }
        current = null;
        bgm.Stop();
    }

    public void PlaySe(string name)
    {
        var clip = Load("Audio/SE/" + name);
        if (clip != null) se.PlayOneShot(clip);
    }

    // HPが赤いあいだ くり返し鳴る警告音（効果音の名前 lowhp。ファイルがなければ ピコピコを作って鳴らす）
    AudioSource alarm;
    public void SetAlarm(bool on)
    {
        if (alarm == null)
        {
            alarm = gameObject.AddComponent<AudioSource>();
            alarm.loop = true;
            alarm.clip = Load("Audio/SE/lowhp");
            if (alarm.clip == null) alarm.clip = Beep();
        }
        alarm.volume = Se * 0.18f; // 強すぎたので半分に（0.35 → 0.18）
        if (on && !alarm.isPlaying) alarm.Play();
        else if (!on && alarm.isPlaying) alarm.Stop();
    }

    // ピコピコ：高い音と少し低い音を短く鳴らして、間をあける（0.6秒でくり返し）
    static AudioClip Beep()
    {
        const int rate = 22050; int n = (int)(rate * 0.6f);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / rate;
            float f = t < 0.09f ? 1568f : t >= 0.13f && t < 0.22f ? 1175f : 0; // ソ と レ
            data[i] = f > 0 && Mathf.Sin(2 * Mathf.PI * f * t) > 0 ? 0.25f : f > 0 ? -0.25f : 0;
        }
        var clip = AudioClip.Create("lowhp", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
