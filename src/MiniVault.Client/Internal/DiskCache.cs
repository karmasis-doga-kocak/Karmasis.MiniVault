using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Karmasis.Cryptography.Keys;

namespace MiniVault.Client.Internal;

/// <summary>
/// An on-disk cache of secrets, encrypted at rest with a key derived from the client's own credentials
/// (<c>clientId</c> + <c>clientSecret</c>). The file is unreadable to anyone who does not know those
/// credentials, and a different <c>clientSecret</c> (or a corrupt/foreign file) yields an empty cache rather
/// than an exception.
/// </summary>
internal sealed class DiskCache
{
    private const int FormatVersion = 1;
    private const string HkdfInfo = "minivault-cache";

    private readonly string _directory;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Action<string>? _log;

    // Per-file rather than per-instance so two DiskCache instances pointed at the same file (e.g. distinct
    // client instances sharing a cache directory) don't race each other's temp-file-plus-move sequence.
    private static readonly ConcurrentDictionary<string, object> FileLocks = new();

    public DiskCache(string directory, string clientId, string clientSecret, Action<string>? log)
    {
        if (directory is null) throw new ArgumentNullException(nameof(directory));
        if (clientId is null) throw new ArgumentNullException(nameof(clientId));
        if (clientSecret is null) throw new ArgumentNullException(nameof(clientSecret));

        _directory = directory;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _log = log;

        FilePath = Path.Combine(directory, clientId + ".cache");
    }

    /// <summary>The full path of the encrypted cache file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Loads and decrypts the cache file. Returns an empty list (without logging) when the file does not
    /// exist, and an empty list (logging a diagnostic message) when it exists but cannot be read: wrong key,
    /// corruption, or an unrecognized format.
    /// </summary>
    public IReadOnlyList<CachedSecret> Load()
    {
        if (!File.Exists(FilePath)) return Array.Empty<CachedSecret>();

        try
        {
            var blob = File.ReadAllBytes(FilePath);
            var key = DeriveKey();
            try
            {
                var aad = Encoding.UTF8.GetBytes(_clientId);
                var plain = AeadCipher.Decrypt(blob, key, aad);
                try
                {
                    var json = Encoding.UTF8.GetString(plain);
                    var file = JsonSerializer.Deserialize<CacheFileDto>(json, JsonOptions)
                        ?? throw new JsonException("Cache file deserialized to null.");

                    if (file.FormatVersion != FormatVersion)
                        throw new JsonException($"Unsupported cache format version {file.FormatVersion}.");

                    var entries = file.Entries ?? new List<CacheEntryDto>();
                    return entries
                        .Select(e => new CachedSecret(
                            e.Name ?? throw new JsonException("Cache entry missing name."),
                            Convert.FromBase64String(e.Value ?? throw new JsonException("Cache entry missing value.")),
                            e.ContentType,
                            e.Version,
                            e.UpdatedAt,
                            e.FetchedAt))
                        .ToList();
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"MiniVault disk cache at '{FilePath}' could not be read and will be ignored: {ex.Message}");
            return Array.Empty<CachedSecret>();
        }
    }

    /// <summary>
    /// Encrypts and atomically writes the given entries to the cache file, creating the target directory if
    /// necessary. Safe to call concurrently: writes are serialized and each one lands via a temp-file-plus-move
    /// so a reader never observes a partially written file.
    /// </summary>
    public void Save(IReadOnlyList<CachedSecret> entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        var dto = new CacheFileDto
        {
            FormatVersion = FormatVersion,
            Entries = entries.Select(e => new CacheEntryDto
            {
                Name = e.Name,
                Value = Convert.ToBase64String(e.Value),
                ContentType = e.ContentType,
                Version = e.Version,
                UpdatedAt = e.UpdatedAt,
                FetchedAt = e.FetchedAt,
            }).ToList(),
        };

        // The serialized JSON string itself cannot be cleared (strings are immutable in .NET), so it is built
        // and encoded to bytes in a single expression to keep its lifetime - and thus its exposure - as short
        // as possible; the byte[] we get from encoding it, unlike the string, can and is cleared below.
        byte[]? plain = null;

        var key = DeriveKey();
        byte[] blob;
        try
        {
            plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, JsonOptions));
            var aad = Encoding.UTF8.GetBytes(_clientId);
            blob = AeadCipher.Encrypt(plain, key, aad);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
            if (plain is not null) Array.Clear(plain, 0, plain.Length);
        }

        var fullPath = Path.GetFullPath(FilePath);
        lock (FileLocks.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(_directory);

            var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tempPath, blob);

            try
            {
                if (File.Exists(FilePath))
                {
                    File.Replace(tempPath, FilePath, null);
                }
                else
                {
                    File.Move(tempPath, FilePath);
                }
            }
            catch (IOException)
            {
                // Another writer may have raced us between the existence check and the move/replace.
                // Fall back to delete-then-move, which is atomic enough for our purposes (last writer wins).
                if (File.Exists(tempPath))
                {
                    File.Delete(FilePath);
                    File.Move(tempPath, FilePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    private byte[] DeriveKey()
    {
        var ikm = Encoding.UTF8.GetBytes(_clientSecret);
        var salt = Encoding.UTF8.GetBytes(_clientId);
        return KeyDerivation.Hkdf(ikm, salt, HkdfInfo, 32);
    }

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CacheFileDto
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("entries")]
        public List<CacheEntryDto>? Entries { get; set; }
    }

    private sealed class CacheEntryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("fetchedAt")]
        public DateTimeOffset FetchedAt { get; set; }
    }
}
