# Moonlight Hub 副屏中心 v2.0.11

### 修复

- **串流约一分钟后虚拟屏被停用、Sunshine 回退抓主屏**：Sunshine 的 RTSP 端口并不始终保有 TCP 连接，
  之前用“没有 RTSP 连接”判断空闲会误判。现在改为读取 Sunshine 日志里最后一条
  `CLIENT CONNECTED / CLIENT DISCONNECTED`，客户端连接期间绝不会停用虚拟屏。
- 日志已记录断开后仍保留 20 秒缓冲，避免客户端重连瞬间被切走；日志尚未写出连接状态时按“仍在串流”
  处理，优先保证画面不中断。
