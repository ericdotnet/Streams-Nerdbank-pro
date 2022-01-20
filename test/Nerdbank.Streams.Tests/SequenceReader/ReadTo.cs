// <auto-generated /> // copied from https://github.com/dotnet/runtime/blob/cf5b231fcbea483df3b081939b422adfb6fd486a/src/libraries/System.Memory/tests
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Buffers;
using Xunit;

namespace Nerdbank.Streams.Tests.SequenceReader
{
    public class ReadTo
    {
        [Theory,
            InlineData(false, false),
            InlineData(false, true),
            InlineData(true, false),
            InlineData(true, true)]
        public void TryReadTo_Span(bool advancePastDelimiter, bool useEscapeOverload)
        {
            ReadOnlySequence<byte> bytes = SequenceFactory.Create(new byte[][] {
                new byte[] { 0 },
                new byte[] { 1, 2 },
                new byte[] { },
                new byte[] { 3, 4, 5, 6 }
            });

            SequenceReader<byte> reader = new SequenceReader<byte>(bytes);

            // Read to 0-5
            for (byte i = 0; i < bytes.Length - 1; i++)
            {
                SequenceReader<byte> copy = reader;

                // Can read to the first integer (0-5)
                Assert.True(
                    useEscapeOverload
                        ? copy.TryReadTo(out ReadOnlySpan<byte> span, i, 255, advancePastDelimiter)
                        : copy.TryReadTo(out span, i, advancePastDelimiter));

                // Should never have a null Position object
                Assert.NotNull(copy.Position.GetObject());

                // Should be able to then read to 6
                Assert.True(
                    useEscapeOverload
                        ? copy.TryReadTo(out span, 6, 255, advancePastDelimiter)
                        : copy.TryReadTo(out span, 6, advancePastDelimiter));

                Assert.NotNull(copy.Position.GetObject());

                // If we didn't advance, we should still be able to read to 6
                Assert.Equal(!advancePastDelimiter,
                    useEscapeOverload
                        ? copy.TryReadTo(out span, 6, 255, advancePastDelimiter)
                        : copy.TryReadTo(out span, 6, advancePastDelimiter));
            }
        }

        [Theory,
            InlineData(false, false),
            InlineData(false, true),
            InlineData(true, false),
            InlineData(true, true)]
        public void TryReadTo_Sequence(bool advancePastDelimiter, bool useEscapeOverload)
        {
            ReadOnlySequence<byte> bytes = SequenceFactory.Create(new byte[][] {
                new byte[] { 0 },
                new byte[] { 1, 2 },
                new byte[] { },
                new byte[] { 3, 4, 5, 6 }
            });

            SequenceReader<byte> reader = new SequenceReader<byte>(bytes);

            // Read to 0-5
            for (byte i = 0; i < bytes.Length - 1; i++)
            {
                SequenceReader<byte> copy = reader;

                // Can read to the first integer (0-5)
                Assert.True(
                    useEscapeOverload
                        ? copy.TryReadTo(out ReadOnlySequence<byte> sequence, i, 255, advancePastDelimiter)
                        : copy.TryReadTo(out sequence, i, advancePastDelimiter));

                // Should never have a null Position object
                Assert.NotNull(copy.Position.GetObject());
                ReadOnlySequence<byte>.Enumerator enumerator = sequence.GetEnumerator();
                while (enumerator.MoveNext())
                    ;

                // Should be able to read to final 6
                Assert.True(
                    useEscapeOverload
                        ? copy.TryReadTo(out sequence, 6, 255, advancePastDelimiter)
                        : copy.TryReadTo(out sequence, 6, advancePastDelimiter));

                Assert.NotNull(copy.Position.GetObject());
                enumerator = sequence.GetEnumerator();
                while (enumerator.MoveNext())
                    ;

                // If we didn't advance, we should still be able to read to 6
                Assert.Equal(!advancePastDelimiter,
                    useEscapeOverload
                        ? copy.TryReadTo(out sequence, 6, 255, advancePastDelimiter)
                        : copy.TryReadTo(out sequence, 6, advancePastDelimiter));
            }
        }

        [Fact]
        public void TryReadExact_Sequence()
        {
            ReadOnlySequence<int> data = SequenceFactory.Create(new int[][] {
                new int[] { 0 },
                new int[] { 1, 2 },
                new int[] { },
                new int[] { 3, 4 }
            });

            var sequenceReader = new SequenceReader<int>(data);

            Assert.True(sequenceReader.TryReadExact(0, out ReadOnlySequence<int> sequence));
            Assert.Equal(0, sequence.Length);

            for (int i = 0; i < 2; i++)
            {
                Assert.True(sequenceReader.TryReadExact(2, out sequence));
                Assert.Equal(Enumerable.Range(i * 2, 2), sequence.ToArray());
            }

            // There is only 1 item in sequence reader
            Assert.False(sequenceReader.TryReadExact(2, out _));

            // The last 1 item was not advanced so still can be fetched
            Assert.True(sequenceReader.TryReadExact(1, out sequence));
            Assert.Equal(1, sequence.Length);
            Assert.Equal(4, sequence.First.Span[0]);

            Assert.True(sequenceReader.End);
        }

        [Theory,
            InlineData(false),
            InlineData(true)]
        public void TryReadTo_NotFound_Span(bool advancePastDelimiter)
        {
            ReadOnlySequence<byte> bytes = SequenceFactory.Create(new byte[][] {
                new byte[] { 1 },
                new byte[] { 2, 3, 255 }
            });

            SequenceReader<byte> reader = new SequenceReader<byte>(bytes);
            reader.Advance(4);
            Assert.False(reader.TryReadTo(out ReadOnlySpan<byte> span, 255, 0, advancePastDelimiter));
        }

        [Theory,
            InlineData(false),
            InlineData(true)]
        public void TryReadTo_NotFound_Sequence(bool advancePastDelimiter)
        {
            ReadOnlySequence<byte> bytes = SequenceFactory.Create(new byte[][] {
                new byte[] { 1 },
                new byte[] { 2, 3, 255 }
            });

            SequenceReader<byte> reader = new SequenceReader<byte>(bytes);
            reader.Advance(4);
            Assert.False(reader.TryReadTo(out ReadOnlySequence<byte> span, 255, 0, advancePastDelimiter));
        }
    }
}
