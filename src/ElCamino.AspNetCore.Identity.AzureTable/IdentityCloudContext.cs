// MIT License Copyright 2020 (c) David Melendez. All rights reserved. See License.txt in the project root for license information.
using System;
using Azure.Data.Tables;
using ElCamino.AspNetCore.Identity.AzureTable.Model;

namespace ElCamino.AspNetCore.Identity.AzureTable
{
    public class IdentityCloudContext : IDisposable
    {
        protected TableServiceClient _client = null;
        protected bool _disposed = false;
        protected IdentityConfiguration _config = null;
        protected TableClient _roleTable;
        protected TableClient _indexTable;
        protected TableClient _userTable;

        public IdentityCloudContext(IdentityConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }
            Initialize(config);
        }

        protected virtual void Initialize(IdentityConfiguration config)
        {
            _config = config;

            if (string.IsNullOrEmpty(_config.StorageConnectionString) && _config.StorageConnectionUri == null)
            {
                throw new ArgumentNullException(nameof(config.StorageConnectionString), "Either StorageConnectionString or StorageConnectionUri are required");
            }
            else if (!string.IsNullOrEmpty(_config.StorageConnectionString))
            {
                _client = new TableServiceClient(_config.StorageConnectionString);

                if (_config.TokenCredential != null)
                {
                    //If we've been passed a TokenCredential we can use that instead of the credentials in the connection string
                    _client = new TableServiceClient(_client.Uri, _config.TokenCredential);
                }
            }
            else if (_config.StorageConnectionUri != null)
            {
                if (config.TokenCredential == null)
                {
                    throw new ArgumentNullException(nameof(config.TokenCredential), "TokenCredential is required when Uri is specified");
                }
                else
                {
                    _client = new TableServiceClient(_client.Uri, config.TokenCredential);
                }
            }

            _indexTable = _client.GetTableClient(FormatTableNameWithPrefix(!string.IsNullOrWhiteSpace(_config.IndexTableName) ? _config.IndexTableName : TableConstants.TableNames.IndexTable));
            _roleTable = _client.GetTableClient(FormatTableNameWithPrefix(!string.IsNullOrWhiteSpace(_config.RoleTableName) ? _config.RoleTableName : TableConstants.TableNames.RolesTable));
            _userTable = _client.GetTableClient(FormatTableNameWithPrefix(!string.IsNullOrWhiteSpace(_config.UserTableName) ? _config.UserTableName : TableConstants.TableNames.UsersTable));
        }

        ~IdentityCloudContext()
        {
            Dispose(false);
        }

        private string FormatTableNameWithPrefix(string baseTableName)
        {
            if (!string.IsNullOrWhiteSpace(_config.TablePrefix))
            {
                return string.Format("{0}{1}", _config.TablePrefix, baseTableName);
            }
            return baseTableName;
        }

        public TableClient RoleTable
        {
            get
            {
                ThrowIfDisposed();
                return _roleTable;
            }
        }

        public TableClient UserTable
        {
            get
            {
                ThrowIfDisposed();
                return _userTable;
            }
        }

        public TableClient IndexTable
        {
            get
            {
                ThrowIfDisposed();
                return _indexTable;
            }
        }

        public TableServiceClient Client
        {
            get
            {
                ThrowIfDisposed();
                return _client;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(base.GetType().Name);
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _client = null;
                _indexTable = null;
                _roleTable = null;
                _userTable = null;
                _disposed = true;
            }
        }
    }
}
