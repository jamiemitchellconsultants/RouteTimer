using Microsoft.AspNetCore.Http;
using RouteTimer.Api;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class TrainingUploadBatchTests
{
    [Fact]
    public void Open_disposes_previously_opened_streams_when_a_later_stream_fails()
    {
        var firstStream = new TrackingStream();
        var files = new FormFileCollection
        {
            new TestFormFile("first.fit", firstStream),
            new ThrowingFormFile("second.fit")
        };

        Assert.Throws<InvalidOperationException>(() => TrainingUploadBatch.Open(files));

        Assert.True(firstStream.WasDisposed);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TestFormFile(string fileName, Stream stream) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => stream.Length;
        public string Name => "file";
        public string FileName => fileName;
        public Stream OpenReadStream() => stream;
        public void CopyTo(Stream target) => stream.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => stream.CopyToAsync(target, cancellationToken);
    }

    private sealed class ThrowingFormFile(string fileName) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => 0;
        public string Name => "file";
        public string FileName => fileName;
        public Stream OpenReadStream() => throw new InvalidOperationException("stream open failed");
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
