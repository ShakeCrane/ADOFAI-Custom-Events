using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // A bounded scalar pixel program. The event chooses only the operations below;
    // generated IL cannot name CLR members, execute loops, or access Unity/game objects.
    internal static class PixelKernel
    {
        private enum Op { Constant, U, V, X, Y, Uniform, Get, Set, Sample, Add, Sub, Mul, Div,
            Min, Max, Abs, Floor, Fract, Sin, Cos, Sqrt, Pow, Ge, Le, Eq, Select, Clamp }
        private struct Instruction { internal Op Code; internal int A, B; internal float Value; }
        internal delegate void Runner(PixelPlane[] sources, float[] uniforms, float[] output, int width, int height, Action checkpoint);
        internal sealed class Compiled
        {
            private readonly Runner _runner;
            private readonly int[] _channels;
            private readonly int _uniforms, _work;
            internal Compiled(Runner runner, int[] channels, int uniforms, int work)
            { _runner = runner; _channels = channels; _uniforms = uniforms; _work = work; }
            internal void Execute(PixelPlane[] sources, float[] uniforms, float[] output, int width, int height,
                CancellationToken cancellation, Action additionalCheckpoint = null, int timeoutSeconds = 2)
            {
                if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 1048576 ||
                    sources == null || sources.Length != _channels.Length || uniforms == null || uniforms.Length != _uniforms ||
                    output == null || output.LongLength != (long)width * height || (long)width * height * _work > 350000000 ||
                    timeoutSeconds < 1 || timeoutSeconds > 15)
                    throw new ArgumentException("Invalid pixel kernel inputs");
                long memory = output.LongLength * sizeof(float);
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] == null || sources[i].Channels < _channels[i] || ReferenceEquals(sources[i].Data, output))
                        throw new ArgumentException("Invalid pixel kernel source");
                    memory += sources[i].Data.LongLength * sizeof(float);
                }
                if (memory > 128L * 1024 * 1024) throw new ArgumentException("Pixel kernel memory limit exceeded");
                foreach (float number in uniforms)
                    if (float.IsNaN(number) || float.IsInfinity(number)) throw new ArgumentException("Invalid pixel uniform");
                long deadline = Stopwatch.GetTimestamp() + timeoutSeconds * Stopwatch.Frequency;
                Action check = () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("Pixel kernel exceeded its time limit");
                    additionalCheckpoint?.Invoke();
                };
                check(); _runner(sources, uniforms, output, width, height, check);
            }
        }

        private static readonly Dictionary<string, Op> Names = new Dictionary<string, Op>(StringComparer.Ordinal)
        {
            ["c"] = Op.Constant, ["u"] = Op.U, ["v"] = Op.V, ["x"] = Op.X, ["y"] = Op.Y,
            ["uniform"] = Op.Uniform, ["get"] = Op.Get, ["set"] = Op.Set, ["sample"] = Op.Sample,
            ["add"] = Op.Add, ["sub"] = Op.Sub, ["mul"] = Op.Mul, ["div"] = Op.Div,
            ["min"] = Op.Min, ["max"] = Op.Max, ["abs"] = Op.Abs, ["floor"] = Op.Floor,
            ["fract"] = Op.Fract, ["sin"] = Op.Sin, ["cos"] = Op.Cos, ["sqrt"] = Op.Sqrt,
            ["pow"] = Op.Pow, ["ge"] = Op.Ge, ["le"] = Op.Le, ["eq"] = Op.Eq,
            ["select"] = Op.Select, ["clamp"] = Op.Clamp
        };
        private static int Index(JToken token, int upper)
        {
            if (token?.Type != JTokenType.Integer) throw new ArgumentException("Invalid pixel instruction index");
            long value = token.Value<long>();
            if (value < 0 || value >= upper) throw new ArgumentOutOfRangeException("pixel instruction index");
            return (int)value;
        }
        internal static Compiled Compile(JToken source, int sourceCount, int uniformCount)
        {
            if (!(source is JArray list) || list.Count < 1 || list.Count > 256 || sourceCount < 0 || sourceCount > 16 ||
                uniformCount < 0 || uniformCount > 128) throw new ArgumentException("Invalid pixel program size");
            var instructions = new List<Instruction>(list.Count);
            var channels = new int[sourceCount];
            int depth = 0, initialized = 0;
            foreach (var item in list)
            {
                if (!(item is JArray args) || args.Count < 1 || args[0].Type != JTokenType.String ||
                    !Names.TryGetValue(args[0].Value<string>(), out var op)) throw new ArgumentException("Invalid pixel instruction");
                var instruction = new Instruction { Code = op };
                int length = op == Op.Sample ? 3 : op == Op.Constant || op == Op.Uniform || op == Op.Get || op == Op.Set ? 2 : 1;
                if (args.Count != length) throw new ArgumentException("Invalid pixel instruction arguments");
                switch (op)
                {
                    case Op.Constant:
                        if (args[1].Type != JTokenType.Float && args[1].Type != JTokenType.Integer) throw new ArgumentException("Invalid pixel constant");
                        instruction.Value = args[1].Value<float>();
                        if (float.IsNaN(instruction.Value) || float.IsInfinity(instruction.Value)) throw new ArgumentException("Invalid pixel constant");
                        depth++; break;
                    case Op.U: case Op.V: case Op.X: case Op.Y: depth++; break;
                    case Op.Uniform: instruction.A = Index(args[1], uniformCount); depth++; break;
                    case Op.Get:
                        instruction.A = Index(args[1], 16);
                        if ((initialized & (1 << instruction.A)) == 0) throw new ArgumentException("Uninitialized pixel register");
                        depth++; break;
                    case Op.Set:
                        instruction.A = Index(args[1], 16);
                        if (depth < 1) throw new ArgumentException("Pixel stack underflow");
                        initialized |= 1 << instruction.A; depth--; break;
                    case Op.Sample:
                        instruction.A = Index(args[1], sourceCount); instruction.B = Index(args[2], 4);
                        channels[instruction.A] = Math.Max(channels[instruction.A], instruction.B + 1);
                        if (depth < 2) throw new ArgumentException("Pixel stack underflow");
                        depth--; break;
                    case Op.Abs: case Op.Floor: case Op.Fract: case Op.Sin: case Op.Cos: case Op.Sqrt: case Op.Clamp:
                        if (depth < 1) throw new ArgumentException("Pixel stack underflow"); break;
                    case Op.Select:
                        if (depth < 3) throw new ArgumentException("Pixel stack underflow"); depth -= 2; break;
                    default:
                        if (depth < 2) throw new ArgumentException("Pixel stack underflow"); depth--; break;
                }
                if (depth > 32) throw new ArgumentException("Pixel stack overflow");
                instructions.Add(instruction);
            }
            if (depth != 1) throw new ArgumentException("Invalid pixel result stack");
            return new Compiled(Emit(instructions), channels, uniformCount, list.Count);
        }
        private static float Floor(float value) => (float)Math.Floor(value);
        private static float Fract(float value) => value - (float)Math.Floor(value);
        private static float Sin(float value) => (float)Math.Sin(value);
        private static float Cos(float value) => (float)Math.Cos(value);
        private static float Sqrt(float value) => (float)Math.Sqrt(value);
        private static float Pow(float value, float power) => (float)Math.Pow(value, power);
        private static float Clamp(float value) => float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
        private static MethodInfo Method(string name) => typeof(PixelKernel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static Runner Emit(List<Instruction> code)
        {
            var method = new DynamicMethod("PEV_PixelKernel", typeof(void),
                new[] { typeof(PixelPlane[]), typeof(float[]), typeof(float[]), typeof(int), typeof(int), typeof(Action) }, typeof(PixelKernel), true);
            var il = method.GetILGenerator();
            var registers = new LocalBuilder[16];
            for (int i = 0; i < registers.Length; i++) registers[i] = il.DeclareLocal(typeof(float));
            var index = il.DeclareLocal(typeof(int)); var x = il.DeclareLocal(typeof(int)); var y = il.DeclareLocal(typeof(int));
            var a = il.DeclareLocal(typeof(float)); var b = il.DeclareLocal(typeof(float));
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index);
            var loop = il.DefineLabel(); var next = il.DefineLabel(); var end = il.DefineLabel();
            il.MarkLabel(loop);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Bge, end);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4, 4095); il.Emit(OpCodes.And); il.Emit(OpCodes.Brtrue, next);
            il.Emit(OpCodes.Ldarg_S, (byte)5); il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
            il.MarkLabel(next);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Rem); il.Emit(OpCodes.Stloc, x);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Div); il.Emit(OpCodes.Stloc, y);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldloc, index);
            foreach (var q in code)
            {
                switch (q.Code)
                {
                    case Op.Constant: il.Emit(OpCodes.Ldc_R4, q.Value); break;
                    case Op.X: case Op.Y: il.Emit(OpCodes.Ldloc, q.Code == Op.X ? x : y); il.Emit(OpCodes.Conv_R4); break;
                    case Op.U: case Op.V:
                        il.Emit(OpCodes.Ldloc, q.Code == Op.U ? x : y); il.Emit(OpCodes.Conv_R4);
                        il.Emit(OpCodes.Ldc_R4, .5f); il.Emit(OpCodes.Add);
                        if (q.Code == Op.U) il.Emit(OpCodes.Ldarg_3); else il.Emit(OpCodes.Ldarg_S, (byte)4);
                        il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Div); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Uniform: il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, q.A); il.Emit(OpCodes.Ldelem_R4); break;
                    case Op.Get: il.Emit(OpCodes.Ldloc, registers[q.A]); break;
                    case Op.Set: il.Emit(OpCodes.Stloc, registers[q.A]); break;
                    case Op.Sample:
                        il.Emit(OpCodes.Stloc, b); il.Emit(OpCodes.Stloc, a);
                        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, q.A); il.Emit(OpCodes.Ldelem_Ref);
                        il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldloc, b); il.Emit(OpCodes.Ldc_I4, q.B);
                        il.Emit(OpCodes.Callvirt, typeof(PixelPlane).GetMethod("Sample", BindingFlags.Instance | BindingFlags.NonPublic)); break;
                    case Op.Add: il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Sub: il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Mul: il.Emit(OpCodes.Mul); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Div: il.Emit(OpCodes.Div); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Min: case Op.Max: case Op.Abs:
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod(q.Code.ToString(), q.Code == Op.Abs ? new[] { typeof(float) } : new[] { typeof(float), typeof(float) })); break;
                    case Op.Floor: case Op.Fract: case Op.Sin: case Op.Cos: case Op.Sqrt: case Op.Pow: case Op.Clamp:
                        il.Emit(OpCodes.Call, Method(q.Code.ToString())); break;
                    case Op.Ge: il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Le: il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Eq: il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Conv_R4); break;
                    case Op.Select:
                        il.Emit(OpCodes.Stloc, b); il.Emit(OpCodes.Stloc, a);
                        il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Ceq);
                        var falseValue = il.DefineLabel(); var selected = il.DefineLabel();
                        il.Emit(OpCodes.Brtrue, falseValue); il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Br, selected);
                        il.MarkLabel(falseValue); il.Emit(OpCodes.Ldloc, b); il.MarkLabel(selected); break;
                }
            }
            il.Emit(OpCodes.Stelem_R4);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
            il.Emit(OpCodes.Br, loop); il.MarkLabel(end); il.Emit(OpCodes.Ret);
            return (Runner)method.CreateDelegate(typeof(Runner));
        }
    }
}
