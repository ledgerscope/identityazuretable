﻿// MIT License Copyright 2020 (c) David Melendez. All rights reserved. See License.txt in the project root for license information.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Azure.Data.Tables;
using ElCamino.AspNetCore.Identity.AzureTable.Helpers;
using ElCamino.AspNetCore.Identity.AzureTable.Model;
using Azure;
using System.Net;
using System.Reflection;

namespace ElCamino.AspNetCore.Identity.AzureTable
{
    public class UserOnlyStore<TUser> 
        : UserOnlyStore<TUser, IdentityCloudContext> where TUser : Model.IdentityUser<string>, new()
    {
        public UserOnlyStore(IdentityCloudContext context, IKeyHelper keyHelper, IdentityConfiguration config) : base(context, keyHelper, config) { }

    }

    public class UserOnlyStore<TUser, TContext> 
        : UserOnlyStore<TUser, TContext, string, Model.IdentityUserClaim, Model.IdentityUserLogin, Model.IdentityUserToken>
        where TUser : Model.IdentityUser<string>, new()
        where TContext : IdentityCloudContext
    {
        public UserOnlyStore(TContext context, IKeyHelper keyHelper, IdentityConfiguration config) : base(context, keyHelper, config) { }
    }

    public class UserOnlyStore<TUser, TContext, TKey, TUserClaim, TUserLogin, TUserToken> :
        UserStoreBase<TUser, TKey, TUserClaim, TUserLogin, TUserToken>
        , IDisposable
        where TUser : Model.IdentityUser<TKey>, new()
        where TKey : IEquatable<TKey>
        where TUserLogin : Model.IdentityUserLogin<TKey>, new()
        where TUserClaim : Model.IdentityUserClaim<TKey>, new()
        where TUserToken : Model.IdentityUserToken<TKey>, new()
        where TContext : IdentityCloudContext
    {
        protected bool _disposed;

        protected TableClient _userTable;
        protected TableClient _indexTable;
        protected IKeyHelper _keyHelper;

        private IdentityConfiguration _config = null;

        private readonly string FilterString;

        public UserOnlyStore(TContext context, IKeyHelper keyHelper, IdentityConfiguration config) : base(new IdentityErrorDescriber())
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _userTable = context.UserTable;
            _indexTable = context.IndexTable;
            _keyHelper = keyHelper;
            _config = config;

            string partitionFilter = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.GreaterThanOrEqual, _keyHelper.PreFixIdentityUserId),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.LessThan, _keyHelper.PreFixIdentityUserIdUpperBound));
            string rowFilter = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.GreaterThanOrEqual, _keyHelper.PreFixIdentityUserId),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.LessThan, _keyHelper.PreFixIdentityUserIdUpperBound));
            FilterString = TableQuery.CombineFilters(partitionFilter, TableOperators.And, rowFilter);
        }

        /// <summary>
        /// Queries will be slow unless they include Partition and/or Row keys
        /// </summary>
        public override IQueryable<TUser> Users
        {
            get
            {
                return _userTable.Query<TUser>(FilterString).AsQueryable();
            }
        }

        public virtual Task CreateTablesIfNotExistsAsync()
        {
            Task[] tasks = new Task[]
                    {
                        _userTable.CreateIfNotExistsAsync(),
                        _indexTable.CreateIfNotExistsAsync(),
                    };
            return Task.WhenAll(tasks);
        }

        public override async Task AddClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (claims == null) throw new ArgumentNullException(nameof(claims));

            BatchOperationHelper bHelper = new BatchOperationHelper(_userTable);

            List<Task> tasks = new List<Task>();
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            foreach (Claim c in claims)
            {
                bHelper.AddEntity(CreateUserClaim(user, c));
                tasks.Add(_indexTable.UpsertEntityAsync(CreateClaimIndex(userPartitionKey, c.Type, c.Value), TableUpdateMode.Replace));
            }

            tasks.Add(bHelper.SubmitBatchAsync(cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public virtual async Task AddClaimAsync(TUser user, Claim claim)
        {
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (claim == null) throw new ArgumentNullException(nameof(claim));

            List<Task> tasks = new List<Task>(2);

            tasks.Add(_userTable.AddEntityAsync(CreateUserClaim(user, claim)));
            tasks.Add(_indexTable.UpsertEntityAsync(CreateClaimIndex(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)), claim.Type, claim.Value)));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public override Task AddLoginAsync(TUser user, UserLoginInfo login, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (login == null) throw new ArgumentNullException(nameof(login));

            TUserLogin item = CreateUserLogin(user, login);

            Model.IdentityUserIndex index = CreateLoginIndex(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)), item.LoginProvider, item.ProviderKey);

            return Task.WhenAll(_userTable.AddEntityAsync(item, cancellationToken:cancellationToken)
                , _indexTable.UpsertEntityAsync(index, cancellationToken: cancellationToken));
        }

        public async override Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            ((Model.IGenerateKeys)user).GenerateKeys(_keyHelper);

            try
            {
                string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
                List<Task> tasks = new List<Task>(2);
                tasks.Add(_userTable.AddEntityAsync(user));
                tasks.Add(_indexTable.AddEntityAsync(CreateUserNameIndex(userPartitionKey, user.UserName)));

                if (!string.IsNullOrWhiteSpace(user.Email))
                {
                    Model.IdentityUserIndex index = CreateEmailIndex(userPartitionKey, user.Email);
                    tasks.Add(_indexTable.UpsertEntityAsync(index));
                }

                await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
                return IdentityResult.Success;
            }
            catch (AggregateException aggex)
            {
                aggex.Flatten();
                return IdentityResult.Failed(new IdentityError() { Code = "001", Description = "User Creation Failed." });
            }
        }

        public async override Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            List<Task> tasks = new List<Task>(50);
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            var userRows = await GetUserAggregateQueryAsync(userPartitionKey).ToListAsync().ConfigureAwait(false);

            tasks.Add(DeleteAllUserRows(userPartitionKey, userRows));

            var deleteUserIndex = CreateUserNameIndex(userPartitionKey, user.UserName);
            tasks.Add(_indexTable.DeleteEntityAsync(deleteUserIndex.PartitionKey, deleteUserIndex.RowKey,  TableConstants.ETagWildcard, cancellationToken));

            var userAgg = MapUserAggregate(userPartitionKey, userRows);

            //Don't use the BatchHelper for login index table, partition keys are likely not the same
            //since they are based on logonprovider and providerkey
            foreach (var userLogin in userAgg.Logins)
            {
                var deleteLoginIndex = CreateLoginIndex(userPartitionKey, userLogin.LoginProvider, userLogin.ProviderKey);
                tasks.Add(_indexTable.DeleteEntityAsync(deleteLoginIndex.PartitionKey, deleteLoginIndex.RowKey,  TableConstants.ETagWildcard, cancellationToken));
            }

            foreach (var userClaim in userAgg.Claims)
            {
                var deleteClaimIndex = CreateClaimIndex(userPartitionKey, userClaim.ClaimType, userClaim.ClaimValue);
                tasks.Add(_indexTable.DeleteEntityAsync(deleteClaimIndex.PartitionKey, deleteClaimIndex.RowKey,  TableConstants.ETagWildcard, cancellationToken));
            }

            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                var deleteEmailIndex = CreateEmailIndex(userPartitionKey, user.Email);
                tasks.Add(_indexTable.DeleteEntityAsync(deleteEmailIndex.PartitionKey, deleteEmailIndex.RowKey,  TableConstants.ETagWildcard, cancellationToken));
            }

            try
            {
                await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
                return IdentityResult.Success;
            }
            catch (AggregateException aggex)
            {
                aggex.Flatten();
                return IdentityResult.Failed(new IdentityError() { Code = "003", Description = "Delete user failed." });
            }
        }


        public new void Dispose()
        {
            base.Dispose();
            this.Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                this._indexTable = null;
                this._userTable = null;
                this.Context = null;
                this._disposed = true;
            }
        }

        protected override Task<TUserLogin> FindUserLoginAsync(TKey userId, string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return FindUserLoginAsync(ConvertIdToString(userId), loginProvider, providerKey);
        }

        protected async Task<TUserLogin> FindUserLoginAsync(string userId, string loginProvider, string providerKey)
        {
            string rowKey = _keyHelper.GenerateRowKeyIdentityUserLogin(loginProvider, providerKey);

            TableQuery tq = new TableQuery();
            tq.TakeCount = 1;
            tq.FilterString = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, userId),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, rowKey));
            return await _userTable.ExecuteQueryAsync<TUserLogin>(tq).FirstOrDefaultAsync().ConfigureAwait(false);

            //if (log != null)
            //{
            //    TUserLogin tlogin = MapTableEntity<TUserLogin>(log);
            //    return tlogin;
            //}

            //return null;
        }

        protected override async Task<TUserLogin> FindUserLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            string rowKey = _keyHelper.GenerateRowKeyIdentityUserLogin(loginProvider, providerKey);
            string partitionKey = _keyHelper.GeneratePartitionKeyIndexByLogin(loginProvider, providerKey);
            var loginQuery = GetUserIdByIndex(partitionKey, rowKey);

            IdentityUserIndex indexInfo = await _indexTable.ExecuteQueryAsync<IdentityUserIndex>(loginQuery).FirstOrDefaultAsync().ConfigureAwait(false);

            if (indexInfo != null)
            {
                string userId = indexInfo.Id;
                return await FindUserLoginAsync(userId, loginProvider, providerKey).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// Overridden for better performance.
        /// </summary>
        /// <param name="loginProvider"></param>
        /// <param name="providerKey"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task<TUser> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            string rowKey = _keyHelper.GenerateRowKeyIdentityUserLogin(loginProvider, providerKey);
            string partitionKey = _keyHelper.GeneratePartitionKeyIndexByLogin(loginProvider, providerKey);
            TableQuery loginQuery = GetUserIdByIndex(partitionKey, rowKey);

            return GetUserAggregateAsync(loginQuery);
        }

        public override Task<TUser> FindByEmailAsync(string plainEmail, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            return this.GetUserAggregateAsync(FindByEmailQuery(plainEmail));
        }



        public async Task<IEnumerable<TUser>> FindAllByEmailAsync(string plainEmail)
        {
            this.ThrowIfDisposed();
            IEnumerable<TUser> users = await GetUsersAggregateByIndexQueryAsync(FindByEmailQuery(plainEmail), GetUserQueryAsync).ConfigureAwait(false);
            return users.Where(user => _keyHelper.GenerateRowKeyUserEmail(plainEmail) == _keyHelper.GenerateRowKeyUserEmail(user.Email));
        }

        protected override Task<TUser> FindUserAsync(TKey userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return GetUserAsync(ConvertIdToString(userId));
        }

        protected TableQuery GetUserByRoleQuery(string plainRoleName)
             => GetUserIdsByIndex(_keyHelper.GenerateRowKeyIdentityUserRole(plainRoleName));

        protected TableQuery GetUserByClaimQuery(Claim claim)
         => GetUserIdsByIndex(_keyHelper.GenerateRowKeyIdentityUserClaim(claim.Type, claim.Value));

        protected TableQuery FindByEmailQuery(string plainEmail)
         => GetUserIdsByIndex(_keyHelper.GenerateRowKeyUserEmail(plainEmail));

        protected TableQuery FindByUserNameQuery(string userName)
            => GetUserIdsByIndex(_keyHelper.GenerateRowKeyUserName(userName));

        protected TableQuery GetUserIdByIndex(string partitionkey, string rowkey)
        {
            TableQuery tq = new TableQuery();
            tq.TakeCount = 1;
            tq.SelectColumns = new List<string>() { nameof(IdentityUserIndex.Id) };
            tq.FilterString = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, partitionkey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, rowkey));
            return tq;
        }

        protected TableQuery GetUserIdsByIndex(string partitionKey)
        {
            TableQuery tq = new TableQuery();
            tq.SelectColumns = new List<string>() { nameof(IdentityUserIndex.Id) };
            tq.FilterString = TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, partitionKey);
            return tq;
        }

        public override Task<TUser> FindByIdAsync(string userId, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return GetUserAsync(_keyHelper.GenerateRowKeyUserId(userId));
        }

        public override async Task<TUser> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            TableQuery userId = FindByUserNameQuery(normalizedUserName);
            TUser user = await GetUserAggregateAsync(userId).ConfigureAwait(false);
            //Make sure the index lookup matches the user record.
            if(user != default(TUser) 
                && user.NormalizedUserName != null
                && user.NormalizedUserName.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase))
            {
                return user;
            }
            return default(TUser);
        }

        protected async Task<TUserClaim> GetUserClaimAsync(TUser user, Claim claim)
        {
            try
            {
                return await _userTable.GetEntityAsync<TUserClaim>(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)),
                    _keyHelper.GenerateRowKeyIdentityUserClaim(claim.Type, claim.Value)).ConfigureAwait(false);
            }
            catch (RequestFailedException rfe)
            when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                return null;
            }
        }


        public override async Task<IList<Claim>> GetClaimsAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            List<Claim> rClaims = new List<Claim>();

            TableQuery tq = new TableQuery();
            string partitionFilter =
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, user.PartitionKey);
            string rowFilter = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.GreaterThanOrEqual, _keyHelper.PreFixIdentityUserClaim),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.LessThan, _keyHelper.PreFixIdentityUserClaimUpperBound));
            tq.FilterString = TableQuery.CombineFilters(partitionFilter, TableOperators.And, rowFilter);
            await foreach (var tclaim in _userTable.ExecuteQueryAsync<TUserClaim>(tq).ConfigureAwait(false))
            {
                //1.7 Claim rowkey migration 
                if (_keyHelper.GenerateRowKeyIdentityUserClaim(tclaim.ClaimType, tclaim.ClaimValue) == tclaim.RowKey)
                {
                    rClaims.Add(new Claim(tclaim.ClaimType, tclaim.ClaimValue));
                }
            }

            return rClaims;
        }

        protected T MapTableEntity<T>(TableEntity dte) where T : ITableEntity, new()
        {
            T t = new T()
            {
                ETag = dte.ETag,
                PartitionKey = dte.PartitionKey,
                RowKey = dte.RowKey,
                Timestamp = dte.Timestamp
            };
            Type type = t.GetType();

            foreach (string tableProperty in dte.Keys)
            {
                var prop = type.GetProperty(tableProperty, BindingFlags.Public | BindingFlags.Instance| BindingFlags.SetProperty);
                if (prop != null)
                {
                    object tempValue = null;
                    if (dte.TryGetValue(tableProperty, out tempValue))
                    {
                        prop.SetValue(t, tempValue);
                    }
                }
            }
            return t;
        }

        public override async Task<IList<UserLoginInfo>> GetLoginsAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            List<UserLoginInfo> rLogins = new List<UserLoginInfo>();

            TableQuery tq = new TableQuery();
            string partitionFilter =
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, user.PartitionKey);
            string rowFilter = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.GreaterThanOrEqual, _keyHelper.PreFixIdentityUserLogin),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.LessThan, _keyHelper.PreFixIdentityUserLoginUpperBound));
            tq.FilterString = TableQuery.CombineFilters(partitionFilter, TableOperators.And, rowFilter);
            await foreach (var tul in _userTable.ExecuteQueryAsync<TUserLogin>(tq).ConfigureAwait(false))
            {
                rLogins.Add(new UserLoginInfo(tul.LoginProvider, tul.ProviderKey, tul.ProviderDisplayName));
            }

            return rLogins;
        }

        protected async virtual Task<TUser> GetUserAsync(string userId)
        {
            try
            {
                return await _userTable.GetEntityAsync<TUser>(partitionKey: userId, rowKey: userId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch(RequestFailedException rfe)
            when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                return null;
            }            
        }

        /// <summary>
        /// Retrieves User rows by partitionkey UserId
        /// </summary>
        /// <param name="userIdPartitionKey">Must be formatted as UserId PartitionKey</param>
        /// <returns></returns>
        protected IAsyncEnumerable<TableEntity> GetUserAggregateQueryAsync(string userIdPartitionKey)
        {
            TableQuery tq = new TableQuery();
            tq.FilterString = TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, userIdPartitionKey);

            return _userTable.ExecuteQueryAsync<TableEntity>(tq);
        }

        protected async Task<IEnumerable<TUser>> GetUserAggregateQueryAsync(IEnumerable<string> userIds,
                Func<string, string> setFilterByUserId = null,
                Func<TUserClaim, bool> whereClaim = null)
        {
            const double pageSize = 50.0;
            int pages = (int)Math.Ceiling(((double)userIds.Count() / pageSize));
            List<TableQuery> listTqs = new List<TableQuery>(pages);
            IEnumerable<string> tempUserIds = null;

            for (int currentPage = 1; currentPage <= pages; currentPage++)
            {
                if (currentPage > 1)
                {
                    tempUserIds = userIds.Skip(((currentPage - 1) * (int)pageSize)).Take((int)pageSize);
                }
                else
                {
                    tempUserIds = userIds.Take((int)pageSize);
                }

                TableQuery tq = new TableQuery();
                int i = 0;
                foreach (var tempUserId in tempUserIds)
                {

                    string temp = TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, tempUserId);
                    if (setFilterByUserId != null)
                    {
                        temp = setFilterByUserId(tempUserId);
                    }

                    if (i > 0)
                    {
                        tq.FilterString = TableQuery.CombineFilters(tq.FilterString, TableOperators.Or, temp);
                    }
                    else
                    {
                        tq.FilterString = temp;
                    }
                    i++;
                }
                listTqs.Add(tq);

            }

            ConcurrentBag<TUser> bag = new ConcurrentBag<TUser>();
