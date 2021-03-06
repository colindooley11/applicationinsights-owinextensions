﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Owin;

namespace ApplicationInsights.OwinExtensions
{
    public class HttpRequestTrackingMiddleware : OwinMiddleware
    {
        private readonly TelemetryClient _client;
        private readonly RequestTrackingConfiguration _configuration;

        [Obsolete("Use the overload accepting RequestTrackingConfiguration")]
        public HttpRequestTrackingMiddleware(
            OwinMiddleware next, 
            TelemetryConfiguration configuration = null, 
            Func<IOwinRequest, IOwinResponse, bool> shouldTraceRequest = null, 
            Func<IOwinRequest, IOwinResponse, KeyValuePair<string,string>[]> getContextProperties = null) : 
            this(next, new RequestTrackingConfiguration
            {
                TelemetryConfiguration = configuration,
                ShouldTrackRequest = shouldTraceRequest != null 
                    ? ctx => Task.FromResult(shouldTraceRequest(ctx.Request, ctx.Response))
                    : new RequestTrackingConfiguration().ShouldTrackRequest,
                GetAdditionalContextProperties = getContextProperties != null 
                    ? ctx => Task.FromResult(getContextProperties(ctx.Request, ctx.Response).AsEnumerable())
                    : new RequestTrackingConfiguration().GetAdditionalContextProperties,
            })
        {
        }

        public HttpRequestTrackingMiddleware(
            OwinMiddleware next,
            RequestTrackingConfiguration configuration = null) : base(next)
        {
            _configuration = configuration ?? new RequestTrackingConfiguration();

            _client = _configuration.TelemetryConfiguration != null 
                ? new TelemetryClient(_configuration.TelemetryConfiguration) 
                : new TelemetryClient();
        }

        public override async Task Invoke(IOwinContext context)
        {
            // following request properties have to be accessed before other middlewares run
            // otherwise access could result in ObjectDisposedException
            var method = context.Request.Method;
            var path = context.Request.Path.ToString();
            var uri = context.Request.Uri;

            var requestId = _configuration.RequestIdFactory(context);
            var requestParentId = OperationContext.Get()?.ParentOperationId;

            var requestStartDate = DateTimeOffset.Now;
            var stopWatch = new Stopwatch();

            using (new OperationContextScope(
                operationId: OperationContext.Get()?.OperationId ?? requestId,
                parentOperationId: requestId))
            using (new OperationContextStoredInOwinContextScope(context))
            {

                stopWatch.Start();

                try
                {
                    await Next.Invoke(context);

                    stopWatch.Stop();

                    if (await _configuration.ShouldTrackRequest(context))
                        await TrackRequest(requestId, requestParentId, method, path, uri, context,
                            context.Response.StatusCode, requestStartDate, stopWatch.Elapsed);

                }
                catch (Exception e)
                {
                    stopWatch.Stop();

                    TraceException(requestId, e);

                    if (await _configuration.ShouldTrackRequest(context))
                        await TrackRequest(requestId, requestParentId, method, path, uri, context,
                            (int)HttpStatusCode.InternalServerError, requestStartDate, stopWatch.Elapsed);

                    throw;
                }
            }
        }

        private async Task TrackRequest(
            string requestId,
            string requestParentId,
            string method,
            string path,
            Uri uri,
            IOwinContext context, 
            int responseCode, 
            DateTimeOffset requestStartDate, 
            TimeSpan duration)
        {
            var name = $"{method} {path}";

            var telemetry = new RequestTelemetry(
                name,
                requestStartDate,
                duration,
                responseCode.ToString(),
                success: responseCode < 400)
            {
                Id = requestId,
                HttpMethod = method,
                Url = uri
            };


            telemetry.Context.Operation.ParentId = requestParentId;
            telemetry.Context.Operation.Name = name;

            foreach (var kvp in await _configuration.GetAdditionalContextProperties(context))
                telemetry.Context.Properties.Add(kvp);

            _client.TrackRequest(telemetry);
        }

        private void TraceException(string requestId, Exception e)
        {
            var telemetry = new ExceptionTelemetry(e);
            telemetry.Context.Operation.ParentId = requestId;

            _client.TrackException(telemetry);
        }
    }

}
