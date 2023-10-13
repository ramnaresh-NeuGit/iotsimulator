// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using DotNetty.Buffers;
using DotNetty.Codecs.Mqtt;
using DotNetty.Codecs.Mqtt.Packets;
using DotNetty.Handlers.Logging;
using DotNetty.Handlers.Tls;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Microsoft.Azure.Devices.Client.Exceptions;
using Microsoft.Azure.Devices.Client.Extensions;
using Microsoft.Azure.Devices.Client.TransientFaultHandling;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;

#if NET5_0_OR_GREATER

using TaskCompletionSource = System.Threading.Tasks.TaskCompletionSource;

#else
using TaskCompletionSource = Microsoft.Azure.Devices.Shared.TaskCompletionSource;
#endif

namespace Microsoft.Azure.Devices.Client.Transport.Mqtt
{
    //
    // Note on ConfigureAwait: dotNetty is using a custom TaskScheduler that binds Tasks to the corresponding
    // EventLoop. To limit I/O to the EventLoopGroup and keep Netty semantics, we are going to ensure that the
    // task continuations are executed by this scheduler using ConfigureAwait(true).
    //
    // All awaited calls that happen within dotnetty's pipeline should be ConfigureAwait(true).
    //
    internal sealed class MqttTransportHandler : TransportHandler, IMqttIotHubEventHandler
    {
        private const int ProtocolGatewayPort = 8883;
        private const int MaxMessageSize = 256 * 1024;
        private const string ProcessorThreadCountVariableName = "MqttEventsProcessorThreadCount";

        // Topic names for receiving cloud-to-device messages.

        private const string DeviceBoundMessagesTopicFilter = "devices/{0}/messages/devicebound/#";
        private const string DeviceBoundMessagesTopicPrefix = "devices/{0}/messages/devicebound/";

        // Topic names for retrieving a device's twin properties.
        // The client first subscribes to "$iothub/twin/res/#", to receive the operation's responses.
        // It then sends an empty message to the topic "$iothub/twin/GET/?$rid={request id}, with a populated value for request Id.
        // The service then sends a response message containing the device twin data on topic "$iothub/twin/res/{status}/?$rid={request id}", using the same request Id as the request.

        private const string TwinResponseTopicFilter = "$iothub/twin/res/#";
        private const string TwinResponseTopicPrefix = "$iothub/twin/res/";
        private const string TwinGetTopic = "$iothub/twin/GET/?$rid={0}";
        private const string TwinResponseTopicPattern = @"\$iothub/twin/res/(\d+)/(\?.+)";

        // Topic name for updating device twin's reported properties.
        // The client first subscribes to "$iothub/twin/res/#", to receive the operation's responses.
        // The client then sends a message containing the twin update to "$iothub/twin/PATCH/properties/reported/?$rid={request id}", with a populated value for request Id.
        // The service then sends a response message containing the new ETag value for the reported properties collection on the topic "$iothub/twin/res/{status}/?$rid={request id}", using the same request Id as the request.
        private const string TwinPatchTopic = "$iothub/twin/PATCH/properties/reported/?$rid={0}";

        // Topic names for receiving twin desired property update notifications.

        private const string TwinPatchTopicFilter = "$iothub/twin/PATCH/properties/desired/#";
        private const string TwinPatchTopicPrefix = "$iothub/twin/PATCH/properties/desired/";

        // Topic name for responding to direct methods.
        // The client first subscribes to "$iothub/methods/POST/#".
        // The service sends method requests to the topic "$iothub/methods/POST/{method name}/?$rid={request id}".
        // The client responds to the direct method invocation by sending a message to the topic "$iothub/methods/res/{status}/?$rid={request id}", using the same request Id as the request.

        private const string MethodPostTopicFilter = "$iothub/methods/POST/#";
        private const string MethodPostTopicPrefix = "$iothub/methods/POST/";
        private const string MethodResponseTopic = "$iothub/methods/res/{0}/?$rid={1}";

        // Topic names for enabling events on Modules.

        private const string ReceiveEventMessagePatternFilter = "devices/{0}/modules/{1}/#";
        private const string ReceiveEventMessagePrefixPattern = "devices/{0}/modules/{1}/";

        private static readonly int s_generationPrefixLength = Guid.NewGuid().ToString().Length;
        private static readonly Lazy<IEventLoopGroup> s_eventLoopGroup = new Lazy<IEventLoopGroup>(GetEventLoopGroup);
        private static readonly TimeSpan s_regexTimeoutMilliseconds = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan s_defaultTwinTimeout = TimeSpan.FromSeconds(60);

        private readonly string _generationId = Guid.NewGuid().ToString();
        private readonly string _receiveEventMessageFilter;
        private readonly string _receiveEventMessagePrefix;
        private readonly string _deviceboundMessageFilter;
        private readonly string _deviceboundMessagePrefix;
        private readonly string _hostName;
        private readonly Func<IPAddress[], int, Task<IChannel>> _channelFactory;
        private readonly ConcurrentQueue<string> _completionQueue;
        private readonly MqttIotHubAdapterFactory _mqttIotHubAdapterFactory;
        private readonly QualityOfService _qosSendPacketToService;
        private readonly QualityOfService _qosReceivePacketFromService;
        private readonly bool _retainMessagesAcrossSessions;
        private readonly RetryPolicy _closeRetryPolicy;
        private readonly ConcurrentQueue<Message> _messageQueue;
        private readonly TaskCompletionSource _connectCompletion = new TaskCompletionSource();
        private readonly TaskCompletionSource _subscribeCompletionSource = new TaskCompletionSource();
        private readonly IWebProxy _webProxy;

