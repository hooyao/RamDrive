## 1. WinFsp.Native

- [x] 1.1 Add by-name reparse query and native find/resolve callback wiring, buffer-size handling and AOT-safe ABI declarations.
- [x] 1.2 Add binding tests for resolution, intermediate paths and non-reparse behavior.

## 2. RamDrive

- [x] 2.1 Store and validate reparse metadata atomically with namespace mutations; expose get/set/delete and metadata.
- [x] 2.2 Enable WinFsp reparse support and by-name lookup, preserve target access checks and cache notifications.
- [x] 2.3 Reference the released binding package for clean builds and adapt consumers to its API.

## 3. Verification

- [x] 3.1 Add unit tests for data validation, tag mismatches, metadata and concurrent mutations.
- [x] 3.2 Add UNC integration tests for junctions, file/directory symlinks, relative/absolute/cross-volume targets, broken/cyclic links, renames, cache and error handling.
- [x] 3.3 Run OpenSpec validation, builds, relevant binding tests, full RamDrive unit and integration suites, and AOT publish where available.
- [x] 3.4 Document behavior, limitations and the binding dependency.
- [x] 3.5 Model reparse mutation and bounded resolution, check negative controls, and run the existing minimal system model.
- [x] 3.6 Exercise the published AOT executable on a real temporary drive-letter mount and unmount it cleanly.
- [x] 3.7 Run the existing standard system model to completion.

## 4. Review fixes

- [x] 4.1 Preserve all Windows UTF-16 path code units when synthesizing resolution data.
- [x] 4.2 Handle trailing separators in link targets without changing stored reparse data.
- [x] 4.3 Accept valid third-party GUID reparse buffers in WinFsp.Native and reject truncated ones.
- [x] 4.4 Run regression suites, bounded link models, AOT publish, and a real drive-letter test.

## 5. Publish and consume WinFsp.Native

- [x] 5.1 Merge the WinFsp.Native change after Copilot review and Windows x64/ARM64 CI.
- [x] 5.2 Configure trusted publishing and publish WinFsp.Native 0.1.3-pre.5 to NuGet.
- [x] 5.3 Replace the temporary source snapshot with the published NuGet package and verify the build and integration tests.

## 6. PR review follow-up

- [x] 6.1 Verify target ACL enforcement and target identity/cache coherence with mounted integration tests.
