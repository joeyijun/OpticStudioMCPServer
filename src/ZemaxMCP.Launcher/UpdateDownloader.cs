using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZemaxMCP.Launcher;

internal static class UpdateDownloader
{
    internal static async Task DownloadAsync(HttpClient client, string url, Stream destination, long maximumBytes,
        CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Update download exceeds its size limit.");
        using var abort = cancellationToken.Register(() => response.Dispose());
        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[65536];
        long total = 0;
        long lastReported = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > maximumBytes) throw new IOException("Update download exceeds its size limit.");
                await destination.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                if (total - lastReported >= 1024 * 1024) { progress?.Report(total); lastReported = total; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(total);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
    internal static async Task<string> ReadMetadataAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        await DownloadAsync(client, url, bytes, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF');
    }
    internal static async Task DownloadFileAsync(HttpClient client, string url, string path,
        CancellationToken cancellationToken, IProgress<long> progress)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await DownloadAsync(client, url, file, 512L * 1024 * 1024, cancellationToken, progress).ConfigureAwait(false);
    }
}
