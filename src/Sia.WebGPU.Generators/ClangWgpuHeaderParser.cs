using ClangSharp.Interop;

namespace Sia.WebGPU.Generators;

internal static class ClangWgpuHeaderParser
{
    public static WgpuHeader Parse(string source) =>
        WithTranslationUnit(source, translationUnit => {
            ThrowOnParseErrors(translationUnit);
            return CreateHeader(translationUnit.Cursor);
        });

    private static WgpuHeader CreateHeader(CXCursor root)
    {
        var children = root.Children().ToArray();
        var handles = children.Where(IsHandleTypedef).Select(CreateHandle).OrderBy(static handle => handle.Name).ToArray();
        var flagEnums = children.Where(IsFlagTypedef).Select(child => CreateFlagEnum(child, children)).ToArray();
        var enums = children.Where(IsEnumDeclaration).Select(CreateEnum).Concat(flagEnums).OrderBy(static value => value.Name).ToArray();

        return new WgpuHeader(
            enums,
            handles,
            children.Where(IsStructDeclaration).Select(CreateStruct).OrderBy(static value => value.Name).ToArray(),
            children.Select(CreateCallback).Where(static callback => callback is not null).Select(static callback => callback!).OrderBy(static value => value.Name).ToArray(),
            children.Where(IsFunctionDeclaration).Select(CreateFunction).OrderBy(static value => value.Name).ToArray());
    }

    private static WgpuEnum CreateEnum(CXCursor cursor) =>
        new(
            WgpuNameTransforms.NormalizeEnumName(cursor.Spelling.CString),
            "int",
            false,
            cursor.Children()
                .Where(static child => child.Kind == CXCursorKind.CXCursor_EnumConstantDecl)
                .Select(child => new WgpuEnumValue(
                    WgpuNameTransforms.NormalizeEnumValueName(child.Spelling.CString, cursor.Spelling.CString + "_"),
                    child.EnumConstantDeclValue.ToString()))
                .ToArray());

