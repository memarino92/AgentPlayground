using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Npgsql;
using Renci.SshNet.Common;

using PersonalAgent.Api.Models;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Services;

internal sealed record WhatboxDiskUsage(long FilesAllocatedBytes, long FilesystemAvailableBytes, long FilesystemTotalBytes);
internal sealed record WhatboxSeedingStatus(int Total, int Seeding, int CompleteInactive, int Incomplete, long SeedingUploadBytesPerSecond);
internal sealed class WhatboxOperationException(string Message) : InvalidOperationException(Message);

internal sealed class WhatboxService(IWhatboxSettingsStore Store, IWhatboxSshTransport Transport, ILogger<WhatboxService> Logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public async Task<WhatboxSettingsResponse> GetAsync(CancellationToken Token)
    {
        var (version, configuration) = await Store.ReadAsync(Token);
        return configuration.ToResponse(version);
    }

    public async Task<WhatboxSettingsResponse> SaveAsync(SaveWhatboxSettingsRequest Request, CancellationToken Token)
    {
        var (version, saved) = await Store.ReadAsync(Token);
        if (version != Request.Version) throw new IntegrationConflictException();
        var host = (Request.Host ?? "").Trim().ToLowerInvariant();
        var username = (Request.Username ?? "").Trim();
        var fingerprint = (Request.HostKeySha256 ?? "").Trim();
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) fingerprint = fingerprint[7..];
        fingerprint = fingerprint.TrimEnd('=');
        if (host.Length > 253 || !host.EndsWith(".whatbox.ca", StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw Invalid(nameof(Request.Host), "Enter a Whatbox SSH hostname ending in .whatbox.ca.");
        if (username.Length is 0 or > 128 || !username.All(Character =>
            char.IsAsciiLetterOrDigit(Character) || Character is '_' or '-'))
            throw Invalid(nameof(Request.Username), "Enter your Whatbox SSH username.");
        if (fingerprint.Length != 43 || !Convert.TryFromBase64String(fingerprint + "=", new byte[32], out var count) || count != 32)
            throw Invalid(nameof(Request.HostKeySha256), "Enter the verified SHA-256 SSH host fingerprint.");
        var filesDirectory = ValidatePath(Request.FilesDirectory, nameof(Request.FilesDirectory));
        var socketPath = ValidatePath(Request.RTorrentSocketPath, nameof(Request.RTorrentSocketPath));

        var key = Request.PrivateKey is null ? saved.PrivateKey : Request.PrivateKey.Trim();
        if (key is "") key = null;
        if (Request.PrivateKey is not null && key is not null)
        {
            if (key.Length > 16384) throw Invalid(nameof(Request.PrivateKey), "Enter a valid SSH private key.");
            key += "\n";
            try { using var parsed = WhatboxSshTransport.ParseKey(key); }
            catch (Exception error) when (error is SshException or ArgumentException or FormatException or
                CryptographicException or InvalidOperationException or NotSupportedException)
            {
                throw Invalid(nameof(Request.PrivateKey), "Enter a supported SSH private key without a passphrase.");
            }
        }
        if (Request.Enabled && key is null) throw Invalid(nameof(Request.PrivateKey), "Save a dedicated SSH key before enabling Whatbox.");
        var configuration = new WhatboxConfiguration(host, username, fingerprint, Request.Enabled, key, filesDirectory, socketPath);
        return configuration.ToResponse(await Store.SaveAsync(version, configuration, Token));
    }

    public async Task<bool> IsEnabledAsync(CancellationToken Token)
    {
        try
        {
            var (_, configuration) = await Store.ReadAsync(Token);
            return configuration is { Enabled: true, PrivateKey: not null };
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or CryptographicException or JsonException)
        {
            Logger.LogWarning("Whatbox settings unavailable ({ErrorType})", error.GetType().Name);
            return false;
        }
    }

    public async Task<WhatboxConnectionResult> TestAsync(CancellationToken Token)
    {
        var (_, configuration) = await Store.ReadAsync(Token);
        if (configuration.PrivateKey is null) return new(false, "Save the SSH settings and a private key first.");
        try
        {
            await RunAsync(configuration, WhatboxOperation.Connection, Token);
            return new(true, "The API verified the Whatbox host and authenticated over SSH.");
        }
        catch (WhatboxOperationException error) { return new(false, error.Message); }
    }

    public Task<WhatboxDiskUsage> DiskUsageAsync(AgentAccessContext Access, CancellationToken Token) =>
        ReadAsync<WhatboxDiskUsage>(Access, WhatboxOperation.DiskUsage, Token);

    public Task<WhatboxSeedingStatus> SeedingStatusAsync(AgentAccessContext Access, CancellationToken Token) =>
        ReadAsync<WhatboxSeedingStatus>(Access, WhatboxOperation.SeedingStatus, Token);

    private async Task<T> ReadAsync<T>(AgentAccessContext Access, WhatboxOperation Operation, CancellationToken Token) where T : class
    {
        if (Access.Role != AgentRoles.Owner) throw new UnauthorizedAccessException("Whatbox access requires the Owner role.");
        var (_, configuration) = await Store.ReadAsync(Token);
        if (configuration is not { Enabled: true, PrivateKey: not null })
            throw new WhatboxOperationException("Whatbox is disabled or has no configured SSH key.");
        var output = await RunAsync(configuration, Operation, Token);
        try { return JsonSerializer.Deserialize<T>(output, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new WhatboxOperationException("Whatbox returned an invalid status response."); }
    }

    private async Task<string> RunAsync(WhatboxConfiguration Configuration, WhatboxOperation Operation, CancellationToken Token)
    {
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var result = await Transport.ExecuteAsync(Configuration, Operation, Token);
            success = true;
            return result;
        }
        finally
        {
            Logger.LogInformation("Whatbox {Operation} completed with success {Success} in {DurationMs} ms",
                Operation, success, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static IntegrationValidationException Invalid(string Field, string Message) => new(new() { [Field] = [Message] });

    private static string ValidatePath(string? Value, string Field)
    {
        var path = (Value ?? "").Trim();
        if (path.Length is 0 or > 1024 || path.Any(char.IsControl)
            || !(path.StartsWith('/') || path.StartsWith("~/", StringComparison.Ordinal)))
            throw Invalid(Field, "Enter an absolute remote path or one starting with ~/.");
        return path;
    }
}
