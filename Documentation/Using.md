# Using FabricHealer - Scenarios

FabricHealer ships with [several logic rules](/FabricHealer/PackageRoot/Config/Rules) that form the basis of repair workflow. You just need to modify existing rules to quickly get going. To learn how to create your own logic rules, click [here](LogicWorkflows.md).

**Application Memory Usage Warning -> Trigger Code Package Restart**

***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning (as a percentage of total memory) for any user application (not SF system apps) in my cluster.

***Solution***: We can use the predefined "RestartCodePackage" repair action.

In PackageRoot/Config/Rules/AppRules.config.txt, scroll to the Memory section and add:

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
Mitigate(MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80, 
	GetRepairHistory(?repairCount, TimeWindow=01:00:00), 
	?repairCount < 5,
	RestartCodePackage().
```
***Problem***: I want to check the value for the supplied resource metric (CpuPercent) and ensure that the repair for the target app has not run more than 5 times in the last 1 hour before running the RestartCodePackage repair on any service belonging to the specified app.

***Solution***:
```
## CPU Time - Percent
Mitigate(AppName="fabric:/MyApp42", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 80, 
	GetRepairHistory(?repairCount, TimeWindow=01:00:00), 
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
	GetHealthEventHistory(?HealthEventCount, TimeRange=00:15:00),
	?HealthEventCount >= 3,
	TimeScopedRestartCodePackage(4, 01:00:00).
```  


Please look through the [existing rules files](/FabricHealer/PackageRoot/Config/Rules) for real examples that have been tested. Simply modify the rules to meet your needs (like supplying your target app names, for example, and adjusting the simple logical constraints, if need be).
