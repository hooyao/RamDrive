# WinFsp.Net — 从零构建 WinFSP 的 .NET 绑定

本文档记录了为 WinFSP（Windows File System Proxy）编写现代 .NET 10 绑定库的完整过程，
包括架构设计、实现细节、踩过的所有坑、以及最终验证通过的方案。

目标读者：想从零复现这个绑定库，或想理解 WinFSP .NET interop 细节的开发者。

---

## 1. 前提条件

- Windows 10/11
- .NET 10 SDK
- [WinFSP](https://winfsp.dev/rel/) 2.x 已安装（需要安装 Developer files）
- Visual Studio Build Tools（AOT publish 需要 C++ linker）
- `vswhere.exe` 在 PATH 中（AOT publish 需要。通常在 `C:\Program Files (x86)\Microsoft Visual Studio\Installer\`）

验证 WinFSP 安装：
```powershell
# WinFSP Launcher 服务应在运行
Get-Service WinFsp.Launcher
# 原生 memfs 应可工作
& "C:\Program Files (x86)\WinFsp\bin\memfs-x64.exe" -m T:
# 另一个终端：dir T:\ 应该看到空目录
```

---

## 2. 架构

```
    ┌─────────────────────────────────────────────────────┐
    │                    用户代码                          │
    │                                                     │
    │  ┌──── 选择 A ────┐    ┌──── 选择 B ──────────────┐ │
    │  │ IFileSystem     │    │ WinFspFileSystem         │ │
    │  │ (高级接口)      │    │ (low-level raw API)      │ │
    │  │ ValueTask,      │    │ delegate → nint slot     │ │
    │  │ string,         │    │ nint, char*, NTSTATUS    │ │
    │  │ Memory<byte>    │    │ FspFullContext*           │ │
    │  └────────┬────────┘    └──────────┬───────────────┘ │
    └───────────┼────────────────────────┼─────────────────┘
                │                        │
    ════════════╪════════ HIGH ══════════╪════════════════
                │                        │
    ┌───────────▼────────────────┐       │
    │  FileSystemHost            │       │
    │  IFileSystem → delegate    │       │
    │  STATUS_PENDING 管理       │       │
    │  buffer pool, cancel       │       │
    └───────────┬────────────────┘       │
                │ 内部使用               │
    ════════════╪════════ LOW ═══════════╪════════════════
                │                        │
    ┌───────────▼────────────────────────▼───────────────┐
    │  WinFspFileSystem                                  │
    │  VolumeParams + InterfacePtr + Mount/Unmount       │
    └───────────┬────────────────────────────────────────┘
                │ P/Invoke (LibraryImport)
    ┌───────────▼────────────────────────────────────────┐
    │  Native: FspApi + FspStructs + NtStatus            │
    └───────────┬────────────────────────────────────────┘
                │ winfsp-x64.dll (runtime, GPLv3)
    ┌───────────▼───────────────┐
    │  WinFSP kernel driver     │
    └───────────────────────────┘
```

**Low-Level API**（`WinFspFileSystem`）：零抽象，用户直接填函数指针到 64-slot struct，自己处理一切。
适合从 C/C++ 移植 WinFSP 实现，或追求极致性能。

**High-Level API**（`IFileSystem` + `FileSystemHost`）：Dokan 风格接口，ValueTask async，
框架处理 STATUS_PENDING / buffer pool / cancellation。适合正常开发。

---

## 3. WinFSP 核心概念（写 .NET binding 之前必须理解）

### 3.1 回调函数表 FSP_FILE_SYSTEM_INTERFACE

WinFSP 的核心数据结构。64 个函数指针槽位，每个 8 字节（x64），共 512 字节。

```
slot  0: GetVolumeInfo
slot  1: SetVolumeLabel
slot  2: GetSecurityByName     ← access check 时调用
slot  3: Create                ← 创建新文件/目录
slot  4: Open                  ← 打开已有文件/目录
slot  5: Overwrite             ← 截断/覆写文件
slot  6: Cleanup               ← 最后一个 handle 关闭
slot  7: Close                 ← 所有引用释放
slot  8: Read
slot  9: Write
slot 10: Flush
slot 11: GetFileInfo
slot 12: SetBasicInfo
slot 13: SetFileSize
slot 14: CanDelete
slot 15: Rename
slot 16: GetSecurity
slot 17: SetSecurity
slot 18: ReadDirectory
slot 19: ResolveReparsePoints
slot 20: GetReparsePoint
...
slot 27: CreateEx              ← Create 的扩展版（支持 EA/reparse）
slot 28: OverwriteEx           ← Overwrite 的扩展版
...
slot 32: DispatcherStopped
slots 33-63: Reserved
```

null 槽位 → WinFSP 自动返回 `STATUS_INVALID_DEVICE_REQUEST`。

### 3.2 三大必填回调（最大的坑）

**fsop.c line 907-910 硬编码检查：**

```c
if ((0 == Interface->Create && 0 == Interface->CreateEx) ||
    0 == Interface->Open ||
    (0 == Interface->Overwrite && 0 == Interface->OverwriteEx))
    return STATUS_INVALID_DEVICE_REQUEST;
```

`Create`（或 `CreateEx`）+ `Open` + `Overwrite`（或 `OverwriteEx`）**必须全部非 null**。

缺少任何一个 → drive 挂载成功（`Mount()` 返回 0）但**完全不可访问**。
`dir` / `type` / Explorer 全部返回 "Incorrect function" 或 "drive not found"。
WinFSP debug log 显示 `<<Create IoStatus=c0000010`（STATUS_INVALID_DEVICE_REQUEST）。

**症状极其隐蔽**：mount 成功、dispatcher 启动、盘符出现——但没有任何请求能到达 Open 回调。
排查时容易误以为是 function pointer、struct layout、或 .NET runtime 的问题。

**即使是只读 FS 也需要：**
- `Create` → 返回 `STATUS_OBJECT_NAME_NOT_FOUND`（不允许创建新文件）
- `Overwrite` → 返回 `STATUS_MEDIA_WRITE_PROTECTED`（不允许覆写）

### 3.3 `FspFileSystemCreate` 的行为

```c
NTSTATUS FspFileSystemCreate(
    PWSTR DevicePath,               // "WinFsp.Disk" 或 "WinFsp.Net"
    const FSP_FSCTL_VOLUME_PARAMS*, // 504 字节配置结构，按值拷贝
    const FSP_FILE_SYSTEM_INTERFACE*, // ⚠ 存储的是指针，不拷贝内容！
    FSP_FILE_SYSTEM**
);
```

**Interface 指针不拷贝**：你分配的 512 字节 interface struct 内存必须在 FS 整个生命周期内有效。

### 3.4 FSP_FILE_SYSTEM 内存布局（x64, 792 字节）

```
offset   0: UINT16 Version
offset   8: PVOID UserContext          ← 你的自定义指针
offset  16: WCHAR VolumeName[256]      ← 512 字节！（不是 128）
offset 528: HANDLE VolumeHandle
offset 536: EnterOperation*
offset 544: LeaveOperation*
offset 552: Operations[22]             ← 内部用，176 字节
offset 728: const INTERFACE* Interface ← 你传入的指针
offset 736: ...
```

**`VolumeName` 是 512 字节**（`FSP_FSCTL_VOLUME_NAME_SIZEMAX = (64+192)*2 = 512`），
不是直觉上的 128 字节。这导致 `Interface` 在 offset 728 而不是 344。

### 3.5 GetSecurityByName + PersistentAcls

| PersistentAcls | GetSecurityByName 槽位 | 行为 |
|---|---|---|
| false | null | 跳过 access check，grant all |
| false | 非 null | 调回调，但 `pSdSize=0` 时跳过 check |
| true | 非 null | 完整 Windows access check（需要返回真实 SD） |
| true | null | 跳过 access check |

**简单 FS 推荐**：不设 `PersistentAcls`，`GetSecurityByName` 返回 `*pAttr = attributes, *pSdSize = 0`。

### 3.6 Debug Logging

WinFSP 有内置的详细日志，默认输出到 Win32 `OutputDebugString`（需要 DebugView 查看）。
重定向到 stderr：

```csharp
WinFspFileSystem.SetDebugLogToStderr();        // 在 Mount 之前调用
fs.Mount("M:", debugLog: uint.MaxValue);        // 开启所有日志
```

日志格式：
```
HelloFs[TID=1234]: >>Create "\", FILE_OPEN, ...    ← 请求进入
HelloFs[TID=1234]: <<Create IoStatus=c0000010[0]   ← 请求返回（NTSTATUS）
```

---

## 4. 回调机制：delegate vs function pointer（最重要的教训）

### 4.1 两种 .NET → native 回调方式

| 方式 | 语法 | AOT | 原理 |
|------|------|-----|------|
| `[UnmanagedCallersOnly]` | `&StaticMethod` | ✅ | 编译时生成 reverse P/Invoke stub |
| `[UnmanagedFunctionPointer]` delegate | `Marshal.GetFunctionPointerForDelegate<T>(d)` | ✅（泛型版本） | runtime 生成 thunk |

### 4.2 我们的验证结果

最初使用 `[UnmanagedCallersOnly]` function pointer 填充 interface struct。**Mount 成功但所有请求返回 `c0000010`。**

经过大量调试（hex dump struct 内容、验证 slot 地址、在回调内写文件跟踪），最终确认：
**真正原因是缺少 Create + Overwrite 必填回调（§3.2），不是 `[UnmanagedCallersOnly]` 的问题。**

但在调试过程中切换到了 delegate 方式，在 AOT 下同样验证通过。

### 4.3 AOT 下的 delegate 用法

```csharp
// ✅ 正确：泛型版本，AOT safe
nint ptr = Marshal.GetFunctionPointerForDelegate<OpenDelegate>(openDelegate);

// ❌ 错误：非泛型版本，AOT 下产生 IL3050 warning
nint ptr = Marshal.GetFunctionPointerForDelegate((Delegate)openDelegate);
```

**delegate 必须 pin**，否则 GC 回收后 function pointer 变成野指针：
```csharp
GCHandle.Alloc(openDelegate); // 只需 Alloc，不需要 Pinned
```

### 4.4 回调模板

```csharp
// 1. 定义 delegate 类型（签名必须和 C 完全匹配）
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int OpenDelegate(nint fs, char* fileName, uint createOptions,
    uint grantedAccess, FspFullContext* ctx, FspFileInfo* pFileInfo);

// 2. 实现回调方法
static int OnOpen(nint fs, char* fileName, uint co, uint ga,
    FspFullContext* ctx, FspFileInfo* pFi)
{
    string name = new(fileName);
    // ... 你的逻辑
    return NtStatus.Success;
}

// 3. Pin delegate 并获取 function pointer
var d = new OpenDelegate(OnOpen);
GCHandle.Alloc(d);
nint fptr = Marshal.GetFunctionPointerForDelegate(d);

// 4. 写入 interface struct 的 slot 4（Open）
nint* slots = (nint*)fs.InterfacePtr;
slots[4] = fptr;
```

---

## 5. 从零复现 HelloFs（完整步骤）

### 5.1 项目结构

```
src/WinFsp.Net/                      WinFsp .NET 绑定库（通用，不依赖 RamDrive）
├── Native/
│   ├── NtStatus.cs                  NTSTATUS 常量
│   ├── CleanupFlags.cs              Cleanup 标志枚举
│   ├── CreateOptions.cs             NtCreateFile 选项
│   ├── FspStructs.cs                blittable 结构体（VolumeParams, FileInfo, DirInfo, ...）
│   ├── FspFileSystemInterface.cs    64 slot function pointer struct
│   └── FspApi.cs                    LibraryImport P/Invoke + DLL resolver
├── WinFspFileSystem.cs              Low-level API: thin shell around native FSP_FILE_SYSTEM
├── IFileSystem.cs                   High-level interface (ValueTask async, SynchronousIo 属性)
├── FileSystemHost.cs                High-level host (回调分发 + 零拷贝优化 + 异步管理)
├── FileOperationInfo.cs             per-handle 状态 + CancellationToken
├── IoResults.cs                     ReadResult / WriteResult / FsResult / CreateResult
└── PinnedBufferPool.cs              POH pinned array 两级池（ThreadLocal + ConcurrentQueue）

src/RamDrive.Core/                   RAM 文件系统核心（纯内存操作，不依赖 WinFsp）
├── Configuration/
│   └── RamDriveOptions.cs           配置项（MountPoint, CapacityMb, PageSizeKb, EnableKernelCache, ...）
├── FileSystem/
│   ├── RamFileSystem.cs             目录树 + 路径解析 + CRUD（全局 _structureLock）
│   └── FileNode.cs                  文件/目录元数据 + PagedFileContent
└── Memory/
    ├── PagePool.cs                  NativeMemory 64KB 页池（ConcurrentStack LIFO, CAS 容量控制）
    └── PagedFileContent.cs          nint[] 页表 + 三阶段写 + ReaderWriterLockSlim

src/RamDrive.Cli/                    启动器 + WinFsp 适配器
├── Program.cs                       DI 容器配置 + Host 启动
├── WinFspRamAdapter.cs              实现 IFileSystem，桥接 RamFileSystem ↔ WinFsp
├── WinFspHostedService.cs           BackgroundService 生命周期（Mount/Unmount）
└── appsettings.jsonc                运行时配置

tests/RamDrive.Benchmarks/           BenchmarkDotNet 性能测试
├── PagedFileContentBenchmark.cs     Core 层：直接测 PagedFileContent.Read/Write
├── OnReadWriteBenchmark.cs          模拟 FileSystemHost.OnRead/OnWrite 完整调用链
├── WinFspEndToEndBenchmark.cs       E2E：FileStream 通过 WinFsp 挂载盘
└── Program.cs                       入口（dotnet run -- [onread|e2e]）

examples/HelloFs/                    最小只读 FS 示例（Low-level API）
└── Program.cs
```

### 5.2 构建 & 运行

```bash
# JIT 模式
dotnet run --project examples/HelloFs

# AOT 模式（需要 vswhere 在 PATH 中）
export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"
dotnet publish examples/HelloFs/HelloFs.csproj -c Release -r win-x64 -o publish-aot
./publish-aot/HelloFs.exe
```

### 5.3 预期输出

```
Mounted at M:
Directory.Exists(M:\): True
Files: [M:\hello.txt]
Content of M:\hello.txt: "example"
Press Enter to unmount...
```

**注意**：M: 可能被占用。改成任何未使用的盘符。检查占用：
```powershell
[System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name }
```

### 5.4 HelloFs 实现的最小回调集

| slot | 回调 | 用途 | 必填？ |
|------|------|------|--------|
| 0 | GetVolumeInfo | 返回卷大小和标签 | 推荐 |
| 2 | GetSecurityByName | 路径查找 + 返回文件属性 | 推荐 |
| 3 | Create | 拒绝创建新文件（只读 FS） | **必填** |
| 4 | Open | 打开根目录 / hello.txt | **必填** |
| 5 | Overwrite | 拒绝覆写（只读 FS） | **必填** |
| 6 | Cleanup | 空实现 | 推荐 |
| 7 | Close | 空实现 | 推荐 |
| 8 | Read | 返回 "example" 内容 | 按需 |
| 11 | GetFileInfo | 返回文件/目录元数据 | 推荐 |
| 18 | ReadDirectory | 枚举目录内容（hello.txt） | 按需 |

### 5.5 VolumeParams 最小配置

```csharp
fs.VolumeParams.SectorSize = 512;
fs.VolumeParams.SectorsPerAllocationUnit = 1;
fs.VolumeParams.MaxComponentLength = 255;
fs.VolumeParams.CasePreservedNames = true;
fs.VolumeParams.UnicodeOnDisk = true;
fs.VolumeParams.UmFileContextIsFullContext = true;
fs.VolumeParams.SetFileSystemName("HelloFs");
```

---

## 6. AOT 兼容性总结

| 组件 | AOT 策略 | 验证状态 |
|------|---------|---------|
| P/Invoke | `[LibraryImport]` source-gen | ✅ |
| 回调 | `[UnmanagedFunctionPointer]` + `Marshal.GetFunctionPointerForDelegate<T>` | ✅ |
| struct | 全部 blittable (`LayoutKind.Sequential` / `Explicit`, `fixed` arrays) | ✅ |
| DLL 加载 | `NativeLibrary.SetDllImportResolver` + Registry fallback | ✅ |
| HelloFs.exe | `dotnet publish -c Release -r win-x64` → 2 MB native binary | ✅ |

---

## 7. 踩坑清单（按被坑的时间顺序）

| # | 坑 | 症状 | 原因 | 花费时间 |
|---|---|---|---|---|
| 1 | Drive 挂载成功但完全不可访问 | `dir M:\` → "Incorrect function"；`Directory.Exists` → False | 缺少 Create 和/或 Overwrite 必填回调 | 数小时 |
| 2 | `VolumeName` 数组大小计算错误 | Interface 指针在错误的 offset（344 vs 728） | `FSP_FSCTL_VOLUME_NAME_SIZEMAX = 512 bytes`，不是 128 | 1 小时 |
| 3 | WinFSP debug log 不可见 | mount 后无日志输出 | 默认输出到 `OutputDebugString`，需要 `SetDebugLogToStderr()` 重定向 | 30 分钟 |
| 4 | `Marshal.GetFunctionPointerForDelegate` AOT warning | IL3050: RequiresDynamicCode | 非泛型版本不 AOT safe，改用 `<T>` 泛型版本 | 5 分钟 |
| 5 | AOT publish 失败 "vswhere not recognized" | link.exe 找不到 | `vswhere.exe` 不在 PATH，加 `C:\...\Microsoft Visual Studio\Installer\` | 5 分钟 |
| 6 | 旧进程占用盘符 | Mount 返回 `STATUS_OBJECT_NAME_COLLISION` (0xC0000035) | 上次运行被 kill -9 没有正确 unmount，幽灵占用 | 10 分钟 |
| 7 | bash 子进程里 `dir` 无输出 | cmd.exe 在 non-interactive pipe 模式 | 用 Python `os.path.exists()` 或 .NET `Directory.Exists()` 替代 | 20 分钟 |
| 8 | `SetMountPointEx("R:\")` 崩溃 (AV) | 0xC0000005 in `FspFileSystemSetMountPointEx` | `"R:\"` 触发未初始化的 Mount Manager 路径，传 `"R:"` 用 `DefineDosDevice` | 1 小时 |
| 9 | ATTO 找不到盘符 | ATTO Disk Benchmark 看不到 R: | `DefineDosDevice` 不注册到 Volume Mount Manager。传 `"\\.\R:"` 走 Mount Manager 路径（需要 admin） | 30 分钟 |
| 10 | Read 128KB I/O error | `cat` 报 Input/output error | `UnmanagedBufferPool` block size 64KB，WinFsp 发来 128KB 请求 → `RentAsMemory` throw → `HandleException` → `STATUS_UNEXPECTED_IO_ERROR` | 1 小时 |
| 11 | DotNext `UnmanagedMemoryPool<T>` 不是真池 | 性能比手搓版差 | 每次 `Rent()` 都 `NativeMemory.Alloc`，`Dispose()` 都 `Free`，没有复用。改回自建池 | 30 分钟 |
| 12 | Read 在 ATTO 中远慢于 Write | Write 9 GB/s，Read 6 GB/s | WinFsp kernel Read 路径固有的 user-kernel round-trip 开销（BenchmarkDotNet 证实 Core 层 Read 比 Write 快 2 倍） | 调研 |
| 13 | Read 远慢于 memfs（3.8 vs 9.5 GB/s） | dd benchmark 对比发现巨大差距 | memfs 设了 `FileInfoTimeout=-1` 启用 Windows 内核页缓存。cached I/O 的 Read 直接从 OS 缓存读，不回用户态 | 1 小时 |

---

## 8. 挂载点格式与 Mount Manager

WinFsp 的 `FspFileSystemSetMountPointEx` 根据 mount point 字符串格式选择不同的挂载方式：

| 格式 | 路由 | 行为 |
|------|------|------|
| `"X:"` | `FspPathIsDrive` → `DefineDosDevice` | 快速，但 ATTO 等磁盘工具**看不到** |
| `"\\.\X:"` 或 `"\\?\X:"` | `FspPathIsMountmgrDrive` → Mount Manager | 通过 Volume Mount Manager 注册，所有应用可见。**需要管理员权限** |
| `"X:\"` | ❌ crash (0xC0000005) | 触发了未初始化的 Mount Manager 路径，**不要用** |
| 目录路径 | `FspMountSet_Directory` | Reparse point junction |

**RamDrive 选择**：`WinFspHostedService` 将配置的 `MountPoint`（如 `R:\`）转换为 `\\.\R:` 格式，走 Mount Manager 路径。

判断逻辑（来自 `src/dll/library.h`）：
- `FspPathIsDrive`: `isalpha(s[0]) && s[1]==':' && s[2]=='\0'`
- `FspPathIsMountmgrDrive`: `s[0]=='\\' && s[1]=='\\' && (s[2]=='?'||s[2]=='.') && s[3]=='\\' && isalpha(s[4]) && s[5]==':' && s[6]=='\0'`

---

## 9. 缓冲区池设计演进

### 9.1 初版：手搓 `UnmanagedBufferPool`

- `NativeMemory.AlignedAlloc(blockSize, 4096)` 分配页对齐的 native memory
- `ConcurrentStack<nint>` LIFO free list 复用
- `MemoryManager<byte>` 子类包装，支持 `IMemoryOwner<byte>` 接口
- **问题**：固定 64KB block size，128KB+ 请求直接 throw

### 9.2 踩坑：DotNext `UnmanagedMemoryPool<T>`

名字叫 Pool 但**不是真正的池**——每次 `Rent()` 都 `NativeMemory.Alloc`，`Dispose()` 都 `Free`。
ATTO benchmark 性能明显下降。

### 9.3 当前方案：`PinnedBufferPool`（两级缓存）

使用 `GC.AllocateArray<byte>(size, pinned: true)` 在 Pinned Object Heap (POH) 分配。
不需要 `unsafe`，不需要手动 `Free`，GC 管理生命周期。

**两级缓存架构：**

```
Thread A:  [ThreadLocal cache: 1 buf/tier]  ←→  ┌─────────────────────────┐
Thread B:  [ThreadLocal cache: 1 buf/tier]  ←→  │  Shared ConcurrentQueue  │ (per tier)
Thread C:  [ThreadLocal cache: 1 buf/tier]  ←→  │  + Interlocked count cap │
                                                 └─────────────────────────┘
                                                          ↕ (miss)
                                                 GC.AllocateArray (pinned)
```

**Rent 路径：**
1. ThreadLocal cache 命中 → **零 CAS，~1ns**
2. miss → ConcurrentQueue.TryDequeue → **一次 CAS，~10ns**
3. miss → `GC.AllocateArray<byte>(size, pinned: true)` → **~100ns+**

**Return 路径：**
1. ThreadLocal cache 空 → 放入 → **零 CAS**
2. cache 已满 → ConcurrentQueue.Enqueue（if count < cap）→ **一次 CAS**
3. queue 满 → 丢弃，让 GC 回收

**可配置 tier：**
```csharp
// 构造时传入 Tier[] 配置（自动按 BlockSize 升序排序）
var pool = new PinnedBufferPool([
    new(64 * 1024,      32),  // 64 KB  × 32 =  2 MB
    new(256 * 1024,     16),  // 256 KB × 16 =  4 MB
    new(1024 * 1024,     8),  // 1 MB   ×  8 =  8 MB
    new(4 * 1024 * 1024, 4),  // 4 MB   ×  4 = 16 MB
]);
// 或使用预置配置
var pool = new PinnedBufferPool(PinnedBufferPool.DefaultTiers);
var pool = new PinnedBufferPool(PinnedBufferPool.MinimalTiers);
```

**线程安全：**
- ThreadLocal cache 用 `ThreadLocal<byte[]?[]>` 实例字段（非 `[ThreadStatic]`），每个 pool 实例独立
- Shared pool 用 `ConcurrentQueue<byte[]>` + `Interlocked` count cap，完全 lock-free MPMC

**超过最大 tier 的请求** → 一次性 `GC.AllocateArray`，不入池，GC 回收

### 9.4 同步 I/O 零拷贝优化

对于 `SynchronousIo = true` 的 FS（如 RamDrive），`FileSystemHost` 用 thread-static `NativeBufferMemory` 直接包装 WinFsp kernel buffer，完全跳过 `PinnedBufferPool`：

```
同步 Read:  kernel buffer → NativeBufferMemory 包装 → ReadFile 直接写入 → 返回
同步 Write: kernel buffer → NativeBufferMemory 包装 → WriteFile 直接读取 → 返回
```

零 rent、零 copy、零 alloc。`PinnedBufferPool` 只在异步 FS（`SynchronousIo = false`）时使用。

`IFileSystem.SynchronousIo` 属性：
- `true`：Read/Write 的 `ValueTask` 总是同步完成，启用零拷贝快速路径
- `false`（默认）：安全的异步路径，通过 pooled buffer 中转

---

## 10. FileInfoTimeout 与 Windows 内核缓存

### 10.1 性能影响

| FileInfoTimeout | cached Read | cached Write | Direct I/O Read | Direct I/O Write |
|-----------------|-------------|--------------|-----------------|------------------|
| 0（默认） | ~3.8 GB/s | ~2.1 GB/s | ~6 GB/s | ~9 GB/s |
| -1 (无限) | **~9.5 GB/s** | **~3.6 GB/s** | ~6 GB/s | ~9 GB/s |

### 10.2 原理

`FileInfoTimeout=-1` 让 WinFsp 启用 Windows 文件系统缓存管理器的**完整页缓存**：
- **cached Read**：首次 Read 回到用户态回调取数据；后续 Read 直接从 OS 页缓存读取，不再回用户态
- **cached Write**：写入 OS 页缓存后立即返回；cache manager 异步回写到用户态 FS
- **Direct I/O**：绕过缓存，始终走完整 user-kernel round-trip，不受影响

### 10.3 配置

RamDrive 通过 `appsettings.jsonc` 的 `EnableKernelCache` 控制：

```jsonc
"EnableKernelCache": true   // → FileInfoTimeout = -1（默认开启）
```

对 RAM disk 完全安全——我们是唯一的数据源，无外部修改，缓存永不过期。

---

## 11. 性能 Benchmark 数据

测试环境：AMD Ryzen 9 5950X, 32GB DDR4, .NET 10.0.4 AOT, WinFsp 2.x

### 11.1 三层 Benchmark（256MB 顺序 I/O，GB/s）

| BlockSize | Core Write | Core Read | OnRead Write | OnRead Read | E2E Write | E2E Read |
|-----------|-----------|----------|-------------|------------|----------|---------|
| 64 KB | 14.2 | 27.2 | 13.9 | 25.1 | 1.65 | 2.18 |
| 128 KB | 14.7 | 27.4 | 14.4 | 27.2 | 2.14 | 1.56 |
| 1 MB | 14.4 | 27.2 | 14.3 | 27.4 | 4.25 | 2.69 |
| 4 MB | 14.4 | 27.3 | 14.1 | 27.3 | 4.67 | 2.88 |
| 16 MB | 13.7 | 24.9 | 13.6 | 26.1 | 4.66 | 3.02 |

- **Core**：`PagedFileContent.Read/Write` 直接调用
- **OnRead**：模拟 `FileSystemHost.OnRead` 完整调用链（GCHandle 解引用 + HandleState 查找 + NativeBufferMemory 包装 + IFileSystem dispatch）
- **E2E**：`FileStream` 通过 WinFsp 挂载盘（`FileInfoTimeout=0`，无缓存）

### 11.2 关键发现

1. **OnRead 层和 Core 层几乎一致**（128KB+ 差距 <3%）—— GCHandle/NativeBufferMemory/adapter 的开销可忽略
2. **128KB 处 OnRead Read 无断崖**（27.2 GB/s）—— 断崖只出现在 E2E
3. **E2E 的巨大 gap 100% 来自 WinFsp kernel round-trip**
4. **零 GC 分配**（`Allocated = 0`）—— 零分配设计到位
5. **开启 `EnableKernelCache` 后 E2E cached Read 达到 9.5 GB/s**——与 WinFsp 官方 memfs 持平

### 11.3 vs ImDisk（内核态 RAM disk）

| | ImDisk | RamDrive (Direct I/O) | RamDrive (Cached + KernelCache) |
|---|---|---|---|
| Read 峰值 | ~9 GB/s | ~6 GB/s | **~9.5 GB/s** |
| Write 峰值 | ~7.4 GB/s | **~9.2 GB/s** | ~5 GB/s |

- Cached Read 已追平内核态 ImDisk
- Direct I/O Write 超过 ImDisk（我们的 PagePool 内存管理更高效）
- Direct I/O Read 的差距是 WinFsp 架构限制（user-kernel transition）

---

## 12. RamDrive 端到端运行架构

### 12.1 启动流程

```
Program.cs (DI 容器)
    │
    ├── PagePool                         NativeMemory 64KB 页池
    ├── RamFileSystem(PagePool)          内存文件系统（目录树 + 路径解析）
    ├── WinFspRamAdapter(RamFileSystem)  IFileSystem 实现（回调桥接）
    └── WinFspHostedService              BackgroundService
            │
            ├── new FileSystemHost(adapter)
            ├── adapter.Init(host)       设置 SectorSize, FileInfoTimeout, PersistentAcls, FileSystemName 等
            ├── mount point 转换         "R:\" → TrimEnd('\\') → "\\.\R:" (Mount Manager)
            ├── host.Mount("\\.\R:")     → WinFspFileSystem.Mount → FspFileSystemCreate + SetMountPoint + StartDispatcher
            └── await Task.Delay(∞)      等待 Ctrl+C
```

### 12.2 I/O 数据流（以 Read 为例）

```
应用层: ReadFile("R:\test.txt", buffer, 4096)
    │
    ▼ (Windows 内核 IRP)
WinFsp kernel driver (winfsp-x64.sys)
    │
    ▼ (user-kernel transition) ← 性能瓶颈所在
FileSystemHost.OnRead [UnmanagedCallersOnly, Cdecl]
    │
    ├── Self(fs)                     GCHandle → FileSystemHost 引用 (~2ns)
    ├── H(ctx)                       GCHandle → HandleState 引用 (~2ns)
    │
    ├── if (SynchronousIo = true) ← RamDrive 走这条路
    │   ├── GetNativeBuffer(buffer, length)   ThreadStatic NativeBufferMemory.Reset (~1ns)
    │   ├── adapter.ReadFile(fileName, directBuf.Memory, offset, info, ct)
    │   │   ├── node = (FileNode)info.Context             (~1ns, 缓存的引用)
    │   │   ├── node.Content.Length                        (ReaderWriterLockSlim read lock)
    │   │   ├── node.Content.Read(offset, buffer.Span)    (read lock + memcpy per page)
    │   │   │   └── 每 64KB 页: native page → kernel buffer 直接 memcpy
    │   │   └── return ReadResult.Success(bytesRead)       (同步 ValueTask, 零 alloc)
    │   └── *pBt = bytesTransferred; return NTSTATUS       → 回到内核
    │
    └── else (异步 FS)
        ├── PinnedBufferPool.Rent(length)    ThreadLocal → ConcurrentQueue → AllocArray
        ├── adapter.ReadFile(..., owned.Memory, ...)
        ├── if (同步完成) → copy owned → kernel buffer → Dispose owned
        └── else → STATUS_PENDING → ContinueWith → FspFileSystemSendResponse
```

**零拷贝关键**：`SynchronousIo = true` 时，`NativeBufferMemory` 直接包装 WinFsp 的 kernel buffer 指针，
`PagedFileContent.Read` 的 `memcpy` 直接从 native page 写入 kernel buffer，省去中间 buffer 的 rent/copy。

### 12.3 内存布局

```
进程内存
├── Managed Heap
│   ├── RamFileSystem._root (FileNode tree)
│   ├── FileNode.Name, Attributes, Timestamps
│   └── PagedFileContent._pages (nint[] page table)  ← 唯一 managed 对象
│
├── NativeMemory (outside GC heap)
│   ├── PagePool: nint[] 页指针 → NativeMemory.AllocZeroed(64KB) 物理页
│   └── PagePool.ConcurrentStack<nint> free list
│
├── Pinned Object Heap (POH, GC 管理但不移动)
│   └── PinnedBufferPool: byte[] pinned arrays (仅异步 FS 使用)
│
└── WinFsp kernel buffers (内核态，由 WinFsp driver 管理)
    └── 每个 I/O 请求的 buffer 指针传入 OnRead/OnWrite
```

### 12.4 关键设计约束

1. **热路径零 managed allocation**：Read/Write/GetFileInfo 等高频回调不能在 managed heap 上分配对象
   - `ValueTask<T>` 同步返回 = 零 Task boxing
   - `FileNode` 通过 `FileOperationInfo.Context` 缓存 = 零 FindNode
   - `NativeBufferMemory` 是 ThreadStatic 复用 = 零 MemoryManager 创建
   - `ReadDirectory` 用 native buffer + `FspApi.AddDirInfo` = 零 IEnumerable

2. **文件数据全在 NativeMemory**：`PagePool` 分配的 64KB 页在 GC heap 之外，零 GC 压力

3. **锁策略**：
   - `RamFileSystem._structureLock` (global) — 只保护目录树结构变更（create/delete/move）
   - `PagedFileContent._lock` (per-file `ReaderWriterLockSlim`) — 保护文件内容读写
   - Write 三阶段最小化 write lock 持有时间：scan(read lock) → alloc(no lock) → write(write lock)

---

## 13. Next Steps

| 优先级 | 任务 | 状态 |
|--------|------|------|
| P0 | Low-Level HelloFs example（AOT 验证） | ✅ 完成 |
| P1 | FileSystemHost 高级 API（`[UnmanagedCallersOnly]` 回调） | ✅ 完成 |
| P2 | RamDrive WinFspRamAdapter (实现 IFileSystem) | ✅ 完成 |
| P3 | 从 Dokan 迁移到 WinFsp，移除 DokanNet 依赖 | ✅ 完成 |
| P4 | 性能优化：PinnedBufferPool + 零拷贝 + EnableKernelCache | ✅ 完成 |
| P5 | BenchmarkDotNet 性能测试套件（Core / OnRead / E2E） | ✅ 完成 |
| P6 | PinnedBufferPool 两级缓存（ThreadLocal + ConcurrentQueue + cap） | ✅ 完成 |
| P7 | WinFspRamAdapter 移到独立 RamDrive.Adapter 项目 | 待做 |
| P8 | 随机 I/O / 多线程 benchmark | 待做 |
