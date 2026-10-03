using System;
using System.Net;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlareSolverrSharp.Exceptions;
using FlareSolverrSharp.Types;
using FlareSolverrSharp.Utilities;
using Newtonsoft.Json;

namespace FlareSolverrSharp.Solvers
{
    public class FlareSolverr : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreLocker> Lockers = new ConcurrentDictionary<string, SemaphoreLocker>();
        private readonly SemaphoreLocker _locker;
        private readonly HttpClient _httpClient;
        private readonly Uri _flareSolverrUri;

        public int MaxTimeout = 60000;
        public string ProxyUrl = "";
        public string ProxyUsername = null;
        public string ProxyPassword = null;
        public TimeSpan QueueTimeout = TimeSpan.FromMinutes(5);
        public TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

        public FlareSolverr(string flareSolverrApiUrl)
            : this(flareSolverrApiUrl, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, null)
        {
        }

        internal FlareSolverr(string flareSolverrApiUrl, HttpClient httpClient, SemaphoreLocker locker)
        {
            var apiUrl = flareSolverrApiUrl;
            if (!apiUrl.EndsWith("/"))
                apiUrl += "/";
            _flareSolverrUri = new Uri(apiUrl + "v1");
            _httpClient = httpClient;
            _locker = locker ?? Lockers.GetOrAdd(_flareSolverrUri.AbsoluteUri, _ => new SemaphoreLocker());
        }

        public async Task<FlareSolverrResponse> Solve(HttpRequestMessage request, string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            return await SendFlareSolverrRequest(GenerateFlareSolverrRequest(request, sessionId), cancellationToken);
        }

        public async Task<FlareSolverrResponse> CreateSession()
        {
            var req = new FlareSolverrRequestGet
            {
                Cmd = "sessions.create",
                MaxTimeout = MaxTimeout,
                Proxy = GetProxy()
            };
            return await SendFlareSolverrRequest(GetSolverRequestContent(req));
        }

        public async Task<FlareSolverrResponse> ListSessions()
        {
            var req = new FlareSolverrRequestGet
            {
                Cmd = "sessions.list",
                MaxTimeout = MaxTimeout,
                Proxy = GetProxy()
            };
            return await SendFlareSolverrRequest(GetSolverRequestContent(req));
        }

        public async Task<FlareSolverrResponse> DestroySession(string sessionId)
        {
            var req = new FlareSolverrRequestGet
            {
                Cmd = "sessions.destroy",
                MaxTimeout = MaxTimeout,
                Proxy = GetProxy(),
                Session = sessionId
            };
            return await SendFlareSolverrRequest(GetSolverRequestContent(req));
        }

        private async Task<FlareSolverrResponse> SendFlareSolverrRequest(HttpContent flareSolverrRequest,
            CancellationToken cancellationToken = default)
        {
            FlareSolverrResponse result = null;

            using (flareSolverrRequest)
                await _locker.LockAsync(async () =>
                {
                    HttpResponseMessage response;
                    using var activeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // Start this deadline only after a browser slot is available.
                    activeTimeout.CancelAfter(TimeSpan.FromMilliseconds(MaxTimeout) + CleanupTimeout);
                    try
                    {
                        response = await _httpClient.PostAsync(_flareSolverrUri, flareSolverrRequest, activeTimeout.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        throw new FlareSolverrException("Timed out waiting for FlareSolverr to solve the challenge and clean up its browser.");
                    }
                    catch (HttpRequestException e)
                    {
                        throw new FlareSolverrException("Error connecting to FlareSolverr server: " + e);
                    }
                    catch (Exception e)
                    {
                        throw new FlareSolverrException("Exception: " + e);
                    }
                    using (response)
                    {
                        // Don't try parsing if FlareSolverr hasn't returned 200 or 500
                        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.InternalServerError)
                        {
                            throw new FlareSolverrException("HTTP StatusCode not 200 or 500. Status is :" + response.StatusCode);
                        }

                        var resContent = await response.Content.ReadAsStringAsync();
                        try
                        {
                            result = JsonConvert.DeserializeObject<FlareSolverrResponse>(resContent);
                        }
                        catch (Exception)
                        {
                            throw new FlareSolverrException("Error parsing response, check FlareSolverr. Response: " + resContent);
                        }

                        try
                        {
                            if (result == null || !Enum.TryParse(result.Status, true, out FlareSolverrStatusCode returnStatusCode))
                                throw new FlareSolverrException("Invalid status returned by FlareSolverr.");

                            if (returnStatusCode.Equals(FlareSolverrStatusCode.ok))
                            {
                                return result;
                            }

                            if (returnStatusCode.Equals(FlareSolverrStatusCode.warning))
                            {
                                throw new FlareSolverrException(
                                "FlareSolverr was able to process the request, but a captcha was detected. Message: "
                                + result.Message);
                            }

                            if (returnStatusCode.Equals(FlareSolverrStatusCode.error))
                            {
                                throw new FlareSolverrException(
                                "FlareSolverr was unable to process the request, please check FlareSolverr logs. Message: "
                                + result.Message);
                            }

                            throw new FlareSolverrException("Unable to map FlareSolverr returned status code, received code: "
                            + result.Status + ". Message: " + result.Message);
                        }
                        catch (ArgumentException)
                        {
                            throw new FlareSolverrException("Error parsing status code, check FlareSolverr log. Status: "
                                + result.Status + ". Message: " + result.Message);
                        }
                    }
                }, cancellationToken, QueueTimeout);

            return result;
        }

