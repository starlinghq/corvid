namespace Corvid.Kernel.Checkpoints;

/// <summary>Path canonicalization shared by the checkpoint store and the built-in file tools' workspace.</summary>
public static class RealPaths
{
    private const int MaxLinks = 40;

    /// <summary>
    /// Follows symbolic links in every existing component, like realpath(3), keeping missing components as written. Throws
    /// <see cref="IOException"/> on a link loop.
    /// </summary>
    public static string Resolve(string absolute)
    {
        ArgumentNullException.ThrowIfNull(absolute);
        var pending = new Stack<string>(absolute.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = current == "/" ? "/" + part : current + "/" + part;
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks)
                throw new IOException($"Too many levels of symbolic links at {next}.");
            if (Path.IsPathRooted(target))
                current = "/";
            foreach (var component in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(component);
        }

        return current;
    }
}
