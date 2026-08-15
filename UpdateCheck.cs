using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

// One-shot GitHub "latest release" check. Static, no UI dependency (never touches Forms/controls);
// results are read later by the caller. Failures are logged and swallowed.
static class UpdateCheck
{
    public const string RepoOwner = "KAIbsb";
    public const string RepoName = "dsh-tray";
    public const string ReleasesApiUrl = "https://api.github.com/repos/KAIbsb/dsh-tray/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/KAIbsb/dsh-tray/releases/latest";

    // latest/download/... always redirects to the newest release binary
    public static string DownloadUrl { get { return ReleasesPageUrl + "/download/dsh-tray.exe"; } }
    // GitHub release checksums live next to the asset as <asset>.sha256
    public static string ChecksumUrl { get { return DownloadUrl + ".sha256"; } }

    public static string LatestTag;
    public static string LatestVersion;

    // true once a newer version than this build has been discovered (result of the last check)
    public static bool IsNewerAvailable { get { return LatestVersion != null; } }

    // fire-and-forget background check; the caller reads LatestTag/LatestVersion later
    public static void CheckOnce(string appVersion)
    {
        ThreadPool.QueueUserWorkItem(delegate { Check(appVersion); });
    }

    // synchronous check; returns true when a newer release is available. Call on a worker
    // thread (it blocks up to the request timeout); no UI interaction.
    public static bool Check(string appVersion)
    {
        try
        {
            // .NET Framework defaults to TLS 1.0/1.1 via Schannel on some systems; GitHub
            // requires TLS 1.2+, so enable it explicitly (idempotent, process-wide)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(ReleasesApiUrl);
            req.UserAgent = "dsh-tray/" + appVersion;
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.Accept = "application/vnd.github+json";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                string json = sr.ReadToEnd();
                string tag = ExtractJsonString(json, "tag_name");
                if (tag == null) { Logging.Log("UpdateCheck: no tag_name in response"); return false; }
                string latest = NormalizeVersion(tag);
                if (latest == null) { Logging.Log("UpdateCheck: unparsable tag " + tag); return false; }
                if (IsNewer(latest, appVersion))
                {
                    LatestTag = tag;
                    LatestVersion = latest;
                    Logging.Log("UpdateCheck: new version available " + tag);
                    return true;
                }
                Logging.Log("UpdateCheck: up to date (latest " + tag + ")");
                return false;
            }
        }
        catch (Exception ex) { Logging.Log("UpdateCheck failed: " + ex.Message); return false; }
    }

    // Generic small GET returning the trimmed response body, or null on any failure.
    static string GetText(string url, string tag)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "dsh-tray/" + (LatestVersion ?? "unknown");
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd().Trim();
        }
        catch (Exception ex) { Logging.Log(tag + " failed: " + ex.Message); return null; }
    }

    // Download the latest release exe to destPath, then verify its SHA-256 against the published
    // checksum. Returns true only when the file exists on disk and the hash matches. On any
    // failure the partial file is deleted (and re-created empty) so nothing stale lingers. Call on
    // a worker thread; no UI interaction.
    public static bool DownloadAndVerify(string destPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 1. download the binary
            bool exeOk = false;
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(DownloadUrl);
                req.UserAgent = "dsh-tray-update/" + (LatestVersion ?? "unknown");
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    resp.GetResponseStream().CopyTo(fs);
                exeOk = true;
            }
            if (!exeOk) { TryDelete(destPath); return false; }

            // 2. parse the checksum ("<hex>  dsh-tray.exe"); take the first whitespace-delimited token
            string checksumText = GetText(ChecksumUrl, "UpdateCheck checksum fetch");
            if (string.IsNullOrEmpty(checksumText))
            {
                Logging.Log("UpdateCheck: checksum unavailable");
                TryDelete(destPath);
                return false;
            }
            string expected = null;
            int sp = checksumText.IndexOfAny(new[] { ' ', '\t' });
            expected = sp > 0 ? checksumText.Substring(0, sp).Trim() : checksumText.Trim();

            // 3. compute the local SHA-256 and compare (case-insensitive)
            string actual;
            using (var fs = new FileStream(destPath, FileMode.Open, FileAccess.Read))
                actual = Sha256Hex(fs);
            if (expected.Length == 64 && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Logging.Log("UpdateCheck: download verified sha256=" + actual);
                return true;
            }
            Logging.Log("UpdateCheck: sha256 mismatch expected=" + expected + " actual=" + actual);
            TryDelete(destPath);
            return false;
        }
        catch (Exception ex)
        {
            Logging.Log("UpdateCheck download/verify failed: " + ex.Message);
            TryDelete(destPath);
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static string Sha256Hex(FileStream fs)
    {
        byte[] hash = SHA256.Create().ComputeHash(fs);
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // minimal "key": "value" string extraction (Github release payload, tag_name is a plain
    // version string, no escapes). No JSON library dependency.
    static string ExtractJsonString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i + needle.Length);
        if (i < 0) return null;
        i = json.IndexOf('"', i + 1);
        if (i < 0) return null;
        int j = json.IndexOf('"', i + 1);
        if (j < 0) return null;
        return json.Substring(i + 1, j - i - 1);
    }

    // "v1.2.3" -> "1.2.3"; keeps up to three numeric dot-segments, ignores a non-numeric suffix
    static string NormalizeVersion(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        string t = tag.Trim();
        if (t[0] == 'v' || t[0] == 'V') t = t.Substring(1);
        int[] parts = ParseParts(t);
        if (parts == null || parts.Length == 0) return null;
        string result = parts[0].ToString();
        for (int i = 1; i < parts.Length; i++) result += "." + parts[i];
        return result;
    }

    // true when `latest` (e.g. 1.2.3) is strictly newer than the first three segments of `current`
    // (e.g. 1.1.0.0)
    static bool IsNewer(string latest, string current)
    {
        int[] l = ParseParts(latest);
        int[] c = ParseParts(current);
        if (l == null || c == null) return false;
        for (int i = 0; i < 4; i++)
        {
            int lv = i < l.Length ? l[i] : 0;
            int cv = i < c.Length ? c[i] : 0;
            if (lv > cv) return true;
            if (lv < cv) return false;
        }
        return false;
    }

    static int[] ParseParts(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        string[] segs = s.Trim().Split('.');
        var nums = new int[Math.Min(segs.Length, 4)];
        for (int i = 0; i < nums.Length; i++)
        {
            string seg = segs[i].Trim();
            int j = 0;
            while (j < seg.Length && seg[j] >= '0' && seg[j] <= '9') j++;
            if (j == 0 || !int.TryParse(seg.Substring(0, j), out nums[i])) return null;
        }
        return nums;
    }
}
