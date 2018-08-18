﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Moq;
using Nerdbank.Streams;
using Xunit;
using Xunit.Abstractions;

public class PipeExtensionsTests : TestBase
{
    public PipeExtensionsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void UsePipeReader_WebSocket_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => PipeExtensions.UsePipeReader((WebSocket)null));
    }

    [Fact]
    public void UsePipeWriter_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => PipeExtensions.UsePipeWriter((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PipeExtensions.UsePipeWriter((WebSocket)null));
    }

    [Fact]
    public void UsePipeWriter_NonReadableStream()
    {
        var unreadableStream = new Mock<Stream>(MockBehavior.Strict);
        unreadableStream.SetupGet(s => s.CanWrite).Returns(false);
        Assert.Throws<ArgumentException>(() => unreadableStream.Object.UsePipeWriter());
        unreadableStream.VerifyAll();
    }

    [Fact]
    public async Task UsePipeWriter_Stream()
    {
        byte[] expectedBuffer = this.GetRandomBuffer(2048);
        var stream = new MemoryStream(expectedBuffer.Length);
        var writer = stream.UsePipeWriter(this.TimeoutToken);
        await writer.WriteAsync(expectedBuffer.AsMemory(0, 1024), this.TimeoutToken);
        await writer.WriteAsync(expectedBuffer.AsMemory(1024, 1024), this.TimeoutToken);

        // As a means of waiting for the async process that copies what we write onto the stream,
        // complete our writer and wait for the reader to complete also.
        writer.Complete();
        await writer.WaitForReaderCompletionAsync().WithCancellation(this.TimeoutToken);

        Assert.Equal(expectedBuffer, stream.ToArray());
    }

    [Fact]
    public async Task UsePipeWriter_StreamFails()
    {
        var expectedException = new InvalidOperationException();
        var unreadableStream = new Mock<Stream>(MockBehavior.Strict);
        unreadableStream.SetupGet(s => s.CanWrite).Returns(true);

        // Set up for either ReadAsync method to be called. We expect it will be Memory<T> on .NET Core 2.1 and byte[] on all the others.
#if NETCOREAPP2_1
        unreadableStream.Setup(s => s.WriteAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Throws(expectedException);
#else
        unreadableStream.Setup(s => s.WriteAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(expectedException);
#endif

        var writer = unreadableStream.Object.UsePipeWriter();
        await writer.WriteAsync(new byte[1], this.TimeoutToken);
        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WaitForReaderCompletionAsync().WithCancellation(this.TimeoutToken));
        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task UsePipeWriter_Stream_TryWriteAfterComplete()
    {
        byte[] expectedBuffer = this.GetRandomBuffer(2048);
        var stream = new MemoryStream(expectedBuffer.Length);
        var writer = stream.UsePipeWriter();
        await writer.WriteAsync(expectedBuffer, this.TimeoutToken);
        writer.Complete();
        Assert.Throws<InvalidOperationException>(() => writer.GetMemory());
        Assert.Throws<InvalidOperationException>(() => writer.GetSpan());
        Assert.Throws<InvalidOperationException>(() => writer.Advance(0));
    }

    [Fact]
    public async Task UsePipeWriter_Stream_Flush_Precanceled()
    {
        byte[] expectedBuffer = this.GetRandomBuffer(2048);
        var stream = new MemoryStream(expectedBuffer.Length);
        var writer = stream.UsePipeWriter();
        var memory = writer.GetMemory(expectedBuffer.Length);
        expectedBuffer.CopyTo(memory);
        writer.Advance(expectedBuffer.Length);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.FlushAsync(new CancellationToken(true)).AsTask());
    }

    [Fact]
    public async Task UsePipe_Stream()
    {
        var ms = new HalfDuplexStream();
        IDuplexPipe pipe = ms.UsePipe(cancellationToken: this.TimeoutToken);
        await pipe.Output.WriteAsync(new byte[] { 1, 2, 3 }, this.TimeoutToken);
        var readResult = await pipe.Input.ReadAsync(this.TimeoutToken);
        Assert.Equal(3, readResult.Buffer.Length);
        pipe.Input.AdvanceTo(readResult.Buffer.End);
    }

    [Fact]
    public async Task UsePipeReader_WebSocket()
    {
        var expectedBuffer = new byte[] { 4, 5, 6 };
        var webSocket = new MockWebSocket();
        webSocket.EnqueueRead(expectedBuffer);
        var pipeReader = webSocket.UsePipeReader(cancellationToken: this.TimeoutToken);
        var readResult = await pipeReader.ReadAsync(this.TimeoutToken);
        Assert.Equal(expectedBuffer, readResult.Buffer.First.Span.ToArray());
        pipeReader.AdvanceTo(readResult.Buffer.End);
    }

    [Fact]
    public async Task UsePipeWriter_WebSocket()
    {
        var expectedBuffer = new byte[] { 4, 5, 6 };
        var webSocket = new MockWebSocket();
        var pipeWriter = webSocket.UsePipeWriter(this.TimeoutToken);
        await pipeWriter.WriteAsync(expectedBuffer, this.TimeoutToken);
        pipeWriter.Complete();
        await pipeWriter.WaitForReaderCompletionAsync();
        var message = webSocket.WrittenQueue.Dequeue();
        Assert.Equal(expectedBuffer, message.Buffer.ToArray());
    }

    [Fact]
    public async Task UsePipe_WebSocket()
    {
        var expectedBuffer = new byte[] { 4, 5, 6 };
        var webSocket = new MockWebSocket();
        webSocket.EnqueueRead(expectedBuffer);
        var pipe = webSocket.UsePipe(cancellationToken: this.TimeoutToken);

        var readResult = await pipe.Input.ReadAsync(this.TimeoutToken);
        Assert.Equal(expectedBuffer, readResult.Buffer.First.Span.ToArray());
        pipe.Input.AdvanceTo(readResult.Buffer.End);

        await pipe.Output.WriteAsync(expectedBuffer, this.TimeoutToken);
        pipe.Output.Complete();
        await pipe.Output.WaitForReaderCompletionAsync();
        var message = webSocket.WrittenQueue.Dequeue();
        Assert.Equal(expectedBuffer, message.Buffer.ToArray());
    }
}
