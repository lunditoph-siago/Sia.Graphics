using ClangSharp.Interop;

namespace Sia.WebGPU.Generators;

internal static class ClangWgpuHeaderParser
{
    public static WgpuHeader Parse(string source) =>
        WithTranslationUnit(source, translationUnit => {
            ThrowOnParseErrors(translationUnit);
            return CreateHeader(translationUnit.Cursor, source);
        });

    private static WgpuHeader CreateHeader(CXCursor root, string source)
    {
        var children = root.Children().ToArray();
        var handles = children
            .Where(IsHandleTypedef)
            .Select(CreateHandle)
            .OrderBy(static handle => handle.Name)
            .ToArray();
        var flagEnums = children
            .Where(IsFlagTypedef)
            .Select(child => CreateFlagEnum(child, children));
        var enums = children
            .Where(IsEnumDeclaration)
            .Select(CreateEnum)
            .Concat(flagEnums)
            .OrderBy(static value => value.Name)
            .ToArray();
        var structs = children
            .Where(IsStructDeclaration)
            .Select(CreateStruct)
            .OrderBy(static value => value.Name)
            .ToArray();
        var callbacks = children
            .Select(CreateCallback)
            .Where(static callback => callback is not null)
            .Select(static callback => callback!)
            .OrderBy(static value => value.Name)
            .ToArray();
        var functions = children
            .Where(IsFunctionDeclaration)
            .Select(CreateFunction)
            .OrderBy(static value => value.Name)
            .ToArray();

        return new WgpuHeader(
            enums,
            handles,
            structs,
            callbacks,
            functions,
            WgpuMacroParser.ParseConstants(source),
            WgpuMacroParser.ParseInitializers(source));
    }

    private static WgpuEnum CreateEnum(CXCursor cursor)
    {
        var nativeName = GetSpelling(cursor);
        return new WgpuEnum(
            WgpuNameTransforms.NormalizeEnumName(nativeName),
            "int",
            false,
            cursor.Children()
                .Where(static child => child.Kind == CXCursorKind.CXCursor_EnumConstantDecl)
                .Select(child => new WgpuEnumValue(
                    WgpuNameTransforms.NormalizeEnumValueName(GetSpelling(child), nativeName + "_"),
                    child.EnumConstantDeclValue.ToString()))
                .ToArray());
    }

