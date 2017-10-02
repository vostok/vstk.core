﻿using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Vostok.Clusterclient.Model;
using Vostok.Commons.Collections;
using Vostok.Logging;

namespace Vostok.Clusterclient.Transport.Http
{
    // ReSharper disable MethodSupportsCancellation
    // ReSharper disable PossibleNullReferenceException

    public class VostokHttpTransport : ITransport
    {
        private const int PreferredReadSize = 16*1024;
        private const int LOHObjectSizeThreshold = 85*1000;

        private static readonly TimeSpan RequestAbortTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly IPool<byte[]> ReadBuffersPool = new UnlimitedLazyPool<byte[]>(() => new byte[PreferredReadSize]);

        private readonly VostokHttpTransportSettings settings;
        private readonly ILog log;
        private readonly ConnectTimeLimiter connectTimeLimiter;

        public VostokHttpTransport(ILog log)
            : this(new VostokHttpTransportSettings(), log)
        {
        }

        public VostokHttpTransport(VostokHttpTransportSettings settings, ILog log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            connectTimeLimiter = new ConnectTimeLimiter(settings, log);
        }

        public async Task<Response> SendAsync(Request request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout.TotalMilliseconds < 1)
            {
                LogRequestTimeout(request, timeout);
                return new Response(ResponseCode.RequestTimeout);
            }

            var state = new HttpWebRequestState(timeout);

            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(state.TimeRemaining, timeoutCancellation.Token);
                var senderTask = SendInternalAsync(request, state, cancellationToken);
                var completedTask = await Task.WhenAny(timeoutTask, senderTask).ConfigureAwait(false);
                if (completedTask is Task<Response> taskWithResponse)
                {
                    timeoutCancellation.Cancel();
                    return taskWithResponse.GetAwaiter().GetResult();
                }

                // (iloktionov): Если выполнившееся задание не кастуется к Task<Response>, сработал таймаут.
                state.CancelRequest();
                LogRequestTimeout(request, timeout);
                // TODO(iloktionov): fix threadpool if needed

                // (iloktionov): Попытаемся дождаться завершения задания по отправке запроса перед тем, как возвращать результат:
                await Task.WhenAny(senderTask.ContinueWith(_ => {}), Task.Delay(RequestAbortTimeout)).ConfigureAwait(false);

                if (!senderTask.IsCompleted)
                    LogFailedToWaitForRequestAbort();