        public void Dispose() => _httpClient.Dispose();

        private FlareSolverrRequestProxy GetProxy()
        {
            FlareSolverrRequestProxy proxy = null;
            if (!string.IsNullOrWhiteSpace(ProxyUrl))
            {
                proxy = new FlareSolverrRequestProxy
                {
                    Url = ProxyUrl,
                };
                if (!string.IsNullOrWhiteSpace(ProxyUsername))
                {
                    proxy.Username = ProxyUsername;
                }
                ;
                if (!string.IsNullOrWhiteSpace(ProxyPassword))
                {
                    proxy.Password = ProxyPassword;
                }
                ;
            }
            return proxy;
        }

        private HttpContent GetSolverRequestContent(FlareSolverrRequest request)
        {
            var payload = JsonConvert.SerializeObject(request, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            HttpContent content = new StringContent(payload, Encoding.UTF8, "application/json");
            return content;
        }

        private HttpContent GenerateFlareSolverrRequest(HttpRequestMessage request, string sessionId = "")
        {
            FlareSolverrRequest req;
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = null;

            var url = request.RequestUri.ToString();

            FlareSolverrRequestProxy proxy = GetProxy();

            if (request.Method == HttpMethod.Get)
            {
                req = new FlareSolverrRequestGet
                {
                    Cmd = "request.get",
                    Url = url,
                    MaxTimeout = MaxTimeout,
                    Proxy = proxy,
                    Session = sessionId
                };
            }
            else if (request.Method == HttpMethod.Post)
            {
                // request.Content.GetType() doesn't work well when encoding != utf-8
                var contentMediaType = request.Content.Headers.ContentType?.MediaType.ToLower() ?? "<null>";
                if (contentMediaType.Contains("application/x-www-form-urlencoded"))
                {
                    req = new FlareSolverrRequestPost
                    {
                        Cmd = "request.post",
                        Url = url,
                        PostData = request.Content.ReadAsStringAsync().Result,
                        MaxTimeout = MaxTimeout,
                        Proxy = proxy,
                        Session = sessionId
                    };
                }
                else if (contentMediaType.Contains("multipart/form-data")
                         || contentMediaType.Contains("text/html"))
                {
                    //TODO Implement - check if we just need to pass the content-type with the relevant headers
                    throw new FlareSolverrException("Unimplemented POST Content-Type: " + contentMediaType);
                }
                else
                {
                    throw new FlareSolverrException("Unsupported POST Content-Type: " + contentMediaType);
                }
            }
            else
            {
                throw new FlareSolverrException("Unsupported HttpMethod: " + request.Method);
            }

            return GetSolverRequestContent(req);
        }

    }
}
