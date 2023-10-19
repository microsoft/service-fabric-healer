﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.IO;

namespace FabricHealer.Utilities
{
    /// <summary>
    /// Serialization and deserialization utility for Json objects
    /// </summary>
    public static class JsonSerializationUtility
    {
        /// <summary>
        /// Determines if the supplied string is a serialized instance of the specified type T.
        /// </summary>
        /// <typeparam name="T">Type to be evaluated.</typeparam>
        /// <param name="text">Json string.</param>
        /// <param name="treatMissingMembersAsError">Optional boolean to treat missing type members as Error or not.</param>
        /// <returns>True if the string is a serialized instance of type T. False otherwise.</returns>
        public static bool IsJson<T>(string text, bool treatMissingMembersAsError = false)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                return TryDeserializeObject<T>(text, out _, treatMissingMembersAsError);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Tries to serialize an instance of the supplied type.
        /// </summary>
        /// <typeparam name="T">Input type.</typeparam>
        /// <param name="obj">Instance of type T.</param>
        /// <param name="data">out: the Json-serialized instance of the supplied type T.</param>
        /// <returns>A Json (string) representation of the supplied instance of type T.</returns>
        public static bool TrySerializeObject<T>(T obj, out string data)
        {
            if (obj == null)
            {
                data = null;
                return false;
            }

            try
            {
                data = JsonConvert.SerializeObject(obj);
                return true;
            }
            catch (JsonException)
            {

            }

            data = null;
            return false;
        }

        /// <summary>
        /// Tries to deserialize a Json string into an instance of specified type T. Default behavior ignores missing members.
        /// </summary>
        /// <typeparam name="T">Target type.</typeparam>
        /// <param name="obj">Json string representing an instance of type T.</param>
        /// <param name="data">out: an instance of type T.</param>
        /// <param name="treatMissingMembersAsError">Optional boolean to treat missing type members as Error or not.</param>
        /// <returns>An instance of the specified type T or default if the string can't be deserialized into the specified type T.</returns>
        public static bool TryDeserializeObject<T>(string obj, out T data, bool treatMissingMembersAsError = false)
        {
            if (string.IsNullOrWhiteSpace(obj))
            {
                data = default;
                return false;
            }
            
            try
            {
                var jsonSerializerSettings = new JsonSerializerSettings 
                { 
                    MissingMemberHandling = treatMissingMembersAsError ? MissingMemberHandling.Error : MissingMemberHandling.Ignore 
                };

                data = JsonConvert.DeserializeObject<T>(obj, jsonSerializerSettings);
                return true;
            }
            catch (JsonException)
            {

            }

            data = default;
            return false;
        }

        public static bool TrySerializeObjectToFile<T>(string fileName, T obj)
        {
            if (!TrySerializeObject(obj, out string file))
            {
                return false;
            }

            File.WriteAllText(fileName, file);

            return true;

        }

        public static bool TryDeserializeObjectFromFile<T>(string fileName, out T obj)
        {
            if (TryDeserializeObject(File.ReadAllText(fileName), out obj))
            {
                return true;
            }

            obj = default;
            return false;
        }
    }
}
