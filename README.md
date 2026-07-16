# Tuna

<p align="center">
  <img src="assets/Tuna.png" width="128" alt="Tuna 小黄鸡">
</p>

<p align="center">
  Windows 资源管理器历史记录、快速访问与桌面宠物工具
</p>

Tuna 使用 .NET 8 与 Avalonia UI 开发，可以记录最近访问的文件和文件夹，并通过轻量、无边框的浮动面板快速搜索和打开历史路径。软件同时内置可交互桌面宠物，并兼容 Codex 桌宠格式。

## 功能特性

- **资源管理器历史记录**：自动记录最近访问的文件与文件夹，支持搜索、筛选、固定和快速打开。
- **快速呼出面板**：面板可跟随鼠标位置显示，支持失焦自动隐藏、始终置顶和单实例唤醒。
- **自由调整尺寸**：可从窗口四边及四角缩放，窗口尺寸会保存至数据文件，下次启动自动恢复。
- **桌面宠物**：支持拖动、方向识别、悬停动画、随机动作以及右键菜单交互。
- **Codex 桌宠兼容**：支持导入兼容的 `.codex-pet` 宠物包，并展示宠物名称和完整介绍。
- **轻量运行**：采用 NativeAOT 单文件发布，宠物动画按需运行，尽量降低后台资源占用。
- **原生 Windows 交互**：结合 Win32 API 处理窗口激活、置顶、缩放、快捷唤醒等行为。

## 内置宠物

当前提供 4 个内置宠物：

| 宠物 | 说明 |
| --- | --- |
| tuna小黄鸡 | Tuna 默认宠物，为软件设计的原创小黄鸡，不可删除 |
| Doro | 内置宠物，可删除 |
| GUGUGAGA | 内置宠物，可删除 |
| ikkun | 内置宠物，可删除 |

宠物右键菜单提供“隐藏宠物”“设置”和“退出”。宠物备注支持悬停查看完整介绍。

## Codex 宠物资源

- [Codex Pets：创建 Codex 宠物指南](https://codex-pet.org/zh/how-to-create-a-codex-pet/)
- [Codex Pet：宠物资源库](https://codexpet.xyz/pets/)

## 数据保存

用户设置、窗口尺寸和历史记录默认保存在：

```text
%APPDATA%\Tuna\history_config.json
```

内置宠物资源位于：

```text
ExplorerHistoryTracker/Assets/Pets/
```

## 开发环境

- Windows 10 或更高版本
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Avalonia UI

## 编译

在仓库根目录执行：

```powershell
dotnet build .\ExplorerHistoryTracker\ExplorerHistoryTracker.csproj -c Release
```

也可以运行根目录下的 `编译.bat`。NativeAOT 发布脚本位于：

```text
ExplorerHistoryTracker/发布.ps1
```

## 项目结构

```text
ExplorerHistoryTracker/      应用程序源码
  Assets/Pets/               内置桌面宠物
  Models/                    数据模型
  Services/                  历史记录、图标和宠物服务
  ViewModels/                界面逻辑
assets/                      项目图标
软件发布/                    本地发布输出（不提交到仓库）
```

欢迎提交 Issue 或 Pull Request。
