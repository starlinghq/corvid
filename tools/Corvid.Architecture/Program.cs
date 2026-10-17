using Corvid.Architecture;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Corvid.Architecture -- <repository-root>");
    return 2;
}

try
{
    var errors = BoundaryCheck.CheckRepository(Path.GetFullPath(args[0]));
    foreach (var error in errors)
        Console.Error.WriteLine(error);
    if (errors.Count != 0)
        return 1;
    Console.WriteLine("Production dependency boundaries passed.");
    return 0;
}
catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine($"Cannot check boundaries: {exception.Message}");
    return 1;
}
