## Extending Repair Workflows with Logic Programming
FabricHealer employs configuration-as-logic by leveraging the expressive power of [Guan](https://github.com/microsoft/guan), a general-purpose logic programming system composed of a C# API and logic interpreter/query executor. It enables Prolog style syntax for writing logic rules and executing queries over them. Guan enables FabricHealer's configuration-as-logic model for defining execution workflows for automatic repairs in Service Fabric clusters (Windows and Linux). Note that Guan is not a complete implementation of Prolog (nor is that its intention). There are differences and extended capabilities, however. One difference is the requirement for a trailing "." in Prolog logic rules: This is not required in Guan as each rule is a separate C# string (in a ```List<string>```). That said, in FabricHealer, we use the "." delimiter because FabricHealer pre-parses rules before handing them over to Guan (so we can do things like have comments in rule files) as a ```List<string>``` and we rely on the trailing "." to differentiate individual rules in a single file (we could choose not do this, of course, but we chose to do so for simplicity reasons). This is why you see trailing "."s in FabricHealer logic rules and not in Guan sample logic rules on the Guan repo.

**Why?**

Supporting formal logic-based repair workflows gives users more tools and options to express their custom repair workflows. Formal logic gives users the power to express concepts like if/else statements, leverage boolean operators, and even things like recursion! Logic programming allows users to easily and concisely express complex repair workflows that leverage the complete power of a logic programming language. We use GuanLogic for our underlying logic processing, which is a general purpose logic programming API written by Lu Xun (Microsoft) that enables Prolog-like (https://en.wikipedia.org/wiki/Prolog) rule definition and query execution in C#.  

While not necessary, reading Chapters 1-10 of the [learnprolognow](http://www.learnprolognow.org/lpnpage.php?pagetype=html&pageid=lpn-htmlch1) online (free) book can be quite useful and is highly recommended if you want to create more advanced rules to suit your more complex needs over time. Note that the documentation here doesn't assume you have any experience with logic programming. This is also the case with respect to using FabricHealer: several rules are already in place and you will simply need to change parts of an existing rule (like supplying your app names, for example) to get up and running very quickly.

**Do I need experience with logic programming?**

No, using logic to express repair workflows is easy! One doesn't need a deep knowledge and understanding of logic programming to write their own repair workflows. However, the more sophisticated/complex you need to be, then the more knowledge you will need to possess. For now, let's start with a very simple example to help inspire your own logic-based repair workflows.

***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning for a *specific* application in my cluster (e.g. "fabric:/App1"). 

***Solution***: We can leverage Guan and its built-in equals operator for checking the name of the application that triggered the warning against the name of the application for which we decided we want to perform a code package restart for. For application level health events, the repair workflow is defined inside the PackageRoot/Config/LogicRules/AppRules.guan file. Here is that we would enter:

```
Mitigate(AppName="fabric:/App1", MetricName="MemoryPercent") :- RestartCodePackage.
```

Don't be alarmed if you don't understand how to read that repair action! We will go more in-depth later about the syntax and semantics of Guan. The takeaway is that expressing a Guan repair workflow doesn't require a deep knowledge of Prolog programming to get started. Hopefully this also gives you a general idea about the kinds of repair workflows we can express with GuanLogic.

Each repair policy has its own corresponding configuration file located in the FabricHealer project's PackageRoot/Config/LogicRules folder: 

| Repair Policy             | Configuration File Name      | 
|---------------------------|------------------------------| 
| AppRepairPolicy           | AppRules.guan                | 
| DiskRepairPolicy          | DiskRules.guan               | 
| FabricNodeRepairPolicy    | FabricNodeRules.guan         |  
| MachineRepairPolicy       | MachineRules.guan            | 
| ReplicaRepairPolicy       | ReplicaRules.guan            | 
| SystemServiceRepairPolicy | SystemServiceRules.guan      | 


Now let's look at *how* to actually define a Guan logic repair workflow, so that you will have the knowledge necessary to express your own.

### Writing Logic Rules

This [site](https://www.metalevel.at/prolog/concepts) gives a good, fast overview of basic prolog concepts which you may find useful.

The building block for creating Guan logic repair workflows is through the use and composition of **Predicates**. A predicate has a name, and zero or more arguments.
In FH, there are two different kinds of predicates: **Internal Predicates** and **External Predicates**. Internal predicates are equivalent to standard 
predicates in Prolog. An internal predicate defines relations between their arguments and other internal predicates.
External predicates on the other hand are more similar to functions in terms of behaviour. External predicates are usually used to perform
actions such as checking values, performing calculations, performing repairs, and binding values to variables.

### FabricHealer Predicates 

Here is a list of currently implemented **External Predicates**:

**Note**: Technically, an external predicate is not a function - though you can think of it as a function as it optionally takes input and does specific things. In reality, an external predicate is a type.
You can look at how each one of the external predicates below are defined and implemented by looking in FabricHealer/Repair/Guan source folder.
Given this, you only need to append an "()" at the end of an external predicate if you don't specify any arguments.
E.g., you don't have to specify ```MyPredicate``` as ```MyPredicate()``` if you don't supply any arguments (or variables to which some value will be bound for use in subgoals). You can if you want. It's really up to you.

**Please read the [Optional Arguments](#Optional-Arguments) section to learn more about how optional argument support works in FabricHealer.**

**RestartCodePackage** 

Attempts to restart a service code package (all related facts are already known by FH as these fact were provided by either FO or FHProxy).

Arguments: 

- DoHealthChecks (Boolean), Optional
- MaxWaitTimeForHealthStateOk (TimeSpan), Optional
- MaxExecutionTime (TimeSpan), Optional

Note: MaxWaitTimeForHealthStateOk is a blocking setting. That is, FH will wait for the specified amount of time before deciding if the repair succeeded or not. This means that if the target's HealthState is not Ok after a repair completes, after the specified wait time, then the repair
is considered to be unsuccessful. Think of this setting as a blocking Ok HealthState "probationary" check that blocks for the amount of time you specify.

Example: 

```Mitigate(MetricName="Threads") :- RestartCodePackage(false, 00:10:00, 00:30:00)..```

**RestartFabricNode**

Restarts a Service Fabric Node. Note that the repair task that is created for this type of repair will have a NodeImpactLevel of Restart, so
Service Fabric will disable the node before the job is Approved, after which time FabricHealer will execute the repair.

Arguments: none. 

Example: 

```Mitigate(HealthState=Error) :- GetRepairHistory(?repairCount, 08:00:00), ?repairCount < 2, RestartFabricNode.``` 

**DeactivateFabricNode**

Schedules a Service Fabric Repair Job to deactivate a Service Fabric Node. 

Arguments:

- ImpactLevel (string, supported values: Restart, RemoveData, RemoveNode), Optional (default is Restart). 

Example: 

```Mitigate(HealthState=Error) :- GetRepairHistory(?repairCount, 08:00:00), ?repairCount < 2, DeactivateFabricNode(RemoveData).```

**RestartReplica** 

Attempts to restart a service replica (the replica (or instance) id is already known by FH as that fact was provided by either FO or FHProxy). 

Arguments: 

- DoHealthChecks (Boolean), Optional
- MaxWaitTimeForHealthStateOk (TimeSpan), Optional
- MaxExecutionTime (TimeSpan), Optional

Example: 

```Mitigate(MetricName="Threads", MetricValue=?value) :- ?value > 500, RestartReplica.``` 

Attempts to restart the replica of the service that emitted the health event, returns true if successful, else false. 

**ScheduleMachineRepair**

Arguments: 

- DoHealthChecks (Boolean), Optional
- RepairAction (String), Required

Attempts to schedule an infrastructure repair for the underlying virtual machine, returns true if successful, else false. 

**RestartFabricSystemProcess**

Arguments: 

- DoHealthChecks (Boolean), Optional
- MaxWaitTimeForHealthStateOk (TimeSpan), Optional
- MaxExecutionTime (TimeSpan), Optional

Attempts to restart a system service process that is misbehaving as per the FO health data.  

**DeleteFiles** 

Arguments: 

- Path (String, **must always be the first argument**), Required.
- SortOrder (String, supported values are Ascending, Descending), Optional.
- MaxFilesToDelete (long), Optional.
- RecurseSubdirectories (Boolean), Optional.
- SearchPattern (String), Optional.

Attempts to delete files in a supplied path. You can supply target path, max number of files to remove, sort order (ASC/DESC). 

**Helper Predicates**

**LogInfo, LogWarning, LogError** 

These will emit telemetry/etw/health event at corresponding level (Info, Warning, Error) from a rule and can help with debugging, auditing, upstream action (ETW/Telemetry -> Alerts, for example). 

Arguments: 

- Message (String), Required 

Example (input string is a formatted string with arguments, in this case): 

```LogInfo("0042_{0}: Specified Machine repair escalations have been exhausted for node {0}. Human intervention is required.", ?nodeName)```

Example (simple string, unformatted): 

```LogInfo("This is a message...")```

**GetRepairHistory** 

Gets the number of times a repair has been run with a supplied time window. 

This is an example of a predicate that takes up to 3 arguments, where the first one must be a variable (as it will hold the result, which can then be used in subsequent subgoals within the rule). 

Example: 

```GetRepairHistory(?repairCount, 08:00:00, System.Azure.Heal)```

The above example specifies that a variable named ?repairCount will hold the value of how many times System.Azure.Heal machine repair jobs were completed in 8 hours. For machine repairs, you must specify the action name. For other types of repairs, you do not need to do that. E.g., for
a service-level repair 

```GetRepairHistory(?repairCount, 02:00:00)``` is all FH needs as it keeps track of the repairs that it executes (where it is the Executor, which is it never is for machine-level repairs).

**CheckFolderSize** 

Checks the size of a specified folder (full path) and returns a boolean value indicating whether the supplied max size (MB or GB) has been reached or exceeded. 

Arguments: 

- FolderPath (string), Required. You do not specify a name, just the value.

**CheckInsideRunInterval** 

Checks if some repair has already run once within the specified time frame (TimeSpan). 

Arguments: 

- [Unnamed], (TimeSpan), Required. (You do not specify a name, just the value) 

**CheckInsideHealthStateMinDuration** 

Checks to see if the entity has been in some HealthState for the specified duration. Facts like EntityType and HealthState are already known to FabricHealer. They are therfore not arguments this predicate supports (it doesn't have to).

Arguments: 

[Unnamed], (TimeSpan), Required. 

Example: 

```
## Don't proceed if the target node hasn't been in Error (including cyclic Up/Down) state for at least two hours.
Mitigate :- CheckInsideHealthStateMinDuration(02:00:00), !.
``` 

**CheckOutstandingRepairs** 

Checks the number of repairs (repair tasks) currently in flight in the cluster is less than or equal to the supplied argument.

Arguments: 

[Unnamed], (long), Required. 

```
## Don't proceed if there are already 2 or more entity-specific (implicit fact) repairs currently active in the cluster.
Mitigate :- CheckOutstandingRepairs(2), !.
```

**CheckInsideScheduleInterval** 

Checks if the last related (context is the type of repair, which FH already knows) repair was scheduled within the supplied TimeSpan.

Arguments: 

[Unnamed], (TimeSpan), Required. 

Example: 

```
## Don't proceed if FH scheduled a machine repair less than 10 minutes ago.
Mitigate :- CheckInsideScheduleInterval(00:10:00), !.
```

**CheckInsideNodeProbationPeriod** 

This is for machine or node level repairs. It checks if the last related repair was Completed less than the supplied TimeSpan ago. 

Arguments: 

[Unnamed], (TimeSpan), Required. 

Example: 

```
## Don't proceed if target node is currently inside a post-repair health probation period (post-repair means a Completed repair; target node is still recovering).
Mitigate :- CheckInsideNodeProbationPeriod(00:30:00), !.
```
**LogRule** 

Logs the entire repair rule in which LogRule is specified. 

Arguments:

[Unnamed], (long), Required.

Example: 

```
Mitigate(Source=?source, Property=?property) :- LogRule(64), match(?source, "SomeOtherWatchdog"),
    match(?property, "SomeOtherFailure"), DeactivateFabricNode(RemoveData).
```

The logic rule above begins at line number 64 in the related file (in this case, MachineRules.guan). By specifying LogRule(64), FabricHealer will emit the 
rule in its entirety as an SF health event (Ok HealthState), ETW event and telemetry event (ApplicationInsights/LogAnalytics). This is very useful for debugging and auditing
rules.

### Forming a Logic Repair Workflow

Now that we know what predicates are, let's learn how to form a logic repair workflow. 

A GuanLogic program is expressed in terms of **rule(s)** and a program is executed by running a **query** over these rule(s). A rule is of the form: 

```goal(*args, ...*) :- PredicateA(), PredicateB(), ...```. 


A simple though imprecise way to understand what a rule is, is to just think of it as a function.
```
goal(*args, ...*) -> Function signature (name + arguments)
PredicateA(), PredicateB(), ... -> Function body (everything to the right of the ":-" is part of the function body)
```

A query is simply a way to invoke a rule (function). 
```goal() -> invokes the rule of name "goal" with the same number of arguments``` 

By default, for logic-based repair workflows, FH will execute a query which calls a rule named ```Mitigate()```. Think of ```Mitigate()``` as the root for executing the repair workflow, similar to the Main() function in most programming languages. By default, ```Mitigate()``` passes arguments that can be passed to predicates used in the repair workflow.

For example if you wanted to use AppName and ServiceName in your repair workflow you would specify them like so:
```
Mitigate(AppName=?x, ServiceName=?y) :- ..., ?x == "fabric:/App1", ...
now the variable ?x is bound to the application name and ?y is bound to the service name. You can name variables whatever you want
```

Here's a simple example that we've seen before:

```
Mitigate :- RestartCodePackage.
```

Essentially, what this logic repair workflow (mitigation scenario) is describing that if FO emits a health event Warning/Error related to any application entity and the App repair policy is enabled, then we will execute the repair action. FH will automatically detect that it is a logic workflow, so it will invoke the root rule ```Mitigate()```. Guan determines that the ```Mitigate()``` rule is defined inside the repair action, where it then will try to execute the body of the ```Mitigate()``` rule.

Users can define multiple rules (separated by a newline) as part of a repair workflow, here is an example:

```
Mitigate :- RestartCodePackage.
Mitigate :- RestartFabricNode.
```

This seems confusing as we've defined ```Mitigate()``` twice. Here is the execution flow explained in words: "Look for the *first* ```Mitigate()``` rule (read from top to bottom). The *first* ```Mitigate()``` rule is the one that calls ```RestartCodePackage()``` in its body. So we try to run the first rule. If the first rule fails (i.e. ```RestartCodePackage()``` returns false) then we check to see if there is another rule named  ```Mitigate()```, which there is. The next ```Mitigate()``` rule we find is the one that calls ```RestartFabricNode()``` so we try to run the second rule. 

This concept of retrying rules is important to understand. Imagine your goal is that you want ```Mitigate()``` to return true, so you will try every rule named  ```Mitigate()``` in order until one returns true, in which case the query stops. This concept of retrying rules is also how you can model conditional branches and boolean operators in GuanLogic repair workflows.

**Important Syntax Rules**: Each rule must end with a period, a single rule may be split up across multiple lines for readability:

```
Mitigate :- Predicate),    <-- The first predicate in the rule must be inline with the Head of the rule like so
            PredicateB,
            PredicateC.
```

The following would be invalid:

```
Mitigate :- 
            PredicateA,
            PredicateB,
            PredicateC.
```

**Modelling Boolean Operators**

Let's look at how we can create AND/OR/NOT statements in Guan logic repair workflows.

**NOT**
```
Mitigate :- not((condition A)), (true branch B).
Can be read as: if (!A) then goto B
```

NOT behaviour is achieved by wrapping any predicate inside ```not()``` which is a built-in GuanLogic predicate.


**AND**
```
Mitigate :- (condition A), (condition B), (true branch C).
Can be read as: if (A and B) then goto C
```

AND behavior is achieved by separating predicates with commas, similar to programming with the ```||``` character.

**OR**
```
Mitigate :- (condition A), (true branch C).
Mitigate :- (condition B), (true branch C).
Can be read as: if (A or B) then goto C
```

OR behaviour is achieved by separating predicates by rule. Here is the execution flow for the above workflow: Go to first ```Mitigate()``` rule -> does predicate A succeed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. We find the second ```Mitigate()``` rule -> does predicate B suceed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. There are no more ```Mitigate()``` rules so the workflow is over.

**Using internal predicates**

So far we've only looked at creating rules that are invoked from the root ```Mitigate()``` query, but users can also create their own rules like so:

```
MyInternalPredicate :- RestartCodePackage.
Mitigate :- MyInternalPredicate.
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

Mitigate :- IntervalForRepairTarget(?target, ?runinterval), CheckInsideRunInterval(?runinterval), !.
```

IMPORTANT: the state machine holding the data that the CheckInsideRunInterval predicate compares your specified RunInterval TimeSpan value against is our friendly neighborhood RepairManagerService(RM), a stateful Service Fabric System Service that orchestrates repairs
and manages repair state. ***FH requires the presence of RM in order to function***.

The base type for all predicates in Guan is PredicateType. It's constructor takes a required string pararmeter (the name of the predicate as it is used in a rule) and optional parameters of public visibility (bool) and minimum/maximum positional arguments. Note that for RestartCodePackagePredicateType the min 
positional argument is set to 0, which means there are no required arguments. That said, look how it is called as the definition of an internal predicate, TimeScopedRestartCodePackage in the App rules file:

```
TimeScopedRestartCodePackage :- RestartCodePackage(DoHealthChecks=true, MaxWaitTimeForHealthStateOk=00:10:00).
```
This means that the RestartCodePackage predicate takes two optinal args (see RestartCodePackagePredicateType.cs to see how handling optional arguments is implemented) and, again, 0 required arguments. This is important to remember as you build rules for existing predicates and when you write your own.


Let's look at another example of an internal predicate that is used in FH's SystemAppRules rules file, TimeScopedRestartFabricNode, whixh is a simple convenience internal predicate used to check for the number of times a repair has run to completion within a supplied time window.
If completed repair count is less then supplied value, then run RestartFabricNode mitigation. Here, you can see it removes the need to have to write the same logic in multiple places. 

```
TimeScopedRestartFabricNode(?count, ?time) :- GetRepairHistory(?repairCount, ?time), ?repairCount < ?count, 
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

**Filtering constraints in the head of rule**

If you wish to do a single test for equality such as ```?AppName == "fabric:/App1``` you don't actually need to write this in the body of your rules, instead you can specify these values inside Mitigate() like so:

```
## This is the preferred way to do this for a single value test (note the use of = operator, not ==). It is easier to read and employs less (unnecessary) basic logic.
Mitigate(AppName="fabric:/App1") :- ...
```

The above specifies that subgoals will only execute if the AppName fact is "fabric:/App1". This is equivalent to the following:

```
Mitigate(AppName=?appName) :- ?appName == "fabric:/App1", ...
```

Obviously, the first way of doing it is more succinct and, again, preferred for simple cases where you are only interested in a single value for the fact. If, for example,
you want to test for multiple values of AppName, then you have to pull the variable out into a subrule as you can't add a logical expression to the head of a rule.

E.g., you want to proceed if AppName is either fabric:/App1 or fabric:/App42:

```
Mitigate(AppName=?appName) :- ?appName == "fabric:/App1" || ?appName == "fabric:/App42", ...
```

Or, you are only interested in any AppName that is not fabric:/App1 or fabric:/App42:

```
Mitigate(AppName=?appName) :- not(?appName == "fabric:/App1" || ?appName == "fabric:/App42"), ...
```

### Optional Arguments

FabricHealer's Guan predicate implementations support optional arguments and they can be specified in any order, as long they are also named. 

E.g., 

```Mitigate(AppName="fabric:/FooBar", MetricName="MemoryMB") :- RestartReplica(DoHealthChecks=false, MaxWaitTimeForHealthStateOk=00:05:00, MaxExecutionTime=00:15:00).```

The above rule will execute the subgoal when the AppName fact is "fabric:/FooBar" and the MetricName is "MemoryMB" (both facts would only come from FabricObserver or FHProxy). Let's just look at this piece: 
```RestartReplica(DoHealthChecks=false, MaxWaitTimeForHealthStateOk=00:05:00, MaxExecutionTime=00:15:00)``` 

Note the arguments. It could also be written as

```RestartReplica(false, 00:05:00, 00:15:00)``` 

However, if you just wanted to supply either the health checks boolean or wait timespan or execution timespan argument, then you would have to also employ the relevant name:

```RestartReplica(MaxWaitTimeForHealthStateOk=00:05:00)``` 

Or 

```RestartReplica(MaxExecutionTime=00:15:00)``` 

Or 

```RestartReplica(DoHealthChecks=false)```  

Or 

``` RestartReplica(DoHealthChecks=false, MaxExecutionTime=00:15:00)``` 

Hopefully, you can see the pattern here: You can employ any combination of predicate arguments in FabricHealer's Guan predicates (not the case in Guan's system predicates, however), but **they must each be named if you do not specifcy all of them**. 


### Facts available to you in the head (Mitigate) of any rule you employ.

#### Application/Service logic rules 
**App/Service repair requires facts from FabricObserver or FHProxy. FH can be deployed to a single or multiple nodes.** 

#### Applicable Named Arguments present in Mitigate. Corresponding data is supplied by FabricObserver or FHProxy, renamed for brevity by FH. 
| Argument Name             | Definition                                                                                   |
|---------------------------|----------------------------------------------------------------------------------------------|
| AppName                   | Name of the SF application, format is "fabric:/SomeApp" (Quotes are required)                |
| ServiceName               | Name of the SF service, format is "fabric:/SomeApp/SomeService" (Quotes are required)        |
| ServiceKind               | The state kind of SF service: Stateful or Stateless                                          |
| NodeName                  | Name of the node                                                                             | 
| NodeType                  | Type of node                                                                                 |  
| ObserverName              | Name of Observer that generated the event (if the data comes from FabricObserver service)    |
| PartitionId               | Id of the partition                                                                          |
| ReplicaOrInstanceId       | Id of the replica or instance                                                                |
| ReplicaRole               | Role of replica: Primary or ActiveSecondary. Or None (e.g., Stateless replica)               |
| ErrorCode                 | Supported Error Code emitted by caller (e.g. "FO002")                                        | 
| MetricName                | Name of the metric (e.g., CpuPercent or MemoryMB, etc.)                                      |   
| MetricValue               | Corresponding Metric Value (e.g. "85" indicating 85% CPU usage)                              | 
| OS                        | The name of the OS from which the data was collected (Linux or Windows)                      |
| ProcessName               | The name of the service process                                                              |
| ProcessStartTime          | The time (UTC) the process was created on the machine                                        |
| HealthState               | The HealthState of the target entity: Error or Warning                                       |
| Source                    | The Source ID of the related SF Health Event                                                 |
| Property                  | The Property of the related SF Health Event                                                  |
 
#### Application-related Metric Names.
| Name                      |                                                                               
|---------------------------| 
| ActiveTcpPorts            |                                        
| CpuPercent                |    
| EphemeralPorts            | 
| EphemeralPortsPercent     | 
| EndpointUnreachable*      |   
| MemoryMB                  | 
| MemoryPercent             | 
| Handles                   | 
| HandlesPercent            | 
| Threads                   | 

#### Currently supported repair predicates for Application service level repair.
| Name                      | Definition                                                                               
|---------------------------|---------------------------------------------------------------------------------------------------------------|
| RestartCodePackage        | Safely ends the code package process, which restarts all of the user service replicas hosted in that process. |
| RestartReplica            | Restarts the offending stateful/stateless service replica without safety checks.   


#### Disk logic rules  
**Disk repair requires facts from FabricObserver or FHProxy. FH must be present on the node where disk operations are to be done.**

#### Applicable Named Arguments present in Mitigate. Facts are supplied by FabricObserver, FHProxy or FH itself.
| Argument Name             | Definition                                                             |
|---------------------------|------------------------------------------------------------------------|
| NodeName                  | Name of the node                                                       |
| NodeType                  | Type of node                                                           |
| ErrorCode                 | Supported Error Code emitted by caller (e.g. "FO002")                  |
| MetricName                | Name of the Metric  (e.g., CpuPercent or MemoryMB, etc.)               |
| MetricValue               | Corresponding Metric Value (e.g. "85" indicating 85% CPU usage)        |
| OS                        | The name of the OS where FabricHealer is running (Linux or Windows)    |
| HealthState               | The HealthState of the target entity: Error or Warning                 |
| Source                    | The Source ID of the related SF Health Event                           |
| Property                  | The Property of the related SF Health Event                            |

#### Disk-related Metric Names.
| Name                      |                                                                                 
|---------------------------|
| DiskSpacePercent          |                                       
| DiskSpaceMB               |  
| FolderSizeMB              |

#### Currently supported repair predicates for use in Disk repair rules.
| Name                      | Definition                                                                               
|---------------------------|----------------------------------------------------------------------------|
| DeleteFiles               | Deletes files in a specified directory (full path) with optional arguments.|


#### System service logic rules 
**System service repair requires facts from FabricObserver or FHProxy. FH must be present on the node where process operations are to be done.**

#### Applicable Named Arguments for Mitigate. Corresponding data is supplied by FabricObserver, Renamed for brevity by FH.
| Argument Name             | Definition                                                                                                   |
|---------------------------|--------------------------------------------------------------------------------------------------------------|
| AppName                   | Name of the SF System Application entity. This is always "fabric:/System"                                    |
| NodeName                  | Name of the node                                                                                             | 
| NodeType                  | Type of node                                                                                                 |  
| ErrorCode                 | Supported Error Code emitted by caller (e.g. "FO002")                                                        | 
| MetricName                | Name of the Metric (e.g., CpuPercent or MemoryMB, etc.)                                                      |   
| MetricValue               | Corresponding Metric value (e.g. "85" indicating 85% CPU usage)                                              | 
| ProcessName               | The name of a Fabric system service process supplied in TelemetryData instance                               | 
| ProcessStartTime          | The time (UTC) the process was created on the machine                                                        |
| OS                        | The name of the OS from which the data was collected (Linux or Windows)                                      |
| HealthState               | The HealthState of the target entity: Error or Warning                                                       |
| Source                    | The Source ID of the related SF Health Event                                                                 |
| Property                  | The Property of the related SF Health Event                                                                  |

#### System Service-related Metric Names.
| Name                      |                                                                                    
|---------------------------|
| ActiveTcpPorts            |                                         
| CpuPercent                |    
| EphemeralPorts            |     
| MemoryMB                  | 
| FileHandles               | 
| FileHandlesPercent        | 
| Threads                   |

#### Currently supported repair predicates for System Service level repair.
| Name                       | Definition                                                                      |       
|----------------------------|---------------------------------------------------------------------------------|
| RestartFabricSystemProcess | Restarts the offending stateful/stateless service replica without safety checks.|
| RestartFabricNode          | Restarts the target Fabric Node.                                                |


#### Machine logic rules
**Machine repair does not require facts from FabricObserver or FHProxy. FH can be deployed to a single or multiple nodes. All VM level repairs are executed by InfrastructureService, not FabricHealer. FabricHealer only schedules machine repairs.**

##### Applicable Named Arguments for Mitigate. Facts are supplied by FabricObserver, FHProxy or FH itself.
Any argument below with (FO/FHProxy) means that *only* FabricObsever or FHProxy will present the fact. Else, FH will provide them.

| Argument Name             | Definition                                                             |
|---------------------------|------------------------------------------------------------------------|
| NodeName                  | Name of the node                                                       |
| NodeType                  | Type of node                                                           |
| ErrorCode (FO/FHProxy)    | Supported Error Code emitted by caller (e.g. "FO002")                  |
| MetricName (FO/FHProxy)   | Name of the Metric  (e.g., CpuPercent or MemoryMB, etc.)               |
| MetricValue (FO/FHProxy)  | Corresponding Metric Value (e.g. "85" indicating 85% CPU usage)        |
| OS                        | The name of the OS where FabricHealer is running (Linux or Windows)    |
| HealthState               | The HealthState of the target entity: Error or Warning                 |
| Source                    | The Source ID of the related SF Health Event                           |
| Property                  | The Property of the related SF Health Event                            |

#### Metric Names, from FO or FHProxy.
| Name                           |                                           
|--------------------------------|
| ActiveTcpPorts                 |                  
| CpuPercent                     |
| EphemeralPorts                 |
| EphemeralPortsPercent          |
| MemoryMB                       |
| MemoryPercent                  |
| Handles (Linux-only)           |
| HandlesPercent (Linux-only)    |

#### Currently supported repair predicates for Machine (VM) level repair.
| Name                       | Definition                                                                      |       
|----------------------------|---------------------------------------------------------------------------------|
| ScheduleMachineRepair      | Schedules an infrastructure repair task, requires a supported Repair Action     |
| DeactivateFabricNode       | Deactivates a target Fabric node, with optional specified ImpactLevel           |