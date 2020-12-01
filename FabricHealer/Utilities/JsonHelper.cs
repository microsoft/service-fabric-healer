// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;

namespace FabricHealer.Utilities
{
    public static class JsonHelper
    {
        public static bool IsJson<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                _ = JsonConvert.DeserializeObject<T>(text);
                return true;
            }
            catch (JsonSerializationException)
            {
                return false;
            }
            catch (JsonReaderException)
            {
                return false;
            }
            catch (JsonWriterException)
            {
                return false;
            }
        }
    }
}
