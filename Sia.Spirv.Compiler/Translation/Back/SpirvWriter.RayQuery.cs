using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            // Ray-query handles cannot be loaded or copied. Companion variables
            // track legal operations without ever reading an uninitialized handle.
            private readonly Dictionary<uint, (uint State, uint Min, uint Max)> rayQueryStates = [];
            private void InitializeRayQueryState(uint query)
            {
                uint state = Variable(ShaderType.U32), min = Variable(ShaderType.F32), max = Variable(ShaderType.F32);
                Add(Op.Store, state, owner.Constant(Expression.U32(0)));
                Add(Op.Store, min, owner.Null(ShaderType.F32)); Add(Op.Store, max, owner.Null(ShaderType.F32));
                rayQueryStates.Add(query, (state, min, max));
            }
            private uint RayAnd(uint a, uint b) => Result(Op.LogicalAnd, ShaderType.Bool, a, b);
            private uint RayNot(uint value) => Result(Op.LogicalNot, ShaderType.Bool, value);
            private uint RayFlag(uint value, uint mask) => Result(Op.INotEqual, ShaderType.Bool,
                Result(Op.BitwiseAnd, ShaderType.U32, value, owner.Constant(Expression.U32(mask))), owner.Constant(Expression.U32(0)));
            private void RayGuard(uint condition, Action body)
            {
                uint yes = owner.Id(), done = owner.Id();
                Add(Op.SelectionMerge, done, 0); Add(Op.BranchConditional, condition, yes, done);
                Label(yes); body(); Branch(done); Label(done);
            }
            private uint RayRead(Op operation, ShaderType type, uint query, bool committed) =>
                Result(operation, type, query, owner.Constant(Expression.U32(committed ? 1u : 0u)));

            private uint RayQuery(Expression.Call call)
            {
                uint[] args = call.Arguments.Select(Value).ToArray(); uint query = args[0];
                if (!rayQueryStates.TryGetValue(query, out var tracker)) throw owner.Error("Ray query operations require a local query.", call.Span);
                uint state = Result(Op.Load, ShaderType.U32, tracker.State);
                uint active = RayAnd(RayFlag(state, 2), RayNot(RayFlag(state, 4)));
                switch (call.Function)
                {
                    case "rayQueryInitialize":
                    {
                        var desc = RayQueryTypes.Descriptor;
                        uint[] fields = desc.Members.Select((m, i) => Result(Op.CompositeExtract, m.Type, args[2], (uint)i)).ToArray();
                        uint valid = RayAnd(Result(Op.FOrdLessThanEqual, ShaderType.Bool, fields[2], fields[3]),
                            Result(Op.FOrdGreaterThanEqual, ShaderType.Bool, fields[2], owner.Null(ShaderType.F32)));
                        foreach (int vector in new[] { 4, 5 })
                        {
                            var bools = new ShaderType.Vector(3, ShaderType.Bool);
                            uint invalid = Result(Op.LogicalOr, ShaderType.Bool,
                                Result(Op.Any, ShaderType.Bool, Result(Op.IsNan, bools, fields[vector])),
                                Result(Op.Any, ShaderType.Bool, Result(Op.IsInf, bools, fields[vector])));
                            valid = RayAnd(valid, RayNot(invalid));
                        }
                        // The pinned reference rejects each mutually exclusive flag group.
                        foreach (uint[] group in new uint[][] { [256, 512], [256, 16, 32], [1, 2, 64, 128] })
                            for (int i = 0; i < group.Length; i++) for (int j = i + 1; j < group.Length; j++)
                                valid = RayAnd(valid, RayNot(RayAnd(RayFlag(fields[0], group[i]), RayFlag(fields[0], group[j]))));
                        Add(Op.Store, tracker.Max, fields[3]);
                        RayGuard(valid, () =>
                        {
                            Add(Op.RayQueryInitializeKHR, query, args[1], fields[0], fields[1], fields[4], fields[2], fields[5], fields[3]);
                            Add(Op.Store, tracker.Min, fields[2]); Add(Op.Store, tracker.State, owner.Constant(Expression.U32(1)));
                        });
                        return 0;
                    }
                    case "rayQueryProceed":
                    {
                        uint result = Variable(ShaderType.Bool); Add(Op.Store, result, owner.Constant(Expression.Bool(false)));
                        RayGuard(RayAnd(RayFlag(state, 1), RayNot(RayFlag(state, 4))), () =>
                        {
                            uint proceed = Result(Op.RayQueryProceedKHR, ShaderType.Bool, query); Add(Op.Store, result, proceed);
                            uint flags = Result(Op.Select, ShaderType.U32, proceed, owner.Constant(Expression.U32(2)), owner.Constant(Expression.U32(6)));
                            Add(Op.Store, tracker.State, Result(Op.BitwiseOr, ShaderType.U32, state, flags));
                        });
                        return Result(Op.Load, ShaderType.Bool, result);
                    }
                    case "rayQueryTerminate": RayGuard(active, () => Add(Op.RayQueryTerminateKHR, query)); return 0;
                    case "rayQueryConfirmIntersection":
                        RayGuard(active, () => RayGuard(Result(Op.IEqual, ShaderType.Bool,
                            RayRead(Op.RayQueryGetIntersectionTypeKHR, ShaderType.U32, query, false), owner.Constant(Expression.U32(0))),
                            () => Add(Op.RayQueryConfirmIntersectionKHR, query))); return 0;
                    case "rayQueryGenerateIntersection":
                        RayGuard(active, () =>
                        {
                            uint candidate = RayRead(Op.RayQueryGetIntersectionTypeKHR, ShaderType.U32, query, false);
                            uint latest = Variable(ShaderType.F32); Add(Op.Store, latest, Result(Op.Load, ShaderType.F32, tracker.Max));
                            uint committed = RayRead(Op.RayQueryGetIntersectionTypeKHR, ShaderType.U32, query, true);
                            RayGuard(Result(Op.INotEqual, ShaderType.Bool, committed, owner.Constant(Expression.U32(0))),
                                () => Add(Op.Store, latest, RayRead(Op.RayQueryGetIntersectionTKHR, ShaderType.F32, query, true)));
                            uint inRange = RayAnd(Result(Op.FOrdGreaterThanEqual, ShaderType.Bool, args[1], Result(Op.Load, ShaderType.F32, tracker.Min)),
                                Result(Op.FOrdLessThanEqual, ShaderType.Bool, args[1], Result(Op.Load, ShaderType.F32, latest)));
                            RayGuard(RayAnd(inRange, Result(Op.IEqual, ShaderType.Bool, candidate, owner.Constant(Expression.U32(1)))),
                                () => Add(Op.RayQueryGenerateIntersectionKHR, query, args[1]));
                        }); return 0;
                    case "rayQueryGetCommittedIntersection": case "rayQueryGetCandidateIntersection":
                    {
                        bool committed = call.Function == "rayQueryGetCommittedIntersection";
                        var type = (ShaderType.Structure)call.Type;
                        uint result = Variable(type); Add(Op.Store, result, owner.Null(type));
                        uint ready = RayAnd(RayFlag(state, 2), committed ? RayFlag(state, 4) : RayNot(RayFlag(state, 4)));
                        RayGuard(ready, () =>
                        {
                            uint raw = RayRead(Op.RayQueryGetIntersectionTypeKHR, ShaderType.U32, query, committed);
                            uint kind = committed ? raw : Result(Op.Select, ShaderType.U32,
                                Result(Op.IEqual, ShaderType.Bool, raw, owner.Constant(Expression.U32(0))),
                                owner.Constant(Expression.U32(1)), owner.Constant(Expression.U32(3)));
                            void StoreMember(int member, uint value) => Add(Op.Store,
                                Result(Op.AccessChain, new ShaderType.Pointer(type.Members[member].Type, AddressSpace.Function), result, owner.Constant(Expression.U32((uint)member))), value);
                            StoreMember(0, kind);
                            RayGuard(Result(Op.INotEqual, ShaderType.Bool, kind, owner.Constant(Expression.U32(0))), () =>
                            {
                                Op[] common = [Op.RayQueryGetIntersectionInstanceCustomIndexKHR, Op.RayQueryGetIntersectionInstanceIdKHR,
                                    Op.RayQueryGetIntersectionInstanceShaderBindingTableRecordOffsetKHR, Op.RayQueryGetIntersectionGeometryIndexKHR,
                                    Op.RayQueryGetIntersectionPrimitiveIndexKHR];
                                for (int i = 0; i < common.Length; i++) StoreMember(i + 2, RayRead(common[i], type.Members[i + 2].Type, query, committed));
                                StoreMember(9, RayRead(Op.RayQueryGetIntersectionObjectToWorldKHR, type.Members[9].Type, query, committed));
                                StoreMember(10, RayRead(Op.RayQueryGetIntersectionWorldToObjectKHR, type.Members[10].Type, query, committed));
                                if (committed) StoreMember(1, RayRead(Op.RayQueryGetIntersectionTKHR, ShaderType.F32, query, true));
                                RayGuard(Result(Op.IEqual, ShaderType.Bool, kind, owner.Constant(Expression.U32(1))), () =>
                                {
                                    if (!committed) StoreMember(1, RayRead(Op.RayQueryGetIntersectionTKHR, ShaderType.F32, query, false));
                                    StoreMember(7, RayRead(Op.RayQueryGetIntersectionBarycentricsKHR, type.Members[7].Type, query, committed));
                                    StoreMember(8, RayRead(Op.RayQueryGetIntersectionFrontFaceKHR, ShaderType.Bool, query, committed));
                                });
                            });
                        }); return Result(Op.Load, type, result);
                    }
                    case "getCommittedHitVertexPositions": case "getCandidateHitVertexPositions":
                    {
                        owner.capabilities.Add(5391); owner.extensions.Add("SPV_KHR_ray_tracing_position_fetch");
                        bool committed = call.Function == "getCommittedHitVertexPositions";
                        uint result = Variable(call.Type); Add(Op.Store, result, owner.Null(call.Type));
                        uint ready = RayAnd(RayFlag(state, 2), committed ? RayFlag(state, 4) : RayNot(RayFlag(state, 4)));
                        RayGuard(ready, () => RayGuard(Result(Op.IEqual, ShaderType.Bool,
                            RayRead(Op.RayQueryGetIntersectionTypeKHR, ShaderType.U32, query, committed), owner.Constant(Expression.U32(committed ? 1u : 0u))),
                            () => Add(Op.Store, result, RayRead(Op.RayQueryGetIntersectionTriangleVertexPositionsKHR, call.Type, query, committed))));
                        return Result(Op.Load, call.Type, result);
                    }
                    default: throw owner.Error("Unknown ray query operation.", call.Span);
                }
            }
        }
    }
}
