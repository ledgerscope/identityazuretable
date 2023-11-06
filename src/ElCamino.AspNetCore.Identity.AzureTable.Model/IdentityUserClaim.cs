﻿// MIT License Copyright 2020 (c) David Melendez. All rights reserved. See License.txt in the project root for license information.
using System;
using Azure;
using Azure.Data.Tables;

namespace ElCamino.AspNetCore.Identity.AzureTable.Model
{
    public class IdentityUserClaim : IdentityUserClaim<string>, IGenerateKeys
    {
        public IdentityUserClaim() { }

        /// <summary>
        /// Generates Row and Id keys.
        /// Partition key is equal to the UserId
        /// </summary>
        public void GenerateKeys(IKeyHelper keyHelper)
        {
            RowKey = PeekRowKey(keyHelper);
            KeyVersion = keyHelper.KeyVersion;
        }

        /// <summary>
        /// Generates the RowKey without setting it on the object.
        /// </summary>
        /// <returns></returns>
        public string PeekRowKey(IKeyHelper keyHelper)
        {
            return keyHelper.GenerateRowKeyIdentityUserClaim(ClaimType, ClaimValue);
        }

        public double KeyVersion { get; set; }


    }

    public class IdentityUserClaim<TKey> : Microsoft.AspNetCore.Identity.IdentityUserClaim<TKey>,
        ITableEntity
        where TKey : IEquatable<TKey>
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;

    }
}
