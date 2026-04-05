# WinFsp.Net — 现代 .NET 10 WinFSP 绑定库：设计、计划与进展

## 1. 项目背景与动机

### 1.1 为什么迁移到 WinFSP

RamDrive 当前使用 Dokany (`DokanNet` 2.3.0.3) 作为用户态文件系统驱动。WinFSP 在架构层面有根本性优势：

| 维度 | Dokany | WinFSP |
|------|--------|--------|
| **内核通信模型** | "notification thread"，每次 I/O 多次上下文切换 | FSP_FSCTL_TRANSACT 事务模型，**最差 2 次上下文切换** |
| **Read/Write 数据路径** | 用户态需额外 buffer copy | **零拷贝**：内核直接将调用进程 buffer 映射到 FS 地址空间 |
| **批量处理** | 逐请求处理 | 单次上下文切换可服务**多个排队请求** |
| **缓存集成** | 用户态自行处理 | 内核 FSD 自带 cache manager + fast I/O 路径 |
| **稳定性** | NamedStreams 等兼容性问题 | "无已知内核崩溃或资源泄漏" |
| **社区实测** | RAM FS 比原生 NTFS 慢 | MEMFS **比 NTFS 还快**（fast I/O 更高效） |

### 1.2 为什么不用官方 winfsp.net NuGet

官方 `winfsp.net`（GPLv3）存在三个技术问题：

1. **AOT 不兼容**：
   - `Marshal.GetDelegateForFunctionPointer()` — 运行时反射生成 delegate
   - `Assembly.GetExecutingAssembly()` — trimmer/AOT 不友好
   - `GetEntryPoint<T>()` 用 `typeof(T).Name` 作为 entry point 名 — 反射模式

2. **性能损失**：
   - 每个回调经过 `delegate → Marshal → managed method` 三层间接调用
   - DLL 加载用 `LoadLibraryW` + `GetProcAddress` 手动绑定 40+ 函数指针
   - `Object` 类型的 FileNode/FileDesc 参数导致 boxing

3. **API 过时**：
   - target .NET Framework 3.5 / .NET Standard 2.0
   - `IntPtr Buffer + int length` 而非 `Span<byte>`
   - `UInt32`/`UInt64`/`Boolean` 而非 `uint`/`ulong`/`bool`
   - 无 async I/O 封装

### 1.3 重写目标

| 目标 | 手段 |
|------|------|
| **100% Native AOT** | `LibraryImport`(编译时 source-gen) + `UnmanagedCallersOnly` + function pointer |
| **零拷贝 I/O** | `Span<byte>` / `ReadOnlySpan<byte>` 直接包装内核映射 buffer |
| **零 GC 压力** | 无 delegate 分配、无 managed array 中转、无 boxing |
| **现代 API** | 精确类型、nullable、async 支持 |
| **通用库** | 覆盖全部 39 个 virtual 方法，可用于任何 WinFSP 文件系统实现 |

RamDrive 将采用 GPLv3 许可，与 WinFSP 完全兼容。

---

## 2. 架构设计

### 2.1 整体分层

