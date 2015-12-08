# Seq.App.Transform
Collects events and allows a javascript to transform them into different events written back to the log.

## Features

- Quickly transform any event using javascript, 1-1 or 1-n
- Calculate rolling averages, min/max or sums over a period of time
- Emit events at a specific interval
- Combine with other apps, such as Slack or e-mail notifications to build intelligent alerts

## How to use

By default, the javascript code in the **Script** field is executed on each received event, but it may be triggered at a set interval instead by using the **Interval** field.

The script can access information about the current event, or last received event in case you're using the **Interval** setting. There are standard properties, always available, and there are the event properties/data. They are all variables in the global scope, so they are easy to access.

The standard properties are:

- **eventId** - The ID of the event
- **eventLevel** - The logging level of the event
- **eventTimestamp** - The timestamp of when the event was emitted
- **eventMessage** - The rendered message of the event

Apart from the standard fields you can access the event properties by just referencing them by name:

```javascript
var prop = SomeValue;
```

The above script would put the value of the event property *SomeValue* in a variable called *prop*.

### Writing events

The following methods are available for writing events:

- logVerbose(*&lt;message&gt;*[, properties])
- logDebug(*&lt;message&gt;*[, properties])
- logInfo(*&lt;message&gt;*[, properties])
- logWarn(*&lt;message&gt;*[, properties])
- logError(*&lt;message&gt;*[, properties])
- logFatal(*&lt;message&gt;*[, properties])

Message is a string that may contain Serilog style placeholders (see examples below). Properties is an object that contains event properties, which may or may not appear in the message.

### Aggregation

It's possible to get aggregated numeric data by setting the **Aggregation - Window** field. By setting this, the script can use the global *aggregate* object to access information about the collected events.

*aggregate* provides the following functions and fields:

- **length** - The number of events in the aggregate
- **sum("*****&lt;property name&gt;*****")** - If the specified property is numerical, calculates the sum of all values
- **avg("*****&lt;property name&gt;*****")** - If the specified property is numerical, calculates the average value
- **max("*****&lt;property name&gt;*****")** - If the specified property is numerical, gives you the highest number of the values
- **min("*****&lt;property name&gt;*****")** - If the specified property is numerical, gives you the lowest number of the values

### Examples

In order to calculate the number of errors per minute:

```javascript
/*
 * Signal: Only errors
 * Aggregation - Window: 60
 * Interval: 60 
 */

// Script:

logInfo("Number of errors last minute: {Errors}", {Errors: aggregate.length});
```

Emit an event if the 5-minute rolling average of a timestamp exceeds a given number:

```javascript
/*
 * Aggregation - Window: 300
 * Interval: 0
 */

// Script:

// Assuming that there's a property called elapsed with a millisecond value in it
var avg = aggregate.avg("Elapsed")
if (avg > 100) {
    logWarn("Time for operation is unusually high {Average}", {Average:avg});
}
```