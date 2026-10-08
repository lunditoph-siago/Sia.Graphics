using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private readonly Dictionary<uint, (Expression Image, Expression Sampler)> sampledImageValues = [];
        private sealed record ImageOperands(Expression? Bias = null, Expression? Level = null, Expression? Ddx = null, Expression? Ddy = null, Expression? Offset = null, Expression? Sample = null);

        private void UpgradeComparisonResources()
        {
            var roots = globals.Keys.ToDictionary(id => id, id => id);
            var pairs = new Dictionary<uint, (uint Image, uint Sampler)>();
            foreach (var instruction in binary.Instructions)
            {
                var a = instruction.Operands; Op op = (Op)instruction.Opcode;
                if (op is Op.Load or Op.CopyObject or Op.AccessChain or Op.InBoundsAccessChain && a.Length >= 3 && roots.TryGetValue(a[2], out uint root)) roots[a[1]] = root;
                if (op == Op.SampledImage && a.Length == 4) pairs[a[1]] = (a[2], a[3]);
            }
            foreach (var instruction in binary.Instructions.Where(i => (Op)i.Opcode is Op.ImageSampleDrefImplicitLod or Op.ImageSampleDrefExplicitLod or Op.ImageDrefGather))
            {
                current = instruction; var a = instruction.Operands; Count(5);
                if (!pairs.TryGetValue(a[2], out var pair)) throw Error("Comparison sampling requires a sampled image.");
                void Upgrade(uint value, bool sampler)
                {
                    if (!roots.TryGetValue(value, out uint id)) throw Error("Comparison resource parameter typing is not supported yet.");
                    ShaderType UpgradeType(ShaderType old) => old is ShaderType.BindingArray array ? array with { Element = UpgradeType(array.Element) } : sampler ? new ShaderType.Sampler(true) : old is ShaderType.Image image ? image with { Depth = true } : throw Error("Comparison sampling requires a texture.");
                    var global = globals[id]; ShaderType type = UpgradeType(global.Variable.Type);
                    var upgraded = global.Variable with { Type = type };
                    module.Globals[module.Globals.IndexOf(global.Variable)] = upgraded;
                    globals[id] = (upgraded, global.Storage, global.Binding); values[id] = new Expression.Reference(upgraded.Name, type);
                }
                Upgrade(pair.Sampler, true); Upgrade(pair.Image, false);
            }
        }

        private ImageOperands ReadImageOperands(uint[] a, int cursor)
        {
            if (cursor == a.Length) return new();
            uint mask = a[cursor++];
            if ((mask & ~95u) != 0) throw Error("Unsupported image operand flags.");
            Expression? Next(uint bit)
            {
                if ((mask & bit) == 0) return null;
                if (cursor >= a.Length) throw Error("Truncated image operands.");
                return Value(a[cursor++]);
            }
            Expression? bias = Next(1), level = Next(2), dx = Next(4), dy = Next(4), offset = Next(8), dynamicOffset = Next(16), sample = Next(64);
            if (cursor != a.Length || offset is not null && dynamicOffset is not null) throw Error("Invalid image operand combination.");
            return new(bias, level, dx, dy, offset ?? dynamicOffset, sample);
        }
        private Expression ToI32(Expression value) => value.Type == ShaderType.I32 ? value : new Expression.Convert(ShaderType.I32, value);
        private (Expression Coordinate, Expression? Layer) Coordinates(ShaderType.Image image, Expression value)
        {
            int rank = image.Dimension switch { ImageDimension.D1 => 1, ImageDimension.D2 => 2, _ => 3 };
            ShaderType.Scalar scalar = value.Type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, _ => throw Error("Texture coordinate must be scalar/vector.") };
            int given = value.Type is ShaderType.Vector vector ? vector.Size : 1;
            if (given < rank + (image.Arrayed ? 1 : 0)) throw Error("Texture coordinate has too few components.");
            Expression coordinate = given == rank ? value : rank == 1 ? Index(value, Expression.U32(0)) : new Expression.Swizzle(value, "xyz"[..rank], new ShaderType.Vector(rank, scalar));
            Expression? layer = image.Arrayed ? ToI32(Index(value, Expression.U32((uint)rank))) : null;
            return (coordinate, layer);
        }
        private Expression ImageLoad(uint[] a)
        {
            Count(4); Expression texture = Value(a[2]);
            if (texture.Type is not ShaderType.Image image) throw Error("Image load requires a texture.");
            var (coordinate, layer) = Coordinates(image, Value(a[3])); var operands = ReadImageOperands(a, 4);
            if (operands.Bias is not null || operands.Ddx is not null || operands.Offset is not null) throw Error("Unsupported image-load operands.");
            var args = new List<Expression> { texture, coordinate }; if (layer is not null) args.Add(layer);
            if (image.StorageFormat is null) args.Add(image.Multisampled ? operands.Sample ?? throw Error("Multisampled fetch requires sample index.") : operands.Level ?? Expression.I32(0));
            else if (operands.Sample is not null || operands.Level is not null) throw Error("Storage image load has sampling operands.");
            ShaderType result = image.Depth ? ShaderType.F32 : new ShaderType.Vector(4, image.Component);
            Expression call = new Expression.Call("textureLoad", args, result);
            return image.Depth && Type(a[0]) is ShaderType.Vector v ? new Expression.Construct(v, [call]) : call;
        }
        private Expression ImageStore(uint[] a)
        {
            Count(3); Expression texture = Value(a[0]);
            if (texture.Type is not ShaderType.Image image || image.StorageFormat is null) throw Error("Image write requires a storage texture.");
            var (coordinate, layer) = Coordinates(image, Value(a[1])); var operands = ReadImageOperands(a, 3);
            if (operands != new ImageOperands()) throw Error("Storage image writes with extra operands are not supported yet.");
            var args = new List<Expression> { texture, coordinate }; if (layer is not null) args.Add(layer); args.Add(Value(a[2]));
            return new Expression.Call("textureStore", args, new ShaderType.Void());
        }
        private Expression ImageSample(Op op, uint[] a)
        {
            bool compare = op is Op.ImageSampleDrefImplicitLod or Op.ImageSampleDrefExplicitLod or Op.ImageDrefGather;
            bool gather = op is Op.ImageGather or Op.ImageDrefGather;
            Count(compare || gather ? 5 : 4);
            if (!sampledImageValues.TryGetValue(a[2], out var pair) || pair.Image.Type is not ShaderType.Image image) throw Error("Undefined sampled texture.");
            var (coordinate, layer) = Coordinates(image, Value(a[3])); int cursor = 4;
            Expression? depth = compare ? Value(a[cursor++]) : null;
            Expression? component = gather && !compare ? Value(a[cursor++]) : null;
            var operands = ReadImageOperands(a, cursor);
            if (operands.Sample is not null) throw Error("Sampling cannot specify a multisample index.");
            string name = gather ? compare ? "textureGatherCompare" : "textureGather" : compare ? operands.Level is not null ? "textureSampleCompareLevel" : "textureSampleCompare" : operands.Ddx is not null ? "textureSampleGrad" : operands.Level is not null ? "textureSampleLevel" : operands.Bias is not null ? "textureSampleBias" : "textureSample";
            var args = new List<Expression>(); if (component is not null && !image.Depth) args.Add(ToI32(component));
            args.Add(pair.Image); args.Add(pair.Sampler); args.Add(coordinate); if (layer is not null) args.Add(layer); if (depth is not null) args.Add(depth);
            if (compare && operands.Level is not null)
            {
                if (!ConstantEvaluator.TryEvaluate(operands.Level, out var level) || level is not Expression.Literal literal || System.Convert.ToDouble(literal.Value) != 0) throw Error("WGSL comparison sampling only supports level zero.");
            }
            else if (operands.Level is not null) args.Add(image.Depth ? ToI32(operands.Level) : operands.Level.Type == ShaderType.F32 ? operands.Level : new Expression.Convert(ShaderType.F32, operands.Level));
            if (operands.Bias is not null) args.Add(operands.Bias);
            if (operands.Ddx is not null) { args.Add(operands.Ddx); args.Add(operands.Ddy!); }
            if (operands.Offset is not null) args.Add(operands.Offset);
            ShaderType result = !gather && (compare || image.Depth) ? ShaderType.F32 : new ShaderType.Vector(4, image.Component);
            Expression call = new Expression.Call(name, args, result);
            return image.Depth && !compare && !gather && Type(a[0]) is ShaderType.Vector v ? new Expression.Construct(v, [call]) : call;
        }
        private Expression ImageQuery(Op op, uint[] a)
        {
            Count(op == Op.ImageQuerySizeLod ? 4 : 3, op == Op.ImageQuerySizeLod ? 4 : 3);
            Expression texture = Value(a[2]); if (texture.Type is not ShaderType.Image image) throw Error("Image query requires a texture.");
            string name = op == Op.ImageQueryLevels ? "textureNumLevels" : op == Op.ImageQuerySamples ? "textureNumSamples" : "textureDimensions";
            int rank = image.Dimension switch { ImageDimension.D1 => 1, ImageDimension.D3 => 3, _ => 2 };
            ShaderType dimensions = rank == 1 ? ShaderType.U32 : new ShaderType.Vector(rank, ShaderType.U32);
            ShaderType result = name == "textureDimensions" ? dimensions : ShaderType.U32;
            var args = new List<Expression> { texture }; if (op == Op.ImageQuerySizeLod) args.Add(Value(a[3]));
            Expression query = new Expression.Call(name, args, result);
            if (name == "textureDimensions" && image.Arrayed)
            {
                query = new Expression.Construct(new ShaderType.Vector(rank + 1, ShaderType.U32), [query, new Expression.Call("textureNumLayers", [texture], ShaderType.U32)]);
            }
            return query.Type == Type(a[0]) ? query : new Expression.Convert(Type(a[0]), query);
        }
    }
}
