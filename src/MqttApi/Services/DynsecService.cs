using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MqttApi.Configuration;
using MqttApi.Constants;
using MqttApi.Models.Dynsec;
using MQTTnet;

namespace MqttApi.Services;

public class DynsecService : IDynsecService, IHostedService
{
    private readonly IMqttClientService _mqtt;
    private readonly MqttOptions _options;
    private readonly ILogger<DynsecService> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DynsecResponse>> _pending = new();
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public DynsecService(IMqttClientService mqtt, IOptions<MqttOptions> options, ILogger<DynsecService> logger)
    {
        _mqtt = mqtt;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _readerTask = ReadMessagesAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_readerTask != null)
            await _readerTask.ConfigureAwait(false);
    }

    public async Task CreateUserAsync(string username, string password, string? email, string[] roles,
        bool disabled = false, string? textDescription = null, CancellationToken ct = default)
    {
        var command = new DynsecCommand
        {
            Command = DynsecConstants.Commands.CreateClient,
            Username = username,
            Password = password,
            TextName = email,
            TextDescription = textDescription,
            Disabled = disabled,
            Roles = roles.Length > 0 ? roles.Select(r => new DynsecRoleRef { Rolename = r }).ToList() : null
        };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task VerifyUserAsync(string username, string token, CancellationToken ct = default)
    {
        var user = await GetUserAsync(username, ct)
            ?? throw new KeyNotFoundException($"User '{username}' not found.");

        var storedToken = user.TextDescription ?? string.Empty;
        var storedBytes = Encoding.UTF8.GetBytes(storedToken);
        var inputBytes = Encoding.UTF8.GetBytes(token);

        if (storedBytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes))
            throw new InvalidOperationException("Invalid or expired verification token.");

        var rolename = DynsecConstants.Acl.RolePrefix + username;
        await CreateRoleAsync(rolename, username, ct);

        var command = new DynsecCommand
        {
            Command = DynsecConstants.Commands.ModifyClient,
            Username = username,
            TextDescription = "",
            Disabled = false,
            Roles = [new DynsecRoleRef { Rolename = rolename }]
        };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task CreateRoleAsync(string rolename, string ownerUsername, CancellationToken ct = default)
    {
        var command = new DynsecCommand
        {
            Command = DynsecConstants.Commands.CreateRole,
            Rolename = rolename,
            Acls =
            [
                new DynsecAcl
                {
                    AclType = DynsecConstants.Acl.PublishClientSend,
                    Topic = string.Format(DynsecConstants.Acl.TopicTemplate, ownerUsername),
                    Priority = 0,
                    Allow = true
                }
            ]
        };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, rolename);
    }

    public async Task DeleteUserAsync(string username, CancellationToken ct = default)
    {
        var command = new DynsecCommand { Command = DynsecConstants.Commands.DeleteClient, Username = username };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task ChangePasswordAsync(string username, string newPassword, CancellationToken ct = default)
    {
        var command = new DynsecCommand { Command = DynsecConstants.Commands.ModifyClient, Username = username, Password = newPassword };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task ChangeEmailAsync(string username, string newEmail, string token, CancellationToken ct = default)
    {
        var command = new DynsecCommand
        {
            Command = DynsecConstants.Commands.ModifyClient,
            Username = username,
            TextName = newEmail,
            TextDescription = token,
            Disabled = true
        };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task ChangeUsernameAsync(string username, string newUsername, string newPassword, CancellationToken ct = default)
    {
        var existing = await GetUserAsync(username, ct)
            ?? throw new KeyNotFoundException($"User '{username}' not found.");

        await CreateUserAsync(
            newUsername,
            newPassword,
            existing.TextName,
            existing.Roles?.Select(r => r.Rolename).ToArray() ?? [],
            ct: ct);

        await DeleteUserAsync(username, ct);
    }

    public async Task<DynsecClientData?> GetUserAsync(string username, CancellationToken ct = default)
    {
        var command = new DynsecCommand { Command = DynsecConstants.Commands.GetClient, Username = username };
        var response = await SendCommandAsync(command, ct);
        if (response.Error == DynsecConstants.Errors.ClientNotFound)
            return null;
        ThrowOnError(response, username);
        return response.Data?.Client;
    }

    public async Task SetVerificationTokenAsync(string username, string token, CancellationToken ct = default)
    {
        var command = new DynsecCommand
        {
            Command = DynsecConstants.Commands.ModifyClient,
            Username = username,
            TextDescription = token
        };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, username);
    }

    public async Task<IReadOnlyList<string>> ListClientNamesAsync(CancellationToken ct = default)
    {
        var command = new DynsecCommand { Command = DynsecConstants.Commands.ListClients };
        var response = await SendCommandAsync(command, ct);
        ThrowOnError(response, "listClients");
        return response.Data?.Clients ?? [];
    }

    private async Task<DynsecResponse> SendCommandAsync(DynsecCommand command, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        command.CorrelationData = correlationId;

        var tcs = new TaskCompletionSource<DynsecResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            var envelope = new DynsecCommandEnvelope { Commands = [command] };
            var json = JsonSerializer.Serialize(envelope);
            await _mqtt.PublishAsync(DynsecConstants.CommandTopic, json, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                ct, new CancellationTokenSource(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds)).Token);

            timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task ReadMessagesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _mqtt.Messages.ReadAllAsync(ct))
            {
                try
                {
                    var json = Encoding.UTF8.GetString(message.PayloadSegment);
                    var envelope = JsonSerializer.Deserialize<DynsecResponseEnvelope>(json);
                    if (envelope == null) continue;

                    foreach (var response in envelope.Responses)
                    {
                        if (response.CorrelationData != null && _pending.TryGetValue(response.CorrelationData, out var tcs))
                            tcs.TrySetResult(response);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing dynsec response");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void ThrowOnError(DynsecResponse response, string username)
    {
        if (response.IsSuccess) return;

        throw response.Error switch
        {
            DynsecConstants.Errors.ClientAlreadyExists => new InvalidOperationException($"User '{username}' already exists."),
            DynsecConstants.Errors.ClientNotFound      => new KeyNotFoundException($"User '{username}' not found."),
            _                                          => new Exception($"Dynsec error: {response.Error}")
        };
    }
}