```
┌──────────────────────────────────────────────────────────────────┐
│  RamDrive.Cli (Program.cs)                                       │
│    ├─ DI: PagePool, RamFileSystem, WinFspRamAdapter              │
│    └─ WinFspHostedService : BackgroundService                    │
└──────────────────────────┬───────────────────────────────────────┘
                           │ 继承
┌──────────────────────────▼───────────────────────────────────────┐
│  WinFsp.Net (通用绑定库)                                         │
│                                                                  │
│  ┌─────────────────────┐  ┌────────────────────────────────────┐ │
│  │  FileSystemBase     │  │  FileSystemHost                    │ │
│  │  (抽象基类)          │  │  (Mount/Unmount/Dispose)           │ │
│  │  39 virtual 方法     │◄─┤  45+ 配置属性 (VolumeParams proxy) │ │
│  │  + 3 async 方法      │  │  填充 Interface struct              │ │
│  │  + ExceptionHandler  │  │  管理 FSP_FILE_SYSTEM 生命周期      │ │
│  └──────────▲──────────┘  └───────────────┬────────────────────┘ │
│             │                              │                     │
│  ┌──────────┴──────────┐                   │                     │
│  │FileSystemCallbacks  │◄──────────────────┘                     │
│  │[UnmanagedCallersOnly]│   26 个静态回调                         │
│  │ partial static class │   + 异步分发 + security/reparse 辅助   │
│  └──────────┬──────────┘                                         │
│             │                                                    │
│  ┌──────────┴──────────┐  ┌───────────────────────────────────┐  │
│  │FileContextManager   │  │OperationContext                   │  │
│  │GCHandle 管理         │  │异步完成封装 (STATUS_PENDING 语义)  │  │
│  │{FileNode,FileDesc}  │  │CompleteRead/Write/ReadDir         │  │
│  └─────────────────────┘  └───────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │  Native Layer                                               │ │
│  │  FspApi.cs         — LibraryImport (25+ 函数) + DLL resolver│ │
│  │  FspStructs.cs     — 10 个 blittable struct                 │ │
│  │  FspFileSystemInterface.cs — 64 slot function pointer struct │ │
│  │  NtStatus.cs (31), CleanupFlags.cs (7), CreateOptions.cs (8)│ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
                           │ 动态链接
                ┌──────────▼──────────┐
                │  winfsp-x64.dll     │
                │  (WinFSP 原生驱动)   │
                │  GPLv3              │
                └─────────────────────┘
```

### 2.2 数据流：一次 ReadFile 调用的完整路径

```
应用程序: ReadFile(R:\data.bin, offset=100000, length=8192)
    │
    ▼ 内核空间
Windows NTOS → IRP_MJ_READ → WinFSP FSD
    │
    │ FSD 将应用进程 buffer 映射到用户态 FS 地址空间（零拷贝）
    │ 打包为 FSP_FSCTL_TRANSACT_REQ，放入 I/O 队列
    │
    ▼ 用户空间（Dispatcher 线程 ── WinFSP 管理）
FspFileSystemInterface.Read 函数指针被调用
    │
    ▼
FileSystemCallbacks.OnRead [UnmanagedCallersOnly, Cdecl]
    │  1. 从 *(nint*)(fileSystem + 8) 读取 UserContext → GCHandle → FileSystemBase
    │  2. 从 FullContext.UserContext2 → GCHandle → FileContextHolder{FileNode, FileDesc}
    │  3. TryGetAsyncContext() 获取 OperationContext
    │  4. 调用 fileSystemBase.ReadAsync(fileNode, fileDesc, offset, length, opCtx)
    │     ├─ 返回 true → STATUS_PENDING（异步接管，稍后 CompleteRead）
    │     └─ 返回 false → 继续同步路径
    │  5. 构造 Span<byte> 包装 native buffer：new Span<byte>((void*)buffer, length)
    │  6. 调用 fileSystemBase.Read(fileNode, fileDesc, span, offset, out bytesTransferred)
    │
    ▼
WinFspRamAdapter.Read (RamDrive 具体实现)
    │  node.Content.Read(offset, span)
    │     → PagedFileContent.Read
    │        → pageIndex = offset / 65536
    │        → memcpy: NativeMemory page → WinFSP mapped buffer
    │  零拷贝 + 零 GC 分配
    │
    ▼ 返回
STATUS_SUCCESS, bytesTransferred = 8192
    │
    ▼ 内核空间
FSD 完成 IRP → 数据已在应用进程原始 buffer 中
```

**整条路径的内存拷贝次数：1 次**（NativeMemory page → 应用进程 buffer）。
对比 Dokany：至少 2 次拷贝（page → managed buffer → Dokan buffer → 应用进程）。

### 2.3 FileContext 模式选择

WinFSP 提供三种 FileContext 模式：

| 模式 | 标志 | FileContext 含义 | 适用场景 |
|------|------|-----------------|---------|
| 默认 | 无 | 单 PVOID = file node | 简单 FS |
| UserContext2 | `UmFileContextIsUserContext2` | 单 PVOID = file descriptor | per-handle 状态 |
| **FullContext** | `UmFileContextIsFullContext` | 指向 `{UserContext, UserContext2}` | **通用库必选** |

