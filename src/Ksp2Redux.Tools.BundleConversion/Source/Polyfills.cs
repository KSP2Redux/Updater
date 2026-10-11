// netstandard2.1 predates init accessors and records, so the compiler needs this marker type to
// emit them. It is internal, so it never clashes with the framework's own copy on newer targets.
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
