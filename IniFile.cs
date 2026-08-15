using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Minimal ini file reader/writer. Leaf class (depends only on Logging for nothing in fact — but
// kept dependency-free). Comment/empty lines (# or ; prefix) are preserved verbatim and never
// participate in key matching.
static class IniFile
{
    public static List<string> Load(string path)
    {
        try
        {
            if (File.Exists(path)) return new List<string>(File.ReadAllLines(path, Encoding.UTF8));
        }
        catch (Exception ex) { Logging.Log("IniFile.Load failed: " + ex.Message); }
        return new List<string>();
    }

    // returns the value of the first ACTIVE (non-comment, non-empty) line matching `key`,
    // trimmed; null when absent
    public static string Get(List<string> lines, string key)
    {
        foreach (string raw in lines)
        {
            string t = raw.Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (t.Substring(0, eq).Trim().ToLowerInvariant() == key.ToLowerInvariant())
                return t.Substring(eq + 1).Trim();
        }
        return null;
    }

    // replace the value of the first active line matching `key`; append a new line when absent
    public static void Set(List<string> lines, string key, string value)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (t.Substring(0, eq).Trim().ToLowerInvariant() == key.ToLowerInvariant())
            {
                lines[i] = key + "=" + value;
                return;
            }
        }
        lines.Add(key + "=" + value);
    }

    public static void Save(string path, List<string> lines)
    {
        try
        {
            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
        }
        catch (Exception ex) { Logging.Log("IniFile.Save failed: " + ex.Message); }
    }
}
