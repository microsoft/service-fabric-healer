## Extending Repair Workflows with Logic Programming
FabricHealer employs configuration-as-logic by leveraging the expressive power of [Guan](https://github.com/microsoft/guan), a general-purpose logic programming system composed of a C# API and logic interpreter/query executor. It enables Prolog style syntax for writing logic rules and executing queries over them. Guan enables FabricHealer's configuration-as-logic model for defining execution workflows for automatic repairs in Service Fabric clusters (Windows and Linux).

**Why?**

Supporting formal logic-based repair workflows gives users more tools and options to express their custom repair workflows. Formal logic gives users the power to express concepts like if/else statements, leverage boolean operators, and even things like recursion! Logic programming allows users to easily and concisely express complex repair workflows that leverage the complete power of a logic programming language. We use GuanLogic for our underlying logic processing, which is a general purpose logic programming API written by Lu Xun (Microsoft) that enables Prolog-like (https://en.wikipedia.org/wiki/Prolog) rule definition and query execution in C#.  


While not necessary, reading Chapters 1-3 of the [learnprolognow](http://www.learnprolognow.org/lpnpage.php?pagetype=html&pageid=lpn-htmlch1) book can be quite useful. Note that the documentation here doesn't assume you have any experience with logic programming.

**Do I need experience with logic programming?**

No, using logic to express repair workflows is easy! One doesn't need a deep knowledge and understanding of logic programming to write their own complex repair workflows! Let's start with an example to help inspire your own logic-based repair workflows!


***Problem***: I want to perform a code package restart if FabricObserver emits a memory usage warning for a *specific* application in my cluster (e.g. "fabric:/App1"). 

***Solution***: We can leverage Guan and its built-in equals operator for checking the name of the application that triggered the warning against the name of the application for which we decided we want to perform a code package restart for. For application level health events, the repair workflow is defined inside the PackageRoot/Config/Rules/AppRules.config.txt file. Here is that we would enter:

```
Mitigate(AppName=?AppName) :- ?AppName == "fabric:/App1", RestartCodePackage(MaxRepairs=5, MaxTimeWindow=1:00:00).
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

Here is a list of currently implemented External Predicates:

**External Predicates**

```RestartCodePackage(long MaxRepairs, TimeSpan MaxTimeWindow)``` 

Attempts to restart the code package for the service that emitted the health event, returns true if successful, else false.  

```RestartFabricNode(long MaxRepairs, TimeSpan MaxTimeWindow, optional string SafeRestart)```

Attempts to restart the node of the service that emitted the health event, returns true if successful, else false. Takes an optional Safe parameter: "safe" or "unsafe" which defines whether or not to perform a safe or unsafe node restart. A safe node restart will try to first deactivate the node before restarting, whereas an unsafe node restart will try restarting the node without first trying to deactivate it. 

```RestartReplica()``` 

Attempts to restart the replica of the service that emitted the health event, returns true if successful, else false. 

```RestartVM()``` 

Attempts to restart the underlying virtual machine of the service that emitted the health event, returns true if successful, else false.

**Forming a Logic Repair Workflow**

Now that we know what predicates are, let's learn how to form a logic repair workflow. 

A GuanLogic program is expressed in terms of **rule(s)** and a program is executed by running a **query** over these rule(s). A rule is of the form: 

```RuleHead(*args, ...*) :- PredicateA(), PredicateB(), ...```. 


A simple way to understand what a rule is, is to just think of it as a function.
```
RuleHead(*args, ...*) -> Function signature (name + arguments)
PredicateA(), PredicateB(), ... -> Function body (everything to the right of the ":-" is part of the function body)
```

A query is simply a way to invoke a rule (function). 
```RuleHead() -> invokes the rule of name "RuleHead" with the same number of arguments``` 

By default, for logic-based repair workflows, FH will execute a query which calls a rule named ```Mitigate()```. Think of ```Mitigate()``` as the root for executing the repair workflow, similar to the Main() function in most programming languages. By default, ```Mitigate()``` passes arguments that can be passed to predicates part of the repair workflow.

| Argument Name             | Definition                                                                                   |
|---------------------------|----------------------------------------------------------------------------------------------|
| AppName                   | Name of the SF application, format is fabric:/SomeApp                                        |
| ServiceName               | Name of the SF service, format is fabric:/SomeApp/SomeService                                |
| NodeName                  | Name of the node                                                                             | 
| NodeType                  | Type of node                                                                                 |  
| PartitionId               | Id of the partition                                                                          |
| ReplicaOrInstanceId       | Id of the replica or instance                                                                |
| FOErrorCode               | Error Code emitted by FO (e.g. "FO002")                                                      | 
| MetricName                | Name of the metric emitted by FO (e.g., CpuPercent or MemoryMB, etc.)                        |   
| MetricValue               | Metric Value emitted by FO (e.g. "85" indicating 85% CPU usage)                              | 

For example if you wanted to use AppName and ServiceName in your repair workflow you would specify them like so:
```
Mitigate(AppName=?x, ServiceName=?y) :- ..., ?x == "fabric:/App1", ...
now the variable ?x is bound to the application name and ?y is bound to the service name. You can name variables whatever you want
```

Here's a simple example that we've seen before:

```
Mitigate() :- RestartCodePackage(MaxRepairs=5, MaxTimeWindow=1:00:00).
```

Essentially, what this logic repair workflow (mitigation scenario) is describing is that if FO emits a health event that falls under the AppServiceCpuMemoryPortAbuseRepairPolicy and if the repair policy is enabled, then we will execute the repair action. FH will automatically detect that it is a logic workflow, so it will invoke the root rule ```Mitigate()```. Guan determines that the ```Mitigate()``` rule is defined inside the repair action, where it then will try to execute the body of the ```Mitigate()``` rule.

Users can define multiple rules (separated by a newline) as part of a repair workflow, here is an example:

```
Mitigate() :- RestartCodePackage(MaxRepairs=5, MaxTimeWindow=1:00:00).
Mitigate() :- RestartFabricNode(MaxRepairs=5, MaxTimeWindow=1:00:00).
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
Mitigate() :- not((condition A T/F)), (true branch B).
Can be read as: if (!A) then goto B
```

NOT behaviour is achieved by wrapping any predicate inside ```not()``` which is a built-in GuanLogic predicate.


**AND**
```
Mitigate() :- (condition A T/F), (condition B T/F), (true branch C).
Can be read as: if (A and B) then goto C
```

AND behaviour is achieved by separating predicates with commas, similar to programming with the ```||``` character.

**OR**
```
Mitigate() :- (condition A T/F), (true branch C).
Mitigate() :- (condition B T/F), (true branch C).
Can be read as: if (A or B) then goto C
```

OR behaviour is achieved by separating predicates by rule. Here is the execution flow for the above workflow: Go to first ```Mitigate()``` rule -> does predicate A succeed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. We find the second ```Mitigate()``` rule -> does predicate B suceed? If so, continue with branch C, if it fails look for the next ```Mitigate()``` rule if it exists. There are no more ```Mitigate()``` rules so the workflow is over.

**Conditional Branches in Logic Programming**

An if/else conditional branch can be constructed with the following rule pattern:
```
Mitigate() :- (condition T/F), !, (true branch). 
Mitigate() :- (false branch).
```

Notice the ```!``` symbol, this is called a cut operator in Prolog and it essentially prevents backtracking past where it is defined. Consider the case where the conditional check succeeds, the execution will continue towards the ```(true branch)```. However if the ```(true branch)``` returns false, the first rule will fail and Guan will "backtrack" try to execute the second rule, so the execution flow will actually end up in the ```(false branch)``` of the second rule. Clearly this is not how traditional if/else conditionals work, so it is important to understand why we need to use the cut operator. However as long as you understand this concept you may remove the cut operator if that is the type of behaviour you desire.


This pattern can also be repeated to construct else if branches:
```
Mitigate() :- (condition T/F), !, (true branch). 
Mitigate() :- (condition T/F), !, (true branch). 
Mitigate() :- …  
Mitigate() :- (false branch). 
```

The above repair workflow can be read as follows: Run ```CheckRepairHistory()``` against ```RestartCodePackage```, if the number of runs does not exceed 3, then run ```RestartCodePackage()```. If it does exceed 3, then try the next rule. The second rule can read similarly as the first, same with the third. We can control the flow of execution by controlling the ordering of rules like this. Again, notice the usage of the ```!``` cut operators; the cut in the first rule is used to prevent the execution flow from dropping down to the remaining rules in the case where ```RestartCodePackage()``` fails. Since our intended behaviour here is to attempt 3 code package restarts before trying a replica and node restart.

You can check for multiple conditions aswell:
```
Mitigate() :- (condition check_1 T/F), (condition check_2 T/F), …, (condition check_N T/F), (true branch).
Mitigate() :- (false branch)
```

**Using internal predicates**

So far we've only looked at creating rules that are invoked from the root ```Mitigate()``` query, but users can also create their own rules like so:

```
Mitigate() :- MyInternalPredicate().
MyInternalPredicate() :- RestartCodePackage(MaxRepairs=5, MaxTimeWindow=1:00:00).
```

Here we've defined an internal predicate named ```MyInternalPredicate()``` and we can see that it is invoked in the body of the ```Mitigate()``` rule. In order to fulfill the ```Mitigate()``` rule, we will need to fulfill the ```MyInternalPredicate()``` predicate since it is part of the body of the ```Mitigate()``` rule. This repair workflow is identical in behaviour to one that directly calls ```RestartCodePackage()``` inside the body of ```Mitigate()```.

Using internal predicates like this is useful for improving readability and organizing complex repair workflows.

**Filtering parameters from Mitigate()**

If you wish to do equals checks such as ```?AppName == ...``` you don't actually need to write this in the body of your rules, instead you can specify these values inside Mitigate() like so:

```
Mitigate(AppName="fabric:/App1") :- ...
```

What that means, is that the rule will only execute when the AppName is equal to "fabric:/App1". This is equivalent to the following:

```
Mitigate(AppName=?AppName) :- ?AppName == "fabric:/App1", ...
```

Obviously, the first way of doing it is more succinct.
