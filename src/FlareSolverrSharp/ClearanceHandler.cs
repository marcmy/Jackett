using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlareSolverrSharp.Constants;
using FlareSolverrSharp.Exceptions;
using FlareSolverrSharp.Extensions;
using FlareSolverrSharp.Solvers;
using FlareSolverrSharp.Types;
using Cookie = System.Net.Cookie;

namespace FlareSolverrSharp
{
    /// <summary>
    /// An HTTP handler that transparently manages CloudFlare's protection bypass.
    /// </summary>
    public class ClearanceHandler : DelegatingHandler
    {
        private readonly string _flareSolverrApiUrl;
        private volatile FlareSolverr _flareSolverr;
        private readonly object _solverLock = new object();
        private string _userAgent;

        /// <summary>
        /// Max timeout to solve the challenge.
        /// </summary>
        public int MaxTimeout = 60000;
        // Disable the outer HttpClient timeout so queue time is not charged to
        // the tracker request. Each network phase retains its own deadline.
        public TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

        /// <summary>
        /// HTTP Proxy URL.
        /// Example: http://127.0.0.1:8888
        /// </summary>
        public string ProxyUrl = "";

        /// <summary>
        /// HTTP Proxy Username.
        /// </summary>
        public string ProxyUsername = null;

        /// <summary>
        /// HTTP Proxy Password.
        /// </summary>
        public string ProxyPassword = null;

        private HttpClientHandler HttpClientHandler => InnerHandler.GetMostInnerHandler() as HttpClientHandler;

        /// <summary>
        /// Creates a new instance of the <see cref="ClearanceHandler"/>.
        /// </summary>
        /// <param name="flareSolverrApiUrl">FlareSolverr API URL. If null or empty it will detect the challenges, but
        /// they will not be solved. Example: "http://localhost:8191/"</param>
        public ClearanceHandler(string flareSolverrApiUrl)
            : base(new HttpClientHandler())
        {
            // Validate URI
            if (!string.IsNullOrWhiteSpace(flareSolverrApiUrl)
                && !Uri.IsWellFormedUriString(flareSolverrApiUrl, UriKind.Absolute))
            {
                throw new FlareSolverrException("FlareSolverr URL is malformed: " + flareSolverrApiUrl);
            }

            _flareSolverrApiUrl = flareSolverrApiUrl;

        }

        internal ClearanceHandler(string flareSolverrApiUrl, FlareSolverr solver)
            : this(flareSolverrApiUrl)
        {
            _flareSolverr = solver;
        }

        /// <summary>
        /// Sends an HTTP request to the inner handler to send to the server as an asynchronous operation.
        /// </summary>
        /// <param name="request">The HTTP request message to send to the server.</param>
        /// <param name="cancellationToken">A cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Init FlareSolverr
            if (_flareSolverr == null && !string.IsNullOrWhiteSpace(_flareSolverrApiUrl))
            {
                lock (_solverLock)
                {
                    if (_flareSolverr == null)
                    {
                        _flareSolverr = new FlareSolverr(_flareSolverrApiUrl)
                        {
                            MaxTimeout = MaxTimeout,
                            ProxyUrl = ProxyUrl,
                            ProxyUsername = ProxyUsername,
                            ProxyPassword = ProxyPassword
                        };
                    }
                }
            }

            // Set the User-Agent if required
            SetUserAgentHeader(request);

            // Perform the original user request
            var response = await SendTrackerRequestAsync(request, cancellationToken).ConfigureAwait(false);

            // Detect if there is a challenge in the response
            if (ChallengeDetector.IsClearanceRequired(response))
            {
                if (_flareSolverr == null)
                {
                    response.Dispose();
                    throw new FlareSolverrException("Challenge detected but FlareSolverr is not configured");
                }

                // Resolve the challenge using FlareSolverr API
                response.Dispose();
                var flareSolverrResponse = await _flareSolverr.Solve(request, cancellationToken: cancellationToken);

                if (flareSolverrResponse == null)
                {
                    throw new FlareSolverrException("Empty response returned by FlareSolverr");
                }

                if (flareSolverrResponse.Solution == null)
                {
                    throw new FlareSolverrException("Empty solution returned by FlareSolverr");
                }

                if (flareSolverrResponse.Solution.Cookies == null || flareSolverrResponse.Solution.Cookies.Length == 0)
                {
                    throw new FlareSolverrException("Empty cookies returned by FlareSolverr");
                }

                // Save the FlareSolverr User-Agent for the following requests
                var flareSolverUserAgent = flareSolverrResponse.Solution.UserAgent;
                if (flareSolverUserAgent != null && !flareSolverUserAgent.Equals(request.Headers.UserAgent.ToString()))
                {
                    _userAgent = flareSolverUserAgent;

                    // Set the User-Agent if required
                    SetUserAgentHeader(request);
                }

                // Change the cookies in the original request with the cookies provided by FlareSolverr
                InjectCookies(request, flareSolverrResponse);
                response = await SendTrackerRequestAsync(request, cancellationToken).ConfigureAwait(false);

                // Detect if there is a challenge in the response
                if (ChallengeDetector.IsClearanceRequired(response))
                {
                    response.Dispose();
                    throw new FlareSolverrException("The cookies provided by FlareSolverr are not valid");
                }

                // Add the "Set-Cookie" header in the response with the cookies provided by FlareSolverr
                InjectSetCookieHeader(response, flareSolverrResponse);
            }

