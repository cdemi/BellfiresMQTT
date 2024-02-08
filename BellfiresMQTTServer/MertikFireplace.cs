using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BellfiresMQTTServer
{
    public class MertikFireplace(IConfiguration config, ILogger<MertikFireplace> logger) : IHostedLifecycleService
    {
        private readonly IManagedMqttClient mqttClient = new MqttFactory().CreateManagedMqttClient();
        private Timer _statusTimer;
        NetworkStream fireplaceNetworkStream;
        CancellationTokenSource socketCancellationTokenSource;
        readonly TimeSpan fireplacePollingInterval = TimeSpan.FromMinutes(1);
        const string prefix = "0233303330333033303830";
        const string statusCommand = "303303";
        const string onCommand = "314103";
        const string offCommand = "313003";
        const string mqttTopicPrefix = "bellfires/";
        const string mqttStatusTopicPrefix = $"{mqttTopicPrefix}status/";
        const string mqttFlameHeightTopicPrefix = $"{mqttTopicPrefix}flameHeight/";
        string[] flameSteps = { "3830", "3842", "3937", "4132", "4145", "4239", "4335", "4430", "4443", "4537", "4633", "4646" };
        public async Task ReconnectLoop(CancellationToken applicationCancellationToken)
        {
            var ipEndPoint = IPEndPoint.Parse(config.GetValue<string>("FireplaceIPPort")!);
            while (!applicationCancellationToken.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = new();
                    socketCancellationTokenSource = new CancellationTokenSource();
                    var appAndSocketCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(socketCancellationTokenSource.Token, applicationCancellationToken);
                    logger.LogInformation("Connecting to {ipEndPoint}", ipEndPoint);
                    await client.ConnectAsync(ipEndPoint, appAndSocketCancellationToken.Token);
                    logger.LogInformation("Connected to {ipEndPoint}", ipEndPoint);

                    _statusTimer = new Timer(async (state) => { await SendCommand(statusCommand); }, null, TimeSpan.FromSeconds(1), fireplacePollingInterval);

                    fireplaceNetworkStream = client.GetStream();

                    await FireplaceTCPClientLoop(fireplaceNetworkStream, appAndSocketCancellationToken.Token);
                }
                catch (Exception ex)
                {
                    _statusTimer?.Dispose();
                    logger.LogInformation("Disconnected from {ipEndPoint}", ipEndPoint);
                    if (!applicationCancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Error in FireplaceTCPClientLoop");
                        await Task.Delay(5000, applicationCancellationToken);
                    }
                }
            }
        }

        public async Task FireplaceTCPClientLoop(NetworkStream stream, CancellationToken appAndSocketCancellationToken)
        {
            while (true)
            {
                CancellationTokenSource readTimeoutCts = new(fireplacePollingInterval.Add(TimeSpan.FromSeconds(10)));
                var readAndAppAndSocketCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(appAndSocketCancellationToken, readTimeoutCts.Token);

                var buffer = new byte[1_024];
                int received = await stream.ReadAsync(buffer, readAndAppAndSocketCancellationToken.Token);

                var message = Encoding.UTF8.GetString(buffer, 0, received);
                logger.LogTrace("Received Fireplace Raw Message {message}", message);
                await HandleFireplaceStatusUpdate(message);
            }
        }

        public async Task HandleFireplaceStatusUpdate(string message)
        {
            try
            {
                var statusData = message[1..];

                var fireplaceStatus = new FireplaceStatus();

                var intFlameHeight = int.Parse(statusData.Substring(14, 2), System.Globalization.NumberStyles.HexNumber);
                fireplaceStatus.IsOn = intFlameHeight > 123;
                intFlameHeight = (int)Math.Round((intFlameHeight - 128) / 128.0 * 12) + 1;
                fireplaceStatus.FlameHeight = intFlameHeight > 0 ? intFlameHeight : 0;

                logger.LogInformation("Fireplace Status: {fireplaceStatus}", fireplaceStatus);
                await UpdateMQTT(fireplaceStatus);
            }
            catch
            {
                //Could not understand payload
            }
        }

        public async Task UpdateMQTT(FireplaceStatus fireplaceStatus)
        {
            await mqttClient.PublishAsync($"{mqttStatusTopicPrefix}get", fireplaceStatus.IsOn ? "1" : "0", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, true);
            await mqttClient.PublishAsync($"{mqttFlameHeightTopicPrefix}get", fireplaceStatus.FlameHeight.ToString(), MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, true);
        }

        byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        public async Task SendCommand(string command)
        {
            try
            {
                logger.LogTrace("Sending command {command}", command);
                await fireplaceNetworkStream.WriteAsync(StringToByteArray($"{prefix}{command}"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending command {command}", command);
                socketCancellationTokenSource.Cancel();
            }
        }

        public async Task StartMQTT()
        {
            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithClientId("BellfiresMQTTServer")
                    .WithTcpServer(config.GetValue<string>("MQTT:Host"))
                    .WithCredentials(config.GetValue<string>("MQTT:Username"), config.GetValue<string>("MQTT:Password"))
                    .Build())
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += MqttClient_ApplicationMessageReceivedAsync;
            await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic($"{mqttStatusTopicPrefix}set").WithRetainHandling(MQTTnet.Protocol.MqttRetainHandling.DoNotSendOnSubscribe).Build(),
                new MqttTopicFilterBuilder().WithTopic($"{mqttFlameHeightTopicPrefix}set").WithRetainHandling(MQTTnet.Protocol.MqttRetainHandling.DoNotSendOnSubscribe).Build());
            await mqttClient.StartAsync(options);
        }

        private async Task MqttClient_ApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
        {
            if (arg.ApplicationMessage.Topic.StartsWith(mqttStatusTopicPrefix))
            {
                var turnOn = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload) == "1";
                logger.LogInformation("MQTT Command Received {command}", turnOn);

                if (turnOn)
                    await SendCommand(onCommand);
                else
                    await SendCommand(offCommand);
            }
            else if (arg.ApplicationMessage.Topic.StartsWith(mqttFlameHeightTopicPrefix))
            {
                var requestedFlameHeight = Convert.ToInt32(Encoding.UTF8.GetString(arg.ApplicationMessage.Payload));
                logger.LogInformation("MQTT Flame Height Request Received {requestedFlameHeight}", requestedFlameHeight);
                await SendCommand($"3136{flameSteps[requestedFlameHeight - 1]}03");
            }
        }

        #region IHostedLifecycleService
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task StartedAsync(CancellationToken cancellationToken)
        {
            await StartMQTT();
            _ = ReconnectLoop(cancellationToken);
        }

        public Task StartingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        #endregion
    }
}
