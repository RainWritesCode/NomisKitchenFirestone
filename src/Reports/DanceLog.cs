using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NomisKitchen.Reports
{
    internal static class DanceLog
    {
        const string Source = ":Nomi Can't Dance]";
        const string CombinedSource = ":Nomi's Kitchen]";
        const string GameStart = "] perf game start";
        const string TurnedOff = "the fix turns itself off";
        const string TurnedOffNow = "turned off for this session";
        const string LoadingLine = "Loading [Nomi Can't Dance ";
        const string CombinedLoadingLine = "Loading [Nomi's Kitchen ";
        static readonly Regex Fix = new Regex(@":(?:Nomi Can't Dance|Nomi's Kitchen)\] \[(?<salt>[0-9a-f]{8})\] (?<kind>drop|merge|replay|magnet):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex Loaded = new Regex(@":(?:Nomi Can't Dance|Nomi's Kitchen)\] (?:Nomi Can't Dance (?<version>\S+) loaded, .*fix (?<state>on|off)\.|Plugin loaded\.)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex Loading = new Regex(@"Loading \[(?:Nomi Can't Dance|Nomi's Kitchen) (?<version>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal sealed class Summary
        {
            public bool Installed;
            public bool LoadedSeen;
            public string Version;
            public bool? FixOn;
            public int Drop;
            public int Merge;
            public int Replay;
            public int Magnet;
            public int SessionFixes;
            public bool FailedThisSession;
            public string Window;

            public int Fixes => Drop + Merge + Replay + Magnet;
            public bool Loaded => LoadedSeen;

            public Dictionary<string, object> ToManifest() => new Dictionary<string, object>
            {
                ["installed"] = Installed,
                ["loaded"] = Loaded,
                ["version"] = Version,
                ["fixOn"] = FixOn,
                ["fixes"] = Fixes,
                ["drop"] = Drop,
                ["merge"] = Merge,
                ["replay"] = Replay,
                ["magnet"] = Magnet,
                ["sessionFixes"] = SessionFixes,
                ["failedThisSession"] = FailedThisSession,
                ["window"] = Window,
            };
        }

        internal static Summary Summarise(string bepinexLog, bool installed, bool fixSetting)
        {
            var summary = new Summary { Installed = installed, Window = "none" };
            if (string.IsNullOrEmpty(bepinexLog)) return summary;

            var lines = new List<string>();
            using (var reader = new StringReader(bepinexLog))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.IndexOf(Source, StringComparison.Ordinal) >= 0 || line.IndexOf(CombinedSource, StringComparison.Ordinal) >= 0
                        || line.IndexOf(GameStart, StringComparison.Ordinal) >= 0
                        || line.IndexOf(LoadingLine, StringComparison.Ordinal) >= 0 || line.IndexOf(CombinedLoadingLine, StringComparison.Ordinal) >= 0)
                        lines.Add(line);
            }

            int start = lines.FindLastIndex(l => l.IndexOf(GameStart, StringComparison.Ordinal) >= 0);
            string lastSalt = null;
            foreach (var line in lines)
            {
                var loading = Loading.Match(line);
                if (loading.Success) summary.Version = loading.Groups["version"].Value;
                var loaded = Loaded.Match(line);
                if (loaded.Success)
                {
                    summary.LoadedSeen = true;
                    if (loaded.Groups["version"].Success)
                    {
                        summary.Version = loaded.Groups["version"].Value;
                        summary.FixOn = loaded.Groups["state"].Value == "on";
                    }
                    else
                    {
                        summary.FixOn = fixSetting;
                    }
                }
                if (line.IndexOf(TurnedOff, StringComparison.Ordinal) >= 0 || line.IndexOf(TurnedOffNow, StringComparison.Ordinal) >= 0)
                    summary.FailedThisSession = true;
                var fix = Fix.Match(line);
                if (fix.Success)
                {
                    summary.SessionFixes++;
                    lastSalt = fix.Groups["salt"].Value;
                }
            }

            summary.Window = start >= 0 ? "game" : lastSalt != null ? "salt" : "none";
            for (int i = Math.Max(0, start + 1); i < lines.Count; i++)
            {
                var fix = Fix.Match(lines[i]);
                if (!fix.Success || (start < 0 && fix.Groups["salt"].Value != lastSalt)) continue;
                switch (fix.Groups["kind"].Value)
                {
                    case "drop": summary.Drop++; break;
                    case "merge": summary.Merge++; break;
                    case "magnet": summary.Magnet++; break;
                    default: summary.Replay++; break;
                }
            }
            return summary;
        }
    }
}
