using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NomisKitchen.Reports
{
    internal sealed class LogReporter
    {
        const string Endpoint = "https://api.nomi.gg/v1/reports";
        const string GameStartMarker = "GameState.DebugPrintPower() - CREATE_GAME";
        const long PowerLogCap = 256L * 1024 * 1024;
        const long PowerLogHead = 16L * 1024 * 1024;
        const long BepInExCap = 8L * 1024 * 1024;
        const long PluginLogCap = 2L * 1024 * 1024;
        const long MaxZipBytes = 30L * 1024 * 1024;
        const int MaxPending = 5;
        const int UploadTimeoutMs = 120_000;
        static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

        readonly ReporterConfig _config;
        static string _userAgent = "NomisKitchenHDT";
        readonly Version _version;
        int _sending;
        DateTime? _gameStartUtc;
        GameInfo _lastGame;
        string _reporterTag;
        volatile bool _stopped;

        public LogReporter(ReporterConfig config, Version version)
        {
            _userAgent = config.UserAgent ?? "NomisKitchenHDT/" + version;
            _config = config;
            _version = version;
        }

        string PendingDir => _config.PendingDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "NomisKitchenReports", "pending");

        public void Start()
        {
            Log.Info("Log reports: " + (_config.ShareGameLogs ? "sending after each Battlegrounds game" : "off"));
            _ = Task.Run(SendPendingAsync);
        }

        public void Stop()
        {
            _stopped = true;
        }

        internal void OnGameStart()
        {
            if (_stopped) return;
            _gameStartUtc = DateTime.UtcNow;
        }

        internal void OnGameEnd()
        {
            if (_stopped) return;
            try
            {
                var started = _gameStartUtc;
                _gameStartUtc = null;
                var game = _config.EndedGame;
                if (game == null) return;
                var tag = _config.BattleTag ?? _reporterTag;
                _reporterTag = tag;
                var install = HearthstonePaths.InstallDir(_config);
                var power = HearthstonePaths.NewestPowerLog(install);
                _lastGame = new GameInfo
                {
                    Turns = game.Turns,
                    PlayerName = tag,
                    HsBuild = game.HsBuild,
                    Duos = game.Duos,
                    StartedUtc = started,
                    EndedUtc = DateTime.UtcNow,
                    PowerLog = power,
                    PowerLogEnd = power != null ? new FileInfo(power).Length : 0,
                };
                if (!_config.ShareGameLogs)
                {
                    _ = Task.Run(SendPendingAsync);
                    return;
                }
                var info = _lastGame;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SettleDelay).ConfigureAwait(false);
                    if (_stopped || !_config.ShareGameLogs) return;
                    await SendAsync("auto", null, info, info.PlayerName).ConfigureAwait(false);
                });
            }
            catch (Exception ex) { Log.Error("Log report: game end hook failed", ex); }
        }

        async Task<Result> SendAsync(string reason, string note, GameInfo game, string reporterTag)
        {
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            {
                Log.Warn("Log report not sent (" + reason + "): another report is still being sent");
                return Result.Failed("A report is already being sent.");
            }
            Result result;
            try { result = await CollectAndUploadAsync(reason, note, game, reporterTag).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _sending, 0); }
            if (result.Ok) _ = Task.Run(SendPendingAsync);
            return result;
        }

        async Task<Result> CollectAndUploadAsync(string reason, string note, GameInfo game, string reporterTag)
        {
            byte[] zip;
            try { zip = await Task.Run(() => Build(reason, note, game, reporterTag)).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Error("Log report: could not collect the logs", ex);
                return Result.Failed("Could not collect the logs: " + ex.Message);
            }
            var result = await UploadAsync(zip).ConfigureAwait(false);
            if (result.Ok)
            {
                Log.Info("Log report sent (" + reason + "): id " + result.Id + ", " + zip.Length / 1024 + " KB");
            }
            else
            {
                Log.Warn("Log report not sent (" + reason + "): " + result.Error + (result.Retry ? " (kept, will retry)" : ""));
                if (result.Retry) KeepPending(reason, zip);
            }
            return result;
        }

        byte[] Build(string reason, string note, GameInfo game, string reporterTag)
        {
            var priority = BackgroundMode.Enter();
            try
            {
                var install = HearthstonePaths.InstallDir(_config);
                var powerPath = game?.PowerLog ?? HearthstonePaths.NewestPowerLog(install);
                var powerEnd = game?.PowerLog != null ? game.PowerLogEnd : -1;
                var bepinexPath = HearthstonePaths.BepInExLog(install);

                var power = powerPath != null && File.Exists(powerPath) ? SliceLastGame(powerPath, powerEnd) : null;
                var bepinex = bepinexPath != null && File.Exists(bepinexPath) ? OwnLines(ReadTail(bepinexPath, BepInExCap)) : null;
                var plugin = ReadPluginLog();
                var plugins = ListPlugins(install);
                var dance = DanceLog.Summarise(bepinex?.Text,
                    plugins?.Any(p => p.EndsWith("NomiCantDance.dll", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith("NomisKitchen.dll", StringComparison.OrdinalIgnoreCase)) == true, _config.FixMinionDance);

                var redactor = new Redactor();
                var battleTag = FindBattleTag(power, reporterTag);
                redactor.Keep(battleTag);
                int? build = game?.HsBuild;
                power?.ForEachLine(line =>
                {
                    redactor.Learn(line);
                    if (build == null) build = BuildNumber(line);
                });
                foreach (var text in new[] { bepinex?.Text, plugin?.Text })
                    ForEachLine(text, redactor.Learn);

                var manifest = new Dictionary<string, object>
                {
                    ["v"] = 1,
                    ["client"] = _config.Client ?? "hdt-" + _version,
                    ["battleTag"] = battleTag,
                    ["reason"] = reason,
                    ["note"] = string.IsNullOrWhiteSpace(note) ? null : Truncate(note.Trim(), 1000),
                    ["hsBuild"] = build,
                    ["turns"] = game?.Turns,
                    ["duos"] = game?.Duos,
                    ["gameStartedUtc"] = game?.StartedUtc?.ToString("o"),
                    ["gameEndedUtc"] = game?.EndedUtc?.ToString("o"),
                    ["sentUtc"] = DateTime.UtcNow.ToString("o"),
                    ["hdtVersion"] = _config.HdtVersion,
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["fixMinionDance"] = _config.FixMinionDance,
                        ["disableAbbreviation"] = _config.DisableAbbreviation,
                        ["showApmOverlay"] = _config.ShowApmOverlay,
                    },
                    ["bepinexPlugins"] = plugins,
                    ["danceFixes"] = dance.Loaded ? dance.Fixes : (int?)null,
                    ["danceFixOn"] = dance.FixOn,
                    ["minionDance"] = dance.ToManifest(),
                    ["powerLog"] = power?.Describe(true),
                    ["bepinexLog"] = bepinex?.Describe(),
                    ["pluginLog"] = plugin?.Describe(),
                    ["playersRedacted"] = redactor.NameCount,
                };

                using (var buffer = new MemoryStream())
                {
                    using (var zip = new ReportZip(buffer))
                    {
                        var present = manifest.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
                        WriteEntry(zip, "manifest.json", MiniJson.Write(present), null);
                        if (power != null) PowerLogEncoder.Write(zip, power.ForEachLine, redactor.CleanGameLine);
                        if (bepinex != null) WriteEntry(zip, "bepinex.log", bepinex.Text, redactor);
                        if (plugin != null) WriteEntry(zip, "plugin.log", plugin.Text, redactor);
                    }
                    if (buffer.Length > MaxZipBytes) throw new InvalidOperationException("the logs are too large to send (" + buffer.Length / (1024 * 1024) + " MB)");
                    return buffer.ToArray();
                }
            }
            finally
            {
                BackgroundMode.Exit(priority);
            }
        }

        static readonly Regex PlayerNameField = new Regex(@"PlayerName=(?<name>[^\r\n]+)", RegexOptions.CultureInvariant);

        static int? BuildNumber(string line)
        {
            int at = line.IndexOf("BuildNumber=", StringComparison.Ordinal);
            if (at < 0) return null;
            int value = 0, digits = 0;
            for (int i = at + 12; i < line.Length && digits < 9 && line[i] >= '0' && line[i] <= '9'; i++, digits++)
                value = value * 10 + (line[i] - '0');
            return digits > 0 ? value : (int?)null;
        }

        static string FindBattleTag(PowerSlice powerLog, string reporter)
        {
            if (string.IsNullOrWhiteSpace(reporter)) return null;
            if (reporter.IndexOf('#') > 0 || powerLog == null) return reporter;
            string match = null;
            powerLog.ForEachLine(line =>
            {
                if (match != null || line.IndexOf("PlayerName=", StringComparison.Ordinal) < 0) return;
                var name = PlayerNameField.Match(line).Groups["name"].Value.Trim();
                if (name.StartsWith(reporter + "#", StringComparison.Ordinal)) match = name;
            });
            return match ?? reporter;
        }

        static readonly Regex BepInExHeader = new Regex(@"^\[(?<level>[A-Za-z]+)\s*:\s*(?<source>[^\]]+)\]", RegexOptions.CultureInvariant);
        static readonly HashSet<string> OwnSources = new HashSet<string>(StringComparer.Ordinal)
        {
            "BepInEx", "Nomi Can't Dance", "Nomi's Kitchen APM Provider", "Nomi Hates Abbreviation", "Nomi's Kitchen",
        };

        static Captured OwnLines(Captured log)
        {
            var kept = new StringBuilder();
            bool keep = false;
            int dropped = 0;
            ForEachLine(log.Text, line =>
            {
                var header = BepInExHeader.Match(line);
                if (header.Success)
                {
                    var level = header.Groups["level"].Value;
                    keep = OwnSources.Contains(header.Groups["source"].Value.Trim()) || level == "Error" || level == "Fatal";
                }
                if (keep) kept.Append(line.Replace(" This mod modifies the Hearthstone client; Blizzard's terms do not allow client modification.", "")).Append('\n');
                else dropped++;
            });
            if (dropped > 0) kept.Append("[nomi: ").Append(dropped).Append(" lines from other mods left out]\n");
            return new Captured(kept.ToString(), log.OriginalBytes, log.Truncated, log.FoundGameStart);
        }

        static void WriteEntry(ReportZip zip, string name, string text, Redactor redactor)
        {
            using (var writer = new StreamWriter(zip.Create(name), new UTF8Encoding(false)))
            {
                if (redactor == null) { writer.Write(text); return; }
                ForEachLine(text, line => writer.WriteLine(redactor.Clean(line)));
            }
        }

        static void ForEachLine(string text, Action<string> action)
        {
            if (text == null) return;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null) action(line);
            }
        }

        static PowerSlice SliceLastGame(string path, long end)
        {
            using (var stream = OpenShared(path))
            {
                long stop = end > 0 && end <= stream.Length ? end : stream.Length;
                long marker = FindLast(stream, Encoding.ASCII.GetBytes(GameStartMarker), stop);
                bool found = marker >= 0;
                long start = found ? LineStart(stream, marker) : Math.Max(0, stop - PowerLogCap);
                long length = stop - start;
                if (length <= PowerLogCap)
                    return new PowerSlice(path, found, length, new PowerSlice.Range(start, length, false));
                var tailLength = PowerLogCap - PowerLogHead;
                return new PowerSlice(path, found, length,
                    new PowerSlice.Range(start, PowerLogHead, false),
                    new PowerSlice.Range(stop - tailLength, tailLength, true, "[nomi: " + (length - PowerLogCap) / 1024 + " KB of the middle of this game left out]"));
            }
        }

        static Captured ReadTail(string path, long cap)
        {
            using (var stream = OpenShared(path))
            {
                long length = stream.Length;
                if (length <= cap) return new Captured(ReadRange(stream, 0, length), length, false, true);
                return new Captured(DropPartialLine(ReadRange(stream, length - cap, cap)), length, true, true);
            }
        }

        static Captured ReadPluginLog()
        {
            var current = Log.FilePath;
            var old = current + ".old";
            var parts = new List<string>();
            long total = 0;
            if (File.Exists(old)) { var t = ReadTail(old, PluginLogCap / 2); parts.Add(t.Text); total += t.OriginalBytes; }
            if (File.Exists(current)) { var t = ReadTail(current, PluginLogCap / 2); parts.Add(t.Text); total += t.OriginalBytes; }
            return parts.Count == 0 ? null : new Captured(string.Join("", parts), total, total > PluginLogCap, true);
        }

        static List<string> ListPlugins(string install)
        {
            try
            {
                var dir = HearthstonePaths.BepInExPlugins(install);
                if (dir == null || !Directory.Exists(dir)) return null;
                return Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .Select(f => f.Substring(dir.Length).TrimStart('\\', '/'))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return null; }
        }

        static FileStream OpenShared(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        static long FindLast(FileStream stream, byte[] marker, long stop)
        {
            const int chunk = 1 << 20;
            var buffer = new byte[chunk + marker.Length];
            long found = -1;
            long position = 0;
            int carry = 0;
            stream.Position = 0;
            while (position < stop)
            {
                int want = (int)Math.Min(chunk, stop - position);
                int read = stream.Read(buffer, carry, want);
                if (read <= 0) break;
                int available = carry + read;
                for (int i = 0; i + marker.Length <= available; i++)
                {
                    if (buffer[i] != marker[0]) continue;
                    int k = 1;
                    while (k < marker.Length && buffer[i + k] == marker[k]) k++;
                    if (k == marker.Length) found = position - carry + i;
                }
                position += read;
                carry = Math.Min(marker.Length - 1, available);
                Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
            }
            return found;
        }

        static long LineStart(FileStream stream, long offset)
        {
            long from = Math.Max(0, offset - 512);
            var buffer = new byte[offset - from];
            stream.Position = from;
            int read = stream.Read(buffer, 0, buffer.Length);
            for (int i = read - 1; i >= 0; i--)
                if (buffer[i] == (byte)'\n') return from + i + 1;
            return from;
        }

        static string ReadRange(FileStream stream, long start, long length)
        {
            var bytes = new byte[length];
            stream.Position = start;
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read <= 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(bytes, 0, total);
        }

        static string DropPartialLine(string text)
        {
            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text.Substring(newline + 1);
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        async Task<Result> UploadAsync(byte[] zip)
        {
            var transport = _config.Transport;
            if (transport == null) return await UploadWithWebRequestAsync(zip).ConfigureAwait(false);
            ReportAnswer answer;
            try
            {
                answer = await transport(new ReportUpload
                {
                    Url = Endpoint,
                    Body = zip,
                    ContentType = "application/zip",
                    UserAgent = _userAgent,
                    TimeoutSeconds = UploadTimeoutMs / 1000,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Result.Failed("could not reach nomi.gg (" + ex.Message + ")", true);
            }
            if (answer == null || answer.Status == 0)
                return Result.Failed("could not reach nomi.gg (" + (answer?.Error ?? "no answer") + ")", true);
            var text = answer.Body ?? "";
            if (answer.Status >= 200 && answer.Status < 300)
            {
                var id = Regex.Match(text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                return id.Success ? Result.Sent(id.Groups[1].Value) : Result.Failed("unexpected answer from nomi.gg", true);
            }
            var message = Regex.Match(text, "\"error\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            bool retry = answer.Status == 429 || answer.Status >= 500;
            return Result.Failed("nomi.gg answered " + answer.Status + (message.Length > 0 ? ": " + message : ""), retry);
        }

        static async Task<Result> UploadWithWebRequestAsync(byte[] zip)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(Endpoint);
                request.Method = "POST";
                request.ContentType = "application/zip";
                request.UserAgent = _userAgent;
                request.Timeout = UploadTimeoutMs;
                request.ReadWriteTimeout = UploadTimeoutMs;
                request.ContentLength = zip.Length;
                using (var body = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await body.WriteAsync(zip, 0, zip.Length).ConfigureAwait(false);
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var text = reader.ReadToEnd();
                    var id = Regex.Match(text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    return id.Success ? Result.Sent(id.Groups[1].Value) : Result.Failed("unexpected answer from nomi.gg", true);
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse failed)
            {
                int status = (int)failed.StatusCode;
                string message;
                using (failed)
                using (var reader = new StreamReader(failed.GetResponseStream()))
                    message = Regex.Match(reader.ReadToEnd(), "\"error\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                bool retry = status == 429 || status >= 500;
                return Result.Failed("nomi.gg answered " + status + (message.Length > 0 ? ": " + message : ""), retry);
            }
            catch (Exception ex)
            {
                return Result.Failed("could not reach nomi.gg (" + ex.Message + ")", true);
            }
        }

        void KeepPending(string reason, byte[] zip)
        {
            try
            {
                Directory.CreateDirectory(PendingDir);
                File.WriteAllBytes(Path.Combine(PendingDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + reason + ".zip"), zip);
                foreach (var extra in new DirectoryInfo(PendingDir).GetFiles("*.zip").OrderByDescending(f => f.Name).Skip(MaxPending))
                    extra.Delete();
            }
            catch (Exception ex) { Log.Error("Log report: could not keep the report for later", ex); }
        }

        async Task SendPendingAsync()
        {
            try
            {
                if (_stopped || !Directory.Exists(PendingDir)) return;
                if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;
                try
                {
                    foreach (var file in new DirectoryInfo(PendingDir).GetFiles("*.zip").OrderBy(f => f.Name))
                    {
                        if (file.Name.EndsWith("-auto.zip", StringComparison.Ordinal) && !_config.ShareGameLogs)
                        {
                            file.Delete();
                            continue;
                        }
                        var result = await UploadAsync(File.ReadAllBytes(file.FullName)).ConfigureAwait(false);
                        if (result.Ok) Log.Info("Log report sent from the queue: id " + result.Id);
                        else Log.Warn("Log report from the queue not sent: " + result.Error);
                        if (result.Ok || !result.Retry) file.Delete();
                        else break;
                    }
                }
                finally { Interlocked.Exchange(ref _sending, 0); }
            }
            catch (Exception ex) { Log.Error("Log report: queue failed", ex); }
        }

        sealed class GameInfo
        {
            public int? Turns;
            public int? HsBuild;
            public bool? Duos;
            public DateTime? StartedUtc;
            public DateTime? EndedUtc;
            public string PowerLog;
            public long PowerLogEnd;
            public string PlayerName;
        }

        sealed class PowerSlice
        {
            public sealed class Range
            {
                public readonly long Start;
                public readonly long Length;
                public readonly bool SkipFirstLine;
                public readonly string Before;

                public Range(long start, long length, bool skipFirstLine, string before = null)
                {
                    Start = start;
                    Length = length;
                    SkipFirstLine = skipFirstLine;
                    Before = before;
                }
            }

            readonly string _path;
            readonly Range[] _ranges;
            public readonly bool FoundGameStart;
            public readonly long OriginalBytes;

            public PowerSlice(string path, bool foundGameStart, long originalBytes, params Range[] ranges)
            {
                _path = path;
                FoundGameStart = foundGameStart;
                OriginalBytes = originalBytes;
                _ranges = ranges;
            }

            public bool Truncated => _ranges.Length > 1;

            public void ForEachLine(Action<string> action)
            {
                using (var stream = OpenShared(_path))
                {
                    foreach (var range in _ranges)
                    {
                        if (range.Before != null) action(range.Before);
                        stream.Position = range.Start;
                        using (var reader = new StreamReader(new Bounded(stream, range.Length), Encoding.UTF8, false, 1 << 16, true))
                        {
                            if (range.SkipFirstLine) reader.ReadLine();
                            string line;
                            while ((line = reader.ReadLine()) != null) action(line);
                        }
                    }
                }
            }

            public object Describe(bool withGameStart = false)
            {
                var described = new Dictionary<string, object> { ["bytes"] = OriginalBytes, ["truncated"] = Truncated, ["encoding"] = PowerLogEncoder.Encoding };
                if (withGameStart) described["gameStartFound"] = FoundGameStart;
                return described;
            }
        }

        sealed class Bounded : Stream
        {
            readonly Stream _inner;
            long _remaining;

            public Bounded(Stream inner, long length)
            {
                _inner = inner;
                _remaining = length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= read;
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        sealed class Captured
        {
            public readonly string Text;
            public readonly long OriginalBytes;
            public readonly bool Truncated;
            public readonly bool FoundGameStart;

            public Captured(string text, long originalBytes, bool truncated, bool foundGameStart)
            {
                Text = text;
                OriginalBytes = originalBytes;
                Truncated = truncated;
                FoundGameStart = foundGameStart;
            }

            public object Describe(bool withGameStart = false)
            {
                var described = new Dictionary<string, object> { ["bytes"] = OriginalBytes, ["truncated"] = Truncated };
                if (withGameStart) described["gameStartFound"] = FoundGameStart;
                return described;
            }
        }

        internal sealed class Result
        {
            public bool Ok { get; private set; }
            public string Id { get; private set; }
            public string Error { get; private set; }
            public bool Retry { get; private set; }

            public static Result Sent(string id) => new Result { Ok = true, Id = id };
            public static Result Failed(string error, bool retry = false) => new Result { Error = error, Retry = retry };
        }
    }
}
