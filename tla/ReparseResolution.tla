------------------------ MODULE ReparseResolution ------------------------
\* Abstract model of the local reparse-resolution loop. Each local link
\* points to a local link, an ordinary target, a missing target, or an
\* external volume. All finite graphs of NumLinks links are explored and
\* individual targets may change concurrently with resolution.
\* Path-string parsing, ACL checks, cache identity, and Windows/MUP behavior
\* after an external handoff are outside this model.
EXTENDS Naturals, TLC

CONSTANTS NumLinks, MaxHops
ASSUME /\ NumLinks > 0 /\ MaxHops > 0
Links == 1..NumLinks
Missing == 0
External == NumLinks + 1
Ordinary == NumLinks + 2
Destinations == 0..Ordinary

VARIABLES targets, phase, current, hops, result
vars == <<targets, phase, current, hops, result>>

Init ==
    /\ targets \in [Links -> Destinations]
    /\ phase = "idle" /\ current = Missing /\ hops = 0 /\ result = "none"

Start(link) ==
    /\ phase \in {"idle", "done"}
    /\ phase' = "walking" /\ current' = link /\ hops' = 0 /\ result' = "none"
    /\ UNCHANGED targets

Step ==
    /\ phase = "walking"
    /\ IF current = Missing
          THEN /\ phase' = "done" /\ result' = "missing"
               /\ UNCHANGED <<current, hops>>
       ELSE IF current = External
          THEN /\ phase' = "done" /\ result' = "external"
               /\ UNCHANGED <<current, hops>>
       ELSE IF current = Ordinary
          THEN /\ phase' = "done" /\ result' = "found"
               /\ UNCHANGED <<current, hops>>
       ELSE IF hops = MaxHops
          THEN /\ phase' = "done" /\ result' = "limit"
               /\ UNCHANGED <<current, hops>>
       ELSE /\ current' = targets[current] /\ hops' = hops + 1
            /\ UNCHANGED <<phase, result>>
    /\ UNCHANGED targets

Update(link, destination) ==
    /\ targets' = [targets EXCEPT ![link] = destination]
    /\ UNCHANGED <<phase, current, hops, result>>

Next == (\E link \in Links : Start(link)) \/ Step \/
        (\E link \in Links, destination \in Destinations : Update(link, destination))
Spec == Init /\ [][Next]_vars /\ WF_vars(Step)

TypeOK ==
    /\ targets \in [Links -> Destinations]
    /\ phase \in {"idle", "walking", "done"}
    /\ current \in Destinations /\ hops \in 0..MaxHops
    /\ result \in {"none", "missing", "external", "found", "limit"}

BoundedResolution == hops <= MaxHops
TerminalResultCorrect ==
    /\ phase = "done" =>
       CASE result = "missing" -> current = Missing
         [] result = "external" -> current = External
         [] result = "found" -> current = Ordinary
         [] result = "limit" -> current \in Links /\ hops = MaxHops
         [] OTHER -> FALSE
    /\ phase # "done" => result = "none"

ResolutionTerminates == (phase = "walking") ~> (phase = "done")
=============================================================================
