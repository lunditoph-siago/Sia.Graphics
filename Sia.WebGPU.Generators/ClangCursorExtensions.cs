using System.Runtime.InteropServices;

using ClangSharp.Interop;

namespace Sia.WebGPU.Generators;

internal static class ClangCursorExtensions
{
    public static IReadOnlyList<CXCursor> Children(this CXCursor cursor)
    {
        var children = new List<CXCursor>();
        VisitChildren(cursor, children);
        return children;
    }

    private static unsafe void VisitChildren(CXCursor cursor, List<CXCursor> children)
    {
        var handle = GCHandle.Alloc(children);

        try {
            cursor.VisitChildren(VisitChild, new CXClientData(GCHandle.ToIntPtr(handle)));
        } finally {
            handle.Free();
        }
    }

    private static unsafe CXChildVisitResult VisitChild(CXCursor child, CXCursor parent, void* data)
    {
        var children = (List<CXCursor>)GCHandle.FromIntPtr(new IntPtr(data)).Target!;
        children.Add(child);
        return CXChildVisitResult.CXChildVisit_Continue;
    }
}
