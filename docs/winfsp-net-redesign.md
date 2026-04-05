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
src/WinFsp.Net/
├── Native/
│   ├── NtStatus.cs              NTSTATUS 常量
│   ├── CleanupFlags.cs          Cleanup 标志枚举
│   ├── CreateOptions.cs         NtCreateFile 选项
│   ├── FspStructs.cs            blittable 结构体（VolumeParams, FileInfo, DirInfo, FullContext, ...）
│   ├── FspFileSystemInterface.cs  64 slot function pointer struct
│   └── FspApi.cs                LibraryImport P/Invoke + DLL resolver
├── WinFspFileSystem.cs          Low-level API: thin shell around native FSP_FILE_SYSTEM
├── IFileSystem.cs               High-level interface (Dokan 风格, ValueTask)
├── FileSystemHost.cs            High-level host (合并了回调分发 + bridge + 异步管理)
├── FileOperationInfo.cs         per-handle 状态 + CancellationToken
├── IoResults.cs                 ReadResult / WriteResult / FsResult / CreateResult
└── UnmanagedBufferPool.cs       NativeMemory 对齐池（零 GC）

examples/HelloFs/
├── HelloFs.csproj               net10.0-windows, AllowUnsafeBlocks, PublishAot=true
└── Program.cs                   ~290 行，完整可运行的只读 FS
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

---

## 8. Next Steps

| 优先级 | 任务 | 状态 |
|--------|------|------|
| P0 | Low-Level HelloFs example（AOT 验证） | ✅ 完成 |
| P1 | FileSystemHost 回调全部改为 delegate 方式 | 待做 |
| P2 | RamDrive WinFspRamAdapter (实现 IFileSystem) | 待做 |
| P3 | High-Level HelloFs example（用 IFileSystem） | 待做 |
| P4 | 性能对比：WinFSP vs Dokany | 待做 |
