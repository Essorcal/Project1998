// Inter.dat server-list patcher — formalized client redirect.
// Rewrites the Nexon server IPs baked into Inter.dat to a target the client will dial locally.
// Every Nexon IP token is 12 chars; the default target 127.100.10.1 is also 12 chars and is a
// valid 127.0.0.0/8 loopback (no leading zeros -> passes the client's Winsock resolver).

using System.Text;
using Server;
using Shared;

if (args is ["readme-tables", ..])
{
    string readmePath = args.Length > 1 ? args[1] : Path.Combine(RepoPaths.GameDataDir(), "README.md");
    Content.Load();
    string readme = File.ReadAllText(readmePath).Replace("\r\n", "\n", StringComparison.Ordinal);
    int start = readme.IndexOf(Content.TableReadmeStartMarker, StringComparison.Ordinal);
    int end = readme.IndexOf(Content.TableReadmeEndMarker, StringComparison.Ordinal);
    if (start < 0 || end < start)
    {
        Console.Error.WriteLine($"generated table markers not found in {readmePath}");
        return 1;
    }
    end += Content.TableReadmeEndMarker.Length;
    string updated = readme[..start] + Content.RenderTableReadmeBlock() + readme[end..];
    File.WriteAllText(readmePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"wrote 68 CSV table rows to {readmePath}");
    return 0;
}

string src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "Inter.dat";
string target = "127.100.10.1";
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--target") target = args[i + 1];

if (target.Length != 12)
{
    Console.WriteLine($"target must be exactly 12 chars (got {target.Length}). " +
                      "Default 127.100.10.1 works for local; for LAN pick a 12-char form.");
    return 1;
}
if (!File.Exists(src))
{
    Console.WriteLine($"not found: {src}");
    return 1;
}

byte[] data = File.ReadAllBytes(src);

string bak = src + ".bak";
if (!File.Exists(bak)) { File.WriteAllBytes(bak, data); Console.WriteLine($"backup -> {bak}"); }

string[] nexon = { "210.192.90.2", "210.192.90.3", "65.203.45.40" };
int total = 0;
byte[] rep = Encoding.ASCII.GetBytes(target);
foreach (var ip in nexon)
{
    int c = ReplaceAll(data, Encoding.ASCII.GetBytes(ip), rep);
    if (c > 0) Console.WriteLine($"  {ip} -> {target}  ({c})");
    total += c;
}

string outPath = src + ".patched";
File.WriteAllBytes(outPath, data);
Console.WriteLine($"replaced {total} IP token(s); wrote {outPath}");
Console.WriteLine("Copy the .patched file over Inter.dat in the client folder (backup kept).");
return 0;

static int ReplaceAll(byte[] data, byte[] find, byte[] rep)
{
    int count = 0;
    for (int i = 0; i <= data.Length - find.Length; i++)
    {
        bool m = true;
        for (int j = 0; j < find.Length; j++)
            if (data[i + j] != find[j]) { m = false; break; }
        if (m) { Array.Copy(rep, 0, data, i, rep.Length); count++; i += find.Length - 1; }
    }
    return count;
}
