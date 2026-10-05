# Manual Sim

**为罗技 G29 打造的"玻璃盒"手动挡训练模拟器。** 离合打滑、传递扭矩、ECU 怠速补偿、离熄火还有多远，全部有模型、实时显示、事后回放，所以熄火时你能看到*为什么*。

[![CI](https://github.com/langrenxdh/manual-sim/actions/workflows/ci.yml/badge.svg)](https://github.com/langrenxdh/manual-sim/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md)

![带教学浮层的坡起：离合曲线、熄火余量、怠速控制和坡起辅助](docs/media/teaching.png)

## 为什么做

现有的驾驶游戏把离合当成黑盒：熄火了，却不知道发生了什么。City Car Driving 太宽容，每辆车手感都一样；BeamNG 机械上精确但踏板手感弱；赛车模拟不关心起步和熄火。

Manual Sim 围绕一辆车展开，2019 款大众 Golf 110TSI（1.4 L 涡轮，6 档手动），练的是手动挡里最难学的一件事：离合和油门的配合。熄火、抖动、憋车、坡起溜车这些行为都不是脚本，而是从物理里自然出现的。

## 功能

- **1 kHz 物理核心。** 带涡轮迟滞的发动机、ECU 怠速补偿、有半联动区和热量的离合、传动系惯量、坡道、坡起辅助、可选的空调负载。
- **真实踏板和档杆。** 三个独立的模拟踏板、H 档杆（含倒挡）、方向盘上的手刹和启动键。打齿和超转都有后果。
- **力反馈和声音。** 方向盘上的发动机抖动和打齿；发动机、涡轮啸叫、启动机和打齿声由同一份状态实时合成。
- **教学模式。** 实时离合曲线、熄火余量、怠速控制占用、坡起辅助状态和换档建议，另有教练提示。
- **分级练习和考试。** 平路起步、坡起（有/无辅助）、平顺升档、堵车跟车、停在线上起步、下坡、倒车上坡等。每次尝试有分数、等级和扣分最多的那个错误。
- **幽灵、回放和进步曲线。** 每次尝试以 1 kHz 记录。可回放、叠加你的最好成绩，并跟踪多次练习的趋势。
- **镇上驾驶。** 在小镇里转向，带单轨轮胎模型、泊车雷达和俯视图。
- **七款车。** Golf 110TSI、小排量自吸、2.0 柴油、Mustang GT、Civic Type R、GR86 和 MX-5，各有自己的发动机、变速箱、轮胎和声音。
- **驾驶员档案和排行榜**，中英文界面。
- **实时调参面板。** 所有参数都在 `config/*.json` 里，开车时就能改。

## 截图

| 练习菜单 | 小镇与俯视图 |
| --- | --- |
| ![带成绩的练习菜单](docs/media/menu.png) | ![带后视镜和俯视图的镇上驾驶](docs/media/town.png) |

![带遥测读数的实时调参面板](docs/media/tuning.png)

## 环境要求

- Windows 10 或 11（64 位）
- 带 H 档杆的罗技 G29（PS3 模式，已装 Logitech G HUB，转角 900°，关闭自带回中弹簧）
- 喇叭或耳机；引擎声是离合控制的一部分

暂不支持其他方向盘。物理和训练库与平台无关，可以在 Linux 上构建和测试。

## 运行

**用发布包。** 从[最新发布](https://github.com/langrenxdh/manual-sim/releases/latest)下载 `ManualSim.zip` 并解压，运行 `ManualSim.exe`，按首次设置操作（屏幕尺寸、观看距离、喇叭检查）。见 [docs/install.md](docs/install.md)。

**从源码。** 需要 .NET 10 SDK。

```powershell
dotnet run --project src/Sim.App -c Release
```

用 `pwsh scripts/publish.ps1` 构建自包含发布包。

### 按键

| 键 | 作用 | 键 | 作用 |
| --- | --- | --- | --- |
| E | 练习、考试、进步 | T | 教学模式 |
| R / H | 重新开始 / 坡起 | P | 回放 |
| M | 坡道 / 小镇地图 | V | 选车 |
| F | 后视镜 | B | 俯视图 |
| Tab | 调参面板 | L | 语言 |
| F11 | 全屏 | Esc | 返回 / 退出 |

用方向盘上的 X 键启动发动机。倒挡是档杆的第 7 个位置。

## 工作原理

```
 G29 ──► Sim.Input ──┐
                     ▼
        Sim.Core（1 kHz 物理，零依赖）
                     │  无锁双缓冲
      ┌──────────────┼──────────────┬──────────────┐
      ▼              ▼              ▼              ▼
    渲染           音频           力反馈          遥测
  (raylib)       (回调)        (约 100 Hz)       (记录)
```

| 项目 | 作用 |
| --- | --- |
| `Sim.Core` | 物理核心：输入结构进，状态结构出，固定 1 ms 步长。不依赖 SDL、raylib、IO、线程或时钟。 |
| `Sim.Training` | 练习、评分、教练提示、小镇地图。纯逻辑，规则与核心相同。 |
| `Sim.Input` | SDL3 手柄读取和 haptic 力反馈。 |
| `Sim.App` | raylib 宿主：渲染、音频合成、调参面板、遥测。 |
| `tools/HwProbe` | 检查 G29 轴、按钮和力反馈的硬件探测工具。 |

基本规则（物理从模型中涌现、不用魔法数字、不削弱测试、物理永不等待渲染）见 [docs/engineering-rules.md](docs/engineering-rules.md)。

## 测试

```powershell
dotnet test
```

20 个验收测试（T1–T20）固定了车的行为：起步、熄火、坡起、超转、抓地。13 个评分测试（S1–S13）固定了尝试如何评分。它们用脚本化的踏板输入运行，不需要硬件。

## 文档

| 文档 | 内容 |
| --- | --- |
| [设计](docs/design.md) | 完整规格、参数、每个里程碑的决策记录 |
| [路线图](docs/roadmap.md) | v1 之后的里程碑及其门槛 |
| [安装](docs/install.md) | 发布包安装、G HUB 设置、按键 |
| [工程规则](docs/engineering-rules.md) | 技术栈、结构、硬规则、约定 |
| [HwProbe](tools/HwProbe/README.md) | 硬件探测工具用法 |

每份文档都有英文（`.en.md`）和中文（`.md`）两个版本。

## 声明

Manual Sim 是独立的业余项目，与大众、福特、本田、丰田、马自达或罗技没有关联，也未获其认可。车型和产品名称归其所有者所有，仅用于说明所模拟的对象。车辆参数是为了手感而取的近似值，不是厂商数据。

## 许可证

[MIT](LICENSE)
