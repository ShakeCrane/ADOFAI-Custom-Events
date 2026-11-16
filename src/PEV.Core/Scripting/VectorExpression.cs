using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    /// <summary>
    /// A bounded byte-vector expression. The event can choose only the operations below;
    /// emitted code cannot name CLR types, call arbitrary methods or access game objects.
    /// </summary>
    internal static class VectorExpression
    {
        internal delegate void Program(byte[][] inputs, byte[] output, int length, Action checkpoint);
        private enum Op { Input, Constant, Get, Set, Add, Subtract, Multiply, Divide, GreaterEqual, LessEqual, Clamp, Unorm }
        private struct Instruction { public Op Code; public int Index; public float Value; }

        internal static Program Compile(JToken source, int inputCount)
        {
            if (!(source is JArray list) || list.Count < 1 || list.Count > 128 || inputCount < 1 || inputCount > 16)
                throw new ArgumentException("invalid vector expression size");
            var code = new List<Instruction>(list.Count);
            int depth = 0, initialized = 0;
            foreach (var item in list)
            {
                if (!(item is JArray args) || args.Count < 1 || args[0].Type != JTokenType.String)
                    throw new ArgumentException("invalid vector instruction");
                string name = args[0].Value<string>();
                var instruction = new Instruction();
                switch (name)
                {
                    case "in": instruction.Code = Op.Input; break;
                    case "c": instruction.Code = Op.Constant; break;
                    case "get": instruction.Code = Op.Get; break;
                    case "set": instruction.Code = Op.Set; break;
                    case "add": instruction.Code = Op.Add; break;
                    case "sub": instruction.Code = Op.Subtract; break;
                    case "mul": instruction.Code = Op.Multiply; break;
                    case "div": instruction.Code = Op.Divide; break;
                    case "ge": instruction.Code = Op.GreaterEqual; break;
                    case "le": instruction.Code = Op.LessEqual; break;
                    case "clamp": instruction.Code = Op.Clamp; break;
                    case "unorm": instruction.Code = Op.Unorm; break;
                    default: throw new ArgumentException("unknown vector instruction");
                }
                bool hasArgument = instruction.Code == Op.Input || instruction.Code == Op.Constant ||
                    instruction.Code == Op.Get || instruction.Code == Op.Set;
                if (args.Count != (hasArgument ? 2 : 1)) throw new ArgumentException("invalid vector instruction arguments");
                if (instruction.Code == Op.Constant)
                {
                    if (args[1].Type != JTokenType.Integer && args[1].Type != JTokenType.Float)
                        throw new ArgumentException("invalid vector constant");
                    instruction.Value = args[1].Value<float>();
                    if (float.IsNaN(instruction.Value) || float.IsInfinity(instruction.Value))
                        throw new ArgumentException("non-finite vector constant");
                }
                else if (hasArgument)
                {
                    if (args[1].Type != JTokenType.Integer) throw new ArgumentException("invalid vector index");
                    instruction.Index = args[1].Value<int>();
                }
                switch (instruction.Code)
                {
                    case Op.Input:
                        if (instruction.Index < 0 || instruction.Index >= inputCount) throw new ArgumentException("invalid vector input");
                        depth++; break;
                    case Op.Constant: depth++; break;
                    case Op.Get:
                        if (instruction.Index < 0 || instruction.Index >= 8 || (initialized & (1 << instruction.Index)) == 0)
                            throw new ArgumentException("uninitialized vector register");
                        depth++; break;
                    case Op.Set:
                        if (instruction.Index < 0 || instruction.Index >= 8 || depth < 1) throw new ArgumentException("invalid vector register");
                        initialized |= 1 << instruction.Index; depth--; break;
                    case Op.Clamp: case Op.Unorm:
                        if (depth < 1) throw new ArgumentException("vector stack underflow");
                        break;
                    default:
                        if (depth < 2) throw new ArgumentException("vector stack underflow");
                        depth--; break;
                }
                if (depth > 32) throw new ArgumentException("vector stack overflow");
                code.Add(instruction);
            }
            if (depth != 1) throw new ArgumentException("invalid vector result stack");

            var method = new DynamicMethod("PEV_VectorExpression", typeof(void),
                new[] { typeof(byte[][]), typeof(byte[]), typeof(int), typeof(Action) }, typeof(VectorExpression), true);
            var il = method.GetILGenerator();
            var registers = new LocalBuilder[8];
            for (int i = 0; i < registers.Length; i++) registers[i] = il.DeclareLocal(typeof(float));
            var scratch = il.DeclareLocal(typeof(float));
            var whole = il.DeclareLocal(typeof(int));
            void EmitClamp(float maximum)
            {
                var finite = il.DefineLabel(); var nonnegative = il.DefineLabel(); var done = il.DefineLabel();
                il.Emit(OpCodes.Stloc, scratch);
                il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brtrue, finite);
                il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Stloc, scratch);
                il.MarkLabel(finite);
                il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Bge, nonnegative);
                il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Stloc, scratch);
                il.MarkLabel(nonnegative);
                il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Ldc_R4, maximum); il.Emit(OpCodes.Ble, done);
                il.Emit(OpCodes.Ldc_R4, maximum); il.Emit(OpCodes.Stloc, scratch);
                il.MarkLabel(done); il.Emit(OpCodes.Ldloc, scratch);
            }
            var arrays = new LocalBuilder[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                arrays[i] = il.DeclareLocal(typeof(byte[]));
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Stloc, arrays[i]);
            }
            var index = il.DeclareLocal(typeof(int));
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index);
            var loop = il.DefineLabel(); var skipCheck = il.DefineLabel(); var end = il.DefineLabel();
            il.MarkLabel(loop);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Bge, end);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4, 4095); il.Emit(OpCodes.And); il.Emit(OpCodes.Brtrue, skipCheck);
            il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
            il.MarkLabel(skipCheck);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, index);
            foreach (var q in code)
            {
                switch (q.Code)
                {
                    case Op.Input: il.Emit(OpCodes.Ldloc, arrays[q.Index]); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldelem_U1); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Constant: il.Emit(OpCodes.Ldc_R4, q.Value); break;
                    case Op.Get: il.Emit(OpCodes.Ldloc, registers[q.Index]); break;
                    case Op.Set: il.Emit(OpCodes.Stloc, registers[q.Index]); break;
                    case Op.Add: il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Subtract: il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Multiply: il.Emit(OpCodes.Mul); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Divide: il.Emit(OpCodes.Div); il.Emit(OpCodes.Conv_R4); break;
                    case Op.GreaterEqual: il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4); break;
                    case Op.LessEqual: il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Clamp: EmitClamp(1f); break;
                    case Op.Unorm:
                        EmitClamp(1f); il.Emit(OpCodes.Ldc_R4, 255f); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Stloc, scratch);
                        il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Stloc, whole);
                        il.Emit(OpCodes.Ldloc, whole); il.Emit(OpCodes.Conv_R4);
                        il.Emit(OpCodes.Ldloc, scratch); il.Emit(OpCodes.Ldloc, whole); il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Sub);
                        il.Emit(OpCodes.Ldc_R4, .5f); il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4);
                        il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_R4); break;
                }
            }
            EmitClamp(255f); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stelem_I1);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
            il.Emit(OpCodes.Br, loop); il.MarkLabel(end); il.Emit(OpCodes.Ret);
            return (Program)method.CreateDelegate(typeof(Program));
        }
    }
}
