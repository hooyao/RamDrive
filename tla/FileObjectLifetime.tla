---------------------- MODULE FileObjectLifetime ----------------------
\* File identity is independent of pathname and user-handle lifetime.
\* A context represents the reference kept until WinFsp Close, including
\* memory mappings and delayed paging writes after Cleanup unlinks a name.
\* One dirty bit abstracts a dirty page per context; page-pool accounting
\* and three-phase writes are checked in RamDiskSystem_Lifecycle.cfg.

EXTENDS Naturals, FiniteSets, TLC

CONSTANTS NumNames, NumNodes, NumContexts, BuggyDisposeOnUnlink
ASSUME /\ NumNames > 0 /\ NumNodes > 0 /\ NumContexts > 0
       /\ BuggyDisposeOnUnlink \in BOOLEAN

Names == 1..NumNames
Nodes == 1..NumNodes
Contexts == 1..NumContexts

VARIABLES names, alive, data, contexts, userOpen, mapped, dirty,
          ioFailed, lastReadNode, lastReadData
vars == <<names, alive, data, contexts, userOpen, mapped, dirty,
          ioFailed, lastReadNode, lastReadData>>

Named(n) == \E name \in Names : names[name] = n
Refs(n) == Cardinality({c \in Contexts : contexts[c] = n})
UserRefs(n) == Cardinality({c \in Contexts : contexts[c] = n /\ userOpen[c]})

TypeOK ==
    /\ names \in [Names -> 0..NumNodes]
    /\ alive \in [Nodes -> BOOLEAN]
    /\ data \in [Nodes -> 0..NumNodes]
    /\ contexts \in [Contexts -> 0..NumNodes]
    /\ userOpen \in [Contexts -> BOOLEAN]
    /\ mapped \in [Contexts -> BOOLEAN]
    /\ dirty \in [Contexts -> BOOLEAN]
    /\ ioFailed \in BOOLEAN
    /\ lastReadNode \in 0..NumNodes
    /\ lastReadData \in 0..NumNodes

Init ==
    /\ names = [name \in Names |-> 0]
    /\ alive = [n \in Nodes |-> FALSE]
    /\ data = [n \in Nodes |-> 0]
    /\ contexts = [c \in Contexts |-> 0]
    /\ userOpen = [c \in Contexts |-> FALSE]
    /\ mapped = [c \in Contexts |-> FALSE]
    /\ dirty = [c \in Contexts |-> FALSE]
    /\ ioFailed = FALSE
    /\ lastReadNode = 0
    /\ lastReadData = 0

Create(name, n, c) ==
    /\ names[name] = 0 /\ ~alive[n] /\ Refs(n) = 0
    /\ contexts[c] = 0
    /\ names' = [names EXCEPT ![name] = n]
    /\ alive' = [alive EXCEPT ![n] = TRUE]
    /\ data' = [data EXCEPT ![n] = 0]
    /\ contexts' = [contexts EXCEPT ![c] = n]
    /\ userOpen' = [userOpen EXCEPT ![c] = TRUE]
    /\ UNCHANGED <<mapped, dirty, ioFailed, lastReadNode, lastReadData>>

Open(name, c) ==
    /\ names[name] # 0 /\ contexts[c] = 0
    /\ contexts' = [contexts EXCEPT ![c] = names[name]]
    /\ userOpen' = [userOpen EXCEPT ![c] = TRUE]
    /\ UNCHANGED <<names, alive, data, mapped, dirty, ioFailed,
                    lastReadNode, lastReadData>>

Map(c) ==
    /\ contexts[c] # 0 /\ userOpen[c] /\ ~mapped[c]
    /\ mapped' = [mapped EXCEPT ![c] = TRUE]
    /\ UNCHANGED <<names, alive, data, contexts, userOpen, dirty,
                    ioFailed, lastReadNode, lastReadData>>

Dirty(c) ==
    /\ contexts[c] # 0 /\ mapped[c]
    /\ dirty' = [dirty EXCEPT ![c] = TRUE]
    /\ UNCHANGED <<names, alive, data, contexts, userOpen, mapped,
                    ioFailed, lastReadNode, lastReadData>>

\* Namespace unlink never retargets the retained context to a recreated name.
UnlinkObject(n) ==
    /\ names' = [name \in Names |-> IF names[name] = n THEN 0 ELSE names[name]]
    /\ LET destroy == BuggyDisposeOnUnlink \/ Refs(n) = 0
       IN /\ alive' = [alive EXCEPT ![n] = IF destroy THEN FALSE ELSE @]
          /\ data' = [data EXCEPT ![n] = IF destroy THEN 0 ELSE @]

Cleanup(c, delete) ==
    /\ contexts[c] # 0 /\ userOpen[c]
    /\ ~delete \/ UserRefs(contexts[c]) = 1
    /\ userOpen' = [userOpen EXCEPT ![c] = FALSE]
    /\ IF delete THEN UnlinkObject(contexts[c])
       ELSE UNCHANGED <<names, alive, data>>
    /\ UNCHANGED <<contexts, mapped, dirty, ioFailed, lastReadNode, lastReadData>>