我们选择 **FullContext 模式**（在 `FileSystemHost` 构造时自动设置 `UmFileContextIsFullContext = true`）：
- `UserContext` = 保留未用
- `UserContext2` = GCHandle → `FileContextHolder { FileNode, FileDesc }`
- `FileContextManager.Set/Get/Free` 管理 GCHandle 生命周期
- Create/Open 时分配，Close 时释放（在 `finally` 块中确保释放）

### 2.4 异步 I/O 设计

WinFSP 原生支持 `STATUS_PENDING` 异步操作（仅 Read/Write/ReadDirectory 三个回调）。

**同步路径**（默认，RamDrive 等纯内存 FS）：
```
OnRead → fs.ReadAsync() 返回 false（未覆盖）
       → new Span<byte>(buffer, length)
       → fs.Read(span, offset) → STATUS_SUCCESS
```

**异步路径**（网络 FS）：
```
OnRead → TryGetAsyncContext() 从 FspFileSystemGetOperationContext() 获取 Response 指针
       → 构造 OperationContext{FileSystem, Response, buffer, bufferLength}
       → fs.ReadAsync(fileNode, fileDesc, offset, length, opCtx) 返回 true
       → 回调返回 STATUS_PENDING
       → 任意线程: opCtx.CompleteRead(status, bytesTransferred)
         → 填充 Response.IoStatus* 字段
         → FspFileSystemSendResponse(FileSystem, Response)
```

**OperationContext 实际实现**：

```csharp
[SupportedOSPlatform("windows")]
public readonly unsafe struct OperationContext
{
    internal readonly nint FileSystem;
    internal readonly FspTransactRsp* Response;    // 异步完成写入此 Response
    private readonly nint _buffer;                 // 零拷贝 buffer 指针
    private readonly uint _bufferLength;           // buffer 长度（构造时捕获）

    public Span<byte> GetBuffer();                 // Read 输出 / Write 输入
    public ReadOnlySpan<byte> GetReadOnlyBuffer(); // Write 源数据只读视图
    public void CompleteRead(int status, uint bytesTransferred);
    public void CompleteWrite(int status, uint bytesTransferred, in FspFileInfo fileInfo);
    public void CompleteReadDirectory(int status, uint bytesTransferred);
}
```

### 2.5 IOCTL 支持

`Control` 回调（slot 25）允许用户态 FS 处理自定义 DeviceIoControl 请求：

```csharp
public virtual int Control(object? fileNode, object? fileDesc,
    uint controlCode, ReadOnlySpan<byte> input, Span<byte> output,
    out uint bytesTransferred);
```

需要在 `FileSystemHost` 设置 `DeviceControl = true` 以启用。
回调实现中将 `nint + uint length` 包装为 `Span<byte>` 传给虚方法。

---

## 3. 文件清单与实现状态

### 3.1 Native 层（Phase 1 ✅）

| 文件 | 行数 | 职责 |
|------|------|------|
| `WinFsp.Net.csproj` | 14 | net10.0, `IsAotCompatible`, `AllowUnsafeBlocks`, trim/single-file analyzers |
| `Native/NtStatus.cs` | 37 | 31 个 NTSTATUS 常量（Success, Pending, 错误码等） |
| `Native/CleanupFlags.cs` | 16 | `[Flags] enum`：Delete, SetAllocationSize, SetArchiveBit, 3 个时间戳 |
| `Native/CreateOptions.cs` | 18 | `[Flags] enum`：FileDirectoryFile, WriteThrough, DeleteOnClose 等 8 个标志 |
| `Native/FspStructs.cs` | 311 | 10 个核心 struct，全部 blittable（见 §5） |
| `Native/FspFileSystemInterface.cs` | 93 | 64 槽位 `delegate* unmanaged[Cdecl]` 结构体 |
| `Native/FspApi.cs` | 176 | LibraryImport (25+ 函数) + 注册表 DLL resolver + UserContext 访问 |
| **小计** | **665** | |

### 3.2 框架层（Phase 2 ✅）

