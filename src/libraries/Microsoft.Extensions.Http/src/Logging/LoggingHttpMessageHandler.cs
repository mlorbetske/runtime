// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Http.Logging
{
    /// <summary>
    /// Handles logging of the lifecycle for an HTTP request.
    /// </summary>
    public class LoggingHttpMessageHandler : DelegatingHandler
    {
        private readonly ILogger _logger;
        private readonly HttpClientFactoryOptions? _options;

        private static readonly Func<string, bool> _shouldNotRedactHeaderValue = (header) => false;

        /// <summary>
        /// Initializes a new instance of the <see cref="LoggingHttpMessageHandler"/> class with a specified logger.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to log to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
        public LoggingHttpMessageHandler(ILogger logger)
        {
            ThrowHelper.ThrowIfNull(logger);

            _logger = logger;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LoggingHttpMessageHandler"/> class with a specified logger and options.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to log to.</param>
        /// <param name="options">The <see cref="HttpClientFactoryOptions"/> used to configure the <see cref="LoggingHttpMessageHandler"/> instance.</param>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
        public LoggingHttpMessageHandler(ILogger logger, HttpClientFactoryOptions options)
        {
            ThrowHelper.ThrowIfNull(logger);
            ThrowHelper.ThrowIfNull(options);

            _logger = logger;
            _options = options;
        }

        /// <inheritdoc />
        /// <remarks>Loggs the request to and response from the sent <see cref="HttpRequestMessage"/>.</remarks>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ThrowHelper.ThrowIfNull(request);
            return Core(request, cancellationToken);

            async Task<HttpResponseMessage> Core(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Func<string, bool> shouldRedactHeaderValue = _options?.ShouldRedactHeaderValue ?? _shouldNotRedactHeaderValue;

                // Not using a scope here because we always expect this to be at the end of the pipeline, thus there's
                // not really anything to surround.
                Log.RequestStart(_logger, request, shouldRedactHeaderValue);
                var stopwatch = ValueStopwatch.StartNew();
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                Log.RequestEnd(_logger, response, stopwatch.GetElapsedTime(), shouldRedactHeaderValue);

                return response;
            }
        }

        // Used in tests.
        internal static class Log
        {
            public static class EventIds
            {
                public static readonly EventId RequestStart = new EventId(100, "RequestStart");
                public static readonly EventId RequestEnd = new EventId(101, "RequestEnd");

                public static readonly EventId RequestHeader = new EventId(102, "RequestHeader");
                public static readonly EventId ResponseHeader = new EventId(103, "ResponseHeader");
            }

            private static readonly LogDefineOptions _skipEnabledCheckLogDefineOptions = new LogDefineOptions() { SkipEnabledCheck = true };

            private static readonly Action<ILogger, HttpMethod, string?, Exception?> _requestStart = LoggerMessage.Define<HttpMethod, string?>(
                LogLevel.Information,
                EventIds.RequestStart,
                "Sending HTTP request {HttpMethod} {Uri}",
                _skipEnabledCheckLogDefineOptions);

            private static readonly Action<ILogger, double, int, Exception?> _requestEnd = LoggerMessage.Define<double, int>(
                LogLevel.Information,
                EventIds.RequestEnd,
                "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}");

            public static void RequestStart(ILogger logger, HttpRequestMessage request, Func<string, bool> shouldRedactHeaderValue)
            {
                // We check here to avoid allocating in the GetUriString call unnecessarily
                if (logger.IsEnabled(LogLevel.Information))
                {
                    _requestStart(logger, request.Method, GetUriString(request.RequestUri), null);
                }

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Log(
                        LogLevel.Trace,
                        EventIds.RequestHeader,
                        new HttpHeadersLogValue(HttpHeadersLogValue.Kind.Request, request.Headers, request.Content?.Headers, shouldRedactHeaderValue),
                        null,
                        (state, ex) => state.ToString());
                }
            }

            public static void RequestEnd(ILogger logger, HttpResponseMessage response, TimeSpan duration, Func<string, bool> shouldRedactHeaderValue)
            {
                _requestEnd(logger, duration.TotalMilliseconds, (int)response.StatusCode, null);

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Log(
                        LogLevel.Trace,
                        EventIds.ResponseHeader,
                        new HttpHeadersLogValue(HttpHeadersLogValue.Kind.Response, response.Headers, response.Content?.Headers, shouldRedactHeaderValue),
                        null,
                        (state, ex) => state.ToString());
                }
            }

            private static string? GetUriString(Uri? requestUri)
            {
                return requestUri?.IsAbsoluteUri == true
                    ? requestUri.AbsoluteUri
                    : requestUri?.ToString();
            }
        }
    }
}
