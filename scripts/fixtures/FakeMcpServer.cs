using System;
using System.Text.RegularExpressions;
using System.Threading;

public static class Program
{
    private static string Id(string line)
    {
        var match = Regex.Match(line, "\\\"id\\\"\\s*:\\s*(\\\"(?:\\\\.|[^\\\"])*\\\"|-?\\d+(?:\\.\\d+)?)");
        return match.Success ? match.Groups[1].Value : "null";
    }

    public static void Main()
    {
        string line;
        while ((line = Console.ReadLine()) != null)
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
            if (line.Contains("\"method\":\"test/server-request\""))
            {
                // Deliberately wait for the client response. This exercises the
                // real bidirectional server-request path and exposes deadlocks.
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"sampling-request-1\",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                Console.Out.Flush();
                var samplingResponse = Console.ReadLine();
                if (samplingResponse == null || !samplingResponse.Contains("\"id\":\"sampling-request-1\"") || !samplingResponse.Contains("\"result\""))
                    throw new InvalidOperationException("Expected a response to sampling/createMessage.");
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"completed\":true}}");
                Console.Out.Flush();
                continue;
            }
            if (line.Contains("\"method\":\"test/wait-cancel\""))
            {
                // A cancellation notification must arrive while the original
                // request is still executing, not after it releases its lock.
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"message\":\"ready-cancel\"}}");
                Console.Out.Flush();
                var cancellation = Console.ReadLine();
                if (cancellation == null || !cancellation.Contains("\"method\":\"notifications/cancelled\""))
                    throw new InvalidOperationException("Expected notifications/cancelled while request was active.");
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"cancelled\":true}}");
                Console.Out.Flush();
                continue;
            }
            if (line.Contains("\"method\":\"tools/list\""))
            {
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":50}}");
                Console.Out.Flush();
            }
            if (initialize && line.Contains("\"name\":\"fail-init\""))
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32099,\"message\":\"simulated initialize failure\"}}");
            else Console.WriteLine(initialize
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{\"name\":\"fake-mcp\",\"version\":\"1.0\"}}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
            Console.Out.Flush();
        }
    }
}
