using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<uint, uint> sampledImages = [];
        private uint SampledImageType(ShaderType.Image image)
        {
            uint type = Type(image);
            if (sampledImages.TryGetValue(type, out uint id)) return id;
            id = Id(); declarations.Add(I(Op.TypeSampledImage, id, type)); sampledImages.Add(type, id); return id;
        }

        private sealed partial class FunctionEmitter
        {
            private uint Texture(Expression.Call call)
            {
                string name = call.Function;
                int imageIndex = name == "textureGather" && call.Arguments[0].Type is not ShaderType.Image ? 1 : 0;
                if (call.Arguments[imageIndex].Type is not ShaderType.Image image) throw owner.Error("Texture operation needs an image.");
                if (name.StartsWith("textureAtomic", StringComparison.Ordinal)) return ImageAtomic(call, image);
                uint[] args = call.Arguments.Select(Value).ToArray(); uint texture = args[imageIndex];
                int rank = image.Dimension switch { ImageDimension.D1 => 1, ImageDimension.D3 => 3, _ => 2 };
                if (name is "textureDimensions" or "textureNumLayers" or "textureNumLevels" or "textureNumSamples")
                {
                    owner.capabilities.Add(50);
                    if (name is "textureNumLevels" or "textureNumSamples") return Result(name == "textureNumLevels" ? Op.ImageQueryLevels : Op.ImageQuerySamples, call.Type, texture);
                    int fullRank = rank + (image.Arrayed ? 1 : 0);
                    ShaderType fullType = fullRank == 1 ? ShaderType.U32 : new ShaderType.Vector(fullRank, ShaderType.U32);
                    uint size;
                    if (image.Multisampled || image.StorageFormat is not null) size = Result(Op.ImageQuerySize, fullType, texture);
                    else size = Result(Op.ImageQuerySizeLod, fullType, texture, args.Length > 1 ? args[1] : owner.Constant(Expression.I32(0)));
                    if (name == "textureNumLayers") return Result(Op.CompositeExtract, ShaderType.U32, size, (uint)rank);
                    if (!image.Arrayed) return size;
                    if (rank == 1) return Result(Op.CompositeExtract, call.Type, size, 0);
                    return Result(Op.VectorShuffle, call.Type, new uint[] { size, size }.Concat(Enumerable.Range(0, rank).Select(i => (uint)i)).ToArray());
                }
                if (name is "textureLoad" or "textureStore")
                {
                    int next = imageIndex + 1;
                    uint coordinate = args[next]; ShaderType coordinateType = call.Arguments[next++].Type;
                    if (image.Arrayed) coordinate = AppendCoordinate(coordinate, coordinateType, args[next], call.Arguments[next++].Type);
                    if (name == "textureStore")
                    {
                        if (image.StorageFormat == "bgra8unorm") owner.capabilities.Add(56); // StorageImageWriteWithoutFormat
                        Add(Op.ImageWrite, texture, coordinate, args[next]); return 0;
                    }
                    ShaderType valueType = new ShaderType.Vector(4, image.Component);
                    uint value;
                    if (image.StorageFormat is not null)
                    {
                        if (image.StorageFormat == "bgra8unorm") owner.capabilities.Add(55); // StorageImageReadWithoutFormat
                        value = Result(Op.ImageRead, valueType, texture, coordinate);
                    }
                    else if (image.Multisampled) value = Result(Op.ImageFetch, valueType, texture, coordinate, 64, args[next]);
                    else value = Result(Op.ImageFetch, valueType, texture, coordinate, 2, args[next]);
                    return image.Depth ? Result(Op.CompositeExtract, call.Type, value, 0) : value;
                }
                int cursor = imageIndex + 1;
                if (cursor >= args.Length) throw owner.Error("Sampling operation needs a sampler.");
                uint sampler = args[cursor++];
                uint sampled = owner.Id(); Add(Op.SampledImage, owner.SampledImageType(image), sampled, texture, sampler);
                if (owner.nonUniformValues.Contains(texture) || owner.nonUniformValues.Contains(sampler)) owner.NonUniform(sampled, image);
                uint coordinates = args[cursor]; ShaderType coordinatesType = call.Arguments[cursor++].Type;
                if (name == "textureSampleBaseClampToEdge")
                {
                    owner.capabilities.Add(50);
                    var dimensionsType = new ShaderType.Vector(2, ShaderType.U32);
                    uint dimensions = Result(Op.ImageQuerySizeLod, dimensionsType, texture, owner.Constant(Expression.I32(0)));
                    uint floatingDimensions = Result(Op.ConvertUToF, coordinatesType, dimensions);
                    uint halfTexel = Result(Op.FDiv, coordinatesType, LiteralSplat(0.5f, coordinatesType), floatingDimensions);
                    uint upper = Result(Op.FSub, coordinatesType, One(coordinatesType), halfTexel);
                    coordinates = Glsl(43, coordinatesType, coordinates, halfTexel, upper);
                }
                if (image.Arrayed) coordinates = AppendCoordinate(coordinates, coordinatesType, args[cursor], call.Arguments[cursor++].Type);
                bool comparison = name is "textureSampleCompare" or "textureSampleCompareLevel" or "textureGatherCompare";
                uint depthRef = comparison ? FloatArgument(args[cursor], call.Arguments[cursor++].Type) : 0;
                var extra = new List<uint>(); uint mask = 0;
                if (name == "textureSampleLevel") { mask |= 2; extra.Add(FloatArgument(args[cursor], call.Arguments[cursor++].Type)); }
                else if (name == "textureSampleBias") { mask |= 1; extra.Add(FloatArgument(args[cursor], call.Arguments[cursor++].Type)); }
                else if (name == "textureSampleGrad") { mask |= 4; extra.Add(args[cursor++]); extra.Add(args[cursor++]); }
                else if (name == "textureSampleCompareLevel") { mask |= 2; extra.Add(owner.Constant(new Expression.Literal(0f, ShaderType.F32))); }
                else if (name == "textureSampleBaseClampToEdge") { mask |= 2; extra.Add(owner.Constant(new Expression.Literal(0f, ShaderType.F32))); }
                if (cursor < args.Length) { mask |= 8; extra.Add(args[cursor++]); }
                if (cursor != args.Length) throw owner.Error("Invalid texture sampling argument count.");
                var operands = new List<uint> { sampled, coordinates };
                Op op;
                bool gather = name is "textureGather" or "textureGatherCompare";
                if (gather)
                {
                    op = comparison ? Op.ImageDrefGather : Op.ImageGather;
                    operands.Add(comparison ? depthRef : imageIndex == 1 ? args[0] : owner.Constant(Expression.I32(0)));
                }
                else
                {
                    op = comparison ? (mask & 6) != 0 ? Op.ImageSampleDrefExplicitLod : Op.ImageSampleDrefImplicitLod
                        : (mask & 6) != 0 ? Op.ImageSampleExplicitLod : Op.ImageSampleImplicitLod;
                    if (comparison) operands.Add(depthRef);
                }
                if (mask != 0) { operands.Add(mask); operands.AddRange(extra); }
                ShaderType sampleType = comparison && !gather ? ShaderType.F32 : new ShaderType.Vector(4, image.Component);
                uint sample = Result(op, sampleType, operands.ToArray());
                return image.Depth && !comparison && !gather ? Result(Op.CompositeExtract, call.Type, sample, 0) : sample;
            }

            private uint ImageAtomic(Expression.Call call, ShaderType.Image image)
            {
                uint address = Place(call.Arguments[0]);
                uint coordinate = Value(call.Arguments[1]);
                if (image.Arrayed) coordinate = AppendCoordinate(coordinate, call.Arguments[1].Type, Value(call.Arguments[2]), call.Arguments[2].Type);
                uint pointerType = owner.Pointer(owner.Type(image.Component), 11), pointer = owner.Id();
                Add(Op.ImageTexelPointer, pointerType, pointer, address, coordinate, owner.Constant(Expression.U32(0)));
                if (owner.nonUniformValues.Contains(address)) owner.NonUniform(pointer, image);
                if (image.Component.Width == 8) owner.capabilities.Add(12);
                Op op = call.Function switch
                {
                    "textureAtomicAdd" => Op.AtomicIAdd,
                    "textureAtomicMin" => image.Component.Kind == ScalarKind.Sint ? Op.AtomicSMin : Op.AtomicUMin,
                    "textureAtomicMax" => image.Component.Kind == ScalarKind.Sint ? Op.AtomicSMax : Op.AtomicUMax,
                    "textureAtomicAnd" => Op.AtomicAnd, "textureAtomicOr" => Op.AtomicOr, "textureAtomicXor" => Op.AtomicXor,
                    _ => throw owner.Error("Unsupported image atomic operation.")
                };
                var (scope, semantics, _) = AtomicOperands(call);
                _ = Result(op, image.Component, pointer, scope, semantics, Value(call.Arguments[^1]));
                return 0;
            }

            private uint FloatArgument(uint value, ShaderType type) => Scalar(type).Kind switch
            { ScalarKind.Float => value, ScalarKind.Uint => Result(Op.ConvertUToF, ShaderType.F32, value), _ => Result(Op.ConvertSToF, ShaderType.F32, value) };

            private uint AppendCoordinate(uint coordinates, ShaderType type, uint layer, ShaderType layerType)
            {
                ShaderType.Scalar scalar = Scalar(type);
                int count = type is ShaderType.Vector vector ? vector.Size : 1;
                var elements = new List<uint>();
                if (count == 1) elements.Add(coordinates);
                else for (int i = 0; i < count; i++) elements.Add(Result(Op.CompositeExtract, scalar, coordinates, (uint)i));
                var layerScalar = Scalar(layerType);
                if (scalar.Kind != ScalarKind.Float && scalar.Width != layerScalar.Width)
                {
                    var resized = new ShaderType.Scalar(layerScalar.Kind, scalar.Width);
                    layer = Result(layerScalar.Kind == ScalarKind.Uint ? Op.UConvert : Op.SConvert, resized, layer);
                }
                if (scalar.Kind != layerScalar.Kind)
                {
                    layer = scalar.Kind == ScalarKind.Float ? FloatArgument(layer, layerType) : Result(Op.Bitcast, scalar, layer);
                }
                elements.Add(layer); return Result(Op.CompositeConstruct, new ShaderType.Vector(count + 1, scalar), elements.ToArray());
            }
        }
    }
}