| 文件 | 行数 | 职责 |
|------|------|------|
| `FileSystemBase.cs` | 405 | 抽象基类：36 同步 virtual + 3 async virtual + ExceptionHandler |
| `FileSystemHost.cs` | 436 | 45+ 配置属性 + Mount/MountEx/Unmount/Dispose 生命周期 |
| `FileSystemCallbacks.cs` | 746 | 26 个 `[UnmanagedCallersOnly]` 回调 + security/reparse/normalized-name 辅助 |
| `FileContextManager.cs` | 60 | GCHandle 管理：FileContextHolder{FileNode, FileDesc} ↔ FullContext |
| `OperationContext.cs` | 67 | 异步完成封装：3 个 Complete* 方法 + GetBuffer/GetReadOnlyBuffer |
| **小计** | **1714** | |

### 3.3 待实现（Phase 3-4 — RamDrive 集成）

| 文件 | 预估行数 | 职责 |
|------|---------|------|
| `WinFspRamAdapter.cs` | ~400 | FileSystemBase 的 RamDrive 实现（16 个回调覆盖） |
| `WinFspHostedService.cs` | ~80 | BackgroundService 生命周期 |
| `Program.cs` 修改 | ~10 | 添加 Backend 配置 + 条件 DI 注册 |
| **小计** | **~490** | |

### 3.4 总量

| 层 | 行数 | 状态 |
|----|------|------|
| Native 层 | 665 | ✅ 完成 |
| 框架层 | 1714 | ✅ 完成 |
| RamDrive 集成 | ~490 | ⏳ 待实现 |
| **总计** | **~2869** | |

**编译状态**：全 solution `dotnet build` — **0 warning, 0 error**。

---

## 4. 核心 API 设计

### 4.1 FileSystemBase — 39 个 virtual 方法

| 分类 | 方法 | 数量 |
|------|------|------|
| Lifecycle hooks | `Init`, `Mounted`, `Unmounted`, `DispatcherStopped` | 4 |
| Volume | `GetVolumeInfo`, `SetVolumeLabel` | 2 |
| Security | `GetSecurityByName`, `GetSecurity`, `SetSecurity` | 3 |
| Create/Open | `Create`, `Open`, `Overwrite` | 3 |
| Cleanup/Close | `Cleanup`, `Close` | 2 |
| Sync I/O | `Read`, `Write`, `Flush` | 3 |
| Async I/O | `ReadAsync`, `WriteAsync`, `ReadDirectoryAsync` | 3 |
| Metadata | `GetFileInfo`, `SetBasicInfo`, `SetFileSize` | 3 |
| Delete/Rename | `CanDelete`, `Rename` | 2 |
| Directory | `ReadDirectory` | 1 |
| Reparse | `GetReparsePoint`, `SetReparsePoint`, `DeleteReparsePoint` | 3 |
| Streams | `GetStreamInfo` | 1 |
| EA | `GetEa`, `SetEa` | 2 |
| Misc | `GetDirInfoByName`, `Control` (IOCTL) | 2 |
| Exception | `ExceptionHandler` | 1 |
| **合计** | | **39** |

所有方法默认返回 `NtStatus.InvalidDeviceRequest`（等同于 null 槽位行为）。
`Cleanup`/`Close` 默认空实现（无法报告错误）。

### 4.2 FileSystemHost — 配置与生命周期

**配置属性**（mount 前设置，proxy `FspVolumeParams` 字段）：

| 分类 | 属性 |
|------|------|
| 几何 | `SectorSize`, `SectorsPerAllocationUnit`, `MaxComponentLength` |
| 标识 | `VolumeCreationTime`, `VolumeSerialNumber` |
| 超时 | `FileInfoTimeout`, `VolumeInfoTimeout`, `DirInfoTimeout`, `SecurityTimeout`, `StreamInfoTimeout`, `EaTimeout` |
| 特性 | `CaseSensitiveSearch`, `CasePreservedNames`, `UnicodeOnDisk`, `PersistentAcls` |
| 功能 | `ReparsePoints`, `ReparsePointsAccessCheck`, `NamedStreams`, `ExtendedAttributes`, `DeviceControl` |
| 优化 | `PostCleanupWhenModifiedOnly`, `PassQueryDirectoryPattern`, `PassQueryDirectoryFileName`, `FlushAndPurgeOnCleanup`, `PostDispositionWhenNecessaryOnly` |
| 行为 | `ReadOnlyVolume`, `AllowOpenInKernelMode`, `WslFeatures`, `SupportsPosixUnlinkRename`, `RejectIrpPriorToTransact0` |
| 路径 | `Prefix` (UNC), `FileSystemName` |

