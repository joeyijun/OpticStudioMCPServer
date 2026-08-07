using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public static class Program
{
    private static string Id(string line)
    {
        var match = Regex.Match(line, "\\\"id\\\"\\s*:\\s*(\\\"(?:\\\\.|[^\\\"])*\\\"|-?\\d+(?:\\.\\d+)?)");
        return match.Success ? match.Groups[1].Value : "null";
    }

    private static string GetPipeName(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index].Equals("--pipe", StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return null;
    }

    public static void Main(string[] args)
    {
        var pipeName = GetPipeName(args);
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Serve(Console.In, Console.Out);
            return;
        }

        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect();
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
            {
                writer.AutoFlush = true;
                var secret = Environment.GetEnvironmentVariable("ZEMAX_MCP_PIPE_SECRET");
                if (!string.IsNullOrEmpty(secret))
                {
                    Send(writer, "ZEMAX_MCP_PIPE_HELLO|" + Process.GetCurrentProcess().Id + "|" + secret);
                    if (!string.Equals(reader.ReadLine(), "ZEMAX_MCP_PIPE_OK", StringComparison.Ordinal))
                        throw new InvalidOperationException("The fake Worker pipe handshake was rejected by the Host.");
                }
                Serve(reader, writer);
            }
        }
    }

    private static void Send(TextWriter writer, string message)
    {
        writer.WriteLine(message);
        writer.Flush();
    }

    private static void Serve(TextReader reader, TextWriter writer)
    {
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.Contains("\"id\"")) continue;
            if (!line.Contains("\"method\"")) continue; // Client response to a server request.
            if (line.Contains("\"method\":\"test/hang\""))
            {
                Thread.Sleep(60000);
                continue;
            }
            var id = Id(line);
            var initialize = line.Contains("\"method\":\"initialize\"");
            if (line.Contains("\"method\":\"test/duplicate-server-request\""))
            {
                // The bridge must treat this protocol violation as a fatal
                // stdout-pump failure and restart the backend process.
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":\"duplicate-server-request\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":\"duplicate-server-request\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                Thread.Sleep(60000);
                continue;
            }
            if (line.Contains("\"method\":\"test/server-request\""))
            {
                // Deliberately wait for the client response. This exercises the
                // real bidirectional server-request path and exposes deadlocks.
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":\"sampling-request-1\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                var samplingResponse = reader.ReadLine();
                if (samplingResponse == null || !samplingResponse.Contains("\"id\":\"sampling-request-1\"") || !samplingResponse.Contains("\"result\""))
                    throw new InvalidOperationException("Expected a response to sampling/createMessage.");
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"completed\":true}}");
                continue;
            }
            if (line.Contains("\"method\":\"test/server-request-no-sse\""))
            {
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":\"unreachable-sampling-request\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                var deliveryError = reader.ReadLine();
                if (deliveryError == null || !deliveryError.Contains("\"id\":\"unreachable-sampling-request\"") || !deliveryError.Contains("\"code\":-32601"))
                    throw new InvalidOperationException("Expected an error for an undeliverable server request.");
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"deliveryRejected\":true}}");
                continue;
            }
            if (line.Contains("\"method\":\"test/wait-cancel\""))
            {
                // A cancellation notification must arrive while the original
                // request is still executing, not after it releases its lock.
                Send(writer, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"message\":\"ready-cancel\"}}");
                var cancellation = reader.ReadLine();
                if (cancellation == null || !cancellation.Contains("\"method\":\"notifications/cancelled\"") || !cancellation.Contains("\"requestId\":\"cancel-parent\""))
                    throw new InvalidOperationException("Expected cancellation for the active request.");
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"cancelled\":true}}");
                continue;
            }
            if (line.Contains("\"method\":\"tools/list\""))
            {
                Send(writer, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":50}}");
            }
            if (initialize && line.Contains("\"name\":\"fail-init\""))
                Send(writer, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32099,\"message\":\"simulated initialize failure\"}}");
            else Send(writer, initialize
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"" + (line.Contains("2025-11-25") || line.Contains("\"name\":\"mismatched-protocol\"") ? "2025-11-25" : "2025-03-26") + "\",\"serverInfo\":{\"name\":\"fake-mcp\",\"version\":\"1.0\"}}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
        }
    }
}