            return response;
        }

        private async Task<HttpResponseMessage> SendTrackerRequestAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);
            HttpResponseMessage response = null;
            try
            {
                response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
                var buffering = response.Content.LoadIntoBufferAsync();
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => canceled.TrySetResult(true)))
                {
                    await Task.WhenAny(buffering, canceled.Task).ConfigureAwait(false);
                    if (deadline.IsCancellationRequested)
                    {
                        // Disposal aborts the body read; observe any resulting fault.
                        _ = buffering.ContinueWith(task => { _ = task.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    await buffering.ConfigureAwait(false);
                }
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw new TimeoutException($"Tracker request timed out after {RequestTimeout.TotalSeconds} seconds.");
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }

        private void SetUserAgentHeader(HttpRequestMessage request)
        {
            if (_userAgent != null)
            {
                // Overwrite the header
                request.Headers.Remove(HttpHeaders.UserAgent);
                request.Headers.Add(HttpHeaders.UserAgent, _userAgent);
            }
        }

        private void InjectCookies(HttpRequestMessage request, FlareSolverrResponse flareSolverrResponse)
        {
            // use only Cloudflare and DDoS-GUARD cookies
            var flareCookies = flareSolverrResponse.Solution
                .Cookies
                .Where(cookie => IsCloudflareCookie(cookie.Name))
                .ToList();

            // not using cookies, just add flaresolverr cookies to the header request
            if (!HttpClientHandler.UseCookies)
            {
                foreach (var rCookie in flareCookies)
                {
                    request.Headers.Add(HttpHeaders.Cookie, rCookie.ToHeaderValue());
                }

                return;
            }

            var currentCookies = HttpClientHandler.CookieContainer.GetCookies(request.RequestUri);

            // remove previous FlareSolverr cookies
            foreach (var cookie in flareCookies.Select(flareCookie => currentCookies[flareCookie.Name]).Where(cookie => cookie != null))
            {
                cookie.Expired = true;
            }

            // add FlareSolverr cookies to CookieContainer
            foreach (var rCookie in flareCookies)
            {
                HttpClientHandler.CookieContainer.Add(request.RequestUri, rCookie.ToCookieObj());
            }

            // check if there is too many cookies, we may need to remove some
            if (HttpClientHandler.CookieContainer.PerDomainCapacity >= currentCookies.Count)
            {
                return;
            }

            // check if indeed we have too many cookies
            var validCookiesCount = currentCookies.Cast<Cookie>().Count(cookie => !cookie.Expired);
            if (HttpClientHandler.CookieContainer.PerDomainCapacity >= validCookiesCount)
            {
                return;
            }

            // if there is a too many cookies, we have to make space
            // maybe is better to raise an exception?
            var cookieExcess = HttpClientHandler.CookieContainer.PerDomainCapacity - validCookiesCount;

            foreach (Cookie cookie in currentCookies)
            {
                if (cookieExcess == 0)
                {
                    break;
                }

                if (cookie.Expired || IsCloudflareCookie(cookie.Name))
                {
                    continue;
                }

                cookie.Expired = true;
                cookieExcess -= 1;
            }
        }

        private static void InjectSetCookieHeader(HttpResponseMessage response, FlareSolverrResponse flareSolverrResponse)
        {
            // inject set-cookie headers in the response
            foreach (var rCookie in flareSolverrResponse.Solution.Cookies.Where(cookie => IsCloudflareCookie(cookie.Name)))
            {
                response.Headers.Add(HttpHeaders.SetCookie, rCookie.ToHeaderValue());
            }
        }

        private static bool IsCloudflareCookie(string cookieName) =>
            cookieName.StartsWith("cf_") || cookieName.StartsWith("__cf") || cookieName.StartsWith("__ddg");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flareSolverr?.Dispose();
            }

            base.Dispose(disposing);
        }

    }
}