**生命周期方法**：

```csharp
public int Mount(string? mountPoint,
    byte[]? securityDescriptor = null, bool synchronized = false, uint debugLog = 0);
public int MountEx(string? mountPoint, uint threadCount, ...);
public void Unmount();  // = Dispose()
public string? MountPoint { get; }
```

`Mount` 内部流程：
1. `FileSystemBase.Init(host)` — 允许 FS 配置 host 属性
2. `Marshal.AllocHGlobal` 分配 `FspFileSystemInterface`（512 字节），填充 26 个函数指针
3. `FspFileSystemCreate(devicePath, volumeParams, interfacePtr, out fsPtr)`
4. `GCHandle.Alloc(fileSystem)` → 写入 `fsPtr->UserContext`（offset 8）
5. `FspFileSystemSetOperationGuardStrategy(fsPtr, synchronized ? 1 : 0)`
6. `FspFileSystemSetDebugLog(fsPtr, debugLog)`
7. `FspFileSystemSetMountPointEx(fsPtr, mountPoint, sdPtr)`（SD 需 `Marshal.AllocHGlobal` 临时拷贝）
8. `FileSystemBase.Mounted(host)`
9. `FspFileSystemStartDispatcher(fsPtr, threadCount)`

### 4.3 FileSystemCallbacks — 26 个 UnmanagedCallersOnly 回调

每个回调的统一模式：

```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
internal static int OnXxx(nint fileSystem, FspFullContext* fullContext, ...)
{
    var fs = GetFs(fileSystem);       // *(nint*)(fs+8) → GCHandle → FileSystemBase
    try
    {
        GetContext(fullContext, out var fileNode, out var fileDesc);  // GCHandle → holder
        int result = fs.Xxx(fileNode, fileDesc, ...);
        if (result >= 0) *pOutput = output;
        return result;
    }
    catch (Exception ex) { return HandleException(fs, ex); }
}
```

**已实现的 26 个回调**：

| 槽 | 回调 | 返回类型 | 备注 |
|----|------|---------|------|
| 0 | OnGetVolumeInfo | int | |
| 1 | OnSetVolumeLabel | int | |
| 2 | OnGetSecurityByName | int | 含 CopySecurityDescriptor 辅助 |
| 3 | OnCreate | int | 含 MakeSecurityDescriptor + SetNormalizedName |
| 4 | OnOpen | int | 含 SetNormalizedName |
| 5 | OnOverwrite | int | |
| 6 | OnCleanup | void | 不可报错 |
| 7 | OnClose | void | `finally` 中调用 FileContextManager.Free |
| 8 | OnRead | int | async fallback: TryGetAsyncContext → ReadAsync |
| 9 | OnWrite | int | async fallback: TryGetAsyncContext → WriteAsync |
| 10 | OnFlush | int | |
| 11 | OnGetFileInfo | int | |
| 12 | OnSetBasicInfo | int | |
| 13 | OnSetFileSize | int | |
| 14 | OnCanDelete | int | |
| 15 | OnRename | int | |
| 16 | OnGetSecurity | int | 含 CopySecurityDescriptor |
| 17 | OnSetSecurity | int | 含 MakeSecurityDescriptor |
| 18 | OnReadDirectory | int | async fallback |
| 20 | OnGetReparsePoint | int | 含 CopyReparseData |
| 21 | OnSetReparsePoint | int | Marshal.Copy 到 byte[] |
| 22 | OnDeleteReparsePoint | int | Marshal.Copy 到 byte[] |
| 23 | OnGetStreamInfo | int | |
| 24 | OnGetDirInfoByName | int | |
| 25 | OnControl | int | Span 包装 input/output buffer |
| 29 | OnGetEa | int | |
| 30 | OnSetEa | int | |
| 32 | OnDispatcherStopped | void | 静默吞异常 |

**未填充的槽位**（null → WinFSP 自动返回 STATUS_INVALID_DEVICE_REQUEST）：
- Slot 19: ResolveReparsePoints
- Slot 26: SetDelete
- Slot 27: CreateEx（使用 slot 3 Create 替代）
- Slot 28: OverwriteEx（使用 slot 5 Overwrite 替代）
- Slot 31: Obsolete0
- Slots 33-63: Reserved

