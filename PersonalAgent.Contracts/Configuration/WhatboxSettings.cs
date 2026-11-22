namespace PersonalAgent.Contracts.Configuration;

public sealed record WhatboxSettingsResponse(string? Version, string Host, string Username,
    string HostKeySha256, bool Enabled, bool HasPrivateKey, string FilesDirectory, string RTorrentSocketPath);

public sealed record SaveWhatboxSettingsRequest(string? Version, string Host, string Username,
    string HostKeySha256, bool Enabled, string? PrivateKey, string FilesDirectory, string RTorrentSocketPath);

public sealed record WhatboxConnectionResult(bool Connected, string Message);