    private static WgpuEnum CreateFlagEnum(CXCursor cursor, CXCursor[] allChildren)
    {
        var name = cursor.Spelling.CString;
        return new WgpuEnum(
            name,
            "ulong",
            true,
            allChildren
                .Where(child => IsStaticConstValueOfType(child, name))
                .Select(child => new WgpuEnumValue(
                    WgpuNameTransforms.NormalizeEnumValueName(child.Spelling.CString, name + "_"),
                    child.Evaluate.AsUnsigned.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray());
    }

    private static WgpuHandle CreateHandle(CXCursor cursor) =>
        new(cursor.Spelling.CString);

    private static WgpuStruct CreateStruct(CXCursor cursor) =>
        new(
            WgpuNameTransforms.NormalizeStructName(cursor.Spelling.CString),
            cursor.Children()
                .Where(static child => child.Kind == CXCursorKind.CXCursor_FieldDecl)
                .Select(static child => new WgpuField(
                    WgpuNameTransforms.ToPascalCase(child.Spelling.CString),
                    WgpuTypeTranslator.NormalizeCType(child.Type.Spelling.CString)))
                .ToArray());

    private static WgpuCallback? CreateCallback(CXCursor cursor) =>
        IsCallbackTypedef(cursor)
            ? CreateCallback(cursor, GetFunctionType(cursor.TypedefDeclUnderlyingType))
            : null;

    private static WgpuCallback? CreateCallback(CXCursor cursor, CXType functionType) =>
        IsFunctionType(functionType)
            ? new WgpuCallback(
                cursor.Spelling.CString,
                WgpuTypeTranslator.NormalizeCType(functionType.ResultType.Spelling.CString),
                CreateParameters(functionType, cursor.Children().ToArray()).ToArray())
            : null;

    private static IEnumerable<WgpuParameter> CreateParameters(CXType functionType, CXCursor[] children) =>
        Enumerable.Range(0, functionType.NumArgTypes)
            .Select(index => CreateParameter(index, functionType.GetArgType((uint)index), children));

    private static WgpuParameter CreateParameter(int index, CXType type, CXCursor[] children) =>
        new(GetParameterName(index, children), WgpuTypeTranslator.NormalizeCType(type.Spelling.CString));

    private static string GetParameterName(int index, CXCursor[] children) =>
        children
            .Where(static child => child.Kind == CXCursorKind.CXCursor_ParmDecl)
            .Select(static child => child.Spelling.CString)
            .ElementAtOrDefault(index) ?? $"arg{index}";

    private static CXType GetFunctionType(CXType underlyingType) =>
        underlyingType.kind == CXTypeKind.CXType_Pointer
            ? underlyingType.PointeeType
            : underlyingType;

    private static bool IsEnumDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_EnumDecl &&
        cursor.NumEnumerators > 0 &&
        IsWgpuName(cursor.Spelling.CString);

    private static bool IsStructDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_StructDecl &&
        cursor.NumFields > 0 &&
        IsWgpuName(cursor.Spelling.CString);

    private static bool IsHandleTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        IsWgpuName(cursor.Spelling.CString) &&
        WgpuTypeTranslator.NormalizeCType(cursor.TypedefDeclUnderlyingType.Spelling.CString).EndsWith("Impl*", StringComparison.Ordinal);

    private static bool IsFlagTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        IsWgpuName(cursor.Spelling.CString) &&
        WgpuTypeTranslator.NormalizeCType(cursor.TypedefDeclUnderlyingType.Spelling.CString) == "WGPUFlags";

    private static bool IsStaticConstValueOfType(CXCursor cursor, string typeName) =>
        cursor.Kind == CXCursorKind.CXCursor_VarDecl &&
        WgpuTypeTranslator.NormalizeCType(cursor.Type.Spelling.CString) == typeName &&
        cursor.Spelling.CString.StartsWith(typeName + "_", StringComparison.Ordinal);

    private static bool IsCallbackTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        cursor.Spelling.CString.StartsWith("WGPU", StringComparison.Ordinal) &&
        IsFunctionType(GetFunctionType(cursor.TypedefDeclUnderlyingType)) &&
        (cursor.Spelling.CString == "WGPUProc" || !cursor.Spelling.CString.StartsWith("WGPUProc", StringComparison.Ordinal));

    private static bool IsFunctionDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_FunctionDecl &&
        cursor.Spelling.CString.StartsWith("wgpu", StringComparison.Ordinal);

    private static WgpuFunction CreateFunction(CXCursor cursor) =>
        new(
            cursor.Spelling.CString,
            WgpuTypeTranslator.NormalizeCType(cursor.ResultType.Spelling.CString),
            CreateParameters(cursor.Type, cursor.Children().ToArray()).ToArray());

    private static bool IsWgpuName(string name) =>
        name.StartsWith("WGPU", StringComparison.Ordinal);

    private static bool IsFunctionType(CXType type) =>
        type.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto;

    private static TResult WithTranslationUnit<TResult>(string source, Func<CXTranslationUnit, TResult> useTranslationUnit)
    {
        var index = CXIndex.Create(excludeDeclarationsFromPch: false, displayDiagnostics: false);
        var unsavedFile = CXUnsavedFile.Create(WgpuNames.HeaderFileName, source);

        try {
            var translationUnit = CXTranslationUnit.Parse(
                index,
                WgpuNames.HeaderFileName,
                CreateParseArguments(),
                new[] { unsavedFile },
                CXTranslationUnit_Flags.CXTranslationUnit_None);

            try {
                return useTranslationUnit(translationUnit);
            } finally {
                translationUnit.Dispose();
            }
        } finally {
            unsavedFile.Dispose();
            index.Dispose();
        }
    }

    private static string[] CreateParseArguments() =>
    [
        "-x",
        "c",
        "-std=c11",
        "-DWGPU_SHARED_LIBRARY",
        "-D_WIN32",
        "-DWGPU_SKIP_PROCS",
    ];

    private static void ThrowOnParseErrors(CXTranslationUnit translationUnit)
    {
        var errors = translationUnit.DiagnosticSet
            .Where(static diagnostic => diagnostic.Severity >= CXDiagnosticSeverity.CXDiagnostic_Error)
            .Select(static diagnostic => diagnostic.Format(CXDiagnostic.DefaultDisplayOptions).CString)
            .ToArray();

        if (errors.Length != 0) {
            throw new InvalidOperationException(
                $"Failed to parse {WgpuNames.HeaderFileName}:{WgpuNames.NewLine}{string.Join(WgpuNames.NewLine, errors)}");
        }
    }
}
