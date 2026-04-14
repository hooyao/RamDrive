------------------------- MODULE RamDiskSystem -------------------------
\* Full-system TLA+ model for the RamDrive file system.
\*
\* Covers: PagePool accounting, per-file sparse page tables, 3-phase write
\* (including Phase 3 deficit fallback), Read (concurrent with write),
\* SetLength (extend/truncate — concurrent with write phases),
\* Delete, CreateFile (slot reuse), OverwriteFile with allocationSize
\* check, SetAllocationSize TOCTOU check, GetVolumeInfo as an external
\* observer, and per-file capacity reservation.
\*
\* SetLength can interleave with in-flight writes (between phases where
\* no per-file lock is held). This models concurrent truncation creating
\* more page holes than Phase 1 predicted, triggering the Phase 3
\* deficit fallback path in code.
\*
\* Move/Rename is NOT modeled because it only mutates the directory tree
\* under _structureLock (atomic, serialized) and does not touch page tables
\* or pool state. No concurrency hazard beyond the structure lock.

EXTENDS Integers, FiniteSets, Sequences, TLC

CONSTANTS MaxPages, NumFiles

ASSUME MaxPages > 0 /\ NumFiles > 0

Files == 1..NumFiles
PageIndices == 0..(MaxPages - 1)
\* Data tags: 0 = unallocated/zero, f = written by file f
DataValues == 0..NumFiles

(* ================================================================
   State Variables
   ================================================================ *)
VARIABLES
    \* ── PagePool (global, shared) ──
    poolAllocated,    \* Total pages allocated from OS (free stack + rented)
    poolRented,       \* Pages currently checked out by files

    \* ── Per-file state ──
    fileLength,       \* Logical length in pages (sparse: pages may be unallocated)
    filePages,        \* Sparse page table: [pageIndex -> DataValues]
    fileAlive,        \* TRUE = file exists, FALSE = deleted
    fileReserved,     \* Pages reserved in pool but not yet allocated (per-file)

    \* ── Per-file operation state machine ──
    fileOp,           \* Operation state
    writeTarget,      \* Pages identified in Phase 1 as needing allocation
    writePreAlloc,    \* Number of pages rented in Phase 2
    writeRange,       \* Page indices this write targets

    \* ── Read verification ──
    lastReadFile,     \* Which file was last read (0 = none)
    lastReadVal,      \* Data tag from last read

    \* ── SetAllocSize / OverwriteFile result ──
    allocResult,      \* [Files -> {"none","ok","full"}]

    \* ── External observer ──
    observedFree      \* Last GetVolumeInfo result (in pages)

vars == <<poolAllocated, poolRented,
          fileLength, filePages, fileAlive, fileReserved,
          fileOp, writeTarget, writePreAlloc, writeRange,
          lastReadFile, lastReadVal, allocResult,
          observedFree>>

OpStates == {"idle", "w_p1", "w_p2"}

(* ================================================================
   Helpers
   ================================================================ *)
FreeStack == poolAllocated - poolRented

\* Total reservations across all files
TotalReserved ==
    LET RECURSIVE SumRes(_)
        SumRes(S) == IF S = {} THEN 0
                     ELSE LET f == CHOOSE x \in S : TRUE
                          IN fileReserved[f] + SumRes(S \ {f})
    IN SumRes(Files)

\* Available pages = total - rented - reserved
FreePages == MaxPages - poolRented - TotalReserved

