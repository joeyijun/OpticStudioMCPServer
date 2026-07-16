# ZemaxProject 项目记忆汇总

> 来源：Claude Code 项目记忆 (`C:\Users\<you>\.claude\projects\E--ZemaxProject\memory\`)
> 导出日期：2026-04-16

---

## 1. Zemax MCP 预连接机制

**类型**：project
**概述**：OpticStudio MCP Server 使用后台预连接方案，源码在 `E:\ZemaxProject\ZemaxMCP_源码_本地开发版`，部署到 `C:\Users\<you>\Documents\Zemax\ZemaxMCP`

OpticStudio Standalone 模式的 `CreateNewApplication()` 需要 3-5 秒。为避免阻塞 MCP 握手，采用后台异步连接。

**当前方案（2026-04-16 更新）**：
- `Program.cs`：启动时 `session.StartConnectInBackground()` 后台连接，不阻塞 `host.RunAsync()`
- `ConnectTool.cs`：`zemax_connect` 被显式调用时，等待后台连接完成后返回 `isConnected: true`
- `ZemaxSession.cs`：`ExecuteAsync` 在后台连接进行中时自动等待完成（不再抛异常）；新增 `WaitForBackgroundConnectAsync()`
- `IZemaxSession.cs`：接口新增 `WaitForBackgroundConnectAsync()`

**原因**：需要同时兼容 Claude Code（stdio 超时宽容）和 Codex（超时严格）等不同 MCP 客户端。

**使用要点**：
- 正常使用无需手动调用 `zemax_connect`（启动已后台预连接）
- 显式调用 `zemax_connect` 会阻塞等待连接完成再返回结果
- 任何工具调用如果在后台连接中，自动等待而不是报错
- 异常退出后可能有残留 `OpticStudio.exe` 进程，需手动终止以释放许可证

---

## 2. Zemax IMA 设置持久化经验

**类型**：project
**概述**：通过 ZOSAPI 修改 IMA 分析设置并持久化到桌面版 OpticStudio 的完整方案，包括 CFG 格式补丁

### 核心难点

**2.1 ZOSAPI SaveTo 的 CFG 格式版本不兼容**
- ZOSAPI `SaveTo()` 写入的 CFG 文件偏移 0x0C 为 `0x04`
- 桌面版 OpticStudio 2023 R1 期望该字节为 `0x03`
- 不补丁的话桌面版会忽略部分设置（如 ShowAs 枚举）
- **修复**：SaveTo 之后二进制补丁 0x0C 为 `0x03`

**2.2 文件级 .CFG 覆盖全局 IMA.CFG**
- 桌面版优先读取与 zmx 同目录的 `<同名>.CFG`，其次才读全局 `Configs\IMA.CFG`
- **修复**：saveSettings 同时写入两个文件（全局 + 文件级），都做 0x0C 补丁

**2.3 SaveTo 必须用已修改的 settings 对象**
- `analysis.GetSettings()` 每次返回**新的**未修改对象
- 必须用在内存中已通过反射/TrySetSimpleProperty 修改过的 `imaSettingsObj` 来调用 SaveTo

**2.4 参数默认值覆盖问题**
- 非 null 默认值（如 `int pixelsX = 128`）会在不传参时覆盖已保存的值
- **修复**：所有参数改为可空类型 `int? pixels = null`，null = 不修改

**2.5 RaysX1000 属性名错误**
- 原代码用 `imaSettings.RayDensity`（dynamic 绑定），实际属性名是 `RaysX1000`
- **修复**：改用 `TrySetSimpleProperty(imaSettingsObj, "RaysX1000", ...)` 反射方式

### CFG 二进制格式（OpticStudio 2023 R1）

| 偏移 | 含义 | 示例值 |
|------|------|--------|
| 0x0C | 格式版本（桌面版=03, ZOSAPI=04） | `03` |
| 0x18 | NumberOfPixels | `64 00 00 00` (=100) |
| 0x20 | ShowAs 枚举 | `02`=GreyScale, `04`=FalseColor, `06`=SpotDiagram |
| 0x24 | RaysX1000 | `64 00 00 00` (=100) |

### 正确的持久化流程

```csharp
// 1. 在已修改的 imaSettingsObj 上调用 SaveTo
((dynamic)imaSettingsObj).SaveTo(imaCfgPath);

// 2. 补丁 0x0C 为 0x03
var bytes = File.ReadAllBytes(imaCfgPath);
if (bytes.Length > 0x0C && bytes[0x0C] != 0x03)
{
    bytes[0x0C] = 0x03;
    File.WriteAllBytes(imaCfgPath, bytes);
}

// 3. 同时写入文件级 .CFG
var perFileCfg = Path.ChangeExtension(currentFilePath, ".CFG");
((dynamic)imaSettingsObj).SaveTo(perFileCfg);
PatchCfgVersion(perFileCfg);
```

**原因**：ZOSAPI standalone 模式与桌面版 OpticStudio 的 CFG 文件格式存在版本差异，且设置存储有全局/文件级两层优先级机制。

**使用要点**：任何通过 ZOSAPI SaveTo 写入的 CFG 文件都需要做 0x0C 补丁。如果需要设置生效于特定文件，必须同时写入文件级 .CFG。

---

## 3. Git push 直接执行

**类型**：feedback
**概述**：git push 不要预检认证，直接执行即可，Credential Manager 自动处理

git push 时直接执行命令，不要提前检查凭证、测试 SSH、或让用户手动操作。Windows 上 Git Credential Manager 会自动处理认证流程。

**原因**：用户环境已配置 system 级 `credential.helper=manager`，push 时自动弹出认证或使用缓存凭证。提前检查凭证反而读到了错误的缓存 token 导致误判。

**使用要点**：需要 push 时直接 `git push`，失败了再排查原因。不要预检、不要让用户手动跑命令。

---

## 4. Codex Zemax MCP HTTP 代理方案

**类型**：project
**概述**：Codex 无法通过 stdio 直连 Zemax MCP（沙箱导致 License Unknown），需通过 HTTP 代理连接；Claude Code 走 stdio 直连

Codex CLI 在 Windows 上对 MCP Server 子进程施加安全沙箱限制（restricted token），导致 ZOSAPI 无法验证 Zemax 许可证（`LicenseStatus="Unknown"`），即使环境变量完全一致也无法绕过。

**原因**：Codex 的 MCP 进程沙箱限制了 COM 注册表/许可证文件访问，而 Zemax ZOSAPI 的许可证检测依赖这些系统资源。

**连接方式**：
- **Claude Code**：`type = "stdio"` 直连 `ZemaxMCP.Server.exe`（不受沙箱影响）
- **Codex**：`type = "http"`, `url = "http://localhost:8080/mcp"`，通过 HTTP 代理连接

**相关文件**：
- 代理脚本：`C:\Users\<you>\Documents\Zemax\ZemaxMCP\mcp-http-proxy.mjs`（Node.js，单进程有状态）
- 开机自动启动：`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\start-zemax-proxy.vbs`
- 桌面控制台：`Zemax MCP Proxy` 快捷方式 → `zemax-proxy.ps1`（启动/停止/重启/状态）
- 参考文档：`E:\ZemaxProject\Zemax_MCP_direct_stdio_连接说明.md`

**注意**：两个客户端**不能同时连 Zemax**（单许可证限制）。