\* Models repeated cleanup of an old object after its name has been reused.
RepeatCleanup(c) ==
    /\ contexts[c] # 0 /\ ~userOpen[c]
    /\ UnlinkObject(contexts[c])
    /\ UNCHANGED <<contexts, userOpen, mapped, dirty, ioFailed,
                    lastReadNode, lastReadData>>

Rename(source, target) ==
    /\ source # target /\ names[source] # 0
    /\ LET victim == names[target]
           destroy == victim # 0 /\ (BuggyDisposeOnUnlink \/ Refs(victim) = 0)
       IN /\ names' = [names EXCEPT ![target] = names[source], ![source] = 0]
          /\ alive' = [n \in Nodes |-> IF n = victim /\ destroy THEN FALSE ELSE alive[n]]
          /\ data' = [n \in Nodes |-> IF n = victim /\ destroy THEN 0 ELSE data[n]]
    /\ UNCHANGED <<contexts, userOpen, mapped, dirty, ioFailed,
                    lastReadNode, lastReadData>>

Unmap(c) ==
    /\ contexts[c] # 0 /\ mapped[c]
    /\ mapped' = [mapped EXCEPT ![c] = FALSE]
    /\ UNCHANGED <<names, alive, data, contexts, userOpen, dirty,
                    ioFailed, lastReadNode, lastReadData>>

PagingWrite(c) ==
    /\ contexts[c] # 0 /\ dirty[c]
    /\ LET n == contexts[c]
       IN /\ ioFailed' = ~alive[n]
          /\ dirty' = [dirty EXCEPT ![c] = IF alive[n] THEN FALSE ELSE @]
          /\ data' = [data EXCEPT ![n] = IF alive[n] THEN n ELSE @]
    /\ UNCHANGED <<names, alive, contexts, userOpen, mapped, lastReadNode, lastReadData>>

Read(c) ==
    /\ contexts[c] # 0 /\ alive[contexts[c]]
    /\ lastReadNode' = contexts[c]
    /\ lastReadData' = data[contexts[c]]
    /\ UNCHANGED <<names, alive, data, contexts, userOpen, mapped, dirty, ioFailed>>

Close(c) ==
    /\ contexts[c] # 0 /\ ~userOpen[c] /\ ~mapped[c] /\ ~dirty[c]
    /\ LET n == contexts[c]
           destroy == ~Named(n) /\ Refs(n) = 1
       IN /\ alive' = [alive EXCEPT ![n] = IF destroy THEN FALSE ELSE @]
          /\ data' = [data EXCEPT ![n] = IF destroy THEN 0 ELSE @]
    /\ contexts' = [contexts EXCEPT ![c] = 0]
    /\ UNCHANGED <<names, userOpen, mapped, dirty, ioFailed, lastReadNode, lastReadData>>

Next ==
    \/ \E name \in Names, n \in Nodes, c \in Contexts : Create(name, n, c)
    \/ \E name \in Names, c \in Contexts : Open(name, c)
    \/ \E source \in Names, target \in Names : Rename(source, target)
    \/ \E c \in Contexts : Map(c) \/ Dirty(c) \/ Cleanup(c, FALSE) \/ Cleanup(c, TRUE)
                           \/ RepeatCleanup(c) \/ Unmap(c) \/ PagingWrite(c) \/ Read(c) \/ Close(c)

Fairness == \A c \in Contexts :
    /\ WF_vars(Cleanup(c, FALSE))
    /\ WF_vars(Unmap(c))
    /\ WF_vars(PagingWrite(c))
    /\ WF_vars(Close(c))

Spec == Init /\ [][Next]_vars /\ Fairness

ContextTargetsAlive == \A c \in Contexts : contexts[c] # 0 => alive[contexts[c]]
NamedTargetsAlive == \A name \in Names : names[name] # 0 => alive[names[name]]
NoOwnerlessStorage == \A n \in Nodes : alive[n] = (Named(n) \/ Refs(n) > 0)
ContextStateConsistent == \A c \in Contexts :
    contexts[c] = 0 => ~userOpen[c] /\ ~mapped[c] /\ ~dirty[c]
UniqueNames == \A a, b \in Names : names[a] # 0 /\ names[a] = names[b] => a = b
PagingWritesSucceed == ~ioFailed
ReadConsistent == lastReadNode # 0 => lastReadData \in {0, lastReadNode}
DataIntegrity == \A n \in Nodes : data[n] \in {0, n}

DirtyPagesDrain == \A c \in Contexts : dirty[c] ~> ~dirty[c]
UnlinkedObjectsReleased == \A n \in Nodes : (alive[n] /\ ~Named(n)) ~> ~alive[n]

=============================================================================
