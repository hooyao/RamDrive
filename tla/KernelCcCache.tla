------------------------------- MODULE KernelCcCache -------------------------------
(***************************************************************************)
(* Models the WinFsp kernel `Cc` (Cache Manager) FileInfo cache and the    *)
(* user-mode IFileSystem callback contract. The single property under test:*)
(*                                                                          *)
(*     CacheFileSizeMatchesActual                                           *)
(*       \A f \in Files: CcCache[f].FileSize = ActualFileSize[f]            *)
(*                                                                          *)
(* This invariant is what the chrome-on-RamDrive STATUS_BREAKPOINT bug      *)
(* (#3, May 2026) violated: FlushFileBuffers returned an empty FspFileInfo  *)
(* with FileSize=0, kernel updated CcCache[f].FileSize := 0, subsequent     *)
(* read served zeros from cache, chrome's DCHECK fired.                     *)
(*                                                                          *)
(* Model:                                                                   *)
(*   - Each callback that returns FsResult/CreateResult/WriteResult         *)
(*     publishes a ResponseFileInfo to the kernel.                         *)
(*   - The kernel updates CcCache[f] from that ResponseFileInfo.            *)
(*   - A "buggy" callback can publish stale (zero) FileInfo. Without the    *)
(*     invariant, this doesn't crash the model — but the invariant catches *)
(*     it deterministically.                                                *)
(*                                                                          *)
(* Run:                                                                     *)
(*   java -jar tla2tools.jar -workers auto                                  *)
(*        -config tla/KernelCcCache_Minimal.cfg tla/KernelCcCache.tla       *)
(***************************************************************************)

EXTENDS Naturals, Sequences, FiniteSets, TLC

(***************************************************************************)
(* SCOPE / LIMITATIONS — read this before treating a passing run as proof. *)
(*                                                                          *)
(* This spec is a deliberate simplification:                                *)
(*                                                                          *)
(*   - Each callback explicitly chooses between "publish ActualFileSize"   *)
(*     (correct) and "publish 0" (buggy). The fixed-code config             *)
(*     (BuggyCallbacks={}) cannot violate the invariant by construction:    *)
(*     correctness is encoded as an axiom of the model, not derived. So a   *)
(*     pass on the fixed config is NOT a proof that the real C# code is     *)
(*     correct.                                                             *)
(*                                                                          *)
(*   - The Bug3Repro config (BuggyCallbacks={"FlushFileBuffers"})           *)
(*     deterministically reproduces the bug pattern in 4 steps and is the   *)
(*     real contribution of the model: it documents the failure mode, gives *)
(*     a minimal counterexample, and fails fast if anyone widens            *)
(*     BuggyCallbacks to model future regressions.                          *)
(*                                                                          *)
(* If you want the spec to actually verify the implementation, you would    *)
(* need to model the C# code's per-callback "read node and produce          *)
(* FileInfo" step at finer granularity, then prove that no such step can   *)
(* publish a FileSize that disagrees with the most recent SetFileSize.      *)
(***************************************************************************)

CONSTANTS
    Files,            \* set of file IDs, e.g. {"f1", "f2"}
    MaxFileSize,      \* upper bound on logical size, e.g. 4
    BuggyCallbacks    \* set of callback names that may publish stale FileInfo
                      \*   = {"FlushFileBuffers"} reproduces bug #3
                      \*   = {} models the fixed code (invariant must hold)

VARIABLES
    ActualFileSize,   \* fileId -> nat: ground-truth size in user-mode FS
    CcCache,          \* fileId -> [FileSize: nat]: kernel's cached view
    OpenedFiles       \* set of fileIds that currently have at least one open handle

vars == <<ActualFileSize, CcCache, OpenedFiles>>

(***************************************************************************)
(* Type invariant.                                                          *)
(***************************************************************************)
TypeOK ==
    /\ ActualFileSize \in [Files -> 0..MaxFileSize]
    /\ CcCache \in [Files -> [FileSize: 0..MaxFileSize]]
    /\ OpenedFiles \subseteq Files

(***************************************************************************)
(* Initial state: every file size 0, cache empty (size 0).                *)
(***************************************************************************)
Init ==
    /\ ActualFileSize = [f \in Files |-> 0]
    /\ CcCache = [f \in Files |-> [FileSize |-> 0]]
    /\ OpenedFiles = {}

(***************************************************************************)
(* A callback "publishes" FileInfo by writing to CcCache. Correct callbacks*)
(* publish the actual current size; buggy ones publish 0 (the empty default*)
(* FspFileInfo). Either way, the kernel believes the publication.          *)
(***************************************************************************)
Publish(f, name) ==
    IF name \in BuggyCallbacks
    THEN CcCache' = [CcCache EXCEPT ![f].FileSize = 0]
    ELSE CcCache' = [CcCache EXCEPT ![f].FileSize = ActualFileSize'[f]]

(***************************************************************************)
(* CreateFile: opens a handle, file starts at size 0, callback publishes.  *)
(***************************************************************************)
CreateFile(f) ==
    /\ f \notin OpenedFiles
    /\ OpenedFiles' = OpenedFiles \union {f}
    /\ ActualFileSize' = [ActualFileSize EXCEPT ![f] = 0]
    /\ Publish(f, "CreateFile")

(***************************************************************************)
(* WriteFile: extend file by 1 byte (bounded), callback publishes new size.*)
(***************************************************************************)
WriteFile(f) ==
    /\ f \in OpenedFiles
    /\ ActualFileSize[f] < MaxFileSize
    /\ ActualFileSize' = [ActualFileSize EXCEPT ![f] = ActualFileSize[f] + 1]
    /\ Publish(f, "WriteFile")
    /\ UNCHANGED OpenedFiles

(***************************************************************************)
(* FlushFileBuffers: NO actual size change, but the callback publishes.    *)
(* A buggy implementation that publishes default (zero) FspFileInfo here   *)
(* corrupts CcCache.                                                       *)
(***************************************************************************)
FlushFileBuffers(f) ==
    /\ f \in OpenedFiles
    /\ ActualFileSize' = ActualFileSize
    /\ Publish(f, "FlushFileBuffers")
    /\ UNCHANGED OpenedFiles

(***************************************************************************)
(* GetFileInformation: callback publishes current size.                    *)
(***************************************************************************)
GetFileInformation(f) ==
    /\ f \in OpenedFiles
    /\ ActualFileSize' = ActualFileSize
    /\ Publish(f, "GetFileInformation")
    /\ UNCHANGED OpenedFiles

(***************************************************************************)
(* SetFileSize: explicit truncate / extend.                                *)
(***************************************************************************)
SetFileSize(f, n) ==
    /\ f \in OpenedFiles
    /\ n \in 0..MaxFileSize
    /\ ActualFileSize' = [ActualFileSize EXCEPT ![f] = n]
    /\ Publish(f, "SetFileSize")
    /\ UNCHANGED OpenedFiles

(***************************************************************************)
(* Close: drop the handle. No cache update.                                *)
(***************************************************************************)
Close(f) ==
    /\ f \in OpenedFiles
    /\ OpenedFiles' = OpenedFiles \ {f}
    /\ ActualFileSize' = ActualFileSize
    /\ CcCache' = CcCache

Next ==
    \E f \in Files:
        \/ CreateFile(f)
        \/ WriteFile(f)
        \/ FlushFileBuffers(f)
        \/ GetFileInformation(f)
        \/ \E n \in 0..MaxFileSize: SetFileSize(f, n)
        \/ Close(f)

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* THE INVARIANT we want.                                                  *)
(*                                                                          *)
(* For every file with an open handle, the kernel's cached FileSize must   *)
(* equal the user-mode FS's actual size. (When no handle is open the       *)
(* kernel's cache for that file is irrelevant — the next open will         *)
(* repopulate via CreateFile/OpenFile callback response.)                  *)
(***************************************************************************)
CacheFileSizeMatchesActual ==
    \A f \in Files:
        f \in OpenedFiles => CcCache[f].FileSize = ActualFileSize[f]

==============================================================================
