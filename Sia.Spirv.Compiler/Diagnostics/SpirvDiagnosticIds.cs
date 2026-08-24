namespace Sia.Spirv.Compiler.Diagnostics;

public static class SpirvDiagnosticIds
{
    public const string InvalidKernelSignature = "SPV1001";
    public const string InvalidWorkgroupSize = "SPV1002";
    public const string UnsupportedManagedValue = "SPV1003";
    public const string InvalidControlFlow = "SPV1004";
    public const string InvalidEvaluationStack = "SPV1005";
    public const string InvalidKernelMetadata = "SPV1006";
    public const string ManagedHeapAllocation = "SPV1007";
    public const string ExceptionHandling = "SPV1008";
    public const string DynamicDispatch = "SPV1009";
}
