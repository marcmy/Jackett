using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlareSolverrSharp.Utilities
{
    public class SemaphoreLocker
    {
        public const string ConcurrencyEnvironmentVariable = "JACKETT_FLARESOLVERR_CONCURRENCY";
        private const int DefaultSolverConcurrency = 4;
        private const int MaxSolverConcurrency = 16;

        /// <summary>
        /// Browser solves allowed at once per FlareSolverr endpoint. Each one is a Chrome
        /// instance in FlareSolverr, so this trades memory for search throughput.
        /// </summary>
        public static int DefaultConcurrency { get; } = ReadConcurrency(
            Environment.GetEnvironmentVariable(ConcurrencyEnvironmentVariable));

        private readonly SemaphoreSlim _semaphore;

        internal static int ReadConcurrency(string value) =>
            int.TryParse(value, out var parsed) && parsed > 0
                ? Math.Min(parsed, MaxSolverConcurrency)
                : DefaultSolverConcurrency;

        public SemaphoreLocker() : this(DefaultConcurrency)
        {
        }

        public SemaphoreLocker(int concurrency)
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
