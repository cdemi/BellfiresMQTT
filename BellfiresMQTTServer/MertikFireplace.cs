using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SuperSimpleTcp;
using System.Text;

namespace BellfiresMQTTServer
{
    public class MertikFireplace : IHostedService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<MertikFireplace> _logger;
        private readonly SimpleTcpClient _simpleTcpClient;
        private readonly IManagedMqttClient mqttClient;
        private Timer _statusTimer;
        const string prefix = "0233303330333033303830";
        const string statusCommand = "303303";
        const string onCommand = "314103";
        const string offCommand = "313003";
        const string mqttTopicPrefix = "bellfires/status/";

        public MertikFireplace(IConfiguration config, ILogger<MertikFireplace> logger)
        {
            _config = config;
            _logger = logger;
            _simpleTcpClient = new SimpleTcpClient(config.GetValue<string>("FireplaceIPPort"));
            _simpleTcpClient.Keepalive = new SimpleTcpKeepaliveSettings
            {
                EnableTcpKeepAlives = true
            };
            _simpleTcpClient.Events.Connected += Connected;
            _simpleTcpClient.Events.Disconnected += Disconnected;
            _simpleTcpClient.Events.DataReceived += DataReceived;
            //_simpleTcpClient.Logger = (log) =>
            //{
            //    _logger.LogInformation("SimpleTCP Log: {log}", log);
            //};

            mqttClient = new MqttFactory().CreateManagedMqttClient();

        }

        private async void statusTimerCallback(object? state)
        {
            await SendCommand(statusCommand);
        }

        async Task SendCommand(string command)
        {
            try
            {
                await _simpleTcpClient.SendAsync(StringToByteArray($"{prefix}{command}"));
            }
            catch (Exception ex)
            {
                _logger.LogError("Could not send command to Fireplace :(");
            }
        }

        async void Connected(object sender, EventArgs e)
        {
            _logger.LogInformation("Fireplace Connected");
            await SendCommand(statusCommand);
        }

        async void Disconnected(object sender, EventArgs e)
        {
            _logger.LogError("Fireplace Disconnected");
            await connectToFireplace();
        }

        async void DataReceived(object sender, DataReceivedEventArgs e)
        {
            try
            {
                var statusData = Encoding.Default.GetString(e.Data).Substring(1);

                var fireplaceStatus = new FireplaceStatus();

                var intFlameHeight = int.Parse(statusData.Substring(14, 2), System.Globalization.NumberStyles.HexNumber);
                fireplaceStatus.IsOn = intFlameHeight > 123;
                intFlameHeight = intFlameHeight = (int)Math.Round((intFlameHeight - 128) / 128.0 * 12) + 1;
                fireplaceStatus.FlameHeight = intFlameHeight > 0 ? intFlameHeight : 0;

                _logger.LogInformation("Fireplace Status: {fireplaceStatus}", fireplaceStatus);
                await UpdateMQTT(fireplaceStatus);
            }
            catch
            {

            }
        }

        public async Task UpdateMQTT(FireplaceStatus fireplaceStatus)
        {
            await mqttClient.PublishAsync($"{mqttTopicPrefix}get", fireplaceStatus.IsOn ? "1" : "0", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, true);
        }

        byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        string ByteArrayToString(byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await connectToFireplace();
            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithClientId("BellfiresMQTTServer")
                    .WithTcpServer(_config.GetValue<string>("MQTT:Host"))
                    .WithCredentials(_config.GetValue<string>("MQTT:Username"), _config.GetValue<string>("MQTT:Password"))
                    .Build())
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += MqttClient_ApplicationMessageReceivedAsync;
            await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic($"{mqttTopicPrefix}set").Build());
            await mqttClient.StartAsync(options);

            _statusTimer = new Timer(statusTimerCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private async Task MqttClient_ApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
        {
            var turnOn = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload) == "1";
            _logger.LogInformation("MQTT Command Received {command}", turnOn);

            if (turnOn)
                await SendCommand(onCommand);
            else
                await SendCommand(offCommand);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task connectToFireplace()
        {
            while (true)
            {
                try
                {
                    _simpleTcpClient.ConnectWithRetries();
                    break;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        }
    }
}
