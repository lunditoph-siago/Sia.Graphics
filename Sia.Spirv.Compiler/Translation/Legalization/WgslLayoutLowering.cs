using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Lower explicit array strides and discover enables required by WGSL types.</summary>
internal sealed class WgslLayoutLowering
{
    private readonly Module output = new();
    private readonly Dictionary<ShaderType, ShaderType> types = [];
    private readonly Dictionary<ShaderType.Array, ShaderType.Structure> wrappers = [];
    private readonly HashSet<string> names = new(StringComparer.Ordinal);

    public static Module Run(Module input)
    {
        var pass = new WgslLayoutLowering();
        pass.output.VulkanMemoryModel = input.VulkanMemoryModel;
        pass.output.WorkgroupInitializationRequired = input.WorkgroupInitializationRequired;
        foreach (var s in input.Structures) pass.names.Add(s.Name);
        foreach (var g in input.Globals) pass.names.Add(g.Name);
        foreach (var c in input.Constants) pass.names.Add(c.Name);
        foreach (var f in input.Functions) pass.names.Add(f.Name);
        foreach (var s in input.Structures) pass.Type(s);
        pass.output.Enables.UnionWith(input.Enables);
        pass.output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var g in input.Globals) pass.output.Globals.Add(g with { Type = pass.Type(g.Type), Initializer = g.Initializer is null ? null : pass.Expr(g.Initializer) });
        foreach (var c in input.Constants) pass.output.Constants.Add(c with { Type = pass.Type(c.Type), Value = c.Value is null ? null : pass.Expr(c.Value) });
        foreach (var f in input.Functions)
        {
            var function = new ShaderFunction(f.Name)
            {
                ReturnType = pass.Type(f.ReturnType), ReturnBinding = f.ReturnBinding, Stage = f.Stage,
                TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                WorkgroupSize = f.WorkgroupSize.Select(pass.Expr).ToArray(), EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth, Body = pass.Body(f.Body)
            };
            function.Arguments.AddRange(f.Arguments.Select(a => a with { Type = pass.Type(a.Type) }));
            function.DiagnosticFilters.AddRange(f.DiagnosticFilters);
            pass.output.Functions.Add(function);
        }
        return pass.output;
    }

    private ShaderType Type(ShaderType input)
    {
        if (types.TryGetValue(input, out var mapped)) return mapped;
        if (input is ShaderType.RayQuery or ShaderType.AccelerationStructure
            or ShaderType.Structure { BuiltinResult: BuiltinResultKind.RayDesc or BuiltinResultKind.RayIntersection }) output.Enables.Add("wgpu_ray_query");
        if (input is ShaderType.RayQuery { VertexReturn: true } or ShaderType.AccelerationStructure { VertexReturn: true }) output.Enables.Add("wgpu_ray_query_vertex_return");
        if (input is ShaderType.CooperativeMatrix) output.Enables.Add("wgpu_cooperative_matrix");
        if (input is ShaderType.Scalar { Width: 2 } scalar)
        {
            if (scalar.Kind == ScalarKind.Float) output.Enables.Add("f16");
            if (scalar.Kind is ScalarKind.Sint or ScalarKind.Uint) output.Enables.Add("wgpu_int16");
        }
        mapped = input switch
        {
            ShaderType.Vector v => v with { Component = (ShaderType.Scalar)Type(v.Component) },
            ShaderType.Matrix m => m with { Component = (ShaderType.Scalar)Type(m.Component) },
            ShaderType.CooperativeMatrix m => m with { Component = (ShaderType.Scalar)Type(m.Component) },
            ShaderType.Atomic a => a with { Component = (ShaderType.Scalar)Type(a.Component) },
            ShaderType.Structure s => Structure(s),
            ShaderType.Pointer p => p with { Base = Type(p.Base) },
            ShaderType.Array a => Array(a),
            ShaderType.BindingArray a => a with { Element = Type(a.Element) },
            _ => input
        };
        types.Add(input, mapped); return mapped;
    }

    private ShaderType Structure(ShaderType.Structure structure)
    {
        var result = structure with { Members = structure.Members.Select(m => m with { Type = Type(m.Type) }).ToArray() };
        output.Structures.Add(result); return result;
    }

    private ShaderType Array(ShaderType.Array array)
    {
        ShaderType element = Type(array.Element);
        uint stride = array.Stride ?? TypeLayout.Of(array.Element).Stride;
        var layout = TypeLayout.Of(element);
        if (stride == layout.Stride) return array with { Element = element, Stride = null };
        if (stride < layout.Size || stride % layout.Alignment != 0)
            throw new ShaderException(DiagnosticStage.WgslWrite, "Array stride is incompatible with the WGSL element layout.");
        int suffix = wrappers.Count;
        string name;
        do name = "SiaStride" + suffix++; while (!names.Add(name));
        var wrapper = new ShaderType.Structure(name, [new StructMember("value", element, Size: stride)]);
        wrappers.Add(array, wrapper); output.Structures.Add(wrapper);
        return array with { Element = wrapper, Stride = null };
    }

    private Expression Expr(Expression input)
    {
        // Ensure wrapper discovery before rewriting its accesses and constructors.
        ShaderType type = Type(input.Type);
        return input switch
        {
            Expression.Literal l => new Expression.Literal(l.Value, type) { Span = l.Span },
            Expression.Reference r => new Expression.Reference(r.Name, type) { Span = r.Span },
            Expression.Load l => new Expression.Load(Expr(l.Pointer)) { Span = l.Span, MemoryAccess = l.MemoryAccess },
            Expression.Unary u => new Expression.Unary(u.Operator, Expr(u.Operand), type) { Span = u.Span },
            Expression.Binary b => new Expression.Binary(b.Operator, Expr(b.Left), Expr(b.Right), type) { Span = b.Span },
            Expression.Call c => new Expression.Call(c.Function, c.Arguments.Select(Expr).ToArray(), type) { Binding = c.Binding, Span = c.Span, AtomicMemory = c.AtomicMemory, MemoryAccess = c.MemoryAccess },
            Expression.Construct c => Construct(c, type),
            Expression.Convert c => new Expression.Convert(type, Expr(c.Operand), c.Bitcast) { Span = c.Span },
            Expression.Access a => Access(a),
            Expression.Member m => new Expression.Member(Expr(m.Base), m.Name, type) { Span = m.Span },
            Expression.Swizzle s => new Expression.Swizzle(Expr(s.Vector), s.Components, type) { Span = s.Span },
            Expression.Select s => new Expression.Select(Expr(s.Condition), Expr(s.Accept), Expr(s.Reject)) { Span = s.Span },
            _ => throw new ShaderException(DiagnosticStage.WgslWrite, "Unsupported expression in layout lowering.", input.Span)
        };
    }

    private Expression Construct(Expression.Construct construct, ShaderType type)
    {
        var elements = construct.Components.Select(Expr);
        if (construct.Type is ShaderType.Array array && wrappers.TryGetValue(array, out var wrapper))
            elements = elements.Select(e => (Expression)new Expression.Construct(wrapper, [e]));
        return new Expression.Construct(type, elements.ToArray()) { Span = construct.Span };
    }

    private Expression Access(Expression.Access access)
    {
        Expression container = Expr(access.Base), index = Expr(access.Index);
        ShaderType sourceType = access.Base.Type is ShaderType.Pointer p ? p.Base : access.Base.Type;
        ShaderType resultType = Type(access.Type);
        if (sourceType is ShaderType.Array array && wrappers.TryGetValue(array, out var wrapper))
        {
            ShaderType wrapperType = access.Type is ShaderType.Pointer pointer ? new ShaderType.Pointer(wrapper, pointer.Space, pointer.Access) : wrapper;
            var wrapped = new Expression.Access(container, index, wrapperType);
            return new Expression.Member(wrapped, "value", resultType) { Span = access.Span };
        }
        return new Expression.Access(container, index, resultType) { Span = access.Span };
    }

    private Block Body(Block input)
    {
        var result = new Block();
        result.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var statement in input.Statements) result.Statements.Add(statement switch
        {
            Statement.Nested n => new Statement.Nested(Body(n.Body)),
            Statement.Declare d => d with { Type = Type(d.Type), Initializer = d.Initializer is null ? null : Expr(d.Initializer) },
            Statement.Store s => s with { Target = Expr(s.Target), Value = Expr(s.Value) },
            Statement.Evaluate e => new Statement.Evaluate(Expr(e.Value)),
            Statement.If i => new Statement.If(Expr(i.Condition), Body(i.Accept), Body(i.Reject)),
            Statement.Loop l => new Statement.Loop(Body(l.Body), Body(l.Continuing), l.BreakIf is null ? null : Expr(l.BreakIf)),
            Statement.Switch s => new Statement.Switch(Expr(s.Selector), s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray()),
            Statement.Return r => new Statement.Return(r.Value is null ? null : Expr(r.Value)),
            _ => statement
        });
        return result;
    }
}
