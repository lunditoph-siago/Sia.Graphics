using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Target contract checks shared by input composition and output preparation.
/// External environment validation remains an independent check.</summary>
internal static class ShaderTargetValidator
{
    public static void ValidateStage(SpirvCompilationTarget target, string stage)
    {
        if (!Enum.TryParse<ShaderStage>(stage, out var value) || !Enum.IsDefined(value))
            throw new ArgumentException("Unsupported shader stage: " + stage);
        // Policy meaning follows serialized identifiers, independent of a caller's
        // host set comparer, which is deliberately absent from target identity.
        if (target.AllowedStages is { } stages && !stages.Any(stage => stage == value))
            throw new ShaderException(DiagnosticStage.Validation, "Target does not allow shader stage " + stage + ".");
    }

    public static void ValidateModule(Module module, SpirvCompilationTarget target, bool wgsl = false, bool resources = true)
        => ValidateModule(module, target, wgsl, resources, null);

    internal static void ValidateModule(CanonicalModule canonical, SpirvCompilationTarget target)
        => ValidateModule(canonical.Declarations, target, false, true, canonical.Functions);

    private static void ValidateModule(Module module, SpirvCompilationTarget target, bool wgsl, bool resources,
        IReadOnlyDictionary<string, ControlFlowFunction>? graphs)
    {
        target.Validate(wgsl: wgsl);
        foreach (var function in module.Functions)
            if (graphs?.TryGetValue(function.Name, out var graph) == true) {
                foreach (var block in graph.Blocks) {
                    foreach (var instruction in block.Instructions)
                        if (instruction.Operation is ValueOperation.Demote or ValueOperation.HelperInvocation)
                            ValidateInvocationFeature("SPV_EXT_demote_to_helper_invocation", true, instruction.Span, target, wgsl);
                    if (block.Terminator is ControlFlowTerminator.InvocationKill { ExplicitTermination: true } kill)
                        ValidateInvocationFeature("SPV_KHR_terminate_invocation", false, kill.Span, target, wgsl);
                }
            }
            else ValidateInvocationRequirements(function.Body, target, wgsl);
        foreach (var function in module.Functions.Where(f => f.Stage is not null)) ValidateStage(target, function.Stage!.Value.ToString());
        if (target.KernelAbi == SpirvKernelAbi.WebGpu && module.Globals.Any(g => g.Space == AddressSpace.Immediate))
            throw new ShaderException(DiagnosticStage.Validation, "WebGPU ABI does not allow immediate/push-constant resources.");
        foreach (var entry in module.Functions.Where(f => resources && f.Stage is not null)) {
            var functions = ControlFlowAnalysis.CalledFunctions(module, [entry.Name], graphs);
            var globalNames = module.Globals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
            var used = module.Functions.Where(f => functions.Contains(f.Name)).SelectMany(f =>
                graphs?.TryGetValue(f.Name, out var graph) == true
                    ? graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Symbol>()
                        .Select(s => s.Name).Where(n => globalNames.Contains(n) && !f.Arguments.Any(a => a.Name == n))
                    : UsedGlobals(f, module)).ToHashSet(StringComparer.Ordinal);
            var buffers = module.Globals.Where(g => used.Contains(g.Name) && g.Space is AddressSpace.Storage or AddressSpace.Uniform).ToArray();
            long Count(AddressSpace space) => buffers.Where(g => g.Space == space).Sum(g => g.Type is ShaderType.BindingArray array
                ? (long)(array.Length ?? throw new ShaderException(DiagnosticStage.Validation, "Resource limits require a resolved descriptor array length.")) : 1L);
            var limits = target.ResourceLimits;
            int storageLimit = entry.Stage switch {
                ShaderStage.Vertex => System.Math.Min(limits.MaxStorageBuffersPerShaderStage, limits.MaxStorageBuffersInVertexStage),
                ShaderStage.Fragment => System.Math.Min(limits.MaxStorageBuffersPerShaderStage, limits.MaxStorageBuffersInFragmentStage),
                _ => limits.MaxStorageBuffersPerShaderStage
            };
            if (!limits.SupportsStorageBuffers) storageLimit = 0;
            if (storageLimit != int.MaxValue && Count(AddressSpace.Storage) > storageLimit || limits.MaxUniformBuffersPerShaderStage != int.MaxValue && Count(AddressSpace.Uniform) > limits.MaxUniformBuffersPerShaderStage)
                throw new ShaderException(DiagnosticStage.Validation, "Target buffer binding limit exceeded by entry " + entry.Name + ".");
            foreach (var buffer in buffers) {
                ulong limit = buffer.Space == AddressSpace.Storage ? limits.MaxStorageBufferBindingSize : limits.MaxUniformBufferBindingSize;
                if (limit == ulong.MaxValue) continue;
                var type = buffer.Type is ShaderType.BindingArray array ? array.Element : buffer.Type;
                if (TypeLayout.Of(type).Size > limit)
                    throw new ShaderException(DiagnosticStage.Validation, "Target minimum buffer binding size exceeded by " + buffer.Name + ".");
            }
        }
        if (wgsl && target.AllowedWgslEnables is { } allowed && module.Enables.Any(e => !allowed.Any(a => StringComparer.Ordinal.Equals(a, e))))
            throw new ShaderException(DiagnosticStage.WgslWrite, "Target does not allow a requested WGSL enable.");
    }