\* Total allocated (non-zero) pages across all files (alive or in-transit)
TotalFilePages ==
    LET CountForFile(f) ==
            Cardinality({p \in PageIndices : filePages[f][p] # 0})
    IN LET RECURSIVE SumFiles(_)
           SumFiles(S) == IF S = {} THEN 0
                          ELSE LET f == CHOOSE x \in S : TRUE
                               IN CountForFile(f) + SumFiles(S \ {f})
       IN SumFiles(Files)

TotalPreAlloc ==
    LET RECURSIVE Sum(_)
        Sum(S) == IF S = {} THEN 0
                  ELSE LET f == CHOOSE x \in S : TRUE
                       IN writePreAlloc[f] + Sum(S \ {f})
    IN Sum(Files)

\* Allocated page count for a single file
FileAllocCount(f) == Cardinality({p \in PageIndices : filePages[f][p] # 0})

\* Maximum element of a non-empty set of naturals
MaxElement(S) == CHOOSE p \in S : \A q \in S : q <= p

(* ================================================================
   Type Invariant
   ================================================================ *)
TypeOK ==
    /\ poolAllocated \in 0..MaxPages
    /\ poolRented \in 0..MaxPages
    /\ fileLength \in [Files -> 0..MaxPages]
    /\ filePages \in [Files -> [PageIndices -> DataValues]]
    /\ fileAlive \in [Files -> BOOLEAN]
    /\ fileReserved \in [Files -> 0..MaxPages]
    /\ fileOp \in [Files -> OpStates]
    /\ writeTarget \in [Files -> SUBSET PageIndices]
    /\ writePreAlloc \in [Files -> 0..MaxPages]
    /\ writeRange \in [Files -> SUBSET PageIndices]
    /\ lastReadFile \in 0..NumFiles
    /\ lastReadVal \in DataValues
    /\ allocResult \in [Files -> {"none", "ok", "full"}]
    /\ observedFree \in -(MaxPages)..MaxPages

(* ================================================================
   Init
   ================================================================ *)
Init ==
    /\ poolAllocated = 0
    /\ poolRented = 0
    /\ fileLength = [f \in Files |-> 0]
    /\ filePages = [f \in Files |-> [p \in PageIndices |-> 0]]
    /\ fileAlive = [f \in Files |-> TRUE]
    /\ fileReserved = [f \in Files |-> 0]
    /\ fileOp = [f \in Files |-> "idle"]
    /\ writeTarget = [f \in Files |-> {}]
    /\ writePreAlloc = [f \in Files |-> 0]
    /\ writeRange = [f \in Files |-> {}]
    /\ lastReadFile = 0
    /\ lastReadVal = 0
    /\ allocResult = [f \in Files |-> "none"]
    /\ observedFree = MaxPages

(* ================================================================
   CreateFile: bring a dead file slot back to life (structure-lock)
   Models file creation after deletion — slot reuse.
   ================================================================ *)
DoCreateFile(f) ==
    /\ ~fileAlive[f]
    /\ fileOp[f] = "idle"
    /\ fileAlive' = [fileAlive EXCEPT ![f] = TRUE]
    /\ fileLength' = [fileLength EXCEPT ![f] = 0]
    \* filePages already all 0 from DoDelete (enforced by DeadFilesClean)
    /\ UNCHANGED <<poolAllocated, poolRented, filePages, fileReserved,
                    fileOp, writeTarget, writePreAlloc, writeRange,
                    lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Write Phase 1: scan page table (read-lock)
   Identify unallocated pages in the target range.
   Concurrent with Read (both hold read-lock).
   ================================================================ *)
DoWriteP1(f, pages) ==
    /\ fileOp[f] = "idle"
    /\ fileAlive[f]
    /\ pages # {}
    /\ pages \subseteq 0..(fileLength[f] - 1)
    \* Scan: which target pages are unallocated?
    /\ LET unalloc == {p \in pages : filePages[f][p] = 0}
       IN
       /\ writeTarget' = [writeTarget EXCEPT ![f] = unalloc]
       /\ writeRange' = [writeRange EXCEPT ![f] = pages]
       /\ fileOp' = [fileOp EXCEPT ![f] = "w_p1"]
       /\ UNCHANGED <<poolAllocated, poolRented,
                       fileLength, filePages, fileAlive, fileReserved,
                       writePreAlloc,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Write Phase 2: allocate pages from pool (NO lock)
   Lock-free: RentBatch from pool. Fail = DISK_FULL.
   Unreserves from file's reservations first (CAS in code), freeing
   capacity for actual allocation.
   ================================================================ *)
DoWriteP2(f) ==
    /\ fileOp[f] = "w_p1"
    /\ LET needed == Cardinality(writeTarget[f])
           \* Unreserve from this file's reservations to free capacity
           toUnreserve == IF fileReserved[f] >= needed
                          THEN needed ELSE fileReserved[f]
           fromFree == IF FreeStack >= needed THEN needed ELSE FreeStack
           toAlloc == needed - fromFree
           canAlloc == poolAllocated + toAlloc + (TotalReserved - toUnreserve) <= MaxPages
       IN
       IF needed = 0
       THEN
           \* Nothing to allocate — go straight to Phase 3
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
           /\ fileOp' = [fileOp EXCEPT ![f] = "w_p2"]
           /\ UNCHANGED <<poolAllocated, poolRented,
                           fileLength, filePages, fileAlive, fileReserved,
                           writeTarget, writeRange,
                           lastReadFile, lastReadVal, allocResult, observedFree>>
       ELSE IF canAlloc
       THEN
           \* Success: unreserve, then rent pages
           /\ fileReserved' = [fileReserved EXCEPT ![f] = fileReserved[f] - toUnreserve]
           /\ poolAllocated' = poolAllocated + toAlloc
           /\ poolRented' = poolRented + needed
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = needed]
           /\ fileOp' = [fileOp EXCEPT ![f] = "w_p2"]
           /\ UNCHANGED <<fileLength, filePages, fileAlive,
                           writeTarget, writeRange,
                           lastReadFile, lastReadVal, allocResult, observedFree>>
       ELSE
           \* Failure: DISK_FULL — return to idle (no state changed)
           /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
           /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
           /\ writeRange' = [writeRange EXCEPT ![f] = {}]
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
           /\ UNCHANGED <<poolAllocated, poolRented,
                           fileLength, filePages, fileAlive, fileReserved,
                           lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Write Phase 3: assign pages + write data (write-lock)
   Exclusive with Read on the same file.

   Handles two scenarios:
   (a) SURPLUS — concurrent writer filled gaps between Phase 1 and 3,
       so fewer pages need our pre-alloc. Return excess to pool.
   (b) DEFICIT — concurrent truncation between phases freed pages,
       creating more holes than Phase 1 predicted. Fall back to
       single-page Rent with CAS unreserve from file reservations.
       Maps to the code's Phase 3 fallback path.

   Also extends fileLength if concurrent truncation shrunk it below
   the write range (code: `if (endOffset > _length) _length = endOffset`).
   ================================================================ *)