        private SemaphoreSlim _deviceReceiveMessageSemaphore = new SemaphoreSlim(1, 1);
        private SemaphoreSlim _receivingSemaphore = new SemaphoreSlim(0);
        private CancellationTokenSource _disconnectAwaitersCancellationSource = new CancellationTokenSource();
        private readonly Regex _twinResponseTopicRegex = new Regex(TwinResponseTopicPattern, RegexOptions.Compiled, s_regexTimeoutMilliseconds);
        private readonly Func<MethodRequestInternal, Task> _methodListener;
        private readonly Action<TwinCollection> _onDesiredStatePatchListener;
        private readonly Func<string, Message, Task> _moduleMessageReceivedListener;
        private readonly Func<Message, Task> _deviceMessageReceivedListener;

        private bool _isDeviceReceiveMessageCallbackSet;
        private Func<Task> _cleanupFunc;
        private IChannel _channel;
        private ExceptionDispatchInfo _fatalException;
        private IPAddress[] _serverAddresses;
        private int _state = (int)TransportState.NotInitialized;
        private Action<Message> _twinResponseEvent;

        internal MqttTransportHandler(
            PipelineContext context,
            IotHubConnectionString iotHubConnectionString,
            MqttTransportSettings settings,
            Func<MethodRequestInternal, Task> onMethodCallback = null,
            Action<TwinCollection> onDesiredStatePatchReceivedCallback = null,
            Func<string, Message, Task> onModuleMessageReceivedCallback = null,
            Func<Message, Task> onDeviceMessageReceivedCallback = null)
            : this(context, iotHubConnectionString, settings, null)
        {
            _methodListener = onMethodCallback;
            _deviceMessageReceivedListener = onDeviceMessageReceivedCallback;
            _moduleMessageReceivedListener = onModuleMessageReceivedCallback;
            _onDesiredStatePatchListener = onDesiredStatePatchReceivedCallback;
        }

        internal MqttTransportHandler(
            PipelineContext context,
            IotHubConnectionString iotHubConnectionString,
            MqttTransportSettings settings,
            Func<IPAddress[], int, Task<IChannel>> channelFactory)
            : base(context, settings)
        {
            _mqttIotHubAdapterFactory = new MqttIotHubAdapterFactory(settings);
            _messageQueue = new ConcurrentQueue<Message>();
            _completionQueue = new ConcurrentQueue<string>();

            _serverAddresses = null; // this will be resolved asynchronously in OpenAsync
            _hostName = iotHubConnectionString.HostName;
            _receiveEventMessageFilter = string.Format(CultureInfo.InvariantCulture, ReceiveEventMessagePatternFilter, iotHubConnectionString.DeviceId, iotHubConnectionString.ModuleId);
            _receiveEventMessagePrefix = string.Format(CultureInfo.InvariantCulture, ReceiveEventMessagePrefixPattern, iotHubConnectionString.DeviceId, iotHubConnectionString.ModuleId);

            _deviceboundMessageFilter = string.Format(CultureInfo.InvariantCulture, DeviceBoundMessagesTopicFilter, iotHubConnectionString.DeviceId);
            _deviceboundMessagePrefix = string.Format(CultureInfo.InvariantCulture, DeviceBoundMessagesTopicPrefix, iotHubConnectionString.DeviceId);

            _qosSendPacketToService = settings.PublishToServerQoS;
            _qosReceivePacketFromService = settings.ReceivingQoS;

            // If the CleanSession flag is set to false, C2D messages will be retained across device sessions, i.e. the device
            // will receive the C2D messages that were sent to it while it was disconnected.
            // If the CleanSession flag is set to true, the device will receive only those C2D messages that were sent
            // after it had subscribed to the message topic.
            _retainMessagesAcrossSessions = !settings.CleanSession;

            _webProxy = settings.Proxy;

            if (channelFactory != null)
            {
                _channelFactory = channelFactory;
            }
            else
            {
                ClientOptions options = context.ClientOptions;
                _channelFactory = settings.GetTransportType() switch
                {
                    TransportType.Mqtt_Tcp_Only => CreateChannelFactory(iotHubConnectionString, settings, context.ProductInfo, options),
                    TransportType.Mqtt_WebSocket_Only => CreateWebSocketChannelFactory(iotHubConnectionString, settings, context.ProductInfo, options),
                    _ => throw new InvalidOperationException("Unsupported Transport Setting {0}".FormatInvariant(settings.GetTransportType())),
                };
            }

            _closeRetryPolicy = new RetryPolicy(new TransientErrorIgnoreStrategy(), 5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public TransportState State => (TransportState)Volatile.Read(ref _state);
        public override bool IsUsable => State != TransportState.Closed && State != TransportState.Error;
        public TimeSpan TwinTimeout { get; set; } = s_defaultTwinTimeout;

        #region Client operations

        public override async Task OpenAsync(TimeoutHelper timeoutHelper)
        {
            using var cts = new CancellationTokenSource(timeoutHelper.GetRemainingTime());
            await OpenAsync(cts.Token).ConfigureAwait(false);
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(this, cancellationToken, nameof(OpenAsync));

                cancellationToken.ThrowIfCancellationRequested();

                EnsureValidState(throwIfNotOpen: false);

                await OpenInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, cancellationToken, nameof(OpenAsync));
            }
        }

