using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Sugoroku.Rules
{
    // ゲームを始めたときの設定（セーブに書く。同じ設定と同じ命令の列で、同じゲームが再現できる、15.9）
    public sealed class GameSettings
    {
        public List<(string name, MoveMode mode, string starter)> Players = new List<(string, MoveMode, string)>();
        public List<string> Characters = new List<string>();
        public int MaxWeeks;
        public ulong Seed;
        public string EventRule = "";   // 選んだときの値（"random" のまま。ランダムの結果は乱数で毎回同じになる）
        public List<bool> Cpu = new List<bool>();
    }

    // セーブファイル（1.2）。中身は「開始時の設定」と「受け付けた命令の列」だけ。読み込むときは命令を流し直す
    // ponytail: 命令の流し直しなので、データ（xlsx・CSV）を変えると古いセーブが読めなくなることがある。困るようになったら状態を丸ごと書く形に変える
    // 書き方：1行に1つ。「キー=値」と「cmd 命令の名前 欄=値 …」。値は % で書き換えてある（空白・改行が入っても壊れない）
    public static class SaveFile
    {
        public sealed class Summary { public DateTime SavedAt; public int Week, MaxWeeks; public string NextPlayer = ""; }

        public static string Write(GameState s)
        {
            var sb = new StringBuilder();
            void Kv(string k, object v) => sb.Append(k).Append('=').Append(Esc(Convert.ToString(v, CultureInfo.InvariantCulture))).Append('\n');
            Kv("version", 1);
            Kv("saved", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            Kv("week", s.Week); Kv("maxweeks", s.Settings.MaxWeeks); Kv("next", s.Phase == Phase.GameOver ? "" : s.CurrentPlayer.Name);
            Kv("seed", s.Settings.Seed); Kv("rule", s.Settings.EventRule);
            foreach (var (name, mode, starter) in s.Settings.Players) Kv("player", $"{Esc(name)} {mode} {Esc(starter)}");
            foreach (var c in s.Settings.Characters) Kv("character", c);
            foreach (var c in s.Settings.Cpu) Kv("cpu", c);
            foreach (var cmd in s.History)
            {
                sb.Append("cmd ").Append(cmd.GetType().Name);
                foreach (var f in cmd.GetType().GetFields())
                {
                    var v = f.GetValue(cmd);
                    if (v != null) sb.Append(' ').Append(f.Name).Append('=').Append(Esc(Convert.ToString(v, CultureInfo.InvariantCulture)));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static (GameSettings settings, List<Command> commands, Summary summary) Read(string text)
        {
            var settings = new GameSettings();
            var commands = new List<Command>();
            var summary = new Summary();
            foreach (var line in text.Split('\n').Where(l => l.Trim() != ""))
            {
                if (line.StartsWith("cmd ")) { commands.Add(ReadCommand(line.Substring(4).Trim())); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("読めない行: " + line);
                string k = line.Substring(0, eq), v = Unesc(line.Substring(eq + 1).Trim());
                switch (k)
                {
                    case "saved": summary.SavedAt = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                    case "week": summary.Week = int.Parse(v); break;
                    case "maxweeks": settings.MaxWeeks = summary.MaxWeeks = int.Parse(v); break;
                    case "next": summary.NextPlayer = v; break;
                    case "seed": settings.Seed = ulong.Parse(v); break;
                    case "rule": settings.EventRule = v; break;
                    case "character": settings.Characters.Add(v); break;
                    case "cpu": settings.Cpu.Add(bool.Parse(v)); break;
                    case "player":
                        var parts = v.Split(' ');
                        settings.Players.Add((Unesc(parts[0]), (MoveMode)Enum.Parse(typeof(MoveMode), parts[1]), Unesc(parts[2])));
                        break;
                }
            }
            return (settings, commands, summary);
        }

        static Command ReadCommand(string line)
        {
            var parts = line.Split(' ');
            var type = typeof(Command).Assembly.GetType("Sugoroku.Rules." + parts[0]);
            if (type == null || !typeof(Command).IsAssignableFrom(type)) throw new FormatException("知らない命令: " + parts[0]);
            var cmd = (Command)Activator.CreateInstance(type);
            foreach (var p in parts.Skip(1))
            {
                int eq = p.IndexOf('=');
                var f = type.GetField(p.Substring(0, eq));
                if (f == null) continue;
                string v = Unesc(p.Substring(eq + 1));
                f.SetValue(cmd, f.FieldType.IsEnum ? Enum.Parse(f.FieldType, v) : Convert.ChangeType(v, f.FieldType, CultureInfo.InvariantCulture));
            }
            return cmd;
        }

        static string Esc(string s) => Uri.EscapeDataString(s ?? "");
        static string Unesc(string s) => Uri.UnescapeDataString(s);
    }
}
