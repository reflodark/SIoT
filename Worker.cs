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

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            }
            await Task.Delay(1000, stoppingToken);
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

        await _mqttClient.StartAsync(options);
        await _mqttClient.SubscribeAsync(mqttConfig["SubscribeTopic"]);
        _logger.LogInformation("MQTT Client connected and listening on topic: {Topic}", mqttConfig["SubscribeTopic"]);
    }

     private async Task SetupSignalRConnection(CancellationToken stoppingToken)
    {
        var hubUrl = _config.GetValue<string>("SignalR:HubUrl");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl!)
            .WithAutomaticReconnect() // Handles reconnections
            .Build();
        
        // Handler for commands from the SignalR Hub (IT -> OT)
        _hubConnection.On<string, string>("SendCommandToDevice", async (deviceId, command) =>
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
                await _hubConnection.InvokeAsync("ReceiveDeviceData", topic, payload);
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

        var commandTopic = $"devices/{deviceId}/command";
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
}
