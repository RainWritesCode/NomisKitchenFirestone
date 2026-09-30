using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NomisKitchen.Reports
{
    internal sealed class Redactor
    {
        const int MaxNames = 256;
        const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        static readonly Regex PlayerNameField = new Regex(@"PlayerName=(?<name>[^\r\n]+)", Options);
        static readonly Regex EntityReference = new Regex(@"\bEntity=(?<name>[^\[\]=\r\n]+?) (?=tag=|EffectCardId=|EffectIndex=|Target=|SubOption=|TriggerKeyword=)", Options);
        static readonly Regex BattleTag = new Regex(@"(?<![\p{L}\p{M}\p{N}_#])[\p{L}\p{M}\p{N}_]{2,24}#\d{3,7}(?!\d)", Options);
        static readonly Regex AccountId = new Regex(@"(?i)(""?hi""?\s*[=:]\s*)\d+(\s*,?\s*""?lo""?\s*[=:]\s*)\d+", Options);
        static readonly Regex UserFolder = new Regex(@"(?i)((?:[A-Z]:|\\\\[^\\/\r\n""]+\\[^\\/\r\n""]+)[\\/]+(?:Users|Documents and Settings|profiles?\$?)[\\/]+)[^\\/\r\n""]+", Options);
        static readonly Regex OneDriveOrganisation = new Regex(@"(?i)(OneDrive - )[^\\/\r\n""]+", Options);
        static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", Options);
        static readonly Regex Secret = new Regex(@"(?i)\b(access_token|refresh_token|id_token|api[_-]?key|apikey|client_secret|secret|password|passwd|token|session_?id|sessionkey|auth(?:orization)?)(""?\s*[:=]\s*""?)[^\s""',;&]{6,}", Options);
        static readonly Regex Bearer = new Regex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", Options);
        static readonly HashSet<string> NotNames = new HashSet<string>(StringComparer.Ordinal) { "GameEntity", "UNKNOWN", "0" };

        readonly Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _kept = new HashSet<string>(StringComparer.Ordinal);
        readonly Regex _profiles;
        readonly Regex _userSegment;
        Regex _names;

        public Redactor()
        {
            var profiles = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetEnvironmentVariable("OneDrive"),
                    Environment.GetEnvironmentVariable("OneDriveConsumer"),
                    Environment.GetEnvironmentVariable("OneDriveCommercial"),
                    Environment.GetEnvironmentVariable("HOMESHARE"),
                }
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim().Length > 3)
                .Select(p => p.Trim().TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => p.Length)
                .Select(p => Regex.Escape(p).Replace(@"\\", @"[\\/]+"))
                .ToArray();
            if (profiles.Length > 0) _profiles = new Regex("(?i)(?:" + string.Join("|", profiles) + ")", Options);
            var user = UserName();
            if (!string.IsNullOrWhiteSpace(user) && user.Length >= 2)
                _userSegment = new Regex(@"(?i)(?<=[\\/])" + Regex.Escape(user) + @"(?=[\\/])", Options);
        }

        public int NameCount => _aliases.Count;

        static string UserName()
        {
            try { return ReadUserName(); }
            catch
            {
                try { return Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
                catch { return null; }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ReadUserName() => Environment.UserName;

        public void Keep(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _kept.Add(name);
            if (_aliases.Remove(name)) _names = null;
        }

        public void Learn(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (line.IndexOf("PlayerName=", StringComparison.Ordinal) >= 0)
            {
                foreach (Match m in PlayerNameField.Matches(line)) Add(m.Groups["name"].Value.Trim());
            }
            if (line.IndexOf("Entity=", StringComparison.Ordinal) >= 0)
            {
                foreach (Match m in EntityReference.Matches(line))
                {
                    var name = m.Groups["name"].Value;
                    if (!IsNumber(name)) Add(name);
                }
            }
            if (line.IndexOf('#') >= 0)
            {
                foreach (Match m in BattleTag.Matches(line)) Add(m.Value);
            }
        }

        public string CleanGameLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            if (_names == null && _aliases.Count > 0) Compile();
            if (_names != null) line = _names.Replace(line, m => _aliases[m.Value]);
            if (line.IndexOf('#') >= 0) line = BattleTag.Replace(line, m => _kept.Contains(m.Value) ? m.Value : Alias(m.Value));
            if (line.IndexOf("hi=", StringComparison.Ordinal) >= 0) line = AccountId.Replace(line, "${1}0${2}0");
            return line;
        }

        public string Clean(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            if (_names == null && _aliases.Count > 0) Compile();
            if (_names != null) line = _names.Replace(line, m => _aliases[m.Value]);
            if (line.IndexOf('#') >= 0) line = BattleTag.Replace(line, m => _kept.Contains(m.Value) ? m.Value : Alias(m.Value));
            if (line.IndexOf("hi", StringComparison.OrdinalIgnoreCase) >= 0) line = AccountId.Replace(line, "${1}0${2}0");
            if (line.IndexOf('@') >= 0) line = Email.Replace(line, "<email>");
            line = Bearer.Replace(line, "$1 <redacted>");
            line = Secret.Replace(line, "$1$2<redacted>");
            if (line.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0) line = OneDriveOrganisation.Replace(line, "$1<organisation>");
            if (_profiles != null) line = _profiles.Replace(line, "<profile>");
            line = UserFolder.Replace(line, "$1<user>");
            if (_userSegment != null) line = _userSegment.Replace(line, "<user>");
            return line;
        }

        void Add(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || NotNames.Contains(name) || _kept.Contains(name) || _aliases.ContainsKey(name)) return;
            if (_aliases.Count >= MaxNames) return;
            _aliases[name] = "Player" + (_aliases.Count + 1);
            _names = null;
        }

        string Alias(string name)
        {
            if (!_aliases.ContainsKey(name)) Add(name);
            return _aliases.TryGetValue(name, out var alias) ? alias : "Player";
        }

        void Compile()
        {
            var alternation = string.Join("|", _aliases.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            _names = new Regex(@"(?<![\p{L}\p{M}\p{N}_#])(?:" + alternation + @")(?![\p{L}\p{M}\p{N}_#])", RegexOptions.CultureInvariant);
        }

        static bool IsNumber(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return s.Length > 0;
        }
    }
}
