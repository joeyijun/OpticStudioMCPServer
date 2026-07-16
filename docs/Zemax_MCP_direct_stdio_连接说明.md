# Zemax MCP direct stdio 连接说明

## 目的

这份文档记录在本机上如何不经过当前宿主的 MCP 封装层，直接通过 `stdin/stdout` 启动并调用 `ZemaxMCP.Server.exe`，从而连接 `Zemax OpticStudio`。

适用场景：

- 需要验证 `ZemaxMCP.Server.exe` 本体是否可用
- 内置 MCP 工具链路返回异常，但怀疑服务器本身仍可工作
- 需要做最小可复现连接测试

## 当前环境

- 工作目录：`E:\ZemaxProject`
- Zemax MCP 服务程序：`C:\Users\<you>\Documents\Zemax\ZemaxMCP\ZemaxMCP.Server.exe`
- 连接模式：`standalone`

## 原理

`direct stdio` 的本质是：

1. 用子进程直接启动 `ZemaxMCP.Server.exe`
2. 按 JSON-RPC / MCP 格式向进程的 `stdin` 发送请求
3. 从 `stdout` 逐行读取响应
4. 使用 `tools/call` 调用 `zemax_connect`
5. 再调用 `zemax_status` 确认连接状态

这条链路不依赖当前会话里的 MCP 宿主封装，因此更适合做底层连通性排查。

## 最小连接流程

### 1. 启动服务进程

Python 里最小做法如下：

```python
import subprocess
from pathlib import Path

exe = Path(r"C:\Users\<you>\Documents\Zemax\ZemaxMCP\ZemaxMCP.Server.exe")

proc = subprocess.Popen(
    [str(exe)],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding="utf-8",
    bufsize=1,
)
```

### 2. 发送 `initialize`

发送给服务端的消息：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": {
      "name": "codex-direct-stdio",
      "version": "1.0"
    }
  }
}
```

本机实测返回中，服务端声明：

- `serverInfo.name = "zemax-mcp"`
- `serverInfo.version = "1.0.0"`
- `protocolVersion = "2024-11-05"`

## 3. 调用 `zemax_connect`

通过 `tools/call` 调用：

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "zemax_connect",
    "arguments": {
      "mode": "standalone"
    }
  }
}
```

成功时典型返回：

```json
{
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"success\":true,\"isConnected\":true,\"mode\":\"Standalone\"}"
      }
    ],
    "isError": false
  }
}
```

## 4. 调用 `zemax_status`

继续确认连接状态：

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "zemax_status",
    "arguments": {}
  }
}
```

成功时典型返回：

```json
{
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"success\":true,\"isConnected\":true}"
      }
    ],
    "isError": false
  }
}
```

## 可直接运行的最小脚本

```python
import json
import subprocess
from pathlib import Path

exe = Path(r"C:\Users\<you>\Documents\Zemax\ZemaxMCP\ZemaxMCP.Server.exe")

proc = subprocess.Popen(
    [str(exe)],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding="utf-8",
    bufsize=1,
)


def request(req_id, method, params=None):
    payload = {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": method,
        "params": params or {},
    }
    assert proc.stdin is not None
    assert proc.stdout is not None
    proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
    proc.stdin.flush()

    while True:
        line = proc.stdout.readline()
        if not line:
            stderr = proc.stderr.read() if proc.stderr else ""
            raise RuntimeError(f"server exited unexpectedly: {stderr}")
        line = line.strip()
        if not line or not line.startswith("{"):
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        if msg.get("id") == req_id:
            return msg


try:
    init_resp = request(
        1,
        "initialize",
        {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "codex-direct-stdio", "version": "1.0"},
        },
    )
    connect_resp = request(
        2,
        "tools/call",
        {"name": "zemax_connect", "arguments": {"mode": "standalone"}},
    )
    status_resp = request(
        3,
        "tools/call",
        {"name": "zemax_status", "arguments": {}},
    )

    print("initialize =", json.dumps(init_resp, ensure_ascii=False, indent=2))
    print("connect =", json.dumps(connect_resp, ensure_ascii=False, indent=2))
    print("status =", json.dumps(status_resp, ensure_ascii=False, indent=2))
finally:
    if proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
```

## 本项目内已有参考实现

可以直接参考这些脚本里的 `McpStdioClient`：

- `E:\ZemaxProject\ima_photutils_psf\run_ima_psf_photometry.py`
- `E:\ZemaxProject\psf_compare\export_defocus_minus2p5_visualization.py`
- `E:\ZemaxProject\psf_compare\scan_intra_match_with_mcp.py`

这些脚本都采用同样的模式：

- `subprocess.Popen(...)` 启动 `ZemaxMCP.Server.exe`
- 用 `initialize` 建立会话
- 用 `tools/call` 调用 `zemax_connect`
- 再继续做 `open_file`、`set_fields`、`geometric_image_analysis` 等操作

## 和内置 MCP 工具链路的区别

`direct stdio` 与当前对话内的内置 `mcp__zemax__...` 工具有明显区别：

- `direct stdio`：每次都是新起一个独立的 `ZemaxMCP.Server.exe` 进程
- 内置 MCP 工具：走宿主预先接好的 MCP 服务器链路

因此可能出现这样的情况：

- `direct stdio` 能连上
- 内置 MCP 工具返回 `Invalid Zemax license: Unknown`

这通常说明：

- `ZemaxMCP.Server.exe` 本体和 `standalone` 直连能力是正常的
- 出问题的更可能是宿主这层会话状态、连接缓存、启动参数，或者它使用的不是同一个服务器实例

## 已观察到的兼容性现象

本机测试时，`notifications/initialized` 可能返回：

```text
Method 'notifications/initialized' is not available.
```

但这不影响后续 `tools/call` 调用，也不影响 `zemax_connect` 成功。

因此：

- `initialize` 是必要的
- `notifications/initialized` 不是当前这套服务成功连接的前提

## 排查建议

如果 `direct stdio` 失败，优先检查：

1. `ZemaxMCP.Server.exe` 路径是否正确
2. OpticStudio 是否已正确安装
3. 当前用户是否能正常使用 Zemax 许可证
4. 是否有残留的 Zemax / OpticStudio 进程卡住
5. 服务端 `stderr` 是否输出了更具体的异常

如果 `direct stdio` 成功但内置 MCP 工具失败，优先怀疑：

1. 宿主侧 MCP 会话缓存
2. 宿主使用的服务器配置和手工直连不是同一份
3. 宿主链路中的启动参数或权限环境不同

## 结论

在当前机器上，`direct stdio` 方式已经多次实测成功完成：

- `initialize`
- `zemax_connect(mode="standalone")`
- `zemax_status()`

所以目前可以确认：

- `ZemaxMCP.Server.exe` 本体可启动
- `standalone` 直连链路可用
- 若内置 MCP 工具偶发失败，问题不一定在 Zemax 服务器本体
