using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlareSolverrSharp.Utilities
{
    public class SemaphoreLocker
    {
        private readonly SemaphoreSlim _semaphore;

        public SemaphoreLocker(int concurrency = 2)
        {
            _semaphore = new SemaphoreSlim(concurrency, concurrency);
        }

        public async Task LockAsync<T>(Func<T> worker, CancellationToken cancellationToken = default,
            TimeSpan? queueTimeout = null)
            where T : Task
        {
            if (!await _semaphore.WaitAsync(queueTimeout ?? TimeSpan.FromMinutes(5), cancellationToken))
                throw new TimeoutException("Timed out waiting for a FlareSolverr browser slot. Reduce simultaneous indexer searches.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await worker();
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