**内部辅助方法**：
- `TryGetAsyncContext()` — 从 `FspFileSystemGetOperationContext()` 获取 Response 指针，构建 OperationContext
- `CopySecurityDescriptor()` / `MakeSecurityDescriptor()` — SD byte[] ↔ native buffer 互转
- `CopyReparseData()` — reparse data byte[] → native buffer
- `SetNormalizedName()` — 写入 OpenFileInfo 内嵌的 NormalizedName 指针（offset 计算基于 FspFileInfo 之后的 nint + ushort）
- `GetSecurityDescriptorLength()` — `advapi32.dll` LibraryImport

### 4.4 RamDrive 需要覆盖的回调（16/39）

| 回调 | RamDrive 用途 | 对应 DokanRamAdapter 逻辑 |
|------|--------------|--------------------------|
| GetVolumeInfo | 返回容量信息 + 卷标 | GetDiskFreeSpace + GetVolumeInformation |
| GetSecurityByName | 路径查找 + 返回 FileAttributes | **新增**（Dokan 无此概念） |
| Create | 创建文件/目录 | CreateFile (CreateNew 分支) |
| Open | 打开已有文件/目录 | CreateFile (Open 分支) |
| Overwrite | 截断重写文件 | CreateFile (Truncate 分支) |
| Cleanup | 删除 + 更新时间戳 | Cleanup（CleanupFlags 位图替换 DeletePending） |
| Close | 释放 GCHandle | CloseFile |
| Read | 零拷贝读 | ReadFile（Span 直通 NativeMemory page） |
| Write | 零拷贝写 | WriteFile（ReadOnlySpan 直通） |
| Flush | No-op | FlushFileBuffers |
| GetFileInfo | 返回文件元数据 | GetFileInformation |
| SetBasicInfo | 设置属性和时间戳 | SetFileAttributes + SetFileTime |
| SetFileSize | 设置文件大小/分配大小 | SetEndOfFile / SetAllocationSize |
| CanDelete | 检查是否可删除 | DeleteFile / DeleteDirectory 验证 |
| Rename | 移动/重命名 | MoveFile |
| ReadDirectory | 枚举目录内容 | FindFiles（用 FspApi.FspFileSystemAddDirInfo 打包） |

### 4.5 Dokan → WinFSP 回调映射的关键差异

| 差异点 | Dokan | WinFSP |
|--------|-------|--------|
| 创建/打开 | 单个 `CreateFile` 方法，通过 `FileMode` 区分 | **拆分为** `Create` + `Open` + `Overwrite` 三个方法 |
| 删除 | `DeletePending` flag 在 Cleanup 中检查 | `Cleanup(flags)` 的 `CleanupFlags.Delete` bit |
| 时间戳更新 | 手动在每个操作中设置 | `Cleanup(flags)` 带 `SetLastWriteTime` 等 bit，框架层面处理 |
| 安全描述符 | `GetFileSecurity` 返回空 SD | `GetSecurityByName` **必须实现**（access check 依赖它） |
| 目录枚举 | 返回 `IEnumerable<FindFileInformation>` | 向 native buffer 逐条写入 `FspDirInfo`，调用 helper |
| 缓冲区 | `NativeMemory<byte>` / `ReadOnlyNativeMemory<byte>` | 直接 `nint buffer` → 包装为 `Span<byte>` |
| Append write | `offset == -1` 表示追加 | `WriteToEndOfFile == true` + `offset` 无意义 |
| ConstrainedIo | PagingIo 隐含 clamp | 显式 `ConstrainedIo` 参数 |
| Normalized name | 无 | Create/Open 可返回规范化路径名，框架写入 OpenFileInfo 内嵌字段 |

---

## 5. Native 层结构体详解

### 5.1 FspVolumeParams（504 字节，`LayoutKind.Explicit`）

