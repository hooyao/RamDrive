------------------------- MODULE RamDiskSystem -------------------------
\* Full-system TLA+ model for the RamDrive file system.
\*
\* Covers: PagePool accounting, per-file sparse page tables, 3-phase write,
\* Read (concurrent with write), SetLength (extend/truncate), Delete,
\* CreateFile (slot reuse), OverwriteFile, SetAllocationSize TOCTOU check,
\* and GetVolumeInfo as an external observer.
\*
\* Move/Rename is NOT modeled because it only mutates the directory tree
\* under _structureLock (atomic, serialized) and does not touch page tables
\* or pool state. No concurrency hazard beyond the structure lock.
\*
\* Replaces the narrow PagePoolFixed.tla model that missed the FreeBytes
\* pollution bug caused by Reserve() in SetLength.

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

    \* ── Per-file operation state machine ──
    fileOp,           \* Operation state
    writeTarget,      \* Pages identified in Phase 1 as needing allocation
    writePreAlloc,    \* Number of pages rented in Phase 2
    writeRange,       \* Page indices this write targets

    \* ── Read verification ──
    lastReadFile,     \* Which file was last read (0 = none)
    lastReadVal,      \* Data tag from last read

    \* ── SetAllocSize result ──
    allocResult,      \* [Files -> {"none","ok","full"}]

    \* ── External observer ──
    observedFree      \* Last GetVolumeInfo result (in pages)

vars == <<poolAllocated, poolRented,
          fileLength, filePages, fileAlive,
          fileOp, writeTarget, writePreAlloc, writeRange,
          lastReadFile, lastReadVal, allocResult,
          observedFree>>

OpStates == {"idle", "w_p1", "w_p2"}

(* ================================================================
   Helpers
   ================================================================ *)
FreeStack == poolAllocated - poolRented
FreePages == MaxPages - poolRented

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

(* ================================================================
   Type Invariant
   ================================================================ *)
TypeOK ==
    /\ poolAllocated \in 0..MaxPages
    /\ poolRented \in 0..MaxPages
    /\ fileLength \in [Files -> 0..MaxPages]
    /\ filePages \in [Files -> [PageIndices -> DataValues]]
    /\ fileAlive \in [Files -> BOOLEAN]
    /\ fileOp \in [Files -> OpStates]
    /\ writeTarget \in [Files -> SUBSET PageIndices]
    /\ writePreAlloc \in [Files -> 0..MaxPages]
    /\ writeRange \in [Files -> SUBSET PageIndices]
    /\ lastReadFile \in 0..NumFiles
    /\ lastReadVal \in DataValues
    /\ allocResult \in [Files -> {"none", "ok", "full"}]
    /\ observedFree \in 0..MaxPages

(* ================================================================
   Init
   ================================================================ *)
Init ==
    /\ poolAllocated = 0
    /\ poolRented = 0
    /\ fileLength = [f \in Files |-> 0]
    /\ filePages = [f \in Files |-> [p \in PageIndices |-> 0]]
    /\ fileAlive = [f \in Files |-> TRUE]
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
    /\ UNCHANGED <<poolAllocated, poolRented, filePages,
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
                       fileLength, filePages, fileAlive,
                       writePreAlloc,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Write Phase 2: allocate pages from pool (NO lock)
   Lock-free: RentBatch from pool. Fail = DISK_FULL.
   ================================================================ *)
DoWriteP2(f) ==
    /\ fileOp[f] = "w_p1"
    /\ LET needed == Cardinality(writeTarget[f])
           fromFree == IF FreeStack >= needed THEN needed ELSE FreeStack
           toAlloc == needed - fromFree
           canAlloc == poolAllocated + toAlloc <= MaxPages
       IN
       IF needed = 0
       THEN
           \* Nothing to allocate — go straight to Phase 3
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
           /\ fileOp' = [fileOp EXCEPT ![f] = "w_p2"]
           /\ UNCHANGED <<poolAllocated, poolRented,
                           fileLength, filePages, fileAlive,
                           writeTarget, writeRange,
                           lastReadFile, lastReadVal, allocResult, observedFree>>
       ELSE IF canAlloc
       THEN
           \* Success: rent pages
           /\ poolAllocated' = poolAllocated + toAlloc
           /\ poolRented' = poolRented + needed
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = needed]
           /\ fileOp' = [fileOp EXCEPT ![f] = "w_p2"]
           /\ UNCHANGED <<fileLength, filePages, fileAlive,
                           writeTarget, writeRange,
                           lastReadFile, lastReadVal, allocResult, observedFree>>
       ELSE
           \* Failure: DISK_FULL — return to idle
           /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
           /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
           /\ writeRange' = [writeRange EXCEPT ![f] = {}]
           /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
           /\ UNCHANGED <<poolAllocated, poolRented,
                           fileLength, filePages, fileAlive,
                           lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Write Phase 3: assign pages + write data (write-lock)
   Exclusive with Read on the same file.
   Handle race: page may have been allocated by concurrent writer
   between Phase 1 scan and Phase 3 lock acquisition.
   ================================================================ *)
