using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Comm.UpdateSigner;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "keygen" => KeyGen(args),
                "sign" => Sign(args),
                "verify" => Verify(args),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int KeyGen(string[] args)
    {
        var outDir = GetOption(args, "--out") ?? ".";
        Directory.CreateDirectory(outDir);

        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();

        var privateSeed = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
        var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();

        var seedPath = Path.Combine(outDir, "update-signing-ed25519.seed");
        var pubPath = Path.Combine(outDir, "update-signing-ed25519.pub");

        File.WriteAllText(seedPath, Convert.ToBase64String(privateSeed) + Environment.NewLine, Encoding.ASCII);
        File.WriteAllText(pubPath, Convert.ToBase64String(publicKey) + Environment.NewLine, Encoding.ASCII);

        // Best-effort permission hardening on Unix-like systems.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(seedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(pubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        Console.WriteLine($"Private seed written: {seedPath}");
        Console.WriteLine($"Public key written:   {pubPath}");
        Console.WriteLine();
        Console.WriteLine("Public key (base64):");
        Console.WriteLine(Convert.ToBase64String(publicKey));
        Console.WriteLine();
        Console.WriteLine("Embed this public key in the client as a constant:");
        Console.WriteLine($"public const string UpdatePublicKey = \"{Convert.ToBase64String(publicKey)}\";");
        Console.WriteLine();
        Console.WriteLine("WARNING: keep the seed file offline. Do not commit it to Git or include it in releases.");
        return 0;
    }

    private static int Sign(string[] args)
    {
        var manifestPath = RequireOption(args, "--manifest");
        var keyPath = RequireOption(args, "--key");
        var outPath = GetOption(args, "--out") ?? manifestPath;

        var manifest = ReadManifest(manifestPath);
        manifest.Signature = null;

        var seed = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
        if (seed.Length != 32)
        {
            throw new InvalidOperationException("Private seed must be 32 bytes (base64).");
        }

        var canonical = CanonicalManifestBytes(manifest);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(canonical, 0, canonical.Length);
        var signature = signer.GenerateSignature();

        manifest.Signature = Convert.ToBase64String(signature);
        File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, Encoding.UTF8);

        Console.WriteLine($"Signed manifest written: {outPath}");
        Console.WriteLine($"Signature: {manifest.Signature}");
        return 0;
    }

    private static int Verify(string[] args)
    {
        var manifestPath = RequireOption(args, "--manifest");
        var pubPath = GetOption(args, "--pubkey");
        var pubBase64 = GetOption(args, "--pubkey-base64");

        if (string.IsNullOrWhiteSpace(pubPath) && string.IsNullOrWhiteSpace(pubBase64))
        {
            throw new InvalidOperationException("Either --pubkey <file> or --pubkey-base64 <base64> is required.");
        }

        var manifest = ReadManifest(manifestPath);
        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidOperationException("Manifest has no signature.");
        }

        var signature = Convert.FromBase64String(manifest.Signature);
        manifest.Signature = null;
        var canonical = CanonicalManifestBytes(manifest);

        var pubBytes = !string.IsNullOrWhiteSpace(pubBase64)
            ? Convert.FromBase64String(pubBase64!)
            : Convert.FromBase64String(File.ReadAllText(pubPath!).Trim());

        if (pubBytes.Length != 32)
        {
            throw new InvalidOperationException("Public key must be 32 bytes (base64).");
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(pubBytes, 0));
        verifier.BlockUpdate(canonical, 0, canonical.Length);
        var ok = verifier.VerifySignature(signature);

        Console.WriteLine(ok ? "VERIFY: OK" : "VERIFY: FAILED");
        return ok ? 0 : 1;
    }

    private static UpdateManifest ReadManifest(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException("Manifest is empty or invalid.");
        }
        return manifest;
    }

    private static byte[] CanonicalManifestBytes(UpdateManifest manifest)
    {
        // Keep a stable prefix so signatures are unambiguous even if JSON is reused elsewhere.
        const string prefix = "CommTestTool update manifest Ed25519 v1\0";
        var json = JsonSerializer.Serialize(manifest, CanonicalOptions);
        return Encoding.UTF8.GetBytes(prefix + json);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string RequireOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option: {name}");
        }
        return value!;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Comm.UpdateSigner - Ed25519 update manifest signer");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  keygen --out <directory>");
        Console.WriteLine("  sign --manifest <unsigned.json> --key <seed.base64> [--out <signed.json>]");
        Console.WriteLine("  verify --manifest <signed.json> --pubkey <pub.base64> | --pubkey-base64 <base64>");
    }
}

public sealed class UpdateManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("product")]
    public string Product { get; set; } = "CommTestTool";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows-x64";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("build")]
    public long Build { get; set; }

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("executableSha256")]
    public string? ExecutableSha256 { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("signingKeyId")]
    public string SigningKeyId { get; set; } = "";

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}