        public override async Task SendEventAsync(Message message, CancellationToken cancellationToken)
        {
            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(this, cancellationToken, nameof(SendEventAsync));

                cancellationToken.ThrowIfCancellationRequested();

                EnsureValidState();
                Debug.Assert(_channel != null);

                await _channel.WriteAndFlushAsync(message).ConfigureAwait(true);
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, cancellationToken, nameof(SendEventAsync));
            }
        }

        public override async Task SendEventAsync(IEnumerable<Message> messages, CancellationToken cancellationToken)
        {
            foreach (Message message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendEventAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task<Message> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_isDeviceReceiveMessageCallbackSet)
            {
                if (Logging.IsEnabled)
                    Logging.Error(this, "Callback handler set for receiving C2D messages; ReceiveAsync() will now always return null", nameof(ReceiveAsync));

                return null;
            }

            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(
                        this,
                        cancellationToken, $"ReceiveAsync() called with cancellation requested state of: {cancellationToken.IsCancellationRequested}",
                        $"{nameof(ReceiveAsync)}");

                cancellationToken.ThrowIfCancellationRequested();

                EnsureValidState();

                if (State != TransportState.Receiving)
                {
                    await SubscribeCloudToDeviceMessagesAsync().ConfigureAwait(false);
                }

                await WaitUntilC2dMessageArrivesAsync(cancellationToken).ConfigureAwait(false);
                return ProcessC2dMessage();
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(
                        this,
                        cancellationToken,
                        $"Exiting ReceiveAsync() with cancellation requested state of: {cancellationToken.IsCancellationRequested}",
                        $"{nameof(ReceiveAsync)}");
            }
        }

        public override async Task<Message> ReceiveAsync(TimeoutHelper timeoutHelper)
        {
            if (_isDeviceReceiveMessageCallbackSet)
            {
                if (Logging.IsEnabled)
                    Logging.Error(this, "Callback handler set for receiving C2D messages; ReceiveAsync() will now always return null", nameof(ReceiveAsync));

                return null;
            }

            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(this, timeoutHelper, $"Time remaining for ReceiveAsync(): {timeoutHelper.GetRemainingTime()}", $"{nameof(ReceiveAsync)}");

                EnsureValidState();

                if (State != TransportState.Receiving)
                {
                    await SubscribeCloudToDeviceMessagesAsync().ConfigureAwait(false);
                }

                TimeSpan timeout = timeoutHelper.GetRemainingTime();
                using var cts = new CancellationTokenSource(timeout);

                await WaitUntilC2dMessageArrivesAsync(cts.Token).ConfigureAwait(false);
                return ProcessC2dMessage();
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, timeoutHelper, $"Time remaining for ReceiveAsync(): {timeoutHelper.GetRemainingTime()}", $"{nameof(ReceiveAsync)}");
            }
        }

        private Message ProcessC2dMessage()
        {
            Message message = null;

            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(this, message, $"Will begin processing received C2D message, queue size={_messageQueue.Count}", nameof(ProcessC2dMessage));

                if (_messageQueue.TryDequeue(out message))
                {
                    if (_qosReceivePacketFromService == QualityOfService.AtLeastOnce)
                    {
                        _completionQueue.Enqueue(message.LockToken);
                    }

                    message.LockToken = _generationId + message.LockToken;
                }

                return message;
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, message, $"Processed received C2D message with Id={message?.MessageId}", nameof(ProcessC2dMessage));
            }
        }

        private async Task WaitUntilC2dMessageArrivesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken disconnectToken = _disconnectAwaitersCancellationSource.Token;
            EnsureValidState();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnectToken);

            // Wait until either of the linked cancellation tokens have been canceled.
            await _receivingSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }

        public override async Task CompleteAsync(string lockToken, CancellationToken cancellationToken)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, $"Completing a message with lockToken: {lockToken}", nameof(CompleteAsync));

            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            if (_qosReceivePacketFromService == QualityOfService.AtMostOnce)
            {
                throw new IotHubException("Complete is not allowed for QoS 0.", isTransient: false);
            }

            if (!lockToken.StartsWith(_generationId, StringComparison.InvariantCulture))
            {
                throw new IotHubException(
                    "Lock token is stale or never existed. The message will be redelivered. Please discard this lock token and do not retry the operation.",
                    isTransient: false);
            }

            if (_completionQueue.IsEmpty)
            {
                throw new IotHubException("Unknown lock token.", isTransient: false);
            }

            _completionQueue.TryDequeue(out string actualLockToken);
            if (lockToken.IndexOf(actualLockToken, s_generationPrefixLength, StringComparison.Ordinal) != s_generationPrefixLength ||
                lockToken.Length != actualLockToken.Length + s_generationPrefixLength)
            {
                throw new IotHubException(
                    $"Client must send PUBACK packets in the order in which the corresponding PUBLISH packets were received (QoS 1 messages) per [MQTT-4.6.0-2]. Expected lock token to end with: '{actualLockToken}'; actual lock token: '{lockToken}'.",
                    isTransient: false);
            }

            await _channel.WriteAndFlushAsync(actualLockToken).ConfigureAwait(true);

            if (Logging.IsEnabled)
                Logging.Exit(this, $"Completing a message with lockToken: {lockToken}", nameof(CompleteAsync));
        }

        public override Task AbandonAsync(string lockToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("MQTT protocol does not support this operation");
        }

        public override Task RejectAsync(string lockToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new NotSupportedException("MQTT protocol does not support this operation");
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(
                        this,
                        $"{nameof(DefaultDelegatingHandler)}.Disposed={_isDisposed}; disposing={disposing}",
                        $"{nameof(MqttTransportHandler)}.{nameof(Dispose)}");

                if (!_isDisposed)
                {
                    base.Dispose(disposing);
                    if (disposing)
                    {
                        if (TryStop())
                        {
                            CleanUpAsync().GetAwaiter().GetResult();
                        }

                        // Log the task completion source tasks' exceptions and avoid unobserved exceptions.
                        if (_connectCompletion.Task.Exception != null)
                        {
                            if (Logging.IsEnabled)
                                Logging.Error(this, $"{_connectCompletion} has exception {_connectCompletion.Task.Exception}", nameof(Dispose));
                        }
                        if (_subscribeCompletionSource.Task.Exception != null)
                        {
                            if (Logging.IsEnabled)
                                Logging.Error(this, $"{_subscribeCompletionSource} has exception {_subscribeCompletionSource.Task.Exception}", nameof(Dispose));
                        }

                        _disconnectAwaitersCancellationSource?.Dispose();
                        _disconnectAwaitersCancellationSource = null;

                        _receivingSemaphore?.Dispose();
                        _receivingSemaphore = null;

                        _deviceReceiveMessageSemaphore?.Dispose();
                        _deviceReceiveMessageSemaphore = null;

                        if (_channel is IDisposable disposableChannel)
                        {
                            disposableChannel.Dispose();
                            _channel = null;
                        }
                    }

                    // the _disposed flag is inherited from the base class DefaultDelegatingHandler and is finally set to null there.
                }
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(
                        this,
                        $"{nameof(DefaultDelegatingHandler)}.Disposed={_isDisposed}; disposing={disposing}",
                        $"{nameof(MqttTransportHandler)}.{nameof(Dispose)}");
            }
        }

        public override async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Logging.IsEnabled)
                    Logging.Enter(this, "", $"{nameof(MqttTransportHandler)}.{nameof(CloseAsync)}");

                cancellationToken.ThrowIfCancellationRequested();

                if (TryStop())
                {
                    OnTransportClosedGracefully();

                    await _closeRetryPolicy.RunWithRetryAsync(CleanUpImplAsync, cancellationToken).ConfigureAwait(true);
                }
                else if (State == TransportState.Error)
                {
                    _fatalException.Throw();
                }
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, "", $"{nameof(MqttTransportHandler)}.{nameof(CloseAsync)}");
            }
        }

        #endregion Client operations

        #region MQTT callbacks

        public void OnConnected()
        {
            if (TryStateTransition(TransportState.Opening, TransportState.Open))
            {
                _connectCompletion.TrySetResult();
            }
        }

        private async Task HandleIncomingTwinPatchAsync(Message message)
        {
            try
            {
                if (_onDesiredStatePatchListener != null)
                {
                    using var reader = new StreamReader(message.GetBodyStream(), System.Text.Encoding.UTF8);
                    string patch = reader.ReadToEnd();
                    TwinCollection props = JsonConvert.DeserializeObject<TwinCollection>(patch);
                    await Task.Run(() => _onDesiredStatePatchListener(props)).ConfigureAwait(false);
                    // Messages with QoS = 1 need to be Acknowledged otherwise it results in mismatched Ack to IoT Hub
                    // causing next message being replayed and all subsequent messages being queued.
                    await CompleteIncomingMessageAsync(message).ConfigureAwait(false);
                }
            }
            finally
            {
                message.Dispose();
            }
        }

        private async Task HandleIncomingMethodPostAsync(Message message)
        {
            try
            {
                string[] tokens = Regex.Split(message.MqttTopicName, "/", RegexOptions.Compiled, s_regexTimeoutMilliseconds);

                using var mr = new MethodRequestInternal(tokens[3], tokens[4].Substring(6), message.GetBodyStream(), CancellationToken.None);
                await Task.Run(() => _methodListener(mr)).ConfigureAwait(false);
                // Method with QoS = 1 need to be Acknowledged otherwise it results in mismatched Ack to IoT Hub
                // causing next message being replayed and all subsequent messages being queued.
                await CompleteIncomingMessageAsync(message).ConfigureAwait(false);
            }
            finally
            {
                message.Dispose();
            }
        }

        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The created message is handed to the user and the user application is in charge of disposing the message.")]
        private async Task HandleIncomingMessagesAsync()
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, "Process C2D message via callback", nameof(HandleIncomingMessagesAsync));

            Message message = ProcessC2dMessage();

            // We are intentionally not awaiting _deviceMessageReceivedListener callback.
            // This is a user-supplied callback that isn't required to be awaited by us. We can simply invoke it and continue.
            _ = _deviceMessageReceivedListener?.Invoke(message);
            await TaskHelpers.CompletedTask.ConfigureAwait(false);

            if (Logging.IsEnabled)
                Logging.Exit(this, "Process C2D message via callback", nameof(HandleIncomingMessagesAsync));
        }

        public async void OnMessageReceived(Message message)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, message, nameof(OnMessageReceived));

            // Added Try-Catch to avoid unknown thread exception
            // after running for more than 24 hours
            try
            {
                if ((State & TransportState.Open) == TransportState.Open)
                {
                    string topic = message.MqttTopicName;
                    if (Logging.IsEnabled)
                        Logging.Info(this, $"Received a message on topic: {topic}", nameof(OnMessageReceived));

                    if (topic.StartsWith(TwinResponseTopicPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _twinResponseEvent(message);
                        await CompleteIncomingMessageAsync(message).ConfigureAwait(false);
                    }
                    else if (topic.StartsWith(TwinPatchTopicPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleIncomingTwinPatchAsync(message).ConfigureAwait(false);
                    }
                    else if (topic.StartsWith(MethodPostTopicPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleIncomingMethodPostAsync(message).ConfigureAwait(false);
                    }
                    else if (topic.StartsWith(_receiveEventMessagePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleIncomingEventMessageAsync(message).ConfigureAwait(false);
                    }
                    else if (topic.StartsWith(_deviceboundMessagePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _messageQueue.Enqueue(message);
                        if (_isDeviceReceiveMessageCallbackSet)
                        {
                            await HandleIncomingMessagesAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _receivingSemaphore.Release();
                        }
                    }
                    else
                    {
                        if (Logging.IsEnabled)
                            Logging.Error(this, $"Received MQTT message on an unrecognized topic, ignoring message. Topic: {topic}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Logging.IsEnabled)
                    Logging.Error(this, $"Received an exception while processing an MQTT message: {ex}", nameof(OnMessageReceived));

                OnError(ex);
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, message, nameof(OnMessageReceived));
            }
        }

        private async Task CompleteIncomingMessageAsync(Message message)
        {
            try
            {
                if (_qosReceivePacketFromService == QualityOfService.AtLeastOnce && message.LockToken != null)
                {
                    _completionQueue.Enqueue(message.LockToken);
                    await CompleteAsync(_generationId + message.LockToken, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                OnError(ex);
                throw;
            }
        }

        private async Task HandleIncomingEventMessageAsync(Message message)
        {
            try
            {
                // The MqttTopic is in the format - devices/deviceId/modules/moduleId/inputs/inputName
                // We try to get the endpoint from the topic, if the topic is in the above format.
                string[] tokens = message.MqttTopicName.Split('/');
                string inputName = tokens.Length >= 6 ? tokens[5] : null;

                // Add the endpoint as a SystemProperty
                message.SystemProperties.Add(MessageSystemPropertyNames.InputName, inputName);

                if (_qosReceivePacketFromService == QualityOfService.AtLeastOnce)
                {
                    _completionQueue.Enqueue(message.LockToken);
                }
                message.LockToken = _generationId + message.LockToken;
                await (_moduleMessageReceivedListener?.Invoke(inputName, message) ?? TaskHelpers.CompletedTask).ConfigureAwait(false);
            }
            finally
            {
                message.Dispose();
            }
        }

        public async void OnError(Exception exception)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, exception, nameof(OnError));

            try
            {
                TransportState previousState = MoveToStateIfPossible(TransportState.Error, TransportState.Closed);
                switch (previousState)
                {
                    case TransportState.Error:
                    case TransportState.Closed:
                        return;

                    case TransportState.NotInitialized:
                    case TransportState.Opening:
                        _fatalException = ExceptionDispatchInfo.Capture(exception);
                        _connectCompletion.TrySetException(exception);
                        _subscribeCompletionSource.TrySetException(exception);
                        break;

                    case TransportState.Open:
                    case TransportState.Subscribing:
                        _fatalException = ExceptionDispatchInfo.Capture(exception);
                        _subscribeCompletionSource.TrySetException(exception);
                        OnTransportDisconnected();
                        break;

                    case TransportState.Receiving:
                        _fatalException = ExceptionDispatchInfo.Capture(exception);
                        _disconnectAwaitersCancellationSource.Cancel();
                        OnTransportDisconnected();
                        break;

                    default:
                        string error = $"Unknown transport state: {previousState}";
                        Debug.Fail(error);
                        throw new InvalidOperationException(error);
                }

                await _closeRetryPolicy.RunWithRetryAsync(CleanUpImplAsync).ConfigureAwait(true);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                if (Logging.IsEnabled)
                    Logging.Error(this, ex.ToString(), nameof(OnError));
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, exception, nameof(OnError));
            }
        }

        private TransportState MoveToStateIfPossible(TransportState destination, TransportState illegalStates)
        {
            TransportState previousState = State;
            do
            {
                if ((previousState & illegalStates) > 0)
                {
                    return previousState;
                }

                TransportState prevState;
                if ((prevState = (TransportState)Interlocked.CompareExchange(ref _state, (int)destination, (int)previousState)) == previousState)
                {
                    return prevState;
                }
                previousState = prevState;
            } while (true);
        }

        #endregion MQTT callbacks

        public override async Task EnableReceiveMessageAsync(CancellationToken cancellationToken)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, cancellationToken, nameof(EnableReceiveMessageAsync));

            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            try
            {
                // Wait to grab the semaphore, and then enable C2D message subscription and set _isDeviceReceiveMessageCallbackSet  to true.
                // Once _isDeviceReceiveMessageCallbackSet is set to true, all received C2D messages will be returned on the callback,
                // and not via the polling ReceiveAsync() call.
                await _deviceReceiveMessageSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (State != TransportState.Receiving)
                {
                    await SubscribeCloudToDeviceMessagesAsync().ConfigureAwait(false);
                }
                _isDeviceReceiveMessageCallbackSet = true;
            }
            finally
            {
                _deviceReceiveMessageSemaphore.Release();

                if (Logging.IsEnabled)
                    Logging.Exit(this, cancellationToken, nameof(EnableReceiveMessageAsync));
            }
        }

        public override async Task EnsurePendingMessagesAreDeliveredAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If the device connects with a CleanSession flag set to false, we will need to deliver the messages
            // that were sent before the client had subscribed to the C2D message receive topic.
            if (_retainMessagesAcrossSessions)
            {
                // Received C2D messages are enqueued into _messageQueue.
                while (!_messageQueue.IsEmpty)
                {
                    await HandleIncomingMessagesAsync().ConfigureAwait(false);
                }
            }
        }

        public override async Task DisableReceiveMessageAsync(CancellationToken cancellationToken)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, cancellationToken, nameof(DisableReceiveMessageAsync));

            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            try
            {
                // Wait to grab the semaphore, and then unsubscribe from C2D messages and set _isDeviceReceiveMessageCallbackSet  to false.
                // Once _isDeviceReceiveMessageCallbackSet  is set to false, all received C2D messages can be returned via the polling ReceiveAsync() call.
                await _deviceReceiveMessageSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    // The TransportState is transitioned to Receiving only if the device is subscribed to _deviceboundMessageFilter.
                    // Only if the subscription has been previously set, we will send the unsubscribe packet.
                    if (State == TransportState.Receiving
                        && TryStateTransition(TransportState.Receiving, TransportState.Open))
                    {
                        // Update the TransportState from Receiving to Open.
                        await _channel.WriteAsync(new UnsubscribePacket(0, _deviceboundMessageFilter)).ConfigureAwait(true);
                    }
                    _isDeviceReceiveMessageCallbackSet = false;
                }
                finally
                {
                    _deviceReceiveMessageSemaphore.Release();
                }
            }
            finally
            {
                if (Logging.IsEnabled)
                    Logging.Exit(this, cancellationToken, nameof(DisableReceiveMessageAsync));
            }
        }

        public override async Task EnableMethodsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(MethodPostTopicFilter, _qosReceivePacketFromService))).ConfigureAwait(true);
        }

        public override async Task DisableMethodsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new UnsubscribePacket(0, MethodPostTopicFilter)).ConfigureAwait(true);
        }

        public override async Task EnableEventReceiveAsync(bool isAnEdgeModule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(_receiveEventMessageFilter, _qosReceivePacketFromService))).ConfigureAwait(true);
        }

        public override async Task DisableEventReceiveAsync(bool isAnEdgeModule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new UnsubscribePacket(0, _receiveEventMessageFilter)).ConfigureAwait(true);
        }

        public override async Task SendMethodResponseAsync(MethodResponseInternal methodResponse, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            using var message = new Message(methodResponse.BodyStream)
            {
                MqttTopicName = MethodResponseTopic.FormatInvariant(methodResponse.Status, methodResponse.RequestId)
            };

            await SendEventAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public override async Task EnableTwinPatchAsync(CancellationToken cancellationToken)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, cancellationToken, nameof(EnableTwinPatchAsync));

            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(TwinPatchTopicFilter, _qosReceivePacketFromService))).ConfigureAwait(true);

            if (Logging.IsEnabled)
                Logging.Exit(this, cancellationToken, nameof(EnableTwinPatchAsync));
        }

        public override async Task DisableTwinPatchAsync(CancellationToken cancellationToken)
        {
            if (Logging.IsEnabled)
                Logging.Enter(this, cancellationToken, nameof(DisableTwinPatchAsync));

            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            await _channel.WriteAsync(new UnsubscribePacket(0, TwinPatchTopicFilter)).ConfigureAwait(true);

            if (Logging.IsEnabled)
                Logging.Exit(this, cancellationToken, nameof(DisableTwinPatchAsync));
        }

        public override async Task<Twin> SendTwinGetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnsureValidState();

            using var request = new Message();

            string rid = Guid.NewGuid().ToString();
            request.MqttTopicName = TwinGetTopic.FormatInvariant(rid);

            using Message response = await SendTwinRequestAsync(request, rid, cancellationToken).ConfigureAwait(false);

            using var reader = new StreamReader(response.GetBodyStream(), System.Text.Encoding.UTF8);
            string body = reader.ReadToEnd();

            try
            {
                return new Twin
                {
                    Properties = JsonConvert.DeserializeObject<TwinProperties>(body),
                };
            }
            catch (JsonReaderException ex) when (Logging.IsEnabled)
            {
                Logging.Error(this, $"Failed to parse Twin JSON: {ex}. Message body: '{body}'");
                throw;
            }
        }

        public override async Task SendTwinPatchAsync(TwinCollection reportedProperties, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidState();

            string body = JsonConvert.SerializeObject(reportedProperties);
            using var bodyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));

            using var request = new Message(bodyStream);

            string rid = Guid.NewGuid().ToString();
            request.MqttTopicName = TwinPatchTopic.FormatInvariant(rid);

            await SendTwinRequestAsync(request, rid, cancellationToken).ConfigureAwait(false);
        }

        private async Task OpenInternalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProxyConfigured())
            {
                // No need to do a DNS lookup since we have the proxy address already
#if NET451
                _serverAddresses = new IPAddress[0];
#else
                _serverAddresses = Array.Empty<IPAddress>();
#endif
            }
            else
            {
#if NET451
                _serverAddresses = Dns.GetHostEntry(_hostName).AddressList;
#else
                _serverAddresses = await Dns.GetHostAddressesAsync(_hostName).ConfigureAwait(false);
#endif
            }

            if (TryStateTransition(TransportState.NotInitialized, TransportState.Opening))
            {
                try
                {
                    if (_channel != null
                        && _channel is IDisposable disposableChannel)
                    {
                        if (Logging.IsEnabled)
                            Logging.Info(this, $"_channel is disposable; disposing", nameof(OpenInternalAsync));

                        disposableChannel.Dispose();
                        _channel = null;
                    }
                    _channel = await _channelFactory(_serverAddresses, ProtocolGatewayPort).ConfigureAwait(true);
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    OnError(ex);
                    throw;
                }

                ScheduleCleanup(async () =>
                {
                    _disconnectAwaitersCancellationSource?.Cancel();
                    if (_channel == null)
                    {
                        return;
                    }

                    if (_channel.Active)
                    {
                        await _channel.WriteAsync(DisconnectPacket.Instance).ConfigureAwait(true);
                    }

                    if (_channel.Open)
                    {
                        await _channel.CloseAsync().ConfigureAwait(true);
                    }
                });
            }

            await _connectCompletion.Task.ConfigureAwait(false);

            await SubscribeTwinResponsesAsync().ConfigureAwait(true);
        }

        private bool TryStop()
        {
            TransportState previousState = MoveToStateIfPossible(TransportState.Closed, TransportState.Error);
            switch (previousState)
            {
                case TransportState.Closed:
                case TransportState.Error:
                    return false;

                case TransportState.NotInitialized:
                case TransportState.Opening:
                    _connectCompletion.TrySetCanceled();
                    break;

                case TransportState.Open:
                case TransportState.Subscribing:
                    _subscribeCompletionSource.TrySetCanceled();
                    break;

                case TransportState.Receiving:
                    _disconnectAwaitersCancellationSource.Cancel();
                    break;

                default:
                    Debug.Fail($"Unknown transport state: {previousState}");
                    throw new InvalidOperationException();
            }
            return true;
        }

        private async Task SubscribeCloudToDeviceMessagesAsync()
        {
            if (TryStateTransition(TransportState.Open, TransportState.Subscribing))
            {
                await _channel
                    .WriteAsync(new SubscribePacket(0, new SubscriptionRequest(_deviceboundMessageFilter, _qosReceivePacketFromService)))
                    .ConfigureAwait(true);

                if (TryStateTransition(TransportState.Subscribing, TransportState.Receiving)
                    && _subscribeCompletionSource.TrySetResult())
                {
                    return;
                }
            }
            await _subscribeCompletionSource.Task.ConfigureAwait(false);
        }

        private Task SubscribeTwinResponsesAsync()
        {
            return _channel.WriteAsync(
                new SubscribePacket(
                    0,
                    new SubscriptionRequest(
                        TwinResponseTopicFilter,
                        _qosReceivePacketFromService)));
        }

        private bool ParseResponseTopic(string topicName, out string rid, out int status)
        {
            Match match = _twinResponseTopicRegex.Match(topicName);
            if (match.Success)
            {
                status = Convert.ToInt32(match.Groups[1].Value, CultureInfo.InvariantCulture);
                rid = HttpUtility.ParseQueryString(match.Groups[2].Value).Get("$rid");
                return true;
            }

            rid = "";
            status = 500;
            return false;
        }

        private async Task<Message> SendTwinRequestAsync(Message request, string rid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var responseReceived = new SemaphoreSlim(0);
            Message response = null; ;
            ExceptionDispatchInfo responseException = null;

            void OnTwinResponse(Message possibleResponse)
            {
                try
                {
                    if (ParseResponseTopic(possibleResponse.MqttTopicName, out string receivedRid, out int status))
                    {
                        if (rid == receivedRid)
                        {
                            if (status >= 300)
                            {
                                // The Hub team is refactoring the retriable status codes without breaking changes to the existing ones.
                                // It can be expected that we may bring more retriable codes here in the future.
                                // Retry for Http status code 429 (too many requests)
                                if (status == 429)
                                {
                                    throw new IotHubThrottledException($"Request {rid} was throttled by the server");
                                }
                                else
                                {
                                    throw new IotHubException($"Request {rid} returned status {status}", isTransient: false);
                                }
                            }
                            else
                            {
                                response = possibleResponse;
                                responseReceived.Release();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    responseException = ExceptionDispatchInfo.Capture(e);
                    responseReceived.Release();
                }
            }

            try
            {
                _twinResponseEvent += OnTwinResponse;

                await SendEventAsync(request, cancellationToken).ConfigureAwait(false);

                await responseReceived.WaitAsync(TwinTimeout, cancellationToken).ConfigureAwait(false);

                if (responseException != null)
                {
                    responseException.Throw();
                }
                else if (response == null)
                {
                    throw new TimeoutException($"Response for message {rid} not received");
                }

                return response;
            }
            finally
            {
                _twinResponseEvent -= OnTwinResponse;
            }
        }

        private Func<IPAddress[], int, Task<IChannel>> CreateChannelFactory(IotHubConnectionString iotHubConnectionString, MqttTransportSettings settings, ProductInfo productInfo, ClientOptions options)
        {
            return async (addresses, port) =>
            {
                IChannel channel = null;

                SslStream StreamFactory(Stream stream) => new SslStream(stream, true, settings.RemoteCertificateValidationCallback);

                List<X509Certificate> certs = settings.ClientCertificate == null
                    ? new List<X509Certificate>(0)
                    : new List<X509Certificate> { settings.ClientCertificate };

                SslProtocols protocols = TlsVersions.Instance.Preferred;
#if NET451
                // Requires hardcoding in NET451 otherwise yields error:
                //    Microsoft.Azure.Devices.Client.Exceptions.IotHubCommunicationException: Transient network error occurred, please retry.
                //    DotNetty.Transport.Channels.ClosedChannelException: I/O error occurred.
                if (settings.GetTransportType() == TransportType.Mqtt_Tcp_Only
                    && protocols == SslProtocols.None)
                {
                    protocols = TlsVersions.Instance.MinimumTlsVersions;
                }
#endif

                var clientTlsSettings = new ClientTlsSettings(
                     protocols,
                     settings.CertificateRevocationCheck,
                     certs,
                     iotHubConnectionString.HostName);

                Bootstrap bootstrap = new Bootstrap()
                    .Group(s_eventLoopGroup.Value)
                    .Channel<TcpSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, true)
                    .Option(ChannelOption.Allocator, UnpooledByteBufferAllocator.Default)
                    .Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
                    {
                        var tlsHandler = new TlsHandler(StreamFactory, clientTlsSettings);
                        ch.Pipeline.AddLast(
                            tlsHandler,
                            MqttEncoder.Instance,
                            new MqttDecoder(false, MaxMessageSize),
                            new LoggingHandler(LogLevel.DEBUG),
                            _mqttIotHubAdapterFactory.Create(this, iotHubConnectionString, settings, productInfo, options));
                    }));

                foreach (IPAddress address in addresses)
                {
                    try
                    {
                        if (Logging.IsEnabled)
                            Logging.Info(this, $"Connecting to {address}", nameof(CreateChannelFactory));

                        channel = await bootstrap.ConnectAsync(address, port).ConfigureAwait(true);
                        break;
                    }
                    catch (AggregateException ae)
                    {
                        ae.Handle((ex) =>
                        {
                            if (ex is ConnectException) // We will handle DotNetty.Transport.Channels.ConnectException
                            {
                                if (Logging.IsEnabled)
                                    Logging.Error(this, $"ConnectException trying to connect to {address}: {ex}", nameof(CreateChannelFactory));

                                return true;
                            }

                            return false; // Let anything else stop the application.
                        });
                    }
                    catch (ConnectException ex)
                    {
                        // same as above, we will handle DotNetty.Transport.Channels.ConnectException
                        if (Logging.IsEnabled)
                            Logging.Error(this, $"ConnectException trying to connect to {address}: {ex}", nameof(CreateChannelFactory));
                    }
                }

                return channel ?? throw new IotHubCommunicationException("MQTT channel open failed.");
            };
        }

        private Func<IPAddress[], int, Task<IChannel>> CreateWebSocketChannelFactory(
            IotHubConnectionString iotHubConnectionString,
            MqttTransportSettings settings,
            ProductInfo productInfo,
            ClientOptions options)
        {
            return async (address, port) =>
            {
                string additionalQueryParams = "";

                var websocketUri = new Uri($"{WebSocketConstants.Scheme}{iotHubConnectionString.HostName}:{WebSocketConstants.SecurePort}{WebSocketConstants.UriSuffix}{additionalQueryParams}");
                var websocket = new ClientWebSocket();
                websocket.Options.AddSubProtocol(WebSocketConstants.SubProtocols.Mqtt);

                try
                {
                    if (IsProxyConfigured())
                    {
                        // Configure proxy server
                        websocket.Options.Proxy = _webProxy;
                        if (Logging.IsEnabled)
                            Logging.Info(this, $"{nameof(CreateWebSocketChannelFactory)} Set ClientWebSocket.Options.Proxy to {_webProxy}");
                    }
                }
                catch (PlatformNotSupportedException)
                {
                    // .NET Core 2.0 doesn't support proxy. Ignore this setting.
                    if (Logging.IsEnabled)
                        Logging.Error(this, $"{nameof(CreateWebSocketChannelFactory)} PlatformNotSupportedException thrown as .NET Core 2.0 doesn't support proxy");
                }

                if (settings.WebSocketKeepAlive.HasValue)
                {
                    websocket.Options.KeepAliveInterval = settings.WebSocketKeepAlive.Value;
                    if (Logging.IsEnabled)
                        Logging.Info(this, $"{nameof(CreateWebSocketChannelFactory)} Set websocket keep-alive to {settings.WebSocketKeepAlive}");
                }

                if (settings.ClientCertificate != null)
                {
                    websocket.Options.ClientCertificates.Add(settings.ClientCertificate);
                }

                // Support for RemoteCertificateValidationCallback for ClientWebSocket is introduced in .NET Standard 2.1
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
                if (settings.RemoteCertificateValidationCallback != null)
                {
                    websocket.Options.RemoteCertificateValidationCallback = settings.RemoteCertificateValidationCallback;
                    if (Logging.IsEnabled)
                        Logging.Info(this, $"{nameof(CreateWebSocketChannelFactory)} Setting RemoteCertificateValidationCallback");
                }
#endif

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await websocket.ConnectAsync(websocketUri, cts.Token).ConfigureAwait(false);

                var clientWebSocketChannel = new ClientWebSocketChannel(null, websocket);

                clientWebSocketChannel
                    .Option(ChannelOption.Allocator, UnpooledByteBufferAllocator.Default)
                    .Option(ChannelOption.AutoRead, false)
                    .Option(ChannelOption.RcvbufAllocator, new AdaptiveRecvByteBufAllocator())
                    .Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default)
                    .Pipeline.AddLast(
                        MqttEncoder.Instance,
                        new MqttDecoder(false, MaxMessageSize),
                        new LoggingHandler(LogLevel.DEBUG),
                        _mqttIotHubAdapterFactory.Create(this, iotHubConnectionString, settings, productInfo, options));

                await s_eventLoopGroup.Value.RegisterAsync(clientWebSocketChannel).ConfigureAwait(true);

                return clientWebSocketChannel;
            };
        }

        private void ScheduleCleanup(Func<Task> cleanupTask)
        {
            Func<Task> currentCleanupFunc = _cleanupFunc;
            _cleanupFunc = async () =>
            {
                await cleanupTask().ConfigureAwait(true);

                if (currentCleanupFunc != null)
                {
                    await currentCleanupFunc().ConfigureAwait(true);
                }
            };
        }

        private async Task CleanUpAsync()
        {
            try
            {
                await _closeRetryPolicy.RunWithRetryAsync(CleanUpImplAsync).ConfigureAwait(true);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
            }
        }

        private Task CleanUpImplAsync()
        {
            return _cleanupFunc == null
                ? TaskHelpers.CompletedTask
                : _cleanupFunc();
        }

        private bool TryStateTransition(TransportState fromState, TransportState toState)
        {
            return (TransportState)Interlocked.CompareExchange(ref _state, (int)toState, (int)fromState) == fromState;
        }

        private void EnsureValidState(bool throwIfNotOpen = true)
        {
            if (State == TransportState.Error)
            {
                _fatalException.Throw();
            }

            if (State == TransportState.Closed)
            {
                Debug.Fail($"{nameof(MqttTransportHandler)}.{nameof(EnsureValidState)}: Attempting to reuse transport after it was closed.");
                throw new InvalidOperationException($"Invalid transport state: {State}");
            }

            if (throwIfNotOpen && (State & TransportState.Open) == 0)
            {
                throw new IotHubCommunicationException("MQTT connection is not established. Please retry later.");
            }
        }

        private static IEventLoopGroup GetEventLoopGroup()
        {
            try
            {
                string envValue = Environment.GetEnvironmentVariable(ProcessorThreadCountVariableName);
                if (!string.IsNullOrWhiteSpace(envValue))
                {
                    string processorEventCountValue = Environment.ExpandEnvironmentVariables(envValue);
                    if (int.TryParse(processorEventCountValue, out int processorThreadCount))
                    {
                        if (Logging.IsEnabled)
                            Logging.Info(null, $"EventLoopGroup threads count {processorThreadCount}.");

                        return processorThreadCount <= 0 ? new MultithreadEventLoopGroup() :
                            processorThreadCount == 1 ? (IEventLoopGroup)new SingleThreadEventLoop() :
                            new MultithreadEventLoopGroup(processorThreadCount);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Logging.IsEnabled)
                    Logging.Info(null, $"Could not read EventLoopGroup threads count {ex}");

                return new MultithreadEventLoopGroup();
            }

            if (Logging.IsEnabled)
                Logging.Info(null, "EventLoopGroup threads count was not set.");

            return new MultithreadEventLoopGroup();
        }

        private bool IsProxyConfigured()
        {
            return _webProxy != null
                && _webProxy != DefaultWebProxySettings.Instance;
        }
    }
}