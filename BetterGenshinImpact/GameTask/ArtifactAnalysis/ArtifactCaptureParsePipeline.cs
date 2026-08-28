using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactCaptureParsePipeline
{
    internal static async Task<IReadOnlyList<TResult>> RunAsync<TFrame, TResult>(
        IAsyncEnumerable<TFrame> frames,
        Func<TFrame, CancellationToken, Task<TResult>> parse,
        int capacity,
        CancellationToken cancellationToken)
        where TFrame : IDisposable
    {
        var channel = Channel.CreateBounded<TFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var consumer = Task.Run(async () =>
        {
            var results = new List<TResult>();
            try
            {
                await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    using (frame)
                    {
                        results.Add(await parse(frame, cancellationToken));
                    }
                }
            }
            finally
            {
                while (channel.Reader.TryRead(out var remaining)) remaining.Dispose();
            }
            return (IReadOnlyList<TResult>)results;
        });

        try
        {
            await foreach (var frame in frames.WithCancellation(cancellationToken))
            {
                var ownershipTransferred = false;
                Task? write = null;
                try
                {
                    write = channel.Writer.WriteAsync(frame, cancellationToken).AsTask();
                    if (await Task.WhenAny(write, consumer) == consumer)
                    {
                        await consumer;
                    }
                    await write;
                    ownershipTransferred = true;
                }
                catch
                {
                    channel.Writer.TryComplete();
                    if (write is not null)
                    {
                        try
                        {
                            await write;
                            ownershipTransferred = true;
                        }
                        catch
                        {
                            // Producer still owns a frame rejected by the channel.
                        }
                    }
                    throw;
                }
                finally
                {
                    if (!ownershipTransferred) frame.Dispose();
                }
            }

            channel.Writer.TryComplete();
            return await consumer;
        }
        catch (Exception exception)
        {
            channel.Writer.TryComplete(exception);
            try
            {
                await consumer;
            }
            catch
            {
                // Preserve the producer or consumer exception that ended the pipeline.
            }
            while (channel.Reader.TryRead(out var remaining)) remaining.Dispose();
            throw;
        }
    }
}
