﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Buffers;
    using System.CodeDom.Compiler;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Pipelines;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Runtime.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <content>
    /// Contains the <see cref="Channel"/> nested type.
    /// </content>
    public partial class MultiplexingStream
    {
        /// <summary>
        /// An individual channel within a <see cref="Streams.MultiplexingStream"/>.
        /// </summary>
        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        public class Channel : IDisposableObservable, IDuplexPipe
        {
            /// <summary>
            /// This task source completes when the channel has been accepted, rejected, or the offer is canceled.
            /// </summary>
            private readonly TaskCompletionSource<AcceptanceParameters> acceptanceSource = new TaskCompletionSource<AcceptanceParameters>(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// The source for the <see cref="Completion"/> property.
            /// </summary>
            private readonly TaskCompletionSource<object?> completionSource = new TaskCompletionSource<object?>();

            /// <summary>
            /// The source for the <see cref="OptionsApplied"/> property. May be null if options were provided in ctor.
            /// </summary>
            private readonly TaskCompletionSource<object?>? optionsAppliedTaskSource;

            /// <summary>
            /// A value indicating whether this channel originated locally (as opposed to remotely).
            /// </summary>
            private readonly bool offeredLocally;

            /// <summary>
            /// Tracks the end of any copying from the mxstream to this channel.
            /// </summary>
            private readonly AsyncManualResetEvent mxStreamIOWriterCompleted = new AsyncManualResetEvent();

            /// <summary>
            /// Gets a signal which indicates when the <see cref="RemoteWindowRemaining"/> is non-zero.
            /// </summary>
            private readonly AsyncManualResetEvent remoteWindowNonEmpty = new AsyncManualResetEvent(initialState: true);

            /// <summary>
            /// The number of bytes transmitted from here but not yet acknowledged as processed from there,
            /// and thus occupying some portion of the full <see cref="AcceptanceParameters.RemoteWindowSize"/>.
            /// </summary>
            /// <remarks>
            /// All access to this field should be made within a lock on the <see cref="SyncObject"/> object.
            /// </remarks>
            private long remoteWindowFilled = 0;

            /// <summary>
            /// The number of bytes that may be transmitted before receiving acknowledgment that those bytes have been processed.
            /// </summary>
            /// <remarks>
            /// This field is set to the value of <see cref="OfferParameters.RemoteWindowSize"/> if we accepted the channel,
            /// or the value of <see cref="AcceptanceParameters.RemoteWindowSize"/> if we offered the channel.
            /// </remarks>
            private long remoteWindowSize = -1;

            /// <summary>
            /// Indicates whether the <see cref="Dispose"/> method has been called.
            /// </summary>
            private bool isDisposed;

            /// <summary>
            /// The <see cref="PipeReader"/> to use to get data to be transmitted over the <see cref="Streams.MultiplexingStream"/>.
            /// </summary>
            private PipeReader? mxStreamIOReader;

            /// <summary>
            /// A task that represents the completion of the <see cref="mxStreamIOReader"/>,
            /// signifying the point where we will stop relaying data from the channel to the <see cref="MultiplexingStream"/> for transmission to the remote party.
            /// </summary>
            private Task? mxStreamIOReaderCompleted;

            /// <summary>
            /// The <see cref="PipeWriter"/> the underlying <see cref="Streams.MultiplexingStream"/> should use.
            /// </summary>
            private PipeWriter? mxStreamIOWriter;

            /// <summary>
            /// The I/O to expose on this channel. Will be <c>null</c> if <see cref="ChannelOptions.ExistingPipe"/>
            /// was set to a non-null value when this channel was created.
            /// </summary>
            private IDuplexPipe? channelIO;

            /// <summary>
            /// A task that represents a transition from a <see cref="Pipe"/> to an owner-supplied <see cref="PipeWriter"/>
            /// for use by the underlying <see cref="MultiplexingStream"/> to publish bytes received over the channel.
            /// </summary>
            private Task<PipeWriter>? switchingToExistingPipe;

            /// <summary>
            /// Initializes a new instance of the <see cref="Channel"/> class.
            /// </summary>
            /// <param name="multiplexingStream">The owning <see cref="Streams.MultiplexingStream"/>.</param>
            /// <param name="offeredLocally">A value indicating whether this channel originated locally (as opposed to remotely).</param>
            /// <param name="id">The ID of the channel.</param>
            /// <param name="offerParameters">The parameters of the channel from the offering party.</param>
            /// <param name="channelOptions">The channel options. Should only be null if the channel is created in response to an offer that is not immediately accepted.</param>
            internal Channel(MultiplexingStream multiplexingStream, bool offeredLocally, int id, OfferParameters offerParameters, ChannelOptions? channelOptions = null)
            {
                Requires.NotNull(multiplexingStream, nameof(multiplexingStream));
                Requires.NotNull(offerParameters, nameof(offerParameters));

                this.MultiplexingStream = multiplexingStream;
                this.offeredLocally = offeredLocally;
                this.Id = id;
                this.OfferParams = offerParameters;

                if (!offeredLocally)
                {
                    this.remoteWindowSize = offerParameters.RemoteWindowSize;
                }

                if (channelOptions == null)
                {
                    this.optionsAppliedTaskSource = new TaskCompletionSource<object?>();
                }
                else
                {
                    this.ApplyChannelOptions(channelOptions);
                }
            }

            /// <summary>
            /// Gets the unique ID for this channel.
            /// </summary>
            /// <remarks>
            /// This value is usually shared for an anonymous channel so the remote party
            /// can accept it with <see cref="AcceptChannel(int, ChannelOptions)"/> or
            /// reject it with <see cref="RejectChannel(int)"/>.
            /// </remarks>
            public int Id { get; }

            /// <summary>
            /// Gets the mechanism used for tracing activity related to this channel.
            /// </summary>
            /// <value>A non-null value, once <see cref="ApplyChannelOptions(ChannelOptions)"/> has been called.</value>
            public TraceSource? TraceSource { get; private set; }

            /// <inheritdoc />
            public bool IsDisposed => this.isDisposed || this.Completion.IsCompleted;

            /// <summary>
            /// Gets the reader used to receive data over the channel.
            /// </summary>
            /// <exception cref="NotSupportedException">Thrown if the channel was created with a non-null value in <see cref="ChannelOptions.ExistingPipe"/>.</exception>
            public PipeReader Input => this.channelIO?.Input ?? throw new NotSupportedException(Strings.NotSupportedWhenExistingPipeSpecified);

            /// <summary>
            /// Gets the writer used to transmit data over the channel.
            /// </summary>
            /// <exception cref="NotSupportedException">Thrown if the channel was created with a non-null value in <see cref="ChannelOptions.ExistingPipe"/>.</exception>
            public PipeWriter Output => this.channelIO?.Output ?? throw new NotSupportedException(Strings.NotSupportedWhenExistingPipeSpecified);

            /// <summary>
            /// Gets a <see cref="Task"/> that completes when the channel is accepted, rejected, or canceled.
            /// </summary>
            /// <remarks>
            /// If the channel is accepted, this task transitions to <see cref="TaskStatus.RanToCompletion"/> state.
            /// If the channel offer is canceled, this task transitions to a <see cref="TaskStatus.Canceled"/> state.
            /// If the channel offer is rejected, this task transitions to a <see cref="TaskStatus.Canceled"/> state.
            /// </remarks>
            public Task Acceptance => this.acceptanceSource.Task;

            /// <summary>
            /// Gets a <see cref="Task"/> that completes when the channel is disposed,
            /// which occurs when <see cref="Dispose()"/> is invoked or when both sides
            /// have indicated they are done writing to the channel.
            /// </summary>
            public Task Completion => this.completionSource.Task;

            /// <summary>
            /// Gets the underlying <see cref="Streams.MultiplexingStream"/> instance.
            /// </summary>
            public MultiplexingStream MultiplexingStream { get; }

            internal OfferParameters OfferParams { get; }

            internal string Name => this.OfferParams.Name;

            internal bool IsAccepted => this.Acceptance.Status == TaskStatus.RanToCompletion;

            internal bool IsRejectedOrCanceled => this.Acceptance.Status == TaskStatus.Canceled;

            internal bool IsRemotelyTerminated { get; set; }

            /// <summary>
            /// Gets a <see cref="Task"/> that completes when options have been applied to this <see cref="Channel"/>.
            /// </summary>
            internal Task OptionsApplied => this.optionsAppliedTaskSource?.Task ?? Task.CompletedTask;

            /// <summary>
            /// Gets a value indicating whether this channel originated locally (as opposed to remotely).
            /// </summary>
            internal bool OfferedLocally => this.offeredLocally;

            private string DebuggerDisplay => $"{this.Id} {this.Name ?? "(anonymous)"}";

            /// <summary>
            /// Gets an object that can be locked to make critical changes to this instance's fields.
            /// </summary>
            /// <remarks>
            /// We reuse an object we already have to avoid having to create a new System.Object instance just to lock with.
            /// </remarks>
            private object SyncObject => this.acceptanceSource;

            /// <summary>
            /// Gets the number of bytes that may be transmitted over this channel given the
            /// remaining space in the <see cref="remoteWindowSize"/>.
            /// </summary>
            private long RemoteWindowRemaining
            {
                get
                {
                    lock (this.SyncObject)
                    {
                        Assumes.True(this.remoteWindowSize > 0);
                        return this.remoteWindowSize - this.remoteWindowFilled;
                    }
                }
            }

            /// <summary>
            /// Closes this channel and releases all resources associated with it.
            /// Pending reads and writes may be abandoned if the channel was created with an <see cref="ChannelOptions.ExistingPipe"/>.
            /// </summary>
            /// <remarks>
            /// Because this method may terminate the channel immediately and thus can cause previously queued content to not actually be received by the remote party,
            /// consider this method a "break glass" way of terminating a channel. The preferred method is that both sides "complete writing" and let the channel dispose itself.
            /// </remarks>
            public void Dispose()
            {
                if (!this.IsDisposed)
                {
                    // The code in this delegate needs to happen in several branches including possibly asynchronously.
                    // We carefully define it here with no closure so that the C# compiler generates a static field for the delegate
                    // thus avoiding any extra allocations from reusing code in this way.
                    Action<object?, object> finalDisposalAction = (exOrAntecedent, state) =>
                    {
                        var self = (Channel)state;
                        self.completionSource.TrySetResult(null);
                        self.MultiplexingStream.OnChannelDisposed(self);
                    };

                    this.acceptanceSource.TrySetCanceled();
                    this.optionsAppliedTaskSource?.TrySetCanceled();

                    PipeWriter? mxStreamIOWriter;
                    lock (this.SyncObject)
                    {
                        this.isDisposed = true;
                        mxStreamIOWriter = this.mxStreamIOWriter;
                    }

                    // Complete writing so that the mxstream cannot write to this channel any more.
                    // We must also cancel a pending flush since no one is guaranteed to be reading this any more
                    // and we don't want to deadlock on a full buffer in a disposed channel's pipe.
                    mxStreamIOWriter?.Complete();
                    mxStreamIOWriter?.CancelPendingFlush();
                    this.mxStreamIOWriterCompleted.Set();

                    if (this.channelIO != null)
                    {
                        // We're using our own Pipe to relay user messages, so we can shutdown writing and allow for our reader to propagate what was already written
                        // before actually shutting down.
                        this.channelIO.Output.Complete();
                    }
                    else
                    {
                        // We don't own the user's PipeWriter to complete it (so they can't write anything more to this channel).
                        // We can't know whether there is or will be more bytes written to the user's PipeWriter,
                        // but we need to terminate our reader for their writer as part of reclaiming resources.
                        // We want to complete reading immediately and cancel any pending read.
                        this.mxStreamIOReader?.Complete();
                        this.mxStreamIOReader?.CancelPendingRead();
                    }

                    // Unblock the reader that might be waiting on this.
                    this.remoteWindowNonEmpty.Set();

                    // As a minor perf optimization, avoid allocating a continuation task if the antecedent is already completed.
                    if (this.mxStreamIOReaderCompleted?.IsCompleted ?? true)
                    {
                        finalDisposalAction(null, this);
                    }
                    else
                    {
                        this.mxStreamIOReaderCompleted!.ContinueWith(finalDisposalAction, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Forget();
                    }
                }
            }

            /// <summary>
            /// Gets the pipe writer to use when a message is received for this channel, so that the channel owner will notice and read it.
            /// </summary>
            /// <returns>A <see cref="PipeWriter"/>.</returns>
            internal async ValueTask<PipeWriter> GetReceivedMessagePipeWriterAsync()
            {
                lock (this.SyncObject)
                {
                    Verify.NotDisposed(this);
                    if (this.switchingToExistingPipe == null)
                    {
                        PipeWriter? result = this.mxStreamIOWriter;
                        if (result == null)
                        {
                            this.InitializeOwnPipes(PipeOptions.Default, PipeOptions.Default);
                            result = this.mxStreamIOWriter!;
                        }

                        return result;
                    }
                }

                // Our (non-current) writer must not be writing to the last result we may have given them,
                // since they're asking for access right now. So whatever they may have written on the last result
                // is the last they get to write on that result, so Complete that result.
                this.mxStreamIOWriter!.Complete();

                // Now wait for whatever they may have written previously to propagate to the ChannelOptions.ExistingPipe.Output writer,
                // and then redirect all writing to that writer.
                PipeWriter newWriter = await this.switchingToExistingPipe.ConfigureAwait(false);
                lock (this.SyncObject)
                {
                    Verify.NotDisposed(this);
                    this.mxStreamIOWriter = newWriter;

                    // Skip all this next time.
                    this.switchingToExistingPipe = null;
                }

                return this.mxStreamIOWriter;
            }

            /// <summary>
            /// Called by the <see cref="MultiplexingStream"/> when when it will not be writing any more data to the channel.
            /// </summary>
            internal void OnContentWritingCompleted()
            {
                this.DisposeSelfOnFailure(Task.Run(async delegate
                {
                    if (!this.IsDisposed)
                    {
                        try
                        {
                            var writer = await this.GetReceivedMessagePipeWriterAsync().ConfigureAwait(false);
                            await writer.CompleteAsync().ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException)
                        {
                            if (this.mxStreamIOWriter != null)
                            {
                                await this.mxStreamIOWriter.CompleteAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        if (this.mxStreamIOWriter != null)
                        {
                            await this.mxStreamIOWriter.CompleteAsync().ConfigureAwait(false);
                        }
                    }

                    this.mxStreamIOWriterCompleted.Set();
                }));
            }

            /// <summary>
            /// Accepts an offer made by the remote party.
            /// </summary>
            /// <param name="channelOptions">The options to apply to the channel.</param>
            /// <returns>A value indicating whether the offer was accepted. It may fail if the channel was already closed or the offer rescinded.</returns>
            internal bool TryAcceptOffer(ChannelOptions channelOptions)
            {
                var acceptanceParameters = new AcceptanceParameters((channelOptions.InputPipeOptions?.PauseWriterThreshold ?? this.MultiplexingStream.defaultChannelPauseWriterThreshold) - 1);
                if (this.acceptanceSource.TrySetResult(acceptanceParameters))
                {
                    var payload = new Sequence<byte>(); // don't dispose, since the buffer needs to live longer than this synchronous method.
                    acceptanceParameters.Serialize(payload);
                    this.MultiplexingStream.SendFrame(
                        new FrameHeader
                        {
                            Code = ControlCode.OfferAccepted,
                            ChannelId = this.Id,
                            FramePayloadLength = 4,
                        },
                        payload,
                        CancellationToken.None);
                    try
                    {
                        this.ApplyChannelOptions(channelOptions);
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        // A (harmless) race condition was hit.
                        // Swallow it and return false below.
                    }
                }

                return false;
            }

            /// <summary>
            /// Occurs when the remote party has accepted our offer of this channel.
            /// </summary>
            /// <param name="acceptanceParameters">The channel parameters provided by the accepting party.</param>
            /// <returns>A value indicating whether the acceptance went through; <c>false</c> if the channel is already accepted, rejected or offer rescinded.</returns>
            internal bool OnAccepted(AcceptanceParameters acceptanceParameters)
            {
                lock (this.SyncObject)
                {
                    if (this.acceptanceSource.TrySetResult(acceptanceParameters))
                    {
                        this.remoteWindowSize = acceptanceParameters.RemoteWindowSize;
                        return true;
                    }

                    return false;
                }
            }

            /// <summary>
            /// Invoked when the remote party acknowledges bytes we previously transmitted as processed,
            /// thereby allowing us to consider that data removed from the remote party's "window"
            /// and thus enables us to send more data to them.
            /// </summary>
            /// <param name="bytesProcessed">The number of bytes processed by the remote party.</param>
            internal void OnContentProcessed(long bytesProcessed)
            {
                Requires.Range(bytesProcessed >= 0, nameof(bytesProcessed), "A non-negative number is required.");
                lock (this.SyncObject)
                {
                    Assumes.True(bytesProcessed <= this.remoteWindowFilled);
                    this.remoteWindowFilled -= bytesProcessed;
                    if (this.remoteWindowFilled < this.remoteWindowSize)
                    {
                        this.remoteWindowNonEmpty.Set();
                    }
                }
            }

            /// <summary>
            /// Apply channel options to this channel, including setting up or migrating to an user-supplied pipe writer/reader pair.
            /// </summary>
            /// <param name="channelOptions">The channel options to apply.</param>
            private void ApplyChannelOptions(ChannelOptions channelOptions)
            {
                Requires.NotNull(channelOptions, nameof(channelOptions));
                Assumes.Null(this.TraceSource); // We've already applied options

                try
                {
                    this.TraceSource = channelOptions.TraceSource
                        ?? this.MultiplexingStream.DefaultChannelTraceSourceFactory?.Invoke(this.Id, this.Name)
                        ?? new TraceSource($"{nameof(Streams.MultiplexingStream)}.{nameof(Channel)} {this.Id} ({this.Name})", SourceLevels.Critical);

                    lock (this.SyncObject)
                    {
                        Verify.NotDisposed(this);
                        if (channelOptions.ExistingPipe != null)
                        {
                            if (this.mxStreamIOWriter != null)
                            {
                                // A Pipe was already created (because data has been coming in for this channel even before it was accepted).
                                // To be most efficient, we need to:
                                // 1. Start forwarding all bytes written with this.mxStreamIOWriter to channelOptions.ExistingPipe.Output
                                // 2. Arrange for the *next* call to GetReceivedMessagePipeWriterAsync to:
                                //      call this.mxStreamIOWriter.Complete()
                                //      wait for our forwarding code to finish (without propagating copmletion to channel.ExistingPipe.Output)
                                //      return channel.ExistingPipe.Output
                                //    From then on, GetReceivedMessagePipeWriterAsync should simply return channel.ExistingPipe.Output
                                // Since this channel hasn't yet been exposed to the local owner, we can just replace the PipeWriter they use to transmit.

                                // Take ownership of reading bytes that the MultiplexingStream may have already written to this channel.
                                var mxStreamIncomingBytesReader = this.channelIO!.Input;
                                this.channelIO = null;

                                // Forward any bytes written by the MultiplexingStream to the ExistingPipe.Output writer,
                                // and make that ExistingPipe.Output writer available only after the old Pipe-based writer has completed.
                                // First, capture the ExistingPipe as a local since ChannelOptions is a mutable type, and we're going to need
                                // its current value later on.
                                var existingPipe = channelOptions.ExistingPipe;
                                this.switchingToExistingPipe = Task.Run(async delegate
                                {
                                    // Await propagation of all bytes. Don't complete the ExistingPipe.Output when we're done because we still want to use it.
                                    await mxStreamIncomingBytesReader.LinkToAsync(existingPipe.Output, propagateSuccessfulCompletion: false).ConfigureAwait(false);
                                    return existingPipe.Output;
                                });
                            }
                            else
                            {
                                // We haven't created a Pipe yet, so we can simply direct all writing to the ExistingPipe.Output immediately.
                                this.mxStreamIOWriter = channelOptions.ExistingPipe.Output;
                            }

                            this.mxStreamIOReader = channelOptions.ExistingPipe.Input;
                        }
                        else if (channelOptions.InputPipeOptions != null && this.mxStreamIOWriter != null)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Data received on channel {0} before it was accepted. Migrating data from temporary buffer to accepted channel's new pipe.", this.Id);

                            // Similar strategy to the situation above with ExistingPipe.
                            // Take ownership of reading bytes that the MultiplexingStream may have already written to this channel.
                            var mxStreamIncomingBytesReader = this.channelIO!.Input;

                            var writerRelay = new Pipe();
                            var readerRelay = new Pipe(channelOptions.InputPipeOptions);
                            this.mxStreamIOReader = writerRelay.Reader;
                            this.channelIO = new DuplexPipe(readerRelay.Reader, writerRelay.Writer);

                            this.switchingToExistingPipe = Task.Run(async delegate
                            {
                                // Await propagation of all bytes. Don't complete the readerRelay.Writer when we're done because we still want to use it.
                                await mxStreamIncomingBytesReader.LinkToAsync(readerRelay.Writer, propagateSuccessfulCompletion: false).ConfigureAwait(false);
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Data from temporary buffer to accepted channel {0}'s new pipe is completed.", this.Id);

                                return readerRelay.Writer;
                            });
                        }
                        else
                        {
                            this.InitializeOwnPipes(channelOptions.InputPipeOptions ?? PipeOptions.Default, channelOptions.OutputPipeOptions ?? PipeOptions.Default);
                        }
                    }

                    this.mxStreamIOReaderCompleted = this.ProcessOutboundTransmissionsAsync();
                    this.DisposeSelfOnFailure(this.mxStreamIOReaderCompleted);
                    this.DisposeSelfOnFailure(this.AutoCloseOnPipesClosureAsync());
                }
                catch (Exception ex)
                {
                    this.optionsAppliedTaskSource?.TrySetException(ex);
                    throw;
                }
                finally
                {
                    this.optionsAppliedTaskSource?.TrySetResult(null);
                }
            }

            /// <summary>
            /// Set up our own (buffering) Pipes if they have not been set up yet.
            /// </summary>
            /// <param name="inputPipeOptions">The options for the reading relay <see cref="Pipe"/>. Must not be null.</param>
            /// <param name="outputPipeOptions">The options for the writing relay <see cref="Pipe"/>. Must not be null.</param>
            private void InitializeOwnPipes(PipeOptions inputPipeOptions, PipeOptions outputPipeOptions)
            {
                lock (this.SyncObject)
                {
                    Verify.NotDisposed(this);
                    if (this.mxStreamIOReader == null)
                    {
                        var writerRelay = new Pipe(outputPipeOptions);
                        var readerRelay = new Pipe(inputPipeOptions);
                        this.mxStreamIOReader = writerRelay.Reader;
                        this.mxStreamIOWriter = readerRelay.Writer;
                        this.channelIO = new DuplexPipe(new WindowPipeReader(this, readerRelay.Reader), writerRelay.Writer);
                    }
                }
            }

            /// <summary>
            /// Relays data that the local channel owner wants to send to the remote party.
            /// </summary>
            private async Task ProcessOutboundTransmissionsAsync()
            {
                try
                {
                    // Don't transmit data on the channel until the remote party has accepted it.
                    // This is not just a courtesy: it ensure we don't transmit data from the offering party before the offer frame itself.
                    // Likewise: it may help prevent transmitting data from the accepting party before the acceptance frame itself.
                    await this.Acceptance.ConfigureAwait(false);

                    while (!this.Completion.IsCompleted)
                    {
                        if (!this.remoteWindowNonEmpty.IsSet && this.TraceSource!.Switch.ShouldTrace(TraceEventType.Verbose))
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Remote window is full. Waiting for remote party to process data before sending more.");
                        }

                        await this.remoteWindowNonEmpty.WaitAsync().ConfigureAwait(false);
                        if (this.IsRemotelyTerminated)
                        {
                            if (this.TraceSource!.Switch.ShouldTrace(TraceEventType.Verbose))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Transmission on channel {0} \"{1}\" terminated the remote party terminated the channel.", this.Id, this.Name);
                            }

                            break;
                        }

                        ReadResult result;
                        try
                        {
                            result = await this.mxStreamIOReader!.ReadAsync().ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Someone completed the reader. The channel was probably disposed.
                            if (this.TraceSource!.Switch.ShouldTrace(TraceEventType.Verbose))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Transmission terminated because the reader threw: {0}", ex);
                            }

                            break;
                        }

                        if (result.IsCanceled)
                        {
                            // We've been asked to cancel. Presumably the channel has been disposed.
                            if (this.TraceSource!.Switch.ShouldTrace(TraceEventType.Verbose))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Transmission terminated because the read was canceled.");
                            }

                            break;
                        }

                        // We'll send whatever we've got, up to the maximum size of the frame or available window size.
                        // Anything in excess of that we'll pick up next time the loop runs.
                        var bufferToRelay = result.Buffer.Slice(0, Math.Min(this.RemoteWindowRemaining, Math.Min(result.Buffer.Length, FramePayloadMaxLength)));
                        this.OnTransmittingBytes(bufferToRelay.Length);
                        bool isCompleted = result.IsCompleted && result.Buffer.Length == bufferToRelay.Length;
                        if (this.TraceSource!.Switch.ShouldTrace(TraceEventType.Verbose))
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "{0} of {1} bytes will be transmitted.", bufferToRelay.Length, result.Buffer.Length);
                        }

                        if (bufferToRelay.Length > 0)
                        {
                            FrameHeader header = new FrameHeader
                            {
                                Code = ControlCode.Content,
                                ChannelId = this.Id,
                                FramePayloadLength = (int)bufferToRelay.Length,
                            };

                            await this.MultiplexingStream.SendFrameAsync(header, bufferToRelay, CancellationToken.None).ConfigureAwait(false);
                        }

                        try
                        {
                            // Let the pipe know exactly how much we read, which might be less than we were given.
                            this.mxStreamIOReader.AdvanceTo(bufferToRelay.End);

                            // We mustn't accidentally access the memory that may have been recycled now that we called AdvanceTo.
                            bufferToRelay = default;
                            result.ScrubAfterAdvanceTo();
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Someone completed the reader. The channel was probably disposed.
                            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Transmission terminated because the reader threw: {0}", ex);
                            }

                            break;
                        }

                        if (isCompleted)
                        {
                            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "Transmission terminated because the writer completed.");
                            }

                            break;
                        }
                    }

                    await this.mxStreamIOReader!.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await this.mxStreamIOReader!.CompleteAsync(ex).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    this.MultiplexingStream.OnChannelWritingCompleted(this);
                }
            }

            /// <summary>
            /// Invoked when we transmit data to the remote party
            /// so we can track how much data we're sending them so we don't overrun their receiving buffer.
            /// </summary>
            /// <param name="transmittedBytes">The number of bytes being transmitted.</param>
            private void OnTransmittingBytes(long transmittedBytes)
            {
                Requires.Range(transmittedBytes >= 0, nameof(transmittedBytes), "A non-negative number is required.");
                lock (this.SyncObject)
                {
                    Requires.Range(this.remoteWindowFilled + transmittedBytes <= this.remoteWindowSize, nameof(transmittedBytes), "The value exceeds the space remaining in the window size.");
                    this.remoteWindowFilled += transmittedBytes;
                    if (this.remoteWindowFilled == this.remoteWindowSize)
                    {
                        this.remoteWindowNonEmpty.Reset();
                    }
                }
            }

            private void LocalContentProcessed(long bytesProcessed)
            {
                Memory<byte> memory = new byte[4];
                Utilities.Write(memory.Span, (int)bytesProcessed);
                this.MultiplexingStream.SendFrame(
                    new FrameHeader
                    {
                        Code = ControlCode.ContentProcessed,
                        ChannelId = this.Id,
                        FramePayloadLength = 4,
                    },
                    new ReadOnlySequence<byte>(memory),
                    CancellationToken.None);
            }

            private async Task AutoCloseOnPipesClosureAsync()
            {
                await Task.WhenAll(this.mxStreamIOWriterCompleted.WaitAsync(), this.mxStreamIOReaderCompleted).ConfigureAwait(false);

                if (this.TraceSource!.Switch.ShouldTrace(TraceEventType.Information))
                {
                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEventId.ChannelAutoClosing, "Channel {0} \"{1}\" self-closing because both reader and writer are complete.", this.Id, this.Name);
                }

                this.Dispose();
            }

            private void Fault(Exception exception)
            {
                if (this.TraceSource?.Switch.ShouldTrace(TraceEventType.Critical) ?? false)
                {
                    this.TraceSource!.TraceEvent(TraceEventType.Critical, (int)TraceEventId.FatalError, "Channel Closing self due to exception: {0}", exception);
                }

                this.mxStreamIOReader?.Complete(exception);
                this.Dispose();
            }

            private void DisposeSelfOnFailure(Task task)
            {
                Requires.NotNull(task, nameof(task));

                if (task.IsCompleted)
                {
                    if (task.IsFaulted)
                    {
                        this.Fault(task.Exception.InnerException ?? task.Exception);
                    }
                }
                else
                {
                    task.ContinueWith(
                        (t, s) => ((Channel)s).Fault(t.Exception.InnerException ?? t.Exception),
                        this,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default).Forget();
                }
            }

            [DataContract]
            internal class OfferParameters
            {
                /// <summary>
                /// Initializes a new instance of the <see cref="OfferParameters"/> class.
                /// </summary>
                /// <param name="name">The name of the channel.</param>
                /// <param name="remoteWindowSize">
                /// The maximum number of bytes that may be transmitted and not yet acknowledged as processed by the remote party.
                /// When based on <see cref="PipeOptions.PauseWriterThreshold"/>, this value should be -1 of that value in order
                /// to avoid the actual pause that would be fatal to the read loop of the multiplexing stream.
                /// </param>
                internal OfferParameters(string name, long remoteWindowSize)
                {
                    this.Name = name ?? throw new ArgumentNullException(nameof(name));
                    this.RemoteWindowSize = remoteWindowSize;
                }

                /// <summary>
                /// Gets the name of the channel.
                /// </summary>
                [DataMember]
                internal string Name { get; }

                /// <summary>
                /// Gets the maximum number of bytes that may be transmitted and not yet acknowledged as processed by the remote party.
                /// </summary>
                [DataMember]
                internal long RemoteWindowSize { get; }

                internal static unsafe OfferParameters Deserialize(ReadOnlyMemory<byte> buffer)
                {
                    ReadOnlySpan<byte> nameSlice = buffer.Span.Slice(0, buffer.Length - 4);
                    ReadOnlySpan<byte> remoteWindowSizeSpan = buffer.Span.Slice(buffer.Length - 4);
                    fixed (byte* pName = nameSlice)
                    {
                        return new OfferParameters(
                            pName != null ? ControlFrameEncoding.GetString(pName, nameSlice.Length) : string.Empty,
                            Utilities.ReadInt(remoteWindowSizeSpan));
                    }
                }

                internal unsafe void Serialize(IBufferWriter<byte> writer)
                {
                    var buffer = writer.GetSpan(ControlFrameEncoding.GetMaxByteCount(this.Name.Length));
                    fixed (byte* pBuffer = buffer)
                    fixed (char* pName = this.Name)
                    {
                        int byteLength = ControlFrameEncoding.GetBytes(pName, this.Name.Length, pBuffer, buffer.Length);
                        writer.Advance(byteLength);
                    }

                    buffer = writer.GetSpan(4);
                    Utilities.Write(buffer, checked((int)this.RemoteWindowSize));
                    writer.Advance(4);
                }
            }

            [DataContract]
            internal class AcceptanceParameters
            {
                /// <summary>
                /// Initializes a new instance of the <see cref="AcceptanceParameters"/> class.
                /// </summary>
                /// <param name="remoteWindowSize">
                /// The maximum number of bytes that may be transmitted and not yet acknowledged as processed by the remote party.
                /// When based on <see cref="PipeOptions.PauseWriterThreshold"/>, this value should be -1 of that value in order
                /// to avoid the actual pause that would be fatal to the read loop of the multiplexing stream.
                /// </param>
                internal AcceptanceParameters(long remoteWindowSize) => this.RemoteWindowSize = remoteWindowSize;

                /// <summary>
                /// Gets the maximum number of bytes that may be transmitted and not yet acknowledged as processed by the remote party.
                /// </summary>
                [DataMember]
                internal long RemoteWindowSize { get; }

                internal static AcceptanceParameters Deserialize(ReadOnlyMemory<byte> buffer)
                {
                    return new AcceptanceParameters(Utilities.ReadInt(buffer.Span));
                }

                internal void Serialize(IBufferWriter<byte> writer)
                {
                    var buffer = writer.GetSpan(4);
                    Utilities.Write(buffer, checked((int)this.RemoteWindowSize));
                    writer.Advance(4);
                }
            }

            private class WindowPipeReader : PipeReader
            {
                private readonly Channel owner;
                private readonly PipeReader inner;
                private ReadResult lastReadResult;
                private long bytesProcessed;

                internal WindowPipeReader(Channel owner, PipeReader inner)
                {
                    this.owner = owner;
                    this.inner = inner;
                }

                public override void AdvanceTo(SequencePosition consumed)
                {
                    this.Consumed(consumed);
                    this.inner.AdvanceTo(consumed);
                }

                public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
                {
                    // The reader demands more data if examined == End and consumed < End.
                    bool moreDataRequired = !examined.Equals(consumed) && examined.Equals(this.lastReadResult.Buffer.End);
                    this.Consumed(consumed, moreDataRequired);
                    this.inner.AdvanceTo(consumed, examined);
                }

                public override void CancelPendingRead() => this.inner.CancelPendingRead();

                public override void Complete(Exception? exception = null) => this.inner.Complete(exception);

                public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
                {
                    return this.lastReadResult = await this.inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                }

                public override bool TryRead(out ReadResult readResult)
                {
                    bool result = this.inner.TryRead(out readResult);
                    this.lastReadResult = readResult;
                    return result;
                }

                public override Stream AsStream(bool leaveOpen = false) => this.inner.AsStream();

                public override ValueTask CompleteAsync(Exception? exception = null) => this.inner.CompleteAsync(exception);

                public override Task CopyToAsync(PipeWriter destination, CancellationToken cancellationToken = default) => this.inner.CopyToAsync(destination, cancellationToken);

                public override Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default) => this.inner.CopyToAsync(destination, cancellationToken);

                [Obsolete]
                public override void OnWriterCompleted(Action<Exception, object> callback, object state) => this.inner.OnWriterCompleted(callback, state);

                private void Consumed(SequencePosition consumed, bool moreDataRequired = false)
                {
                    long bytesJustProcessed =
                        this.lastReadResult.Buffer.End.Equals(consumed) ? this.lastReadResult.Buffer.Length :
                        this.lastReadResult.Buffer.Slice(this.lastReadResult.Buffer.Start, consumed).Length;
                    this.bytesProcessed += bytesJustProcessed;

                    // Only send the 'more bytes please' message if we've consumed at least a max frame's worth of data
                    // or if our reader indicates that more data is required before it will examine any more.
                    if (this.bytesProcessed >= FramePayloadMaxLength || moreDataRequired)
                    {
                        // TODO: review moreDataRequired scenario, particularly considering latency where the sender may have already
                        // transmitted additional data. We wouldn't want to advise them to overstep the window to send more data if
                        // we already have or will soon get more data that will appease our reader.
                        this.owner.LocalContentProcessed(this.bytesProcessed);
                        this.bytesProcessed = 0;
                    }
                }
            }
        }
    }
}
