## Moonlight Hub 副屏中心 v1.0.0

把平板 / 手机变成 Windows 电脑的多块低延迟副屏的桌面上位机（Parsec VDD + Sunshine + Moonlight）。

### 亮点
- 直接驱动 Parsec 虚拟显示驱动并持续 keep-alive，虚拟屏不再"几秒后消失"
- 方案一键切换：仅主屏 / 主屏+1~3 副屏 / 仅副屏 / 复制；显示布局页可拖拽摆放
- 每块副屏一个独立 Sunshine 实例，多台 Moonlight 可同时连接
- 客户端发起配对时自动弹出 PIN 窗口，Web 凭据自动管理，不再需要打开 Sunshine 网页
- 没有客户端连接时副屏自动待机，连接时由 Sunshine 自动启用，断开后还原
- 虚拟屏分辨率 / 刷新率跟随客户端请求（预旋转竖屏平板自动转横屏）
- 守护：虚拟屏丢失重建、实例掉线重启、睡眠唤醒复核、驱动异常一键重启适配器
- 托盘图标、开机自启、无中断热更新（`tools/update-hub.ps1`）

### 安装
- `MoonlightHub-Setup-1.0.0.exe`：安装包（需要 .NET 9 Desktop Runtime x64，缺失时会提示下载）
- `MoonlightHub-1.0.0-win-x64-portable.zip`：免安装版，解压运行 `MoonlightHub.exe`

### 依赖
Sunshine（推荐 2025.924 + `sunshine-patches/` 补丁以启用自动配对提示与分辨率跟随）、ParsecVDisplay 0.45 的驱动。
