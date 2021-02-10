// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    public class DiskRepairPolicy : RepairPolicy
    {
        public bool RecurseSubdirectories
        {
            get; set;
        }

        public string FolderPath
        {
            get; set;
        }

        public long MaxNumberOfFilesToDelete
        {
            get; set;
        }

        public FileSortOrder FileAgeSortOrder
        {
            get; set;
        }
    }

    public enum FileSortOrder
    {
        Ascending,
        Descending
    }
}