                return ResponseFactory.BuildResponse(ResponseCode.RequestTimeout, state);
            }
        }

        private async Task<Response> SendInternalAsync(Request request, HttpWebRequestState state, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(state.CancelRequest))
            {
                for (state.ConnectionAttempt = 1; state.ConnectionAttempt <= settings.ConnectionAttempts; state.ConnectionAttempt++)
                {
                    using (state)
                    {
                        if (state.RequestCancelled)
                            return new Response(ResponseCode.Canceled);

                        state.Reset();
                        state.Request = HttpWebRequestFactory.Create(request, state.TimeRemaining);

                        HttpActionStatus status;

                        // (iloktionov): Шаг 1 - отправить тело запроса, если оно имеется.
                        if (state.RequestCancelled)
                            return new Response(ResponseCode.Canceled);

                        if (request.Content != null)
                        {
                            status = await connectTimeLimiter.LimitConnectTime(SendRequestBodyAsync(request, state), request, state).ConfigureAwait(false);

                            if (status == HttpActionStatus.ConnectionFailure)
                                continue;

                            if (status != HttpActionStatus.Success)
                                return ResponseFactory.BuildFailureResponse(status, state);
                        }

                        // (iloktionov): Шаг 2 - получить ответ от сервера.
                        if (state.RequestCancelled)
                            return new Response(ResponseCode.Canceled);

                        status = request.Content != null 
                            ? await GetResponseAsync(request, state).ConfigureAwait(false) 
                            : await connectTimeLimiter.LimitConnectTime(GetResponseAsync(request, state), request, state).ConfigureAwait(false);

                        if (status == HttpActionStatus.ConnectionFailure)
                            continue;

                        if (status != HttpActionStatus.Success)
                            return ResponseFactory.BuildFailureResponse(status, state);

                        // TODO(iloktionov): ...
                    }
                }
            }

            throw new NotImplementedException();
        }

        private async Task<HttpActionStatus> SendRequestBodyAsync(Request request, HttpWebRequestState state)
        {
            try
            {
                state.RequestStream = await state.Request.GetRequestStreamAsync().ConfigureAwait(false);
            }
            catch (WebException error)
            {
                return HandleWebException(request, state, error);
            }
            catch (Exception error)
            {
                LogUnknownException(error);
                return HttpActionStatus.UnknownFailure;
            }

            try
            {
                await state.RequestStream.WriteAsync(request.Content.Buffer, request.Content.Offset, request.Content.Length);
                state.CloseRequestStream();
            }
            catch (Exception error)
            {
                if (IsCancellationException(error))
                    return HttpActionStatus.RequestCanceled;

                LogSendBodyFailure(request, error);
                return HttpActionStatus.SendFailure;
            }

            return HttpActionStatus.Success;
        }

        private async Task<HttpActionStatus> GetResponseAsync(Request request, HttpWebRequestState state)
        {
            try
            {
                state.Response = (HttpWebResponse) await state.Request.GetResponseAsync().ConfigureAwait(false);
                state.ResponseStream = state.Response.GetResponseStream();
                return HttpActionStatus.Success;
            }
            catch (WebException error)
            {
                var status = HandleWebException(request, state, error);
                // (iloktionov): HttpWebRequest реагирует на коды ответа вроде 404 или 500 исключением со статусом ProtocolError.
                if (status == HttpActionStatus.ProtocolError)
                {
                    state.Response = (HttpWebResponse)error.Response;
                    state.ResponseStream = state.Response.GetResponseStream();
                    return HttpActionStatus.Success;
                }
                return status;
            }
            catch (Exception error)
            {
                LogUnknownException(error);
                return HttpActionStatus.UnknownFailure;
            }
        }

        private HttpActionStatus HandleWebException(Request request, HttpWebRequestState state, WebException error)
        {
            switch (error.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.PipelineFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    LogConnectionFailure(request, error, state.ConnectionAttempt);
                    return HttpActionStatus.ConnectionFailure;
                case WebExceptionStatus.SendFailure:
                    LogWebException(error);
                    return HttpActionStatus.SendFailure;
                case WebExceptionStatus.ReceiveFailure:
                    LogWebException(error);
                    return HttpActionStatus.ReceiveFailure;
                case WebExceptionStatus.RequestCanceled: return HttpActionStatus.RequestCanceled;
                case WebExceptionStatus.Timeout: return HttpActionStatus.Timeout;
                case WebExceptionStatus.ProtocolError: return HttpActionStatus.ProtocolError;
                default:
                    LogWebException(error);
                    return HttpActionStatus.UnknownFailure;
            }
        }

        private static bool IsCancellationException(Exception error)
        {
            return error is OperationCanceledException || (error as WebException)?.Status == WebExceptionStatus.RequestCanceled;
        }

        #region Logging

        private void LogRequestTimeout(Request request, TimeSpan timeout)
        {
            log.Error($"Request timed out. Target = {request.Url.Authority}. Timeout = {timeout.TotalSeconds:0.000} sec.");
        }

        private void LogConnectionFailure(Request request, WebException error, int attempt)
        {
            log.Error($"Connection failure. Target = {request.Url.Authority}. Attempt = {attempt}/{settings.ConnectionAttempts}. Status = {error.Status}.", error.InnerException ?? error);
        }

        private void LogWebException(WebException error)
        {
            log.Error($"Error in sending request. Status = {error.Status}.", error.InnerException ?? error);
        }

        private void LogUnknownException(Exception error)
        {
            log.Error("Unknown error in sending request.", error);
        }

        private void LogSendBodyFailure(Request request, Exception error)
        {
            log.Error("Error in sending request body to " + request.Url.Authority, error);
        }

        private void LogReceiveBodyFailure(Request request, Exception error)
        {
            log.Error("Error in receiving request body from " + request.Url.Authority, error);
        }

        private void LogFailedToWaitForRequestAbort()
        {
            log.Warn($"Timed out request was aborted but did not complete in {RequestAbortTimeout}.");
        } 

        #endregion
    }
}