DoWriteP3(f) ==
    /\ fileOp[f] = "w_p2"
    /\ LET
           \* Recount unallocated pages in write range — may differ from
           \* Phase 1 scan due to concurrent truncation/extension
           needPages == {p \in writeRange[f] : filePages[f][p] = 0}
           pagesNeeded == Cardinality(needPages)
           surplus == writePreAlloc[f] - pagesNeeded
           \* Write extends fileLength if truncation shrunk it
           maxWriteIdx == MaxElement(writeRange[f])
           newLen == IF maxWriteIdx + 1 > fileLength[f]
                     THEN maxWriteIdx + 1 ELSE fileLength[f]
           \* New page table: write data tag to all target pages
           newPages == [p \in PageIndices |->
               IF p \in writeRange[f] THEN f
               ELSE filePages[f][p]]
           \* After writing, compute correct reservation to fix over-reservation
           \* caused by concurrent SetLength during write phases.
           \* (Code: CAS loop at end of Phase 3 adjusts _reservedPages.)
           newAllocInRange == Cardinality({p \in 0..(newLen - 1) : newPages[p] # 0})
           correctReserved == IF newLen > newAllocInRange
                              THEN newLen - newAllocInRange ELSE 0
       IN
       IF surplus >= 0 THEN
           \* SURPLUS or exact: assign pre-alloc pages, return excess
           LET adjustedReserved == IF fileReserved[f] > correctReserved
                                   THEN correctReserved ELSE fileReserved[f]
           IN
           /\ filePages' = [filePages EXCEPT ![f] = newPages]
           /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
           /\ fileReserved' = [fileReserved EXCEPT ![f] = adjustedReserved]
           /\ poolRented' = poolRented - surplus
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
           /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
           /\ writeRange' = [writeRange EXCEPT ![f] = {}]
           /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
           /\ UNCHANGED <<poolAllocated, fileAlive,
                           lastReadFile, lastReadVal, allocResult, observedFree>>
       ELSE
           \* DEFICIT: need more pages than pre-allocated
           LET deficit == -surplus
               \* CAS unreserve from file's reservations (code: CAS loop)
               toUnreserve == IF fileReserved[f] >= deficit
                              THEN deficit ELSE fileReserved[f]
               reservedAfterUnreserve == fileReserved[f] - toUnreserve
               \* Also adjust for over-reservation
               adjustedReserved == IF reservedAfterUnreserve > correctReserved
                                   THEN correctReserved
                                   ELSE reservedAfterUnreserve
               \* Try to rent deficit pages from pool
               fromFree == IF FreeStack >= deficit THEN deficit ELSE FreeStack
               toAlloc == deficit - fromFree
               canRent == poolAllocated + toAlloc
                          + (TotalReserved - toUnreserve
                             - (reservedAfterUnreserve - adjustedReserved))
                          <= MaxPages
           IN
           IF canRent THEN
               \* Success: rent additional pages, write all data
               /\ filePages' = [filePages EXCEPT ![f] = newPages]
               /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
               /\ fileReserved' = [fileReserved EXCEPT
                      ![f] = adjustedReserved]
               /\ poolAllocated' = poolAllocated + toAlloc
               /\ poolRented' = poolRented + deficit
               /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
               /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
               /\ writeRange' = [writeRange EXCEPT ![f] = {}]
               /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
               /\ UNCHANGED <<fileAlive,
                               lastReadFile, lastReadVal, allocResult,
                               observedFree>>
           ELSE
               \* Failure: return all pre-alloc pages, abort write
               /\ poolRented' = poolRented - writePreAlloc[f]
               /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
               /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
               /\ writeRange' = [writeRange EXCEPT ![f] = {}]
               /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
               /\ UNCHANGED <<poolAllocated, fileLength, filePages,
                               fileAlive, fileReserved,
                               lastReadFile, lastReadVal, allocResult,
                               observedFree>>

(* ================================================================
   Read: observe a page (read-lock on file)
   Concurrent with other reads and Write Phase 1 (both read-lock).
   Blocked during Write Phase 3 (write-lock) — modeled by TLA+
   atomicity: DoWriteP3 and DoRead cannot interleave mid-step.
   ================================================================ *)
DoRead(f, p) ==
    /\ fileAlive[f]
    /\ p < fileLength[f]
    /\ lastReadFile' = f
    /\ lastReadVal' = filePages[f][p]
    /\ UNCHANGED <<poolAllocated, poolRented,
                    fileLength, filePages, fileAlive, fileReserved,
                    fileOp, writeTarget, writePreAlloc, writeRange,
                    allocResult, observedFree>>

(* ================================================================
   SetLength: extend (write-lock on file)
   Reserves capacity in the pool for unallocated pages. This prevents
   aggregate file sizes from exceeding total capacity and guarantees
   subsequent writes will succeed (required for kernel cache mode).
   Fails if available capacity is insufficient.

   Can interleave with write phases: between Phase 1 (read-lock
   released) and Phase 3 (write-lock acquired), another thread can
   acquire the write-lock and call SetLength. This is the mechanism
   that triggers Phase 3's deficit path.
   ================================================================ *)
DoExtend(f, newLen) ==
    /\ fileOp[f] \in {"idle", "w_p1", "w_p2"}
    /\ fileAlive[f]
    /\ newLen > fileLength[f]
    /\ newLen <= MaxPages
    /\ LET allocCount == FileAllocCount(f)
           \* Pages needing reservation = new page count - already allocated - already reserved
           additionalNeeded == newLen - allocCount - fileReserved[f]
           needed == IF additionalNeeded > 0 THEN additionalNeeded ELSE 0
       IN
       \* Guard: enough free capacity for new reservations
       /\ needed <= FreePages
       /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
       /\ fileReserved' = [fileReserved EXCEPT ![f] = fileReserved[f] + needed]
       /\ UNCHANGED <<poolAllocated, poolRented,
                       filePages, fileAlive,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   SetLength: truncate (write-lock on file)
   Free pages beyond new length, return to pool.
   Release excess reservations.

   Can interleave with write phases (see DoExtend comment).
   ================================================================ *)
