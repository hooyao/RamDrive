--------------------------- MODULE PagePoolFixed ---------------------------
\* FIXED model: Write Phase 2 unreserves own reservations BEFORE renting pages.
\*
\* Fix semantics:
\*   Phase 2 does: Unreserve(min(fileReserved, needed)) → RentBatch(needed)
\*   This releases the capacity that was reserved for exactly these writes,
\*   so AllocateNewPageIfUnderCapacity can use that capacity for allocation.
\*
\*   On failure: re-Reserve what was unreserved (restore invariant).

EXTENDS Integers, FiniteSets, TLC

CONSTANTS MaxPages, NumFiles

ASSUME MaxPages > 0 /\ NumFiles > 0

Files == 1..NumFiles
PagesPerFile == MaxPages \div NumFiles

VARIABLES
    poolAllocated,
    poolReserved,
    poolRented,
    fileReserved,
    fileRented,
    filePhase

vars == <<poolAllocated, poolReserved, poolRented,
          fileReserved, fileRented, filePhase>>

TypeOK ==
    /\ poolAllocated \in 0..MaxPages
    /\ poolReserved \in 0..MaxPages
    /\ poolRented \in 0..MaxPages
    /\ fileReserved \in [Files -> 0..MaxPages]
    /\ fileRented \in [Files -> 0..MaxPages]
    /\ filePhase \in [Files -> {"init", "reserved", "renting", "rented", "failed", "done"}]

FreeStack == poolAllocated - poolRented

Init ==
    /\ poolAllocated = 0
    /\ poolReserved = 0
    /\ poolRented = 0
    /\ fileReserved = [f \in Files |-> 0]
    /\ fileRented = [f \in Files |-> 0]
    /\ filePhase = [f \in Files |-> "init"]

(* ────────────────────────────────────────────
   SetLength: Pool.Reserve(PagesPerFile)
   ──────────────────────────────────────────── *)
DoSetLength(f) ==
    /\ filePhase[f] = "init"
    /\ poolAllocated + poolReserved + PagesPerFile <= MaxPages
    /\ poolReserved' = poolReserved + PagesPerFile
    /\ fileReserved' = [fileReserved EXCEPT ![f] = PagesPerFile]
    /\ filePhase' = [filePhase EXCEPT ![f] = "reserved"]
    /\ UNCHANGED <<poolAllocated, poolRented, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 1: scan pages (read lock)
   ──────────────────────────────────────────── *)
DoPhase1(f) ==
    /\ filePhase[f] = "reserved"
    /\ filePhase' = [filePhase EXCEPT ![f] = "renting"]
    /\ UNCHANGED <<poolAllocated, poolReserved, poolRented, fileReserved, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 2 (FIXED): Unreserve own reservation, then RentBatch

   The fix: before renting, unreserve min(fileReserved[f], needed)
   so AllocateNewPageIfUnderCapacity sees reduced poolReserved.
   On failure: re-reserve (restore the invariant).
   ──────────────────────────────────────────── *)
DoPhase2(f) ==
    /\ filePhase[f] = "renting"
    /\ LET needed == PagesPerFile
           \* FIX: unreserve own reservations first
           unreserved == IF fileReserved[f] >= needed THEN needed ELSE fileReserved[f]
           poolReservedAfterUnreserve == poolReserved - unreserved
           \* Now try to rent with reduced poolReserved
           fromFree == IF FreeStack >= needed THEN needed ELSE FreeStack
           toAlloc == needed - fromFree
           canAlloc == (poolAllocated + toAlloc) + poolReservedAfterUnreserve <= MaxPages
       IN
       IF canAlloc
       THEN
           \* Success: unreserve, then rent
           /\ poolReserved' = poolReservedAfterUnreserve
           /\ poolAllocated' = poolAllocated + toAlloc
           /\ poolRented' = poolRented + needed
           /\ fileReserved' = [fileReserved EXCEPT ![f] = fileReserved[f] - unreserved]
           /\ fileRented' = [fileRented EXCEPT ![f] = needed]
           /\ filePhase' = [filePhase EXCEPT ![f] = "rented"]
       ELSE
           \* Failure: re-reserve (no net change) — genuine out of space
           /\ filePhase' = [filePhase EXCEPT ![f] = "failed"]
           /\ UNCHANGED <<poolAllocated, poolReserved, poolRented, fileReserved, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 3: finalize (write lock)
   Reservations already consumed in Phase 2 — just mark done.
   ──────────────────────────────────────────── *)
DoPhase3(f) ==
    /\ filePhase[f] = "rented"
    /\ filePhase' = [filePhase EXCEPT ![f] = "done"]
    /\ UNCHANGED <<poolAllocated, poolReserved, poolRented, fileReserved, fileRented>>

(* ════════════════════════════════════════════
   Spec
   ════════════════════════════════════════════ *)
Next ==
    \/ \E f \in Files :
        \/ DoSetLength(f)
        \/ DoPhase1(f)
        \/ DoPhase2(f)
        \/ DoPhase3(f)
    \/ /\ \A f \in Files : filePhase[f] \in {"done", "failed"}
       /\ UNCHANGED vars

Fairness == \A f \in Files :
    /\ WF_vars(DoSetLength(f))
    /\ WF_vars(DoPhase1(f))
    /\ WF_vars(DoPhase2(f))
    /\ WF_vars(DoPhase3(f))

Spec == Init /\ [][Next]_vars /\ Fairness

(* ════════════════════════════════════════════
   Invariants & Properties
   ════════════════════════════════════════════ *)

\* File with reservations must not get spurious DISK_FULL
NoSpuriousDiskFull ==
    \A f \in Files :
        filePhase[f] = "failed" => fileReserved[f] = 0

\* Pool accounting stays sane
PoolConsistent ==
    /\ poolAllocated >= 0
    /\ poolReserved >= 0
    /\ poolRented >= 0
    /\ poolRented <= poolAllocated
    /\ poolAllocated + poolReserved <= MaxPages

\* No capacity leak: total committed never exceeds MaxPages
NoCapacityLeak ==
    poolAllocated + poolReserved <= MaxPages

\* All files eventually complete
AllDone == \A f \in Files : filePhase[f] = "done"
Liveness == <>AllDone

=============================================================================
