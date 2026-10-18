// VIZEN update signing (ECDSA P-256, SHA-256). Run with the .NET 10 SDK:
//   dotnet run scripts/update-signing.cs -- newkey <private-key.pem>      creates a key pair, prints the public key
//   dotnet run scripts/update-signing.cs -- publickey <private-key.pem>   prints the public key (base64 SPKI)
//   dotnet run scripts/update-signing.cs -- sign <private-key.pem> <latest.json>   writes <latest.json>.sig
//   dotnet run scripts/update-signing.cs -- verify <public-key-base64> <latest.json>
// The private key file must stay outside the repository (and outside synced folders). In GitHub Actions it comes from
// the UPDATE_SIGNING_KEY secret. The public key is built into VIZEN (UpdateSignature.TrustedKeys).
using System.Security.Cryptography;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: newkey|publickey|sign|verify ...");
    return 2;
}

switch (args[0])
{
    case "newkey":
    {
        if (File.Exists(args[1]))
        {
            Console.Error.WriteLine($"{args[1]} already exists; not overwritten");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "publickey":
    {
        using var key = Load(args[1]);
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "sign" when args.Length >= 3:
    {
        using var key = Load(args[1]);
        var data = File.ReadAllBytes(args[2]);
        var signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllText(args[2] + ".sig", Convert.ToBase64String(signature));
        Console.WriteLine($"signed {args[2]} ({data.Length} bytes)");
        return 0;
    }
    case "verify" when args.Length >= 3:
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(args[1]), out _);
        var ok = key.VerifyData(File.ReadAllBytes(args[2]), Convert.FromBase64String(File.ReadAllText(args[2] + ".sig").Trim()),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine(ok ? "valid" : "INVALID");
        return ok ? 0 : 1;
    }
    default:
        Console.Error.WriteLine("usage: newkey|publickey|sign|verify ...");
        return 2;
}

static ECDsa Load(string path)
{
    // The key comes from a file, or (GitHub Actions) from an environment variable named by "env:NAME".
    var pem = path.StartsWith("env:") ? Environment.GetEnvironmentVariable(path[4..]) ?? throw new InvalidOperationException($"{path} is empty") : File.ReadAllText(path);
    var key = ECDsa.Create();
    key.ImportFromPem(pem);
    return key;
}
