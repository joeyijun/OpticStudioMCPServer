using System.Net;
using System.Text;
using ZemaxMCP.Launcher;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyUpdateDownloadsAsync()
    {
        using var good = new HttpClient(new DownloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("\uFEFF{\"signed\":true}")) }));
        var text = await UpdateDownloader.ReadMetadataAsync(good, "https://test.invalid/metadata", CancellationToken.None);
        Assert(text.StartsWith("{"), "BOM-prefixed update metadata was not decoded compatibly.");
        using var failed = new HttpClient(new DownloadHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        try { await UpdateDownloader.ReadMetadataAsync(failed, "https://test.invalid/404", CancellationToken.None); throw new Exception("Accepted failed download."); }
        catch (HttpRequestException) { }
        using var oversized = new HttpClient(new DownloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[32]) }));
        using var destination = new MemoryStream();
        try { await UpdateDownloader.DownloadAsync(oversized, "https://test.invalid/large", destination, 16, CancellationToken.None); throw new Exception("Accepted oversized update."); }
        catch (IOException) { }
        using var unboundedLength = new HttpClient(new DownloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableBytesStream(new byte[32])) }));
        try { await UpdateDownloader.DownloadAsync(unboundedLength, "https://test.invalid/no-length", destination, 16, CancellationToken.None); throw new Exception("Accepted oversized body without Content-Length."); }
        catch (IOException) { }

        // Simulates a half-open body that ignores ReadAsync's token. Disposing
        // the HTTP response on cancellation must still release the read.
        using var stalledStream = new StalledUpdateStream();
        using var stalled = new HttpClient(new DownloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stalledStream) }));
        using var cancellation = new CancellationTokenSource(100);
        var download = UpdateDownloader.ReadMetadataAsync(stalled, "https://test.invalid/stall", cancellation.Token);
        Assert(await Task.WhenAny(download, Task.Delay(2000)) == download, "Cancelled stalled update body did not terminate.");
        try { await download; throw new Exception("Cancelled update returned successful metadata."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Asynchronous update downloads: valid/BOM metadata, HTTP errors, size bounds and stalled-body cancellation passed.");
    }

    private sealed class DownloadHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }
    private sealed class StalledUpdateStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _read.Task;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _read.TrySetException(new ObjectDisposedException(nameof(StalledUpdateStream))); base.Dispose(disposing); }
    }
    private sealed class NonSeekableBytesStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
