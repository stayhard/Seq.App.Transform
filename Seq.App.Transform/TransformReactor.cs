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
        
        private static double? ToDouble(object v)
        {
            if (v is NumberInstance)
            {
                return ((NumberInstance)v).Value;
            }
            else if (v is double)
            {
                return (double)v;
            }
            else if (v is decimal)
            {
                return (double)(decimal)v;
            }
            else if (v is long)
            {
                return (long)v;
            }
            return null;
        }

        private void Transform()
        {
            try
            {
                List<LogEventData> window;

                if (WindowSeconds > 0)
                {
                    window = _window.Where(r => r.LocalTimestamp >= DateTime.Now.AddSeconds(-WindowSeconds)).ToList();
                }
                else
                {
                    window = new List<LogEventData>();
                }

                if (WindowSeconds <= 0 && _window.Any())
                {
                    window.Add(_window.Last());
                }

                var engine = new ScriptEngine();
                engine.EnableExposedClrTypes = true;

                var properties = new Dictionary<string, ArrayInstance>
                {
                    { "$Id", engine.Array.Construct() },
                    { "$Level", engine.Array.Construct() },
                    { "$Timestamp", engine.Array.Construct() },
                    { "$Message", engine.Array.Construct() },
                };
                foreach (var e in window)
                {
                    properties["$Id"].Push(e.Id);
                    properties["$Level"].Push(e.Level);
                    properties["$Timestamp"].Push(e.LocalTimestamp);
                    properties["$Message"].Push(e.RenderedMessage);

                    foreach (var p in e.Properties)
                    {
                        if (!properties.ContainsKey(p.Key))
                        {
                            properties.Add(p.Key, engine.Array.Construct());
                        }
                        properties[p.Key].Push(p.Value);
                    }
                }
                
                engine.SetGlobalValue("all", new Aggregator(engine, properties, r => r));
                engine.SetGlobalValue("first", new Aggregator(engine, properties, r => r.ElementValues.FirstOrDefault()));
                engine.SetGlobalValue("last", new Aggregator(engine, properties, r => r.ElementValues.LastOrDefault()));
                engine.SetGlobalValue("max", new Aggregator(engine, properties, r =>
                {
                    double? result = null;
                    foreach (var v in r.Properties.Select(p => p.Value))
                    {
                        var d = ToDouble(v);
                        if (d != null && result == null || result < d)
                        {
                            result = d;
                        }
                    }
                    return result;
                }));

                engine.SetGlobalValue("min", new Aggregator(engine, properties, r =>
                {
                    double? result = null;
                    foreach (var v in r.Properties.Select(p => p.Value))
                    {
                        var d = ToDouble(v);
                        if (d != null && result == null || d < result)
                        {
                            result = d;
                        }
                    }
                    return result;
                }));

                engine.SetGlobalValue("mean", new Aggregator(engine, properties, r =>
                {
                    double sum = 0;
                    int count = 0;
                    foreach (var v in r.Properties.Select(p => p.Value))
                    {
                        var d = ToDouble(v);
                        if (d != null)
                        {
                            sum += (double)d;
                            count += 1;
                        }
                    }

                    return sum / count;
                }));

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

        private class Aggregator : ObjectInstance
        {
            private readonly IDictionary<string, ArrayInstance> _properties;
            private readonly Func<ArrayInstance, object> _func;

            public Aggregator(ScriptEngine engine, IDictionary<string, ArrayInstance> properties, Func<ArrayInstance, object> func) : base(engine)
            {
                _properties = properties;
                _func = func;
            }

            protected override object GetMissingPropertyValue(string propertyName)
            {
                if (!_properties.ContainsKey(propertyName))
                {
                    _properties.Add(propertyName, Engine.Array.Construct());
                }
                return _func(_properties[propertyName]);
            }
        }

        private static object ToClrType(object v)
        {
            if (v is ArrayInstance)
            {
                return ((ArrayInstance)v).ElementValues.Select(ToClrType).ToList();
            }
            if (v is ClrInstanceWrapper)
            {
                return ((ClrInstanceWrapper)v).WrappedInstance;
            }
            if (v is ObjectInstance)
            {
                return ((ObjectInstance)v).Properties.ToDictionary(r => r.Name, r => ToClrType(r.Value));
            }
            return v;
        }

        private ILogger GetLoggerFor(object properties)
        {
            var l = Log;
            if (properties != null && properties is ObjectInstance)
            {
                foreach (var prop in ((ObjectInstance)properties).Properties)
                {
                    l = l.ForContext(prop.Name, ToClrType(prop.Value), true);
                }
            }
            return l;
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
            lock (this)
            {
                if (WindowSeconds > 0)
                {
                    _window.Enqueue(evt.Data);

                    while (_window.Count > 0 && _window.Peek().LocalTimestamp < DateTime.Now.AddSeconds(-WindowSeconds))
                    {
                        _window.Dequeue();
                    }
                }
                else
                {
                    _window.Clear();
                    _window.Enqueue(evt.Data);
                }

                if (IntervalSeconds <= 0)
                {
                    Transform();
                }
            }
        }
        
    }
}
