﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyHealthCheck.HealthChecks;
using TinyHealthCheck.Models;

namespace TinyHealthCheck
{
    public class HealthCheckService<T> : BackgroundService where T : IHealthCheck
    {
        private readonly ILogger<HealthCheckService<T>> _logger;
        private readonly TinyHealthCheckConfig _config;
        private readonly T _healthCheck;
        private readonly HttpListener _listener = new HttpListener();

        public HealthCheckService(ILogger<HealthCheckService<T>> logger, T healthCheck, TinyHealthCheckConfig config)
        {
            _logger = logger;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _healthCheck = healthCheck;
        }

        /// <summary>
        /// Start the HTTP Listener loop. Runs indefinitely.
        /// </summary>
        /// <param name="cancellationToken">Token that can be used to stop the listener</param>
        /// <returns><see cref="Task"/></returns>
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Prefixes.Add($"http://{_config.Hostname}:{_config.Port}/");
                _listener.Start();

                _logger.LogInformation($"TinyHealthCheck<{typeof(T).Name}> started on port '{_config.Port}'");

                var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var httpContextTask = _listener.GetContextAsync();
                    var completedTask = await Task.WhenAny(httpContextTask, cancelTask).ConfigureAwait(false);
                    if(completedTask == cancelTask) break;
                    ThreadPool.QueueUserWorkItem(async x => await ProcessHealthCheck((HttpListenerContext)x, cancellationToken).ConfigureAwait(false), httpContextTask.Result);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "TinyHealthCheck had an exception!");
            }
        }

        /// <summary>
        /// The process that returns responses to clients.
        /// </summary>
        /// <param name="client">HttpListenerContext that listens for HTTP requests</param>
        /// <param name="cancellationToken">Token that can be used to stop the listener</param>
        /// <returns><see cref="Task"/></returns>
        private async Task ProcessHealthCheck(HttpListenerContext client, CancellationToken cancellationToken)
        {
            var request = client.Request;

            _logger.LogInformation($"TinyHealthCheck received a request from {request.RemoteEndPoint}");

            using (var response = client.Response)
            {
                if (!request.HttpMethod.Equals("GET", StringComparison.InvariantCultureIgnoreCase)
                    || !request.Url.PathAndQuery.Equals(_config.UrlPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    response.StatusCode = 404;
                    return;
                };

                var healthCheckResult = await _healthCheck.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                response.ContentType = healthCheckResult.ContentType;
                response.ContentEncoding = healthCheckResult.ContentEncoding;

                response.StatusCode = (int)healthCheckResult.StatusCode;
                byte[] data = Encoding.UTF8.GetBytes(healthCheckResult.Body);

                response.ContentLength64 = data.LongLength;
                await response.OutputStream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
