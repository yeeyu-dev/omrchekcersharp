# OMRChecker（C# / .NET 8）

本项目是 [OMRChecker](https://github.com/Udayraj123/OMRChecker)（原作者 Udayraj Deshmukh，Python 实现）的 C# 移植版，用于识别答题卡（OMR）并自动评分。
输入文件格式（`template.json` / `config.json` / `evaluation.json`）与原项目完全兼容；模板编写方法、样例说明等可参考原项目文档 [docs/upstream-README.md](docs/upstream-README.md)（其中的 Python 安装 / 运行命令已不适用）。

## 环境要求

- .NET SDK 8.0+
- Windows x64（OpenCV 原生库来自 `OpenCvSharp4.Windows`；Linux 需改为引用对应的 OpenCvSharp runtime 包）

> 本机全局 NuGet 源（`10.100.5.132`）当前返回 502，因此根目录的 `nuget.config` 只针对本解决方案改用 nuget.org。若内网源恢复，可删除该文件。

## 常用命令

```bash
# 构建
dotnet build OMRChecker.sln

# 运行（在仓库根目录执行）
dotnet run --project src/OMRChecker -- -i samples/sample1 -o outputs/sample1
dotnet run --project src/OMRChecker -- -i samples/sample1 -l     # 布局调试模式
dotnet run --project src/OMRChecker -- -h                       # 查看帮助

# 测试（含与原 Python 版快照逐行比对的 15 个样例回归测试）
dotnet test OMRChecker.sln
```

命令行参数：`-i/--inputDir`（可多个，默认 `inputs`）、`-o/--outputDir`（默认 `outputs`）、`-l/--setLayout`、`-a/--autoAlign`、`-d/--debug`（打印完整异常堆栈）。
设置环境变量 `OMR_HEADLESS=1` 可禁止弹出调试窗口（服务器 / CI 环境）。

## 目录结构

```
.
├── OMRChecker.sln
├── nuget.config
├── src/OMRChecker/
│   ├── Program.cs              # 命令行解析
│   ├── Entry.cs                # 递归遍历目录、写结果 CSV
│   ├── Config/                 # 强类型配置与默认值
│   ├── Constants/              # 题型、颜色、图像处理常量
│   ├── Core/                   # 模板、字段块、识别核心（对齐、阈值、标注）
│   ├── Evaluation/             # 评分与解释表
│   ├── Processors/             # 6 个预处理器 + 注册表
│   ├── Schemas/                # JSON Schema（嵌入资源）
│   ├── Logging/                # 控制台日志与表格
│   └── Utils/                  # 图像 / PDF / CSV / JSON / 格式化工具
├── tests/OMRChecker.Tests/
│   ├── TestData/               # 原 Python 版的 pytest 快照与测试图片
│   └── *.cs                    # xUnit 测试
├── samples/                    # 样例答题卡、模板与评分配置
└── docs/                       # 原项目文档
```

## 技术选型

| 用途 | 库 |
| --- | --- |
| 图像处理 | OpenCvSharp4 4.10.0.20241108 |
| JSON Schema 校验（draft 2020-12） | JsonSchema.Net 7.3.4 |
| PDF 渲染 | Docnet.Core 2.6.0（PDFium） |
| PDF 原生 DPI 推断 | PdfPig 0.1.16 |
| 控制台表格 / 日志 / CSV | 项目自带 `ConsoleTable` / `Logger` / `CsvUtils` |

## 与原 Python 版的差异

识别结果与原版一致（`samples/` 下 15 个快照样例的 CSV 逐行相同）。以下为有意的差异：

1. **修正的缺陷**
   - 标注图中“未涂”气泡的灰色方框，原版误用了上一个已涂气泡的坐标，现画在正确位置。
   - `save_image_level` 的中间图像堆叠，原版路径缺少分隔符（写到 `CheckedOMRsstack/`），现写到 `CheckedOMRs/stack/`。
   - 仅开启 `enable_evaluation_table_to_csv`（未开启 `should_explain_scoring`）时原版会崩溃，现可正常导出。
   - `-a/--autoAlign` 在原版中被解析但未生效，现会覆盖 `alignment_params.auto_align`。
   - 关闭调试窗口后原版会一直空转，现检测到窗口关闭即继续。
2. **未移植**：`show_image_level >= 6` 时的 matplotlib 统计图（给出一次警告后跳过），不影响识别结果。
3. **行为调整**：标记图 / JSON 文件读取失败时抛出异常并以退出码 1 结束（原版直接 `exit(31)` / `exit(1)`）；图片读写使用字节流，支持中文路径。
4. **保留的原有逻辑**：`CropOnMarkers` 中 `apply_erode_subtract` 的条件判断与选项名含义相反，为保证识别结果一致而保留（代码中已注释说明）。

## 扩展预处理器

1. 在 `src/OMRChecker/Processors/` 新建类继承 `ImagePreprocessor`，实现 `ApplyFilter`。
2. 在 `ProcessorManager.Factories` 中登记。
3. 在 `src/OMRChecker/Schemas/template.schema.json` 的 `preProcessors.items.properties.name.enum` 中加入名称，并按需添加 options 校验。

## 许可证

沿用原项目许可证，见 [LICENSE](LICENSE)。
