# Using FabricHealer - Scenarios

FabricHealer ships with [several logic rules](/FabricHealer/PackageRoot/Config/LogicRules) that form the basis of repair workflow. You just need to modify existing rules to quickly get going. To learn how to create your own logic rules, click [here](LogicWorkflows.md).

**You can enable/disable repairs for target types (e.g., Application, Node, VM, Disk) by setting values (true or false) in the Repair Policies Enablement section of ApplicationManifest.xml.**  

**Note:** For local testing on your dev machine, you must add RepairManager service to your local SF dev cluster configuration file (C:\SFDevCluster\Data\clusterManifest.xml) and then run a node configuration update that points to the location of the updated clusterManifest.xml file:  

```XML
    <Section Name="RepairManager">
      <Parameter Name="MinReplicaSetSize" Value="1" />
      <Parameter Name="TargetReplicaSetSize" Value="1" />
    </Section>
```

Run  

```PowerShell
Update-ServiceFabricNodeConfiguration -ClusterManifestPath C:\SFDevCluster\Data\clusterManifest.xml
```
The cluster will be rebuilt and the RepairManager service will be added to the System services. Then, you can experiment with FH locally in the way that it will work on an actual cluster in the cloud.  

### Scenarios - Service Repair


***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning (as a percentage of total memory) for any user application (not SF system apps) in my cluster.

***Solution***: We can use the predefined "RestartCodePackage" repair action.

In PackageRoot/Config/LogicRules/AppRules.guan, scroll to the Memory section and add:

```
Mitigate(MetricName="MemoryPercent") :- RestartCodePackage().
```

**Please note that ## is how comment lines are specified in FabricHealer's logic rules. They are not block comments and apply to single lines only.** 
```
 ## this is a comment on one line. I do not span
 ## lines. See? :)
```

In the repair rules files, you will see GetRepairHistory. This is an *external* predicate. That is, it is not a Guan system predicate (implemented in the Guan runtime) or internal predicate (which only exists within and as part of the rule - it has no backing implementation): it is user-implemented; 
look in the [FabricHealer/Repair/Guan](/FabricHealer/Repair/Guan) folder to see all external predicate impls.  

GetRepairHistory takes a TimeSpan formatted value (e.g., xx:xx:xx) as the only input, and has one output variable, ?repairCount, which will hold the value computed by the predicate. The TimeSpan argument represents the span of time in which
Completed repairs have occurred for the repair type (in this case App level repairs for an application named "fabric:/System"). ?repairCount can then be used in subsequent logic within the same rule (not all rules in the file,
just the rule that it is a part of). You can see a more advanced approach in the [AppRules](/FabricHealer/PackageRoot/Config/LogicRules/AppRules.guan) and [SystemAppRules](/FabricHealer/PackageRoot/Config/LogicRules/SystemServiceRules.guan) files where rather than having each rule run the same check, a convenience internal predicate is used that takes arguments.

Repair type is implicitly or explicitly specified in the query. Implicitly, FH already knows the context internally when this rule is run since it gets the related information from FabricObserver's
health report, passing each metric as a default argument available to the query (Mitigate, in this case). To be clear, in the above example, AppName is one of the default named arguments available to Mitigate and it's corresponding
value is passed from FabricObserver in  health report data (held within a serialized instance of TelemetryData type). Learn more [here](LogicWorkflows.md). 
Here, we use the named argument expression, AppName to say "when the app name is \"fabric:/System\"".

***IMPORTANT: Whenever you use arithmetic operators inside a string that is not mathematical in nature (so, a forward slash, for example), you must "quote" the value.
If you do not do this, then Guan will assume you want it do some arithmetic operation with the value, which in the case of something like "fabric:/System"
or "fabric:/MyApp42" you certainly do not want.***

***Problem***: I want to specify different repair actions for different applications.

***Solution***:
```
Mitigate(AppName="fabric:/SampleApp1") :- RepairApp1().  
Mitigate(AppName="fabric:/SampleApp2") :- RepairApp2().  
RepairApp1() :- ...
RepairApp2() :- ...
```

Here, ```RepairApp1()``` and ```RepairApp2()``` are custom rules, the above workflow can be read as follows: If ```?AppName``` is equal to ```SampleApp1``` then we want to invoke the rule named ```RepairApp1```. From there we would execute the ```RepairApp1``` rule just like we would for any other rule like ```Mitigate```.  