```
Offset   Size   Field
──────   ────   ─────
  0      2      Version (= 504 for V1)
  2      2      SectorSize
  4      2      SectorsPerAllocationUnit
  6      2      MaxComponentLength
  8      8      VolumeCreationTime
 16      4      VolumeSerialNumber
 20      4      TransactTimeout (DEPRECATED)
 24      4      IrpTimeout
 28      4      IrpCapacity
 32      4      FileInfoTimeout
 36      4      Flags (32 bitfields packed into uint, 通过 bool 属性访问)
 40    384      Prefix[192] (WCHAR, fixed char)
424     32      FileSystemName[16] (WCHAR, fixed char)
───── V1 extension ─────
456      4      AdditionalFlags (5 bitfields packed into uint)
460      4      VolumeInfoTimeout
464      4      DirInfoTimeout
468      4      SecurityTimeout
472      4      StreamInfoTimeout
476      4      EaTimeout
480      4      FsextControlCode
484      4      Reserved32[1]
488     16      Reserved64[2]
──────────
504 bytes total ✓ (validated by C header static assert)
```

辅助方法：`SetPrefix()`, `SetFileSystemName()`, `IsPrefixEmpty()`, 31 个 bool flag 属性。

### 5.2 其他结构体

| struct | 大小 | Layout | 说明 |
|--------|------|--------|------|
| `FspFileInfo` | 72 B | Sequential | 文件元数据（属性、大小、时间戳、IndexNumber、EaSize） |
| `FspVolumeInfo` | 88 B | Sequential | 卷信息（TotalSize、FreeSize、VolumeLabel[32]） |
| `FspFullContext` | 16 B | Sequential | {UserContext, UserContext2} ull pair |
| `FspDirInfo` | 变长 | Sequential | ushort Size + FileInfo + 24B padding + FileNameBuf[255] |
| `FspStreamInfo` | 变长 | Sequential | ushort Size + StreamSize + AllocationSize + StreamNameBuf[255] |
| `FspOperationContext` | 16 B | Sequential | {Request*, Response*} 指向当前操作的事务缓冲区 |
| `FspTransactReq` | 88 B | Explicit | 事务请求头（Version, Size, Kind, Hint） |
| `FspTransactRsp` | 128 B | Explicit | 事务响应头（+IoStatus+FileInfo，用于异步完成） |
| `FspFileSystemInterface` | 512 B | Sequential | 64 个 function pointer 槽（x64） |

`FspDirInfo`/`FspStreamInfo` 提供 `SetFileName()`/`SetStreamName()` 辅助方法。
`FspTransactKind` 静态类：`Read=5, Write=6, QueryDirectory=14`。

### 5.3 FSP_FILE_SYSTEM UserContext 偏移

```c
typedef struct _FSP_FILE_SYSTEM {
    UINT16 Version;          // offset 0, 2 bytes
    // 6 bytes padding (x64 PVOID alignment)
    PVOID UserContext;       // offset 8 on x64
    ...
} FSP_FILE_SYSTEM;           // total 792 bytes (x64, static-asserted in winfsp.h:1119)
```

访问方式（`FspApi.cs`）：
```csharp
internal static unsafe nint GetUserContext(nint fileSystem)
    => *(nint*)((byte*)fileSystem + nint.Size);  // nint.Size = 8 on x64

internal static unsafe void SetUserContext(nint fileSystem, nint value)
    => *(nint*)((byte*)fileSystem + nint.Size) = value;
```

---

## 6. AOT 兼容性保证

| 层 | AOT 策略 |
|----|---------|
| P/Invoke | `[LibraryImport]` — compile-time source-generated marshaler（FspApi + advapi32） |
| 回调 | `[UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]` + `&Method` 编译时地址 |
| Interface 传递 | `Marshal.AllocHGlobal` + 手动填充指针 → `nint` 传给 `FspFileSystemCreate` |
| 结构体 | 全部 blittable（无引用类型字段），`fixed char[]` / `fixed ulong[]` |
| DLL 加载 | `NativeLibrary.SetDllImportResolver` — .NET runtime built-in |
| 字符串 | `StringMarshalling.Utf16`（声明在 LibraryImport 上） |
| GCHandle | `GCHandle.Alloc/Free` — AOT safe |
| 注册表 | `Microsoft.Win32.Registry.GetValue` — .NET built-in, AOT safe |
| 反射 | **完全不使用** |
| 项目配置 | `<IsAotCompatible>true</IsAotCompatible>` + `EnableTrimAnalyzer` + `EnableSingleFileAnalyzer` |
| 验证 | 全 solution `dotnet build` — **0 warning, 0 error** |

