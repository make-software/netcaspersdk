using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Casper.Network.SDK.Types;

namespace Casper.Network.SDK.SSE
{
    [Flags]
    public enum ChannelType
    {
        /// <summary>
        /// Channel to subscribe to all events other than `DeployAccepted`s and
        /// `FinalitySignature`s.
        /// </summary>
        Main,
        /// <summary>
        /// Channel to subscribe to only `DeployAccepted` events.
        /// </summary>
        Deploys,
        /// <summary>
        /// Channel to subscribe to only `DeployAccepted` events.
        /// </summary>
        Sigs,
    };

    /// <summary>
    /// An enumeration with the different events triggered by a node.
    /// </summary>
    [Flags]
    public enum EventType
    {
        /// <summary>
        /// Add `All` to an event callback to catch all events triggered by a node.
        /// </summary>
        All,
        /// <summary>
        /// Add `ApiVersion` to an event callback to catch this event containing the version of the node's API server.
        /// This event will always be the first sent to a new client, and will have no associated event ID provided.
        /// </summary>
        ApiVersion,
        /// <summary>
        /// Add `BlockAdded` to an event callback to catch events due to a new <see cref="Block">Block</see> generated by the network.
        /// </summary>
        BlockAdded,
        /// <summary>
        /// Add `DeployAccepted` to an event callback to catch events due to a new <see cref="Deploy">Deploy</see> received for processing in the network.
        /// </summary>
        DeployAccepted,
        /// <summary>
        /// Add `DeployProcessed` to an event callback to catch events due to a new <see cref="Deploy">Deploy</see> processed by the network.
        /// </summary>
        DeployProcessed,
        /// <summary>
        /// Add `Fault` to an event callback to catch events due to a validator's fault in an era.
        /// </summary>
        Fault,
        /// <summary>
        /// Add `Step` to an event callback to catch events due to a new step.
        /// </summary>
        Step,
        /// <summary>
        /// Add `FinalitySignature` to an event callback to catch events due to block signature finalization.
        /// </summary>
        FinalitySignature,
        /// <summary>
        /// Add `DeployExpired` to an event callback to catch events due to an expired <see cref="Deploy">Deploy</see>.
        /// </summary>
        DeployExpired,
        /// <summary>
        /// Add `TransactionAccepted` to an event callback to catch events due to a new <see cref="Transaction">Transaction</see> accepted by the network.
        /// </summary>
        TransactionAccepted,
        /// <summary>
        /// Add `TransactionProcessed` to an event callback to catch events due to a new <see cref="Transaction">Transaction</see> processed by the network.
        /// </summary>
        TransactionProcessed,
        /// <summary>
        /// Add `TransactionExpired` to an event callback to catch events due to an expired <see cref="Transaction">Transaction</see>.
        /// </summary>
        TransactionExpired,
    }

    internal struct EventData
    {
        public bool HasData => !string.IsNullOrEmpty(Payload);

