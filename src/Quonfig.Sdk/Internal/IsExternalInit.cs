#if !NET5_0_OR_GREATER
using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill so that C# 9+ <c>init</c>-only setters compile on netstandard2.0.
/// Required by every <c>record</c> declaration in this assembly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit { }
#endif
