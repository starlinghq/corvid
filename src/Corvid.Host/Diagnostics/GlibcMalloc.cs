using System.Runtime.InteropServices;
using System.Text;

namespace Corvid.Host.Diagnostics;

/// <summary>
/// Starling requires <c>MALLOC_MMAP_THRESHOLD_=65536</c> in the environment of a Linux process that runs GLiNER2 (C-010).
/// glibc reads its malloc tunables before <c>Main</c>, so setting the variable from managed code is too late: an
/// executable that loads models calls <see cref="EnsureMmapThreshold"/> first thing, which re-executes the same process
/// image with the same arguments and the variable added. <c>CORVID_NO_REEXEC=1</c> turns this off.
/// </summary>
public static partial class GlibcMalloc
{
    public const string OptOutVariable = "CORVID_NO_REEXEC";

    /// <summary>
    /// Returns normally when the variable is already set, the platform is not Linux, or re-execution is off or fails (with
    /// the reason). On success it does not return: the process image is replaced.
    /// </summary>
    public static string? EnsureMmapThreshold()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable(Doctor.MallocVariable) == Doctor.MallocValue)
            return null;
        if (Environment.GetEnvironmentVariable(OptOutVariable) is "1" or "true")
            return $"{OptOutVariable} is set; {Doctor.MallocVariable} stays unset.";
        try
        {
            // The exact argv this process started with, including the dotnet host and the assembly when run with `dotnet`.
            var argv = File.ReadAllBytes("/proc/self/cmdline").AsSpan().TrimEnd((byte)0).ToArray()
                .Split((byte)0).Select(Encoding.UTF8.GetString).ToArray();
            if (argv.Length == 0)
                return "cannot read /proc/self/cmdline.";
            var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(name => name != Doctor.MallocVariable)
                .Select(name => $"{name}={Environment.GetEnvironmentVariable(name)}")
                .Append($"{Doctor.MallocVariable}={Doctor.MallocValue}")
                .ToArray();
            // The real path rather than /proc/self/exe, so the process keeps its name (ps shows corvid, not exe).
            Execute(Environment.ProcessPath ?? "/proc/self/exe", argv, environment);
            return $"execve failed (errno {Marshal.GetLastPInvokeError()}).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    private static void Execute(string path, string[] argv, string[] environment)
    {
        var strings = new List<IntPtr>();
        try
        {
            var argvBlock = Block(argv, strings);
            var envBlock = Block(environment, strings);
            var file = Marshal.StringToCoTaskMemUTF8(path);
            strings.Add(file);
            // Only returns on failure.
            _ = ExecVe(file, argvBlock, envBlock);
        }
        finally
        {
            foreach (var pointer in strings)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    // A null-terminated array of UTF-8 strings, allocated so the caller frees every pointer (the array included).
    private static IntPtr Block(string[] values, List<IntPtr> allocated)
    {
        var array = Marshal.AllocCoTaskMem(IntPtr.Size * (values.Length + 1));
        allocated.Add(array);
        for (var i = 0; i < values.Length; i++)
        {
            var value = Marshal.StringToCoTaskMemUTF8(values[i]);
            allocated.Add(value);
            Marshal.WriteIntPtr(array, i * IntPtr.Size, value);
        }
        Marshal.WriteIntPtr(array, values.Length * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    private static IEnumerable<byte[]> Split(this byte[] bytes, byte separator)
    {
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i == bytes.Length || bytes[i] == separator)
            {
                yield return bytes[start..i];
                start = i + 1;
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int ExecVe(IntPtr path, IntPtr argv, IntPtr environment);
}
