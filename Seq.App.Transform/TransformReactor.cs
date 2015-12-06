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
            DisplayName = "Aggregation - Interval (seconds)",
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
                _timer.Elapsed += (s, e) => Transform();
                _timer.Start();
            }

        }
        
        private void Transform()
        {
            lock (_window)
            {
                if (WindowSeconds > 0)
                {
                    while (_window.Count > 0 && _window.Peek().LocalTimestamp < DateTime.Now.AddSeconds(-WindowSeconds))
                    {
                        _window.Dequeue();
                    }
                }
                else
                {
                    _window.Clear();
                }

                if (_current == null)
                {
                    // Nothing to transform
                    return;
                }

                using (var context = new JavascriptContext())
                {
                    context.SetParameter("aggregate", new Aggregate(_window));

                    foreach (var prop in _current.Properties)
                    {
                        context.SetParameter(prop.Key, prop.Value);
                    }

                    context.SetParameter("eventId", _current.Id);
                    context.SetParameter("eventLevel", _current.Level);
                    context.SetParameter("eventTimestamp", _current.LocalTimestamp);
                    context.SetParameter("eventMessage", _current.RenderedMessage);
                    
                    context.SetParameter("log", new JsLog(Log));
                    
                    var res = context.Run(Script);
                    Log.ForContext("Result", res, true).Information("Js result");
                }
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
                foreach (var prop in properties)
                {
                    l = l.ForContext(prop.Key, prop.Value, true);
                }
                return l;
            }

            public void trace(string message, IDictionary<string, object> properties)
            {
                verbose(message, properties);
            }

            public void verbose(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Verbose(message);
            }

            public void debug(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Debug(message);
            }

            public void info(string message, IDictionary<string, object> properties)
            {
                information(message, properties);
            }

            public void information(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Information(message);
            }

            public void warn(string message, IDictionary<string, object> properties)
            {
                warning(message, properties);
            }

            public void warning(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Warning(message);
            }

            public void err(string message, IDictionary<string, object> properties)
            {
                error(message, properties);
            }

            public void error(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Error(message);
            }

            public void fatal(string message, IDictionary<string, object> properties)
            {
                GetLoggerFor(properties).Fatal(message);
            }
        }

        private class Aggregate
        {
            private readonly IEnumerable<LogEventData> _data;

            public Aggregate(IEnumerable<LogEventData> data)
            {
                _data = data;
            }

            public decimal length
            {
                get { return 0; }
            }

            public decimal sum(string property)
            {
                return 0;
            }

            public decimal max(string property)
            {
                return 0;
            }

            public decimal min(string property)
            {
                return 0;
            }

            public decimal avg(string property)
            {
                return 0;
            }
        }

        public void On(Event<LogEventData> evt)
        {
            lock (_window)
            {
                _window.Enqueue(evt.Data);
            }
            _current = evt.Data;

            if (IntervalSeconds <= 0)
            {
                Transform();
            }
        }
        
    }
}
