# Tuna (Explorer History Tracker)

Tuna 是一个用 Avalonia UI 编写的 Windows 资源管理器历史记录追踪与快速访问工具。

Tuna is a Windows Explorer history tracker and quick access tool built with Avalonia UI.

---

## 功能特性 (Features)

* 🔍 **历史记录追踪 (History Tracking)**：自动记录并展示您最近在资源管理器中访问过的文件夹与文件路径。
* ⚡ **极简设计 (Minimalist Design)**：采用类似 Spotlight 的无边框浮动窗口设计，贴合鼠标位置即时呼出，失焦自动隐藏。
* 🚀 **一键触达 (Quick Navigation)**：快速检索并跳转至历史路径，极大地提升文件管理效率。
* 🔒 **单例模式 (Single Instance)**：通过 Named Event / Mutex 确保系统全局仅运行一个实例，重复运行将自动唤醒并置顶已有窗口。
* 🛠️ **Win32 底层结合 (Win32 Interop)**：结合原生 API 在 OS 级别控制窗口行为，保证交互平滑稳定。

---

## 运行与编译 (Build & Run)

本项目基于 **.NET 8.0** 与 **Avalonia UI** 开发。

### 前提条件 (Prerequisites)
* 已安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本。

### 编译构建 (Build)
使用 dotnet CLI 或 Visual Studio 进行构建：
```bash
dotnet build -c Release
```

您也可以直接双击运行根目录下的 `编译.bat` 自动完成编译和发布包 durable 构建。

---

## 开源协议 (License)
本项目以开源形式发布，欢迎大家提 Issue 或 Pull Request 共同改进！
