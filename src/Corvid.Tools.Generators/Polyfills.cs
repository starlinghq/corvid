// netstandard2.0 lacks the type the compiler needs for records and init accessors.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}