    private static WgpuEnum CreateFlagEnum(CXCursor cursor, CXCursor[] allChildren)
    {
        var name = GetSpelling(cursor);
        return new WgpuEnum(
            name,
            "ulong",
            true,
            allChildren
                .Where(child => IsStaticConstValueOfType(child, name))
                .Select(child => new WgpuEnumValue(
                    WgpuNameTransforms.NormalizeEnumValueName(GetSpelling(child), name + "_"),
                    GetUnsignedValue(child).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray());
    }

    private static WgpuHandle CreateHandle(CXCursor cursor) =>
        new(GetSpelling(cursor));

    private static WgpuStruct CreateStruct(CXCursor cursor) =>
        new(
            WgpuNameTransforms.NormalizeStructName(GetSpelling(cursor)),
            cursor.Children()
                .Where(static child => child.Kind == CXCursorKind.CXCursor_FieldDecl)
                .Select(static child => new WgpuField(
                    WgpuNameTransforms.ToPascalCase(GetSpelling(child)),
                    WgpuTypeTranslator.NormalizeCType(GetSpelling(child.Type))))
                .ToArray());

    private static WgpuCallback? CreateCallback(CXCursor cursor) =>
        IsCallbackTypedef(cursor)
            ? CreateCallback(cursor, GetFunctionType(cursor.TypedefDeclUnderlyingType))
            : null;

    private static WgpuCallback? CreateCallback(CXCursor cursor, CXType functionType) =>
        IsFunctionType(functionType)
            ? new WgpuCallback(
                GetSpelling(cursor),
                WgpuTypeTranslator.NormalizeCType(GetSpelling(functionType.ResultType)),
                CreateParameters(functionType, cursor.Children().ToArray()).ToArray())
            : null;

    private static IEnumerable<WgpuParameter> CreateParameters(CXType functionType, CXCursor[] children) =>
        Enumerable.Range(0, functionType.NumArgTypes)
            .Select(index => CreateParameter(index, functionType.GetArgType((uint)index), children));

    private static WgpuParameter CreateParameter(int index, CXType type, CXCursor[] children) =>
        new(GetParameterName(index, children), WgpuTypeTranslator.NormalizeCType(GetSpelling(type)));

    private static string GetParameterName(int index, CXCursor[] children) =>
        children
            .Where(static child => child.Kind == CXCursorKind.CXCursor_ParmDecl)
            .Select(static child => GetSpelling(child))
            .ElementAtOrDefault(index) ?? $"arg{index}";

    private static CXType GetFunctionType(CXType underlyingType) =>
        underlyingType.kind == CXTypeKind.CXType_Pointer
            ? underlyingType.PointeeType
            : underlyingType;

    private static bool IsEnumDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_EnumDecl &&
        cursor.NumEnumerators > 0 &&
        IsWgpuName(GetSpelling(cursor));

    private static bool IsStructDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_StructDecl &&
        cursor.NumFields > 0 &&
        IsWgpuName(GetSpelling(cursor));

    private static bool IsHandleTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        IsWgpuName(GetSpelling(cursor)) &&
        WgpuTypeTranslator
            .NormalizeCType(GetSpelling(cursor.TypedefDeclUnderlyingType))
            .EndsWith("Impl*", StringComparison.Ordinal);

    private static bool IsFlagTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        IsWgpuName(GetSpelling(cursor)) &&
        WgpuTypeTranslator.NormalizeCType(GetSpelling(cursor.TypedefDeclUnderlyingType)) == "WGPUFlags";

    private static bool IsStaticConstValueOfType(CXCursor cursor, string typeName) =>
        cursor.Kind == CXCursorKind.CXCursor_VarDecl &&
        WgpuTypeTranslator.NormalizeCType(GetSpelling(cursor.Type)) == typeName &&
        GetSpelling(cursor).StartsWith(typeName + "_", StringComparison.Ordinal);

    private static bool IsCallbackTypedef(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_TypedefDecl &&
        GetSpelling(cursor).StartsWith("WGPU", StringComparison.Ordinal) &&
        IsFunctionType(GetFunctionType(cursor.TypedefDeclUnderlyingType)) &&
        IsSupportedCallbackName(GetSpelling(cursor));

    private static bool IsSupportedCallbackName(string name) =>
        !name.StartsWith("WGPUProc", StringComparison.Ordinal) || name == "WGPUProc";

    private static bool IsFunctionDeclaration(CXCursor cursor) =>
        cursor.Kind == CXCursorKind.CXCursor_FunctionDecl &&
        GetSpelling(cursor).StartsWith("wgpu", StringComparison.Ordinal);

    private static WgpuFunction CreateFunction(CXCursor cursor) =>
        new(
            GetSpelling(cursor),
            WgpuTypeTranslator.NormalizeCType(GetSpelling(cursor.ResultType)),
            CreateParameters(cursor.Type, cursor.Children().ToArray()).ToArray());

    private static string GetSpelling(CXCursor cursor)
    {
        using var spelling = cursor.Spelling;
        return spelling.CString;
    }

    private static string GetSpelling(CXType type)
    {
        using var spelling = type.Spelling;
        return spelling.CString;
    }

    private static ulong GetUnsignedValue(CXCursor cursor)
    {
        using var result = cursor.Evaluate;
        return result.AsUnsigned;
    }

    private static bool IsWgpuName(string name) =>
        name.StartsWith("WGPU", StringComparison.Ordinal);

    private static bool IsFunctionType(CXType type) =>
        type.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto;

    private static TResult WithTranslationUnit<TResult>(
        string source,
        Func<CXTranslationUnit, TResult> useTranslationUnit)
    {
        using var index = CXIndex.Create(excludeDeclarationsFromPch: false, displayDiagnostics: false);
        using var unsavedFile = CXUnsavedFile.Create(WgpuNames.HeaderFileName, source);
        using var translationUnit = CXTranslationUnit.Parse(
            index,
            WgpuNames.HeaderFileName,
            CreateParseArguments(),
            new[] { unsavedFile },
            CXTranslationUnit_Flags.CXTranslationUnit_None);

        return useTranslationUnit(translationUnit);
    }

    private static string[] CreateParseArguments() =>
    [
        "-x",
        "c",
        "-std=c11",
        "-DWGPU_SKIP_PROCS",
    ];

    private static void ThrowOnParseErrors(CXTranslationUnit translationUnit)
    {
        using var diagnosticSet = translationUnit.DiagnosticSet;
        var errors = diagnosticSet
            .Where(static diagnostic => diagnostic.Severity >= CXDiagnosticSeverity.CXDiagnostic_Error)
            .Select(static diagnostic => FormatDiagnostic(diagnostic))
            .ToArray();

        if (errors.Length != 0) {
            var message = string.Join(WgpuNames.NewLine, errors);
            throw new InvalidOperationException(
                $"Failed to parse {WgpuNames.HeaderFileName}:{WgpuNames.NewLine}{message}");
        }
    }

    private static string FormatDiagnostic(CXDiagnostic diagnostic)
    {
        using var formatted = diagnostic.Format(CXDiagnostic.DefaultDisplayOptions);
        return formatted.CString;
    }
}
