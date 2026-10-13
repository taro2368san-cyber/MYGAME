using System.Collections.Generic;
using System.Text;

namespace Sugoroku.Rules
{
    // CSV の1シート分。列は見出しの名前で引く（同じ見出しが2つあるときは最初の列、見出しが空の列は無視）
    public sealed class CsvTable
    {
        public readonly string Name;
        public readonly List<string[]> Rows = new List<string[]>();
        readonly Dictionary<string, int> columns = new Dictionary<string, int>();

        public CsvTable(string name, string text, List<string> problems)
        {
            Name = name;
            var lines = Parse(text);
            if (lines.Count == 0) { problems.Add($"[エラー] {name}: 中身が空です"); return; }

            var header = lines[0];
            var duplicates = new List<string>();
            for (int i = 0; i < header.Length; i++)
            {
                string h = header[i].Trim();
                if (h.Length == 0) continue;
                if (columns.ContainsKey(h)) duplicates.Add(h);
                else columns[h] = i;
            }
            if (duplicates.Count > 0)
                problems.Add($"[警告] {name}: 見出しが重複しています（最初の列を使います）: {string.Join("、", duplicates)}");

            for (int i = 1; i < lines.Count; i++)
            {
                bool empty = true;
                foreach (var cell in lines[i]) if (cell.Trim().Length > 0) { empty = false; break; }
                if (!empty) Rows.Add(lines[i]);
            }
        }

        public bool Has(string column) => columns.ContainsKey(column);

        // row の column 列の値（列がない・セルがないときは空文字）
        public string Get(string[] row, string column) =>
            columns.TryGetValue(column, out int i) && i < row.Length ? row[i].Trim() : "";

        // 引用符・セル内の改行に対応した CSV の読み取り
        static List<string[]> Parse(string text)
        {
            var result = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            int start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    result.Add(row.ToArray()); row.Clear();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); result.Add(row.ToArray()); }
            return result;
        }
    }
}
