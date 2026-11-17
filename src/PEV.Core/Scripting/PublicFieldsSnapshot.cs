using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PEV.Scripting
{
    // A plain data snapshot: only public instance fields are serialized. Unity properties,
    // live objects and internal host references never cross into the sandbox.
    internal static class PublicFieldsSnapshot
    {
        private sealed class FieldsOnlyResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type type)
            {
                if (!IsDataType(type)) throw new ArgumentException("Snapshot contains a non-data object");
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(field => !field.IsNotSerialized).ToArray();
                if (fields.Any(field => !IsDataType(field.FieldType)))
                    throw new ArgumentException("Snapshot contains a non-data field");
                return fields.Cast<MemberInfo>().ToList();
            }
        }
        private static bool IsDataType(Type type)
        {
            if (type.IsArray) return type.GetArrayRank() == 1 && IsDataType(type.GetElementType());
            return type == typeof(string) || (type.IsValueType && !type.IsPointer && !type.IsByRef);
        }
        internal static void RequireImmutableRecord(Type type) => CheckImmutableRecord(type, 0);
        private static void CheckImmutableRecord(Type type, int depth)
        {
            if (depth > 8) throw new ArgumentException("Snapshot record nesting is too deep");
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return;
            if (!type.IsValueType || type.IsPointer || type.IsByRef) throw new ArgumentException("Snapshot record contains a mutable reference");
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                CheckImmutableRecord(field.FieldType, depth + 1);
        }
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new FieldsOnlyResolver(), MaxDepth = 32,
            ReferenceLoopHandling = ReferenceLoopHandling.Error
        };
        internal static string Serialize<T>(T value)
        {
            if (!IsDataType(typeof(T))) throw new ArgumentException("Snapshot root must be plain data");
            return JsonConvert.SerializeObject(value, Settings);
        }
    }
}
