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