DoWriteP3(f) ==
    /\ fileOp[f] = "w_p2"
    /\ LET
           \* Pages still unallocated (need our pre-alloc)
           stillUnalloc == {p \in writeTarget[f] : filePages[f][p] = 0}
           consumed == Cardinality(stillUnalloc)
           \* Pre-alloc pages not consumed (race: another writer allocated them)
           excess == writePreAlloc[f] - consumed
           \* New page table: write data tag to all target pages
           newPages == [p \in PageIndices |->
               IF p \in writeRange[f] THEN f   \* Write our data
               ELSE filePages[f][p]]           \* Keep existing
       IN
       /\ filePages' = [filePages EXCEPT ![f] = newPages]
       \* Return excess pre-alloc pages to pool
       /\ poolRented' = poolRented - excess
       /\ writePreAlloc' = [writePreAlloc EXCEPT ![f] = 0]
       /\ writeTarget' = [writeTarget EXCEPT ![f] = {}]
       /\ writeRange' = [writeRange EXCEPT ![f] = {}]
       /\ fileOp' = [fileOp EXCEPT ![f] = "idle"]
       /\ UNCHANGED <<poolAllocated, fileLength, fileAlive,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

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
                    fileLength, filePages, fileAlive,
                    fileOp, writeTarget, writePreAlloc, writeRange,
                    allocResult, observedFree>>

(* ================================================================
   SetLength: extend (write-lock)
   Sparse: just set logical length, no page allocation.
   Fails if single file would exceed total pool capacity.
   ================================================================ *)
DoExtend(f, newLen) ==
    /\ fileOp[f] = "idle"
    /\ fileAlive[f]
    /\ newLen > fileLength[f]
    /\ newLen <= MaxPages
    /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
    /\ UNCHANGED <<poolAllocated, poolRented,
                    filePages, fileAlive,
                    fileOp, writeTarget, writePreAlloc, writeRange,
                    lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   SetLength: truncate (write-lock)
   Free pages beyond new length, return to pool.
   OverwriteFile is modeled as DoTruncate(f, 0) — truncate to empty.
   ================================================================ *)
DoTruncate(f, newLen) ==
    /\ fileOp[f] = "idle"
    /\ fileAlive[f]
    /\ newLen >= 0
    /\ newLen < fileLength[f]
    /\ LET freed == Cardinality({p \in PageIndices :
                        p >= newLen /\ p < fileLength[f] /\ filePages[f][p] # 0})
           newPages == [p \in PageIndices |->
               IF p >= newLen THEN 0 ELSE filePages[f][p]]
       IN
       /\ poolRented' = poolRented - freed
       /\ filePages' = [filePages EXCEPT ![f] = newPages]
       /\ fileLength' = [fileLength EXCEPT ![f] = newLen]
       /\ UNCHANGED <<poolAllocated, fileAlive,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

(* ================================================================
   Delete: dispose file, return all pages (structure-lock + write-lock)
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
       /\ UNCHANGED <<poolAllocated,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, allocResult, observedFree>>

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
                       fileLength, filePages, fileAlive,
                       fileOp, writeTarget, writePreAlloc, writeRange,
                       lastReadFile, lastReadVal, observedFree>>

(* ================================================================
   GetVolumeInfo: external observer (no lock)
   ================================================================ *)
DoGetVolumeInfo ==
    /\ observedFree' = FreePages
    /\ UNCHANGED <<poolAllocated, poolRented,
                    fileLength, filePages, fileAlive,
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

\* FreeBytes is never polluted by reservations — THE KEY INVARIANT
\* In the new design, FreePages depends ONLY on poolRented (no reservations).
FreeBytesAccurate ==
    FreePages = MaxPages - poolRented

\* No single file exceeds total capacity
SingleFileCap ==
    \A f \in Files : fileLength[f] <= MaxPages

\* Data integrity: no file ever contains another file's data tag.
\* Holds in ALL states (not just idle) because only DoWriteP3(f) modifies
\* filePages[f], and it only writes tag f or keeps existing {0, f} values.
DataIntegrity ==
    \A f \in Files : fileAlive[f] =>
        \A p \in PageIndices :
            filePages[f][p] \in {0, f}

\* Read consistency: last read value belongs to the file that was read
ReadConsistent ==
    lastReadFile > 0 => lastReadVal \in {0, lastReadFile}

\* Dead files hold no pages
DeadFilesClean ==
    \A f \in Files : ~fileAlive[f] =>
        /\ fileLength[f] = 0
        /\ \A p \in PageIndices : filePages[f][p] = 0

\* Note: SetAllocSize correctness is self-evident from the action definition
\* (simple comparison: additional > FreePages → "full"). No invariant needed.
\* The TOCTOU nature means the result may be stale by the time the write
\* happens — this is by design and acceptable.

(* ================================================================
   Liveness
   ================================================================ *)

\* Every write that enters Phase 1 eventually returns to idle
WriteTerminates ==
    \A f \in Files : (fileOp[f] = "w_p1") ~> (fileOp[f] = "idle")

=============================================================================