    public static void ValidateWgslInvocationFeatures(Module module)
    {
        foreach (var function in module.Functions) ValidateInvocationRequirements(function.Body, null, wgsl: true);
    }

    internal static void ValidateWgslInvocationFeatures(CanonicalModule canonical)
    {
        foreach (var function in canonical.Declarations.Functions) {
            if (!canonical.Functions.TryGetValue(function.Name, out var graph)) {
                ValidateInvocationRequirements(function.Body, null, wgsl: true); continue;
            }
            foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
                if (instruction.Operation is ValueOperation.HelperInvocation)
                    throw new ShaderException(DiagnosticStage.WgslWrite,
                        "Dynamic helper invocation queries cannot be represented in WGSL.", instruction.Span);
        }
    }

    private static void ValidateInvocationFeature(string extension, bool demotion, SourceSpan span, SpirvCompilationTarget? target, bool wgsl)
    {
        if (wgsl || target is null) return;
        if (demotion && target.AllowedCapabilities is { } capabilities && !capabilities.Any(capability => capability == 5379))
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Target does not allow SPIR-V capability 5379.", span);
        if (target.Version < 0x10600 && target.AllowedExtensions is { } extensions
            && !extensions.Any(e => StringComparer.Ordinal.Equals(e, extension)))
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Target does not allow SPIR-V extension " + extension + ".", span);
    }

    private static void ValidateInvocationRequirements(Block block, SpirvCompilationTarget? target, bool wgsl = false)
    {
        void Feature(string extension, bool demotion, SourceSpan span) => ValidateInvocationFeature(extension, demotion, span, target, wgsl);
        void Expr(Expression expression) {
            if (expression is Expression.HelperInvocation) {
                if (wgsl) throw new ShaderException(DiagnosticStage.WgslWrite,
                    "Dynamic helper invocation queries cannot be represented in WGSL.", expression.Span);
                Feature("SPV_EXT_demote_to_helper_invocation", true, expression.Span);
            }
            IEnumerable<Expression> children = expression switch {
                Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) Expr(child);
        }
        foreach (var statement in block.Statements) {
            string? extension = statement switch {
                Statement.Kill => "SPV_EXT_demote_to_helper_invocation",
                Statement.InvocationKill { ExplicitTermination: true } => "SPV_KHR_terminate_invocation",
                _ => null
            };
            if (extension is not null) Feature(extension, statement is Statement.Kill, statement.Span);
            switch (statement) {
                case Statement.Declare { Initializer: { } value }: Expr(value); break;
                case Statement.Store store: Expr(store.Target); Expr(store.Value); break;
                case Statement.Evaluate evaluate: Expr(evaluate.Value); break;
                case Statement.Return { Value: { } value }: Expr(value); break;
                case Statement.Nested nested: ValidateInvocationRequirements(nested.Body, target, wgsl); break;
                case Statement.If branch:
                    Expr(branch.Condition);
                    ValidateInvocationRequirements(branch.Accept, target, wgsl); ValidateInvocationRequirements(branch.Reject, target, wgsl); break;
                case Statement.Loop loop:
                    ValidateInvocationRequirements(loop.Body, target, wgsl); ValidateInvocationRequirements(loop.Continuing, target, wgsl);
                    if (loop.BreakIf is { } condition) Expr(condition); break;
                case Statement.Switch selection:
                    Expr(selection.Selector);
                    foreach (var arm in selection.Cases) ValidateInvocationRequirements(arm.Body, target, wgsl); break;
            }
        }
    }

