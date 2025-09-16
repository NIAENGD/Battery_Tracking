using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BatteryTracker.Collector.Sessions;

internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> Merge<T>(this IEnumerable<IAsyncEnumerable<T>> sources, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<T>();
        var tasks = sources.Select(async source =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }).ToArray();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }
}
