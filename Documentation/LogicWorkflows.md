## Extending Repair Workflows with Logic Programming
FabricHealer employs configuration-as-logic by leveraging the expressive power of [Guan](https://github.com/microsoft/guan), a general-purpose logic programming system composed of a C# API and logic interpreter/query executor. It enables Prolog style syntax for writing logic rules and executing queries over them. Guan enables FabricHealer's configuration-as-logic model for defining execution workflows for automatic repairs in Service Fabric clusters (Windows and Linux).

**Why?**

Supporting formal logic-based repair workflows gives users more tools and options to express their custom repair workflows. Formal logic gives users the power to express concepts like if/else statements, leverage boolean operators, and even things like recursion! Logic programming allows users to easily and concisely express complex repair workflows that leverage the complete power of a logic programming language. We use GuanLogic for our underlying logic processing, which is a general purpose logic programming API written by Lu Xun (Microsoft) that enables Prolog-like (https://en.wikipedia.org/wiki/Prolog) rule definition and query execution in C#.  


While not necessary, reading Chapters 1-10 of the [learnprolognow](http://www.learnprolognow.org/lpnpage.php?pagetype=html&pageid=lpn-htmlch1) online (free) book can be quite useful and is highly recommended if you want to create more advanced rules to suit your more complex needs over time. Note that the documentation here doesn't assume you have any experience with logic programming. This is also the case with respect to using FabricHealer: several rules are already in place and you will simply need to change parts of an existing rule (like supplying your app names, for example) to get up and running very quickly.

**Do I need experience with logic programming?**

No, using logic to express repair workflows is easy! One doesn't need a deep knowledge and understanding of logic programming to write their own repair workflows. However, the more sophisticated/complex you need to be, then the more knowledge you will need to possess. For now, let's start with a very simple example to help inspire your own logic-based repair workflows.


***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning for a *specific* application in my cluster (e.g. "fabric:/App1"). 

***Solution***: We can leverage Guan and its built-in equals operator for checking the name of the application that triggered the warning against the name of the application for which we decided we want to perform a code package restart for. For application level health events, the repair workflow is defined inside the PackageRoot/Config/LogicRules/AppRules.config.txt file. Here is that we would enter:

```
Mitigate(AppName="fabric:/App1", MetricName="MemoryPercent") :- RestartCodePackage().
```

Don't be alarmed if you don't understand how to read that repair action! We will go more in-depth later about the syntax and semantics of Guan. The takeaway is that expressing a Guan repair workflow doesn't require a deep knowledge of Prolog programming to get started. Hopefully this also gives you a general idea about the kinds of repair workflows we can express with GuanLogic.

Each repair policy has its own corresponding configuration file: 

| Repair Policy             | Configuration File Name      | 
|---------------------------|------------------------------| 
| AppRepairPolicy           | AppRules.config.txt          | 
| DiskRepairPolicy          | DiskRules.config.txt         | 
| FabricNodeRepairPolicy    | FabricNodeRules.config.txt   | 
| ReplicaRepairPolicy       | ReplicaRules.config.txt      | 
| SystemAppRepairPolicy     | SystemAppRules.config.txt    | 
| VMRepairPolicy            | VmRules.config.txt           | 


Now let's look at *how* to actually define a Guan logic repair workflow, so that you will have the knowledge necessary to express your own.

## Writing Logic Repair Workflows

This [site](https://www.metalevel.at/prolog/concepts) gives a good, fast overview of basic prolog concepts which you may find useful.


The building block for creating Guan logic repair workflows is through the use and composition of **Predicates**. A predicate has a name, and zero or more arguments. In FH, there are two different kinds of predicates: **Internal Predicates** and **External Predicates**. Internal predicates are equivalent to standard predicates in Prolog. An internal predicate defines relations between their arguments and other internal predicates. External predicates on the other hand are more similar to functions in terms of behaviour. External predicates are usually used to perform actions such as checking values, performing calculations, performing repairs, and binding values to variables.

Here is a list of currently implemented **External Predicates**:

**Repair Predicates**

```RestartCodePackage()``` 

Attempts to restart the code package for the service that emitted the health event, returns true if successful, else false.  

```RestartFabricNode()```

Attempts to restart the node of the service that emitted the health event, returns true if successful, else false. Takes an optional Safe parameter: "safe" or "unsafe" which defines whether or not to perform a safe or unsafe node restart. A safe node restart will try to first deactivate the node before restarting, whereas an unsafe node restart will try restarting the node without first trying to deactivate it. 

```RestartReplica()``` 

Attempts to restart the replica of the service that emitted the health event, returns true if successful, else false. 

```RestartVM()``` 

Attempts to restart the underlying virtual machine of the service that emitted the health event, returns true if successful, else false. 

```RestartFabricSystemProcess()``` 

Attempts to restart a system service process that is misbehaving as per the FO health data.  

```DeleteFiles()``` 

Attempts to delete files in a supplied path. You can supply target path, max number of files to remove, sort order (ASC/DESC). 

**Helper Predicates**

```EmitMessage()``` 

This will emit telemetry/etw/health report from a rule which enables informational messaging and can help with debugging. 

```GetRepairHistory()``` 

Gets the number of times a repair has been run with a supplied time window. 

```CheckFolderSize()``` 

Checks the size of a specified folder (full path) and returns a boolean value indicating whether the supplied max size (MB or GB) has been reached or exceeded. 

```CheckInsideRunInterval()``` 

Checks if some repair has already run once within the specified time frame (TimeSpan). 


**Forming a Logic Repair Workflow**

Now that we know what predicates are, let's learn how to form a logic repair workflow. 

A GuanLogic program is expressed in terms of **rule(s)** and a program is executed by running a **query** over these rule(s). A rule is of the form: 

```RuleHead(*args, ...*) :- PredicateA(), PredicateB(), ...```. 


A simple though imprecise way to understand what a rule is, is to just think of it as a function.
```
RuleHead(*args, ...*) -> Function signature (name + arguments)
PredicateA(), PredicateB(), ... -> Function body (everything to the right of the ":-" is part of the function body)
```

A query is simply a way to invoke a rule (function). 
```RuleHead() -> invokes the rule of name "RuleHead" with the same number of arguments``` 

By default, for logic-based repair workflows, FH will execute a query which calls a rule named ```Mitigate()```. Think of ```Mitigate()``` as the root for executing the repair workflow, similar to the Main() function in most programming languages. By default, ```Mitigate()``` passes arguments that can be passed to predicates used in the repair workflow.

| Argument Name             | Definition                                                                                   |
|---------------------------|----------------------------------------------------------------------------------------------|
| AppName                   | Name of the SF application, format is fabric:/SomeApp                                        |
| ServiceName               | Name of the SF service, format is fabric:/SomeApp/SomeService                                |
| NodeName                  | Name of the node                                                                             | 
| NodeType                  | Type of node                                                                                 |  
| PartitionId               | Id of the partition                                                                          |
| ReplicaOrInstanceId       | Id of the replica or instance                                                                |
| FOErrorCode               | Error Code emitted by FO (e.g. "FO002")                                                      | 
| MetricName                | Name of the resource supplied by FO (e.g., CpuPercent or MemoryMB, etc.)                     |   
| MetricValue               | Corresponding Metric Value supplied by FO (e.g. "85" indicating 85% CPU usage)               | 
| SystemServiceProcessName  | The name of a Fabric system service process supplied in FO health data                       | 
| OS                        | The name of the OS from which the FO data was collected (Linux or Windows)                   |

For example if you wanted to use AppName and ServiceName in your repair workflow you would specify them like so:
```
Mitigate(AppName=?x, ServiceName=?y) :- ..., ?x == "fabric:/App1", ...
now the variable ?x is bound to the application name and ?y is bound to the service name. You can name variables whatever you want
```

Here's a simple example that we've seen before:

```
Mitigate() :- RestartCodePackage().
```

Essentially, what this logic repair workflow (mitigation scenario) is describing is that if FO emits a health event that falls under the AppServiceCpuMemoryPortAbuseRepairPolicy and if the repair policy is enabled, then we will execute the repair action. FH will automatically detect that it is a logic workflow, so it will invoke the root rule ```Mitigate()```. Guan determines that the ```Mitigate()``` rule is defined inside the repair action, where it then will try to execute the body of the ```Mitigate()``` rule.

Users can define multiple rules (separated by a newline) as part of a repair workflow, here is an example:

```
Mitigate() :- RestartCodePackage().
Mitigate() :- RestartFabricNode().
```

This seems confusing as we've defined ```Mitigate()``` twice. Here is the execution flow explained in words: "Look for the *first* ```Mitigate()``` rule (read from top to bottom). The *first* ```Mitigate()``` rule is the one that calls ```RestartCodePackage()``` in its body. So we try to run the first rule. If the first rule fails (i.e. ```RestartCodePackage()``` returns false) then we check to see if there is another rule named  ```Mitigate()```, which there is. The next ```Mitigate()``` rule we find is the one that calls ```RestartFabricNode()``` so we try to run the second rule. 

This concept of retrying rules is important to understand. Imagine your goal is that you want ```Mitigate()``` to return true, so you will try every rule named  ```Mitigate()``` in order until one returns true, in which case the query stops. This concept of retrying rules is also how you can model conditional branches and boolean operators in GuanLogic repair workflows.

**Important Syntax Rules**: Each rule must end with a period, a single rule may be split up across multiple lines for readability:

```
Mitigate() :- PredicateA(),    <-- The first predicate in the rule must be inline with the Head of the rule like so
              PredicateB(),
              PredicateC().
```

The following would be invalid:

```
Mitigate() :- 
              PredicateA(),
              PredicateB(),
              PredicateC().
```

**Modelling Boolean Operators**

Let's look at how we can create AND/OR/NOT statements in Guan logic repair workflows.

**NOT**
```
Mitigate() :- not((condition A)), (true branch B).
Can be read as: if (!A) then goto B
```

NOT behaviour is achieved by wrapping any predicate inside ```not()``` which is a built-in GuanLogic predicate.


**AND**
```
Mitigate() :- (condition A), (condition B), (true branch C).
Can be read as: if (A and B) then goto C
```

AND behavior is achieved by separating predicates with commas, similar to programming with the ```||``` character.

**OR**
```
Mitigate() :- (condition A), (true branch C).
Mitigate() :- (condition B), (true branch C).
Can be read as: if (A or B) then goto C
```

OR behaviour is achieved by separating predicates by rule. Here is the execution flow for the above workflow: Go to first ```Mitigate()``` rule -> does predicate A succeed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. We find the second ```Mitigate()``` rule -> does predicate B suceed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. There are no more ```Mitigate()``` rules so the workflow is over.

**Using internal predicates**

So far we've only looked at creating rules that are invoked from the root ```Mitigate()``` query, but users can also create their own rules like so:

```
MyInternalPredicate() :- RestartCodePackage().
Mitigate() :- MyInternalPredicate().
```

Here we've defined an internal predicate named ```MyInternalPredicate()``` and we can see that it is invoked in the body of the ```Mitigate()``` rule. In order to fulfill the ```Mitigate()``` rule, we will need to fulfill the ```MyInternalPredicate()``` predicate since it is part of the body of the ```Mitigate()``` rule. This repair workflow is identical in behaviour to one that directly calls ```RestartCodePackage()``` inside the body of ```Mitigate()```.

Using internal predicates like this is useful for improving readability and organizing complex repair workflows.

With internal predicates, you can easily configure run interval time for a repair (how often to run the repair) in a convenient way.
The ```IntervalForRepairTarget``` predicate below simply allows us to express which target we are interested in determining where we are relative to the specified run interval for the specific repair.
Like all repair configurations in FH, these settings for run interval for various repair targets are defined as part of the rule itself. This is a key part (and advantage) of configuration as logic.

If inside the supplied RunInterval, then cut (!). Here, this effectively means stop processing rules. The logic below specifies that the related Mitigate rule (one that employs the internal predicate) will run for each IntervalForRepairTarget predicate specification.
```
IntervalForRepairTarget(AppName="fabric:/CpuStress", RunInterval=00:15:00).
IntervalForRepairTarget(AppName="fabric:/ContainerFoo2", RunInterval=00:15:00).
IntervalForRepairTarget(MetricName="ActiveTcpPorts", RunInterval=00:15:00).

Mitigate() :- IntervalForRepairTarget(Target=?target, RunInterval=?timespan), CheckInsideRunInterval(RunInterval=?timespan), !.

```
IMPORTANT: the state machine holding the data that the CheckInsideRunInterval predicate compares your specified RunInterval TimeSpan value against is our friendly neighborhood RepairManagerService(RM), a stateful Service Fabric System Service that orchestrates repairs
and manages repair state. ***FH requires the presence of RM in order to function***.  

Let's look at another example of an internal predicate that is used in FH's SystemAppRules rules file, TimeScopedRestartFabricNode, whixh is a simple convenience internal predicate used to check for the number of times a repair has run to completion within a supplied time window.
If completed repair count is less then supplied value, then run RestartFabricNode mitigation. Here, you can see it removes the need to have to write the same logic in multiple places. 

```
TimeScopedRestartFabricNode(?count, ?time) :- GetRepairHistory(?repairCount, TimeWindow=?time), ?repairCount < ?count, 
	RestartFabricNode().

## CPU Time - Percent
Mitigate(AppName="fabric:/System", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80,
	TimeScopedRestartFabricNode(5, 01:00:00).

## Memory Use - Megabytes in use
Mitigate(AppName="fabric:/System", MetricName="MemoryMB", MetricValue=?MetricValue) :- ?MetricValue >= 2048,
	TimeScopedRestartFabricNode(5, 01:00:00).

## Memory Use - Percent in use
Mitigate(AppName="fabric:/System", MetricName="MemoryPercent", MetricValue=?MetricValue) :- ?MetricValue >= 40,
	TimeScopedRestartFabricNode(5, 01:00:00).

## Ephemeral Ports in Use
Mitigate(AppName="fabric:/System", MetricName="EphemeralPorts", MetricValue=?MetricValue) :- ?MetricValue >= 800,
	TimeScopedRestartFabricNode(5, 01:00:00).
```

**Filtering parameters from Mitigate()**

If you wish to do equals checks such as ```?AppName == ...``` you don't actually need to write this in the body of your rules, instead you can specify these values inside Mitigate() like so:

```
## This is the preferred way to do this. It is easier to read and employs less (unnecessary) basic logic.
Mitigate(AppName="fabric:/App1") :- ...
```

What that means, is that the rule will only execute when the AppName is equal to "fabric:/App1". This is equivalent to the following:

```
Mitigate(AppName=?AppName) :- ?AppName == "fabric:/App1", ...
```

Obviously, the first way of doing it is more succinct and, again, preferred.
