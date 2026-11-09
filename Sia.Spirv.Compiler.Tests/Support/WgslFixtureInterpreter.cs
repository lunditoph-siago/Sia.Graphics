using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class WgslFixtureInterpreter
{
    private sealed record Cell(Func<object> Read, Action<object> Write, string? Name = null);

    // Scalar fixture interpreter with parallel phi transfers and observable
    // memory/call/barrier ordering. The expected memory values are separate
    // oracles; this does not compare strings or reproduce the optimization.
    internal static (uint[] Memory, string[] Effects) Execute(string source)
    {
        var module = CanonicalShaderPipeline.Prepare(WgslReader.Parse(source));
        uint[] memory = [7, 0]; var effects = new List<string>();
        object Copy(object value) => value is object[] array ? array.Select(Copy).ToArray() : value;
        object Zero(ShaderType type) => type switch {
            ShaderType.Vector vector => Enumerable.Repeat(Zero(vector.Component), vector.Size).ToArray(),
            ShaderType.Scalar { Kind: ScalarKind.Bool } => false,
            ShaderType.Scalar { Kind: ScalarKind.Uint, Width: 4 } => 0u,
            _ => throw new InvalidOperationException("Unsupported fixture zero value: " + type)
        };
        object? Call(string name, object[] arguments) {
            var graph = module.Functions[name];
            var symbols = graph.Signature.Arguments.Select((a, i) => (a.Name, Value: arguments[i])).ToDictionary(p => p.Name, p => p.Value);
            var values = new Dictionary<int, object>();
            object Read(SsaValue value) => values[value.Id];
            object Binary(string op, object a, object b) {
                if (op == "==") return Equals(a, b);
                if (op == "!=") return !Equals(a, b);
                if (a is bool x && b is bool y) return op switch { "&&" or "&" => x && y, "||" or "|" => x || y, _ => throw new InvalidOperationException(op) };
                uint left = (uint)a, right = (uint)b;
                return op switch {
                    "+" => unchecked(left + right), "-" => unchecked(left - right), "*" => unchecked(left * right),
                    "/" => right == 0 ? left : left / right, "%" => right == 0 ? 0u : left % right,
                    "&" => left & right, "|" => left | right, "^" => left ^ right,
                    "<<" => left << (int)(right & 31), ">>" => left >> (int)(right & 31),
                    "<" => left < right, "<=" => left <= right, ">" => left > right, ">=" => left >= right,
                    _ => throw new InvalidOperationException(op)
                };
            }
            object? Builtin(ValueOperation.Builtin builtin) {
                var args = builtin.Arguments.Select(Read).ToArray();
                if (builtin.Function == "atomicAdd") {
                    effects.Add(builtin.Function); var cell = (Cell)args[0]; var old = (uint)cell.Read();
                    cell.Write(unchecked(old + (uint)args[1])); return old;
                }
                if (builtin.Function == "atomicStore") { effects.Add(builtin.Function); ((Cell)args[0]).Write(args[1]); return null; }
                if (builtin.Function is "workgroupBarrier" or "storageBarrier") { effects.Add("barrier"); return null; }
                if (builtin.Function == "select") return (bool)args[2] ? args[1] : args[0];
                throw new InvalidOperationException("Unexpected builtin: " + builtin.Function);
            }
            int current = graph.Entry;
            for (int step = 0; step < 1000; step++) {
                var block = graph.Blocks.Single(b => b.Id == current);
                foreach (var instruction in block.Instructions) {
                    object? value;
                    switch (instruction.Operation) {
                        case ValueOperation.Literal literal: value = literal.Value; break;
                        case ValueOperation.Symbol symbol:
                            value = module.Declarations.Globals.Any(g => g.Name == symbol.Name && g.Space == AddressSpace.Storage) ? memory : symbols[symbol.Name]; break;
                        case ValueOperation.Let alias: value = Read(alias.Value); break;
                        case ValueOperation.Construct construct:
                            var type = instruction.Result!.Value.Type;
                            var components = construct.Components.Select(Read).ToArray();
                            value = components.Length == 0 ? Zero(type) : type is ShaderType.Vector vector
                                ? components.Length == 1 ? Enumerable.Repeat(Copy(components[0]), vector.Size).ToArray() : components.Select(Copy).ToArray()
                                : throw new InvalidOperationException("Unexpected fixture constructor."); break;
                        case ValueOperation.Local:
                            object stored = Zero(((ShaderType.Pointer)instruction.Result!.Value.Type).Base);
                            value = new Cell(() => stored, v => stored = Copy(v)); break;
                        case ValueOperation.Access access:
                            object container = Read(access.Base); int index = Read(access.Index) is int i ? i : checked((int)(uint)Read(access.Index));
                            if (container is uint[] array) value = new Cell(() => array[index], v => array[index] = (uint)v, "data[" + index + "]");
                            else if (container is Cell parent) value = new Cell(() => ((object[])parent.Read())[index], v => {
                                var update = (object[])Copy(parent.Read()); update[index] = Copy(v); parent.Write(update);
                            });
                            else value = Copy(((object[])container)[index]);
                            break;
                        case ValueOperation.Load load: value = Copy(((Cell)Read(load.Pointer)).Read()); break;
                        case ValueOperation.Member member when Read(member.Base) is uint[]: value = Read(member.Base); break;
                        case ValueOperation.Store store:
                            var destination = (Cell)Read(store.Pointer); var written = Copy(Read(store.Value));
                            if (destination.Name is { } cellName) effects.Add("store:" + cellName + "=" + written);
                            destination.Write(written); continue;
                        case ValueOperation.Unary { Operator: "!" } unary: value = !(bool)Read(unary.Operand); break;
                        case ValueOperation.Binary binary: value = Binary(binary.Operator, Read(binary.Left), Read(binary.Right)); break;
                        case ValueOperation.Select select: value = (bool)Read(select.Condition) ? Read(select.Accept) : Read(select.Reject); break;
                        case ValueOperation.Convert convert:
                            value = Read(convert.Operand);
                            value = instruction.Result!.Value.Type switch {
                                ShaderType.Scalar { Kind: ScalarKind.Uint, Width: 4 } when value is int signed => unchecked((uint)signed),
                                ShaderType.Scalar { Kind: ScalarKind.Sint, Width: 4 } when value is uint unsigned => unchecked((int)unsigned),
                                var convertedType when convertedType == convert.Operand.Type => value,
                                _ => throw new InvalidOperationException("Unsupported fixture conversion: " + convert)
                            };
                            break;
                        case ValueOperation.Call call: value = Call(call.Function, call.Arguments.Select(Read).ToArray()); break;
                        case ValueOperation.Barrier: effects.Add("barrier"); continue;
                        case ValueOperation.Builtin builtin: value = Builtin(builtin); break;
                        default: throw new InvalidOperationException("Unexpected operation: " + instruction.Operation);
                    }
                    if (instruction.Result is { } result) values[result.Id] = value ?? throw new InvalidOperationException("Missing fixture result.");
                }
                if (block.Terminator is ControlFlowTerminator.Return returned) return returned.Value is { } result ? Read(result) : null;
                var edge = block.Terminator switch {
                    ControlFlowTerminator.Branch branch => branch.Edge,
                    ControlFlowTerminator.Conditional conditional => (bool)Read(conditional.Condition) ? conditional.Accept : conditional.Reject,
                    ControlFlowTerminator.Switch selection => selection.Cases.FirstOrDefault(c => c.Values.Any(v =>
                        System.Convert.ToDouble(v.Value) == System.Convert.ToDouble(Read(selection.Selector))))?.Edge ?? selection.Default,
                    _ => throw new InvalidOperationException("Unexpected fixture control flow.")
                };
                var incoming = edge.Arguments.Select(Read).ToArray(); var target = graph.Blocks.Single(b => b.Id == edge.Target);
                for (int p = 0; p < incoming.Length; p++) values[target.Parameters[p].Id] = incoming[p];
                current = edge.Target;
            }
            throw new InvalidOperationException("Fixture did not terminate.");
        }
        Call(module.Declarations.Functions.Single(f => f.Stage == ShaderStage.Compute).Name, []);
        return (memory, effects.ToArray());
    }
}
