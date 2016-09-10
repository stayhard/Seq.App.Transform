# Seq.App.Transform
Collects events and allows a javascript to transform them into different events written back to the log.

## Features

- Quickly transform any event using javascript
- Calculate rolling averages, min/max or sums over a period of time
- Emit events at a specific interval
- Combine with other apps, such as Slack or e-mail notifications to build intelligent alerts

## How to use

By default, the javascript code in the **Script** field is executed on each received event, but it may be triggered at a set interval instead by using the **Interval** field.

The script can access information and properties about events in the aggregation window, or last received event if set to window size is set to 0.

Data is accessed through a few global objects that is used similarly. Each of these objects holds the properties of all events in the window but aggregates the data in different ways.

The global objects are:

- **all** - All the values of the property in an array
- **count** - The number of values of all events in window
- **first** - The value of the property of the first event in window
- **last** - The value of the property of the last event in window
- **min** - Lowest numerical property value of all events in window
- **min** - Highest numerical property value of all events in window
- **mean** - Calculated numerical mean of values in window
- **sum** - Calculated sum of values in window

On each of the global objects you can find all the properties of all events in the window. There are also standard properties, always available.

The standard properties are:

- **$Id** - The ID of the event
- **$Level** - The logging level of the event
- **$Timestamp** - The timestamp of when the event was emitted
- **$Message** - The rendered message of the event

Usage example:

```javascript
var lastMessage = last.$Message;
```

### Writing events

The following methods are available for writing events:

- logVerbose(*&lt;message&gt;*[, properties])
- logDebug(*&lt;message&gt;*[, properties])
- logInfo(*&lt;message&gt;*[, properties])
- logWarn(*&lt;message&gt;*[, properties])
- logError(*&lt;message&gt;*[, properties])
- logFatal(*&lt;message&gt;*[, properties])

Message is a string that may contain Serilog style placeholders (see examples below). Properties is an object that contains event properties, which may or may not appear in the message.

### Incident Handling

In order to simplify writing smart alerts and incident tracking, there are two global functions that can be used:

- openIncident(*&lt;name&gt;*)
- closeIncident(*&lt;name&gt;*)

An app can open and close multiple incidents indepentent of each other by using a different _name_ value. The app will only write to the event log when the state changes (from closed to open and vice versa).

You can easily list the status of incidents using the following query in Seq:

```sql
select last(IncidentState) as State, ToIsoString(last(@Timestamp)) as Timestamp from stream where length(IncidentName) > 0 group by IncidentName
```

### Examples

In order to calculate the number of errors per minute:

```javascript
/*
 * Signal: Only errors
 * Window: 60
 * Interval: 60 
 */

// Script:

logInfo("Number of errors last minute: {Errors}", {Errors: count.$Id});
```

Open an incident if the 5-minute rolling average of a timestamp exceeds a given number:

```javascript
/*
 * Window: 300
 * Interval: 0
 */

// Script:

// Assuming that there's a property called elapsed with a millisecond value in it
var incidentName = "mean.Elapsed > 100";
if (mean.Elapsed > 100) {
    openIncident(incidentName);
} else {
	closeIncident(incidentName);
}
```