using Noesis.Javascript;
using Seq.Apps;
using Seq.Apps.LogEvents;
using Serilog;
using System;
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

                var window = _window.ToList();

                using (var context = new JavascriptContext())
                {
                    context.SetParameter("aggregate", new Aggregate(window, WindowSeconds));
                    if (_current?.Properties != null)
                    {
                        foreach (var prop in _current.Properties)
                        {
                            context.SetParameter(prop.Key, prop.Value);
                        }
                    }
                    context.SetParameter("eventId", _current?.Id);
                    context.SetParameter("eventLevel", _current?.Level);
                    context.SetParameter("eventTimestamp", _current?.LocalTimestamp);
                    context.SetParameter("eventMessage", _current?.RenderedMessage);


                    context.SetParameter("__$log", new JsLog(Log));

                    context.Run(@"
function logTrace(msg, properties) { __$log.Verbose(msg, properties); }
function logVerbose(msg, properties) { __$log.Verbose(msg, properties); }
function logDebug(msg, properties) { __$log.Debug(msg, properties); }
function logInfo(msg, properties) { __$log.Information(msg, properties); }
function logInformation(msg, properties) { __$log.Information(msg, properties); }
function logWarn(msg, properties) { __$log.Warning(msg, properties); }
function logWarning(msg, properties) { __$log.Warning(msg, properties); }
function logError(msg, properties) { __$log.Error(msg, properties); }
function logFatal(msg, properties) { __$log.Fatal(msg, properties); }
" + Script);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to transform event");
            }
        }

        private class JsLog
        {
            private readonly ILogger _logger;

            public JsLog(ILogger logger)
            {
                _logger = logger;
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
        }

        private class Aggregate
        {
            private readonly IList<LogEventData> _data;
            private readonly int _windowSeconds;

            public Aggregate(IList<LogEventData> data, int windowSeconds)
            {
                _data = data;
                _windowSeconds = windowSeconds;
            }

            public decimal length
            {
                get { return _data.Count(); }
            }

            private IEnumerable<decimal> SelectDecimal(string property)
            {
                return _data.Where(r => r.LocalTimestamp > DateTime.Now.AddSeconds(-_windowSeconds)).Select(r => r.Properties.ContainsKey(property) ? Convert.ToDecimal(r.Properties[property]) : 0);
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