DoTruncate(f, newLen) ==
    /\ fileOp[f] \in {"idle", "w_p1", "w_p2"}
    /\ fileAlive[f]
    /\ newLen >= 0
    /\ newLen < fileLength[f]
    /\ LET freed == Cardinality({p \in PageIndices :
                        p >= newLen /\ p < fileLength[f] /\ filePages[f][p] # 0})
           newPages == [p \in PageIndices |->
               IF p >= newLen THEN 0 ELSE filePages[f][p]]
           \* After freeing, count allocated pages in retained range
           newAllocCount == Cardinality({p \in 0..(newLen - 1) :
                               newPages[p] # 0})
           \* Reservations should cover only unallocated pages in retained range
           newReserved == IF newLen > newAllocCount
                          THEN newLen - newAllocCount ELSE 0
           reserveRelease == IF fileReserved[f] > newReserved
                             THEN fileReserved[f] - newReserved ELSE 0
       IN
       /\ poolRented' = poolRented - freed
       /\ filePages' = [filePages EXCEPT ![f] = newPages]
       /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
       /\ fileReserved' = [fileReserved EXCEPT ![f] = fileReserved[f] - reserveRelease]
       /\ UNCHANGED <<poolAllocated, fileAlive,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Delete: dispose file, return all pages (structure-lock + write-lock)
   Releases all reservations.
   Only when idle — an open write handle prevents deletion.
   ================================================================ *)
DoDelete(f) ==
    /\ fileOp[f] = "idle"
    /\ fileAlive[f]
    /\ LET freed == Cardinality({p \in PageIndices : filePages[f][p] # 0})
           clearedPages == [p \in PageIndices |-> 0]
       IN
       /\ poolRented' = poolRented - freed
       /\ filePages' = [filePages EXCEPT ![f] = clearedPages]
       /\ fileLength' = [fileLength EXCEPT ![f] = 0]
       /\ fileAlive' = [fileAlive EXCEPT ![f] = FALSE]
       /\ fileReserved' = [fileReserved EXCEPT ![f] = 0]
       /\ UNCHANGED <<poolAllocated,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   OverwriteFile: truncate to 0, then check allocationSize (write-lock)
   The allocationSize is a TOCTOU hint — capacity may change before
   the actual write. The truncation always succeeds; the alloc check
   is advisory (code returns STATUS_DISK_FULL if exceeded).
   ================================================================ *)
DoOverwrite(f, allocSize) ==
    /\ fileOp[f] = "idle"
    /\ fileAlive[f]
    /\ LET freed == Cardinality({p \in PageIndices : filePages[f][p] # 0})
           clearedPages == [p \in PageIndices |-> 0]
           \* Free pages after truncation and reservation release
           newFree == FreePages + freed + fileReserved[f]
       IN
       /\ poolRented' = poolRented - freed
       /\ filePages' = [filePages EXCEPT ![f] = clearedPages]
       /\ fileLength' = [fileLength EXCEPT ![f] = 0]
       /\ fileReserved' = [fileReserved EXCEPT ![f] = 0]
       \* Record allocationSize check result
       /\ allocResult' = [allocResult EXCEPT ![f] =
              IF allocSize <= 0 THEN "none"
              ELSE IF allocSize <= newFree THEN "ok"
              ELSE "full"]
       /\ UNCHANGED <<poolAllocated, fileAlive,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, observedFree>>

(* ================================================================
   SetAllocationSize: TOCTOU best-effort check (no lock, no state change)
   Called before file copy to check if space is available.
   Records result — may be stale by the time the actual write happens.
   ================================================================ *)
DoSetAllocSize(f, requestedLen) ==
    /\ fileAlive[f]
    /\ fileOp[f] = "idle"
    /\ requestedLen > 0
    /\ LET additional == requestedLen - fileLength[f]
           freeNow == FreePages
       IN
       /\ allocResult' = [allocResult EXCEPT ![f] =
              IF additional <= 0 THEN "ok"
              ELSE IF additional <= freeNow THEN "ok"
              ELSE "full"]
       /\ UNCHANGED <<poolAllocated, poolRented,
                       fileLength, filePages, fileAlive, fileReserved,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, observedFree>>

(* ================================================================
   GetVolumeInfo: external observer (no lock)
   ================================================================ *)
DoGetVolumeInfo ==
    /\ observedFree' = FreePages
    /\ UNCHANGED <<poolAllocated, poolRented,
                    fileLength, filePages, fileAlive, fileReserved,
                    fileOp, writeTarget, writePreAlloc, writeRange,
                    lastReadFile, lastReadVal, allocResult>>

(* ================================================================
   Spec
   ================================================================ *)
Next ==
    \/ \E f \in Files :
        \/ DoCreateFile(f)
        \/ \E pages \in (SUBSET (0..(MaxPages - 1)) \ {{}}) :
               DoWriteP1(f, pages)
        \/ DoWriteP2(f)
        \/ DoWriteP3(f)
        \/ \E p \in PageIndices : DoRead(f, p)
        \/ \E newLen \in 1..MaxPages : DoExtend(f, newLen)
        \/ \E newLen \in 0..(MaxPages - 1) : DoTruncate(f, newLen)
        \/ DoDelete(f)
        \/ \E allocSize \in 0..MaxPages : DoOverwrite(f, allocSize)
        \/ \E reqLen \in 1..MaxPages : DoSetAllocSize(f, reqLen)
    \/ DoGetVolumeInfo
    \* Stuttering when quiescent
    \/ /\ \A f \in Files : fileOp[f] = "idle"
       /\ UNCHANGED vars

Fairness == \A f \in Files :
    /\ \A pages \in (SUBSET (0..(MaxPages - 1)) \ {{}}) :
           WF_vars(DoWriteP1(f, pages))
    /\ WF_vars(DoWriteP2(f))
    /\ WF_vars(DoWriteP3(f))

Spec == Init /\ [][Next]_vars /\ Fairness

(* ================================================================
   Safety Invariants
   ================================================================ *)

\* Pool accounting stays sane
PoolConsistent ==
    /\ poolRented >= 0
    /\ poolAllocated >= 0
    /\ poolRented <= poolAllocated
    /\ poolAllocated <= MaxPages

\* Rented pages = file-held pages + in-transit pre-allocated pages
NoPageLeak ==
    TotalFilePages + TotalPreAlloc = poolRented

\* Committed capacity (rented + reserved) never exceeds total pool.
\* THE KEY INVARIANT — prevents aggregate file sizes from exceeding capacity.
CommittedWithinCapacity ==
    poolRented + TotalReserved <= MaxPages

\* FreePages is consistent and never negative
FreeBytesAccurate ==
    FreePages >= 0

\* No single file exceeds total capacity
SingleFileCap ==
    \A f \in Files : fileLength[f] <= MaxPages

\* Per-file reservations are non-negative and bounded.
\* When a file has an in-flight write (not idle), Phase 3 may extend
\* fileLength beyond reservations, so the upper bound only applies at rest.
ReservationsConsistent ==
    \A f \in Files :
        /\ fileReserved[f] >= 0
        /\ (fileAlive[f] /\ fileOp[f] = "idle") =>
            fileReserved[f] <= fileLength[f] - FileAllocCount(f)

\* Data integrity: no file ever contains another file's data tag.
DataIntegrity ==
    \A f \in Files : fileAlive[f] =>
        \A p \in PageIndices :
            filePages[f][p] \in {0, f}

\* Read consistency: last read value belongs to the file that was read
ReadConsistent ==
    lastReadFile > 0 => lastReadVal \in {0, lastReadFile}

\* Dead files hold no pages and no reservations
DeadFilesClean ==
    \A f \in Files : ~fileAlive[f] =>
        /\ fileLength[f] = 0
        /\ fileReserved[f] = 0
        /\ \A p \in PageIndices : filePages[f][p] = 0

(* ================================================================
   Liveness
   ================================================================ *)

\* Every write that enters Phase 1 eventually returns to idle
WriteTerminates ==
    \A f \in Files : (fileOp[f] = "w_p1") ~> (fileOp[f] = "idle")

=============================================================================
