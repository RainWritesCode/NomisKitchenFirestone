using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using NomisKitchen.Reports;
using UnityEngine;

namespace NomisKitchen
{
    public sealed class ApmSampler : MonoBehaviour
    {
        static readonly BindingFlags IF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Action GameStarted;
        internal static Action<GameSnapshot, string> GameEnded;

        Type _gtType;
        readonly Dictionary<string, object> _gtCache = new Dictionary<string, object>();
        readonly object[] _tagArgs = new object[1];

        int _actions;
        int _prevGold, _prevHand, _prevBoard;
        int _lastTurn = -1;
        long _turnStartMs;
        double _peak, _avg;
        int _head, _tail;
        readonly long[] _times = new long[128];
        readonly int[] _counts = new int[128];
        double _gamePeak;
        int _gamePeakTurn;
        long _lastSampleMs;
        bool _turnSampled;

        float _sampleAccum;
        bool _inGame;
        bool _battlegrounds;
        bool _spectator;
        bool _duos;
        int _snapshotTurn;
        string _battleTag;
        readonly PerfProbe _perf = new PerfProbe();
        readonly System.Diagnostics.Stopwatch _sampleWatch = new System.Diagnostics.Stopwatch();

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (_inGame) _perf.Frame(dt);
            _sampleAccum += dt;
            if (_sampleAccum < 0.25f) return;
            _sampleAccum = 0f;
            _sampleWatch.Restart();
            try { Sample(); } catch { }
            WebUpload.Pump();
            _sampleWatch.Stop();
            if (_inGame) _perf.Sample(_sampleWatch.Elapsed.TotalMilliseconds);
        }

        void Sample()
        {
            object gs = CallStaticGet("GameState");
            if (gs == null) { EndGame(); return; }
            object ge = Invoke(gs, "GetGameEntity");
            bool combat = false;
            int turn = 0;
            if (ge != null)
            {
                object inCombat = Invoke(ge, "IsInBattlegroundsCombatPhase");
                combat = inCombat is bool b && b;
                int rawTurn = GetInt(ge, "m_realTimeTurn");
                if (rawTurn == 0) rawTurn = TagOf(ge, "TURN");
                turn = rawTurn > 0 ? (rawTurn + 1) / 2 : 0;
            }
            int friendlyId = 0;
            try { friendlyId = Convert.ToInt32(Invoke(gs, "GetFriendlyPlayerId")); } catch { }
            if (friendlyId == 0) { EndGame(); return; }
            if (!_inGame) BeginGame();
            if (turn > _snapshotTurn) _snapshotTurn = turn;
            _perf.Phase(turn, combat);

            if (turn <= 0) return;
            if (combat) { LogTurn(); return; }

            object friendly = Invoke(gs, "GetFriendlySidePlayer");
            if (friendly == null) return;
            int gold = TagOf(friendly, "RESOURCES") - TagOf(friendly, "RESOURCES_USED");
            int hand = CardCount(Invoke(friendly, "GetHandZone"));
            int board = CardCount(Invoke(friendly, "GetBattlefieldZone")) + HeldBoardMinion(friendlyId);
            _perf.Entities = CardCount(Invoke(gs, "GetEntityMap"), "get_Count");

            if (turn != _lastTurn)
            {
                LogTurn();
                ResetTurn(turn);
                _prevGold = gold; _prevHand = hand; _prevBoard = board;
                return;
            }

            _actions += ActionsBetween(_prevGold, _prevHand, _prevBoard, gold, hand, board);
            _prevGold = gold; _prevHand = hand; _prevBoard = board;
            long now = Environment.TickCount;
            _times[_tail] = now;
            _counts[_tail] = _actions;
            _tail = (_tail + 1) % 128;
            if (_tail == _head) _head = (_head + 1) % 128;
            long windowStart = now - 4000;
            while (((_head + 1) % 128) != _tail && _times[_head] < windowStart) _head = (_head + 1) % 128;
            double span = (now - _times[_head]) / 1000.0;
            double current = span >= 0.5 ? Math.Max(0, 60.0 * (_actions - _counts[_head]) / span) : 0;
            if (current > _peak) _peak = current;
            if (current > _gamePeak) { _gamePeak = current; _gamePeakTurn = turn; }
            _lastSampleMs = now;
            _turnSampled = true;
            double seconds = (now - _turnStartMs) / 1000.0;
            if (seconds > 0.6) _avg = 60.0 * _actions / seconds;
        }

