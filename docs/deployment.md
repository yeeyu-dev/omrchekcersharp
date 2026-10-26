# 部署说明

本文说明如何将 OMRChecker 部署到客户机器。程序目标框架为 .NET Framework 4.8（`net48`），仅支持 Windows x64。

## 一、客户机环境要求

部署前请逐项确认以下三点：

### 1. 操作系统：Windows 10 1607 及以后，或 Windows 11（x64）

Windows 10 1507 / 1511 无法安装 .NET Framework 4.8，不受支持。

查看系统版本：`Win + R` 输入 `winver`。

### 2. .NET Framework 4.8 或更高版本

| 系统版本 | 自带版本 |
|---|---|
| Win10 1607 ~ 1809 | 4.6.2 ~ 4.7.2，**需另行安装 4.8** |
| Win10 1903 及以后 | 4.8（部分版本经 Windows 更新为 4.8.1） |
| Win11 | 4.8 / 4.8.1 |

在 PowerShell 中检查：

```powershell
(Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full").Release
```

结果 **≥ 528040** 即满足要求（≥ 533320 为 4.8.1）。不满足时安装 [.NET Framework 4.8 离线安装包](https://dotnet.microsoft.com/download/dotnet-framework/net48)（需管理员权限，安装后可能需要重启）。

### 3. Microsoft Visual C++ 2015-2022 x64 运行库

OpenCV 原生库 `OpenCvSharpExtern.dll` 依赖该运行库，**最容易漏装**。缺失时程序启动即报 `DllNotFoundException: OpenCvSharpExtern` 或 "找不到指定的模块"。

检查：在"设置 → 应用 → 已安装的应用"中查找 "Microsoft Visual C++ 2015-2022 Redistributable (x64)"。

未安装时下载安装：<https://aka.ms/vs/17/release/vc_redist.x64.exe>

## 二、发布与拷贝

在开发机仓库根目录执行：

```bash
dotnet build src/OMRChecker/OMRChecker.csproj -c Release
```

将 `src/OMRChecker/bin/Release/net48/` **整个目录**拷贝到客户机，其中必须包含：

- `OMRChecker.exe`、`OMRChecker.exe.config`
- 托管依赖 `*.dll`（OpenCvSharp、System.Text.Json、JsonSchema.Net、Docnet.Core、PdfPig 等）
- 原生库 `OpenCvSharpExtern.dll`、`pdfium.dll`

不要只拷贝 exe，也不要把原生库挪到子目录。

## 三、验证

在客户机上，进入程序目录执行：

```powershell
.\OMRChecker.exe -h
```

能正常输出帮助信息即说明三项环境要求均已满足。再用一份样例实际跑通：

```powershell
.\OMRChecker.exe -i <样例目录> -o <输出目录>
```

无图形界面的服务器上运行时，先设置 `$env:OMR_HEADLESS = "1"`，避免弹出调试窗口。

## 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| 双击无反应或提示需要安装 .NET Framework | 未安装 4.8，见第 2 项 |
| `DllNotFoundException: OpenCvSharpExtern` | 未装 VC++ x64 运行库（第 3 项），或 `OpenCvSharpExtern.dll` 未拷贝 |
| `BadImageFormatException` | 在 32 位系统上运行；程序仅支持 x64 |
| 处理 PDF 时报找不到 `pdfium` | `pdfium.dll` 未与 exe 放在同一目录 |
