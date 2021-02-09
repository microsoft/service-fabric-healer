# Using FabricHealer - Scenarios

To learn how create your own GuanLogic repair workflows, click [here](LogicWorkflows.md).

**Application Memory Usage Warning -> Trigger Code Package Restart**

***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning (as a percentage of total memory) for any application in my cluster.

***Solution***: We can use the predefined "RestartCodePackage" repair action.

Navigate to the PackageRoot/Config/Rules/AppRules.config.txt file and copypaste this repair workflow:

```
Mitigate(MetricName=MemoryPercent) :- RestartCodePackage().
```

**System Application CPU Usage Warning -> Trigger Fabric Node Restart**

***Problem***: I want to perform a fabric node restart if FabricObserver emits a cpu usage warning for any system application in my cluster.

***Solution***: We can use the predefined "RestartFabricNode" repair action.

Navigate to the PackageRoot/Config/Rules/SystemAppRules.config.txt file and copypaste this repair workflow:

```
## CPU Time - Percent 
Mitigate(AppName="fabric:/System", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 90,
	GetRepairHistory(?repairCount, TimeWindow=01:00:00), 
	?repairCount < 5, 
	RestartFabricNode().
```

**Please note that ## is how comment lines are specified in FabricHealer's logic rules. They are not block comments and apply to single lines only.** 
```
 ## this is a comment on one line. I do not span
 ## lines. See? :)
```

**GetRepairHistory** is an *external* predicate. That is, it is not a Guan system predicate (implemented in the Guan runtime) or internal predicate (which only exists within and as part of the rule - it has no backing implementation): it is user-implemented; 
look in the [FabricHealer/Repair/Guan](/FabricHealer/Repair/Guan) folder to see all external predicate impls.  

GetRepairHistory takes a time span formatted value as the only input, TimeWindow, and has one output variable, ?repairCount, which will hold the value computed by the predicate call. TimeWindow means the span of time in which
Completed repairs have occurred for the repair type (in this case App level repairs for an application named "fabric:/System"). ?repairCount can then be used in subsequent logic within the same rule (not all rules in the file,
just the rule that it is a part of). You can see a more advanced approach in the [AppRules](/FabricHealer/PackageRoot/Config/Rules/AppRules.config.txt) and [SystemAppRules](/FabricHealer/PackageRoot/Config/Rules/SystemAppRules.config.txt) files where rather than having each rule run the same check, a convenience internal predicate is used that takes arguments.

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
Mitigate() :- CheckInsideRunInterval(RunInterval=02:00:00), !.

## CPU Time - Percent
Mitigate(MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 20, 
	GetRepairHistory(?repairCount, TimeWindow=01:00:00), 
	?repairCount < 5,
	RestartCodePackage().
```
***Problem***: I want to check the observed value for the supplied resource metric (Cpu, Disk, Memory, etc.) and ensure the we are within the specified run interval before running the RestartCodePackage repair on any service belonging to the specified Application that FabricObserver is monitoring.

***Solution***:
```
## CPU Time - Percent
Mitigate(AppName="fabric:/MyApp42", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 20, 
	GetRepairHistory(?repairCount, TimeWindow=01:00:00), 
	?repairCount < 5,
	RestartCodePackage().
```


