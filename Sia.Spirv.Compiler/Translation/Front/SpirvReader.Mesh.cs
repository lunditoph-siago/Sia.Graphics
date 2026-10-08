using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private readonly Dictionary<(uint Entry, uint Mode), uint> meshModes = [];
        private readonly Dictionary<uint, string> taskPayloads = [];
        private readonly HashSet<uint> taskTerminatingFunctions = [];
        private readonly HashSet<Statement> taskUnwinds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, MeshShaderInfo[]> meshCountTargets = [];
        private Expression.Reference? taskTerminated, taskDispatchSize, meshControlIndex;
        private readonly Dictionary<uint, MeshShaderInfo> meshEntries = [];
        private readonly Dictionary<uint, Expression> meshLocalIndices = [];
        private readonly Dictionary<string, MeshProjection> meshProjections = new(StringComparer.Ordinal);
        private uint resolvingMeshFunction;
        private uint meshCopyCounter;
        private sealed record MeshProjection(MeshShaderInfo Info, bool Primitive, string[] Fields, ShaderType NativeElement);
        private static Expression MeshPlace(string name, ShaderType type, AddressSpace space = AddressSpace.Workgroup) => new Expression.Reference(name, new ShaderType.Pointer(type, space));
        private static Expression MeshField(Expression parent, string name, ShaderType type) => new Expression.Member(parent, name,
            new ShaderType.Pointer(type, ((ShaderType.Pointer)parent.Type).Space));

        private void PrepareMeshEntries()
        {
            var calls = rawFunctions.ToDictionary(f => f.Id, f => f.Blocks.SelectMany(b => b.Instructions)
                .Where(i => (Op)i.Opcode == Op.FunctionCall).Select(i => i.Operands[2]).ToArray());
            var reachable = new Dictionary<uint, HashSet<uint>>();
            foreach (var entry in entries)
            {
                var used = new HashSet<uint>();
                void Visit(uint id) { if (!used.Add(id)) return; if (!calls.TryGetValue(id, out var children)) throw Error("Undefined called function."); foreach (uint child in children) Visit(child); }
                Visit(entry.Id); reachable.Add(entry.Id, used);
            }
            foreach (var raw in rawFunctions)
            {
                var emits = raw.Blocks.Select(b => b.Terminator).Where(t => t is not null && (Op)t.Opcode == Op.EmitMeshTasksEXT).ToArray();
                if (emits.Length == 0) continue;
                if (entries.Any(e => reachable[e.Id].Contains(raw.Id) && e.Stage != ShaderStage.Task)) throw Error("Task emission is only valid in task entry call graphs.");
                if (emits.Any(e => e!.Operands.Length is < 3 or > 4)) throw Error("Invalid task emission operands.");
                taskTerminatingFunctions.Add(raw.Id);
            }
            bool changed;
            do { changed = false; foreach (var raw in rawFunctions) if (calls[raw.Id].Any(taskTerminatingFunctions.Contains)) changed |= taskTerminatingFunctions.Add(raw.Id); } while (changed);
            if (entries.Any(e => e.Stage == ShaderStage.Task))
            {
                taskTerminated = Private("sia_task_terminated", ShaderType.Bool);
                taskDispatchSize = Private("sia_task_dispatch_size", new ShaderType.Vector(3, ShaderType.U32));
            }
            foreach (var entry in entries.Where(e => e.Stage == ShaderStage.Task))
            {
                var payloads = entry.Interfaces.Where(id => globals.TryGetValue(id, out var g) && g.Storage == 5402).ToHashSet();
                foreach (var emit in rawFunctions.Where(f => reachable[entry.Id].Contains(f.Id)).SelectMany(f => f.Blocks)
                    .Select(b => b.Terminator).Where(t => t is not null && (Op)t.Opcode == Op.EmitMeshTasksEXT && t.Operands.Length == 4))
                {
                    uint id = emit!.Operands[3];
                    if (!globals.TryGetValue(id, out var global) || global.Storage != 5402) throw Error("Task emission payload is not a task_payload variable.");
                    payloads.Add(id);
                }
                if (payloads.Count > 1) throw Error("Task entry uses more than one payload.");
                string payload;
                if (payloads.Count == 1) payload = globals[payloads.Single()].Variable.Name;
                else
                {
                    payload = "sia_task_unused_payload_" + entry.Id;
                    module.Globals.Add(new(payload, ShaderType.U32, AddressSpace.TaskPayload));
                }
                taskPayloads.Add(entry.Id, payload);
            }
            var groups = new Dictionary<string, MeshShaderInfo>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e.Stage == ShaderStage.Mesh))
            {
                uint Mode(uint mode) => meshModes.TryGetValue((entry.Id, mode), out uint value) ? value : throw Error("Mesh entry is missing required execution modes.");
                uint maxVertices = Mode(26), maxPrimitives = Mode(5270), topology = Mode(0);
                if (maxVertices == 0 || maxPrimitives == 0) throw Error("Zero-capacity mesh output needs equivalent WGSL lowering.");
                var outputIds = entry.Interfaces.Where(id => globals.TryGetValue(id, out var g) && g.Storage == 3).Order().ToArray();
                string key = string.Join(',', outputIds) + $":{maxVertices}:{maxPrimitives}:{topology}";
                if (!groups.TryGetValue(key, out var info))
                {
                    var vertices = new List<StructMember>(); var primitives = new List<StructMember>();
                    var projections = new List<(string Name, bool Primitive, string[] Fields, ShaderType Element)>();
                    foreach (uint id in outputIds)
                    {
                        var g = globals[id];
                        if (g.Variable.Type is not ShaderType.Array { Length: not null } array) throw Error("Mesh outputs require fixed outer arrays.");
                        var members = array.Element is ShaderType.Structure block ? block.Members : [new StructMember("value", array.Element, Binding: g.Binding)];
                        bool primitive = HasDecoration(id, 5271) || members.Any(m => m.Binding?.PerPrimitive == true
                            || m.Binding?.Builtin is "point_index" or "line_indices" or "triangle_indices" or "cull_primitive" or "primitive_index");
                        if (array.Length != (primitive ? maxPrimitives : maxVertices)) throw Error("Mesh output capacity differs from its execution mode.");
                        string[] fields = new string[members.Count];
                        for (int field = 0; field < members.Count; field++)
                        {
                            var m = members[field]; var binding = m.Binding ?? throw Error("Mesh output has an undecorated member.");
                            fields[field] = $"field_{id}_{field}";
                            if (primitive && binding.Location.HasValue) binding = binding with { PerPrimitive = true };
                            (primitive ? primitives : vertices).Add(new(fields[field], m.Type, Binding: binding));
                        }
                        if (meshProjections.ContainsKey(g.Variable.Name)) throw Error("Mesh entries sharing only part of an output interface require alias-preserving lowering.");
                        projections.Add((g.Variable.Name, primitive, fields, array.Element));
                    }
                    var vertexType = new ShaderType.Structure("SiaMeshVertex_" + entry.Id, vertices);
                    var primitiveType = new ShaderType.Structure("SiaMeshPrimitive_" + entry.Id, primitives);
                    var aggregate = new ShaderType.Structure("SiaMeshOutput_" + entry.Id,
                        [new("vertices", new ShaderType.Array(vertexType, maxVertices), Binding: new(Builtin: "vertices")),
                         new("primitives", new ShaderType.Array(primitiveType, maxPrimitives), Binding: new(Builtin: "primitives")),
                         new("vertex_count", ShaderType.U32, Binding: new(Builtin: "vertex_count")),
                         new("primitive_count", ShaderType.U32, Binding: new(Builtin: "primitive_count"))]);
                    var variable = new GlobalVariable("sia_mesh_output_" + entry.Id, aggregate, AddressSpace.Workgroup);
                    module.Structures.AddRange([vertexType, primitiveType, aggregate]); module.Globals.Add(variable);
                    var stub = new ShaderFunction("mesh") { MeshOutput = variable.Name };
                    info = MeshShaderInfo.Inspect(module, stub); groups.Add(key, info);
                    foreach (var projection in projections) meshProjections.Add(projection.Name, new(info, projection.Primitive, projection.Fields, projection.Element));
                    foreach (uint id in outputIds) module.Globals.Remove(globals[id].Variable);
                }
                meshEntries.Add(entry.Id, info);
                var local = entry.Interfaces.Where(id => globals.TryGetValue(id, out var g) && g.Storage == 1 && g.Binding?.Builtin == "local_invocation_index" && g.Variable.Type == ShaderType.U32).ToArray();
                if (local.Length != 0) meshLocalIndices.Add(entry.Id, Value(local[0]));
                else
                {
                    var variable = new GlobalVariable("sia_mesh_local_index_" + entry.Id, ShaderType.U32, AddressSpace.Private);
                    module.Globals.Add(variable); meshLocalIndices.Add(entry.Id, MeshPlace(variable.Name, ShaderType.U32, AddressSpace.Private));
                }
            }
            foreach (var raw in rawFunctions.Where(f => f.Blocks.SelectMany(b => b.Instructions).Any(i => (Op)i.Opcode == Op.SetMeshOutputsEXT)))
            {
                var users = entries.Where(e => reachable[e.Id].Contains(raw.Id)).ToArray();
                if (users.Any(e => e.Stage != ShaderStage.Mesh)) throw Error("Mesh count setting is only valid in mesh entry call graphs.");
                meshCountTargets.Add(raw.Id, users.Select(e => meshEntries[e.Id]).Distinct().ToArray());
            }
            if (meshCountTargets.Count != 0) meshControlIndex = Private("sia_mesh_control_index", ShaderType.U32);
            Expression.Reference Private(string name, ShaderType type)
            { module.Globals.Add(new(name, type, AddressSpace.Private)); return (Expression.Reference)MeshPlace(name, type, AddressSpace.Private); }
        }

        private Statement.Return TaskUnwind()
        {
            var statement = new Statement.Return(functions[resolvingMeshFunction].ReturnType is ShaderType.Void ? null
                : new Expression.Construct(functions[resolvingMeshFunction].ReturnType, []));
            taskUnwinds.Add(statement); return statement;
        }
        private void NormalizeTaskContinuing(Block tail, Block loopBody)
        {
            bool HasUnwind(Statement s) => taskUnwinds.Contains(s) || s switch
            {
                Statement.If i => i.Accept.Statements.Any(HasUnwind) || i.Reject.Statements.Any(HasUnwind),
                Statement.Nested n => n.Body.Statements.Any(HasUnwind),
                Statement.Switch sw => sw.Cases.Any(c => c.Body.Statements.Any(HasUnwind)),
                _ => false
            };
            if (!tail.Statements.Any(HasUnwind)) return;
            Expression Active() => new Expression.Unary("!", new Expression.Load(taskTerminated!), ShaderType.Bool);
            Block Map(Block source)
            {
                var result = new Block();
                foreach (var statement in source.Statements)
                {
                    if (taskUnwinds.Contains(statement)) continue;
                    Statement mapped = statement;
                    if (statement is Statement.Declare d)
                    {
                        result.Statements.Add(new Statement.Declare(d.Name, d.Type, null));
                        if (d.Initializer is null) continue;
                        mapped = new Statement.Store(new Expression.Reference(d.Name, d.Type), d.Initializer);
                    }
                    else mapped = statement switch
                    {
                        Statement.If i => i with { Accept = Map(i.Accept), Reject = Map(i.Reject) },
                        Statement.Nested n => new Statement.Nested(Map(n.Body)),
                        Statement.Switch sw => sw with { Cases = sw.Cases.Select(c => c with { Body = Map(c.Body) }).ToArray() },
                        _ => statement
                    };
                    var active = new Block(); active.Statements.Add(mapped);
                    result.Statements.Add(new Statement.If(Active(), active, new()));
                }
                return result;
            }
            var mapped = Map(tail); tail.Statements.Clear(); tail.Statements.AddRange(mapped.Statements);
            var unwind = new Block(); unwind.Statements.Add(TaskUnwind());
            loopBody.Statements.Insert(0, new Statement.If(new Expression.Load(taskTerminated!), unwind, new()));
        }
        private void PropagateTaskTermination(Block body, uint callee)
        {
            if (!taskTerminatingFunctions.Contains(callee)) return;
            if (taskTerminated is null) throw Error("Unreachable task termination needs dead-function elimination.");
            var unwind = new Block(); unwind.Statements.Add(TaskUnwind());
            body.Statements.Add(new Statement.If(new Expression.Load(taskTerminated), unwind, new()));
        }
        private void LowerTaskEmission(Block body, uint[] operands)
        {
            if (taskDispatchSize is null || taskTerminated is null) throw Error("Task emission is outside a task entry call graph.");
            body.Statements.Add(new Statement.Store(taskDispatchSize, new Expression.Construct(taskDispatchSize.Type is ShaderType.Pointer p ? p.Base : taskDispatchSize.Type, operands.Take(3).Select(Value).ToArray())));
            body.Statements.Add(new Statement.Store(taskTerminated, Expression.Bool(true)));
            body.Statements.Add(TaskUnwind());
        }
        private void InitializeMeshEntryControl(Entry entry, ShaderFunction function)
        {
            if (entry.Stage == ShaderStage.Mesh && meshControlIndex is not null)
                function.Body.Statements.Add(new Statement.Store(meshControlIndex, new Expression.Load(meshLocalIndices[entry.Id])));
        }

        private Expression ProjectionField(MeshProjection projection, Expression index, int field)
        {
            var info = projection.Info; string arrayName = projection.Primitive ? "primitives" : "vertices";
            ShaderType.Array array = projection.Primitive ? info.Primitives : info.Vertices;
            var container = MeshField(MeshPlace(info.Variable.Name, info.Structure), arrayName, array);
            var element = new Expression.Access(container, index, new ShaderType.Pointer(array.Element, AddressSpace.Workgroup));
            var member = ((ShaderType.Structure)array.Element).Members.Single(m => m.Name == projection.Fields[field]);
            return MeshField(element, member.Name, member.Type);
        }
        private Expression? ProjectMeshOutput(Expression expression, Expression index)
        {
            if (expression is Expression.Reference r && meshProjections.TryGetValue(r.Name, out var projection) && projection.NativeElement is not ShaderType.Structure)
                return ProjectionField(projection, index, 0);
            if (expression is Expression.Access { Base: Expression.Reference root } a && meshProjections.TryGetValue(root.Name, out var block)
                && block.NativeElement is ShaderType.Structure structure)
            {
                if (!ConstantEvaluator.TryEvaluate(index, out var value) || value is not Expression.Literal literal) throw Error("Mesh interface block member index must be constant.");
                int field = System.Convert.ToInt32(literal.Value);
                if ((uint)field >= structure.Members.Count) throw Error("Mesh interface member index is out of range.");
                return ProjectionField(block, a.Index, field);
            }
            return null;
        }
        private bool MeshAggregateCopy(Block body, Expression pointer, Expression value, bool load, SpirvMemoryAccess? memoryAccess = null)
        {
            MeshProjection projection; Expression? elementIndex = null;
            if (pointer is Expression.Reference root && meshProjections.TryGetValue(root.Name, out var array)) projection = array;
            else if (pointer is Expression.Access { Base: Expression.Reference parent } access
                && meshProjections.TryGetValue(parent.Name, out var block) && block.NativeElement is ShaderType.Structure)
            { projection = block; elementIndex = access.Index; }
            else return false;
            Expression ReadElement(Expression index) => projection.NativeElement is ShaderType.Structure structure
                ? new Expression.Construct(structure, structure.Members.Select((m, field) => MemoryLoad(ProjectionField(projection, index, field), m.Type, memoryAccess?.Leaf())).ToArray())
                : MemoryLoad(ProjectionField(projection, index, 0), projection.NativeElement, memoryAccess?.Leaf());
            void CopyElement(Block target, Expression index, Expression item)
            {
                if (load) target.Statements.Add(new Statement.Store(item, ReadElement(index)));
                else if (projection.NativeElement is ShaderType.Structure structure)
                    for (int field = 0; field < structure.Members.Count; field++)
                        target.Statements.Add(MemoryStore(ProjectionField(projection, index, field), new Expression.Member(item, structure.Members[field].Name, structure.Members[field].Type), memoryAccess?.Leaf()));
                else target.Statements.Add(MemoryStore(ProjectionField(projection, index, 0), item, memoryAccess?.Leaf()));
            }
            if (elementIndex is not null) { CopyElement(body, elementIndex, value); return true; }
            var nativeArray = (ShaderType.Array)((ShaderType.Pointer)pointer.Type).Base;
            string name = "sia_mesh_copy_" + meshCopyCounter++;
            var counter = MeshPlace(name, ShaderType.U32, AddressSpace.Function);
            body.Statements.Add(new Statement.Declare(name, ShaderType.U32, Expression.U32(0)));
            var loop = new Block(); var done = new Block(); done.Statements.Add(new Statement.Break());
            loop.Statements.Add(new Statement.If(new Expression.Binary(">=", new Expression.Load(counter), Expression.U32(nativeArray.Length!.Value), ShaderType.Bool), done, new()));
            Expression item = Index(value, new Expression.Load(counter));
            CopyElement(loop, new Expression.Load(counter), item);
            var tail = new Block(); tail.Statements.Add(new Statement.Store(counter, new Expression.Binary("+", new Expression.Load(counter), Expression.U32(1), ShaderType.U32)));
            body.Statements.Add(new Statement.Loop(loop, tail)); return true;
        }
        private void LowerMeshCounts(Block body, Expression vertices, Expression primitives)
        {
            var first = new Block();
            foreach (var info in meshCountTargets[resolvingMeshFunction])
            {
                var target = MeshPlace(info.Variable.Name, info.Structure);
                first.Statements.Add(new Statement.Store(MeshField(target, "vertex_count", ShaderType.U32), vertices));
                first.Statements.Add(new Statement.Store(MeshField(target, "primitive_count", ShaderType.U32), primitives));
            }
            if (meshControlIndex is not null) body.Statements.Add(new Statement.If(new Expression.Binary("==", new Expression.Load(meshControlIndex), Expression.U32(0), ShaderType.Bool), first, new()));
        }

        private void ConfigureMeshEntry(Entry entry, ShaderFunction function)
        {
            if (entry.Stage == ShaderStage.Task)
            {
                function.TaskPayload = taskPayloads[entry.Id];
                function.ReturnType = new ShaderType.Vector(3, ShaderType.U32); function.ReturnBinding = new(Builtin: "mesh_task_size");
                function.Body.Statements.Add(new Statement.Store(taskTerminated!, Expression.Bool(false)));
                function.Body.Statements.Add(new Statement.Store(taskDispatchSize!, new Expression.Construct(function.ReturnType, [])));
            }
            if (entry.Stage != ShaderStage.Mesh) return;
            var info = meshEntries[entry.Id]; function.MeshOutput = info.Variable.Name;
            uint[] payloads = entry.Interfaces.Where(id => globals.TryGetValue(id, out var g) && g.Storage == 5402).ToArray();
            if (payloads.Length > 1) throw Error("Mesh entry has more than one payload.");
            if (payloads.Length == 1) function.TaskPayload = globals[payloads[0]].Variable.Name;
            if (meshLocalIndices[entry.Id] is Expression.Reference r && r.Name.StartsWith("sia_mesh_local_index_", StringComparison.Ordinal))
            {
                string name = "sia_mesh_invocation_" + entry.Id;
                function.Arguments.Add(new(name, ShaderType.U32, new(Builtin: "local_invocation_index")));
                function.Body.Statements.Add(new Statement.Store(r, new Expression.Reference(name, ShaderType.U32)));
            }
        }

        private void AdjustMeshPosition(Entry entry, ShaderFunction function)
        {
            if (!options.AdjustCoordinateSpace || entry.Stage != ShaderStage.Mesh) return;
            var info = meshEntries[entry.Id]; var vertexType = (ShaderType.Structure)info.Vertices.Element;
            var position = vertexType.Members.Single(m => m.Binding?.Builtin == "position");
            function.Body.Statements.Add(new Statement.Barrier(false, true));
            string counterName = "sia_mesh_flip_index_" + entry.Id;
            var counter = MeshPlace(counterName, ShaderType.U32, AddressSpace.Function);
            function.Body.Statements.Add(new Statement.Declare(counterName, ShaderType.U32, new Expression.Load(meshLocalIndices[entry.Id])));
            Expression step = Expression.U32(1);
            foreach (var size in function.WorkgroupSize) step = new Expression.Binary("*", step, size.Type == ShaderType.U32 ? size : new Expression.Convert(ShaderType.U32, size), ShaderType.U32);
            var loop = new Block(); var done = new Block(); done.Statements.Add(new Statement.Break());
            loop.Statements.Add(new Statement.If(new Expression.Binary(">=", new Expression.Load(counter), Expression.U32(info.Vertices.Length!.Value), ShaderType.Bool), done, new()));
            var array = MeshField(MeshPlace(info.Variable.Name, info.Structure), "vertices", info.Vertices);
            var vertex = new Expression.Access(array, new Expression.Load(counter), new ShaderType.Pointer(vertexType, AddressSpace.Workgroup));
            var pos = MeshField(vertex, position.Name, position.Type); var y = new Expression.Access(pos, Expression.U32(1), new ShaderType.Pointer(ShaderType.F32, AddressSpace.Workgroup));
            loop.Statements.Add(new Statement.Store(y, new Expression.Unary("-", new Expression.Load(y), ShaderType.F32)));
            var continuing = new Block(); continuing.Statements.Add(new Statement.Store(counter, new Expression.Binary("+", new Expression.Load(counter), step, ShaderType.U32)));
            function.Body.Statements.Add(new Statement.Loop(loop, continuing));
        }
    }
}
