using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    private sealed partial class Writer
    {
        private readonly HashSet<string> reserved = new(StringComparer.Ordinal);
        private readonly Dictionary<(ShaderType From, ShaderType To), string> bitcastHelpers = [];
        private bool globalExpression;
        private string Expr(Expression value) => WgslWriter.Expr(value, Bitcast);
        private static ShaderType.Scalar Component(ShaderType type) => type is ShaderType.Vector v ? v.Component : (ShaderType.Scalar)type;
        private static int Count(ShaderType type) => type is ShaderType.Vector v ? v.Size : 1;

        private void ReserveNames(Module module)
        {
            reserved.UnionWith(module.Structures.Select(s => s.Name)); reserved.UnionWith(module.Globals.Select(g => g.Name));
            reserved.UnionWith(module.Constants.Select(c => c.Name)); reserved.UnionWith(module.Functions.Select(f => f.Name));
            void Body(Block block)
            {
                foreach (var statement in block.Statements)
                    switch (statement)
                    {
                        case Statement.Declare d: reserved.Add(d.Name); break;
                        case Statement.Nested n: Body(n.Body); break;
                        case Statement.If i: Body(i.Accept); Body(i.Reject); break;
                        case Statement.Loop l: Body(l.Body); Body(l.Continuing); break;
                        case Statement.Switch s: foreach (var c in s.Cases) Body(c.Body); break;
                    }
            }
            foreach (var f in module.Functions) { reserved.UnionWith(f.Arguments.Select(a => a.Name)); Body(f.Body); }
        }

        private string Bitcast(Expression.Convert conversion)
        {
            ShaderType from = conversion.Operand.Type, to = conversion.Type;
            var a = Component(from); var b = Component(to); string value = Expr(conversion.Operand);
            bool NarrowInteger(ShaderType.Scalar s) => s.Width == 2 && s.Kind is ScalarKind.Sint or ScalarKind.Uint;
            if (!NarrowInteger(a) && !NarrowInteger(b)) return $"bitcast<{TypeName(to)}>({value})";
            if (NarrowInteger(a) && NarrowInteger(b) && Count(from) == Count(to)) return $"{TypeName(to)}({value})";
            if (globalExpression) return Repack(from, to, value, false).Result;
            if (!bitcastHelpers.TryGetValue((from, to), out string? name))
            {
                int index = bitcastHelpers.Count;
                do name = "naga_bitcast_" + index++; while (!reserved.Add(name));
                bitcastHelpers.Add((from, to), name);
            }
            return $"{name}({value})";
        }

        // Pack little-endian 32-bit words, then rebuild destination components.
        // Helpers take the source once, preserving function-call side effects.
        private static (string[] Words, string Result) Repack(ShaderType from, ShaderType to, string value, bool namedWords)
        {
            var a = Component(from); var b = Component(to);
            string Part(int i) => Count(from) == 1 ? $"({value})" : $"({value}).{"xyzw"[i]}";
            string SourceWord(int index)
            {
                if (a.Width == 2)
                {
                    string Half(int i) => i >= Count(from) ? "0u" : a.Kind == ScalarKind.Float
                        ? $"bitcast<u32>(vec2<f16>({Part(i)}, 0h))" : $"u32(u16({Part(i)}))";
                    return $"({Half(index * 2)} | ({Half(index * 2 + 1)} << 16u))";
                }
                if (a.Width == 4) return a.Kind == ScalarKind.Uint ? Part(index) : $"bitcast<u32>({Part(index)})";
                string wide = a.Kind == ScalarKind.Uint ? Part(index / 2) : $"bitcast<u64>({Part(index / 2)})";
                return index % 2 == 0 ? $"u32({wide})" : $"u32({wide} >> 32u)";
            }
            int wordCount = (Count(from) * a.Width + 3) / 4;
            string[] words = Enumerable.Range(0, wordCount).Select(SourceWord).ToArray();
            string Word(int index) => namedWords ? "word" + index : words[index];
            string Target(int index)
            {
                string bits;
                if (b.Width == 2)
                {
                    bits = index % 2 == 0 ? Word(index / 2) : $"({Word(index / 2)} >> 16u)";
                    return b.Kind == ScalarKind.Float ? $"bitcast<vec2<f16>>({bits}).x" : $"{TypeName(b)}({bits})";
                }
                bits = b.Width == 4 ? Word(index) : $"(u64({Word(index * 2)}) | (u64({Word(index * 2 + 1)}) << 32u))";
                return b.Kind == ScalarKind.Uint ? bits : $"bitcast<{TypeName(b)}>({bits})";
            }
            string result = Count(to) == 1 ? Target(0) : $"{TypeName(to)}({string.Join(", ", Enumerable.Range(0, Count(to)).Select(Target))})";
            return (words, result);
        }

        private void WriteBitcastHelpers()
        {
            foreach (var ((from, to), name) in bitcastHelpers)
            {
                Line(); Open($"fn {name}(value: {TypeName(from)}) -> {TypeName(to)}");
                var (words, result) = Repack(from, to, "value", true);
                for (int i = 0; i < words.Length; i++) Line($"let word{i}: u32 = {words[i]};");
                Line($"return {result};"); Close();
            }
        }
    }
}
