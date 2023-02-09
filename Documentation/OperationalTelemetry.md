## FabricHealer Operational Telemetry

FabricHealer (FH) operational data is transmitted to Microsoft and contains information about FabricHealer.  This information helps us understand how FabricHealer is doing in the real world, what type of environment it runs in, and how many, if any, successful or unsuccessful repairs it conducts. This information will help us make sure we invest time in the right places. This data does not contain PII or any information about the services being repaired in your cluster. 

**This information is only used by the Service Fabric team and will be retained for no more than 90 days.** 

Disabling / Enabling transmission of Operational Data: 

Transmission of operational data is controlled by a setting and can be easily turned off. ```EnableOperationalTelemetry``` setting in ```ApplicationManifest.xml``` controls transmission of Operational data. **Note that if you are deploying FabricHealer to a cluster running in a restricted region (China) or cloud (Gov) you should disable this feature before deploying to remain compliant. Please do not send data outside of any restricted boundary.**

Setting the value to false as below will prevent the transmission of operational data: 

**\<Parameter Name="EnableOperationalTelemetry" DefaultValue="false" />** 

As with most of FabricHealer's application settings, you can also do this with a versionless parameter-only application upgrade: 

```Powershell
Connect-ServiceFabricCluster ...

$appParams = @{ "EnableOperationalTelemetry" = "false"; }
Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/FabricHealer -ApplicationParameter $appParams -ApplicationTypeVersion 1.0.10 -UnMonitoredAuto
 
```

#### Questions we want to answer from data: 

-	Health of FH 
       -	If FH crashes with an unhandled exception that can be caught, related error information will be sent to us (this will include the offending FH stack). This will help us improve quality. 
-	Enabled Repairs 
    -	Helps us focus effort on the most useful repairs.
-	Is FH successfully repairing issues? This data is represented in the total number of SuccessfulRepairs FH conducts in a 24 hour window.
-	This telemetry is sent once every 24 hours and internal data counters are reset after each telemetry transmission.

#### Operational data details: 

Here is a full example of exactly what is sent in one of these telemetry events, in this case, from an SFRP cluster: 

```JSON
{
  "EventName": "OperationalEvent",
  "TaskName": "FabricHealer",
  "EventRunInterval": "1.00:00:00",
  "SFRuntimeVersion": "9.0.1048.9590",
  "ClusterId": "00000000-1111-1111-0000-00f00d000d",
  "ClusterType": "SFRP",
  "NodeNameHash": "3e83569d4c6aad78083cd081215dafc81e5218556b6a46cb8dd2b183ed0095ad",
  "FHVersion": "1.1.15",
  "UpTime": "00:00:00.2164523",
  "Timestamp": "2023-02-07T21:45:25.2443014Z",
  "OS": "Windows",
  "EnabledRepairCount": 2,
  "TotalRepairAttempts": 4,
  "SuccessfulRepairs": 4,
  "FailedRepairs": 0
}
```

Let's take a look at the data and why we think it is useful to share with us. We'll go through each object property in the JSON above.
-	**EventName** - this is the name of the telemetry event.
-	**TaskName** - this specifies that the event is from FabricHealer.
-	**EventRunInterval** - this is how often this telemetry is sent from a node in a cluster.
-   **SFRuntimeVersion** - this is the Service Fabric runtime version installed on the machine.
-	**ClusterId** - this is used to both uniquely identify a telemetry event and to correlate data that comes from a cluster.
-	**ClusterType** - this is the type of cluster: Standalone or SFRP.
-	**NodeNameHash** - this is a sha256 hash of the name of the Fabric node from where the data originates. It is used to correlate data from specific nodes in a cluster (the hashed node name will be known to be part of the cluster with a specific cluster id).
-	**FHVersion** - this is the internal version of FH (if you have your own version naming, we will only know what the FH code version is (not your specific FH app version name)).
-	**UpTime** - this is the amount of time FH has been running since it last started.
-	**Timestamp** - this is the time, in UTC, when FH sent the telemetry.
-	**OS** - this is the operating system FH is running on (Windows or Linux).
-   **EnabledRepairs** - this is the number of enabled repairs.
-   **TotalRepairAttempts** - this is the number of times repairs were attempted.
-   **SuccessfulRepairs** - this is the number of successful repairs.
-   **FailedRepairs** - this is the number of failed repairs.


If the ClusterType is not SFRP then a TenantId (Guid) is sent for use in the same way we use ClusterId. 

This information will **really** help us understand how FabricHealer is doing out there and we would greatly appreciate you sharing it with us!


