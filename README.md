# Moonlight Hub 副屏中心

把平板 / 手机变成 Windows 电脑的**多块低延迟副屏**：一个 Logitech G HUB 风格的桌面上位机，
统一管理 **Parsec 虚拟显示驱动（Parsec VDD）**、**Sunshine 多实例**和 **Moonlight 客户端配对**。

![.NET 9](https://img.shields.io/badge/.NET-9.0%20WPF-512BD4) ![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)

## 功能

* **虚拟副屏永不掉线**：直接通过设备接口驱动 Parsec VDD，内置 50 ms keep-alive（驱动 100 ms 内收不到 ping 就会移除虚拟屏，这正是很多"副屏几秒后消失"问题的根因）。
* **方案一键切换**：仅主屏 / 主屏 + 1~3 块副屏 / 仅副屏（低延迟）/ 复制到副屏；分辨率、刷新率、摆放位置可编辑，"显示布局"页支持拖拽摆放。
* **多台 Moonlight 同时连接**：每块副屏对应一个独立的 Sunshine 实例（不同端口），各自的 `output_name` 由 Hub 按 Sunshine/libdisplaydevice 的算法直接计算，触摸/鼠标坐标各自映射到自己的屏幕。
* **配对不再打开网页**：客户端发起配对时 Hub 自动弹出 PIN 输入框（托盘气泡提示），输入即完成；Web 凭据由 Hub 自动生成并加密保存。
* **无连接时自动关闭副屏**：虚拟屏平时"已连接但停用"，客户端连接时由 Sunshine 在抓取前自动启用，断开 3 秒后还原。
* **跟随客户端**：虚拟屏刷新率/分辨率自动匹配客户端请求（竖屏平板 1200×2000 → 主机 2000×1200 预旋转）。
  方案里的刷新率是下限，客户端帧率更高时自动提高；没有正好的模式时选它的倍数或更高一档（只注册了 120Hz 时 60/90fps 都用 120Hz）。
* **驱动模式表同步**：Parsec 驱动只在虚拟屏重新插拔时读取 `HKLM\SOFTWARE\Parsec\vdd` 的自定义模式。在"虚拟屏"页注册/删除模式后，
  Hub 自动重新插拔空闲的虚拟屏；正在串流的虚拟屏标记为"待重新插拔"，客户端断开后自动处理。"显示布局"页的刷新率下拉框会把
  已注册但驱动尚未发布的模式一并列出，选择后记入方案。
* **守护**：虚拟屏丢失自动重建、实例掉线自动重启、睡眠唤醒/显示变更后复核、驱动异常一键重启适配器。
* **无中断更新**：`tools/update-hub.ps1` 热替换正在运行的 Hub，虚拟屏与串流会话不断。
* **命令行**：`MoonlightHub.exe --cli status|displays|apply <方案>|instances|pair <id> <PIN>|clients|unpair|adapter|takeover|restore|autostart on|off|shutdown …`

## 运行要求

* Windows 10 19041+ / Windows 11，x64
* [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)（安装包会检测并提示）
* [Sunshine](https://github.com/LizardByte/Sunshine)（推荐 2025.924，配合 `sunshine-patches/` 里的补丁可获得自动配对提示与分辨率跟随；不打补丁也能用，退化为手动输入 PIN）
* [Parsec VDD](https://github.com/nomi-san/parsec-vdd)（ParsecVDisplay 0.45 附带的驱动）
* 客户端：Moonlight（Android / iOS / PC）

## 快速开始

1. 安装 Sunshine 与 ParsecVDisplay（只需要它的驱动），安装本程序（`MoonlightHub-Setup-*.exe`）。
2. 打开 Hub → 设置 → 确认 `sunshine.exe` 路径与实例端口（默认 47989 / 48989 / 49989）→ 保存。
3. 概览页选择方案，例如「主屏 + 1 副屏」。
4. 平板 Moonlight 添加电脑 IP（实例 2/3 用 `IP:48989` 这种带端口的形式），点击后在 Hub 弹出的窗口里输入 PIN。
5. 连接后副屏自动启用；断开后自动待机。

## 构建

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained false -o ..\dist
# 安装包（需要 Inno Setup 6）
ISCC.exe ..\installer\MoonlightHub.iss
```

编译中间目录默认重定向到 `%LOCALAPPDATA%\MoonlightHub\build`（见 `src/Directory.Build.props`）。

## 目录

| 目录 | 内容 |
| --- | --- |
| `src/` | WPF 应用源码（Core = 驱动/显示/Sunshine/守护逻辑，UI = 界面） |
| `installer/` | Inno Setup 脚本 |
| `tools/` | `update-hub.ps1` 热更新、`organize-folder.ps1` 工作区整理 |
| `sunshine-patches/` | 定制 Sunshine 的补丁与说明 |

## 原理简述

* Parsec VDD 协议：`\\?\root#display#0000#{00b41627-04c4-429e-a26e-0265cf50c8fa}` 设备接口，IOCTL `ADD 0x0022e004 / REMOVE 0x0022a008 / UPDATE 0x0022a00c / VERSION 0x0022e010`；UPDATE 必须持续发送。
* Sunshine 设备 ID：`UUIDv5(nil, EDID + 实例ID稳定部分 + UTF-16 NUL)`，与 libdisplaydevice 一致，因此可在启动实例前就确定 `output_name`。
* 待机：实例配置 `dd_configuration_option = ensure_active`（仅副屏方案用 `ensure_only_display`）+ `dd_config_revert_on_disconnect = enabled`。

## 许可

本仓库暂未指定开源许可证（All rights reserved），如需开源请自行添加 LICENSE。
