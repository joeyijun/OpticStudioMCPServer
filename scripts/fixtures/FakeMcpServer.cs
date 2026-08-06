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
                // Use the same id as the client request. A bridge must not treat
                // this server-initiated request as the response it is awaiting.
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[]}}");
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"completed\":true}}");
                Console.Out.Flush();
                continue;
            }
            if (line.Contains("\"method\":\"tools/list\""))
            {
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":50}}");
                Console.Out.Flush();
            }
            Console.WriteLine(initialize
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{\"name\":\"fake-mcp\",\"version\":\"1.0\"}}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
            Console.Out.Flush();
        }
    }
}