#if DEBUG
            DateTime startUserAggTotal = DateTime.UtcNow;
#endif
            var tasks = listTqs.Select((q) =>
            {
                return _userTable.ExecuteQueryAsync<TableEntity>(q).ToListAsync()
                     .ContinueWith((taskResults) =>
                     {
                         //ContinueWith returns completed task. Calling .Result is safe here.

                         foreach (var s in taskResults.Result.GroupBy(g => g.PartitionKey))
                         {
                             var userAgg = MapUserAggregate(s.Key, s);
                             bool addUser = true;
                             if (whereClaim != null)
                             {
                                 if (!userAgg.Claims.Any(whereClaim))
                                 {
                                     addUser = false;
                                 }
                             }
                             if (addUser)
                             {
                                 bag.Add(userAgg.User);
                             }
                         }
                     });

            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
#if DEBUG
            Debug.WriteLine("GetUserAggregateQuery (GetUserAggregateTotal): {0} seconds", (DateTime.UtcNow - startUserAggTotal).TotalSeconds);
            Debug.WriteLine("GetUserAggregateQuery (Return Count): {0} userIds", bag.Count());
#endif
            return bag;
        }

        protected async virtual Task<IEnumerable<TUser>> GetUserQueryAsync(IEnumerable<string> userIds)
        {
            const double pageSize = 50.0;
            int pages = (int)Math.Ceiling(((double)userIds.Count() / pageSize));
            List<TableQuery> listTqs = new List<TableQuery>(pages);
            IEnumerable<string> tempUserIds = Enumerable.Empty<string>();

            for (int currentPage = 1; currentPage <= pages; currentPage++)
            {
                if (currentPage > 1)
                {
                    tempUserIds = userIds.Skip(((currentPage - 1) * (int)pageSize)).Take((int)pageSize);
                }
                else
                {
                    tempUserIds = userIds.Take((int)pageSize);
                }

                TableQuery tq = new TableQuery();
                int tempUserCounter = 0;
                foreach (string tempUserId in tempUserIds)
                {

                    string temp = TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, tempUserId), TableOperators.And,
                        TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, tempUserId));
                    if (tempUserCounter > 0)
                    {
                        tq.FilterString = TableQuery.CombineFilters(tq.FilterString, TableOperators.Or, temp);
                    }
                    else
                    {
                        tq.FilterString = temp;
                    }
                    tempUserCounter++;
                }
                listTqs.Add(tq);

            }

            ConcurrentBag<TUser> bag = new ConcurrentBag<TUser>();
