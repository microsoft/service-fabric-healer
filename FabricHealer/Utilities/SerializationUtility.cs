// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.IO;

namespace FabricHealer.Utilities
{
    /// <summary>
    /// Serialization and deserialization utility for Json objects
    /// </summary>
    public static class SerializationUtility
    {
#pragma warning disable CA1720 // Identifier contains type name
        public static bool TrySerialize<T>(T objTarget, out string obj)
#pragma warning restore CA1720 // Identifier contains type name
        {
            try
            {
                obj = JsonConvert.SerializeObject(objTarget);
                return true;
            }
            catch (Exception)
            {
                obj = default;
                return false;
            }
        }

        public static bool TryDeserialize<T>(string serializedObj, out T obj)
        {
            try
            {
                obj = JsonConvert.DeserializeObject<T>(serializedObj);
                return true;
            }
            catch (Exception)
            {
                obj = default;
                return false;
            }
        }

#pragma warning disable CA1720 // Identifier contains type name
        public static bool TrySerializeObjectToFile<T>(string fileName, T obj)
#pragma warning restore CA1720 // Identifier contains type name
        {
            if (TrySerialize(obj, out string file))
            {
                File.WriteAllText(fileName, file);
                return true;
            }

            return false;
        }

        public static bool TryDeserializeObjectFromFile<T>(string fileName, out T obj)
        {
            if (TryDeserialize(File.ReadAllText(fileName), out obj))
            {
                return true;
            }

            obj = default;
            return false;
        }
    }
}
