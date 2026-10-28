using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

static class Program
{
    static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".local", ".git" };
    static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase) { ".png", ".gif", ".wav", ".ico", ".jpg", ".jpeg", ".webp", ".mp3", ".mp4", ".zip", ".7z", ".rar", ".exe", ".dll", ".pdb", ".nupkg", ".pdf" };
    static readonly byte[][] Signatures = {
        new byte[]{137,80,78,71,13,10,26,10}, "GIF87a"u8.ToArray(), "GIF89a"u8.ToArray(),
        new byte[]{0,0,1,0}, "RIFF"u8.ToArray(), new byte[]{255,216,255},
        new byte[]{80,75,3,4}, new byte[]{55,122,188,175,39,28}, "%PDF-"u8.ToArray()
    };
    static readonly Regex Secrets = new(@"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{24,}|xox[baprs]-[A-Za-z0-9-]{20,}|AIza[0-9A-Za-z_-]{35}|-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----|[a-z]+://[^\s/:]+:[^\s/@]+@)");
    static readonly Regex PrivatePath = new(@"(?i)(?:[A-Z]:[\\/]Users[\\/][^\s""'<>]+|[A-Z]:[\\/](?:Development|Project)[\\/]|/home/[A-Za-z0-9_.-]+/)");
    static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    static bool HasMedia(ReadOnlySpan<byte> bytes) { foreach (var signature in Signatures) if (bytes.StartsWith(signature)) return true; return false; }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static IEnumerable<string> SourceFiles(string root, string? candidateRoot = null)
    {
        candidateRoot ??= root;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root)) {
            var info = new FileInfo(entry);
            Require((info.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse point in candidate: " + info.Name);
            if (Directory.Exists(entry)) {
                var relative = Path.GetRelativePath(candidateRoot, entry).Replace('\\', '/');
                if (!Generated.Contains(info.Name) && relative != "docs/reports")
                    foreach (var file in SourceFiles(entry, candidateRoot)) yield return file;
            } else yield return entry;
        }
    }
    static void Source(string root)
    {
        root = Path.GetFullPath(root);
        var allowed = File.ReadAllLines(Path.Combine(root, "docs", "PUBLIC-FILES.txt")).Where(x => x.Length > 0 && !x.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        var actual = SourceFiles(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        Require(actual.SetEquals(allowed), "Public file whitelist mismatch: " + string.Join(", ", actual.Except(allowed).Concat(allowed.Except(actual))));
        foreach (var rel in actual.Order()) {
            var path = Path.Combine(root, rel); var bytes = File.ReadAllBytes(path);
            Require(!Media.Contains(Path.GetExtension(path)) && !HasMedia(bytes) && !bytes.Contains((byte)0), "Binary/media source: " + rel);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            Require(!Secrets.IsMatch(text), "Potential secret; inspect locally without quoting: " + rel);
            Require(!PrivatePath.IsMatch(text), "Private absolute path: " + rel);
            Require(!Regex.IsMatch(text, @"(?i)data:(?:image|audio)/[^;]+;base64,"), "Embedded data URI: " + rel);
        }
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"))) {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("ls-files"); start.ArgumentList.Add("-z");
            using var process = Process.Start(start)!; var paths = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);process.WaitForExit();
            Require(process.ExitCode == 0 && paths.All(allowed.Contains), "Git index includes an unapproved file");
        }
        Console.WriteLine($"PASS source: {actual.Count} exact whitelist files; no binary/media signatures, known-token rules or private-path matches. This is not an independent secrets scanner.");
    }
    static List<string> Icons(string path)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        var dir = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        if (dir.Size == 0) return new();
        var bytes = pe.GetSectionData(dir.RelativeVirtualAddress).GetContent(0, dir.Size).ToArray();
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2));
        var result = new List<string>();
        void Walk(int offset, uint type, int depth) {
            Require(depth <= 4, "Invalid native resource tree");
            var count = U16(offset + 12) + U16(offset + 14);
            for (int i = 0; i < count; i++) {
                var entry = offset + 16 + 8 * i; var name = U32(entry); var target = U32(entry + 4);
                var kind = depth == 0 ? name : type;
                if ((target & 0x80000000) != 0) Walk((int)(target & 0x7fffffff), kind, depth + 1);
                else if (kind is 3 or 14) {
                    var data = (int)target; var rva = U32(data); var size = U32(data + 4);
                    var value = pe.GetSectionData((int)rva).GetContent(0, checked((int)size)).ToArray();
                    result.Add(kind + ":" + Hash(value));
                }
            }
        }
        Walk(0, 0, 0); result.Sort(StringComparer.Ordinal);return result;
    }
    static void Output(string assembly, string apphost, string sdkHost)
    {
        using var stream = File.OpenRead(assembly); using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader(); var managed = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        var blob = pe.GetSectionData(managed.RelativeVirtualAddress).GetContent(0, managed.Size).ToArray();int count = 0;
        foreach (var handle in metadata.ManifestResources) {
            var resource = metadata.GetManifestResource(handle);var name = metadata.GetString(resource.Name);
            Require(resource.Implementation.IsNil && name.EndsWith(".g.resources", StringComparison.Ordinal), "Unexpected manifest resource: " + name);
            var offset = checked((int)resource.Offset); var length = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(offset, 4));
            using var reader = new ResourceReader(new MemoryStream(blob, offset + 4, length));
            foreach (DictionaryEntry entry in reader) {
                var key = (string)entry.Key;Require(key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase), "Non-BAML embedded resource: " + key);
                Require(entry.Value is Stream, "Unknown WPF resource type: " + key);
                using var data = new MemoryStream();((Stream)entry.Value!).CopyTo(data);var bytes = data.ToArray();
                Require(!HasMedia(bytes), "Media embedded behind BAML name: " + key);count++;
                Console.WriteLine("BAML " + key + " SHA256 " + Hash(bytes));
            }
        }
        Require(count > 0, "No WPF resources inspected");
        var expected = Icons(sdkHost);var actual = Icons(apphost);
        Require(expected.SequenceEqual(actual), "Apphost icons differ from installed .NET SDK host");
        Require(Icons(assembly).Count == 0, "Unexpected managed DLL native icons");
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*", SearchOption.AllDirectories))
            Require(!Media.Contains(Path.GetExtension(path)) || Path.GetExtension(path) is ".dll" or ".exe" or ".pdb", "Loose media in output: " + Path.GetFileName(path));
        Console.WriteLine($"PASS output: {Path.GetFileName(assembly)}; {count} BAML; 0 image/audio resources; {actual.Count} native icon/group payloads match SDK host");
    }
    static int Main(string[] args)
    {
        try {
            if (args.Length == 2 && args[0] == "source") Source(args[1]);
            else if (args.Length == 4 && args[0] == "output") Output(args[1], args[2], args[3]);
            else throw new ArgumentException("Usage: source <root> | output <assembly.dll> <apphost.exe> <SDK apphost.exe>");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message);return 1; }
    }
}
