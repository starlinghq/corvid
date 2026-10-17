using Corvid.Vault.Client;

// Arguments: socket path, session id, secret name, URL, canary.
// Uses the secret through Vault, prints what it saw, then holds still until stdin closes so the parent can read this
// process's memory. The canary is a known string the parent must find, which proves the memory scan works.
if (args.Length != 5)
{
    Console.Error.WriteLine("usage: <socket> <session> <secret> <url> <canary>");
    return 2;
}

string canary = args[4];
var client = new VaultClient(args[1], args[0]);
Console.WriteLine("STEP:mint");
string placeholder = await client.MintPlaceholderAsync(new SecretRef(args[2]));
Console.WriteLine("STEP:send");

using var http = new HttpClient(new VaultHandler(client));
using var request = new HttpRequestMessage(HttpMethod.Get, args[3]);
request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {placeholder}");
using HttpResponseMessage response = await http.SendAsync(request);
string body = await response.Content.ReadAsStringAsync();
Console.WriteLine("STEP:scrub");
string echoed = string.Join(";", response.Headers.TryGetValues("X-Echo", out var values) ? values : []);
string scrubbed = await client.ScrubAsync("tool output: " + body);

Console.WriteLine($"STATUS:{(int)response.StatusCode}");
Console.WriteLine($"BODY:{body}");
Console.WriteLine($"ECHO:{echoed}");
Console.WriteLine($"SCRUBBED:{scrubbed}");
Console.WriteLine($"CANARY-LENGTH:{canary.Length}");
Console.WriteLine("READY");
Console.Out.Flush();
Console.In.ReadLine();
GC.KeepAlive(canary);
return 0;
