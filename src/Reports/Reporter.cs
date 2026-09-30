using System;
using System.Threading.Tasks;

namespace NomisKitchen.Reports
{
    public sealed class ReportSettings
    {
        public Version Version { get; set; }
        public Func<bool> ShareGameLogs { get; set; }
        public Func<string> HearthstoneDir { get; set; }
        public Func<bool> FixMinionDance { get; set; }
        public Func<bool> DisableAbbreviation { get; set; }
        public Func<bool> ShowApmOverlay { get; set; }
        public string PluginLogPath { get; set; }
        public string Client { get; set; }
        public string HdtVersion { get; set; }
        public string UserAgent { get; set; }
        public string PendingDir { get; set; }
        public Func<string> DetectedHearthstoneDir { get; set; }
        public Func<GameSnapshot> EndedGame { get; set; }
        public Func<string> BattleTag { get; set; }
        public Action<string> Info { get; set; }
        public Action<string> Warn { get; set; }
        public Action<string, Exception> Error { get; set; }
        public Func<ReportUpload, Task<ReportAnswer>> Transport { get; set; }
    }

    public sealed class ReportUpload
    {
        public string Url { get; set; }
        public byte[] Body { get; set; }
        public string ContentType { get; set; }
        public string UserAgent { get; set; }
        public int TimeoutSeconds { get; set; }
    }

    public sealed class ReportAnswer
    {
        public int Status { get; set; }
        public string Body { get; set; }
        public string Error { get; set; }
    }

    public sealed class GameSnapshot
    {
        public int? Turns { get; set; }
        public int? HsBuild { get; set; }
        public bool? Duos { get; set; }
    }

    public sealed class Reporter
    {
        readonly LogReporter _inner;

        public Reporter(ReportSettings settings)
        {
            Log.Bind(settings);
            _inner = new LogReporter(new ReporterConfig(settings), settings.Version ?? new Version(0, 0));
        }

        public void Start() => _inner.Start();

        public void Stop() => _inner.Stop();

        public void OnGameStart() => _inner.OnGameStart();

        public void OnGameEnd() => _inner.OnGameEnd();
    }

    internal sealed class ReporterConfig
    {
        readonly ReportSettings _settings;

        public ReporterConfig(ReportSettings settings)
        {
            _settings = settings;
        }

        public bool ShareGameLogs => _settings.ShareGameLogs?.Invoke() ?? false;
        public string HearthstoneDir => _settings.HearthstoneDir?.Invoke() ?? "";
        public bool FixMinionDance => _settings.FixMinionDance?.Invoke() ?? false;
        public bool DisableAbbreviation => _settings.DisableAbbreviation?.Invoke() ?? false;
        public bool ShowApmOverlay => _settings.ShowApmOverlay?.Invoke() ?? false;
        public string Client => _settings.Client;
        public string HdtVersion => _settings.HdtVersion;
        public string UserAgent => _settings.UserAgent;
        public string PendingDir => _settings.PendingDir;
        public string DetectedHearthstoneDir => Safe(_settings.DetectedHearthstoneDir);
        public GameSnapshot EndedGame => _settings.EndedGame?.Invoke();
        public string BattleTag => Safe(_settings.BattleTag);
        public Func<ReportUpload, Task<ReportAnswer>> Transport => _settings.Transport;

        static string Safe(Func<string> read)
        {
            try { return read?.Invoke(); }
            catch { return null; }
        }
    }

    internal static class Log
    {
        static ReportSettings _settings;

        internal static void Bind(ReportSettings settings) => _settings = settings;

        internal static string FilePath => _settings?.PluginLogPath ?? "";

        internal static void Info(string message) => _settings?.Info?.Invoke(message);

        internal static void Warn(string message) => _settings?.Warn?.Invoke(message);

        internal static void Error(string message, Exception ex = null) => _settings?.Error?.Invoke(message, ex);
    }
}
