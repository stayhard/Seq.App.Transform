using Jurassic;
using Jurassic.Library;
using Seq.Apps;
using Seq.Apps.LogEvents;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace Seq.App.Transform
{
    [SeqApp("Transform",
        Description = "Collects events and allows a javascript to transform them into different events written back to the log.")]
    public class TransformReactor : Reactor, ISubscribeTo<LogEventData>
    {
        private Queue<LogEventData> _window;
        private LogEventData _current;
        private Timer _timer;
        private ConcurrentDictionary<string, bool> _incidents = new ConcurrentDictionary<string, bool>();

        [SeqAppSetting(
            DisplayName = "Aggregation - Window (seconds)",
            HelpText = "The number of seconds within which the events will be collected and sent to transform script. Set to 0 to only collect the last event.")]
        public int WindowSeconds { get; set; }

        [SeqAppSetting(
            DisplayName = "Interval (seconds)",
            IsOptional = true,
            HelpText = "How often the script will run. Set to 0 to run on each received event.")]
        public int IntervalSeconds { get; set; }

        [SeqAppSetting(
            DisplayName = "Script (Javascript)",
            IsOptional = false,
            InputType = SettingInputType.LongText,
            HelpText = "The script for transforming the events.")]
        public string Script { get; set; }

        protected override void OnAttached()
        {
            base.OnAttached();

            _window = new Queue<LogEventData>();

            if (IntervalSeconds > 0)
            {
                _timer = new Timer();
                _timer.Interval = IntervalSeconds * 1000;
                _timer.Elapsed += (s, e) =>
                {
                    lock (this)
                    {
                        Transform();
                    }
                };
                _timer.Start();
            }

        }
        
        private void Transform()
        {
            try
            {

                var window = _window.Where(r => r.LocalTimestamp >= DateTime.Now.AddSeconds(-WindowSeconds)).ToList();

                var engine = new Jurassic.ScriptEngine();

                engine.SetGlobalValue("aggregate", new Aggregate(engine, window));
                if (_current?.Properties != null)
                {
                    foreach (var prop in _current.Properties)
                    {
                        engine.SetGlobalValue(prop.Key, prop.Value);
                    }
                }

                if (_current != null)
                {
                    engine.SetGlobalValue("eventId", _current.Id);
                    engine.SetGlobalValue("eventLevel", _current.Level);
                
                    engine.SetGlobalValue("eventTimestamp",
                        engine.Date.Construct(
                            _current.LocalTimestamp.Year,
                            _current.LocalTimestamp.Month,
                            _current.LocalTimestamp.Day,
                            _current.LocalTimestamp.Hour,
                            _current.LocalTimestamp.Minute,
                            _current.LocalTimestamp.Second,
                            _current.LocalTimestamp.Millisecond));
                    engine.SetGlobalValue("eventMessage", _current.RenderedMessage);
                }

                var verbose = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Verbose(a.Value));
                var debug = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Debug(a.Value));
                var information = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Information(a.Value));
                var warning = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Warning(a.Value));
                var error = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Error(a.Value));
                var fatal = new Action<StringInstance, object>((a, b) => GetLoggerFor(b).Fatal(a.Value));

                engine.SetGlobalFunction("logTrace", verbose);
                engine.SetGlobalFunction("logVerbose", verbose);
                engine.SetGlobalFunction("logDebug", debug);
                engine.SetGlobalFunction("logInfo", information);
                engine.SetGlobalFunction("logInformation", information);
                engine.SetGlobalFunction("logWarn", warning);
                engine.SetGlobalFunction("logWarning", warning);
                engine.SetGlobalFunction("logError", error);
                engine.SetGlobalFunction("logFatal", fatal);

                engine.SetGlobalFunction("openIncident", new Action<StringInstance>(name =>
                {
                    if (_incidents.TryAdd(name.Value, true) || _incidents.TryUpdate(name.Value, true, false))
                    {
                        Log.ForContext("IncidentState", "Open").Error("[ Incident Open ] {IncidentName}", name);
                    }
                }));
                engine.SetGlobalFunction("closeIncident", new Action<StringInstance>(name =>
                {
                    if (_incidents.TryAdd(name.Value, false) || _incidents.TryUpdate(name.Value, false, true))
                    {
                        Log.ForContext("IncidentState", "Closed").Information("[ Incident Closed ] {IncidentName}", name);
                    }
                }));

                engine.Evaluate(Script);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to transform event");
            }
        }

        private ILogger GetLoggerFor(object properties)
        {
            var l = Log;
            if (properties != null && properties is ObjectInstance)
            {
                foreach (var prop in ((ObjectInstance)properties).Properties)
                {
                    l = l.ForContext(prop.Name, prop.Value, true);
                }
            }
            return l;
        }

        private class JsLog
        {
            private readonly ILogger _logger;
            private readonly IDictionary<string, bool> _incidents;

            public JsLog(ILogger logger, IDictionary<string, bool> incidents)
            {
                _logger = logger;
                _incidents = incidents;
            }

            private ILogger GetLoggerFor(IDictionary<string, object> properties)
            {
                var l = _logger;
                if (properties != null)
                {
                    foreach (var prop in properties)
                    {
                        l = l.ForContext(prop.Key, prop.Value, true);
                    }
                }
                return l;
            }

            public void Verbose(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Verbose(message);
            }

            public void Debug(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Debug(message);
            }

            public void Information(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Information(message);
            }

            public void Warning(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Warning(message);
            }

            public void Error(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Error(message);
            }

            public void Fatal(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Fatal(message);
            }

            public void OpenIncident(string name)
            {
                //var current = _incidents.GetOrAdd(name, false);

                //if (!current)
                //{
                //    _incidents.TryUpdate(name, true, true);
                //}

                //_logger.ForContext("IncidentState", "Open").Error("[ Incident Open ] {IncidentName}", name);
            }
            public void CloseIncident(string name)
            {
                //var current = _incidents.GetOrAdd(name, false);

                //if (current)
                //{
                //    _incidents.TryUpdate(name, false, false);
                //    _logger.ForContext("IncidentState", "Closed").Information("[ Incident Closed ] {IncidentName}", name);
                //}
            }
        }

        private class Aggregate : ObjectInstance
        {
            private readonly IList<LogEventData> _data;

            public Aggregate(ScriptEngine engine, IList<LogEventData> data)
                : base(engine)
            {
                PopulateFunctions();
                _data = data;
            }

            public decimal length
            {
                get { return _data.Count(); }
            }

            private IEnumerable<decimal> SelectDecimal(string property)
            {
                return _data.Select(r => r.Properties.ContainsKey(property) ? Convert.ToDecimal(r.Properties[property]) : 0);
            }

            public decimal sum(string property)
            {
                return SelectDecimal(property).Sum();
            }

            public decimal max(string property)
            {
                return SelectDecimal(property).Max();
            }

            public decimal min(string property)
            {
                return SelectDecimal(property).Min();
            }

            public decimal avg(string property)
            {
                return SelectDecimal(property).Average();
            }
        }

        public void On(Event<LogEventData> evt)
        {
            if (WindowSeconds > 0)
            {
                _window.Enqueue(evt.Data);

                while (_window.Count > 0 && _window.Peek().LocalTimestamp < DateTime.Now.AddSeconds(-WindowSeconds))
                {
                    _window.Dequeue();
                }
            }
            
            _current = evt.Data;

            if (IntervalSeconds <= 0)
            {
                Transform();
            }
        }
        
    }
}
