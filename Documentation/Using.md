# Using FabricHealer - Scenarios

To learn how create your own GuanLogic repair workflows, click [here](LogicWorkflows.md).

**Application Memory Usage Warning -> Trigger Code Package Restart**

***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning for any application in my cluster.

***Solution***: We can use the predefined "RestartCodePackage" repair action.

Navigate to the PackageRoot/Config/Rules/AppRules.config.txt file and copypaste this repair workflow:

``` 
Mitigate() :- RestartCodePackage(MaxRepairs=5, MaxTimeWindow=1:00:00).
```


**System Application CPU Usage Warning -> Trigger Fabric Node Restart**

***Problem***: I want to perform a fabric node restart if FabricObserver emits a cpu usage warning for any system application in my cluster.

***Solution***: We can use the predefined "RestartFabricNode" repair action.

Navigate to the PackageRoot/Config/Rules/SystemAppRules.config.txt file and copypaste this repair workflow:

```
Mitigate() :- RestartFabricNode(MaxRepairs=5, MaxTimeWindow=1:00:00).
```


***Problem***: I want to specify different repair actions for different applications.

***Solution***:
```
Mitigate(AppName="fabric:/SampleApp1") :- !, RepairApp1().  
Mitigate(AppName="fabric:/SampleApp2") :- !, RepairApp2().  
RepairApp1() :- ...
RepairApp2() :- ...
```

Here, ```RepairApp1()``` and ```RepairApp2()``` are custom rules, the above workflow can be read as follows: If ```?AppName``` is equal to ```SampleApp1``` then we want to invoke the rule named ```RepairApp1```. From there we would execute the ```RepairApp1``` rule just like we would for any other rule like ```Mitigate```. The ```!``` is a cut operator and it prevents unnecessary backtracking.


***Problem***: I want to check the observed value for the supplied resource metric (Cpu, Disk, Memory, etc.) and ensure the we are within the run interval (determined by supplied MaxRepairs and MaxTimeWindow values) before running the RestartCodePackage repair on any app service that FabricObserver is monitoring.

***Solution***:
```
## CPU Time - Percent
Mitigate(MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 20, 
	GetRepairHistory(?repairCount, ?lastRunTime, RestartCodePackage), 
	?repairCount < 5,
	CheckInsideRunInterval(MaxRepairs=5, MaxTimeWindow=01:00:00, LastRunTime=?lastRunTime),
	!,
	RestartCodePackage(MaxRepairs=5, MaxTimeWindow=01:00:00).

```

***Problem***: I want to check the observed value for the supplied resource metric (Cpu, Disk, Memory, etc.) and ensure the we are within the run interval (determined by supplied MaxRepairs and MaxTimeWindow values) before running the RestartCodePackage repair on any app service belonging to the specified Application that FabricObserver is monitoring.

***Solution***:
```
## CPU Time - Percent
Mitigate(AppName="fabric:/MyApp42", MetricName="CpuPercent", MetricValue=?MetricValue) :- ?MetricValue >= 20, 
	GetRepairHistory(?repairCount, ?lastRunTime, RestartCodePackage), 
	?repairCount < 5,
	CheckInsideRunInterval(MaxRepairs=5, MaxTimeWindow=01:00:00, LastRunTime=?lastRunTime),
	!,
	RestartCodePackage(MaxRepairs=5, MaxTimeWindow=01:00:00).
```