---

## 7. 实施路线与进度

| 阶段 | 状态 | 内容 | 验证方式 |
|------|------|------|---------|
| Phase 1: Native 层 | ✅ 完成 | 7 个文件，665 行 | `dotnet build` 0 warning |
| Phase 2: 框架层 | ✅ 完成 | 5 个文件，1714 行 | `dotnet build` 0 warning |
| Phase 3: RamDrive 最小挂载 | ⏳ 下一步 | WinFspRamAdapter + HostedService + Backend 配置 | R:\ 盘符可见 + `echo/type/del` |
| Phase 4: 目录操作 | 📋 计划 | ReadDirectory + CanDelete + Rename 完整实现 | `mkdir/dir/rmdir` |
| Phase 5: AOT 验证 | 📋 计划 | `dotnet publish -r win-x64 -c Release` | AOT 发布运行 + 性能对比 |

---

## 8. 与现有代码的集成策略

### 8.1 共存方案

```csharp
// appsettings.jsonc
{
  "RamDrive": {
    "Backend": "WinFsp",  // 或 "Dokan"
    ...
  }
}

// Program.cs
if (options.Backend == "WinFsp")
{
    services.AddSingleton<WinFspRamAdapter>();
    services.AddHostedService<WinFspHostedService>();
}
else
{
    services.AddSingleton<DokanRamAdapter>();
    services.AddHostedService<DokanHostedService>();
}
```

### 8.2 不变的部分

以下核心组件 **完全不修改**：
- `PagePool` — NativeMemory 页面池
- `PagedFileContent` — 分页文件内容（三阶段写锁）
- `RamFileSystem` — 路径解析、目录树、结构锁
- `FileNode` — 文件/目录节点模型
- `RamDriveOptions` — 配置模型（仅添加 `Backend` 字段）

### 8.3 项目引用关系

```
RamDrive.Cli ──→ RamDrive.Core ──→ WinFsp.Net (新增)
                                └─→ DokanNet (保留，共存期)
```

---

## 9. 关键参考源

| 资源 | 位置 | 用途 |
|------|------|------|
| WinFSP C API 头文件 | `F:\MyProjects\winfsp\inc\winfsp\winfsp.h` | FSP_FILE_SYSTEM_INTERFACE (line 193-1093), FSP_FILE_SYSTEM (1096-1117) |
| WinFSP fsctl 结构体 | `F:\MyProjects\winfsp\inc\winfsp\fsctl.h` | VolumeParams (192-266), FileInfo (277-290), DirInfo (299-310) |
| WinFSP memfs-dotnet | `F:\MyProjects\winfsp\tst\memfs-dotnet\Program.cs` | 官方 .NET 参考实现（行为参考，非代码参考） |
| DokanRamAdapter | `src/RamDrive.Core/FileSystem/DokanRamAdapter.cs` | 438 行，所有回调逻辑的移植源 |
| DokanHostedService | `src/RamDrive.Core/FileSystem/DokanHostedService.cs` | 108 行，生命周期模式参考 |
| RamFileSystem | `src/RamDrive.Core/FileSystem/RamFileSystem.cs` | 核心 FS API 表面 |
| FileNode | `src/RamDrive.Core/FileSystem/FileNode.cs` | 节点模型 |

---

## 10. 法律说明

RamDrive 项目将采用 GPLv3 许可，与 WinFSP 的 GPLv3 完全兼容，无许可冲突。

重写 WinFSP .NET 绑定的动机**不是规避 GPL**，而是：
1. **现代化 .NET API**：Span/ReadOnlySpan、function pointer、LibraryImport
2. **Native AOT 兼容**：官方 `winfsp.net` 使用 `Marshal.GetDelegateForFunctionPointer`、`Assembly.GetExecutingAssembly()` 反射加载等 AOT 不兼容模式
3. **性能优化**：消除 delegate 间接调用开销、消除 managed 堆分配、零拷贝 Span I/O
4. **简化依赖**：不依赖 .NET Framework 3.5 / .NET Standard 2.0 时代的包装层
