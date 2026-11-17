// Fixed blittable layouts for the supported, four-byte-aligned GPU strides.
using System;
using System.Runtime.InteropServices;

namespace PEV.Scripting
{
    internal sealed partial class GpuRecordBuffer
    {
        [StructLayout(LayoutKind.Sequential, Size = 4)] private struct Record4 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 8)] private struct Record8 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 12)] private struct Record12 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 16)] private struct Record16 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 20)] private struct Record20 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 24)] private struct Record24 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 28)] private struct Record28 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 32)] private struct Record32 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 36)] private struct Record36 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 40)] private struct Record40 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 44)] private struct Record44 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 48)] private struct Record48 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 52)] private struct Record52 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 56)] private struct Record56 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 60)] private struct Record60 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 64)] private struct Record64 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 68)] private struct Record68 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 72)] private struct Record72 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 76)] private struct Record76 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 80)] private struct Record80 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 84)] private struct Record84 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 88)] private struct Record88 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 92)] private struct Record92 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 96)] private struct Record96 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 100)] private struct Record100 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 104)] private struct Record104 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 108)] private struct Record108 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 112)] private struct Record112 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 116)] private struct Record116 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 120)] private struct Record120 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 124)] private struct Record124 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 128)] private struct Record128 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 132)] private struct Record132 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 136)] private struct Record136 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 140)] private struct Record140 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 144)] private struct Record144 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 148)] private struct Record148 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 152)] private struct Record152 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 156)] private struct Record156 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 160)] private struct Record160 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 164)] private struct Record164 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 168)] private struct Record168 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 172)] private struct Record172 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 176)] private struct Record176 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 180)] private struct Record180 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 184)] private struct Record184 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 188)] private struct Record188 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 192)] private struct Record192 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 196)] private struct Record196 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 200)] private struct Record200 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 204)] private struct Record204 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 208)] private struct Record208 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 212)] private struct Record212 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 216)] private struct Record216 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 220)] private struct Record220 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 224)] private struct Record224 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 228)] private struct Record228 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 232)] private struct Record232 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 236)] private struct Record236 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 240)] private struct Record240 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 244)] private struct Record244 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 248)] private struct Record248 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 252)] private struct Record252 { private byte _first; }
        [StructLayout(LayoutKind.Sequential, Size = 256)] private struct Record256 { private byte _first; }
        private static readonly Type[] Types = {
            typeof(Record4), typeof(Record8), typeof(Record12), typeof(Record16), typeof(Record20), typeof(Record24), typeof(Record28), typeof(Record32),
            typeof(Record36), typeof(Record40), typeof(Record44), typeof(Record48), typeof(Record52), typeof(Record56), typeof(Record60), typeof(Record64),
            typeof(Record68), typeof(Record72), typeof(Record76), typeof(Record80), typeof(Record84), typeof(Record88), typeof(Record92), typeof(Record96),
            typeof(Record100), typeof(Record104), typeof(Record108), typeof(Record112), typeof(Record116), typeof(Record120), typeof(Record124), typeof(Record128),
            typeof(Record132), typeof(Record136), typeof(Record140), typeof(Record144), typeof(Record148), typeof(Record152), typeof(Record156), typeof(Record160),
            typeof(Record164), typeof(Record168), typeof(Record172), typeof(Record176), typeof(Record180), typeof(Record184), typeof(Record188), typeof(Record192),
            typeof(Record196), typeof(Record200), typeof(Record204), typeof(Record208), typeof(Record212), typeof(Record216), typeof(Record220), typeof(Record224),
            typeof(Record228), typeof(Record232), typeof(Record236), typeof(Record240), typeof(Record244), typeof(Record248), typeof(Record252), typeof(Record256),
        };
    }
}