    // Resolve lexical shadowing before attributing a reference to a module resource.
    private static HashSet<string> UsedGlobals(ShaderFunction function, Module module)
    {
        var globals = module.Globals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var scopes = new Stack<HashSet<string>>(); scopes.Push(function.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal));
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Expr(Expression value) {
            if (value is Expression.Reference reference && globals.Contains(reference.Name) && !scopes.Any(s => s.Contains(reference.Name))) used.Add(reference.Name);
            IEnumerable<Expression> children = value switch {
                Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) Expr(child);
        }
        void Body(Block body, bool scope = true) {
            if (scope) scopes.Push(new(StringComparer.Ordinal));
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.Declare d: if (d.Initializer is { } value) Expr(value); scopes.Peek().Add(d.Name); break;
                case Statement.Store s: Expr(s.Target); Expr(s.Value); break;
                case Statement.Evaluate e: Expr(e.Value); break;
                case Statement.Return { Value: { } returned }: Expr(returned); break;
                case Statement.Nested n: Body(n.Body); break;
                case Statement.If i: Expr(i.Condition); Body(i.Accept); Body(i.Reject); break;
                case Statement.Switch s: Expr(s.Selector); foreach (var arm in s.Cases) Body(arm.Body); break;
                case Statement.Loop l:
                    scopes.Push(new(StringComparer.Ordinal)); Body(l.Body, false);
                    scopes.Push(new(StringComparer.Ordinal)); Body(l.Continuing, false); if (l.BreakIf is { } condition) Expr(condition);
                    scopes.Pop(); scopes.Pop(); break;
            }
            if (scope) scopes.Pop();
        }
        Body(function.Body); return used;
    }

    public static void ValidateWgsl(string text, SpirvCompilationTarget target)
    {
        if (target.AllowedWgslEnables is not { } allowed) return;
        foreach (string line in text.Split('\n').Where(l => l.StartsWith("enable ", StringComparison.Ordinal))) {
            string enable = line[7..].Trim().TrimEnd(';');
            if (!allowed.Any(a => StringComparer.Ordinal.Equals(a, enable))) throw new ShaderException(DiagnosticStage.WgslWrite, "Target does not allow WGSL enable " + enable + ".");
        }
    }

    public static void ValidateBinary(SpirvBinary binary, SpirvCompilationTarget target)
    {
        target.Validate();
        if (binary.Version != target.Version)
            throw new ShaderException(DiagnosticStage.SpirvWrite, "SPIR-V output version differs from the selected target.");
        foreach (var instruction in binary.Instructions) {
            if ((Op)instruction.Opcode == Op.Capability && target.AllowedCapabilities is { } capabilities
                && !capabilities.Any(capability => capability == instruction.Operands[0]))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Target does not allow SPIR-V capability " + instruction.Operands[0] + ".");
            if ((Op)instruction.Opcode == Op.Extension && target.AllowedExtensions is { } extensions) {
                string extension = SpirvBinary.ReadString(instruction.Operands, out _);
                if (!extensions.Any(e => StringComparer.Ordinal.Equals(e, extension)))
                    throw new ShaderException(DiagnosticStage.SpirvWrite, "Target does not allow SPIR-V extension " + extension + ".");
            }
        }
    }
}