***Problem***: I want to check the observed value for the supplied resource metric (Cpu, Disk, Memory, etc.) and ensure the we are within the specified run interval before running the RestartCodePackage repair on any app service that FabricObserver is monitoring.

***Solution***:
```
## First, check if we are inside run interval. If so, then cut (!).
Mitigate() :- CheckInsideRunInterval(02:00:00), !.

## CPU Time - Percent
Mitigate(MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80, 
	GetRepairHistory(?repairCount, 01:00:00), 
	?repairCount < 5,
	RestartCodePackage().
```
***Problem***: I want to check the value for the supplied resource metric (CpuPercent) and ensure that the repair for the target app has not run more than 5 times in the last 1 hour before running the RestartCodePackage repair on any service belonging to the specified app.

***Solution***:
```
## CPU Time - Percent
Mitigate(AppName="fabric:/MyApp42", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80, 
	GetRepairHistory(?repairCount, 01:00:00), 
	?repairCount < 5,
	RestartCodePackage().
```

***Problem***: I want to check the value for the supplied resource metric (CpuPercent) and ensure that the usage is non-transient - that FabricObserver has generated at least 3 health reports for this issue in a 15 minute time span - before running the RestartCodePackage repair on any service belonging to the specified app.

***Solution***: 
```
## Try to mitigate an SF Application in Error or Warning named fabric:/MyApp42 where one of its services is consuming too much CPU (as a percentage of total CPU) 
## and where at least 3 health events identifying this problem were produced in the last 15 minutes. This is useful to ensure you don't mitigate a transient (short-lived)
## problem as they will self-correct.
Mitigate(AppName="fabric:/MyApp42", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80, 
	GetHealthEventHistory(?HealthEventCount, 00:15:00),
	?HealthEventCount >= 3,
	TimeScopedRestartCodePackage(4, 01:00:00).
```  


***Problem***: I want to limit how long a specific repair can run (set an end date).

***Solution***: 

time() and DateTime() are Guan functions (system functions) that can be used in combination for exactly this purpose.
time() with no arguments returns DateTime.UtcNow. DateTime will return a DateTime object that represents the supplied datetime string. 
***Note***:  you must wrap the date string in quotes to make it explicit to Guan that the arg is a string as it contains mathematical operators (in this case a /).

```
## The rule below reads: If any of the specified (set in Mitigate) app's service processes have put it into Warning due to CPU
## over-consumption and today's date is later than the supplied end date, emit a message, stop processing rules (!).

Mitigate(AppName="fabric:/CpuStress", MetricName="CpuPercent") :- time() > DateTime("11/30/2021"),
	EmitMessage("Exceeded specified end date for repair of fabric:/MyApp CpuPercent usage violations. Target end date: {0}. Current date (Utc): {1}", DateTime("11/30/2021"), time()), !.

## Alternatively, you could enforce repair end dates inline (as a subgoal) to any rule, e.g.,

Mitigate(AppName="fabric:/PortEater42", MetricName="EphemeralPorts", MetricValue=?MetricValue) :- time() < DateTime("11/30/2021"),
	?MetricValue >= 8500,
	TimeScopedRestartCodePackage(4, 01:00:00).
```  


### Scenarios - Disk Repair (Note: Requires related facts from FabricObserer service)

**FabricHealer needs to be deployed to each node in the cluster for this type of mitigation to work and only facts provided by FabricObserver service are supported today**. 


***Problem***: I want to delete files in multiple directories when a Disk Warning is supplied by FabricObserver

***Solution***: 

If FabricObserver generates a Warning related to disk space usage (which, of course, you configured in FO), then try and delete files
in the following set of directory paths (note that using member predicate in a rule enables iteration through a collection):

```
Mitigate(MetricName=?MetricName) :- match(?MetricName, "DiskSpace"), GetRepairHistory(?repairCount, 08:00:00), 
	?repairCount < 5,
	member(config(?X,?Y), [config("D:\SvcFab\Log\Traces", 50), config("C:\fabric_observer_logs", 10), config("E:\temp", 10)]), 
	CheckFolderSize(?X, MaxFolderSizeGB=?Y),
	DeleteFiles(?X, SortOrder=Ascending, MaxFilesToDelete=10, RecurseSubdirectories=true).
```

Note that in the above rule, the value you specify for the ?Y variable in config(?X,?Y) is GB size unit (MaxFolderSizeGB). You could instead choose to use MaxFolderSizeMB, which
means ?Y values are MB size unit.