        public EventType EventType { get; set; }
        public int Id { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>
    /// The client used to subscribe to events emitted by a Casper node. 
    /// </summary>
    /// <remarks>
    /// Instantiate the `ServerEventsClient` class indicating the host and the port of the node
    /// that you want to connect to. Then, add one or more event callback methods to subscribe
    /// to some of the <see cref="EventType">EventType</see>s offered by the node and start to
    /// listen to the event stream.<br/>
    /// Refer to the <see href="https://github.com/make-software/casper-net-sdk/blob/master/Docs/Examples/AwaitEvents/Program.cs">AwaitEvents</see>
    /// for a practical example that shows how to use this class. 
    /// </remarks>
    public class ServerEventsClient : ISSEClient
    {
        private Dictionary<EventType, ChannelType> _evt2Channel;
        private Dictionary<ChannelType, int> _channels;

        private readonly List<SSECallback> _callbacks;

        private readonly Dictionary<ChannelType, Tuple<Task, CancellationTokenSource>> _runningTasks;

        protected string _host;
        protected int _port;
        
        public ServerEventsClient()
        {
            _callbacks = new List<SSECallback>();

            _evt2Channel = new Dictionary<EventType, ChannelType>()
            {
                {EventType.DeployAccepted, ChannelType.Deploys},
                {EventType.BlockAdded, ChannelType.Main},
                {EventType.DeployProcessed, ChannelType.Main},
                {EventType.Fault, ChannelType.Main},
                {EventType.Step, ChannelType.Main},
                {EventType.FinalitySignature, ChannelType.Sigs},
                {EventType.DeployExpired, ChannelType.Main},
                {EventType.TransactionAccepted, ChannelType.Main},
                {EventType.TransactionProcessed, ChannelType.Main},
                {EventType.TransactionExpired, ChannelType.Main},
            };
            _runningTasks = new Dictionary<ChannelType, Tuple<Task, CancellationTokenSource>>();
        }
        
        /// <summary>
        /// Instantiate the class indicating the host and the port of a node.
        /// </summary>
        /// <param name="host">IP or domain name of the node.</param>
        /// <param name="port">Event stream port.</param>
        public ServerEventsClient(string host, int port) : this()
        {
            _host = host;
            _port = port;
        }

        /// <summary>
        /// Adds an event callback method that is called for each subscribed event emitted by the node. 
        /// </summary>
        /// <param name="eventType">One or more event types to subscribe to.</param>
        /// <param name="name">Name of the callback. Use it later to remove the callback when needed.</param>
        /// <param name="cb">Callback method to call.</param>
        /// <param name="startFrom">The minimum Id number in the event stream that we want to receive.</param>
        public void AddEventCallback(EventType eventType, string name, EventCallback cb, int startFrom = int.MaxValue)
        {
            var callback = new SSECallback(eventType, name, cb);
            if (_callbacks.Contains(callback))
                throw new ArgumentException($"A callback for '{callback}' already exist. Remove it first.",
                    nameof(name));

            _callbacks.Add(callback);

            UpdateChannels(eventType, startFrom);
        }

        /// <summary>
        /// Removes an event callback method from the client.
        /// </summary>
        /// <param name="eventType">Event types subscribed in the callback method.</param>
        /// <param name="name">Name of the callback.</param>
        /// <returns></returns>
        public bool RemoveEventCallback(EventType eventType, string name)
        {
            var found = _callbacks.Remove(new SSECallback(eventType, name));

            UpdateChannels(eventType);

            return found;
        }

        private void UpdateChannels(EventType eventType, int startFrom = int.MaxValue)
        {
            var ch = new Dictionary<ChannelType, int>();
            foreach (var cb in _callbacks)
            {
                if (cb.EventType == EventType.All)
                {
                    // cant handle startFrom for EventType.All, skipping parameter
                    _channels = new Dictionary<ChannelType, int>()
                    {
                        {ChannelType.Main, int.MaxValue}, {ChannelType.Deploys, int.MaxValue},
                        {ChannelType.Sigs, int.MaxValue}
                    };
                    return;
                }

                if (_evt2Channel.ContainsKey(cb.EventType))
                {
                    var channel = _evt2Channel[cb.EventType];
                    if (ch.ContainsKey(channel) && channel == _evt2Channel[eventType])
                        ch[channel] = Math.Min(ch[channel], startFrom);
                    else
                        ch.Add(_evt2Channel[cb.EventType], startFrom);
                }
            }

            _channels = ch;
        }

        /// <summary>
        /// Connects to the node and starts listening to its even stream. If no callback method has been
        /// added, this method throws an exception.
        /// </summary>
        /// <exception cref="Exception"></exception>
        public void StartListening()
        {
            if (_channels.Count == 0)
                throw new Exception("No channels to listen. Add callbacks first.");
            if (_runningTasks.Count > 0)
                return;

            foreach (var channelType in _channels)
            {
                if (!_runningTasks.ContainsKey(channelType.Key))
                {
                    var tokenSource = new CancellationTokenSource();
                    var task = ListenChannelAsync(channelType.Key, channelType.Value, tokenSource.Token);
                    _runningTasks.Add(channelType.Key, new Tuple<Task, CancellationTokenSource>(task, tokenSource));
                    Task.Delay(3000).Wait();
                }
            }
        }

        /// <summary>
        /// Stops listening to the event stream and disconnects from the node.
        /// </summary>
        public async Task StopListening()
        {
            var tasks = new List<Task>();

            foreach (var runningTask in _runningTasks)
            {
                runningTask.Value.Item2.Cancel();
                tasks.Add(runningTask.Value.Item1);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Blocks the calling thread until all channels are disconnected.
        /// </summary>
        public void Wait()
        {
            // returns when all channel listeners are closed
            //
            var tasks = new List<Task>();

            foreach (var runningTask in _runningTasks)
                tasks.Add(runningTask.Value.Item1);

            Task.WhenAll(tasks).Wait();
        }

        /// <summary>
        /// Returns an instance of an HttpClient. Derived classes can override this method to get
        /// the client object from an HttpClientFactory, for example.
        /// </summary>
        /// <returns>a new or recycled instance of HttpClient</returns>
        protected virtual HttpClient _getHttpClient()
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri($"http://{_host}:{_port}");
            return client;
        }

        private Task ListenChannelAsync(ChannelType channelType, int? startFrom, CancellationToken cancelToken)
        {
            var task = Task.Run(async () =>
            {
                var client = _getHttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                var eventData = new EventData();

                while (!cancelToken.IsCancellationRequested)
                {
                    try
                    {
                        var uriBuilder = new UriBuilder(new Uri(client.BaseAddress +
                            $"events"));

                        if (startFrom != null && startFrom != int.MaxValue)
                            uriBuilder.Query = $"start_from={startFrom}";
                        else
                            uriBuilder.Query = $"start_from={0}";
                            
                        using (var streamReader =
                            new StreamReader(await client.GetStreamAsync(uriBuilder.Uri, cancelToken)))
                        {
                            while (!streamReader.EndOfStream && !cancelToken.IsCancellationRequested)
                            {
                                var message = await streamReader.ReadLineAsync();
                         
                                if (ParseStream(message, ref eventData))
                                {
                                    EmitEvent(eventData);
                                    eventData = new EventData();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                        Console.WriteLine("Retrying in 5 seconds");
                        await Task.Delay(5000, cancelToken);
                    }
                }
            }, cancelToken);

            return task;
        }

        private static bool ParseStream(string line, ref EventData eventData)
        {
            if (string.IsNullOrEmpty(line) || line.Trim().Equals(":"))
                return eventData.HasData
                    ;
            if (line.TrimStart().StartsWith(@"data:{""ApiVersion"""))
            {
                eventData.EventType = EventType.ApiVersion;
                eventData.Id = 0;
                eventData.Payload = line.Trim().Substring(5);
                return true;
            }

            if (line.Trim().StartsWith("data:{"))
            {
                // extract event type from first json object
                var q1 = line.IndexOf('"');
                var q2 = line.IndexOf('"', q1 + 1);
                var evtType = line.Substring(q1 + 1, q2 - q1 - 1);
                if (Enum.TryParse(evtType, out EventType evt))
                {
                    eventData.EventType = evt;
                    eventData.Id = 0;
                    eventData.Payload = line.Trim().Substring(5);
                    ;
                }

                // id needed to complete the event
                return false;
            }

            if (line.Trim().StartsWith("id:"))
            {
                if (int.TryParse(line.Substring(3).Trim(), out var id))
                {
                    eventData.Id = id;
                    return true;
                }

                return false;
            }

            return false;
        }

        private void EmitEvent(EventData eventData)
        {
            JsonDocument jsonDoc = null;

            foreach (var callback in _callbacks)
            {
                try
                {
                    if (callback.EventType == EventType.All || callback.EventType == eventData.EventType)
                    {
                        if (jsonDoc == null)
                            jsonDoc = JsonDocument.Parse(eventData.Payload);

                        callback.CallbackFn(new SSEvent()
                        {
                            EventType = eventData.EventType,
                            Id = eventData.Id,
                            Result = jsonDoc.RootElement
                        });
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}