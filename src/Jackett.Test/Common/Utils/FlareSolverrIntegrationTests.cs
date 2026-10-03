using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlareSolverrSharp;
using FlareSolverrSharp.Exceptions;
using FlareSolverrSharp.Utilities;
using NUnit.Framework;
using Solver = FlareSolverrSharp.Solvers.FlareSolverr;

namespace Jackett.Test.Common.Utils
{
    [TestFixture]
    public class FlareSolverrIntegrationTests
    {
        private static TaskCompletionSource<bool> Signal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static HttpRequestMessage TrackerRequest() =>
            new HttpRequestMessage(HttpMethod.Get, "https://tracker.example/search");

        private static HttpResponseMessage Solution() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\",\"solution\":{\"userAgent\":\"test-browser\"," +
                "\"cookies\":[{\"name\":\"cf_clearance\",\"value\":\"clearance\"}]}}")
        };

        private static Solver CreateSolver(Func<CancellationToken, Task<HttpResponseMessage>> send,
            SemaphoreLocker locker = null, string endpoint = null) =>
            new Solver(endpoint ?? "http://solver.example/" + Guid.NewGuid() + "/",
                new HttpClient(new CallbackHandler(send)) { Timeout = Timeout.InfiniteTimeSpan }, locker);

        private static async Task Ready(Task task)
        {
            Assert.That(await Task.WhenAny(task, Task.Delay(5000)), Is.SameAs(task), "Operation did not finish.");
            await task;
        }

        [Test]
        public async Task TwoSolvesRunConcurrentlyAndThirdWaitsAcrossClients()
        {
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            var endpoint = "http://solver.example/" + Guid.NewGuid() + "/";
            async Task<HttpResponseMessage> Send(CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                    entered.TrySetResult(true);
                await release.Task;
                return Solution();
            }
            using var first = CreateSolver(Send, endpoint: endpoint);
            using var second = CreateSolver(Send, endpoint: endpoint);
            using var third = CreateSolver(Send, endpoint: endpoint);
            using var request = TrackerRequest();
            var a = first.Solve(request);
            var b = second.Solve(request);
            await Ready(entered.Task);
            var c = third.Solve(request);
            Assert.That(calls, Is.EqualTo(2));
            release.SetResult(true);
            await Ready(Task.WhenAll(a, b, c));
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public async Task SeparateEndpointsDoNotBlockEachOther()
        {
            var release = Signal();
            var otherEntered = Signal();
            using var first = CreateSolver(async token => { await release.Task; return Solution(); });
            using var second = CreateSolver(async token =>
                { otherEntered.SetResult(true); await release.Task; return Solution(); },
                endpoint: "http://other-solver.example/");
            using var request = TrackerRequest();
            var a = first.Solve(request);
            var b = first.Solve(request);
            var c = second.Solve(request);
            await Ready(otherEntered.Task);
            release.SetResult(true);
            await Ready(Task.WhenAll(a, b, c));
        }

        [Test]
        public async Task CanceledQueueEntryNeverReachesServerOrConsumesSlot()
        {
            var release = Signal();
            var calls = 0;
            using var solver = CreateSolver(async token =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return Solution();
            }, new SemaphoreLocker(1));
            using var request = TrackerRequest();
            var first = solver.Solve(request);
            using var canceled = new CancellationTokenSource();
            var queued = solver.Solve(request, cancellationToken: canceled.Token);
            canceled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            Assert.That(calls, Is.EqualTo(1));
            release.SetResult(true);
            await Ready(first);
            await Ready(solver.Solve(request));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public async Task ActiveCancellationReachesServerAndReleasesSlot()
        {
            var entered = Signal();
            var calls = 0;
            using var solver = CreateSolver(async token =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Solution();
            }, new SemaphoreLocker(1));
            using var request = TrackerRequest();
            using var canceled = new CancellationTokenSource();
            var active = solver.Solve(request, cancellationToken: canceled.Token);
            await Ready(entered.Task);
            canceled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await active);
            await Ready(solver.Solve(request));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public async Task WaitingDoesNotConsumeActiveSolverBudget()
        {
            var locker = new SemaphoreLocker(1);
            var release = Signal();
            var occupying = locker.LockAsync(async () => await release.Task);
            using var solver = CreateSolver(token => Task.FromResult(Solution()), locker);
            solver.MaxTimeout = 100;
            solver.CleanupTimeout = TimeSpan.FromMilliseconds(100);
            using var request = TrackerRequest();
            var queued = solver.Solve(request);
            await Task.Delay(300);
            Assert.That(queued.IsCompleted, Is.False);
            release.SetResult(true);
            await Ready(Task.WhenAll(occupying, queued));
        }

        [Test]
        public async Task QueueHasAnExplicitDeadline()
        {
            var locker = new SemaphoreLocker(1);
            var release = Signal();
            var occupying = locker.LockAsync(async () => await release.Task);
            using var solver = CreateSolver(token => Task.FromResult(Solution()), locker);
            solver.QueueTimeout = TimeSpan.FromMilliseconds(100);
            using var request = TrackerRequest();
            var error = Assert.ThrowsAsync<TimeoutException>(async () => await solver.Solve(request));
            Assert.That(error.Message, Does.Contain("browser slot"));
            release.SetResult(true);
            await Ready(occupying);
            await Ready(solver.Solve(request));
        }

        [Test]
        public async Task SolverAllowsCleanupTimeButStillHasAnActiveDeadline()
        {
            using var solver = CreateSolver(async token =>
            {
                await Task.Delay(150, token);
                return Solution();
            });
            solver.MaxTimeout = 50;
            solver.CleanupTimeout = TimeSpan.FromMilliseconds(500);
            using var request = TrackerRequest();
            await Ready(solver.Solve(request));
            solver.CleanupTimeout = TimeSpan.Zero;
            var error = Assert.ThrowsAsync<FlareSolverrException>(async () => await solver.Solve(request));
            Assert.That(error.Message, Does.Contain("clean up"));
        }

        [Test]
        public async Task TrackerRetryWorksAfterQueueExceedsNetworkTimeout()
        {
            var locker = new SemaphoreLocker(1);
            var release = Signal();
            var occupying = locker.LockAsync(async () => await release.Task);
            var calls = 0;
            using var handler = new ClearanceHandler("http://solver.example/",
                CreateSolver(token => Task.FromResult(Solution()), locker))
            {
                RequestTimeout = TimeSpan.FromMilliseconds(100),
                InnerHandler = new TrackerHandler(token =>
                {
                    var response = new HttpResponseMessage(Interlocked.Increment(ref calls) == 1
                        ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                    {
                        Content = new StringContent(calls == 1 ? "<title>Just a moment...</title>" : "results")
                    };
                    response.Headers.TryAddWithoutValidation("Server", "cloudflare");
                    return Task.FromResult(response);
                })
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var result = client.GetAsync("https://tracker.example/search");
            await Task.Delay(300);
            Assert.That(result.IsCompleted, Is.False);
            release.SetResult(true);
            await Ready(occupying);
            using var response = await result;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("results"));
            Assert.That(calls, Is.EqualTo(2));
            var tracker = (TrackerHandler)handler.InnerHandler;
            var cookies = ((HttpClientHandler)tracker.InnerHandler).CookieContainer.GetCookies(new Uri("https://tracker.example/"));
            Assert.That(cookies["cf_clearance"].Value, Is.EqualTo("clearance"));
            Assert.That(response.Headers.GetValues("Set-Cookie"), Does.Contain("cf_clearance=clearance"));
        }

        [Test]
        public void SlowTrackerStillTimesOutWithoutAnOuterHttpClientDeadline()
        {
            using var handler = new ClearanceHandler(null)
            {
                RequestTimeout = TimeSpan.FromMilliseconds(100),
                InnerHandler = new TrackerHandler(async token =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                })
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var error = Assert.ThrowsAsync<TimeoutException>(async () => await client.GetAsync("https://tracker.example/"));
            Assert.That(error.Message, Does.Contain("Tracker request timed out"));
        }

        [Test]
        public void SlowTrackerBodyIsAlsoCoveredByNetworkDeadline()
        {
            using var handler = new ClearanceHandler(null)
            {
                RequestTimeout = TimeSpan.FromMilliseconds(100),
                InnerHandler = new TrackerHandler(token => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new HangingContent()
                }))
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            Assert.ThrowsAsync<TimeoutException>(async () => await client.GetAsync("https://tracker.example/"));
        }

        private sealed class CallbackHandler : HttpMessageHandler
        {
            private readonly Func<CancellationToken, Task<HttpResponseMessage>> _send;
            public CallbackHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(token);
        }

        private sealed class TrackerHandler : DelegatingHandler
        {
            private readonly Func<CancellationToken, Task<HttpResponseMessage>> _send;
            public TrackerHandler(Func<CancellationToken, Task<HttpResponseMessage>> send)
                : base(new HttpClientHandler()) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(token);
        }

        private sealed class HangingContent : HttpContent
        {
            private readonly TaskCompletionSource<bool> _disposed = Signal();
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => _disposed.Task;
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override void Dispose(bool disposing)
            {
                _disposed.TrySetResult(true);
                base.Dispose(disposing);
            }
        }
    }
}
