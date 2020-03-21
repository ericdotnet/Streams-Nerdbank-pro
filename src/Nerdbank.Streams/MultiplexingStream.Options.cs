﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Diagnostics;
    using Microsoft;

    /// <content>
    /// Contains the <see cref="Options"/> nested type.
    /// </content>
    public partial class MultiplexingStream
    {
        /// <summary>
        /// Describes the options that a <see cref="MultiplexingStream"/> may be created with.
        /// </summary>
        public class Options
        {
            /// <summary>
            /// Backing field for the <see cref="TraceSource"/> property.
            /// </summary>
            private TraceSource traceSource = new TraceSource(nameof(MultiplexingStream), SourceLevels.Critical);

            /// <summary>
            /// Backing field for the <see cref="DefaultChannelReceivingWindowSize"/> property.
            /// </summary>
            private long defaultChannelReceivingWindowSize = RecommendedDefaultChannelReceivingWindowSize;

            /////// <summary>
            /////// Gets or sets the maximum number of channel offers from the remote party that are allowed before the
            /////// connection is terminated for abuse.
            /////// </summary>
            /////// <value>The default value is 100.</value>
            ////public int MaximumAllowedChannelOffers { get; set; } = 100;

            /// <summary>
            /// Gets or sets the logger used by this instance.
            /// </summary>
            /// <value>Never null.</value>
            public TraceSource TraceSource
            {
                get => this.traceSource;
                set
                {
                    Requires.NotNull(value, nameof(value));
                    this.traceSource = value;
                }
            }

            /// <summary>
            /// Gets or sets the number of received bytes that may be buffered locally per channel (transmitted from the remote party but not yet processed).
            /// </summary>
            /// <value>
            /// Must be a positive value.
            /// </value>
            /// <exception cref="ArgumentOutOfRangeException">Thrown if set to a non-positive value.</exception>
            public long DefaultChannelReceivingWindowSize
            {
                get => this.defaultChannelReceivingWindowSize;
                set
                {
                    Requires.Range(value > 0, nameof(value));
                    this.defaultChannelReceivingWindowSize = value;
                }
            }

            /// <summary>
            /// Gets or sets the protocol version to be used.
            /// </summary>
            /// <value>The default is 1.</value>
            /// <remarks>
            /// 1 is the original and default version.
            /// 2 is a protocol breaking change and adds backpressure support.
            /// </remarks>
            public int MajorProtocolVersion { get; set; } = 1;

            /// <summary>
            /// Gets or sets a factory for <see cref="TraceSource"/> instances to attach to a newly opened <see cref="Channel"/>
            /// when its <see cref="ChannelOptions.TraceSource"/> is <c>null</c>.
            /// </summary>
            /// <remarks>
            /// The delegate receives a channel ID and name, and may return a <see cref="TraceSource"/> or <c>null</c>.
            /// </remarks>
            public Func<int, string, TraceSource?>? DefaultChannelTraceSourceFactory { get; set; }
        }
    }
}