***Problem***: I want to delete files in multiple directories when a Disk Warning is supplied by FabricObserver related to FO's Folder Size monitoring feature
(DiskObserver's DiskObserverEnableFolderSizeMonitoring setting and related settings (which employ MB thresholds only, so employ MaxFolderSizeMB in logic rule for ?Y value size unit). 


***Solution***: 

```
Mitigate(MetricName=?MetricName) :- match(?MetricName, "FolderSizeMB"), GetRepairHistory(?repairCount, 08:00:00), 
	?repairCount < 8,
	member(config(?X,?Y), [config("D:\SvcFab\Log\Traces", 4096), config("C:\fabric_observer_logs", 500), config("E:\temp", 1024)]), 
	CheckFolderSize(?X, MaxFolderSizeMB=?Y),
	DeleteFiles(?X, SortOrder=Ascending, MaxFilesToDelete=10, RecurseSubdirectories=true).
```

If you want to just specify a single directory, then you could do something like this: 

```
## Constrain on folder size Error or Warning code (facts from FabricObserver).
Mitigate(ErrorCode=?ErrorCode) :- ?ErrorCode == "FO042" || ?ErrorCode == "FO043", GetRepairHistory(?repairCount, 08:00:00),
	?repairCount < 4,
	CheckFolderSize("C:\fabric_observer_logs", MaxFolderSizeMB=250),
	DeleteFiles("C:\fabric_observer_logs", SortOrder=Ascending, MaxFilesToDelete=5, RecurseSubdirectories=true).
```

If you want to constrain on specific file types within a directory, then you could do something like this (only delete files with .dmp extension, for example): 

```
## Constrain on folder size Error or Warning code (facts from FabricObserver).
Mitigate(ErrorCode=?ErrorCode) :- ?ErrorCode == "FO042" || ?ErrorCode == "FO043", GetRepairHistory(?repairCount, 08:00:00),
	?repairCount < 4,
	CheckFolderSize("C:\fabric_observer_logs", MaxFolderSizeMB=250),
	DeleteFiles("C:\fabric_observer_logs", SortOrder=Ascending, MaxFilesToDelete=5, RecurseSubdirectories=true, SearchPattern="*.dmp").
```



### Debugging/Auditing Rules 

There are two ways to have FabricHealer audit logic rules: 

- Global Repair Predicate tracing - EnableLogicRuleTracing application parameter setting (boolean). If this is enabled, then FabricHealer will trace/log the entire rule
that is currently executing if it contains a Repair predicate (RestartCodePackage, ScheduleMachineRepair, RestartReplica, DeleteFiles, etc.).

- LogRule Helper predicate - you can add LogRule predicate to rules you want FH to trace/log. Any rule that contains this predicate will be logged in its entirety, which is 
extremely useful for debugging/auditing purposes. LogRule predicate requires a line number argument: e.g., LogRule(42) means log the entire rule that starts on line 42.

**If you set EnableLogicRuleTracing to true and write rules that employ the same repair predicate (and arguments), then you must specify LogRule predicate in each of these rules.** 

This is an example of using LogRule in machine-level repair rules where the end goal (the repair predicate) is exactly the same for each rule. If you also have EnableLogicRuleTracing set
to true, then no logging will take place for the rule(s) that do not employ a LogRule predicate. You can see below that the first rule does not specify LogRule. In the case where EnableLogicRuleTracing
is set to true, the first rule will not be traced by FabricHealer.

```
Mitigate(Source=?source, Property=?property) :- match(?source, "SomeWatchdog"), match(?property, "SomeMachineFailure"),
	DeactivateFabricNode(ImpactLevel=RemoveData).

Mitigate(Source=?source, Property=?property) :- LogRule(65), match(?source, "SomeWatchdog"), match(?property, "SomeOtherMachineFailure"),
	DeactivateFabricNode(ImpactLevel=RemoveData).

Mitigate(Source=?source, Property=?property) :- LogRule(71), match(?source, "SomeOtherWatchdog"), match(?property, "AnotherMachineFailureType"),
	DeactivateFabricNode(ImpactLevel=RemoveData).
``` 

The correct way to specify rule logging in the rules above is like this: 

```
Mitigate(Source=?source, Property=?property) :- LogRule(59), match(?source, "SomeWatchdog"), match(?property, "SomeMachineFailure"),
	DeactivateFabricNode(ImpactLevel=RemoveData).

Mitigate(Source=?source, Property=?property) :- LogRule(65), match(?source, "SomeWatchdog"), match(?property, "SomeOtherMachineFailure"),
	DeactivateFabricNode(ImpactLevel=RemoveData).

Mitigate(Source=?source, Property=?property) :- LogRule(71), match(?source, "SomeOtherWatchdog"), match(?property, "AnotherMachineFailureType"),
	DeactivateFabricNode(ImpactLevel=RemoveData).
```

When Guan is parsing/executing the specfied goals in the rule, it will first call LogRule, as specified in the rules above, which will generate a telemetry event
that will look like this: 

```
Executing logic rule 'Mitigate(Source=?source, Property=?property) :- LogRule(59), match(?source, "SomeWatchdog"), match(?property, "SomeMachineFailure"), DeactivateFabricNode(ImpactLevel=RemoveData)' 
Executing logic rule 'Mitigate(Source=?source, Property=?property) :- LogRule(65), match(?source, "SomeWatchdog"), match(?property, "SomeOtherMachineFailure"), DeactivateFabricNode(ImpactLevel=RemoveData)' 
Executing logic rule 'Mitigate(Source=?source, Property=?property) :- LogRule(71), match(?source, "SomeOtherWatchdog"), match(?property, "AnotherMachineFailureType"), DeactivateFabricNode(ImpactLevel=RemoveData)' 
```

This makes it really easy to spot rules that are leading to unintended consequences due to some user error in its specification. If a rule is malformed or not legitimate, then
that problem will surface to you well before LogRule would run (as a GuanException with the related details in the error output).

Please look through the [existing rules files](/FabricHealer/PackageRoot/Config/LogicRules) for real examples that have been tested. Simply modify the rules to meet your needs (like supplying your target app names, for example, and adjusting the simple logical constraints, if need be). 

### Application Parameter-Only Application Upgrades 

Most of the important settings employed by FabricHealer our housed in ApplicationManifest.xml as Application Parameters. You can change these settings without redeploying FabricHealer by conducting parameter-only, versionless application upgrades. 

For example: 

``` PowerShell

$appName = "fabric:/FabricHealer"
$appVersion = "1.2.15"

$myApplication = Get-ServiceFabricApplication -ApplicationName $appName
$appParamCollection = $myApplication.ApplicationParameters

# Fill the map with *existing* app parameter settings.
$applicationParameterMap = @{}

foreach ($pair in $appParamCollection)
{
    $applicationParameterMap.Add($pair.Name, $pair.Value);
}

# If replacing *existing* app parameter(s), remove them  from the list of current params first.
if ($applicationParameterMap.ContainsKey("HealthCheckIntervalInSeconds"))
{
    $applicationParameterMap.Remove("HealthCheckIntervalInSeconds");
}

# Add the updated target app parameter(s) to the collection.
$applicationParameterMap.Add("HealthCheckIntervalInSeconds","90")

# UnmonitoredAuto is fine here. FH manages these settings changes internally and there is no impact on service 
# health for this type of "upgrade" (no code is being updated, process is not going to go down).
Start-ServiceFabricApplicationUpgrade -ApplicationName $appName -ApplicationTypeVersion $appVersion -ApplicationParameter $applicationParameterMap -UnmonitoredAuto

``` 

### Stopping FabricHealer processing with a Repair Job 

You can stop FabricHealer from doing any work by creating a custom repair task (CustomAction = FabricHealer.Stop).
You can then re-enable FabricHealer by canceling said repair task. Note that that this does not stop the FabricHealer service process. 
It just prevents FabricHealer from doing any work. FabricHealer service entity will go into Warning so that you don't forget about this.
Warning health state has no real impact on the service nor the cluster.

Example (you only need to specify a single node name): 

To stop FabricHealer processing: 

``` PowerShell
Start-ServiceFabricRepairTask -NodeNames _Node_0 -CustomAction FabricHealer.Stop
``` 

To restart FabricHealer processing, Stop the repair task, specifying the TaskId for the FabricHealer.Stop repair task you created above: 

``` PowerShell
# the specified TaskId below is just an example of what the default TaskId pattern will look like (FabricClient/[guid])
Stop-ServiceFabricRepairTask -TaskId FabricClient/b58c07ae-82fb-4c45-9486-0a1400f34a46
``` 



