using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
namespace SIoT;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private IManagedMqttClient? _mqttClient;
    private HubConnection? _hubConnection;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SetupSignalRConnection(stoppingToken);

        await SetupMqttClient(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Service stopped.");
        }
    }

    private async Task SetupMqttClient(CancellationToken stoppingToken)
    {
        var mqttConfig = _config.GetSection("Mqtt");
        var options = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithClientOptions(new MqttClientOptionsBuilder()
                .WithTcpServer(mqttConfig["BrokerAddress"], int.Parse(mqttConfig["Port"]!))
                .WithCredentials(mqttConfig["Username"], mqttConfig["Password"])
                .WithClientId($"middleware-{Guid.NewGuid()}")
                .Build())
            .Build();

        _mqttClient = new MqttFactory().CreateManagedMqttClient();

        _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

        try
        {
            await _mqttClient.StartAsync(options);
            await _mqttClient.SubscribeAsync(mqttConfig["SubscribeTopic"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to MQTT broker.");
            return;
        }
        _logger.LogInformation("MQTT Client connected and listening on topic: {Topic}", mqttConfig["SubscribeTopic"]);
    }

    private async Task SetupSignalRConnection(CancellationToken stoppingToken)
    {
        var hubUrl = _config.GetValue<string>("SignalR:HubUrl");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl!)
            .WithAutomaticReconnect() // Handles reconnections
            .Build();

        // Handle reconnections
        _hubConnection.Closed += async (exception) =>
        {
            await Task.Delay(new Random().Next(1, 5) * 1000);
            await _hubConnection.StartAsync(stoppingToken);
        };

        // Handle incoming messages
        _hubConnection.On<string, string>("ReceiveMessage", async (deviceId, command) =>
        {
            await SendCommandViaMqtt(deviceId, command);
        });

        try
        {
            await _hubConnection.StartAsync(stoppingToken);
            _logger.LogInformation("SignalR connection established to hub {HubUrl}.", hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to the SignalR Hub.");
        }
    }

    private async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        _logger.LogInformation("Message received from MQTT. Topic: {Topic}, Payload: {Payload}", topic, payload);

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("SendMessage", topic, payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to the SignalR Hub.");
            }
        }
    }

    private async Task SendCommandViaMqtt(string deviceId, string command)
    {
        if (_mqttClient == null || !_mqttClient.IsStarted)
        {
            _logger.LogWarning("MQTT client not connected. Command not sent.");
            return;
        }

        var commandTopic = _config.GetValue<string>("Mqtt:PublishTopic");
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(commandTopic)
            .WithPayload(command)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.EnqueueAsync(message);
        _logger.LogInformation("Command sent to device {DeviceId} via topic {Topic}.", deviceId, commandTopic);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mqttClient != null)
        {
            await _mqttClient.StopAsync();
        }
        if (_hubConnection != null)
        {
            await _hubConnection.StopAsync(cancellationToken);
            await _hubConnection.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }

    public async Task SendTastDataToSignalRServer()
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("SendMessage", "test", "test");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to the SignalR Hub.");
            }
        }
    }
}
