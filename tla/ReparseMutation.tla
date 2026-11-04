------------------------- MODULE ReparseMutation -------------------------
\* Models reparse metadata, the namespace lock and the file content lock.
\* Tags abstract immutable reparse buffers. Zero means no reparse point.
\* One writer abstracts the page-pool write protocol, checked separately by
\* RamDiskSystem; only its content-lock boundary and file size matter here.
EXTENDS Naturals, TLC

CONSTANTS BrokenFileLock, BrokenDirLock
ASSUME /\ BrokenFileLock \in BOOLEAN /\ BrokenDirLock \in BOOLEAN

Tags == 1..2
VARIABLES tree, content, dirTag, child, fileTag, fileNonempty,
          fPhase, fReq, fValid, fWasEmpty,
          dPhase, dReq, dValid, dWasEmpty,
          wPhase, badFile, badReplace, badInvalid,
          seenTag, seenData, seenAttr

vars == <<tree, content, dirTag, child, fileTag, fileNonempty,
          fPhase, fReq, fValid, fWasEmpty,
          dPhase, dReq, dValid, dWasEmpty,
          wPhase, badFile, badReplace, badInvalid,
          seenTag, seenData, seenAttr>>

Init ==
    /\ tree = "none" /\ content = "none"
    /\ dirTag = 0 /\ child = FALSE
    /\ fileTag = 0 /\ fileNonempty = FALSE
    /\ fPhase = "idle" /\ fReq = 1 /\ fValid = TRUE /\ fWasEmpty = TRUE
    /\ dPhase = "idle" /\ dReq = 1 /\ dValid = TRUE /\ dWasEmpty = TRUE
    /\ wPhase = "idle"
    /\ badFile = FALSE /\ badReplace = FALSE /\ badInvalid = FALSE
    /\ seenTag = 0 /\ seenData = 0 /\ seenAttr = FALSE

\* SetReparsePoint(file): acquire structure, then content; retain content
\* lock from the length check through publication of the immutable buffer.
StartFileSet(tag, valid) ==
    /\ tree = "none" /\ fPhase = "idle"
    /\ tree' = "file" /\ fPhase' = "wait" /\ fReq' = tag /\ fValid' = valid
    /\ UNCHANGED <<content, dirTag, child, fileTag, fileNonempty, fWasEmpty,
                   dPhase, dReq, dValid, dWasEmpty, wPhase, badFile,
                   badReplace, badInvalid, seenTag, seenData, seenAttr>>

CheckFile ==
    /\ tree = "file" /\ fPhase = "wait" /\ content = "none"
    /\ fWasEmpty' = ~fileNonempty
    /\ content' = IF BrokenFileLock THEN "none" ELSE "file"
    /\ fPhase' = "checked"
    /\ UNCHANGED <<tree, dirTag, child, fileTag, fileNonempty, fReq, fValid,
                   dPhase, dReq, dValid, dWasEmpty, wPhase, badFile,
                   badReplace, badInvalid, seenTag, seenData, seenAttr>>

CommitFile ==
    /\ tree = "file" /\ fPhase = "checked"
    /\ IF BrokenFileLock THEN content = "none" ELSE content = "file"
    /\ LET success == fValid /\ fWasEmpty /\ (fileTag = 0 \/ fileTag = fReq)
       IN /\ fileTag' = IF success THEN fReq ELSE fileTag
          /\ badFile' = (badFile \/ (success /\ fileNonempty))
          /\ badReplace' = (badReplace \/ (success /\ fileTag # 0 /\ fileTag # fReq))
          /\ badInvalid' = (badInvalid \/ (success /\ ~fValid))
    /\ content' = "none" /\ fPhase' = "unlock"
    /\ UNCHANGED <<tree, dirTag, child, fileNonempty, fReq, fValid, fWasEmpty,
                   dPhase, dReq, dValid, dWasEmpty, wPhase, seenTag, seenData, seenAttr>>

ReleaseFile ==
    /\ tree = "file" /\ fPhase = "unlock"
    /\ tree' = "none" /\ fPhase' = "idle"
    /\ UNCHANGED <<content, dirTag, child, fileTag, fileNonempty, fReq, fValid, fWasEmpty,
                   dPhase, dReq, dValid, dWasEmpty, wPhase, badFile,
                   badReplace, badInvalid, seenTag, seenData, seenAttr>>

\* Write scan/rent are outside the final content write lock; they do not
\* change length. A writer may have started before SetReparsePoint.
StartWrite ==
    /\ wPhase = "idle" /\ wPhase' = "ready"
    /\ UNCHANGED <<tree, content, dirTag, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

EnterWrite ==
    /\ wPhase = "ready" /\ content = "none"
    /\ wPhase' = "holding" /\ content' = "writer"
    /\ UNCHANGED <<tree, dirTag, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

CommitWrite ==
    /\ wPhase = "holding" /\ content = "writer"
    /\ fileNonempty' = TRUE /\ wPhase' = "idle" /\ content' = "none"
    /\ UNCHANGED <<tree, dirTag, child, fileTag, fPhase, fReq, fValid, fWasEmpty,
                   dPhase, dReq, dValid, dWasEmpty, badFile, badReplace, badInvalid,
                   seenTag, seenData, seenAttr>>

Truncate ==
    /\ content = "none" /\ fileNonempty
    /\ fileNonempty' = FALSE
    /\ UNCHANGED <<tree, content, dirTag, child, fileTag, fPhase, fReq, fValid, fWasEmpty,
                   dPhase, dReq, dValid, dWasEmpty, wPhase, badFile, badReplace, badInvalid,
                   seenTag, seenData, seenAttr>>