        void BeginGame()
        {
            _inGame = true;
            _snapshotTurn = 0;
            try
            {
                var mgr = GameMgr.Get();
                _battlegrounds = mgr != null && mgr.IsBattlegrounds();
                _spectator = mgr != null && mgr.IsSpectator();
                _duos = mgr != null && mgr.IsBattlegroundDuoGame();
            }
            catch
            {
                _battlegrounds = false;
            }
            try { _battleTag = BnetPresenceMgr.Get()?.GetMyPlayer()?.GetBattleTag()?.ToString(); } catch { _battleTag = null; }
            if (_battlegrounds && !_spectator) GameStarted?.Invoke();
        }

        void EndGame()
        {
            if (!_inGame) return;
            LogTurn();
            Plugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture, "apm game peak {0:F2} on turn {1}", _gamePeak, _gamePeakTurn));
            _perf.GameOver();
            _inGame = false;
            _lastTurn = -1;
            _actions = 0;
            _peak = 0; _avg = 0;
            _gamePeak = 0; _gamePeakTurn = 0;
            if (!_battlegrounds || _spectator) return;
            try
            {
                GameEnded?.Invoke(new GameSnapshot { Turns = _snapshotTurn > 0 ? _snapshotTurn : (int?)null, Duos = _duos }, _battleTag);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Game sharing failed at the end of the game: " + e.Message);
            }
        }

        void LogTurn()
        {
            if (_lastTurn <= 0 || !_turnSampled) return;
            double seconds = (_lastSampleMs - _turnStartMs) / 1000.0;
            Plugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "apm turn {0}: {1} actions in {2:F2} s, average {3:F2}, peak {4:F2}", _lastTurn, _actions, seconds, _avg, _peak));
            _turnSampled = false;
        }

        void ResetTurn(int turn)
        {
            _turnSampled = false;
            _lastTurn = turn;
            _actions = 0;
            _turnStartMs = Environment.TickCount;
            _peak = 0; _avg = 0;
            _head = 0;
            _times[0] = _turnStartMs;
            _counts[0] = 0;
            _tail = 1;
        }

        internal static int ActionsBetween(int prevGold, int prevHand, int prevBoard, int gold, int hand, int board)
        {
            int actions = 0;
            if (gold != prevGold) actions++;
            if (board > prevBoard) actions += board - prevBoard;
            if (hand < prevHand && board <= prevBoard && gold == prevGold) actions++;
            if (hand > prevHand && gold == prevGold && board == prevBoard) actions++;
            return actions;
        }

        object GTag(string name)
        {
            if (_gtCache.TryGetValue(name, out var cached)) return cached;
            if (_gtType == null) _gtType = FindType("GAME_TAG");
            object value = null;
            if (_gtType != null) { try { value = Convert.ToInt32(Enum.Parse(_gtType, name)); } catch { } }
            _gtCache[name] = value;
            return value;
        }

        int TagOf(object entity, string tag)
        {
            object tagId = GTag(tag);
            if (tagId == null) return 0;
            _tagArgs[0] = tagId;
            try
            {
                object result = Invoke(entity, "GetTag", _tagArgs);
                if (result is int n) return n;
                if (result != null) return Convert.ToInt32(result);
            }
            catch { }
            return 0;
        }

        int HeldBoardMinion(int friendlyId)
        {
            object held = Invoke(CallStaticGet("InputManager"), "GetHeldCard");
            object entity = held != null ? Invoke(held, "GetEntity") : null;
            if (entity == null) return 0;
            return TagOf(entity, "ZONE") == 1 && TagOf(entity, "CONTROLLER") == friendlyId && TagOf(entity, "CARDTYPE") == 4 ? 1 : 0;
        }

        static int CardCount(object source, string counter = "GetCardCount")
        {
            object n = Invoke(source, counter);
            return n is int count ? count : 0;
        }

        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        static readonly Dictionary<Type, Func<object>> Instances = new Dictionary<Type, Func<object>>();
        static readonly Dictionary<Type, Dictionary<string, MethodInfo>> Methods = new Dictionary<Type, Dictionary<string, MethodInfo>>();
        static readonly Dictionary<Type, Dictionary<string, FieldInfo>> Fields = new Dictionary<Type, Dictionary<string, FieldInfo>>();

        static Type FindType(string name)
        {
            if (Types.TryGetValue(name, out var cached) && cached != null) return cached;
            var type = Type.GetType(name);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { type = assembly.GetType(name); if (type != null) break; } catch { }
                }
            }
            Types[name] = type;
            return type;
        }

        static object CallStaticGet(string typeName)
        {
            var type = FindType(typeName);
            if (type == null) return null;
            if (!Instances.TryGetValue(type, out var get))
            {
                var method = type.GetMethod("Get", SF, null, Type.EmptyTypes, null);
                var field = method == null ? type.GetField("s_instance", SF) : null;
                if (method != null) get = () => method.Invoke(null, null);
                else if (field != null) get = () => field.GetValue(null);
                Instances[type] = get;
            }
            if (get == null) return null;
            try { return get(); } catch { return null; }
        }

        static object Invoke(object target, string method, params object[] args)
        {
            if (target == null) return null;
            var runtimeType = target.GetType();
            if (!Methods.TryGetValue(runtimeType, out var byName))
            {
                byName = new Dictionary<string, MethodInfo>();
                Methods[runtimeType] = byName;
            }
            if (!byName.TryGetValue(method, out var found))
            {
                var types = new Type[args.Length];
                for (int i = 0; i < args.Length; i++) types[i] = args[i] != null ? args[i].GetType() : typeof(object);
                for (Type t = runtimeType; t != null && found == null; t = t.BaseType)
                {
                    try { found = t.GetMethod(method, IF, null, types, null); } catch { }
                    if (found == null) { try { found = t.GetMethod(method, IF); } catch { } }
                }
                byName[method] = found;
            }
            if (found == null) return null;
            try { return found.Invoke(target, args); } catch { return null; }
        }

        static object Field(object target, string name)
        {
            if (target == null) return null;
            var runtimeType = target.GetType();
            if (!Fields.TryGetValue(runtimeType, out var byName))
            {
                byName = new Dictionary<string, FieldInfo>();
                Fields[runtimeType] = byName;
            }
            if (!byName.TryGetValue(name, out var found))
            {
                for (Type t = runtimeType; t != null && found == null; t = t.BaseType) found = t.GetField(name, IF);
                byName[name] = found;
            }
            if (found == null) return null;
            try { return found.GetValue(target); } catch { return null; }
        }

        static int GetInt(object target, string name)
        {
            object value = Field(target, name);
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    sealed class PerfProbe
    {
        const float HitchMs = 250f;
        const int MaxHitchLines = 30;

        int _turn = -1;
        bool _combat;
        int _frames;
        double _totalMs;
        float _maxMs;
        int _over50;
        int _over100;
        int _gcAtStart;
        int _samples;
        double _sampleTotalMs;
        double _sampleMaxMs;
        int _hitchLines;
        long _managedAtStart;

        public int Entities;

        public void Frame(float seconds)
        {
            if (_turn < 0) return;
            float ms = seconds * 1000f;
            _frames++;
            _totalMs += ms;
            if (ms > _maxMs) _maxMs = ms;
            if (ms > 50f) _over50++;
            if (ms > 100f) _over100++;
            if (ms > HitchMs && _hitchLines < MaxHitchLines)
            {
                _hitchLines++;
                Plugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "perf hitch {0:F0} ms on turn {1} {2} (entities {3})", ms, _turn, _combat ? "combat" : "shop", Entities));
            }
        }

        public void Sample(double ms)
        {
            if (_turn < 0) return;
            _samples++;
            _sampleTotalMs += ms;
            if (ms > _sampleMaxMs) _sampleMaxMs = ms;
        }

        public void Phase(int turn, bool combat)
        {
            if (turn == _turn && combat == _combat) return;
            if (_turn < 0) Plugin.Log?.LogInfo("perf game start");
            Flush();
            _turn = turn;
            _combat = combat;
            Begin();
        }

        public void GameOver()
        {
            if (_turn < 0) return;
            Flush();
            Plugin.Log?.LogInfo("perf game over");
            _turn = -1;
            _hitchLines = 0;
        }

        void Begin()
        {
            _frames = 0; _totalMs = 0; _maxMs = 0; _over50 = 0; _over100 = 0;
            _samples = 0; _sampleTotalMs = 0; _sampleMaxMs = 0;
            _gcAtStart = GC.CollectionCount(0);
            _managedAtStart = GC.GetTotalMemory(false);
        }

        void Flush()
        {
            if (_turn < 0 || _frames == 0) return;
            Plugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "perf turn {0} {1}: {2} frames in {3:F1} s, avg {4:F1} ms, max {5:F0} ms, {6} over 50 ms, {7} over 100 ms, " +
                "{8} GCs, heap {9:F0} MB ({10:+0;-0} MB), apm sampler avg {11:F2} ms max {12:F1} ms over {13} runs, entities {14}",
                _turn, _combat ? "combat" : "shop", _frames, _totalMs / 1000.0, _totalMs / _frames, _maxMs, _over50, _over100,
                GC.CollectionCount(0) - _gcAtStart, GC.GetTotalMemory(false) / 1048576.0,
                (GC.GetTotalMemory(false) - _managedAtStart) / 1048576.0,
                _samples == 0 ? 0 : _sampleTotalMs / _samples, _sampleMaxMs, _samples, Entities));
        }
    }
}
