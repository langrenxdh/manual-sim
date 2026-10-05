# 工程规则

[English](engineering-rules.en.md)

在这个代码库上工作的基本规则。完整规格见 [`design.md`](design.md)，v1 之后的计划见 [`roadmap.md`](roadmap.md)。

## 技术栈

- C# / .NET 10。应用（`Sim.App`、`Sim.Input`、`HwProbe`）面向 Windows；
  `Sim.Core`、`Sim.Training` 及其测试在 Linux 上也能构建并通过（`net10.0`，不含任何系统相关代码）。
- raylib-cs 负责渲染和音频流。
- SDL3（C# 绑定）只负责手柄输入和 haptic 力反馈，不用于窗口。
- xUnit v3 做测试（`global.json` 让 `dotnet test` 使用 Microsoft.Testing.Platform，.NET 10 需要）。

## 项目结构

```
src/Sim.Core          物理核心，零依赖
src/Sim.Input         SDL3 输入适配 + 力反馈
src/Sim.App           raylib 宿主：渲染、音频、调参面板、遥测
src/Sim.Training      练习与评分，纯逻辑，规则与 Sim.Core 相同
tests/Sim.Core.Tests  验收测试 + 单元测试
tests/Sim.Training.Tests  脚本化的练习与评分测试
tools/HwProbe         G29 硬件探测工具
config/               车辆参数文件（如 golf-110tsi.json）
```

## 硬规则

1. **`Sim.Core` 不依赖** SDL、raylib、文件 IO、线程或时钟。
   输入结构进，状态结构出，固定步长 dt = 1 ms。
2. **行为必须从物理中涌现。** 不为熄火、抖动、坡起、"憋车"写特例代码。
   行为不对就修模型，不要加 if。
3. **物理里没有魔法数字。** 所有可调量都在车辆参数记录里，
   从 `config/*.json` 加载，并显示在调参面板上。
4. **设计文档里的验收测试（T1–T20）和评分测试（S1–S13）必须一直通过。**
   不得为了通过而削弱、删除或放宽测试。
5. **硬件固定：** G29 方向盘 + 三踏板 + H 档杆，一台显示器，普通电脑喇叭。
   任何设计都不得需要额外硬件。
6. **物理永不等待渲染。** 1 kHz 的物理线程写入无锁双缓冲；
   渲染、音频回调、力反馈（约 100 Hz）和遥测各自读取最新状态。
7. **一次只做一个里程碑**（设计文档中的 M0 → M4，然后路线图中的 M5 → M13，按顺序）。
   每个里程碑在门槛处结束，手动验证后再开始下一个。

## 约定

- 代码、标识符和注释使用英文。
- 面向人的文档都是双语的：中文 `X.md` 加英文 `X.en.md`，互相链接。
  每次文档修改必须在同一个提交里同时改两个文件。
  唯一的例外是首页：`README.md` 是英文，`README.zh.md` 是中文。
- 小提交，每个提交只做一件事。
- 凡需要真实 G29 的事（硬件验证、手感调参）都在方向盘上手动验证。
