//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Documents.Rntbd
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
#if COSMOSCLIENT
    using Microsoft.Azure.Cosmos.Rntbd;
    using Microsoft.Azure.Documents.FaultInjection;
#endif

#if NETSTANDARD15 || NETSTANDARD16
    using Trace = Microsoft.Azure.Documents.Trace;
#endif

    using ResponsePool = RntbdConstants.RntbdEntityPool<RntbdConstants.Response, RntbdConstants.ResponseIdentifiers>;

    // Dispatcher encapsulates the state and logic needed to dispatch multiple requests through
    // a single connection.
    internal sealed class Dispatcher : IDisposable
    {
        // Connection is thread-safe for sending.
        // Receiving is done only from the receive loop.
        // Initialization and disposal are not thread safe. Guard these
        // operations with connectionLock.
        private readonly Connection connection;
        private readonly UserAgentContainer userAgent;
        private readonly Uri serverUri;
        private readonly IConnectionStateListener connectionStateListener;
        // All individual operations on CancellationTokenSource are thread safe.
        // When examining IsCancellationRequested in order to decide whether to cancel,
        // guard the operation with connectionLock.
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly TimerPool idleTimerPool;
        private readonly bool enableChannelMultiplexing;

        private bool disposed = false;

        private ServerProperties serverProperties = null;

        private int nextRequestId = 0;

        // Acquire after connectionLock.
        private readonly object callLock = new object();
        private Task receiveTask = null; // Guarded by callLock.
        // Guarded by callLock.
        private readonly Dictionary<uint, CallInfo> calls = new Dictionary<uint, CallInfo>();

        // Guarded by callLock.
        // The call map can become frozen if the underlying connection becomes
        // unusable. This can happen if initialization fails, either the send
        // stream or the receive stream are broken, the receive loop fails etc.
        private bool callsAllowed = true;

        // lock used to guard underlying tcp connection state, which is a state level higher than callLock
        // Acquire before callLock
        private readonly object connectionLock = new object();
        private PooledTimer idleTimer; // Guarded by connectionLock
        private Task idleTimerTask;  // Guarded by connectionLock

        private readonly RntbdServerErrorInjector serverErrorInjector;

        public Dispatcher(
            Uri serverUri,
            UserAgentContainer userAgent,
            IConnectionStateListener connectionStateListener,
            string hostNameCertificateOverride,
            TimeSpan receiveHangDetectionTime,
            TimeSpan sendHangDetectionTime,
            TimerPool idleTimerPool,
            TimeSpan idleTimeout,
            bool enableChannelMultiplexing,
            MemoryStreamPool memoryStreamPool,
            RemoteCertificateValidationCallback remoteCertificateValidationCallback,
            Func<string, Task<IPAddress>> dnsResolutionFunction,
            RntbdServerErrorInjector serverErrorInjector)
        {
            this.connection = new Connection(
                serverUri, hostNameCertificateOverride,
                receiveHangDetectionTime, sendHangDetectionTime,
                idleTimeout,
                memoryStreamPool,
                remoteCertificateValidationCallback,
                dnsResolutionFunction);
            this.userAgent = userAgent;
            this.connectionStateListener = connectionStateListener;
            this.serverUri = serverUri;
            this.idleTimerPool = idleTimerPool;
            this.enableChannelMultiplexing = enableChannelMultiplexing;
            this.serverErrorInjector = serverErrorInjector;
        }

        #region Test hook.

        internal event Action TestOnConnectionClosed;
        internal bool TestIsIdle
        {
            get
            {
                lock (this.connectionLock)
                {
                    if (this.connection.Disposed)
                    {
                        return true;
                    }
                    TimeSpan ignoredTimeToIdle;
                    return !this.connection.IsActive(out ignoredTimeToIdle);
                }
            }
        }

        #endregion

        public bool Healthy
        {
            get
            {
                this.ThrowIfDisposed();

                if (this.cancellation.IsCancellationRequested)
                {
                    return false;
                }

                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    if (!this.callsAllowed)
                    {
                        return false;
                    }
                }

                bool healthy;
                try
                {
                    healthy = this.connection.Healthy;
                }
                catch (ObjectDisposedException)
                {
                    // Connection.Healthy shouldn't be called while the connection is being disposed, or after.
                    // To avoid additional locking, handle the exception here and store the outcome.
                    DefaultTrace.TraceWarning(
                        "RNTBD Dispatcher {0}: ObjectDisposedException from Connection.Healthy",
                        this);
                    healthy = false;
                }

                if (healthy)
                {
                    return true;
                }

                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    this.callsAllowed = false;
                }
                return false;
            }
        }

        public async Task OpenAsync(ChannelOpenArguments args)
        {
            this.ThrowIfDisposed();
            try
            {
                Debug.Assert(this.connection != null);
                await this.connection.OpenAsync(args);
                await this.NegotiateRntbdContextAsync(args);

                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    Debug.Assert(this.receiveTask == null);
                    // The background receive loop and its failure continuation
                    // task should use a task scheduler internal to the Cosmos DB
                    // client. In any case, they should avoid using the current
                    // task scheduler, because some components use task schedulers
                    // for accounting, others to control and suspend task execution.
                    // In these cases, it does not make sense for the current
                    // accounting entity to get charged for the background receive
                    // loop in perpetuity, and there's a risk the task might get
                    // suspended and never resumed.
                    this.receiveTask = Task.Factory.StartNew(
                        async delegate
                        {
                            await this.ReceiveLoopAsync();
                        },
                        this.cancellation.Token,
                        TaskCreationOptions.LongRunning |
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).Unwrap();
                    _ = this.receiveTask.ContinueWith(completedTask =>
                    {
                        Debug.Assert(completedTask.IsFaulted);
                        Debug.Assert(this.serverUri != null);
                        Debug.Assert(completedTask.Exception != null);
                        DefaultTrace.TraceWarning(
                            "RNTBD Dispatcher.ReceiveLoopAsync failed. Consuming the task " +
                            "exception asynchronously. Dispatcher: {0}. Exception: {1}",
                            this, completedTask.Exception?.InnerException);
                    },
                    default(CancellationToken),
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                }

                if (this.idleTimerPool != null)
                {
                    // idle timeout is enabled
                    this.StartIdleTimer();
                }
            }
            catch (DocumentClientException)
            {
                this.DisallowInitialCalls();
                throw;
            }
            catch (TransportException)
            {
                this.DisallowInitialCalls();
                throw;
            }
        }

        public sealed class PrepareCallResult : IDisposable
        {
            private bool disposed = false;

            public PrepareCallResult(uint requestId, Uri uri, TransportSerialization.SerializedRequest serializedRequest)
            {
                this.RequestId = requestId;
                this.Uri = uri;
                this.SerializedRequest = serializedRequest;
            }

            public uint RequestId { get; private set; }

            public TransportSerialization.SerializedRequest SerializedRequest { get; }
            public Uri Uri { get; private set; }

            /// <inheritdoc />
            public void Dispose()
            {
                if (!this.disposed)
                {
                    this.SerializedRequest.Dispose();
                    this.disposed = true;
                }
            }
        }

        // PrepareCall assigns a request ID to the request, serializes it, and
        // returns the result. The caller must treat PrepareCallResult as an
        // opaque handle.
        public PrepareCallResult PrepareCall(DocumentServiceRequest request, 
                                             TransportAddressUri physicalAddress,
                                             ResourceOperation resourceOperation, 
                                             Guid activityId, 
                                             TransportRequestStats transportRequestStats)
        {
            uint requestId = unchecked((uint) Interlocked.Increment(ref this.nextRequestId));
            string requestIdString = requestId.ToString(CultureInfo.InvariantCulture);

            int headerSize;
            int? bodySize;
            TransportSerialization.SerializedRequest serializedRequest = TransportSerialization.BuildRequest(
                request,
                physicalAddress.PathAndQuery,
                resourceOperation,
                activityId,
                this.connection.BufferProvider,
                requestIdString,
                out headerSize, 
                out bodySize);

            transportRequestStats.RequestBodySizeInBytes = bodySize;
            transportRequestStats.RequestSizeInBytes = serializedRequest.RequestSize;

            return new PrepareCallResult(requestId, physicalAddress.Uri, serializedRequest);
        }

        public async Task<StoreResponse> CallAsync(ChannelCallArguments args, TransportRequestStats transportRequestStats)
        {
            this.ThrowIfDisposed();
            // The current task scheduler must be used for correctness and to
            // track per tenant physical charges for the compute gateway. 
            using (CallInfo callInfo = new CallInfo(
                args.CommonArguments.ActivityId,
                args.PreparedCall.Uri,
                TaskScheduler.Current,
                transportRequestStats))
            {
                uint requestId = args.PreparedCall.RequestId;
                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    transportRequestStats.NumberOfInflightRequestsInConnection = this.calls.Count;
                    if (!this.callsAllowed)
                    {
                        Debug.Assert(args.CommonArguments.UserPayload);
                        throw new TransportException(
                            TransportErrorCode.ChannelMultiplexerClosed, null,
                            args.CommonArguments.ActivityId, args.PreparedCall.Uri,
                            this.ToString(), args.CommonArguments.UserPayload,
                            args.CommonArguments.PayloadSent);
                    }
                    this.calls.Add(requestId, callInfo);
                }
                try
                {
                    if (this.serverErrorInjector != null)
                    {
                        if (this.serverErrorInjector.InjectRntbdServerResponseError(args, transportRequestStats))
                        {
                            transportRequestStats.RecordState(TransportRequestStats.RequestStage.Sent);
                            if (transportRequestStats.FaultInjectionServerErrorType == FaultInjectionServerErrorType.TIMEOUT)
                            {
                                callInfo.SendFailed();
                                throw transportRequestStats.FaultInjectionException;
                            }
                            else
                            {
                                return Task.FromResult(transportRequestStats.FaultInjectionStoreResponse).Result;
                            }                                                      
                        }

                        if (this.serverErrorInjector.InjectRntbdServerResponseDelay(args, transportRequestStats))
                        {
                            try
                            {
                                await this.connection.WriteRequestWithResponseDelayAsync(
                                    args.CommonArguments,
                                    args.PreparedCall.SerializedRequest,
                                    transportRequestStats);
                            }
                            catch (Exception e)
                            {
                                callInfo.SendFailed();
                                throw new TransportException(
                                    TransportErrorCode.SendFailed, e,
                                    args.CommonArguments.ActivityId, args.PreparedCall.Uri,
                                    this.ToString(), args.CommonArguments.UserPayload,
                                    args.CommonArguments.PayloadSent);
                            }
                        }
                    }
                    try
                    {
                        await this.connection.WriteRequestAsync(
                            args.CommonArguments,
                            args.PreparedCall.SerializedRequest,
                            transportRequestStats);
                        transportRequestStats.RecordState(TransportRequestStats.RequestStage.Sent);
                    }
                    catch (Exception e)
                    {
                        callInfo.SendFailed();
                        throw new TransportException(
                            TransportErrorCode.SendFailed, e,
                            args.CommonArguments.ActivityId, args.PreparedCall.Uri,
                            this.ToString(), args.CommonArguments.UserPayload,
                            args.CommonArguments.PayloadSent);
                    }
                    // Do not add any code after the end of the previous block.
                    // Anything that needs to execute before ReadResponseAsync must
                    // be in the try block, so that no exceptions are missed.
                    return await callInfo.ReadResponseAsync(args);
                }
                catch (DocumentClientException)
                {
                    this.DisallowRuntimeCalls();
                    throw;
                }
                catch (TransportException)
                {
                    this.DisallowRuntimeCalls();
                    throw;
                }
                finally
                {
                    this.RemoveCall(requestId);
                }
            }
        }

        public void CancelCall(PrepareCallResult preparedCall)
        {
            this.ThrowIfDisposed();
            CallInfo call = this.RemoveCall(preparedCall.RequestId);
            if (call != null)
            {
                call.Cancel();
            }
        }

        public override string ToString()
        {
            return this.connection.ToString();
        }

        public void Dispose()
        {
            this.ThrowIfDisposed();
            this.disposed = true;

            DefaultTrace.TraceInformation("Disposing RNTBD Dispatcher {0}", this);

            Task idleTimerTaskCopy = null;
            Debug.Assert(!Monitor.IsEntered(this.connectionLock));
            lock (this.connectionLock)
            {
                this.StartConnectionShutdown();
                idleTimerTaskCopy = this.StopIdleTimer();
            }

            this.WaitTask(idleTimerTaskCopy, "idle timer");

            Task receiveTaskCopy = null;
            Debug.Assert(!Monitor.IsEntered(this.connectionLock));
            lock (this.connectionLock)
            {
                Debug.Assert(this.idleTimer == null);
                Debug.Assert(this.idleTimerTask == null);

                receiveTaskCopy = this.CloseConnection();
            }

            this.WaitTask(receiveTaskCopy, "receive loop");

            DefaultTrace.TraceInformation("RNTBD Dispatcher {0} is disposed", this);
        }

        private void StartIdleTimer()
        {
            DefaultTrace.TraceInformation("RNTBD idle connection monitor: Timer is starting...");

            TimeSpan timeToIdle = TimeSpan.MinValue;
            bool scheduled = false;
            try
            {
                Debug.Assert(!Monitor.IsEntered(this.connectionLock));
                lock (this.connectionLock)
                {
                    bool active = this.connection.IsActive(out timeToIdle);
                    Debug.Assert(active);
                    if (!active)
                    {
                        // This would be rather unexpected.
                        DefaultTrace.TraceCritical("RNTBD Dispatcher {0}: New connection already idle.", this);
                        return;
                    }

                    this.ScheduleIdleTimer(timeToIdle);
                    scheduled = true;
                }
            }
            finally
            {
                if (scheduled)
                {
                    DefaultTrace.TraceInformation(
                        "RNTBD idle connection monitor {0}: Timer is scheduled to fire {1} seconds later at {2}.",
                        this, timeToIdle.TotalSeconds, DateTime.UtcNow + timeToIdle);
                }
                else
                {
                    DefaultTrace.TraceInformation("RNTBD idle connection monitor {0}: Timer is not scheduled.", this);
                }
            }
        }

        private void OnIdleTimer(Task precedentTask)
        {
            Task receiveTaskCopy = null;

            Debug.Assert(!Monitor.IsEntered(this.connectionLock));
            lock (this.connectionLock)
            {
                if (this.cancellation.IsCancellationRequested)
                {
                    return;
                }

                Debug.Assert(!this.connection.Disposed);
                TimeSpan timeToIdle;
                bool active = this.connection.IsActive(out timeToIdle);
                if (active)
                {
                    this.ScheduleIdleTimer(timeToIdle);
                    return;
                }

                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    if (this.calls.Count > 0)
                    {
                        DefaultTrace.TraceCritical(
                            "RNTBD Dispatcher {0}: Looks idle but still has {1} pending requests",
                            this, this.calls.Count);
                        active = true;
                    }
                    else
                    {
                        this.callsAllowed = false;
                    }
                }

                if (active)
                {
                    this.ScheduleIdleTimer(timeToIdle);
                    return;
                }

                this.idleTimer = null;
                this.idleTimerTask = null;

                this.StartConnectionShutdown();
                receiveTaskCopy = this.CloseConnection();
            }

            this.WaitTask(receiveTaskCopy, "receive loop");
        }

        // this.connectionLock must be held.
        private void ScheduleIdleTimer(TimeSpan timeToIdle)
        {
            Debug.Assert(Monitor.IsEntered(this.connectionLock));
            this.idleTimer = this.idleTimerPool.GetPooledTimer((int)timeToIdle.TotalSeconds);
            this.idleTimerTask = this.idleTimer.StartTimerAsync().ContinueWith(this.OnIdleTimer, TaskContinuationOptions.OnlyOnRanToCompletion);
            this.idleTimerTask.ContinueWith(
                failedTask =>
                {
                    DefaultTrace.TraceWarning(
                        "RNTBD Dispatcher {0} idle timer callback failed: {1}",
                        this, failedTask.Exception?.InnerException);
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // this.connectionLock must be held.
        private void StartConnectionShutdown()
        {
            Debug.Assert(Monitor.IsEntered(this.connectionLock));
            if (this.cancellation.IsCancellationRequested)
            {
                return;
            }
            try
            {
                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    this.callsAllowed = false;
                }

                this.cancellation.Cancel();
            }
            catch (AggregateException e)
            {
                DefaultTrace.TraceWarning(
                    "RNTBD Dispatcher {0}: Registered cancellation callbacks failed: {1}",
                    this, e);
                // Deliberately ignoring the exception.
            }
        }

        // this.connectionLock must be held.
        private Task StopIdleTimer()
        {
            Task idleTimerTaskCopy = null;
            Debug.Assert(Monitor.IsEntered(this.connectionLock));
            if (this.idleTimer != null)
            {
                if (this.idleTimer.CancelTimer())
                {
                    // Dispose() won the race and the timer was cancelled.
                    this.idleTimer = null;
                    this.idleTimerTask = null;
                }
                else
                {
                    idleTimerTaskCopy = this.idleTimerTask;
                }
            }
            return idleTimerTaskCopy;
        }

        // this.connectionLock must be held.
        private Task CloseConnection()
        {
            Task receiveTaskCopy = null;
            Debug.Assert(Monitor.IsEntered(this.connectionLock));
            if (!this.connection.Disposed)
            {
                Debug.Assert(!Monitor.IsEntered(this.callLock));
                lock (this.callLock)
                {
                    receiveTaskCopy = this.receiveTask;
                }

                this.connection.Dispose();
                this.TestOnConnectionClosed?.Invoke();
            }
            return receiveTaskCopy;
        }

        private void WaitTask(Task t, string description)
        {
            if (t == null)
            {
                return;
            }
            try
            {
                // Don't hold locks while blocking this thread
                Debug.Assert(!Monitor.IsEntered(this.callLock));
                Debug.Assert(!Monitor.IsEntered(this.connectionLock));
                t.Wait();
            }
            catch (Exception e)
            {
                DefaultTrace.TraceWarning(
                    "RNTBD Dispatcher {0}: Parallel task failed: {1}. Exception: {2}",
                    this, description, e);
                // Intentionally swallowing the exception. The caller can't
                // do anything useful with it.
            }
        }

        private void ThrowIfDisposed()
        {
            if (this.disposed)
            {
                Debug.Assert(this.serverUri != null);
                throw new ObjectDisposedException(
                    string.Format("{0}:{1}", nameof(Dispatcher), this.serverUri));
            }
        }

        private async Task NegotiateRntbdContextAsync(ChannelOpenArguments args)
        {
            byte[] contextMessage = TransportSerialization.BuildContextRequest(
                args.CommonArguments.ActivityId, this.userAgent, args.CallerId, this.enableChannelMultiplexing);

            await this.connection.WriteRequestAsync(
                args.CommonArguments, 
                new TransportSerialization.SerializedRequest(new BufferProvider.DisposableBuffer(contextMessage), requestBody: null),
                transportRequestStats: null);

            // Read the response.
            using Connection.ResponseMetadata responseMd =
                await this.connection.ReadResponseMetadataAsync(args.CommonArguments);

            // Full header and metadata are read now. Parse out more fields and handle them.
            StatusCodes status = (StatusCodes) BitConverter.ToUInt32(responseMd.Header.Array, 4);
            byte[] responseActivityIdBytes = new byte[16];
            Buffer.BlockCopy(responseMd.Header.Array, 8, responseActivityIdBytes, 0, 16);
            // Server should just be echoing back the ActivityId from the connection request, but retrieve it 
            // from the wire and use it from here on, to be absolutely certain we have the same ActivityId 
            // the server is using
            Guid activityId = new Guid(responseActivityIdBytes);
            Trace.CorrelationManager.ActivityId = activityId;

            BytesDeserializer deserializer = new BytesDeserializer(responseMd.Metadata.Array, responseMd.Metadata.Count);
            RntbdConstants.ConnectionContextResponse response = new RntbdConstants.ConnectionContextResponse();
            response.ParseFrom(ref deserializer);
            string serverAgent = BytesSerializer.GetStringFromBytes(response.serverAgent.value.valueBytes);
            string serverVersion = BytesSerializer.GetStringFromBytes(response.serverVersion.value.valueBytes);
            Debug.Assert(this.serverProperties == null);
            this.serverProperties = new ServerProperties(serverAgent, serverVersion);

            if ((UInt32)status < 200 || (UInt32)status >= 400)
            {
                Debug.Assert(args.CommonArguments.UserPayload == false);

                using (MemoryStream errorResponseStream = await this.connection.ReadResponseBodyAsync(
                    new ChannelCommonArguments(activityId,
                        TransportErrorCode.TransportNegotiationTimeout,
                        args.CommonArguments.UserPayload)))
                {
                    Error error = Resource.LoadFrom<Error>(errorResponseStream);

                    DocumentClientException exception = new DocumentClientException(
                        string.Format(CultureInfo.CurrentUICulture,
                            RMResources.ExceptionMessage,
                            error.ToString()),
                        null,
                        (HttpStatusCode)status,
                        this.connection.ServerUri);

                    if (response.clientVersion.isPresent)
                    {
                        exception.Headers.Add("RequiredClientVersion",
                            BytesSerializer.GetStringFromBytes(response.clientVersion.value.valueBytes));
                    }

                    if (response.protocolVersion.isPresent)
                    {
                        exception.Headers.Add("RequiredProtocolVersion",
                            response.protocolVersion.value.valueULong.ToString());
                    }

                    if (response.serverAgent.isPresent)
                    {
                        exception.Headers.Add("ServerAgent",
                            BytesSerializer.GetStringFromBytes(response.serverAgent.value.valueBytes));
                    }

                    if (response.serverVersion.isPresent)
                    {
                        exception.Headers.Add(
                            HttpConstants.HttpHeaders.ServerVersion,
                            BytesSerializer.GetStringFromBytes(response.serverVersion.value.valueBytes));
                    }

                    throw exception;
                }
            }

            args.OpenTimeline.RecordRntbdHandshakeFinishTime();
        }

        private async Task ReceiveLoopAsync()
        {
            CancellationToken cancellationToken = this.cancellation.Token;
            ChannelCommonArguments args = new ChannelCommonArguments(
                Guid.Empty, TransportErrorCode.ReceiveTimeout, true);
            ResponsePool.EntityOwner response = default;
            Connection.ResponseMetadata responseMd = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    args.ActivityId = Guid.Empty;
                    response = ResponsePool.Instance.Get();
                    responseMd = await this.connection.ReadResponseMetadataAsync(args);
                    ArraySegment<byte> metadata = responseMd.Metadata;

                    TransportSerialization.RntbdHeader header =
                        TransportSerialization.DecodeRntbdHeader(responseMd.Header.Array);

                    args.ActivityId = header.ActivityId;
                    BytesDeserializer deserializer = new BytesDeserializer(metadata.Array, metadata.Count);
                    Debug.Assert(response.Entity != null);
                    response.Entity.ParseFrom(ref deserializer);

                    MemoryStream bodyStream = null;
                    if (response.Entity.payloadPresent.value.valueByte != (byte) 0x00)
                    {
                        bodyStream = await this.connection.ReadResponseBodyAsync(args);
                    }

                    this.DispatchRntbdResponse(responseMd, response, header, bodyStream);
                    responseMd = null;
                }
                this.DispatchCancellation();
            }
            catch (OperationCanceledException)
            {
                response.Dispose();
                responseMd?.Dispose();
                this.DispatchCancellation();
            }
            catch (ObjectDisposedException)
            {
                response.Dispose();
                responseMd?.Dispose();
                this.DispatchCancellation();
            }
            catch (Exception e)
            {
                response.Dispose();
                responseMd?.Dispose();
                this.DispatchChannelFailureException(e);
            }

#if DEBUG
            lock (this.callLock)
            {
                Debug.Assert(!this.callsAllowed);
                Debug.Assert(this.calls.Count == 0);
            }
#endif
        }

        private Dictionary<uint, CallInfo> StopCalls()
        {
            Dictionary<uint, CallInfo> clonedCalls;
            Debug.Assert(!Monitor.IsEntered(this.callLock));
            lock (this.callLock)
            {
                clonedCalls = new Dictionary<uint, CallInfo>(this.calls);
                this.calls.Clear();
                this.callsAllowed = false;
            }
            return clonedCalls;
        }

        private void DispatchRntbdResponse(
            Connection.ResponseMetadata responseMd,
            ResponsePool.EntityOwner rntbdResponse,
            TransportSerialization.RntbdHeader responseHeader,
            MemoryStream responseBody)
        {
            if (!rntbdResponse.Entity.transportRequestID.isPresent ||
                (rntbdResponse.Entity.transportRequestID.GetTokenType() != RntbdTokenTypes.ULong))
            {
                responseBody?.Dispose();
                rntbdResponse.Dispose();
                responseMd.Dispose();
                throw TransportExceptions.GetInternalServerErrorException(
                    this.serverUri,
                    RMResources.ServerResponseTransportRequestIdMissingError);
            }

            CallInfo call = this.RemoveCall(rntbdResponse.Entity.transportRequestID.value.valueULong);
            if (call != null)
            {
                Debug.Assert(this.serverProperties != null);
                Debug.Assert(this.serverProperties.Version != null);
                call.TransportRequestStats.RecordState(TransportRequestStats.RequestStage.Received);
                call.TransportRequestStats.ResponseMetadataSizeInBytes = responseMd.Metadata.Count;
                call.TransportRequestStats.ResponseBodySizeInBytes = responseBody?.Length;
                call.SetResponse(responseMd, rntbdResponse, responseHeader, responseBody, this.serverProperties.Version);
            }
            else
            {
                responseBody?.Dispose();
                responseMd.Dispose();
                rntbdResponse.Dispose();
            }
        }

        private void DispatchChannelFailureException(Exception ex)
        {
            Dictionary<uint, CallInfo> clonedCalls = this.StopCalls();
            foreach (KeyValuePair<uint, CallInfo> entry in clonedCalls)
            {
                CallInfo call = entry.Value;
                Debug.Assert(call != null);
                call.SetConnectionBrokenException(ex, this.ToString());
            }

            // If there are no pending calls on this channel and a subscriber is interested
            // in connection events, examine the exception and raise the event appropriately.
            if ((clonedCalls.Count > 0) || (this.connectionStateListener == null))
            {
                // If any calls are pending, the event is unnecessary because callers will get
                // TransportException. If this.connectionStateListener is null, the point is moot.
                return;
            }
            TransportException transportException = ex as TransportException;
            if (transportException == null)
            {
                // The client transport stack catches SocketException and IOException and wraps them
                // in TransportException as appropriate. Other exception types may also be thrown.
                // In some cases, it may be reasonable to catch those exceptions and wrap them in
                // TransportException.
                // In other cases, it may be reasonable to handle additional exception types here.
                DefaultTrace.TraceWarning(
                    "Not a TransportException. Will not raise the connection state change event: {0}", ex);
                return;
            }

            // Copy a bunch of values locally to avoid capturing the this pointer.
            ConnectionEvent connectionEvent;
            switch (transportException.ErrorCode)
            {
                case TransportErrorCode.ReceiveStreamClosed:
                    connectionEvent = ConnectionEvent.ReadEof;
                    break;

                case TransportErrorCode.ReceiveFailed:
                    connectionEvent = ConnectionEvent.ReadFailure;
                    break;

                default:
                    DefaultTrace.TraceWarning(
                        "Will not raise the connection state change event for TransportException error code {0}. Exception: {1}",
                        transportException.ErrorCode.ToString(), transportException.Message);
                    return;
            }
            IConnectionStateListener connectionStateListener = this.connectionStateListener;
            ServerKey serverKey = new ServerKey(this.serverUri);
            DateTime exceptionTime = transportException.Timestamp;
            // Run the event handler asynchronously and catch all exceptions.
            Task t = Task.Run(() =>
            {
                connectionStateListener.OnConnectionEvent(connectionEvent, exceptionTime, serverKey);
            });
            t.ContinueWith(failedTask =>
            {
                DefaultTrace.TraceError("OnConnectionEvent callback failed: {0}", failedTask.Exception?.InnerException);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void DispatchCancellation()
        {
            Dictionary<uint, CallInfo> clonedCalls = this.StopCalls();
            foreach (KeyValuePair<uint, CallInfo> entry in clonedCalls)
            {
                CallInfo call = entry.Value;
                Debug.Assert(call != null);
                call.Cancel();
            }
        }

        private CallInfo RemoveCall(uint requestId)
        {
            Debug.Assert(!Monitor.IsEntered(this.callLock));
            CallInfo callInfo = null;
            lock (this.callLock)
            {
                bool found = this.calls.TryGetValue(requestId, out callInfo);
                bool removed = this.calls.Remove(requestId);
                Debug.Assert(found == removed);
            }
            return callInfo;
        }

        // The only difference between DisallowInitialCalls and
        // DisallowRuntimeCalls is an extra debug assert. In debug mode,
        // DisallowInitialCalls ensures nobody got in yet.
        private void DisallowInitialCalls()
        {
            Debug.Assert(!Monitor.IsEntered(this.callLock));
            lock (this.callLock)
            {
                Debug.Assert(this.calls.Count == 0);
                this.callsAllowed = false;
            }
        }

        private void DisallowRuntimeCalls()
        {
            Debug.Assert(!Monitor.IsEntered(this.callLock));
            lock (this.callLock)
            {
                this.callsAllowed = false;
            }
        }

        internal sealed class CallInfo : IDisposable
        {
            private readonly TaskCompletionSource<StoreResponse> completion =
                new TaskCompletionSource<StoreResponse>();
            private readonly SemaphoreSlim sendComplete =
                new SemaphoreSlim(0);

            private readonly Guid activityId;
            private readonly Uri uri;
            private readonly TaskScheduler scheduler;

            private bool disposed = false;
            private readonly object stateLock = new object();
            private State state;

            public TransportRequestStats TransportRequestStats { get; }

            public CallInfo(Guid activityId, Uri uri, TaskScheduler scheduler, TransportRequestStats transportRequestStats)
            {
                Debug.Assert(activityId != Guid.Empty);
                Debug.Assert(uri != null);
                Debug.Assert(scheduler != null);
                Debug.Assert(transportRequestStats != null);

                this.activityId = activityId;
                this.uri = uri;
                this.scheduler = scheduler;
                this.TransportRequestStats = transportRequestStats;
            }

            public Task<StoreResponse> ReadResponseAsync(ChannelCallArguments args)
            {
                this.ThrowIfDisposed();
                // If execution got this far, sending the request succeeded.
                this.CompleteSend(State.Sent);
                args.CommonArguments.SetTimeoutCode(
                    TransportErrorCode.ReceiveTimeout);
                return this.completion.Task;
            }

            public void SendFailed()
            {
                this.ThrowIfDisposed();
                this.CompleteSend(State.SendFailed);
            }

            public void SetResponse(
                Connection.ResponseMetadata responseMd,
                ResponsePool.EntityOwner rntbdResponse,
                TransportSerialization.RntbdHeader responseHeader,
                MemoryStream responseBody,
                string serverVersion)
            {
                this.ThrowIfDisposed();
                // Call SetResult asynchronously. Otherwise, the tasks awaiting on
                // completionSource.Task will be continued on this thread. This is
                // undesirable for both correctness reasons (it can lead to
                // deadlock, because the receive task will not complete until this
                // function returns, which may not happen if a continued task tries
                // to dispose this object) and for performance reasons (SetResult
                // needs to be fire-and-forget, not synchronous and sequential).

                // .NET Framework 4.6 introduces
                // TaskCreationOptions.RunContinuationsAsynchronously. It should be
                // used when it becomes available.
                this.RunAsynchronously(() =>
                    {
                        Trace.CorrelationManager.ActivityId = this.activityId;
                        try
                        {
                            StoreResponse storeResponse = TransportSerialization.MakeStoreResponse(
                                responseHeader.Status,
                                responseHeader.ActivityId,
                                rntbdResponse.Entity,
                                responseBody,
                                serverVersion);
                            this.completion.SetResult(storeResponse);
                        }
                        catch (Exception e)
                        {
                            this.completion.SetException(e);
                            responseBody?.Dispose();
                        }
                        finally
                        {
                            rntbdResponse.Dispose();
                            responseMd.Dispose();
                        }
                    });
            }

            public void SetConnectionBrokenException(Exception inner, string sourceDescription)
            {
                this.ThrowIfDisposed();
                // Call SetException asynchronously.
                this.RunAsynchronously(
                    async delegate
                    {
                        Trace.CorrelationManager.ActivityId = this.activityId;
                        // When an API caller sends a request, it can get two
                        // exceptions concurrently, one from the inline SendAsync
                        // call, and one from the background receive loop doing
                        // ReceiveAsync. Avoid setting an exception on the
                        // completion object if sending the request already
                        // failed. The exception is not necessary, and the
                        // unused completion would trigger
                        // UnobservedTaskException upon garbage collection.
                        await this.sendComplete.WaitAsync();
                        lock (this.stateLock)
                        {
                            if (this.state != State.Sent)
                            {
                                return;
                            }
                        }
                        this.completion.SetException(
                            new TransportException(
                                TransportErrorCode.ConnectionBroken, inner,
                                this.activityId, this.uri, sourceDescription,
                                true, true));
                    });
            }

            public void Cancel()
            {
                this.ThrowIfDisposed();
                // Call SetCanceled asynchronously.
                this.RunAsynchronously(() =>
                    {
                        Trace.CorrelationManager.ActivityId = this.activityId;
                        this.completion.SetCanceled();
                    });
            }

            public void Dispose()
            {
                this.ThrowIfDisposed();
                this.disposed = true;
                this.sendComplete.Dispose();
            }

            private void ThrowIfDisposed()
            {
                if (this.disposed)
                {
                    throw new ObjectDisposedException(nameof(CallInfo));
                }
            }

            private void RunAsynchronously(Action action)
            {
                Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    this.scheduler).ContinueWith(
                    failedTask =>
                    {
                        DefaultTrace.TraceError(
                            "Unexpected: Rntbd asynchronous completion " +
                            "call failed. Consuming the task exception asynchronously. " +
                            "Exception: {0}", failedTask.Exception?.InnerException);
                    },
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            private void RunAsynchronously(Func<Task> action)
            {
                Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    this.scheduler).Unwrap().ContinueWith(
                    failedTask =>
                    {
                        DefaultTrace.TraceError(
                            "Unexpected: Rntbd asynchronous completion " +
                            "call failed. Consuming the task exception asynchronously. " +
                            "Exception: {0}", failedTask.Exception?.InnerException);
                    },
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            private void CompleteSend(State newState)
            {
                Debug.Assert(!Monitor.IsEntered(this.stateLock));
                lock (this.stateLock)
                {
                    if (this.state != State.New)
                    {
                        throw new InvalidOperationException(
                            "Send may only complete once");
                    }
                    Debug.Assert(this.state == State.New);
                    Debug.Assert(
                        newState == State.Sent ||
                        newState == State.SendFailed);
                    this.state = newState;
                    this.sendComplete.Release();
                }
            }

            private enum State
            {
                New,
                Sent,
                SendFailed,
            }
        }
    }
}