#if DEBUG
            DateTime startUserAggTotal = DateTime.UtcNow;
#endif
            IEnumerable<Task> tasks = listTqs.Select((q) =>
            {
                return _userTable.ExecuteQueryAsync<TUser>(q).ToListAsync()
                .ContinueWith((taskResults) =>
                {
                    foreach (var s in taskResults.Result)
                    {
                        bag.Add(s);
                    }
                });

            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
#if DEBUG
            Debug.WriteLine("GetUserAggregateQuery (GetUserAggregateTotal): {0} seconds", (DateTime.UtcNow - startUserAggTotal).TotalSeconds);
#endif
            return bag;
        }

        protected (TUser User,
            IEnumerable<TUserClaim> Claims,
            IEnumerable<TUserLogin> Logins,
            IEnumerable<TUserToken> Tokens)
            MapUserAggregate(string userId, IEnumerable<TableEntity> userResults)
        {

            TUser user = default(TUser);
            IEnumerable<TUserClaim> claims = Enumerable.Empty<TUserClaim>();
            IEnumerable<TUserLogin> logins = Enumerable.Empty<TUserLogin>();
            IEnumerable<TUserToken> tokens = Enumerable.Empty<TUserToken>();

            var vUser = userResults.Where(u => u.RowKey.Equals(userId) && u.PartitionKey.Equals(userId)).SingleOrDefault();

            if (vUser != null)
            {
                //User
                user = MapTableEntity<TUser>(vUser);

                //Claims
                claims = userResults.Where(u => u.RowKey.StartsWith(_keyHelper.PreFixIdentityUserClaim)
                     && u.PartitionKey.Equals(userId))
                    .Select((log) =>
                    {
                        return MapTableEntity<TUserClaim>(log);
                    });
                //Logins
                logins = userResults.Where(u => u.RowKey.StartsWith(_keyHelper.PreFixIdentityUserLogin)
                    && u.PartitionKey.Equals(userId))
                    .Select((log) =>
                    {
                        return MapTableEntity<TUserLogin>(log);
                    });

                //Tokens
                tokens = userResults.Where(u => u.RowKey.StartsWith(_keyHelper.PreFixIdentityUserToken)
                     && u.PartitionKey.Equals(userId))
                    .Select((log) =>
                    {
                        return MapTableEntity<TUserToken>(log);
                    });
            }
            return (user, claims, logins, tokens);
        }

        protected virtual async Task<TUser> GetUserAggregateAsync(TableQuery queryUser)
        {
            var user = await _indexTable.ExecuteQueryAsync<IdentityUserIndex>(queryUser).FirstOrDefaultAsync().ConfigureAwait(false);
            if (user != null)
            {
                string userId = user.Id;
                return await GetUserAsync(userId).ConfigureAwait(false);
            }

            return default(TUser);
        }

        protected async Task<IEnumerable<TUser>> GetUsersAggregateByIndexQueryAsync(TableQuery queryUser, Func<IEnumerable<string>, Task<IEnumerable<TUser>>> getUserFunc)
        {
#if DEBUG
            DateTime startIndex = DateTime.UtcNow;
#endif
            ConcurrentBag<IEnumerable<TUser>> lUsers = new ConcurrentBag<IEnumerable<TUser>>();
            string token = string.Empty;
            const int takeCount = 30;
            const int taskMax = 10;
            queryUser.TakeCount = takeCount;
            List<Task> taskBatch = new List<Task>(taskMax);
            Func<IEnumerable<string>, Task> getUsers = async (ids) =>
            {
                lUsers.Add((await getUserFunc(ids).ConfigureAwait(false)));
            };
            while (token != null)
            {
                IAsyncEnumerable<Page<IdentityUserIndex>> pages = _indexTable.QueryAsync<IdentityUserIndex>(queryUser.FilterString, takeCount, new string[] { nameof(IdentityUserIndex.Id) }).AsPages(continuationToken:token);
                Page<IdentityUserIndex> page = await pages.FirstOrDefaultAsync().ConfigureAwait(false);
                var tempUserIds = page.Values.Select(u => u.Id).Distinct();
                taskBatch.Add(getUsers(tempUserIds));
                if (taskBatch.Count % taskMax == 0)
                {
                    await Task.WhenAll(taskBatch).ConfigureAwait(false);
                    taskBatch.Clear();
                }
                token = page.ContinuationToken;
            }

            if (taskBatch.Count > 0)
            {
                await Task.WhenAll(taskBatch).ConfigureAwait(false);
                taskBatch.Clear();
            }
#if DEBUG
            Debug.WriteLine("GetUsersAggregateByIndexQueryAsync (Index query): {0} seconds", (DateTime.UtcNow - startIndex).TotalSeconds);
#endif

            return lUsers.SelectMany(u => u);
        }

        public virtual async Task RemoveClaimAsync(TUser user, Claim claim)
        {
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (claim == null) throw new ArgumentNullException(nameof(claim));

            // Claim ctor doesn't allow Claim.Value to be null. Need to allow string.empty.


            TUserClaim local = await GetUserClaimAsync(user, claim).ConfigureAwait(false);
            if (local != null)
            {
                TUserClaim deleteUserClaim = CreateUserClaim(user, claim);
                IdentityUserIndex deleteUserClaimIndex = CreateClaimIndex(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)), claim.Type, claim.Value);

                var tasks = new Task[]
                {
                    _indexTable.DeleteEntityAsync(deleteUserClaimIndex.PartitionKey, deleteUserClaimIndex.RowKey,  TableConstants.ETagWildcard),
                    _userTable.DeleteEntityAsync(deleteUserClaim.PartitionKey, deleteUserClaim.RowKey,  TableConstants.ETagWildcard)
                };

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

        }

        public override async Task ReplaceClaimAsync(TUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            if (newClaim == null) throw new ArgumentNullException(nameof(newClaim));

            // Claim ctor doesn't allow Claim.Value to be null. Need to allow string.empty.
            BatchOperationHelper bHelper = new BatchOperationHelper(_userTable);

            TUserClaim local = await GetUserClaimAsync(user, claim).ConfigureAwait(false);
            List<Task> tasks = new List<Task>(3);
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            if (local != null)
            {
                TUserClaim deleteClaim = CreateUserClaim(user, claim);
                bHelper.DeleteEntity(deleteClaim.PartitionKey, deleteClaim.RowKey,  TableConstants.ETagWildcard);
                var deleteClaimIndex = CreateClaimIndex(userPartitionKey, claim.Type, claim.Value);
                tasks.Add(_indexTable.DeleteEntityAsync(deleteClaimIndex.PartitionKey, deleteClaimIndex.RowKey, ifMatch: TableConstants.ETagWildcard, cancellationToken: cancellationToken));
            }
            TUserClaim item = CreateUserClaim(user, newClaim);

            bHelper.AddEntity(item);
            tasks.Add(_indexTable.UpsertEntityAsync(CreateClaimIndex(userPartitionKey, newClaim.Type, newClaim.Value), cancellationToken: cancellationToken));

            tasks.Add(bHelper.SubmitBatchAsync(cancellationToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public override async Task RemoveClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (claims == null) throw new ArgumentNullException(nameof(claims));

            // Claim ctor doesn't allow Claim.Value to be null. Need to allow string.empty.
            List<Task> tasks = new List<Task>();
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            BatchOperationHelper bHelper = new BatchOperationHelper(_userTable);
            var userClaims = await this.GetClaimsAsync(user).ConfigureAwait(false);
            foreach (Claim claim in claims)
            {
                Claim local = (from uc in userClaims
                               where uc.Type == claim.Type && uc.Value == claim.Value
                               select uc).FirstOrDefault();
                if (local != null)
                {
                    var deleteUserClaim = CreateUserClaim(user, local);
                    bHelper.DeleteEntity(deleteUserClaim.PartitionKey, deleteUserClaim.RowKey,  TableConstants.ETagWildcard);
                    var deleteClaimIndex = CreateClaimIndex(userPartitionKey, local.Type, local.Value);
                    tasks.Add(_indexTable.DeleteEntityAsync(deleteClaimIndex.PartitionKey, deleteClaimIndex.RowKey, ifMatch: TableConstants.ETagWildcard, cancellationToken: cancellationToken));
                }
            }
            tasks.Add(bHelper.SubmitBatchAsync(cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        protected override TUserClaim CreateUserClaim(TUser user, Claim claim)
        {
            TUserClaim uc = base.CreateUserClaim(user, claim);
            ((IGenerateKeys)uc).GenerateKeys(_keyHelper);
            uc.PartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            uc.UserId = user.Id;
            uc.ETag =  TableConstants.ETagWildcard;
            return uc;
        }

        protected override TUserLogin CreateUserLogin(TUser user, UserLoginInfo login)
        {
            TUserLogin ul = base.CreateUserLogin(user, login);
            ((IGenerateKeys)ul).GenerateKeys(_keyHelper);
            ul.PartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            ul.UserId = user.Id;
            ul.ETag =  TableConstants.ETagWildcard;
            return ul;
        }

        public override async Task RemoveLoginAsync(TUser user, string loginProvider, string providerKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            TUserLogin item = await FindUserLoginAsync(userPartitionKey, loginProvider, providerKey).ConfigureAwait(false);

            if (item != null)
            {
                Model.IdentityUserIndex index = CreateLoginIndex(userPartitionKey, item.LoginProvider, item.ProviderKey);
                await Task.WhenAll(_indexTable.DeleteEntityAsync(index.PartitionKey, index.RowKey,  TableConstants.ETagWildcard, cancellationToken),
                                    _userTable.DeleteEntityAsync(item.PartitionKey, item.RowKey,  TableConstants.ETagWildcard, cancellationToken)).ConfigureAwait(false);
            }
        }

        public override async Task SetUserNameAsync(TUser user, string userName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            //Only remove the username if different
            //The UserManager calls UpdateAsync which will generate the new username index record
            if (!string.IsNullOrWhiteSpace(user.UserName) && user.UserName != userName)
            {
                await DeleteUserNameIndexAsync(ConvertIdToString(user.Id), user.UserName).ConfigureAwait(false);
            }
            user.UserName = userName;
        }

        public override async Task SetEmailAsync(TUser user, string email, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            //Only remove the email if different
            //The UserManager calls UpdateAsync which will generate the new email index record
            if (!string.IsNullOrWhiteSpace(user.Email) && user.Email != email)
            {
                await DeleteEmailIndexAsync(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)), user.Email).ConfigureAwait(false);
            }
            user.Email = email;
        }

        //Fixes deletes for non-unique emails for users.
        protected async Task DeleteEmailIndexAsync(string userId, string plainEmail)
        {
            TableQuery tq = new TableQuery();
            tq.FilterString = TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, _keyHelper.GenerateRowKeyUserEmail(plainEmail)),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, userId));

            await foreach (IdentityUserIndex de in _indexTable.ExecuteQueryAsync<IdentityUserIndex>(tq).ConfigureAwait(false))
            {
                if (de.Id.Equals(userId, StringComparison.OrdinalIgnoreCase))
                {
                    await _indexTable.DeleteEntityAsync(de.PartitionKey, de.RowKey,  TableConstants.ETagWildcard).ConfigureAwait(false);
                }
            }
        }

        protected async Task DeleteUserNameIndexAsync(string userId, string userName)
        {
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(userId);
            try
            {
                var result = await _indexTable.GetEntityAsync<IdentityUserIndex>(_keyHelper.GenerateRowKeyUserName(userName), userPartitionKey).ConfigureAwait(false);
                if (result.Value != null)
                {
                    _ = await _indexTable.DeleteEntityAsync(result.Value.PartitionKey, result.Value.RowKey,  TableConstants.ETagWildcard).ConfigureAwait(false);
                }
            }
            catch(RequestFailedException rfe)
            when(rfe.Status == (int)HttpStatusCode.NotFound)
            {
                //noop - if it isn't found, we can't delete
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="userId">UserId in PartitionKey format</param>
        /// <param name="userRows"></param>
        /// <returns></returns>
        protected Task DeleteAllUserRows(string userId, IEnumerable<TableEntity> userRows)
        {
            var deleteBatchHelper = new BatchOperationHelper(_userTable);
            foreach (TableEntity delUserRow in userRows)
            {
                if (userId == delUserRow.PartitionKey)
                {
                    deleteBatchHelper.DeleteEntity(delUserRow.PartitionKey, delUserRow.RowKey,  TableConstants.ETagWildcard);
                }
            }

            return deleteBatchHelper.SubmitBatchAsync();
        }


        public override async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));
            string userPartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            List<Task> tasks = new List<Task>(3);
            tasks.Add(_userTable.UpdateEntityAsync(user,  TableConstants.ETagWildcard, TableUpdateMode.Replace, cancellationToken));

            tasks.Add(_indexTable.UpsertEntityAsync(CreateUserNameIndex(userPartitionKey, user.UserName), cancellationToken:cancellationToken));
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                Model.IdentityUserIndex indexEmail = CreateEmailIndex(userPartitionKey, user.Email);

                tasks.Add(_indexTable.UpsertEntityAsync(indexEmail));
            }

            try
            {
                await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
                return IdentityResult.Success;
            }
            catch (AggregateException aggex)
            {
                aggex.Flatten();
                return IdentityResult.Failed(new IdentityError() { Code = "002", Description = "User Update Failed." });
            }

        }

        public TContext Context { get; private set; }

        protected Model.IdentityUserIndex CreateClaimIndex(string userPartitionKey, string claimType, string claimValue)
        {
            return new Model.IdentityUserIndex()
            {
                Id = userPartitionKey,
                PartitionKey = _keyHelper.GenerateRowKeyIdentityUserClaim(claimType, claimValue),
                RowKey = userPartitionKey,
                KeyVersion = _keyHelper.KeyVersion,
                ETag =  TableConstants.ETagWildcard
            };
        }


        /// <summary>
        ///  Creates an role -> users index suitable for a crud operation
        /// </summary>
        /// <param name="userPartitionKey">Formatted UserId from the KeyHelper or IdentityUser.Id.ToString()</param>
        /// <param name="plainRoleName">Plain role name</param>
        /// <returns></returns>
        protected Model.IdentityUserIndex CreateRoleIndex(string userPartitionKey, string plainRoleName)
        {
            return new Model.IdentityUserIndex()
            {
                Id = userPartitionKey,
                PartitionKey = _keyHelper.GenerateRowKeyIdentityUserRole(plainRoleName),
                RowKey = userPartitionKey,
                KeyVersion = _keyHelper.KeyVersion,
                ETag =  TableConstants.ETagWildcard
            };
        }

        /// <summary>
        /// Creates an email index suitable for a crud operation
        /// </summary>
        /// <param name="userid">Formatted UserId from the KeyHelper or IdentityUser.Id.ToString()</param>
        /// <param name="email">Plain email address.</param>
        /// <returns></returns>
        protected Model.IdentityUserIndex CreateEmailIndex(string userid, string email)
        {
            return new Model.IdentityUserIndex()
            {
                Id = userid,
                PartitionKey = _keyHelper.GenerateRowKeyUserEmail(email),
                RowKey = userid,
                KeyVersion = _keyHelper.KeyVersion,
                ETag =  TableConstants.ETagWildcard
            };
        }

        /// <summary>
        /// Create an index for getting the user id based on his user name,
        /// </summary>
        /// <param name="userPartitionKey"></param>
        /// <param name="userName"></param>
        /// <returns></returns>
        protected Model.IdentityUserIndex CreateUserNameIndex(string userPartitionKey, string userName)
        {
            return new Model.IdentityUserIndex()
            {
                Id = userPartitionKey,
                PartitionKey = _keyHelper.GenerateRowKeyUserName(userName),
                RowKey = userPartitionKey,
                KeyVersion = _keyHelper.KeyVersion,
                ETag =  TableConstants.ETagWildcard
            };
        }

        protected Model.IdentityUserIndex CreateLoginIndex(string userPartitionKey, string loginProvider, string providerKey)
        {
            return new Model.IdentityUserIndex()
            {
                Id = userPartitionKey,
                PartitionKey = _keyHelper.GeneratePartitionKeyIndexByLogin(loginProvider, providerKey),
                RowKey = _keyHelper.GenerateRowKeyIdentityUserLogin(loginProvider, providerKey),
                KeyVersion = _keyHelper.KeyVersion,
                ETag =  TableConstants.ETagWildcard
            };

        }

        public override async Task<IList<TUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            if (claim == null)
            {
                throw new ArgumentNullException(nameof(claim));
            }

            string getTableQueryFilterByUserId(string userId)
            {
                string rowFilter = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, userId),
                    TableOperators.Or,
                    TableQuery.GenerateFilterCondition(nameof(TableEntity.RowKey), QueryComparisons.Equal, _keyHelper.GenerateRowKeyIdentityUserClaim(claim.Type, claim.Value)));

                string tqFilter = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(TableEntity.PartitionKey), QueryComparisons.Equal, userId), TableOperators.And,
                    rowFilter);
                return tqFilter;
            }

            return (await this.GetUsersAggregateByIndexQueryAsync(GetUserByClaimQuery(claim), (userId) => {
                return GetUserAggregateQueryAsync(userId, setFilterByUserId: getTableQueryFilterByUserId, whereClaim: (uc) =>
                {
                    return uc.RowKey == _keyHelper.GenerateRowKeyIdentityUserClaim(claim.Type, claim.Value);
                });

            }).ConfigureAwait(false)).ToList();
        }

        /// <summary>
        /// Base returns null as default, override returns string.empty as default
        /// </summary>
        /// <param name="user"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task<string> GetUserIdAsync(TUser user, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (user == null) throw new ArgumentNullException(nameof(user));

            return Task.FromResult(user.Id != null ? user.Id.ToString() : string.Empty);
        }

        protected override async Task<TUserToken> FindTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _userTable.GetEntityAsync<TUserToken>(_keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id)),
                    _keyHelper.GenerateRowKeyIdentityUserToken(loginProvider, name)).ConfigureAwait(false);
                return result?.Value;
            }
            catch (RequestFailedException rfe)
            when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                return default(TUserToken);
            }
        }

        protected override TUserToken CreateUserToken(TUser user, string loginProvider, string name, string value)
        {
            TUserToken item = base.CreateUserToken(user, loginProvider, name, value);
            ((Model.IGenerateKeys)item).GenerateKeys(_keyHelper);
            item.PartitionKey = _keyHelper.GenerateRowKeyUserId(ConvertIdToString(user.Id));
            item.UserId = user.Id;
            item.ETag =  TableConstants.ETagWildcard;
            return item;
        }

        protected override Task AddUserTokenAsync(TUserToken token)
        {
            return _userTable.UpsertEntityAsync(token, TableUpdateMode.Replace);
        }


        protected override Task RemoveUserTokenAsync(TUserToken token)
        {
            return _userTable.DeleteEntityAsync(token.PartitionKey, token.RowKey,  TableConstants.ETagWildcard);
        }



    }

}