\* Directory link mutations and child creation share the structure lock.
StartDirSet(tag, valid) ==
    /\ tree = "none" /\ dPhase = "idle"
    /\ tree' = "dir" /\ dPhase' = "checking" /\ dReq' = tag /\ dValid' = valid
    /\ UNCHANGED <<content, dirTag, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dWasEmpty, wPhase,
                   badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

CheckDir ==
    /\ tree = "dir" /\ dPhase = "checking"
    /\ dWasEmpty' = ~child /\ dPhase' = "checked"
    /\ tree' = IF BrokenDirLock THEN "none" ELSE "dir"
    /\ UNCHANGED <<content, dirTag, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dReq, dValid, wPhase,
                   badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

CommitDir ==
    /\ dPhase = "checked"
    /\ IF BrokenDirLock THEN tree = "none" ELSE tree = "dir"
    /\ LET success == dValid /\ dWasEmpty /\ (dirTag = 0 \/ dirTag = dReq)
       IN /\ dirTag' = IF success THEN dReq ELSE dirTag
          /\ badReplace' = (badReplace \/ (success /\ dirTag # 0 /\ dirTag # dReq))
          /\ badInvalid' = (badInvalid \/ (success /\ ~dValid))
    /\ tree' = "none" /\ dPhase' = "idle"
    /\ UNCHANGED <<content, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dReq, dValid, dWasEmpty, wPhase,
                   badFile, seenTag, seenData, seenAttr>>

CreateChild ==
    /\ tree = "none" /\ dirTag = 0 /\ ~child
    /\ child' = TRUE
    /\ UNCHANGED <<tree, content, dirTag, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   wPhase, badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

RemoveChild ==
    /\ tree = "none" /\ child
    /\ child' = FALSE
    /\ UNCHANGED <<tree, content, dirTag, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   wPhase, badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

DeleteTag(which) ==
    /\ tree = "none"
    /\ IF which = "file" THEN /\ fileTag # 0 /\ fileTag' = 0 /\ UNCHANGED dirTag
       ELSE /\ dirTag # 0 /\ dirTag' = 0 /\ UNCHANGED fileTag
    /\ UNCHANGED <<tree, content, child, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   wPhase, badFile, badReplace, badInvalid, seenTag, seenData, seenAttr>>

\* GetReparseMetadata reads one immutable buffer reference; attributes and tag
\* in the result are derived from that same snapshot even if a later set occurs.
Observe(which) ==
    /\ LET snapshot == IF which = "file" THEN fileTag ELSE dirTag
       IN /\ seenTag' = snapshot /\ seenData' = snapshot /\ seenAttr' = (snapshot # 0)
    /\ UNCHANGED <<tree, content, dirTag, child, fileTag, fileNonempty,
                   fPhase, fReq, fValid, fWasEmpty, dPhase, dReq, dValid, dWasEmpty,
                   wPhase, badFile, badReplace, badInvalid>>

Next ==
    \/ \E tag \in Tags, valid \in BOOLEAN : StartFileSet(tag, valid) \/ StartDirSet(tag, valid)
    \/ CheckFile \/ CommitFile \/ ReleaseFile
    \/ StartWrite \/ EnterWrite \/ CommitWrite \/ Truncate
    \/ CheckDir \/ CommitDir \/ CreateChild \/ RemoveChild
    \/ \E which \in {"file", "dir"} : DeleteTag(which) \/ Observe(which)

Fairness == /\ SF_vars(CheckFile) /\ WF_vars(CommitFile) /\ WF_vars(ReleaseFile)
            /\ WF_vars(CommitWrite) /\ WF_vars(CheckDir) /\ SF_vars(CommitDir)
Spec == Init /\ [][Next]_vars /\ Fairness

TypeOK ==
    /\ tree \in {"none", "file", "dir"} /\ content \in {"none", "file", "writer"}
    /\ dirTag \in 0..2 /\ fileTag \in 0..2 /\ child \in BOOLEAN /\ fileNonempty \in BOOLEAN
    /\ fPhase \in {"idle", "wait", "checked", "unlock"} /\ fReq \in Tags
    /\ dPhase \in {"idle", "checking", "checked"} /\ dReq \in Tags
    /\ fValid \in BOOLEAN /\ fWasEmpty \in BOOLEAN /\ dValid \in BOOLEAN /\ dWasEmpty \in BOOLEAN
    /\ wPhase \in {"idle", "ready", "holding"}
    /\ badFile \in BOOLEAN /\ badReplace \in BOOLEAN /\ badInvalid \in BOOLEAN
    /\ seenTag \in 0..2 /\ seenData \in 0..2 /\ seenAttr \in BOOLEAN

DirectoryLinkHasNoChildren == dirTag # 0 => ~child
FileSetWasEmpty == ~badFile
RejectWrongTagAndMalformedData == ~badReplace /\ ~badInvalid
MetadataSnapshotConsistent == seenTag = seenData /\ (seenAttr = (seenTag # 0))
LockOwnershipConsistent ==
    /\ (fPhase # "idle") = (tree = "file")
    /\ (wPhase = "holding") = (content = "writer")
    /\ content = "file" => tree = "file" /\ fPhase = "checked"
    /\ ~BrokenDirLock => ((dPhase # "idle") = (tree = "dir"))

FileSetTerminates == (fPhase # "idle") ~> (fPhase = "idle")
DirSetTerminates == (dPhase # "idle") ~> (dPhase = "idle")
=============================================================================
