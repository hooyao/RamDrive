--------------------------- MODULE PagePoolReservation ---------------------------
\* TLA+ model of PagePool Reserve/Rent + PagedFileContent SetLength/Write
\* interaction under kernel-cache mode.
\*
\* Abstraction:
\*   - Pool tracks: allocated, reserved, rented (freeStack = allocated - rented)
\*   - Each file does: SetLength(Reserve N) → Write(Phase1 scan, Phase2 Rent, Phase3 consume)
\*   - Capacity check in AllocateNewPage: allocated + reserved < maxPages  ← THE BUG
\*
\* We model N concurrent file copies, each needing PagesPerFile pages,
\* with total pool capacity = MaxPages.

EXTENDS Integers, FiniteSets, TLC

CONSTANTS MaxPages, NumFiles

ASSUME MaxPages > 0 /\ NumFiles > 0

Files == 1..NumFiles
PagesPerFile == MaxPages \div NumFiles

VARIABLES
    poolAllocated,   \* total pages allocated from OS (free stack + rented)
    poolReserved,    \* total reservations across all files
    poolRented,      \* pages currently checked out
    fileReserved,    \* [f] → pages reserved by SetLength for file f
    fileRented,      \* [f] → pages rented by Write Phase 2 for file f
    filePhase        \* [f] → state machine per file

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
   Check: allocated + reserved + count <= MaxPages
   ──────────────────────────────────────────── *)
DoSetLength(f) ==
    /\ filePhase[f] = "init"
    /\ poolAllocated + poolReserved + PagesPerFile <= MaxPages
    /\ poolReserved' = poolReserved + PagesPerFile
    /\ fileReserved' = [fileReserved EXCEPT ![f] = PagesPerFile]
    /\ filePhase' = [filePhase EXCEPT ![f] = "reserved"]
    /\ UNCHANGED <<poolAllocated, poolRented, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 1: scan pages (read lock).
   Abstract: just transition. All PagesPerFile needed.
   ──────────────────────────────────────────── *)
DoPhase1(f) ==
    /\ filePhase[f] = "reserved"
    /\ filePhase' = [filePhase EXCEPT ![f] = "renting"]
    /\ UNCHANGED <<poolAllocated, poolReserved, poolRented, fileReserved, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 2: RentBatch (no lock)

   RentBatch pops from freeStack, then allocates new pages.
   AllocateNewPageIfUnderCapacity check:  allocated + reserved < MaxPages

   We model the batch atomically:
     fromFree = min(FreeStack, needed)
     toAlloc  = needed - fromFree
     toAlloc succeeds only if: allocated + toAlloc + reserved <= MaxPages
       (each new page increments allocated; check is per-page CAS loop,
        but batch effect is: final allocated + reserved <= MaxPages)
   ──────────────────────────────────────────── *)
DoPhase2(f) ==
    /\ filePhase[f] = "renting"
    /\ LET needed == PagesPerFile
           fromFree == IF FreeStack >= needed THEN needed ELSE FreeStack
           toAlloc == needed - fromFree
           \* Can the new allocations fit under the capacity check?
           canAlloc == (poolAllocated + toAlloc) + poolReserved <= MaxPages
       IN
       IF canAlloc
       THEN
           \* Success
           /\ poolAllocated' = poolAllocated + toAlloc
           /\ poolRented' = poolRented + needed
           /\ fileRented' = [fileRented EXCEPT ![f] = needed]
           /\ filePhase' = [filePhase EXCEPT ![f] = "rented"]
           /\ UNCHANGED <<poolReserved, fileReserved>>
       ELSE
           \* Failure: DISK_FULL
           /\ filePhase' = [filePhase EXCEPT ![f] = "failed"]
           /\ UNCHANGED <<poolAllocated, poolReserved, poolRented, fileReserved, fileRented>>

(* ────────────────────────────────────────────
   Write Phase 3: consume reservations (write lock)
   Actual pages now replace the reservations.
   ──────────────────────────────────────────── *)
DoPhase3(f) ==
    /\ filePhase[f] = "rented"
    /\ LET consumed == IF fileReserved[f] >= fileRented[f]
                        THEN fileRented[f]
                        ELSE fileReserved[f]
       IN
       /\ poolReserved' = poolReserved - consumed
       /\ fileReserved' = [fileReserved EXCEPT ![f] = fileReserved[f] - consumed]
       /\ filePhase' = [filePhase EXCEPT ![f] = "done"]
       /\ UNCHANGED <<poolAllocated, poolRented, fileRented>>

(* ════════════════════════════════════════════
   Spec
   ════════════════════════════════════════════ *)
Next ==
    \E f \in Files :
        \/ DoSetLength(f)
        \/ DoPhase1(f)
        \/ DoPhase2(f)
        \/ DoPhase3(f)

Fairness == \A f \in Files :
    /\ WF_vars(DoSetLength(f))
    /\ WF_vars(DoPhase1(f))
    /\ WF_vars(DoPhase2(f))
    /\ WF_vars(DoPhase3(f))

Spec == Init /\ [][Next]_vars /\ Fairness

(* ════════════════════════════════════════════
   Invariants
   ════════════════════════════════════════════ *)

\* CRITICAL: Write should not fail when the file has reservations
NoSpuriousDiskFull ==
    \A f \in Files :
        filePhase[f] = "failed" => fileReserved[f] = 0

\* Pool accounting must stay consistent
PoolConsistent ==
    /\ poolAllocated >= 0
    /\ poolReserved >= 0
    /\ poolRented >= 0
    /\ poolRented <= poolAllocated

\* Liveness: all files eventually complete
AllDone == \A f \in Files : filePhase[f] = "done"
Liveness == <>AllDone

=============================================================================
